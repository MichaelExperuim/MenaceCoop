using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
#if MELON
using Il2CppMenace.PlayerSettings;
using Il2CppMenace.States;
using Il2CppMenace.Strategy;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;
using Il2CppMenace.Tools;
using Il2CppMenace.UI;
using Il2CppMenace.UI.Menu;
using Il2CppMenace.UI.Strategy;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
using Tactical = Il2CppTactical;   // the ROOT Tactical namespace (MovementSystem), not Menace.Tactical
#else
using Menace.PlayerSettings;
using Menace.States;
using Menace.Strategy;
using Menace.Tactical;
using Menace.Tactical.Skills;
using Menace.Tools;
using Menace.UI;
using Menace.UI.Menu;
using Menace.UI.Strategy;
#endif

namespace MenaceCoop;

// Mirror-input lockstep. Locally-initiated player orders are broadcast;
// received orders are re-invoked here. AI turns are not broadcast -- both sims
// run the AI from an identical RNG state (host reseeds at every faction
// switch, and every player order carries its own fresh seed so incidental RNG
// consumption like aim previews can never desync the stream).
internal static class Replication
{
    // True while we are executing an order that arrived over the network,
    // so the broadcast prefixes below don't echo it back.
    internal static bool ApplyingRemote;

    // True only inside TacticalState.EndTurn / UITactical.EndTurnAll -- the
    // player's explicit end-turn commands. Engine-driven SetTurnDone calls
    // (round-start resets, AP exhaustion) are deterministic on both sides and
    // must NOT be broadcast.
    internal static bool InExplicitEndTurn;

    // True while the guest applies a host squad-size change, so the guest's
    // squad-edit block exempts it and the change isn't echoed back.
    internal static bool ApplyingRemoteSquaddie;

    // True while REPLAYING a remote skill for its visuals/AP only: the skill's
    // damage roll differs per machine, so we must NOT let the replay change HP --
    // the authoritative HP comes solely from the "ehp"/"edie" messages. Without
    // this the receiver double-counts (replay damage + authoritative delta) and
    // enemy HP drifts low relative to the shooter's machine.
    // DAMAGE CAN LAND AFTER THE REPLAY CALL HAS ALREADY RETURNED.
    //
    // This was a plain bool set for the duration of skill.Use(). But a projectile
    // or area weapon resolves its damage ASYNCHRONOUSLY -- the acid spitter lands
    // several hits after Use() returns, by which time the finally block had
    // already cleared the flag. Those hits escaped suppression, so the guest
    // rolled its OWN splash targets for a shot the host had already resolved:
    //
    //   [SYNC] Applied remote skill active.fire_acid_spitter at (12,19)
    //   [LOCALDMG:G] darby sq7  -8 -> 0 (suppressed=False)
    //   [LOCALDMG:G] darby sq8  -8 -> 0 (suppressed=False)
    //
    // Host and guest then killed DIFFERENT squad members of the same unit
    // (host sq5/sq22/sq0 vs guest sq8/sq23/sq6/sq24/sq21, zero overlap), which is
    // the roster divergence in both directions on both player and enemy units.
    //
    // So suppression now has a TAIL: it stays on briefly after a replayed action
    // so late-resolving damage is still caught. Authoritative HP still arrives via
    // ehp/edie, which do not run under this flag.
    private static bool _suppressElementDamage;
    private static float _suppressDamageUntil;

    // The tail is SCOPED TO THE REPLAYED SHOT'S TARGET AREA, not global.
    //
    // A global tail made units immortal: on the host, replaying a guest MOVE armed
    // the tail, then the host's own AI attack landed inside that window and was
    // suppressed, so player units stopped taking damage entirely. Scoping it to the
    // tile the replayed shot was aimed at keeps late splash caught while leaving
    // every unrelated hit alone.
    private static int _tailX = int.MinValue, _tailY = int.MinValue;
    private const int TailRadius = 3;          // covers splash around the impact

    internal static bool SuppressElementDamage
    {
        get => _suppressElementDamage;
        set
        {
            _suppressElementDamage = value;
            if (!value) _suppressDamageUntil = UnityEngine.Time.realtimeSinceStartup + ReplayDamageTail;
        }
    }

    // Remember where a replayed shot was aimed, so its late damage can be caught.
    internal static void ArmDamageTailAt(int x, int y) { _tailX = x; _tailY = y; }

    // True when this specific element's damage should be swallowed: either we are
    // inside a replay, or it is late damage from one, landing near its impact point.
    // WHO FIRED DECIDES WHO OWNS THE HIT -- a cause, not a clock.
    //
    // Every shot is simulated on BOTH machines: the shooter's, and the partner's
    // replay of it. Only the replay was meant to be swallowed, but the suppression
    // flag spans Skill.Use() alone and projectile damage lands after it returns,
    // so the replay's damage went through unsuppressed, was broadcast, and the
    // shooter applied that echo on top of its own. Measured on one heavy truck:
    // three 6-point rifle hits from the local-forces garrison took it 140 -> 112 on
    // BOTH machines, where 122 was correct -- 38 host hits relayed to the guest and
    // 34 guest hits relayed back, none of them suppressed. Two timing windows were
    // tried against this and both regressed (see below): a window cannot tell a
    // late replayed hit from this machine's own next hit on the same target.
    //
    // The attacker can. Every element damage write happens inside
    // Actor.OnDamageReceived (138 of 138 in the probe logs), which names the
    // attacker. A player unit's hit belongs to the machine that owns that unit;
    // everything else -- AI of any faction, structures, hazards, damage over time
    // -- belongs to the host, which runs the AI. The other machine swallows its
    // own copy and takes the owner's "ehp" instead.
    private static bool _hitIsPartners;
    private static int _hitFrame = -1;

    // SNAPSHOTS RACE IN-FLIGHT DAMAGE (r87/r88). BoardSettled only knows about
    // movement; a projectile's damage lands after Skill.Use returns, so a soft
    // snapshot taken (or applied) while hits are still resolving carries a
    // pre-hit HP -- the guest was corrected 1 -> 10 and 10 ms later 10 -> 1 by
    // the next one. Soft snapshots wait until no hit has landed for a moment.
    internal static float LastHitAt = -100f;
    internal static bool HitsQuiet(float seconds = 1.5f)
        => UnityEngine.Time.realtimeSinceStartup - LastHitAt > seconds;

    internal static long BeginHit(Entity attacker)
    {
        LastHitAt = UnityEngine.Time.realtimeSinceStartup;
        long prev = ((long)_hitFrame << 1) | (_hitIsPartners ? 1L : 0L);
        _hitIsPartners = IsPartnersHit(attacker);
        _hitFrame = UnityEngine.Time.frameCount;
        _hitDepth++;
        return prev;
    }

    // The hit in progress, for stamping ehp/edie (BUGS.md #28). Frame-scoped like
    // the rest of the hit state.
    private static Entity _hitAttacker;
    private static Skill _hitSkill;
    private static int _hitVis = -1;

    internal static void NoteHitDetail(Entity attacker, Skill skill, DamageInfo info)
    {
        _hitAttacker = attacker;
        _hitSkill = skill;
        try { _hitVis = info != null ? (int)info.DamageVisualizationType : -1; } catch { _hitVis = -1; }
    }

    // Stamp the current hit's attacker, skill and visual onto a damage/death packet.
    internal static void StampHit(CoopMsg msg)
    {
        if (_hitDepth <= 0 || _hitFrame != UnityEngine.Time.frameCount) return;
        try
        {
            var a = _hitAttacker?.TryCast<Actor>();
            if (a != null)
            {
                msg.ARId = DesyncDetector.RosterIndexOf(a);
                msg.ACId = DesyncDetector.CoopIdOf(a);
            }
            msg.SkillId = _hitSkill?.GetID();
            msg.Vis = _hitVis;
        }
        catch { }
    }

    // KILL A RELAYED ELEMENT THE WAY THE ENGINE KILLS ONE (BUGS.md #28).
    //
    // Probed run 33: a local kill runs Element.OnDeath(attacker, DamageInfo, dmg,
    // skill) then CleanUpOnDeath, and never Element.Die(). The relay called Die(),
    // which runs the clean-up without the death -- no animation, no body, so the
    // partner's kills left nothing behind. OnDeath with the real attacker and skill
    // is the engine's own road; Die() stays as the fallback if it refuses.
    internal static void KillElementLikeEngine(Element el, CoopMsg m, int applied)
    {
        if (el == null) return;
        Actor attacker = null;
        Skill skill = null;
        try
        {
            if (m != null && m.ARId >= 0) attacker = DesyncDetector.FindByRosterIndex(m.ARId);
            else if (m != null && m.ACId >= 0) attacker = DesyncDetector.FindByCoopId(TacticalManager.Get(), m.ACId);
            if (attacker != null && !string.IsNullOrEmpty(m?.SkillId))
                skill = attacker.GetSkills()?.GetSkillByID(m.SkillId)?.TryCast<Skill>();
        }
        catch { }
        KillCredit.NoteRelayedKill(el, attacker);
        bool prev = ApplyingRemote;
        ApplyingRemote = true;
        try
        {
            var info = new DamageInfo();
            info.Damage = applied;
            if (m != null && m.Vis >= 0) info.DamageVisualizationType = (DamageVisualizationType)m.Vis;
            el.OnDeath(attacker, info, applied, skill);
            return;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[SYNC] Element.OnDeath refused a relayed death ({e.GetType().Name}) -- using Die()");
        }
        finally { ApplyingRemote = prev; }
        ApplyingRemote = true;
        try { el.Die(); }
        finally { ApplyingRemote = prev; }
    }

    internal static void EndHit(long prev)
    {
        _hitIsPartners = (prev & 1L) != 0;
        _hitFrame = (int)(prev >> 1);
        if (_hitDepth > 0) _hitDepth--;
    }
    private static int _hitDepth;

    // Inside a hit THIS machine decides (its own unit fired, or the host's AI):
    // what the hit does to the victim's morale and suppression is ours to relay.
    internal static bool InOwnHit => _hitDepth > 0 && !_hitIsPartners && !ApplyingRemote
                                      && _hitFrame == UnityEngine.Time.frameCount;

    // A HIT THE HOST DECIDES CAN HAPPEN INSIDE A REPLAY OF THE GUEST'S ORDER.
    //
    // r71 09:55:52: the guest's carrier ended its turn; on the host that turn end
    // ran inside the replay of the guest's "skip", and its turn-end damage (no
    // attacker: DoT/hazard -- the host's to decide) was applied with
    // ApplyingRemote set. The ehp/edie broadcasts skip everything under
    // ApplyingRemote, so it was never sent; the guest swallowed its own copy
    // waiting for it, and sat 11 HP high until the next snapshot. Same shape as a
    // host unit's reaction fire provoked by a replayed guest move. On the host a
    // hit is the partner's only when the partner's own unit fired it, so this
    // cannot re-send a relayed shot.
    internal static bool HostDecidesHitInReplay => CoopNet.Mode == CoopMode.Host && ApplyingRemote
                                      && _hitDepth > 0 && !_hitIsPartners
                                      && _hitFrame == UnityEngine.Time.frameCount;

    // Only trusted within the frame it was set, so a throw inside the engine's
    // damage code (which skips the postfix) cannot leave a stale verdict behind.
    internal static bool InPartnersHit => _hitIsPartners && _hitFrame == UnityEngine.Time.frameCount;

    private static bool IsPartnersHit(Entity attacker)
    {
        if (!CoopNet.Connected || !Ownership.Active) return false;
        Actor shooter = null;
        try { shooter = attacker?.TryCast<Actor>(); } catch { }
        if (shooter != null && Ownership.IsKnownPlayerUnit(shooter))
            return !Ownership.IsLocal(shooter);
        return CoopNet.Mode != CoopMode.Host;
    }

    internal static bool ShouldSwallowDamage(Actor victim)
    {
        if (_suppressElementDamage) return true;
        if (StructureSync.SwallowCollapseHit(victim)) return true;

        // Structures are not relayed at all (no "ehp" path for non-actors), so
        // both machines must keep simulating those themselves.
        if (!ApplyingRemote && victim != null && InPartnersHit) return true;

        // ON THE GUEST, DAMAGE TO A PLAYER UNIT IS ALWAYS THE HOST'S TO DECIDE.
        //
        // Everything that shoots a player unit is enemy AI, and AI runs only on the
        // host. So any damage the GUEST originates against a player unit is a
        // second, independent roll for a hit the host has already resolved -- which
        // is exactly how the two machines ended up killing different squad members.
        //
        // The scoped replay tail catches the splash that lands just after a relayed
        // shot, but not damage-over-time ticks (acid), which arrive rounds later.
        // This invariant covers both, and the authoritative HP still comes from the
        // host via ehp/edie, which do not run under this check.
        // ...but NEVER swallow the host's own authoritative application. The 'ehp'
        // handler kills an element when its HP reaches zero, and that Die() runs
        // under ApplyingRemote. Swallowing it stopped player squaddies dying on the
        // guest at all (guest kept 9 alive where the host had 5).
        if (!ApplyingRemote
            && CoopNet.Mode == CoopMode.Guest && victim != null
            && Ownership.Active && Ownership.IsKnownPlayerUnit(victim))
            return true;

        // THE TIME-BASED TAIL IS GONE. Two attempts, two regressions.
        //
        // Global tail  -> units became immortal: replaying a guest MOVE armed it,
        //                 then the host's own AI attack landed inside the window.
        // Scoped tail  -> enemies survived on the host: the window covered exactly
        //                 the tile the fighting was happening on, so the host's own
        //                 damage to that enemy was swallowed too.
        //
        // A timing window cannot tell "late splash from a replayed shot" apart from
        // "our own next attack on the same target", because they look identical and
        // happen in the same place. The ownership invariant above needs no timing
        // and cannot misfire that way. Late splash on the guest is instead corrected
        // by the host's authoritative ehp/edie, which is what they exist for.
        return false;
    }

    // TRADEOFF, deliberately sized small.
    //
    // The tail suppresses damage GLOBALLY, so if this machine's OWN attack happened
    // to resolve inside the window its damage would be wrongly suppressed -- which
    // would trade this divergence for its mirror image (our own hits going
    // unapplied). The action lock serialises actions and the harness paces ~1-2s
    // apart, so the window must stay well under that.
    //
    // 1.2s covers a projectile flight and the splash that follows it, and is short
    // enough that it should not reach the next action. If the logs later show our
    // own damage being suppressed, this is the number to lower -- not a reason to
    // abandon the tail.
    private const float ReplayDamageTail = 1.2f;

    // Action lock: only ONE player may have an action in flight at a time during
    // the shared player turn. Whoever starts a move/skill grabs the lock; the
    // partner is blocked from STARTING a new action until it (and its animation)
    // finish. This serialises actions so they can't interleave -- the root of
    // most co-op desyncs -- while still allowing fast alternation.
    internal static bool LockHeldByMe;
    // The OTHER seats currently holding the action lock (v5: was one bool).
    private static readonly System.Collections.Generic.HashSet<int> _lockHolders = new();
    internal static bool LockHeldByPartner => _lockHolders.Count > 0;
    // A HOLDER THAT LEAVES NEVER UNLOCKS (r105: M4 was killed 0.6 s into its
    // move; everyone else sat on "your partner is taking their action" until the
    // turn ended). Its lock goes the moment it leaves the session -- the event
    // itself, not a timer (user, 2026-10-03: no time-based fixes).
    internal static void ReleaseLockOf(int seat, string why)
    {
        if (_lockHolders.Remove(seat))
            Plugin.Logger.LogWarning($"[SYNC] Released seat {seat}'s action lock: {why}");
    }

    internal static void ReleaseLocksNotIn(System.Collections.Generic.ICollection<int> seats)
    {
        foreach (var s in new System.Collections.Generic.List<int>(_lockHolders))
            if (!seats.Contains(s)) ReleaseLockOf(s, "left the session");
    }
    private static int SenderSeat(CoopMsg m) => m.From >= 0 ? m.From : Peers.LinkSeat;
    private static float _lockAcquiredAt;
    private const float LockMinHold = 0.6f;   // hold at least this long (let the action start)
    private const float LockFailsafe = 15f;   // never hold forever if a move never settles

    internal static bool ShouldBroadcast()
    {
        if (ApplyingRemote || !CoopNet.Connected) return false;
        var tm = TacticalManager.Get();
        return tm != null && tm.IsPlayerTurn();
    }

    // Host-authoritative AI: during a non-player (AI/allied/neutral) faction
    // turn the guest does NOT run its own AI (it is suppressed -- see
    // GuestSuppressAI), so ONLY the host may broadcast orders then. During the
    // players' own turn both sides broadcast their own units' orders as before.
    private static float _lastMuteLogAt;

    // A silently-refused broadcast is indistinguishable from a lost packet and
    // presents as the partner waiting forever on a unit that is already done.
    // Name the condition that muted us instead of leaving it to be guessed.
    private static bool Muted(string why)
    {
        if (UnityEngine.Time.realtimeSinceStartup - _lastMuteLogAt > 2f)
        {
            _lastMuteLogAt = UnityEngine.Time.realtimeSinceStartup;
            Plugin.Logger.LogWarning($"[NET] Order NOT broadcast -- {why}");
        }
        return false;
    }

    // NEVER RELAY AN ORDER FOR A UNIT THIS SIDE DOES NOT OWN.
    //
    // Authority follows the ACTOR. Every place the mod issues orders already
    // filters on ownership, but the ENGINE also ends turns by itself, and our
    // hooks relayed those too -- so the host broadcast a 'skip' for a guest-owned
    // unit ("TAKEOVER: this side issued 'skip' for a unit it does NOT own
    // ('player_squad.darby')"), ending that player's turn for them.
    //
    // A local engine auto-skip is a local event, not a command: the owner will
    // send the authoritative turn-end for their own unit. Relaying ours steals
    // the activation and the two sides then disagree about who still owes a turn.
    internal static bool MayBroadcastFor(Actor actor)
    {
        try
        {
            if (actor == null) return true;
            if (!Ownership.Active) return true;                 // not divided up yet
            if (!Ownership.IsKnownPlayerUnit(actor)) return true; // AI: host's to relay
            return Ownership.IsLocal(actor);
        }
        catch { return true; }
    }

    internal static bool ShouldBroadcastOrder()
    {
        if (ApplyingRemote || !CoopNet.Connected) return false;
        if (SyncPauseActive) return Muted("sync pause active");
        if (SessionSync.HostHoldFire) return Muted("host hold-fire (waiting for guest to load)");
        if (LockHeldByPartner) return Muted("partner holds the action lock");
        // MUST mirror ActionBlockedByLock exactly: the input hold and this gate
        // are separate Harmony prefixes on the same call, so if the hold skips
        // the action locally but we broadcast it anyway, the order executes on
        // the PARTNER's machine only -- a ghost move that IS a position desync
        // (seen live in 1.4.15: guest blocked during pending relayed orders,
        // but its clicks still went out and moved units on the host).
        if (CoopNet.Mode == CoopMode.Guest && _deferred.Count > 0 && !DeferredIsStale())
            return Muted($"guest has {_deferred.Count} unresolved incoming order(s)");
        // Must mirror ActionBlockedByLock EXACTLY -- a gate that blocks locally
        // while the broadcast still fires produces ghost orders on the partner
        // (the 1.4.16 bug). Replays pass: ApplyingRemote is checked above.
        if (TurnControl.Active && !TurnControl.IsMyTurn) return Muted("turn control is with the partner");
        // Mirrors the enemy-activation hold in ActionBlockedByLock.
        if (CoopNet.Mode == CoopMode.Guest && DesyncDetector.HostInAiPhase) return Muted("the host is running an enemy activation");
        if (CoopNet.Mode == CoopMode.Guest && AiPacing.Count > 0) return Muted("host AI moves still playing back here");
        var tm = TacticalManager.Get();
        if (tm == null) return Muted("no tactical manager");
        if (tm.IsPlayerTurn()) return true;          // player turn: both sides
        if (CoopNet.Mode == CoopMode.Host) return true; // AI turn: host is authority
        // Mirrors the ActionBlockedByLock exception exactly (law 7): if the host
        // is in the players' phase, the guest may act even though its own cursor
        // has already moved on to an AI faction.
        if (DesyncDetector.HostIsInPlayerPhase) return true;
        return Muted("AI turn and we are the guest");
    }

    // HOST: tell the guest which phase we are in -- on every change, and every
    // 2 s as a heartbeat so a lost message or a long AI activation (up to 30 s
    // seen) cannot leave the guest reading a stale phase. See
    // DesyncDetector.NoteHostPhase for why the 5 s snapshot is not enough.
    private static int _hostPhaseSent = -1;
    private static float _hostPhaseSentAt;

    internal static void SendHostPhaseIfChanged(TacticalManager tm, bool heartbeat = false)
    {
        if (CoopNet.Mode != CoopMode.Host || !CoopNet.Connected || tm == null) return;
        int phase = tm.IsPlayerTurn() ? 1 : 0;
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (phase == _hostPhaseSent && !(heartbeat && now - _hostPhaseSentAt >= 2f)) return;
        _hostPhaseSent = phase;
        _hostPhaseSentAt = now;
        CoopNet.Send(new CoopMsg { T = "hphase", Prm = phase });
    }

    internal static void HostPhaseHeartbeat()
    {
        var tm = TacticalManager.Get();
        if (tm == null) { _hostPhaseSent = -1; return; }
        try { SendHostPhaseIfChanged(tm, heartbeat: true); } catch { }
    }

    // Grab the action lock when this side starts a player action. No-op outside
    // the shared player turn (the AI turn is host-only, no contention).
    internal static void AcquireActionLock()
    {
        var tm = TacticalManager.Get();
        if (tm == null || !tm.IsPlayerTurn()) return;
        _lockAcquiredAt = UnityEngine.Time.realtimeSinceStartup;
        if (LockHeldByMe) return;                      // already ours -- just extended above
        LockHeldByMe = true;
        CoopNet.Send(new CoopMsg { T = "alock" });
    }

    // Release our lock once the action has settled (nothing animating), and clean
    // up when the turn/battle ends. Called each frame from CoopRunner.Update.
    internal static void UpdateActionLock()
    {
        var tm = TacticalManager.Get();
        if (tm == null || !CoopNet.Connected || !tm.IsPlayerTurn())
        {
            // out of the shared player turn -- clear any stale lock state
            if (LockHeldByMe) { LockHeldByMe = false; CoopNet.Send(new CoopMsg { T = "aunlock" }); }
            _lockHolders.Clear();
            return;
        }
        if (!LockHeldByMe) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        bool stillActing = (now - _lockAcquiredAt < LockMinHold) || AnyActorMoving();
        if (!stillActing || now - _lockAcquiredAt > LockFailsafe)
        {
            LockHeldByMe = false;
            CoopNet.Send(new CoopMsg { T = "aunlock" });
        }
    }

    // Whether the local player is currently blocked from starting an action.
    internal static bool ActionBlockedByLock()
    {
        if (ApplyingRemote) return false;               // replays are never blocked
        var tm = TacticalManager.Get();
        if (tm == null || !CoopNet.Connected) return false;

        if (SyncPauseActive)
        {
            CoopNet.Status = GuestReload.RejoiningName != null
                ? $"GAME PAUSED -- {GuestReload.RejoiningName} is rejoining"
                : "GAME PAUSED -- resolving a desync";
            return true;
        }

        // HOLD-FIRE MUST BLOCK LOCALLY TOO. ShouldBroadcastOrder refuses to send
        // while the guest is still loading in, but this gate did not refuse the
        // ACTION -- so the host could finish units whose turn-ends were then
        // dropped, and the guest waited on them for the rest of the battle
        // ("Order NOT broadcast -- host hold-fire" immediately followed by a
        // local SetTurnDone). That is law 7 in the other direction: an action we
        // cannot tell our partner about must not happen at all.
        if (SessionSync.HostHoldFire)
        {
            CoopNet.Status = "Hold on -- waiting for your partner to load into the battle";
            return true;
        }

        // SEQUENTIAL PHASES: outside your own phase you may look but not act.
        //
        // There used to be an exception here: SelfTest.LockoutOverride let a side
        // that had been locked out "too long" act anyway. It was set by a TIMER in
        // the old brain, and that brain is gone (2026-09-25). Deleting it is a
        // no-op -- the compiler confirmed the flag was never assigned, so this
        // branch had been unreachable -- and it is the right no-op: CLAUDE.md
        // records from player reports that a timed handover fires on people who
        // are simply thinking. Lockout is answered from board state instead: the
        // moment a player has nothing left to move, their partner gets control.
        if (TurnControl.Active && !TurnControl.IsMyTurn)
        {
            CoopNet.Status = TurnControl.StatusText;
            return true;
        }

        // GUEST ONLY: because the guest's AI processing is suppressed, the game
        // leaves its input live during AI turns -- but an order issued then
        // races the host's relayed AI orders for tiles (seen live: guest moved
        // onto (9,22) an instant before the relayed enemy move into (9,22)
        // arrived; each machine resolved the contest differently and the boards
        // forked). Hold the guest's input until the player faction is actually
        // active and every relayed order has fully applied. The host needs no
        // such guard -- its AI really runs, so the game blocks its input itself.
        // NOTE: _pendingAdvances deliberately does NOT block. The engine uses
        // alternating activations (player -> AI -> player within one round), and
        // both engines advance past AI factions locally at round start -- the
        // host's relayed advance can arrive as a harmless ECHO while the guest
        // is already back in the player phase. Blocking on it deadlocked the
        // whole round (guest frozen, host waiting for the guest's squads).
        // ONE EXCEPTION: the host says it is the players' phase.
        //
        // The engines alternate activations and can end up one step apart -- the
        // guest's cursor on an AI faction while the host is already back on the
        // players, waiting for a guest unit. The guest then refuses to move that
        // unit *because* of its own cursor, and neither side can proceed. Seen
        // live as host fac=1/theirs=1 against guest fac=7/mine=1 with nothing
        // moving and no pause: a pure mutual wait.
        //
        // Acting is safe here precisely because the host is NOT running its AI,
        // so there are no relayed AI orders to race. Deferred orders still hold.
        // The deferred-order hold must EXPIRE.
        //
        // It exists so we do not act on a board that is still catching up. But a
        // guest that cannot act falls behind, falling behind grows the backlog,
        // and the backlog blocks it further -- a guest one action into a battle
        // while the host was three rounds ahead. Orders that have sat unresolved
        // for seconds are not "still playing out", they are stuck, and holding
        // the player hostage to them is worse than a little drift the healer can
        // repair.
        bool backlogFresh = _deferred.Count > 0 && !DeferredIsStale();

        // THE HOST'S AI IS ACTING: HOLD, WHATEVER OUR OWN CURSOR SAYS.
        //
        // The engine alternates activations, and the guest's cursor does not
        // follow the host's AI ones -- the guest's AI is suppressed, so its
        // cursor usually stays on the player faction. The test below therefore
        // missed exactly the case that matters, and TurnControl.Active going
        // false in that state LIFTED turn control instead of holding. The guest
        // could shoot at an enemy the host's AI was still walking (player report
        // 2026-09-30). The host's own phase is the answer; the host's AI never
        // waits on the guest, so holding here cannot deadlock.
        if (CoopNet.Mode == CoopMode.Guest && DesyncDetector.HostInAiPhase)
        {
            CoopNet.Status = "Hold on -- the enemy is acting";
            return true;
        }
        // Same for host AI moves still queued for playback here (AiPacing): the
        // board is a few seconds behind the host until they have played out.
        if (CoopNet.Mode == CoopMode.Guest && AiPacing.Count > 0)
        {
            CoopNet.Status = "Hold on -- the enemy is still moving";
            return true;
        }

        if (CoopNet.Mode == CoopMode.Guest
            && ((!tm.IsPlayerTurn() && !DesyncDetector.HostIsInPlayerPhase) || backlogFresh))
        {
            CoopNet.Status = "Hold on -- the other side's actions are still playing out";
            return true;
        }

        if (!LockHeldByPartner || !tm.IsPlayerTurn()) return false;
        CoopNet.Status = "Hold on -- your partner is taking their action";
        return true;
    }

    // Guest-side queue of "advance past a non-player faction" commands from the
    // host. Applied only while the guest is actually sitting on a non-player
    // faction, which guards against the (rare) case where the command arrives
    // before the guest's own local player->AI transition.
    private static int _pendingAdvances;

    internal static int PendingAdvanceCount => _pendingAdvances;
    internal static void ResetPendingAdvances() => _pendingAdvances = 0;

    // NOTE (1.4.24): the 1.4.21 "host-driven faction cycle" (guest holds ALL
    // local NextFaction, host relays every change) caused three hard freezes:
    // blocking the engine's own advance bookkeeping leaves its activation state
    // hollow, and a later forced NextFaction hangs in a native scan loop. The
    // engine cannot be fought on this -- the guest's engine must cycle locally,
    // and the host relays only AI-faction exits (echoes of transitions the
    // guest already made are dropped). Round drift is the accepted cost until a
    // safe sync point inside the engine is found.
    // ---- Round rollover via the engine's own pipeline (1.4.28) ------------
    // Every guest freeze was OUR cold NextFaction call performing the
    // END-OF-ROUND rollover -- the engine's rollover normally runs inside its
    // own AI processing pipeline (AIFaction.Process drives it on the host),
    // and a cold call from outside that context hangs in native code. So when
    // the host's advance reveals its round is ahead of ours, we do NOT call
    // NextFaction: we open a pump window instead -- the engine's AI pump is
    // unsuppressed (actual AI behavior stays dead, see AiPatches) so IT rolls
    // the round natively, and the window closes the instant our round catches
    // up. Queued advances hold during the window and resume after.
    // THE FACTION PUMP (1.5.2) -- the guest's ONLY advance mechanism.
    // Hard-won law of this engine: any cold NextFaction call from mod code can
    // hang in native code, because special transitions (round rollover, mission
    // end, ...) run inside it and need the engine's own processing context.
    // Three freezes were fixed by routing three cases to the engine; this
    // removes the whole class: mod code NEVER calls NextFaction. Instead, the
    // engine's AI pipeline is unsuppressed exactly while the guest sits on a
    // non-player faction whose units are ALL done (per the host's aidone
    // relays) -- the engine then advances itself through every kind of
    // transition natively, at the host's cadence, and the pump closes the
    // moment a faction with work (or the player phase) is reached.
    private static bool _pumpActive;
    private static float _stallSince = -1f;
    internal static bool RolloverPumpActive => _pumpActive;   // name kept for the AI gates

    private const float StallRecheck = 10f;

    // Does EITHER player still have a unit with a turn to take this round?
    // Player units are identified by ownership, so this counts both sides.
    private static bool AnyPlayerUnitLeftToAct(TacticalManager tm)
    {
        try
        {
            var factions = tm.GetFactions();
            if (factions == null) return false;
            for (int f = 0; f < factions.Length; f++)
            {
                var actors = factions[f]?.GetActors();
                if (actors == null) continue;
                for (int i = 0; i < actors.Count; i++)
                {
                    var a = actors[i];
                    if (a == null || a.IsDying() || a.IsTurnDone()) continue;
                    if (Ownership.Active && Ownership.IsKnownPlayerUnit(a)) return true;
                    if (!Ownership.Active && DesyncDetector.RosterIndexOf(a) >= 0) return true;
                }
            }
        }
        catch { }
        return false;
    }

    // Name exactly which units are holding the cycle, so a stall is never again
    // just "stalled" -- unit, faction and whether we can identify it at all.
    private static void LogPendingUnits(TacticalManager tm)
    {
        try
        {
            // Stranded on a faction the host already finished? Settle it -- this
            // is the exact spot the manual-test freeze sat in.
            if (TakeDeferredAdvance(tm)) return;

            var actors = tm.GetActiveFaction()?.GetActors();
            if (actors == null) return;
            var sb = new System.Text.StringBuilder();
            int n = 0;
            for (int i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || a.IsDying() || a.IsTurnDone()) continue;
                n++;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append($"{DesyncDetector.TemplateNameOf(a)}(id {DesyncDetector.CoopIdOf(a)})");
            }
            // RATE-LIMITED: this fires every tick while stranded, and a single
            // battle produced 5,063 copies of it -- which buried every other
            // warning in the log and made a full sweep useless. Say it once when
            // the situation starts, then only every 10s while it persists.
            string waitKey = $"{tm.GetRound()}:{tm.GetActiveFactionID()}:{n}";
            float nowW = UnityEngine.Time.realtimeSinceStartup;
            if (waitKey != _lastWaitKey || nowW - _lastWaitLogAt > 10f)
            {
                _lastWaitKey = waitKey;
                _lastWaitLogAt = nowW;
                Plugin.Logger.LogWarning($"[SYNC] Still on faction {tm.GetActiveFactionID()} waiting for {n} unit(s): {sb}");
            }
        }
        catch { }
    }

    // True when every faction EXCEPT the given one has finished -- meaning the
    // next faction advance would end the round rather than move to another
    // faction's activation.
    private static bool OtherFactionsDone(TacticalManager tm, int exceptFactionId)
    {
        try
        {
            var factions = tm.GetFactions();
            if (factions == null) return false;
            for (int f = 0; f < factions.Length; f++)
            {
                var fac = factions[f];
                if (fac == null || fac.GetIndex() == exceptFactionId) continue;
                // The engine's own "how many of my units still have to act"
                // -- more trustworthy than counting done-flags ourselves.
                if (fac.GetAmountOfActorsLeftToAct() > 0) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static float _phaseEndStableSince = -1f;
    private static float _lastPhaseNudgeAt;
    private static float _lastAheadLogAt;
    private static float _lastRunawayCheckAt;

    // A runaway round gap is the most damaging desync there is -- every later
    // order lands in the wrong round -- and it is trivially detectable. Anything
    // beyond a round or two apart means the guest has been rolling on its own,
    // so stop the local cycle dead and let the host's snapshots re-establish
    // the truth rather than drifting further.
    // CATCH THE GUEST UP WHEN IT FALLS BEHIND THE HOST'S ROUND.
    //
    // The guest's cycle only advances as the host's relayed AI done-flags
    // arrive, so any lag compounds: it drops a round behind, its player phase
    // therefore happens later, and it looks as though the guest "only starts
    // fighting after the host is done" -- when in truth it is still playing an
    // earlier round of the same battle.
    //
    // The host being on a LATER round is itself proof that every AI activation
    // in the rounds between is finished. Settling those units on the guest
    // adopts a fact the host has already established; it is not the invention of
    // AI turn-state that law 4 forbids. Strictly bounded: guest only, only while
    // genuinely behind, only on a non-player faction, and only after it has been
    // stuck for a few seconds.
    private static float _behindSince = -1f;
    private static float _lastCatchUpAt;

    // Does any PLAYER unit still genuinely have a turn coming? Ownership is not
    // required -- a partner's unfinished unit blocks the round just as ours does.
    // ONLY OUR OWN UNITS MAY BLOCK CATCHING UP.
    //
    // This counted every unit in the player faction, the partner's included --
    // and the catch-up it guards exists precisely to settle a PARTNER unit whose
    // turn-end was lost in transit. So in the one situation the settle code was
    // written for, this gate returned first and the settle code below was
    // unreachable. The two guards contradicted each other and the stricter one
    // won.
    //
    // What that cost, seen live: guest pinned on round 12 with theirs=1 while the
    // host reached round 19, the watchdog retrying 25 times over 21 minutes and
    // every hard resync restoring "0 position(s), 0 HP/AP, 0 combat-state" --
    // repairing nothing because nothing was broken. The board was correct. One
    // unit simply had nobody left who could finish it: the guest may not move the
    // partner's units, and the host had been seven rounds past it for a quarter
    // of an hour and was never going to send that turn-end again.
    //
    // A partner unit is not "genuinely owing a turn" once the host is provably on
    // a later round -- it is finished over there. Settling it is reading the
    // host's state, which the code below already does correctly.
    private static bool AnyPlayerUnitOwesATurn(TacticalManager tm)
    {
        try
        {
            var actors = tm.GetPlayerFaction()?.GetActors();
            for (int i = 0; actors != null && i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null) continue;
                if (a.IsDying() || a.IsTurnDone()) continue;
                // Without an ownership split every player unit is ours, so this
                // keeps its original meaning in that case.
                if (Ownership.Active && Ownership.IsKnownPlayerUnit(a) && !Ownership.IsLocal(a)) continue;
                return true;
            }
        }
        catch { return true; }   // cannot tell -- assume yes and stay put
        return false;
    }

    internal static void CatchUpToHostRound()
    {
        if (CoopNet.Mode != CoopMode.Guest || !CoopNet.Connected) return;
        var tm = TacticalManager.Get();
        if (tm == null || SyncPauseActive || PartnerMissionOver) { _behindSince = -1f; return; }

        try
        {
            int host = DesyncDetector.HostRound;
            int mine = tm.GetRound();
            if (host <= 0 || mine >= host) { _behindSince = -1f; return; }

            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_behindSince < 0) { _behindSince = now; return; }
            if (now - _behindSince < 4f) return;             // let relays arrive first
            if (now - _lastCatchUpAt < 2f) return;
            // "PLAYER TURN" IS NOT THE SAME AS "OUR UNITS STILL OWE TURNS".
            //
            // This used to bail out on IsPlayerTurn() alone, reading it as "we
            // still have people to move". But the cursor can sit on the player
            // phase with EVERY unit already finished -- seen live with the guest
            // stuck on round 1 reporting f1..f9 all left=0, roundDone=True,
            // finishedPct=1 while the host was on round 3. Nothing was left to
            // act, nothing could advance it, and the one mechanism meant to pull
            // us forward refused to run. Only a unit that genuinely still owes a
            // turn should block catching up.
            if (AnyPlayerUnitOwesATurn(tm)) return;
            if (AnyActorMoving()) return;

            var fac = tm.GetActiveFaction();
            var actors = fac?.GetActors();
            if (actors == null) return;

            int settled = 0;
            for (int i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null) continue;
                try
                {
                    if (a.IsDying() || a.IsTurnDone()) continue;

                    // A PARTNER'S UNIT MAY BE SETTLED WHEN THE HOST IS A WHOLE
                    // ROUND AHEAD -- BUT NEVER ONE OF OUR OWN.
                    //
                    // We only reach here when the host is demonstrably on a LATER
                    // round, which means every unit of this round is already
                    // finished over there. Adopting that is reading the host's
                    // state, not fabricating it (law 4 forbids the latter).
                    //
                    // Without this the guest waited forever on a partner turn-end
                    // that was lost in transit: seen live stuck at round 5 with
                    // theirs=1 while the host had reached round 8, the watchdog
                    // firing repeatedly. The previous attempt at this problem was a
                    // periodic re-assert, which caused far worse (v3.7.11) -- this
                    // is one-shot, only while genuinely behind, and never touches a
                    // unit whose turn is ours to spend.
                    if (Ownership.Active && Ownership.IsKnownPlayerUnit(a))
                    {
                        if (Ownership.IsLocal(a)) continue;          // ours: we decide
                        Plugin.Logger.LogWarning(
                            $"[SYNC] Settling partner unit '{DesyncDetector.TemplateNameOf(a)}' -- " +
                            $"the host is on round {host} and we are on {mine}, so it is finished over there");
                    }

                    ApplyingRemote = true;
                    try { a.SetTurnDone(true); a.MarkAsHasActed(); }
                    finally { ApplyingRemote = false; }
                    settled++;
                }
                catch { }
            }

            if (settled > 0)
            {
                _lastCatchUpAt = now;
                Plugin.Logger.LogWarning($"[SYNC] Catching up: host is on round {host}, we are on {mine} -- settled {settled} AI unit(s) the host has already finished");
            }
            else
            {
                // Nothing left to settle, yet we are still a round behind: the
                // board is finished and simply has not rolled over. Ask the
                // engine to close the phase rather than sitting here -- this is
                // the state that had the guest pinned on round 1 indefinitely.
                _lastCatchUpAt = now;
                Plugin.Logger.LogWarning($"[SYNC] Catching up: host is on round {host}, we are on {mine} with nothing left to act -- closing the phase");
                CheckPlayerPhaseEnd();
            }
        }
        catch { }
    }

    internal static void GuardRoundRunaway()
    {
        if (CoopNet.Mode != CoopMode.Guest || !CoopNet.Connected) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now - _lastRunawayCheckAt < 3f) return;
        _lastRunawayCheckAt = now;

        var tm = TacticalManager.Get();
        if (tm == null) return;
        try
        {
            int host = DesyncDetector.HostRound;
            if (host <= 0) return;
            int mine = tm.GetRound();
            if (mine - host < 3) return;                  // a round of lag is normal

            Plugin.Logger.LogError($"[SYNC] ROUND RUNAWAY: guest is on {mine}, host on {host} -- halting the local cycle and asking for authoritative state");
            CoopRunner.ShowDesyncWarning($"Round desync ({mine} vs host {host}) -- resyncing");
            DesyncDetector.RequestResync($"round runaway: guest {mine} vs host {host}");
        }
        catch { }
    }
    private static int _nudgedState = int.MinValue;
    private static int _nudgeTries;

    // END OF THE PLAYERS' PHASE.
    //
    // Both engines end up with the player faction active, ZERO player units left
    // to act and ten aliens waiting -- and sit there. Nothing is desynced: host
    // and guest report byte-identical counters. The transition simply never
    // fires, because a unit finished via SkipTurn never runs the end-of-phase
    // check that TacticalState.EndTurn/UITactical.EndTurnAll perform.
    //
    // TryCheckRound is the engine's OWN "is this phase over" call -- it is not
    // NextFaction (law 1), it decides for itself whether to advance, and it is
    // driven here by a condition both machines evaluate identically, so neither
    // can advance without the other.
    internal static void CheckPlayerPhaseEnd()
    {
        if (!CoopNet.Connected) return;
        var tm = TacticalManager.Get();
        if (tm == null) { _phaseEndStableSince = -1f; return; }
        try
        {
            if (!tm.IsPlayerTurn()) { _phaseEndStableSince = -1f; return; }
            if (TacticalManager.IsAnyActorMoving()) { _phaseEndStableSince = -1f; return; }
            if (BlockingDeferred > 0) { _phaseEndStableSince = -1f; return; }
            if (SyncPauseActive || SessionSync.HostHoldFire) { _phaseEndStableSince = -1f; return; }
            // The battle is already over on the host: there is no phase left to
            // end, and retrying forever just spins (seen as an endless run of
            // "Player phase complete" on a guest whose host had finished).
            if (PartnerMissionOver) { _phaseEndStableSince = -1f; return; }
            if (CoopNet.Mode == CoopMode.Guest && !SessionSync.GuestBattleReady) { _phaseEndStableSince = -1f; return; }

            var fac = tm.GetActiveFaction();
            if (fac == null || fac.GetAmountOfActorsLeftToAct() > 0) { _phaseEndStableSince = -1f; return; }

            // NEVER LEAVE THE PLAYER PHASE HOLDING AN UNFINISHED UNIT.
            //
            // The engine's "actors left to act" reports 0 for a unit that has
            // ACTED but is not TURN-DONE, while the partner is still waiting for
            // that unit's done flag. Rolling the phase there left the guest on
            // the alien faction with one of its own units unfinished and the host
            // stuck in the player phase waiting for it -- neither could move.
            //
            // Finish our own stragglers through the normal command, so the
            // turn-end is broadcast and the partner learns about it, BEFORE the
            // phase is allowed to end.
            if (FinishOwnPendingUnits(tm)) { _phaseEndStableSince = -1f; return; }

            // Settle briefly first: a turn-end still in flight from the partner
            // must be allowed to land before we declare the phase over.
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_phaseEndStableSince < 0) { _phaseEndStableSince = now; return; }
            if (now - _phaseEndStableSince < 1.5f) return;
            if (now - _lastPhaseNudgeAt < 2f) return;

            bool roundIsDone = false;
            try { roundIsDone = tm.IsRoundDone(); } catch { }

            // Give up after a few tries on the SAME SITUATION -- and "round is
            // now spent" is a DIFFERENT situation from "players finished but the
            // aliens have not". Keying on round+faction alone burned the whole
            // budget early in the round, so when the round genuinely became
            // rollable the cap had already given up and it never tried at all
            // (seen as "will not leave the player phase at round 8" with no
            // preceding attempt). roundDone is therefore part of the key.
            // DECLINING TO ROLL MUST NOT COST A RETRY.
            //
            // This check used to sit BELOW the attempt counter, so the very first
            // pass -- taken while the host was still on our round, where waiting
            // is the correct answer -- burned a try. By the time the host moved
            // ahead and rolling became legitimate, the budget was already spent
            // and the guest never tried again: "Round 1 looks spent but the host
            // is on 1 -- waiting" followed by "will not leave the player phase at
            // round 1 -- not retrying", pinned there while the host reached 3.
            if (CoopNet.Mode == CoopMode.Guest)
            {
                int aheadCheck = DesyncDetector.HostRound;
                if (aheadCheck > 0 && tm.GetRound() >= aheadCheck)
                {
                    if (UnityEngine.Time.realtimeSinceStartup - _lastAheadLogAt > 10f)
                    {
                        _lastAheadLogAt = UnityEngine.Time.realtimeSinceStartup;
                        Plugin.Logger.LogInfo($"[TURN] Round {tm.GetRound()} looks spent but the host is on {aheadCheck} -- waiting rather than rolling ahead");
                    }
                    return;
                }
            }

            // The HOST'S round is part of the situation: once it advances, this is
            // a new problem and deserves a fresh budget. Without it, a cap spent
            // while waiting was never refreshed when waiting stopped being right.
            int stateKey = ((tm.GetRound() * 100 + tm.GetActiveFactionID()) * 2 + (roundIsDone ? 1 : 0)) * 1000
                           + (DesyncDetector.HostRound & 0x3FF);
            if (stateKey != _nudgedState) { _nudgedState = stateKey; _nudgeTries = 0; }
            if (_nudgeTries >= 3)
            {
                if (_nudgeTries == 3)
                {
                    _nudgeTries++;
                    Plugin.Logger.LogWarning($"[TURN] Engine will not leave the player phase at round {tm.GetRound()} -- not retrying");
                }
                return;
            }
            _nudgeTries++;

            _lastPhaseNudgeAt = now;

            // ROLLING THE ROUND WHEN THE ENGINE ALREADY AGREES IT IS OVER.
            //
            // TryCheckRound is the polite form and it DECLINES here: on the guest
            // it left the board sitting at "roundDone=True finishedPct=1.00" with
            // every faction at left=0, a full round behind the host, and even
            // unsuppressing the AI pipeline did not move it (the pump was ON).
            // When the engine's own IsRoundDone() says the round is spent, use
            // its own CheckRound() -- and only then, and only while genuinely
            // behind the host, fall back to NextRound(). This is NOT NextFaction
            // (law 1) and NOT writing m_Round (law 2); it is the engine
            // performing its own rollover.
            // THE GUEST MUST NEVER ROLL PAST THE HOST.
            //
            // Only the NextRound fallback was gated on being behind; CheckRound
            // ran whenever the round looked spent. On a guest whose board keeps
            // completing -- all its units done, downed ones settled, AI waiting on
            // relays -- that rolled round after round unopposed. Seen live as host
            // round 17 against guest round 37: a twenty-round runaway, which is a
            // far worse desync than the stall it was trying to clear.
            //
            // Rolling is only ever legitimate to CATCH UP to the host -- that is
            // now enforced above, before the retry budget is touched.

            if (!roundIsDone)
            {
                Plugin.Logger.LogInfo("[TURN] Player phase complete (0 left to act) -- running the engine's round check");
                tm.TryCheckRound();
                return;
            }

            int before = tm.GetRound();
            Plugin.Logger.LogInfo($"[TURN] Round {before} is spent (engine agrees) -- asking the engine to roll it");
            tm.CheckRound();

            if (tm.GetRound() != before) return;             // rolled: done

            // Still here, and the host has already moved on: take the engine's
            // explicit round advance. Gated on being BEHIND the host so this can
            // never push us ahead of them.
            if (CoopNet.Mode == CoopMode.Guest
                && DesyncDetector.HostRound > before
                && _nudgeTries >= 2)
            {
                Plugin.Logger.LogWarning($"[TURN] Round {before} will not roll and the host is on {DesyncDetector.HostRound} -- advancing the round explicitly");
                tm.NextRound();
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[TURN] phase-end check failed: {e.Message}"); }
    }

    // End any of OUR OWN player units that are still not turn-done. Uses the
    // real Skip command so the end-turn replicates -- silently setting the flag
    // would leave the partner waiting exactly as before. Returns true if it had
    // to finish anything, so the caller re-checks rather than rolling this tick.
    private static bool FinishOwnPendingUnits(TacticalManager tm)
    {
        bool finishedAny = false;
        try
        {
            var actors = tm.GetActiveFaction()?.GetActors();
            for (int i = 0; actors != null && i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null) continue;
                try
                {
                    if (a.IsDying() || a.IsTurnDone()) continue;
                    if (!Ownership.Active || !Ownership.IsKnownPlayerUnit(a)) continue;
                    if (!Ownership.IsLocal(a)) continue;          // never end the partner's
                    // Held right now (e.g. the host's enemies are acting): the
                    // skip would be refused, so report "still pending" and retry.
                    if (ActionBlockedByLock()) { finishedAny = true; break; }

                    CoopRunner.LastUserInputAt = UnityEngine.Time.realtimeSinceStartup;
                    a.SkipTurn();                                  // broadcasts
                    finishedAny = true;
                    Plugin.Logger.LogInfo($"[TURN] Finished '{DesyncDetector.TemplateNameOf(a)}' before ending the phase (it had acted but was not marked done)");
                }
                catch { }
            }
        }
        catch { }
        return finishedAny;
    }

    // True when NO faction anywhere on the board still has a unit to act -- the
    // round is genuinely spent and only the rollover is missing.
    private static bool NothingLeftToActAnywhere(TacticalManager tm)
    {
        try
        {
            var factions = tm.GetFactions();
            if (factions == null) return false;
            for (int i = 0; i < factions.Length; i++)
            {
                var f = factions[i];
                if (f == null) continue;
                if (f.GetAmountOfActorsLeftToAct() > 0) return false;
            }
            return true;
        }
        catch { return false; }
    }

    // Mark done ONLY the pending units of the current faction that carry no
    // usable identity, and only when the host has already left this faction and
    // finishing it will not roll the round on us alone. Identifiable units are
    // never touched -- those have a legitimate relay coming and settling them
    // would be the fabrication law 4 forbids.
    // How long the relay must be silent before we conclude an AI turn-end is never
    // coming. Long enough to cover a real network hop and an animation, short
    // enough that a genuinely lost relay still gets cleared quickly.
    private const float RelayQuietSeconds = 3f;

    // Rate-limit state for the "still waiting on this faction" warning.
    private static string _lastWaitKey;
    private static float _lastWaitLogAt;

    private static void SettleUnaddressableStragglers(TacticalManager tm)
    {
        try
        {
            int facId = tm.GetActiveFactionID();
            int round = tm.GetRound();
            // "The host has FINISHED this faction", not "the host is elsewhere".
            // See DesyncDetector.HostFinishedFaction for why the difference cost
            // us nine cancelled enemy turns a round.
            if (!DesyncDetector.HostFinishedFaction(facId, round)) return;
            if (OtherFactionsDone(tm, facId)) return;      // finishing this WOULD roll the round

            // DO NOT SETTLE WHILE THE HOST'S AI IS STILL ARRIVING.
            //
            // "The host has left faction N" can reach us BEFORE the AI's own moves
            // have finished replaying. Settling then cancels those enemies' turns
            // on this machine, and any relay landing afterwards applies to a unit
            // already marked done -- the enemies simply never act here, and the
            // turn visibly breaks.
            //
            // Settling is a LAST RESORT for relays that are never coming, so wait
            // until the wire has actually gone quiet before deciding that.
            if (BlockingDeferred > 0) return;
            if (!BoardSettled()) return;
            if (UnityEngine.Time.realtimeSinceStartup - LastRemoteOrderAppliedAt < RelayQuietSeconds) return;

            var actors = tm.GetActiveFaction()?.GetActors();
            if (actors == null) return;

            int settled = 0;
            for (int i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null) continue;
                try
                {
                    if (a.IsDying() || a.IsTurnDone()) continue;
                    // A PLAYER UNIT IS NEVER SETTLED HERE -- its turn is a human's
                    // to spend, and ending it from a timeout steals it.
                    if (Ownership.Active && Ownership.IsKnownPlayerUnit(a)) continue;

                    // ADDRESSABLE UNITS ARE SETTLED TOO, NOT JUST ANONYMOUS ONES.
                    //
                    // This used to skip any unit with a usable coop id, on the
                    // theory that a relay would eventually finish it. But the
                    // gate above has already established that the HOST HAS LEFT
                    // THIS FACTION in this round -- which means every unit in it
                    // is done on the host, id or no id. If the done relay was
                    // lost, no future relay will ever arrive, and the guest waits
                    // forever: seen live as 1145 repetitions of "Still on faction
                    // 7 waiting for N unit(s): enemy.alien_01_big_warrior_young
                    // (id N)" -- fully addressable units, skipped by this filter,
                    // holding the board dead while the host played on.
                    //
                    // Adopting the host's state for AI units is exactly what the
                    // authority model prescribes; inventing it is what law 4
                    // forbids, and this is the former.
                    bool anonymous = DesyncDetector.CoopIdOf(a) <= 0;

                    ApplyingRemote = true;                                  // never echo this
                    try { a.SetTurnDone(true); a.MarkAsHasActed(); }
                    finally { ApplyingRemote = false; }
                    settled++;
                    Plugin.Logger.LogWarning(
                        $"[SYNC] Settled straggler '{DesyncDetector.TemplateNameOf(a)}'" +
                        $"{(anonymous ? " (no identity)" : " (its done-relay never arrived)")} holding faction {facId} -- the host has already left it");
                }
                catch { }
            }
            if (settled > 0)
                // Say what actually happened. These are not "unidentifiable" --
                // that wording predates the change that made addressable units
                // settleable too, and it sent us looking for an identity bug
                // that was not there.
                CoopRunner.ShowDesyncWarning($"Recovered {settled} stalled enemy turn(s) -- your partner had already finished them");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[SYNC] straggler settle failed: {e.Message}"); }
    }

    // DOWNED UNITS CANNOT ACT, SO THEY MUST NOT HOLD THE TURN ORDER.
    //
    // The engine moves a downed unit OUT of the player faction and into another
    // one (faction 2 in every case seen). It arrives there not turn-done, and it
    // can never become done, because nothing will ever move it -- so that faction
    // never completes and the machine sits on it forever. Observed as the host
    // stuck at "f2 player_squad.darby THEIRS ap=14" with f2:left=1 while the
    // guest had long since moved on.
    //
    // Marking an incapacitated unit done is deterministic -- both machines see
    // the same incapacity from the same relayed damage -- so this is not the
    // fabrication law 4 forbids. It states a fact the engine already agrees with.
    private static float _lastIncapSettleAt;

    // Units that refuse the done flag: logged once each, then left alone rather
    // than retried every second (441 futile settles in a single mission).
    private static readonly System.Collections.Generic.HashSet<System.IntPtr> _futileSettles = new();
    // #97: fleeing player units whose turn the owner ended through the engine, per round.
    private static readonly System.Collections.Generic.HashSet<(System.IntPtr, int)> _rallyTurnEnds = new();

    private static float _lastReassertAt;

    // EACH SIDE RE-ASSERTS THE TURN-ENDS ONLY IT CAN KNOW ABOUT.
    //
    // The host's snapshots carry player turn-done flags, so a lost turn-end from
    // the HOST is eventually repaired on the guest. There is no channel in the
    // other direction: when a GUEST's 'done'/'skip' order is lost, nothing ever
    // tells the host, and it waits on that unit forever. Seen live as host
    // f1:left=1 / theirs=1 against guest f1:left=0 -- the guest had finished
    // everything and the host was still waiting on one of the guest's units.
    //
    // Authority follows the ACTOR, so the fix is symmetric rather than
    // host-special: every few seconds each machine re-states the done flags for
    // the units IT owns. Re-stating is idempotent (the receiver ignores a flag it
    // already has) and only touches finished units, so it can never re-open or
    // steal an activation -- it just closes the window where a dropped packet
    // becomes a permanent wait.
    // DISABLED -- this did more harm than the problem it was written for.
    //
    // The idea was that each side re-states the turn-ends only IT can know about,
    // closing the window where a dropped 'done' leaves the partner waiting. In
    // practice the receiver files every relayed order and expects it to produce an
    // observable change; a restatement is a no-op by design, so it was recorded as
    // an order that NEVER RESOLVED, which requests a resync, which repeats every
    // few seconds, which escalates to "SYNC PAUSE started: repeated sync failures".
    // That is precisely the freeze class this session was spent removing,
    // reintroduced by the fix for a problem that had never actually been observed
    // to deadlock anything.
    //
    // If it is revisited: a restatement needs its own message type that the order
    // tracker does not expect to change anything, and it must never restate a
    // COMMAND whose repetition is destructive (see the removed 'land' re-assert).
    private const bool ReassertEnabled = false;

    internal static void ReassertOwnTurnEnds()
    {
        if (!ReassertEnabled) return;
        if (!CoopNet.Connected || SyncPauseActive || PartnerMissionOver) return;
        if (SessionSync.HostHoldFire) return;

        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now - _lastReassertAt < 4f) return;

        var tm = TacticalManager.Get();
        if (tm == null) return;
        if (!BoardSettled()) return;             // mid-animation: let it land first

        _lastReassertAt = now;

        try
        {
            if (!Ownership.Active) return;       // nothing to claim ownership of yet
            var actors = tm.GetPlayerFaction()?.GetActors();
            for (int i = 0; actors != null && i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null) continue;
                try
                {
                    if (a.IsDying() || !a.IsTurnDone()) continue;          // only FINISHED units
                    if (!Ownership.IsKnownPlayerUnit(a) || !Ownership.IsLocal(a)) continue;

                    var tile = a.GetLastTile();
                    if (tile == null) continue;
                    var pos = tile.GetTilePos();
                    int ax = pos.x, ay = pos.y;

                    int apNow = 0;
                    try { apNow = a.GetActionPoints(); } catch { }

                    var msg = new CoopMsg { T = "done", AX = ax, AY = ay, Prm = 1, MoveAction = apNow };
                    StampIds(msg, a, isOrder: false);   // a restatement of fact, not a new order
                    CoopNet.Send(msg);

                    // REMOVED: a periodic 'land' re-assert of our own positions.
                    //
                    // It looked idempotent and was not. 'land' SETS a position, so
                    // re-sending it every few seconds yanked units back to whatever
                    // tile they held when the timer fired -- dragging them backwards
                    // if they had moved since. Measured damage: repeated
                    // "Order 'land' ... never resolved -- DESYNC", escalating to
                    // "SYNC PAUSE started: repeated sync failures", and a hash gap
                    // that grew to 16,162,493 (~30 actors) instead of the single
                    // 961 (one tile) it was meant to close.
                    //
                    // Restating a COMMAND is not the same as restating a FACT. The
                    // turn-end above is safe because "this unit is finished" cannot
                    // be undone by repetition; a position can.
                }
                catch { }
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[SYNC] turn-end re-assert failed: {e.Message}"); }
    }

    // RELAY AI TURN-ENDS BY OBSERVATION, NOT BY HOOKING ONE METHOD.
    //
    // The old relay was a prefix on Actor.SetTurnDone, and the host sent ZERO
    // aidone messages all battle: nothing in the AI actually calls that method, so
    // the hook could never fire. The guest's faction pump therefore never learned
    // the aliens had finished and stranded on the alien faction waiting for units
    // nobody would ever move -- the freeze.
    //
    // Watching the FLAG instead of a call site cannot miss, whatever internal path
    // the engine uses to end an AI activation. Host only: AI state is the host's to
    // report, and the guest applies what it is told.
    private static readonly System.Collections.Generic.HashSet<System.IntPtr> _aiDoneRelayed = new();
    private static int _aiDoneRound = -1;
    private static float _lastAiDoneScan;

    internal static void RelayAiTurnEnds()
    {
        if (CoopNet.Mode != CoopMode.Host || !CoopNet.Connected) return;
        if (SyncPauseActive || PartnerMissionOver) return;

        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now - _lastAiDoneScan < 0.25f) return;
        _lastAiDoneScan = now;

        var tm = TacticalManager.Get();
        if (tm == null) return;

        try
        {
            int round = tm.GetRound();
            if (round != _aiDoneRound) { _aiDoneRound = round; _aiDoneRelayed.Clear(); }

            var actors = DesyncDetector.AllActors(tm);
            for (int i = 0; actors != null && i < actors.Length; i++)
            {
                var a = actors[i];
                if (a == null) continue;
                try
                {
                    if (!a.IsTurnDone()) continue;
                    if (Ownership.Active && Ownership.IsKnownPlayerUnit(a)) continue;  // AI only
                    if (!_aiDoneRelayed.Add(a.Pointer)) continue;                      // already sent

                    var tile = a.GetLastTile();
                    int ax = -1, ay = -1;
                    if (tile != null) { var p = tile.GetTilePos(); ax = p.x; ay = p.y; }

                    var msg = new CoopMsg { T = "aidone", AX = ax, AY = ay };
                    StampIds(msg, a, isOrder: false);
                    CoopNet.Send(msg);
                }
                catch { }
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[SYNC] AI turn-end relay failed: {e.Message}"); }
    }

    private static IntPtr _incapBattle;
    private static float _incapBattleSince;

    internal static void SettleIncapacitatedUnits()
    {
        if (!CoopNet.Connected) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now - _lastIncapSettleAt < 1f) return;
        _lastIncapSettleAt = now;

        var tm = TacticalManager.Get();
        if (tm == null || SyncPauseActive) return;

        // NOT ON A BOARD THAT IS STILL SPAWNING. At battle start an actor exists
        // before its elements do, so "no living elements" read as dead: the guest's
        // reloaded battle (GuestReload, run 31) killed 14 enemies in its first
        // second, each Die() throwing on a null gameObject. Round 0 is deployment,
        // and the first seconds of any battle are the engine's to finish.
        if (tm.Pointer != _incapBattle) { _incapBattle = tm.Pointer; _incapBattleSince = now; return; }
        if (now - _incapBattleSince < 5f) return;
        try { if (tm.GetRound() < 1) return; } catch { return; }

        // A STRANDED UNIT MUST BE FOUND WHEREVER IT IS, NOT ONLY UNDER THE CURSOR.
        //
        // This scan used to cover just the ACTIVE faction, so the stranded-player-
        // unit case below could only fire if the engine's cursor happened to be
        // sitting on the faction holding the stuck unit. It usually was not: seen
        // live with the guest's cursor on f1 while 'player_squad.darby' was stranded
        // in f2 (downed there but still alive in f1 on the host). Each machine then
        // waited on a different faction and neither could ever finish -- a deadlock
        // the watchdog could not break either, because no amount of state syncing
        // un-downs a unit.
        //
        // Scanning every faction costs nothing and is what makes this safety net
        // actually reachable.
        try
        {
            var everyActor = DesyncDetector.AllActors(tm);
            for (int i = 0; everyActor != null && i < everyActor.Length; i++)
            {
                var a = everyActor[i];
                if (a == null) continue;
                try
                {
                    // AN ACTOR WITH NO LIVING ELEMENTS IS DEAD -- SAY SO.
                    //
                    // Actors die through the "ehp" path (the last element hits 0
                    // and Die()s), which never marks the ACTOR itself. The state
                    // hash skips dying actors, so a side still holding an
                    // all-dead actor counted a whole extra unit: a persistent
                    // ~512,558 mismatch across successive rounds (one actor's
                    // worth of hash), which no snapshot could heal because the
                    // host's snapshot has no such actor to send a correction for.
                    //
                    // Deterministic from element state both machines already
                    // share, so this is reading a fact, not inventing one.
                    // ONE ROAD FOR ACTOR DEATH. This used to repeat the
                    // "all elements dead?" scan inline and then write
                    // a.m_IsDying = true itself -- a second half-applied
                    // destruction, with the same consequence as the one in
                    // MarkDeadIfNoLivingElements: no wreck, no tile cleanup, no
                    // move into GetDeadActors() on this machine. It now calls that
                    // method, which kills through the engine and keeps the flag
                    // write only as a fallback. Identical guards (already dying /
                    // any element alive), so behaviour is unchanged except that
                    // the death is now a real one.
                    MarkDeadIfNoLivingElements(a);

                    if (a.IsTurnDone()) continue;
                    // Already proven to refuse the flag: do not touch it again.
                    // The previous "continue" only skipped to the next actor, so
                    // this retried every single tick -- an endless run of
                    // SetTurnDone on the same unit, which is what the freeze
                    // actually looked like in the log.
                    if (_futileSettles.Contains(a.Pointer)) continue;

                    // Genuinely unable to act: DOWN or STUNNED only.
                    //
                    // !IsActive() was also treated as incapacitated and it does
                    // NOT mean that -- healthy enemies were being marked done
                    // (31 settles for one alien template in a single run), which
                    // robs them of activations and desyncs the turn cycle from
                    // the host's relays.
                    bool incapacitated = a.IsDying() || a.IsStunned();

                    // ALSO: a player unit sitting in a NON-player faction.
                    //
                    // Neither player can ever be given a turn for it there --
                    // turn control only hands out the player faction -- so it
                    // cannot act and cannot finish, and the faction holding it
                    // never completes. Seen as the host stuck on faction 2 with
                    // "player_squad.darby THEIRS" pending while the guest had
                    // that same unit finished.
                    if (!incapacitated && Ownership.Active && Ownership.IsKnownPlayerUnit(a))
                    {
                        int playerFac = -1;
                        try { playerFac = tm.GetPlayerFaction()?.GetIndex() ?? -1; } catch { }
                        // The unit's OWN faction -- we now scan every faction, so
                        // the active one says nothing about where THIS unit is.
                        int here = -1;
                        try { here = a.GetFactionID(); } catch { }
                        if (playerFac >= 0 && here >= 0 && here != playerFac)
                        {
                            incapacitated = true;
                            Plugin.Logger.LogWarning(
                                $"[TURN] '{DesyncDetector.TemplateNameOf(a)}' is a player unit stranded in faction {here} -- nobody can move it there");

                            // A FLEEING UNIT MUST BE ABLE TO RALLY (BUGS.md #97, r151-r153).
                            // The engine's morale recovery runs in the unit's TURN END;
                            // SetTurnDone below skips it, so a broken player squad stayed
                            // fleeing for the rest of the battle (r153: Pike, rounds 3-9,
                            // on every machine). Its OWNER ends the turn through the
                            // engine instead -- relayed like any skip -- once per round,
                            // and the owner's mstate relay carries the rally to everyone.
                            bool fleeing = false;
                            try { fleeing = a.GetMoraleState() == MoraleState.Fleeing; } catch { }
                            int rnd = -1;
                            try { rnd = tm.GetRound(); } catch { }
                            if (fleeing && Ownership.IsLocal(a) && _rallyTurnEnds.Add((a.Pointer, rnd)))
                            {
                                try
                                {
                                    a.SkipTurn();
                                    Plugin.Logger.LogInfo($"[TURN] '{DesyncDetector.TemplateNameOf(a)}' is fleeing in faction {here} -- ended its turn through the engine (morale recovery); now faction {a.GetFactionID()}, morale {a.GetMorale():F0}, {a.GetMoraleState()}");
                                    if (a.IsTurnDone()) continue;
                                }
                                catch (Exception e) { Plugin.Logger.LogWarning($"[TURN] ending the fleeing unit's turn failed: {e.GetType().Name}"); }
                            }
                            // ...and nobody else may end it first (r154 22:01:44: M3
                            // settled the host's fleeing Pike and told the host to end
                            // its turn too, so the owner's engine turn end never ran).
                            // The owner's skip arrives as a relayed order.
                            else if (fleeing && !Ownership.IsLocal(a)) continue;
                        }
                        // FLEEING BUT STILL IN THE PLAYER FACTION (r157 08:55:55, round 6
                        // hang). The host broke Pike 72 -> 0 in one step: its engine
                        // recorded Wavering and kept it in faction 1, yet GetMoraleState
                        // reads Fleeing at morale 0. Turn control skips a fleeing unit
                        // (TurnPhases.HasUnitsLeft, #81), so nobody was handed Pike while
                        // the engine still waited on it -- host on ctrl=them, M2 holding
                        // control with nothing to move. Turn control now counts it, so it
                        // comes to the owner, and the owner ends its turn through the
                        // engine -- only while holding control, or the skip is muted and
                        // never relayed. Retried each second until the engine takes it.
                        else if (playerFac >= 0 && here == playerFac && Ownership.IsLocal(a)
                                 && (!TurnControl.Active || TurnControl.IsMyTurn))
                        {
                            bool fleeing = false;
                            try { fleeing = a.GetMoraleState() == MoraleState.Fleeing; } catch { }
                            int rnd = -1;
                            try { rnd = tm.GetRound(); } catch { }
                            if (fleeing && !_rallyTurnEnds.Contains((a.Pointer, rnd)))
                            {
                                try
                                {
                                    a.SkipTurn();
                                    if (a.IsTurnDone()) _rallyTurnEnds.Add((a.Pointer, rnd));
                                    Plugin.Logger.LogInfo($"[TURN] '{DesyncDetector.TemplateNameOf(a)}' reads fleeing in the player faction -- nobody can command it; ended its turn through the engine (done={a.IsTurnDone()}); now faction {a.GetFactionID()}, morale {a.GetMorale():F0}, {a.GetMoraleState()}");
                                }
                                catch (Exception e) { Plugin.Logger.LogWarning($"[TURN] ending the fleeing unit's turn failed: {e.GetType().Name}"); }
                            }
                        }
                    }

                    if (!incapacitated) continue;

                    ApplyingRemote = true;              // a statement of fact, not an order
                    try { a.SetTurnDone(true); a.MarkAsHasActed(); }
                    finally { ApplyingRemote = false; }

                    // Did it actually take? 441 settles in one mission means the
                    // flag was not sticking and we were retrying every second --
                    // wasted work, and a sign the fix was not doing what its log
                    // line claimed. Report the difference rather than assume.
                    bool stuck = false;
                    try { stuck = a.IsTurnDone(); } catch { }

                    if (!stuck)
                    {
                        if (_futileSettles.Add(a.Pointer))
                        {
                            Plugin.Logger.LogError(
                                $"[TURN] '{DesyncDetector.TemplateNameOf(a)}' is down but REFUSES to be marked done -- removing it from the faction so the turn order can continue");

                            // NOTE: an earlier version REMOVED the unit from the
                            // faction roster here. That was actively harmful --
                            // ResolveOrderActor finds actors by walking faction
                            // rosters, so a removed unit became unaddressable and
                            // every subsequent order for it failed
                            // ("Order 'aidone'/'move'/'land' never resolved" 74
                            // times in one run, each triggering a resync). The
                            // cure was worse than the stall. Leave it in place and
                            // simply stop retrying it.
                        }
                        continue;
                    }

                    Plugin.Logger.LogWarning(
                        $"[TURN] '{DesyncDetector.TemplateNameOf(a)}' is down and cannot act -- marked done so the turn order can continue");

                    // TELL THE PARTNER. A unit one machine has retired must not
                    // take a turn on the other.
                    //
                    // Hard freeze in manual testing: the host saw the guest's
                    // squad break under enemy fire and flee (the engine moves a
                    // fleeing unit out of the player faction), so at round start
                    // it marked that squad done here -- silently. On the guest the
                    // morale break had never happened: the squad took its turn and
                    // ended it, the guest's engine moved on to the garrison's
                    // activation, and the host, which never ran that turn, waited
                    // on the guest for good. Enemy fire is the host's to decide,
                    // and "this unit cannot act" is a fact about the board, so it
                    // travels as the same idempotent turn-done a skip sends.
                    if (Ownership.Active && Ownership.IsKnownPlayerUnit(a))
                    {
                        try
                        {
                            var tile = a.GetLastTile();
                            if (tile != null)
                            {
                                var pos = tile.GetTilePos();
                                int apNow = 0;
                                try { apNow = a.GetActionPoints(); } catch { }
                                var msg = new CoopMsg { T = "done", AX = pos.x, AY = pos.y, Prm = 1, MoveAction = apNow };
                                StampIds(msg, a, isOrder: false);
                                CoopNet.Send(msg);
                                Plugin.Logger.LogInfo($"[TURN] Told partner '{DesyncDetector.TemplateNameOf(a)}' cannot act -- ending its turn there too");
                            }
                        }
                        catch (Exception e) { Plugin.Logger.LogWarning($"[TURN] could not relay down unit: {e.Message}"); }
                    }
                }
                catch { }
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[TURN] incapacitated settle failed: {e.Message}"); }
    }

    private static int _roundHoldLoggedFor = -1;
    internal static void UpdateFactionPump()
    {
        // Cleared the moment there is no battle, so the NEXT mission is not
        // stood down by the previous one's end. Must sit outside the pump
        // condition below -- that condition tests the flag itself.
        if (PartnerMissionOver && TacticalManager.Get() == null) PartnerMissionOver = false;

        bool shouldPump = false;
        if (CoopNet.Mode == CoopMode.Guest && CoopNet.Connected
            && SessionSync.GuestBattleReady && !SyncPauseActive && !PartnerMissionOver)
        {
            var tm = TacticalManager.Get();
            if (tm != null)
            {
                try
                {
                    // STRANDED ON THE PLAYER FACTION AT THE END OF A ROUND.
                    //
                    // The pump below only covers AI factions. But the guest can
                    // also come to rest on the PLAYER faction with every unit --
                    // players and AI alike -- already done, one round behind the
                    // host (seen live as r=6 fac=1 mine=0 theirs=0 ai=0 while the
                    // host was on 7, then "Round mismatch ... persisted 8s").
                    // Nothing advances it: the pump ignores the player faction,
                    // and TryCheckRound cannot roll the round while the AI
                    // pipeline is suppressed. Unsuppressing here lets the engine
                    // perform the rollover itself, exactly as it does for an AI
                    // faction. Gated on being genuinely BEHIND the host, so this
                    // can never push the guest a round ahead.
                    if (tm.IsPlayerTurn()
                        && DesyncDetector.HostRound > 0
                        && tm.GetRound() < DesyncDetector.HostRound
                        && !AnyActorMoving()
                        && BlockingDeferred == 0
                        && NothingLeftToActAnywhere(tm))
                    {
                        shouldPump = true;
                    }
                    else if (!tm.IsPlayerTurn())
                    {
                        var actors = tm.GetActiveFaction()?.GetActors();
                        bool anyPending = false;
                        if (actors != null)
                            for (int i = 0; i < actors.Count; i++)
                            {
                                var a = actors[i];
                                if (a == null || a.IsDying()) continue;
                                if (!a.IsTurnDone()) { anyPending = true; break; }
                            }
                        shouldPump = !anyPending;   // done or empty: engine may advance

                        // THE ROUND ROLLS ON THE HOST FIRST. If advancing would
                        // end the round here while the host is still in it, wait.
                        // Units can look done on the guest while the host still
                        // has them to act -- three different ways seen 2026-10-02
                        // (hand-back settle, a stale deferred advance, and AI
                        // passengers left done after a truck ejected them: hand5
                        // round 3, guest 3.4 s early). Each early roll risks the
                        // turn-order deadlock (BUGS #53). The host's aiadvance
                        // carries its round the moment it rolls, so the wait is
                        // short. Unknown host round (0/-1): do not block.
                        if (shouldPump)
                        {
                            int hostR = DesyncDetector.HostRound;
                            int myR = tm.GetRound();
                            if (hostR > 0 && hostR <= myR && OtherFactionsDone(tm, tm.GetActiveFactionID()))
                            {
                                shouldPump = false;
                                if (_roundHoldLoggedFor != myR)
                                {
                                    _roundHoldLoggedFor = myR;
                                    Plugin.Logger.LogInfo($"[SYNC] Round {myR} looks finished here but the host is still in it -- waiting for the host to roll it first");
                                }
                            }
                        }

                        // STALL FAILSAFE: if the host has already left this
                        // faction while a unit here is still "not done", a done
                        // flag never reached us. Mark the stragglers done (a
                        // data fix -- never a NextFaction call) so the normal
                        // pump can advance us the usual way. Safe by
                        // construction: the host has already finished them.
                        if (anyPending)
                        {
                            float now = UnityEngine.Time.realtimeSinceStartup;
                            if (_stallSince < 0) _stallSince = now;
                            else if (now - _stallSince > 3f)
                            {
                                _stallSince = -1f;
                                // THE FAILSAFE MUST NEVER ROLL THE ROUND.
                                // Completing this faction advances the cycle --
                                // and if every OTHER faction is already done,
                                // that advance IS the round rollover, which
                                // pushes the guest a whole round ahead of the
                                // host (seen as "host 1, guest 2" then
                                // "host 2, guest 3"). Only do it when the host
                                // is genuinely a round ahead, i.e. we are the
                                // ones catching up.
                                // THE GUEST MUST NEVER FABRICATE AI DONE-FLAGS.
                                //
                                // Completing an AI faction locally robs those
                                // enemies of their activation and can roll the
                                // round on this machine alone -- observed as
                                // "the round went over and the enemies never
                                // got their turn", with the guest reaching
                                // round 2 while the host was still on round 1.
                                // The only legitimate source of AI turn-state
                                // is the host's relays, so a stall now asks the
                                // host for authoritative state instead of
                                // inventing it.
                                // Diagnostic only. The real advancement signal
                                // is the host's own faction advance (see
                                // OnRemoteFactionAdvance); if we are still here
                                // after that, name what we are waiting on
                                // instead of guessing or hammering the host.
                                _stallSince = now - (StallRecheck - 5f);   // re-report slowly

                                // PHANTOM UNITS HOLDING A FACTION.
                                //
                                // Seen live: the guest sat on faction 2 waiting
                                // on "player_squad.carda(id -1)" -- a unit with
                                // NO identity, in a faction the host reported as
                                // empty. Nothing can ever finish it: no relay can
                                // address a unit we cannot identify, so the guest
                                // waits forever.
                                //
                                // Per the authority model, an unidentifiable unit
                                // is the host's to decide. Settling ONLY the
                                // unaddressable ones, and only once the host has
                                // demonstrably left this faction, adopts the
                                // host's state rather than inventing our own --
                                // which is what law 4 actually forbids.
                                SettleUnaddressableStragglers(tm);
                                LogPendingUnits(tm);
                            }
                        }
                        else _stallSince = -1f;
                    }
                }
                catch { }
            }
        }
        if (shouldPump != _pumpActive)
        {
            _pumpActive = shouldPump;
            Plugin.Logger.LogInfo(_pumpActive
                ? "[SYNC] Faction pump ON (active AI faction finished -- engine advances)"
                : "[SYNC] Faction pump OFF");
        }
        HandBackIfHostWillNotFollow();
        if (!_pumpActive) UnsettleAfterHandBack();
    }

    // THE HOST'S ENGINE DOES NOT ALTERNATE ON THE GUEST'S END TURN.
    //
    // On the guest, ending a unit's turn ends the player activation and the
    // engine moves to the AI faction (one player activation, one AI activation).
    // On the host the same end arrives as a relayed "done" and the host's engine
    // stays on the players -- it alternates only on its OWN end turn, or once
    // every player unit is done. So the guest's AI wait is satisfied only when
    // the HOST ends one of its units next. If the host has nothing left to end
    // while the guest still has units, that never comes: host waits on the
    // guest's unit, guest waits on the host's AI -- freeze (hand test 2026-10-02
    // 17:00:59, BUGS #54; round 3 of the same run escaped only via a stale
    // deferred advance). In exactly that state the host will stay on the
    // players, so the guest follows it back now. The settle is the same
    // hand-back as OnRemoteFactionAdvance's, un-settled once the engine moves on.
    //
    // Only the HOST can tell "staying on the players" apart from "running an AI
    // activation that has not relayed anything yet" (the guest sees identical
    // unit counts in both), so the host decides and says so: "hoststay".

    private static void CountPlayerUnitsLeft(TacticalManager tm, out int mine, out int theirs)
    {
        mine = 0; theirs = 0;
        var factions = tm.GetFactions();
        if (factions == null) return;
        for (int f = 0; f < factions.Length; f++)
        {
            var actors = factions[f]?.GetActors();
            if (actors == null) continue;
            for (int i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || a.IsDying() || a.IsTurnDone()) continue;
                if (!Ownership.IsKnownPlayerUnit(a)) continue;
                if (Ownership.IsLocal(a)) mine++; else theirs++;
            }
        }
    }

    // HOST: a partner unit's turn just ended here. Check shortly after (the
    // engine's own faction switch can land a frame later) whether we stayed on
    // the players with nothing of our own left while the partner still has units.
    private static float _partnerEndAt = -1f;
    private static bool _hostPassed;
    internal static void NotePartnerTurnEnded()
    {
        if (CoopNet.Mode == CoopMode.Host) _partnerEndAt = UnityEngine.Time.realtimeSinceStartup;
    }

    // HOST pressed PASS TURN. That hands control on WITHOUT ending a unit, so our
    // engine stays on the players -- while a guest whose last unit end moved it
    // to the AI faction is waiting for our next own end, which the pass skips.
    // Same standoff as above, with units of our own left (user repro 2026-10-08:
    // guest's Pike buffs another guest squad, ends, host passes -> guest
    // hourglass until FIX DESYNC; BUGS #72).
    internal static void NoteHostPassedTurn()
    {
        if (CoopNet.Mode != CoopMode.Host) return;
        _hostPassed = true;
        _partnerEndAt = UnityEngine.Time.realtimeSinceStartup;
    }

    private static void HostSayIfStayingOnPlayers()
    {
        if (_partnerEndAt < 0f || CoopNet.Mode != CoopMode.Host || !CoopNet.Connected || !Ownership.Active) return;
        if (UnityEngine.Time.realtimeSinceStartup - _partnerEndAt < 0.4f) return;
        _partnerEndAt = -1f;
        bool passed = _hostPassed;
        _hostPassed = false;
        var tm = TacticalManager.Get();
        if (tm == null) return;
        try
        {
            if (!tm.IsPlayerTurn()) return;                 // the engine went to the AI: the guest's wait is real
            CountPlayerUnitsLeft(tm, out int mine, out int theirs);
            if (theirs == 0) return;                        // round is ending
            if (mine > 0 && !passed) return;                // our next end pairs with it
            CoopNet.Send(new CoopMsg { T = "hoststay", Prm = tm.GetRound() });
            Plugin.Logger.LogInfo(passed
                ? $"[SYNC] Host passed the turn without ending a unit, so our engine stays on the players (round {tm.GetRound()}, {theirs} of theirs to move) -- told the guest"
                : $"[SYNC] Partner ended a unit; we have none left, so our engine stays on the players (round {tm.GetRound()}, {theirs} of theirs to move) -- told the guest");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[SYNC] stay check failed: {e.Message}"); }
    }

    // GUEST: the host's word that it is staying on the players this round.
    private static int _hostStayRound = -1;
    private static float _hostStayAt = -1f;
    internal static void OnHostStay(int round)
    {
        _hostStayRound = round;
        _hostStayAt = UnityEngine.Time.realtimeSinceStartup;
        Plugin.Logger.LogInfo($"[SYNC] Host says it stays on the players (round {round})");
    }

    private static void HandBackIfHostWillNotFollow()
    {
        HostSayIfStayingOnPlayers();
        if (_hostStayRound < 0) return;
        if (CoopNet.Mode != CoopMode.Guest || !CoopNet.Connected || !SessionSync.GuestBattleReady
            || SyncPauseActive || PartnerMissionOver || !Ownership.Active || !FollowHostFactions) { _hostStayRound = -1; return; }
        var tm = TacticalManager.Get();
        if (tm == null) { _hostStayRound = -1; return; }
        try
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_hostStayRound != tm.GetRound() || now - _hostStayAt > 10f) { _hostStayRound = -1; return; }
            // Our own end may still be switching factions; wait until we are
            // actually on the AI faction the host will not run.
            if (tm.IsPlayerTurn()) return;
            if (BlockingDeferred > 0 || AnyActorMoving()) return;
            CountPlayerUnitsLeft(tm, out int mine, out _);
            _hostStayRound = -1;
            if (mine == 0) return;                          // nothing to hand back to
            int faction = tm.GetActiveFactionID();
            ApplyingRemote = true;
            try { CompleteActiveFaction(tm, forHandBack: true); }
            finally { ApplyingRemote = false; }
            Plugin.Logger.LogInfo($"[SYNC] Left AI faction {faction} to follow the host onto the players (round {tm.GetRound()}, {mine} of ours still to move)");
        }
        catch (Exception e) { _hostStayRound = -1; Plugin.Logger.LogWarning($"[SYNC] hand-back check failed: {e.Message}"); }
    }

    // AI units settled ONLY to hand the activation back to the players (see
    // OnRemoteFactionAdvance). The host still has them pending this round, so
    // they must not stay done here: left settled, the guest's engine saw the AI
    // faction spent and rolled the round the moment its last player unit ended,
    // 17 s ahead of the host, and the two turn orders never re-met (hand test
    // 2026-10-02 16:42:36 -> freeze 16:43:02, BUGS #53). Once the engine is on
    // a different faction, un-settle them: a turn-done reset is local, never
    // relayed, and makes the faction count them as left to act again (the old
    // relayed-reset bug proved that), so the guest's next AI activation waits
    // for the host's relays exactly as the host's own engine does.
    private static readonly System.Collections.Generic.List<Actor> _settledForHandBack = new();
    private static int _settledFaction = int.MinValue, _settledRound = -1;

    private static void UnsettleAfterHandBack()
    {
        if (_settledForHandBack.Count == 0) return;
        var tm = TacticalManager.Get();
        if (tm == null) { _settledForHandBack.Clear(); return; }
        try
        {
            if (tm.GetRound() != _settledRound) { _settledForHandBack.Clear(); return; }   // round rolled: engine reset them anyway
            if (tm.GetActiveFactionID() == _settledFaction) return;                         // engine not past it yet
            int n = 0;
            ApplyingRemote = true;
            try
            {
                foreach (var a in _settledForHandBack)
                {
                    if (a == null || a.IsDying() || !a.IsTurnDone()) continue;
                    a.SetTurnDone(false);
                    n++;
                }
            }
            finally { ApplyingRemote = false; }
            _settledForHandBack.Clear();
            Plugin.Logger.LogInfo($"[SYNC] Hand-back done: {n} AI unit(s) of faction {_settledFaction} pending again (the host still has them to act this round)");
        }
        catch (Exception e) { _settledForHandBack.Clear(); Plugin.Logger.LogWarning($"[SYNC] un-settle failed: {e.Message}"); }
    }

    // Host aiadvance messages are now diagnostic only -- the pump advances the
    // guest from replicated done-state, so there is nothing to apply and no
    // message races to guard against.
    // THE HOST'S FACTION ADVANCE IS THE GUEST'S CUE TO ADVANCE TOO.
    //
    // This engine uses ALTERNATING ACTIVATIONS: the host enters an AI faction,
    // runs exactly ONE unit, and leaves again. The guest cannot mirror that on
    // its own -- with AI processing suppressed, its engine only leaves an AI
    // faction once EVERY unit of that faction is done, and nothing on the guest
    // ever makes that happen mid-round. So the guest sat on the alien faction
    // while the host waited for the guest's players to move: a mutual wait, and
    // the freeze that kept coming back in different disguises.
    //
    // Settling that faction here is safe, and this is the key point: on the
    // guest, AI units do not need turn-flags in order to ACT -- their moves and
    // attacks arrive as relayed orders, which are applied regardless of turn
    // state. Their flags only gate OUR faction cycling. What is NOT safe is
    // letting this roll the round ahead of the host, which is what made enemies
    // appear to skip turns, so that case is refused and we wait for the host.
    // The guest follows the host's faction advances. This is required, not
    // optional: with AI suppressed the guest's engine only leaves an AI faction
    // once EVERY unit of that faction is done, which nothing on the guest makes
    // happen mid-round. Turning it off (the 3.3.0 revert) does not "return to
    // v2.0.0" -- v2.0.0 relied on the same relay -- it just strands the guest.
    internal static bool FollowHostFactions = true;

    // A UNIT WHOSE LAST ELEMENT A RELAY KILLED IS DEAD NOW, NOT AT THE NEXT SWEEP.
    //
    // The engine marks an actor dying inside its own damage path, which a relayed
    // hit never enters. The turn-order sweep does catch "no living elements but not
    // marked dying", but only on its next pass -- and in manual testing a pirate
    // flamethrower truck the host had destroyed stayed standing on the guest long
    // enough for the late roster check to report "BATTLES HAVE DRIFTED APART: host
    // 0, you 1". Same rule as that sweep, applied the moment the last element goes.
    private static readonly System.Collections.Generic.HashSet<IntPtr> _killedHere = new();
    private static IntPtr _killedBattle;

    internal static void MarkDeadIfNoLivingElements(Actor actor)
    {
        if (actor == null) return;
        try
        {
            if (actor.IsDying()) return;
            // ONCE. IsDying() does not stay true after the engine's death has run,
            // so the periodic sweep called Die() on the same corpse again every
            // second (seen 2026-09-25 on both machines) -- and bodies vanished.
            // Entity.IsAlive() is the engine's own "already dead"; the set covers
            // any build where that is not cleared either.
            try { if (!actor.IsAlive()) return; } catch { }
            var elems = actor.GetElements();
            if (elems == null) return;
            for (int e = 0; e < elems.Count; e++)
            {
                if (elems[e] == null) continue;
                try { if (elems[e].IsAlive()) return; } catch { return; }
            }
            // USE THE ENGINE'S OWN DEATH, NOT A FLAG WRITE.
            //
            // This used to be `actor.m_IsDying = true` and nothing else, which is
            // a half-applied destruction: the flag says dead while none of the
            // engine's death bookkeeping has run. On the machine that did NOT
            // land the killing blow, a destroyed vehicle therefore left no
            // Tile.Wreckage, no tile cleanup, and no move into GetDeadActors().
            //
            // That is a reported bug (v3.14.0): "the skeletons of the transport
            // leave behind an empty space that cannot be passed through, but the
            // player who did not knock out this transport cannot clear the tile
            // from the skeleton." They cannot clear it because DestroyWreckage
            // needs a wreck, and on their machine one was never created.
            //
            // Infantry hid this: a squaddie IS an element, so element deaths are
            // the whole story and squads looked correct. A vehicle's wreck comes
            // from the ACTOR-level path, which nothing here ever ran.
            //
            // Both machines now take the same road -- the engine's -- which is
            // also the rule the DD co-op project learned from several crashes:
            // only ever write a value the engine would write itself, at a point
            // the engine would write it.
            //
            // SAVE AND RESTORE ApplyingRemote rather than clearing it: callers
            // already hold it true across a wider block, and clearing it here
            // would unguard the rest of theirs.
            var battle = TacticalManager.Get()?.Pointer ?? IntPtr.Zero;
            if (battle != _killedBattle) { _killedBattle = battle; _killedHere.Clear(); }
            if (!_killedHere.Add(actor.Pointer)) return;
            bool prev = ApplyingRemote;
            bool died = false;
            ApplyingRemote = true;
            try { actor.Die(); died = true; }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning(
                    $"[SYNC] Actor.Die() refused for '{DesyncDetector.TemplateNameOf(actor)}': {e.Message} -- falling back to the dying flag");
            }
            finally { ApplyingRemote = prev; }

            // Fallback, not the default. A flag-only death is what caused the
            // wreck divergence, but it is still better than an actor the engine
            // thinks is alive with no living elements.
            if (!died) { try { actor.m_IsDying = true; } catch { } }

            Plugin.Logger.LogInfo(
                $"[SYNC] '{DesyncDetector.TemplateNameOf(actor)}' lost its last element to a relayed hit -- "
                + (died ? "killed through the engine (wreck/tile cleanup included)" : "marked dead (flag only)"));
        }
        catch { }
    }

    // AN ADVANCE BELONGS TO THE FACTION THE HOST LEFT, NOT THE ONE WE ARE ON.
    //
    // Caught as a hard freeze in manual testing. The host finished AI faction 3
    // and then faction 6 back to back (only a truck acted; the rest of 6 were
    // fleeing or suppressed). Both "aiadvance" messages reached the guest while it
    // was still replaying faction 3, and both settled faction 3 -- the second as a
    // no-op. When the guest's engine then moved on to faction 6 nothing settled
    // it, and it sat on "Still on faction 6 waiting for 1 unit(s):
    // enemy.pirate_outcasts" for good, while the host had already handed turn
    // control to the guest and waited on it: a deadlock with both boards frozen.
    //
    // An advance for a faction we have not reached yet is remembered, keyed on
    // (round, faction), and applied when we arrive (see TakeDeferredAdvance).
    private static readonly System.Collections.Generic.HashSet<long> _hostLeftFactions = new();
    private static long FactionKey(int round, int faction) => ((long)round << 20) | (uint)(faction & 0xFFFFF);

    // Called from the guest's waiting loop: the host already left the faction we
    // are stuck on, so settle it now.
    internal static bool TakeDeferredAdvance(TacticalManager tm)
    {
        if (CoopNet.Mode != CoopMode.Guest || _hostLeftFactions.Count == 0 || tm == null) return false;
        try
        {
            if (tm.IsPlayerTurn()) return false;
            int round = tm.GetRound(), faction = tm.GetActiveFactionID();
            if (!_hostLeftFactions.Remove(FactionKey(round, faction))) return false;
            // Same rule as OnRemoteFactionAdvance: settling only exists to hand
            // the activation back to the players. With no player unit left it
            // empties the AI faction and the engine rolls the round on us alone
            // -- hand test 2026-10-02 rounds 3/4/5: guest 8 s / 32 s / 7 s ahead
            // of the host, every time via this deferred path (BUGS #53). Wait
            // instead: the host's relays finish those units, and the round
            // rolls on the host first. (Unless the host has already rolled past
            // this round: then rolling is exactly right, and this is the
            // stranding the deferred advance was written for.)
            if (!AnyPlayerUnitLeftToAct(tm) && DesyncDetector.HostRound <= round)
            {
                Plugin.Logger.LogInfo($"[SYNC] Reached faction {faction} (round {round}), which the host had left once -- nobody has a move left, so waiting for the host's AI rather than rolling the round");
                return false;
            }
            ApplyingRemote = true;
            try { CompleteActiveFaction(tm, forHandBack: true); }
            finally { ApplyingRemote = false; }
            Plugin.Logger.LogInfo($"[SYNC] Reached faction {faction} (round {round}), which the host had already left -- settled it now");
            return true;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[SYNC] deferred faction advance failed: {e.Message}"); return false; }
    }

    internal static void OnRemoteFactionAdvance(int hostRound, int leftFaction = int.MinValue, int leftRound = -1)
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        if (!FollowHostFactions) return;               // v2.0.0: pump only
        var tm = TacticalManager.Get();
        if (tm == null || !SessionSync.GuestBattleReady) return;
        try
        {
            if (tm.IsPlayerTurn())
            {
                // Already with the players: an advance for an AI faction we have
                // not played yet this round still has to be kept for when we do.
                if (leftFaction != int.MinValue && leftRound >= tm.GetRound())
                    _hostLeftFactions.Add(FactionKey(leftRound, leftFaction));
                return;
            }
            int myRound = tm.GetRound();

            if (leftFaction != int.MinValue && leftRound >= 0
                && (leftRound != myRound || tm.GetActiveFactionID() != leftFaction))
            {
                if (leftRound >= myRound)                   // behind the host: keep it for later
                {
                    _hostLeftFactions.Add(FactionKey(leftRound, leftFaction));
                    Plugin.Logger.LogInfo($"[SYNC] Host left faction {leftFaction} (round {leftRound}) while we are still on faction {tm.GetActiveFactionID()} (round {myRound}) -- will settle it when we get there");
                }
                return;
            }

            // ONLY settle when a HUMAN still has something to do this round.
            //
            // Settling exists for one reason: to hand the activation back to the
            // players. If neither player has a unit left to move, settling buys
            // nothing -- and it costs everything, because with the AI factions
            // emptied the engine rolls straight into the next round while the
            // host is still working through its own alien activations. That is
            // how the guest reached round 2 with fresh action points while the
            // host was still in round 1 (logged as AP drift "mine 100 -> host 0"
            // across every unit).
            //
            // Waiting instead is free: the players have nothing to do anyway, and
            // the round then rolls on the host first, exactly as it should.
            if (!AnyPlayerUnitLeftToAct(tm))
            {
                if (hostRound <= myRound)
                {
                    Plugin.Logger.LogInfo("[SYNC] Host advanced, but nobody has a move left -- waiting for the host to roll the round first");
                    return;
                }
            }
            ApplyingRemote = true;
            try { CompleteActiveFaction(tm, forHandBack: true); }
            finally { ApplyingRemote = false; }
            Plugin.Logger.LogInfo($"[SYNC] Host left an AI faction (round {hostRound}) -- settled ours to follow its cycle");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[SYNC] faction follow failed: {e.Message}"); }
    }

    // Kept as a no-op-compatible stub for callers.
    internal static void TryApplyFactionAdvances() { }

    // Mark every actor of the current faction as having finished its turn, so
    // TacticalManager.NextFaction will leave the faction. Used on the guest,
    // whose AI never ran to mark them done itself.
    private static void CompleteActiveFaction(TacticalManager tm, bool forHandBack = false)
    {
        try
        {
            var actors = tm.GetActiveFaction()?.GetActors();
            if (actors == null) return;
            if (forHandBack && (_settledFaction != tm.GetActiveFactionID() || _settledRound != tm.GetRound()))
            {
                // A second settle of the SAME faction before the hand-back ran
                // must add to the list, never drop the first one's units.
                _settledForHandBack.Clear();
                _settledFaction = tm.GetActiveFactionID();
                _settledRound = tm.GetRound();
            }
            for (int i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || a.IsTurnDone()) continue;
                a.SetTurnDone(true);
                a.MarkAsHasActed();
                if (forHandBack) _settledForHandBack.Add(a);
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[SYNC] CompleteActiveFaction failed: {e.Message}"); }
    }

    internal static int NewSeed() => Guid.NewGuid().GetHashCode();

    internal static void Reseed(int seed) => TacticalManager.s_Random = new PseudoRandom(seed);

    internal static Tile GetTile(int x, int y)
    {
        var map = TacticalManager.Get()?.GetMap();
        return map?.GetTile(x, y);
    }

    // A container is an Entity, not necessarily an Actor, so container relays
    // need the tile's entity rather than its actor.
    internal static Entity GetTileEntity(int x, int y)
    {
        try { return GetTile(x, y)?.GetEntity(); } catch { return null; }
    }

    internal static Tile GetTileAt(int x, int y) => GetTile(x, y);

    // #4b: the unit holding skill `skillId` whose minion command (CommandMove /
    // CommandUseSkill handler) is bound to `minion` -- e.g. the squad that deployed
    // the wardog, for its strike. Null if none.
    internal static Actor MinionCommandOwner(Actor minion, string skillId)
    {
        try
        {
            var tm = TacticalManager.Get();
            if (tm == null || minion == null || string.IsNullOrEmpty(skillId)) return null;
            foreach (var a in DesyncDetector.AllActors(tm))
            {
                if (a == null || a.Pointer == minion.Pointer) continue;
                var s = a.GetSkills()?.GetSkillByID(skillId)?.TryCast<Skill>();
                if (s == null) continue;
                var hs = s.GetSkillEventHandlers();
                for (int j = 0; j < (hs?.Length ?? 0); j++)
                {
                    var h = hs[j];
                    if (h == null) continue;
                    Actor bound = null;
                    var mv = h.TryCast<Menace.Tactical.Skills.Effects.Minions.CommandMoveHandler>();
                    if (mv != null) bound = mv.m_Minion;
                    var us = h.TryCast<Menace.Tactical.Skills.Effects.Minions.CommandUseSkillHandler>();
                    if (us != null) bound = us.m_Minion;
                    if (bound != null && bound.Pointer == minion.Pointer) return a;
                }
            }
        }
        catch { }
        return null;
    }

    // Tile.GetActor() THROWS InvalidCastException when the tile holds a Structure
    // (it casts the tile's entity to Actor) -- seen r59. Every tile lookup goes
    // through these: a structure tile has no actor and is not free.
    internal static Actor ActorOn(Tile t)
    {
        try { return t?.GetActor(); } catch { return null; }
    }

    internal static bool TileFree(Tile t)
    {
        if (t == null) return false;
        try { return t.GetActor() == null; } catch { return false; }
    }

    // A remote move on the receiver animates -- the moving unit isn't registered
    // on its destination tile until the move finishes, which can be a frame or
    // two after we've already drained the next message (its turn-end, or a
    // follow-up order). Rather than dropping those as desyncs, an order whose
    // actor can't be resolved yet is deferred and retried for a short window;
    // only if the unit truly never appears do we flag a real desync.
    private static readonly System.Collections.Generic.List<System.Tuple<CoopMsg, float>> _deferred = new();
    private static bool _retrying;
    private static bool _deferResult;
    private static float _boardBusyAt;         // last time any actor was animating a move
    private const float IdleGrace = 3f;         // board must be idle this long before we call it a desync
    private const float HardFailsafe = 30f;     // never hold an order forever
    private const float ArrivalSettle = 20f;    // "reinforcements are still landing" window
    private const float ArrivalGrace = 18f;     // how long to hold an order for a unit still arriving

    // Called by the actor-addressed cases when the actor can't be resolved.
    private static void UnresolvedOrder(CoopMsg m)
    {
        if (_retrying) { _deferResult = true; return; }     // still unresolved during a retry pass
        _deferred.Add(System.Tuple.Create(m, UnityEngine.Time.realtimeSinceStartup));
    }

    // AN ORDER FROM THE HOST'S NEXT ROUND (BUGS.md #96, r150 21:01:15 + 21:04:50).
    //
    // The host's AutoCombat moved its Pike 6-120 ms after the host rolled the
    // round; the clients were still pumping the previous round's AI factions and
    // rolled 0.6-1 s later. The move landed on a Pike that was still turn-done
    // with 0 AP here, the engine refused it (accepted=False), and the unit only
    // got there when the host's "land" snapped it across 4 s later. A move heals
    // that way; a skill or shot refused the same way would simply be lost. So a
    // partner's order for a unit that is turn-done HERE while this machine is
    // behind the host's round waits in the deferred queue until our round roll
    // gives the unit its activation back. Held orders get the hard failsafe, not
    // the idle grace: the board is idle precisely because we are waiting.
    private static readonly System.Collections.Generic.HashSet<CoopMsg> _roundHeld = new();

    private static bool HoldForHostRound(CoopMsg m, Actor actor)
    {
        if (CoopNet.Mode != CoopMode.Guest || actor == null) return false;
        try
        {
            var tm = TacticalManager.Get();
            if (tm == null || tm.GetRound() >= DesyncDetector.HostRound) return false;
            if (!Ownership.Active || !Ownership.IsKnownPlayerUnit(actor) || Ownership.IsLocal(actor)) return false;
            if (!actor.IsTurnDone()) return false;
            if (_roundHeld.Add(m))
                Plugin.Logger.LogInfo($"[SYNC] Held partner's '{m.T}' for '{DesyncDetector.TemplateNameOf(actor)}' -- host is on round {DesyncDetector.HostRound}, we are on {tm.GetRound()} and the unit is still done here");
        }
        catch { return false; }
        UnresolvedOrder(m);
        return true;
    }

    // ---- Native auto-end-turn settings ------------------------------------
    // The game has its own "Auto end turns" (GeneralEndTurnOnLowAP) and "End
    // turn on selecting unit" (GeneralEndTurnOnSelect). Forcing these OFF while
    // connected is strictly better than gating EndTurn after the fact: the
    // engine never STARTS an auto-end, so there is no engine-timed turn end to
    // race and no reliance on "was there a click just now?" heuristics. The
    // player's own values are restored on disconnect, so single-player is
    // untouched.
    private static bool _turnSettingsForced;
    private static bool _origEndTurnLowAP, _origEndTurnOnSelect;

    internal static void EnforceTurnSettings()
    {
        bool want = CoopNet.Connected && Plugin.ManualTurnEnd.Value;
        if (want == _turnSettingsForced) return;
        try
        {
            if (want)
            {
                _origEndTurnLowAP = BoolPlayerSettingExtensions.Get(BoolPlayerSetting.GeneralEndTurnOnLowAP);
                _origEndTurnOnSelect = BoolPlayerSettingExtensions.Get(BoolPlayerSetting.GeneralEndTurnOnSelect);
                BoolPlayerSettingExtensions.Set(BoolPlayerSetting.GeneralEndTurnOnLowAP, false);
                BoolPlayerSettingExtensions.Set(BoolPlayerSetting.GeneralEndTurnOnSelect, false);
                Plugin.Logger.LogInfo($"[TURN] Auto-end-turn forced OFF for co-op (your settings: lowAP={_origEndTurnLowAP}, onSelect={_origEndTurnOnSelect})");
            }
            else
            {
                BoolPlayerSettingExtensions.Set(BoolPlayerSetting.GeneralEndTurnOnLowAP, _origEndTurnLowAP);
                BoolPlayerSettingExtensions.Set(BoolPlayerSetting.GeneralEndTurnOnSelect, _origEndTurnOnSelect);
                Plugin.Logger.LogInfo("[TURN] Auto-end-turn settings restored to your values");
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[TURN] Could not adjust auto-end settings: {e.Message}"); }
        _turnSettingsForced = want;
    }

    // Exposed for DesyncDetector.Tick: snapshots must not be sent or applied
    // while units are animating or while our own orders are still in flight.
    internal static bool BoardBusy() => AnyActorMoving();

    // Is a local order gate closed right now? Callers use this to WAIT rather
    // than misread a refused order as "there was nothing to do" -- which is how
    // a brief hold-fire ended up exhausting a whole squad's turn.
    internal static bool LocallyBlocked()
    {
        try
        {
            if (SyncPauseActive) return true;
            if (SessionSync.HostHoldFire) return true;
            if (AiFireInFlight()) return true;
            return ActionBlockedByLock();
        }
        catch { return false; }
    }

    // ENEMY FIRE IN FLIGHT.
    //
    // Enemies shoot during the PLAYERS' phase -- reaction fire and overwatch --
    // so "it is our turn" is not the same as "nothing hostile is resolving".
    // A player who moves while an enemy shot is being resolved lets the two
    // machines settle that shot against different boards: different cover,
    // different position, potentially a different outcome. The action lock
    // serialises player against player but nothing serialised player against
    // AI, so this is the remaining ordering hazard in a normal turn.
    //
    // A short window after each AI shot holds player ACTIONS only. AI orders
    // themselves are never held -- doing that would stop the host relaying its
    // own AI and deadlock the cycle.
    private static float _aiFireUntil;
    private const float AiFireHold = 1.25f;

    internal static void NoteAiFire() => _aiFireUntil = UnityEngine.Time.realtimeSinceStartup + AiFireHold;

    // ONLY the window after an actual enemy shot.
    //
    // This used to also return true whenever ANY actor was moving anywhere --
    // which during an alien turn is nearly always. The result was that player
    // moves and attacks were blocked almost permanently: units ended their turns
    // having done nothing (14 turn-ends, 3 moves, 0 attacks in one mission).
    // "Something is animating" is not "enemy fire is resolving".
    internal static bool AiFireInFlight()
    {
        if (UnityEngine.Time.realtimeSinceStartup < _aiFireUntil) return true;

        // A MULTI-HIT ENEMY ATTACK IS STILL "ENEMY FIRE".
        //
        // The fixed window only covered a single shot, so when an enemy struck
        // several times in one activation the gaps between hits let a player act
        // mid-burst -- resolving our order against a board the partner was still
        // applying damage to. Hold for as long as the AUTHORITATIVE side says an
        // enemy phase is running, not for a fixed number of milliseconds.
        try
        {
            if (CoopNet.Mode == CoopMode.Guest)
                return DesyncDetector.HostPhaseKnown && !DesyncDetector.HostIsInPlayerPhase;

            var tm = TacticalManager.Get();
            return tm != null && !tm.IsPlayerTurn();
        }
        catch { return false; }
    }

    // Player orders carry UsageParameter.Default; AI shots carry
    // AutoAudioVolume (log-verified). That is how a player action is told from
    // an AI one at the gate, where the actor is not always to hand.
    internal static bool IsPlayerOrder(UsageParameter p) => p == UsageParameter.Default;

    // Applying a remote AP figure must never leave a unit unable to act. A unit
    // that still has its turn and is reported at 0 AP is almost certainly a
    // stale or mis-attributed figure, and applying it strands that unit for the
    // whole round. Observed live: after an AP-GRANTING skill the guest ended up
    // with 0 AP across its squad and could not fire at all. Zero is only
    // credible once the unit is actually finished.
    internal static bool ApValueIsSafe(Actor a, int ap)
    {
        if (a == null || ap < 0) return false;
        if (ap > 0) return true;
        try { return a.IsTurnDone() || a.IsDying(); }
        catch { return false; }
    }

    // BOARD SETTLE WINDOW.
    //
    // AnyActorMoving() is an INSTANT check, and movement resolves tile by tile --
    // so it reads false in the gaps between steps, while a unit is still walking.
    // Snapshots captured in one of those gaps record units in transit, which is
    // why corrections came out consistently ONE TILE short of the destination,
    // and why two snapshots could disagree and bounce a unit back and forth.
    //
    // Requiring the board to have been quiet for a moment removes that whole
    // class: a snapshot is only taken, or applied, once movement has genuinely
    // finished rather than merely paused between steps.
    private static float _lastMovingAt;

    internal static void TrackBoardActivity()
    {
        if (AnyActorMoving()) _lastMovingAt = UnityEngine.Time.realtimeSinceStartup;
    }

    internal static bool BoardSettled(float quietFor = 0.75f)
        => !AnyActorMoving() && UnityEngine.Time.realtimeSinceStartup - _lastMovingAt > quietFor;
    internal static int DeferredCount => _deferred.Count;
    // Orders held for our own round roll (#96) must not block the paths that
    // PERFORM that roll -- they would wait on each other until the failsafe.
    private static int BlockingDeferred => _deferred.Count - _roundHeld.Count;

    // Has the oldest unresolved order been waiting long enough that it is stuck
    // rather than merely in flight? Used so the backlog cannot lock a player out
    // of their own turn indefinitely.
    internal static bool DeferredIsStale()
    {
        try
        {
            if (_deferred.Count == 0) return false;
            float oldest = _deferred[0].Item2;
            return UnityEngine.Time.realtimeSinceStartup - oldest > 4f;
        }
        catch { return false; }
    }
    internal static float LastOrderSentAt;
    // When a partner's move/landing last arrived (DesyncDetector drops a held
    // snapshot older than it -- see there).
    internal static float LastRemoteMoveAt = -1f;
    internal static float LastRemoteOrderAppliedAt;

    // SYNC PAUSE: while active, BOTH machines freeze -- no local orders, no AI
    // processing -- until the hard resync lands and the guest confirms.
    // Managed by DesyncDetector (StartSyncPause / pause flow in Tick).
    //
    // A PAUSE CANNOT OUTLIVE THE CONNECTION. This is not defensive tidying; it is
    // the fix for a reported battle-ending freeze (player report, v3.14.0: "if the
    // guest disconnects during the battle and the host continues it alone, the
    // enemy refuses to make moves and the battle freezes, you have to download the
    // save and start the battle again"). The chain was:
    //
    //   1. a resync starts, so this goes true;
    //   2. the guest drops before confirming, so CoopNet.Connected goes false;
    //   3. DesyncDetector.Tick() early-returns on !CoopNet.Connected -- ABOVE its
    //      own 12s "stuck pause" failsafe, so that failsafe never runs;
    //   4. AiPatches.GuestSuppressAI opens with `if (SyncPauseActive) return false`
    //      and is NOT host/guest gated, so the HOST's ProcessAI is suppressed too;
    //   5. the AI never acts again. Nothing recovers it but reloading the save.
    //
    // The per-round state-hash resync is what made this likely rather than rare:
    // a pause is in flight every single round, so almost any disconnect lands in
    // one. Gating on the connection here fixes it at the source, and every other
    // reader of this flag wants the same answer -- all ~20 of them exist to hold
    // state while WAITING FOR A PARTNER, and there is no partner.
    //
    // Deliberately a property, not a clear-on-disconnect: a reconnect must not be
    // able to resurrect a pause whose resync will never be confirmed. This mirrors
    // Ownership.Active, which is `_active && CoopNet.Connected` for the same reason.
    private static bool _syncPause;
    internal static bool SyncPauseActive
    {
        get => _syncPause && CoopNet.Connected;
        set => _syncPause = value;
    }

    // True while inside Actor.SkipTurn -- an explicit player command whose
    // internal SetTurnDone must pass the manual-turn-end filter.
    internal static bool InSkipTurn;

    // A PARTNER'S UNIT NEVER HAD ITS TURN END DISPATCHED HERE (runs 55-56).
    //
    // TacticalManager.InvokeOnTurnEnd tells every turn-end listener that a unit
    // has finished. The engine calls it from its activation flow for the ACTIVE
    // actor -- on the machine that ran the unit. A replayed skip does run
    // Actor.OnTurnEnd (logged remote=True), but that is not the dispatch, and the
    // "done" that follows found the unit already done and returned. So on both
    // machines nothing listening for turn ends heard a partner's unit: capture
    // zones (CaptureArea.OnTurnEnd -- a guest unit in the zone earned the host no
    // tickets) and delayed ship abilities among them. Dispatch it once per unit
    // per round; the prefix on InvokeOnTurnEnd records every dispatch, the
    // engine's own included, so it is never doubled.
    private static readonly System.Collections.Generic.HashSet<(IntPtr, int)> _turnEndDispatched = new();

    private static int CurrentRound()
    {
        try { return TacticalManager.Get()?.GetRound() ?? -1; } catch { return -1; }
    }

    internal static void NoteTurnEndDispatched(Actor actor)
    {
        if (actor == null) return;
        if (_turnEndDispatched.Count > 4000) _turnEndDispatched.Clear();
        _turnEndDispatched.Add((actor.Pointer, CurrentRound()));
    }

    internal static void EnsureTurnEnded(Actor actor, string why)
    {
        var tm = TacticalManager.Get();
        if (actor == null || tm == null) return;
        if (_turnEndDispatched.Contains((actor.Pointer, CurrentRound()))) return;
        try { tm.InvokeOnTurnEnd(actor); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[SYNC] Turn end for partner's '{DesyncDetector.TemplateNameOf(actor)}' threw: {e.GetType().Name}: {e.Message}"); return; }
        Plugin.Logger.LogInfo($"[SYNC] Ran turn end for partner's '{DesyncDetector.TemplateNameOf(actor)}' ({why})");
    }

    // The host's battle is OVER (its result screen is up). The guest's battle is
    // still standing, and with the host no longer relaying AI orders the guest
    // waits forever on enemies that will never act -- logging "Still on faction 7
    // waiting for 9 unit(s)" every second until the host closes the results and
    // the post-mission sync yanks it out. Knowing the mission ended lets the
    // guest stop waiting and say so instead.
    internal static bool PartnerMissionOver;

    // THE GUEST FOLLOWS THE HOST OUT OF THE BATTLE.
    //
    // Standing the cycle down was not enough: the guest kept sitting in a battle
    // the host had already left, so the two were on completely different screens
    // until the post-mission sync happened to drag it out. Ending the battle here
    // puts both players on the result screen together and back to the menu as a
    // pair, which is what "the mission is over" should mean for both of them.
    //
    // Marked as a remote application so it passes GuestMayNotFinish (this IS the
    // host telling us) and is not echoed back as our own decision.
    private static void HandleMissionEnd()
    {
        try
        {
            var tm = TacticalManager.Get();
            if (tm == null) return;                     // already out of the battle

            ApplyingRemote = true;
            try { tm.Finish(TacticalFinishReason.Leave); }
            finally { ApplyingRemote = false; }

            Plugin.Logger.LogInfo("[SESSION] Left the battle to follow the host to the result screen");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[SESSION] Could not follow the host out of the battle: {e.Message}");
        }
    }

    // Stamp an order with the actor's stable identity (see CoopMsg.RId/CId) so
    // the receiver can resolve it even when tiles have drifted.
    // isOrder=false for ehp/edie: damage packets don't move units, so they must
    // NOT push LastOrderSentAt forward -- during sustained fire that would make
    // the guest discard every incoming healing snapshot as "stale" and drift
    // would never be corrected (observed live with a machine-gun burst).
    internal static void StampIds(CoopMsg m, Actor actor, bool isOrder = true)
    {
        try
        {
            // Our own order counts as activity too -- whichever side holds control,
            // issuing orders means they are playing and must not be hurried.
            if (isOrder) TurnControl.NoteHolderActivity();
            if (isOrder) LastOrderSentAt = UnityEngine.Time.realtimeSinceStartup;
            if (actor == null) return;
            if (isOrder && CoopNet.Mode == CoopMode.Host && !Ownership.IsKnownPlayerUnit(actor))
                m.HVis = FogSync.HostVis(actor);

            // TAKEOVER DETECTOR. An order we ORIGINATE for a player unit that
            // this side does not own is the squad-takeover bug, and until now
            // it left no trace: both machines log the same order line, because
            // the replay runs through the same hook. Naming it at the source
            // makes the next occurrence provable instead of suspected.
            if (isOrder && Ownership.Active && Ownership.IsKnownPlayerUnit(actor) && !Ownership.IsLocal(actor))
                Plugin.Logger.LogError($"[OWN] TAKEOVER: this side issued '{m.T}' for a unit it does NOT own ('{DesyncDetector.TemplateNameOf(actor)}' at ({m.AX},{m.AY})) -- mode={CoopNet.Mode}");
            m.RId = DesyncDetector.RosterIndexOf(actor);
            if (m.RId >= 0) return;
            m.CId = DesyncDetector.CoopIdOf(actor);
            if (m.CId < 0)
            {
                // AI actor not captured yet (e.g. mid-battle reinforcement) --
                // capture ids now and retry once.
                var tm = TacticalManager.Get();
                if (tm != null) { DesyncDetector.EnsureCoopIds(tm); m.CId = DesyncDetector.CoopIdOf(actor); }
            }
            if (m.CId < 0 && UnityEngine.Time.realtimeSinceStartup - _lastNoIdLogAt > 5f)
            {
                // A unit we can't identify is the seed of every cascade -- name
                // it so the gap can actually be closed.
                _lastNoIdLogAt = UnityEngine.Time.realtimeSinceStartup;
                Plugin.Logger.LogWarning($"[SYNC] Sending '{m.T}' with NO identity -- unit '{DesyncDetector.TemplateNameOf(actor)}' at ({m.AX},{m.AY}), faction {actor.GetFactionID()}");
            }
        }
        catch { }
    }
    private static float _lastNoIdLogAt;
    private static int _unresolvedSinceReport;
    private static float _lastUnresolvedReportAt;

    // ---- Building / container entry ---------------------------------------
    // Entering a building is NOT part of MoveTo: the game sets
    // m_EnterContainerAfterMovement on the actor and the containment happens
    // when the move finishes. Replaying only the move would leave the partner's
    // copy standing on the tile while the mover is inside -- a divergence the
    // position fixer cannot repair (it can only teleport between tiles, and a
    // contained unit has no tile at all).
    internal static void StampContainer(CoopMsg m, Actor actor)
    {
        try
        {
            var c = actor?.m_EnterContainerAfterMovement;
            var t = c?.GetTile();
            if (t == null) return;
            var p = t.GetTilePos();
            m.CtX = p.x; m.CtY = p.y;
        }
        catch { }
    }

    internal static void ApplyContainer(Actor actor, CoopMsg m, Tile dest)
    {
        if (actor == null) return;
        try
        {
            if (m.CtX < 0 || m.CtY < 0)
            {
                actor.m_EnterContainerAfterMovement = null;   // plain move / leaving a building
                return;
            }
            var ent = GetTile(m.CtX, m.CtY)?.GetEntity();
            if (ent == null || ent.Pointer == actor.Pointer)
                ent = dest?.GetEntity();                      // fallback: building at the destination
            actor.m_EnterContainerAfterMovement = ent;
            if (ent == null)
                Plugin.Logger.LogWarning($"[SYNC] No container found at ({m.CtX},{m.CtY}) -- unit will stop outside the building");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[SYNC] Container entry failed: {e.Message}"); }
    }

    // Resolve the actor an order refers to: stable identity first (immune to
    // position drift -- the root cause of every order-miss cascade), then the
    // sender's tile as a last resort.
    internal static Actor ResolveOrderActor(CoopMsg m)
    {
        Actor a = null;
        if (m.RId >= 0) a = DesyncDetector.FindByRosterIndex(m.RId);
        if (a == null && m.CId >= 0)
        {
            var tm = TacticalManager.Get();
            if (tm != null) { DesyncDetector.EnsureCoopIds(tm); a = DesyncDetector.FindByCoopId(tm, m.CId); }
        }
        if (a == null)
        {
            var onTile = ActorOn(GetTile(m.AX, m.AY));
            // Never let a unit with its OWN different identity stand in (BUGS #50).
            if (DesyncDetector.TileFallbackFits(onTile, m.RId, m.CId, 0)) a = onTile;
        }
        // BUGS #59: a deferred snapshot older than this relay must not roll it back.
        if (a != null) LastRelayAt[a.Pointer] = UnityEngine.Time.realtimeSinceStartup;
        if (a != null && m.HVis != 0) FogSync.ApplyHostVis(a, m.HVis, m.T);
        return a;
    }

    // When each actor last received a relayed message (order, esup, estance ...).
    internal static readonly System.Collections.Generic.Dictionary<IntPtr, float> LastRelayAt = new();

    // Whether any unit is currently animating a move. A deferred order usually
    // just needs the moving unit to finish arriving, so while ANYTHING is moving
    // we keep waiting no matter how long -- only when the board is genuinely idle
    // and the unit still isn't there do we treat it as a real desync.
    // This runs EVERY FRAME -- it must never take a throwing path repeatedly
    // (a per-frame interop exception plus its logging is exactly the kind of
    // flood that froze both machines in 1.4.21).
    private static bool _movingApiBroken;

    internal static bool AnyActorMoving()
    {
        var tm = TacticalManager.Get();
        if (tm == null) return false;
        if (!_movingApiBroken)
        {
            try
            {
                var moving = tm.GetMovingActors();
                if (moving != null) return moving.Count > 0;
            }
            catch
            {
                _movingApiBroken = true;
                Plugin.Logger.LogWarning("[SYNC] GetMovingActors unavailable -- using the per-actor scan from now on");
            }
        }
        try
        {
            var actors = tm.GetActorsRoot()?.GetComponentsInChildren<Actor>(true);
            if (actors != null)
                for (int i = 0; i < actors.Length; i++)
                    if (actors[i] != null && actors[i].m_IsMoving) return true;
        }
        catch { }
        return false;
    }

    // RELAYED MOVE LANDING CHECK.
    //
    // A relayed move is replayed as a real pathfound walk, so it can end
    // somewhere other than the tile the sender reached: the path may differ
    // around units that are a moment out of step, the walk can be cut short, or
    // MoveTo can decline it outright. That was the last standing desync source --
    // ~7 AI position corrections a mission, healed only when a snapshot happened
    // to catch them, which is both late and visible (enemies snap).
    //
    // Checking the landing here makes the correction immediate and exact, and
    // turns the failure into a logged event instead of silent drift.
    private static readonly System.Collections.Generic.List<(Actor a, int x, int y, float due, bool accepted, bool enter)> _relayedMoves = new();

    private static void TrackRelayedMove(Actor a, int x, int y, bool accepted, bool enter = false)
    {
        if (a == null) return;
        // ONLY THE LATEST DESTINATION MATTERS. A unit can be ordered to move
        // twice in a round, and a pending check from the first move would fire
        // while the unit is legitimately walking to the second -- snapping it
        // BACK to the stale tile. That turned this check from a fix into a
        // desync source (58 snaps and 17 player corrections in one mission).
        for (int i = _relayedMoves.Count - 1; i >= 0; i--)
            if (_relayedMoves[i].a != null && _relayedMoves[i].a.Pointer == a.Pointer)
                _relayedMoves.RemoveAt(i);

        // Wait long enough for the sender's authoritative "land" report to
        // arrive first. That report is the CORRECT answer when a move was cut
        // short by reaction fire, whereas this check assumes the requested
        // destination was reached -- snapping to it would drag a legitimately
        // interrupted unit forward to a tile it never reached. "land" clears
        // this entry, so this only ever fires as a fallback when no report came.
        float delay = accepted ? 3f : 0.2f;
        _relayedMoves.Add((a, x, y, UnityEngine.Time.realtimeSinceStartup + delay, accepted, enter));
        if (_relayedMoves.Count > 64) _relayedMoves.RemoveAt(0);
    }

    internal static void ClearRelayedMoveChecks() => _relayedMoves.Clear();

    internal static void ClearRelayedMoveCheck(Actor a)
    {
        if (a == null) return;
        for (int i = _relayedMoves.Count - 1; i >= 0; i--)
            if (_relayedMoves[i].a != null && _relayedMoves[i].a.Pointer == a.Pointer)
                _relayedMoves.RemoveAt(i);
    }

    // Is this actor's current movement one we are replaying for the partner?
    // Used so the landing report is sent only by the machine that OWNS the move.
    internal static bool IsReplayingMoveFor(Actor a)
    {
        if (a == null) return false;
        for (int i = 0; i < _relayedMoves.Count; i++)
            if (_relayedMoves[i].a != null && _relayedMoves[i].a.Pointer == a.Pointer) return true;
        return false;
    }

    internal static void VerifyRelayedMoves()
    {
        if (_relayedMoves.Count == 0) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        for (int i = 0; i < _relayedMoves.Count; )
        {
            var (a, x, y, due, accepted, enter) = _relayedMoves[i];
            if (now < due) { i++; continue; }
            // Still walking: give it more time rather than snapping mid-stride.
            // THIS unit's walk only, plus a short grace for others' (r159 09:19:15:
            // a hidden chaingun team stopped at (11,26) instead of (11,22) on both
            // clients, and "anyone moving" kept deferring this check through the
            // whole AI phase -- the snapshot healed it 7 s later, three times a
            // mission). Any walk at all re-armed it forever.
            bool selfMoving = false;
            try { selfMoving = a != null && a.m_IsMoving; } catch { }
            // Capped: a unit whose moving flag never clears must not hold this
            // entry forever -- IsReplayingMoveFor keeps the snapshot off it meanwhile.
            if (accepted && selfMoving && now - due < 8f) { i++; continue; }
            if (accepted && now - due < 2f && AnyActorMoving()) { i++; continue; }
            _relayedMoves.RemoveAt(i);
            try
            {
                if (a == null) continue;
                // A downed / stunned / contained unit declines every move. Trying
                // anyway produced a run of "snap to (...) REFUSED" for the same
                // unit and drove the failure escalation into a sync pause.
                //
                // BUT A CONTAINED UNIT IS NOT THE SAME CASE AS A DOWNED ONE, and
                // skipping both in silence is how the rider divergence hid. A
                // passenger the partner has already ejected is PRECISELY a unit
                // whose position is wrong here and needs correcting -- refusing
                // to touch it leaves it parked inside the transport for the rest
                // of the battle while the partner fights it out on the board.
                // Still skipped (a teleport cannot take it out of a container),
                // but no longer silently: this is the one that needs a fix.
                if (!DesyncDetector.CanBeRepositioned(a))
                {
                    bool contained = false;
                    try { contained = ContainerSync.IsContained(a); } catch { }
                    if (contained && !enter)
                        Plugin.Logger.LogWarning(
                            $"[CONTAINER] '{DesyncDetector.TemplateNameOf(a)}' was told to move to ({x},{y}) "
                            + "but is still inside its container here -- correction skipped. The partner has "
                            + "ejected it and this machine has not.");
                    continue;
                }
                var pos = a.GetLastTile()?.GetTilePos();
                if (pos != null && pos.Value.x == x && pos.Value.y == y) continue;   // landed correctly

                var dest = GetTile(x, y);
                if (dest == null) continue;
                // THERE IS NO OTHER HEALER -- this is it.
                //
                // Skipping an occupied destination assumed something else would
                // reconcile it. Nothing does, and the case is not rare: a large
                // creature registers across a 3x3 here while the partner
                // registers only its anchor tile, so every tile around it looks
                // occupied on this machine and free on theirs. Relayed walks to
                // those tiles are refused (accepted=False) and the unit is left
                // wherever it was -- which is how the ejected riders ended up a
                // tile out of step for a whole battle.
                //
                // The forced teleport below does land on such a tile: it is how
                // the container healer puts riders onto the partner's tiles,
                // measured. And co-occupancy is a board the engine itself
                // produces -- the partner has a rider standing on a tile its own
                // transport covers. So trust the sender's position and place the
                // unit, rather than silently keeping a different board.
                var blocker = ActorOn(dest);
                if (blocker != null && blocker.Pointer == a.Pointer) continue;   // already ours

                string from = pos == null ? "(nowhere)" : $"({pos.Value.x},{pos.Value.y})";
                ApplyingRemote = true;
                try
                {
                    MovementAction act = default;
                    // Force | ForceTeleport | AllowMoveThroughActors | SkipPathfinding
                    bool ok = a.MoveTo(dest, ref act, (MovementFlags)(1 | 2 | 4 | 8));
                    Plugin.Logger.LogWarning(ok
                        ? $"[SYNC] Relayed move landed off-target: '{DesyncDetector.TemplateNameOf(a)}' {from} -- snapped to ({x},{y})"
                        : $"[SYNC] Relayed move landed off-target: '{DesyncDetector.TemplateNameOf(a)}' {from} -- snap to ({x},{y}) REFUSED");
                    // DO NOT ESCALATE AN OCCUPIED TILE INTO A RESYNC.
                    //
                    // A refused snap onto a tile another unit holds is the known
                    // large-creature footprint case, and it repeats for the same
                    // unit every check. Escalating it is what previously drove a
                    // run of "snap REFUSED" into a sync pause. Report it and
                    // leave it; resync is for corruption, not for a tile we knew
                    // was occupied before we tried.
                    if (!ok && blocker == null)
                        DesyncDetector.RequestResync($"relayed move for '{DesyncDetector.TemplateNameOf(a)}' could not be corrected");
                }
                finally { ApplyingRemote = false; }
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[SYNC] relayed-move check failed: {e.Message}"); }
        }
    }

    // AN ORDER CANNOT BE JUDGED AGAINST A BOARD THAT DOES NOT EXIST YET.
    //
    // Every "aidone never resolved" in five guest logs -- 53 of 53 -- fired
    // outside a battle. They were not leftovers: CId 382067006, declared a desync
    // in mission prep, was a pirate squad the guest then fought for the whole
    // battle. The host had launched first and its round-1 AI turns were already
    // arriving while the guest was still generating the map. With no board every
    // order is unresolvable and "the board is idle", so the 3s grace expired and
    // the host's opening AI turns were thrown away before the guest's copy of
    // those units existed.
    //
    // So: with no board, hold everything and age nothing; orders start ageing
    // when the board appears. When a battle has genuinely ENDED (gone for a
    // sustained moment -- TacticalManager blinks null during reloads, the same
    // guard Ownership uses), whatever is left belonged to it and is discarded,
    // along with that battle's host wave budgets, which were never cleared and
    // could otherwise stand in for the next battle's.
    private static float _boardSince = -1f;
    private static float _boardGoneSince = -1f;

    // The host opening planning for the NEXT mission is a hard boundary: no
    // order for that mission can exist yet, so anything still queued belongs to
    // the battle before. Needed because a late order can land AFTER the 3 s
    // battle-over discard above and then wait for the next board -- hand test
    // 2026-10-02 17:37:44: 13 mission-1 'aidone's (mission-1 ids and tiles)
    // replayed into mission 2 as "never resolved -- DESYNC". The next mission's
    // own early orders (sent while the guest generates the map) come after the
    // launch, so they are never caught by this.
    internal static void DiscardOrdersFromLastBattle()
    {
        if (TacticalManager.Get() != null) return;      // still in a battle: not a boundary
        if (_deferred.Count > 0)
            Plugin.Logger.LogInfo($"[SYNC] Host opened planning for the next mission; discarded {_deferred.Count} order(s) left over from the last battle");
        _deferred.Clear();
        _hostLeftFactions.Clear();
    }

    internal static void RetryDeferred()
    {
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (TacticalManager.Get() == null)
        {
            if (_boardSince >= 0f)
            {
                if (_boardGoneSince < 0f) _boardGoneSince = now;
                else if (now - _boardGoneSince > 3f)
                {
                    if (_deferred.Count > 0)
                        Plugin.Logger.LogInfo($"[SYNC] Battle over; discarded {_deferred.Count} unresolved order(s) that belonged to it");
                    _deferred.Clear();
                    _roundHeld.Clear();
                    ReinforcementSync.ResetStrength();
                    _hostLeftFactions.Clear();
                    _boardSince = -1f;
                    _boardGoneSince = -1f;
                }
            }
            return;
        }
        _boardGoneSince = -1f;
        if (_boardSince < 0f)
        {
            _boardSince = now;
            if (_deferred.Count > 0)
                Plugin.Logger.LogInfo($"[SYNC] Board ready; resolving {_deferred.Count} order(s) the partner sent before it existed");
        }

        if (_deferred.Count == 0) return;
        if (AnyActorMoving()) _boardBusyAt = now;   // a move is in flight -- keep waiting
        float idleFor = now - _boardBusyAt;
        _retrying = true;
        try
        {
            for (int i = 0; i < _deferred.Count; ) // oldest first, to preserve order
            {
                var m = _deferred[i].Item1;
                // Age from when it was received OR when the board appeared, whichever is later.
                float t = UnityEngine.Mathf.Max(_deferred[i].Item2, _boardSince);
                _deferResult = false;
                Apply(m);                          // re-dispatch; sets _deferResult if still unresolved
                if (!_deferResult)                 // resolved and applied
                {
                    if (_roundHeld.Remove(m))
                        Plugin.Logger.LogInfo($"[SYNC] Released held '{m.T}' after our round roll ({now - _deferred[i].Item2:F1}s)");
                    _deferred.RemoveAt(i);
                    continue;
                }
                // AN ORDER FOR A UNIT THAT HAS NOT ARRIVED YET IS NOT A DESYNC.
                //
                // Three seconds of an idle board was the whole grace, and a guest
                // waiting on a reinforcement it has not spawned yet blows through
                // that trivially -- the board IS idle, precisely because nothing
                // has arrived to move. Measured: the very first failure of the
                // run was CId=1760846283, an id BOTH machines had registered, on
                // an order that simply landed before the guest's copy existed.
                // Dropping it means the guest never applies that unit's action,
                // never registers its kills, and its enemy strength then stays
                // high while the host's falls -- which feeds a different budget
                // into the reinforcement draw and hands the two machines
                // genuinely different waves (host flamethrower team, guest
                // chaingun team). One dropped order cascaded into two armies.
                //
                // So while arrivals are still landing, hold identified orders far
                // longer. The hard failsafe still bounds it.
                float grace = IdleGrace;
                if (m.CId >= 0 && now - DesyncDetector.LastArrivalAt < ArrivalSettle)
                    grace = ArrivalGrace;
                if (_roundHeld.Contains(m)) grace = HardFailsafe;   // #96: waiting on our round roll
                if ((idleFor > grace && now - t > grace) || now - t > HardFailsafe)
                {
                    _roundHeld.Remove(m);
                    _deferred.RemoveAt(i);

                    // A DAMAGE PACKET THAT CANNOT FIND ITS TARGET IS NOT A DESYNC.
                    //
                    // "ehp"/"edie" describe a unit's health, and the usual reason
                    // one cannot be resolved is that the unit is already gone on
                    // this side -- a late packet for a corpse. Treating that as a
                    // divergence made it the single biggest source of resync
                    // requests (6 of 8 in one mission), and repeated requests
                    // escalated into a stop-the-world SYNC PAUSE. Health has its
                    // own authority (the shooter) and the snapshot backstops it,
                    // so drop the packet instead of stopping the game over it.
                    if (m.T == "ehp" || m.T == "edie")
                    {
                        Plugin.Logger.LogInfo($"[SYNC] Dropped a late {m.T} packet for ({m.AX},{m.AY}) -- target already gone here");
                        continue;
                    }

                    Plugin.Logger.LogError($"[SYNC] Order '{m.T}' at ({m.AX},{m.AY}) never resolved (board idle, RId={m.RId} CId={m.CId}) -- DESYNC");

                    // SURFACE THIS. A unit the partner can command and this
                    // machine cannot even identify is precisely the thing a
                    // player wants to know about, and it spent whole battles
                    // visible only in the log -- 266 of them in one run, while
                    // the screen showed nothing but an occasional freeze. Counted
                    // and throttled rather than one banner per order, because the
                    // failure arrives in floods.
                    _unresolvedSinceReport++;
                    float unow = UnityEngine.Time.realtimeSinceStartup;
                    // NOT BEFORE WE ARE EVEN IN THE BATTLE. Reported as a banner
                    // saying "1 order(s)..." while the guest was still loading in
                    // -- of course orders cannot resolve against a board that does
                    // not exist yet, and telling a player about it mid-load is
                    // pure alarm with nothing to act on.
                    bool inBattle = TacticalManager.Get() != null;
                    if (inBattle && unow - _lastUnresolvedReportAt > 20f)
                    {
                        _lastUnresolvedReportAt = unow;
                        CoopRunner.ShowDesyncWarning(
                            $"{_unresolvedSinceReport} order(s) from your partner named units this machine cannot find "
                            + $"(latest: '{m.T}' near {m.AX},{m.AY}). Positions are being corrected automatically.");
                        _unresolvedSinceReport = 0;
                    }
                    // A dropped order means the boards have already diverged
                    // (the sender executed it, we couldn't). Trigger an
                    // authoritative resync NOW instead of leaving the drift to
                    // cascade until the next faction switch.
                    DesyncDetector.RequestResync($"order '{m.T}' at ({m.AX},{m.AY}) unresolvable");
                    continue;
                }
                i++;
            }
        }
        finally { _retrying = false; }
    }

    internal static void Apply(CoopMsg m)
    {
        // Guest reload / rejoin: its own messages, the host's journal, and the
        // live traffic a half-rebuilt board must not see.
        if (GuestReload.Intercept(m)) return;
        FogSync.MarkDirty(m.T);
        // Timestamp partner-order activity: DesyncDetector holds snapshots off
        // while a partner order's effects may still be settling.
        switch (m.T)
        {
            case "skill": case "move": case "moveskill": case "skip":
            case "done": case "ehp": case "edie":
                LastRemoteOrderAppliedAt = UnityEngine.Time.realtimeSinceStartup;
                // The partner is actively playing, so the turn-hold failsafe must
                // not decide they have stalled and take the turn off them.
                TurnControl.NoteHolderActivity();
                break;
        }
        if (m.T == "move" || m.T == "moveskill" || m.T == "land")
            LastRemoteMoveAt = UnityEngine.Time.realtimeSinceStartup;
        switch (m.T)
        {
            case "hello":
            {
                Plugin.Logger.LogInfo($"[NET] Partner hello: mod {m.Ver}, game {m.GVer ?? "?"}, name '{m.Name ?? "?"}'");
                if (CoopNet.Mode == CoopMode.Host)
                {
                    int seat = Peers.Admit(m);
                    if (seat > 0) CoopNet.SendTo(new CoopMsg { T = "seat", Prm = seat }, seat);
                    if (seat > 0) Peers.BroadcastRoster();
                }
                else
                    Peers.NoteHost(m);
                bool modMismatch = m.Ver != Plugin.PluginVersion;
                bool gameMismatch = !string.IsNullOrEmpty(m.GVer) && m.GVer != Plugin.GameFingerprint;
                if (gameMismatch)
                {
                    Plugin.Logger.LogError($"[NET] GAME VERSION MISMATCH: partner build {m.GVer}, you {Plugin.GameFingerprint}");
                    CoopNet.Status = "GAME VERSION MISMATCH -- both must be on the same game build!";
                    CoopRunner.ShowVersionWarning("DIFFERENT GAME VERSIONS -- update both to the same build; co-op will not work reliably", sticky: true);
                }
                else if (modMismatch)
                {
                    Plugin.Logger.LogWarning($"[NET] Mod version mismatch: partner {m.Ver}, you {Plugin.PluginVersion}");
                    CoopNet.Status = $"MOD VERSION MISMATCH! partner {m.Ver}, you {Plugin.PluginVersion} -- update the mod";
                    CoopRunner.ShowVersionWarning($"DIFFERENT MOD VERSIONS (partner {m.Ver}, you {Plugin.PluginVersion}) -- use the same MenaceCoop build", sticky: true);
                }

                // ---- CONTENT MODS -------------------------------------------
                //
                // The two checks above catch different BUILDS. This catches the
                // case that actually bites: same game, same MenaceCoop,
                // different content mods. Those add and patch unit templates, so
                // the two machines generate different armies from the same seed
                // -- and nothing else notices, because the board hash covers
                // position/HP/AP and an enemy that exists for only one player
                // hashes as identical. See ModList.cs.
                if (m.Mods == null)
                {
                    // Older build on the other side: say so rather than let a
                    // silent null read as agreement.
                    Plugin.Logger.LogWarning("[NET] Partner's build does not report its mod list -- cannot compare content mods.");
                }
                else
                {
                    string diff = ModList.Diff(m.Mods);
                    if (diff == null)
                    {
                        Plugin.Logger.LogInfo($"[NET] Mod lists MATCH ({ModList.Count} content mod(s)/melon(s) on both machines)");
                    }
                    else
                    {
                        Plugin.Logger.LogError($"[NET] ***** MOD LISTS DIFFER ***** {diff}");
                        Plugin.Logger.LogError("[NET] Content mods add and patch unit templates, so the two machines can");
                        Plugin.Logger.LogError("[NET] generate DIFFERENT ARMIES from the same mission. Install the same set on both.");
                        // Only claim the banner if a version mismatch has not
                        // already taken it -- that one is the more urgent of the
                        // two and must not be overwritten.
                        if (!gameMismatch && !modMismatch)
                        {
                            CoopNet.Status = "MOD LISTS DIFFER -- you and your partner have different content mods";
                            // Name the mods on screen (user 2026-10-02: "let the users
                            // know") -- "see the log" is not something a player acts on.
                            string shown = diff.Length > 360 ? diff.Substring(0, 360) + "... (full list in the log)" : diff;
                            CoopRunner.ShowVersionWarning(
                                "DIFFERENT MODS -- you may fight different armies. " + shown, sticky: true);
                        }
                    }
                }

                SessionSync.OnPartnerJoined();
                break;
            }

            case "seat":
                Peers.HandleSeat(m);
                break;

            case "players":
                Peers.HandleRoster(m);
                break;

            case "pname":
                Peers.HandleName(m);
                break;

            case "loadreq":
                SessionSync.HandleLoadRequest(m);
                break;

            case "cursor":
                CoopRunner.OnRemoteCursor(m.From, m.CX, m.CY);
                break;

            case "tcursor":
                CoopRunner.OnRemoteTileCursor(m.From, m.AX, m.AY, m.TX, m.TY, m.Prm);
                break;

            case "roster":
                BattleRoster.OnRemote(m.Data);
                break;

            case "terrain":
                TerrainCheck.OnRemote(m.Data);
                break;

            case "hash":
                DesyncDetector.HandleRemote(m);
                break;

            case "hashok":
                DesyncDetector.HandleHashOk(m);
                break;

            case "ctrl":
                TurnControl.HandleRemoteControl(m);
                break;

            case "hphase":
                if (CoopNet.Mode == CoopMode.Guest) DesyncDetector.NoteHostPhase(m.Prm == 1);
                break;

            case "statesync":
                // Queued, not applied inline: snapshots must wait for the board
                // to be idle (see DesyncDetector.Tick).
                DesyncDetector.QueueSnapshot(m.Data);
                break;

            case "reqsync":
                // Guest hit an unresolvable order/packet and asked for help --
                // answer with an authoritative snapshot right away.
                if (CoopNet.Mode == CoopMode.Host)
                {
                    var tm = TacticalManager.Get();
                    if (tm != null) DesyncDetector.SendSnapshot(tm, SenderSeat(m));
                }
                break;

            case "reqhard":
                // Guest asked for a HARD resync (button or escalation).
                if (CoopNet.Mode == CoopMode.Host)
                {
                    var tm = TacticalManager.Get();
                    if (tm != null) DesyncDetector.SendHardSnapshot(tm, SenderSeat(m));
                }
                break;

            case "hardsync":
                DesyncDetector.QueueHardSnapshot(m.Data);
                break;

            case "svar":
                StrategyMirror.HandleVar(m);
                break;

            case "sitem":
                StrategyMirror.HandleItem(m);
                break;

            case "sopen":
                StrategyMirror.HandleScreen(m, open: true);
                break;

            case "convopen":     // host opened an event message / conversation
            case "convnext":     // host drives strategy conversations (#48)
            case "convchoice":
            case "convrole":
            case "convend":
                ConversationSync.HandleRemote(m);
                break;

            case "sclose":
                StrategyMirror.HandleScreen(m, open: false);
                break;

            case "scart":
                StrategyMirror.HandleCart(m);
                break;

            case "sship":
                StrategyMirror.HandleShipUpgrade(m);
                break;

            case "spause":
                // Partner declared a sync pause -- freeze this side too.
                DesyncDetector.StartSyncPause(m.Data ?? "partner request", notifyPartner: false);
                break;

            case "rjhold": GuestReload.OtherRejoining(m); break;
            case "rjdone": GuestReload.OtherRejoined(m); break;

            case "spdone":
                // Guest confirmed the forced state landed -- resume.
                DesyncDetector.EndSyncPause("partner confirmed resync");
                break;

            case "aidone":
            {
                // Host's AI unit finished its activation -- mirror the done
                // flag so our engine's faction cycle consumes activations at
                // the host's cadence (round sync depends on this).
                var actor = ResolveOrderActor(m);
                if (actor == null) { UnresolvedOrder(m); return; }
                if (!actor.IsTurnDone())
                {
                    ApplyingRemote = true;
                    try { actor.SetTurnDone(true); actor.MarkAsHasActed(); }
                    finally { ApplyingRemote = false; }
                }
                break;
            }

            case "reinfs":
                // Host's reinforcement budget for a given round/group, so the guest
                // draws its wave from the same number instead of its own lagging board.
                ReinforcementSync.HandleRemoteStrength(m);
                break;

            case "obj":
                // Host is authoritative for mission outcome; mirror its objective states.
                ObjectiveSync.HandleRemote(m);
                break;

            case "perk":
                // Host promoted a squaddie; mirror it onto the guest roster.
                PerkSync.HandleRemote(m);
                break;

            case "setfac": FactionSync.HandleRemote(m); break;
            case "esup": SuppressionSync.HandleRemote(m); break;
            case "hashrows": DesyncDetector.HandleHashRows(m); break;
            case "dbg": DebugSync.HandleCommand(m); break;
            case "estance": SuppressionSync.HandleStance(m); break;
            case "efacing": FacingSync.Handle(m); break;
            case "efx": EffectSync.HandleRemote(m); break;
            case "sdie": StructureSync.HandleRemote(m); break;
            case "sdiereq": StructureSync.HandleDieRequest(m); break;
            case "flee": FleeSync.HandleRemote(m); break;
            case "adie": GuestReload.ApplyActorDeath(m); break;   // journal-only (#66)
            case "leave": GuestReload.ApplyLeave(m); break;       // journal-only (#57/#67)
            case "mstate": FleeSync.HandleState(m); break;
            case "objprog": ObjectiveProgressSync.HandleRemote(m); break;
            case "dbgreq": DebugSync.HandleRequest(m); break;
            case "ociuse":
                // Partner called in ship support; charge the SAME pool here.
                // Either side may send this -- one budget, two players.
                OffmapSync.HandleRemote(m);
                break;

            case "ociexec": OffmapStrikeSync.HandleRemote(m); break;   // the strike itself (#3b)
            case "ocitiles": OffmapStrikeSync.HandleTiles(m); break;  // strafing runs etc. (#3b)
            case "ociclick": OffmapStrikeSync.HandleClick(m); break;  // crates, turrets, troops, cover (#35)
            case "ocitick": DelayedOffmapSync.HandleRemote(m); break;  // host drives delayed abilities (#35)
            case "ocisched": DelayedOffmapSync.HandleSched(m); break;  // host's schedule-time threshold (#35)
            case "ociupd": DelayedOffmapSync.HandleUpdate(m); break;  // host's standalone UpdateAbilities (#35)
            case "aioci": EnemyOffmapSync.HandleRemote(m); break;     // enemy off-map ability scheduled (v0.7.15)

            case "eseen":
                // Partner's view of where the enemies are, for phantom marking.
                PhantomWatch.HandleRemote(m);
                break;

            case "unload":
            {
                // Host's transport dropped its passengers. The guest runs no AI,
                // so without this relay its copy never unloads at all.
                ContainerSync.ApplyUnload(m, ResolveOrderActor(m));
                break;
            }

            case "alock":
            {
                // Someone grabbed the action lock. If we ALSO hold it (both
                // started within the latency window), the LOWER seat wins the tie
                // -- the host, at two players -- and the other yields so we don't
                // deadlock.
                int from = SenderSeat(m);
                if (LockHeldByMe && Peers.LocalSeat < from) break; // keep ours; ignore theirs
                if (LockHeldByMe)
                {
                    LockHeldByMe = false;
                    CoopNet.Send(new CoopMsg { T = "aunlock" });
                }
                _lockHolders.Add(from);
                break;
            }

            case "aunlock":
                _lockHolders.Remove(SenderSeat(m));
                break;

            case "ready":
                Plugin.Logger.LogInfo("[SESSION] Partner is in the battle");
                if (CoopNet.Mode == CoopMode.Host)
                {
                    // v5: hold fire until EVERY connected client is in.
                    Peers.SetReady(SenderSeat(m));
                    SessionSync.HostHoldFire = !Peers.AllClientsReady;
                    if (SessionSync.HostHoldFire)
                        Plugin.Logger.LogInfo($"[SESSION] Player {SenderSeat(m) + 1} is in; still waiting for the others");
                }
                else SessionSync.HostHoldFire = false;
                // harness may now act -- on the host, once all clients are in
                // Never ASSIGN false here: clearing the flag clears every seat's
                // ready record, so each report would wipe the one before (r93).
                if (CoopNet.Mode != CoopMode.Host || Peers.AllClientsReady)
                    SessionSync.PartnerBattleConfirmed = true;

                // THE HOST MUST ANSWER, NOT ONLY ANNOUNCE.
                //
                // The host's own announcement goes out at ITS battle start, which
                // is before the guest has even loaded. The guest recorded it and
                // then cleared the flag when its own battle began -- wiping the
                // confirmation it already held, so it waited out the full 90s
                // failsafe every time ("Partner never reported into the battle
                // within 90s"). Replying here delivers the confirmation AFTER the
                // guest has entered, which is the only point it can survive.
                //
                // Only the host replies, so this cannot ping-pong.
                if (CoopNet.Mode == CoopMode.Host)
                {
                    CoopNet.SendTo(new CoopMsg { T = "ready" }, SenderSeat(m));
                    GuestReload.OnPartnerReady(SenderSeat(m));   // a reloaded guest is back in: resume
                    // The roster and terrain manifests are NOT published here.
                    // This handshake fires once per SESSION, so publishing from
                    // it checked the first battle and no later one. Both classes
                    // now publish per battle off their own tick -- see
                    // BattleRoster.PublishPerBattle.
                }
                // short single line -- the appended form overflowed the panel
                CoopNet.Status = Ownership.Active
                    ? $"Weapons free! You command {Ownership.LocalCount} of {Ownership.TotalCount} squads"
                    : "Partner is in the battle -- weapons free!";
                CoopRunner.SetSyncBlocker(false);
                break;

            case "passreq":
                TurnControl.HandlePassRequest(m);
                break;

            case "prepready":
                Plugin.Logger.LogInfo("[PREP] Partner's planning view is open");
                CoopNet.Status = "Partner is viewing your planning";
                CoopRunner.SetSyncBlocker(false);
                break;

            case "assign":
                CoopRunner.OnRemoteAssignment(m);
                break;

            case "iplace":
                EquipmentSync.HandlePlace(m);
                break;

            case "irem":
                EquipmentSync.HandleRemove(m);
                break;

            case "ireq":
                EquipmentSync.HandleRequest(m);
                break;

            case "irej":
                EquipmentSync.HandleRefusal(m);
                break;

            case "startmission":
                SessionSync.HandleStartMission(m);
                break;

            case "own":
                Ownership.HandleOwn(m);
                break;

            // A dropped player's units go to the host until they rejoin.
            case "olend": if (CoopNet.Mode == CoopMode.Guest && m.Prm != Peers.LocalSeat) Ownership.LendToHost(m.Prm); break;
            case "oreturn": if (CoopNet.Mode == CoopMode.Guest) Ownership.ReturnLent(m.Prm); break;

            case "prepopen":
                DiscardOrdersFromLastBattle();
                SessionSync.HandlePrepOpen(m);
                break;

            case "prepclose":
                SessionSync.HandlePrepClose(m);
                break;

            case "dpos":
                SessionSync.HandleDpos(m);
                break;

            case "dposreq":
                DeploySync.HandleRequest(m);
                break;

            case "dposrej":
                DeploySync.HandleRefusal(m);
                break;

            case "dadd":
                SessionSync.HandlePlanUnitChange(m, true);
                break;

            case "drem":
                SessionSync.HandlePlanUnitChange(m, false);
                break;

            case "sqadd":
                SessionSync.HandleSquaddieChange(m, true);
                break;

            case "sqrem":
                SessionSync.HandleSquaddieChange(m, false);
                break;

            case "trust":
                SessionSync.HandleTrust(m);
                break;

            case "synced":
                CoopRunner.SetSyncBlocker(false);
                SessionSync.ClearSyncInFlight();
                CoopNet.Status = "Partner synced";
                Plugin.Logger.LogInfo("[SESSION] Partner confirmed sync complete");
                break;

            case "seed":
                Reseed(m.Seed);
                Plugin.Logger.LogInfo($"[SYNC] Applied RNG seed {m.Seed}");
                break;

            case "aiadvance":
                DesyncDetector.NoteHostRound(m.Prm);
                OnRemoteFactionAdvance(m.Prm, m.MoveFlags, m.Seed);
                break;

            case "ehp":
            {
                // The DAMAGE DELTA dealt by the sender (m.Seed), subtracted from
                // our current HP. Deltas (not absolute HP) are essential: when
                // BOTH players damage the same element, absolute values conflict
                // (each machine would keep only the last one it applied and they
                // diverge); deltas accumulate correctly regardless of order.
                var actor = ResolveOrderActor(m);
                // NOTE: Entity.GetElement THROWS on an out-of-range index (it
                // does not return null) -- resolve via the list instead, and by
                // stable squaddie id rather than position (see CoopMsg.SqId).
                var elList = actor?.GetElements();
                var el = ReplicationPatches.ResolveElement(actor, m);
                if (el == null)
                {
                    // TWO VERY DIFFERENT CASES, and treating them alike was
                    // triggering stop-the-world pauses over nothing.
                    //
                    // Actor found but the element index is wrong -> a genuine
                    // structural mismatch, worth a resync (this is the case that
                    // caused the machine-gun cascade).
                    //
                    // Actor NOT found -> the unit is simply gone on this side, so
                    // the packet is a late report about a corpse. Its health is
                    // already settled; asking for a resync (and eventually a sync
                    // pause) over it is pure noise.
                    if (actor == null)
                    {
                        Plugin.Logger.LogInfo($"[SYNC] Dropped a late damage packet for ({m.AX},{m.AY}) -- that unit is already gone here");
                        break;
                    }

                    // THE SQUADDIE IS ALREADY DEAD HERE -- THAT IS AGREEMENT.
                    //
                    // This used to be read as the rosters having diverged, and it
                    // is not. BOTH machines logged the identical "actor=found,7
                    // els" for the same squaddies at the same tile: neither had 9,
                    // so nothing had drifted apart. The packets are simply about
                    // members who are already dead on both sides.
                    //
                    // The confusion came from element INDEX vs living list:
                    // GetElementIndex() is the permanent squad slot (0..8), while
                    // GetElements() holds only the LIVING, so "el8" was never a
                    // valid index into a 7-entry list on either machine.
                    //
                    // Dead is dead; more damage changes nothing. Drop it quietly
                    // rather than escalating -- three earlier fixes chased a
                    // divergence that this evidence says was never happening.
                    Plugin.Logger.LogInfo(
                        $"[SYNC] Dropped a damage packet for sq{m.SqId} at ({m.AX},{m.AY}) -- that squaddie is already dead here");
                    break;
                }
                ApplyingRemote = true;
                try
                {
                    int cur = el.GetHitpoints();
                    int nw = cur - m.Seed;
                    if (nw < 0) nw = 0;
                    el.SetHitpoints(nw);
                    if (nw == 0 && cur > 0) KillElementLikeEngine(el, m, m.Seed); // combined damage killed it
                    // THE UNIT'S TOTAL IS NOT A LIVE SUM. The engine recomputes
                    // Entity HP in its own damage path; a bare element write
                    // leaves the total -- the health bar, and the state hash --
                    // stale. Since hits are owned by the shooter, this relay is
                    // the ONLY damage the receiving machine applies, so without
                    // this the partner's hits never moved the unit's health bar.
                    try { actor.UpdateHitpoints(); } catch { }
                    if (nw == 0) MarkDeadIfNoLivingElements(actor);
                    Plugin.Logger.LogInfo($"[SYNC] Applied element damage {m.Seed}: actor@({m.AX},{m.AY}) el{m.Prm} {cur} -> {nw}");
                }
                finally { ApplyingRemote = false; }
                break;
            }

            case "edie":
            {
                var actor = ResolveOrderActor(m);
                var elList = actor?.GetElements();
                var el = ReplicationPatches.ResolveElement(actor, m);
                if (el == null)
                {
                    Plugin.Logger.LogWarning($"[SYNC] edie: cannot resolve el{m.Prm} (sq{m.SqId}) at ({m.AX},{m.AY}) -- actor={(actor == null ? "NULL" : "found," + (elList?.Count ?? -1) + " els")} RId={m.RId} CId={m.CId}");
                    // A death for a squaddie we do not have means it already died
                    // here -- the outcome AGREES, so there is nothing to repair
                    // and a resync would be pure churn. Only escalate when the
                    // actor is present and the rosters genuinely differ.
                    if (actor == null)
                        Plugin.Logger.LogInfo("[SYNC] Dropped a late death packet -- that unit is already gone here");
                    else
                        DesyncDetector.RepairStructuralMismatch($"squad mismatch at ({m.AX},{m.AY}): sq{m.SqId} is not on this side");
                    break;
                }
                // HITPOINTS ARE THE WRONG QUESTION. This engine's own rule is
                // that hit points do not kill anything -- an element driven to 0
                // stays at zero AND counted alive until Die() runs. So
                // "GetHitpoints() > 0" does not mean "still alive to kill", and
                // once deaths began actually arriving (the edie sender was dead
                // code until v3.14.0) that gap started showing:
                //
                //   guest DEATH pirate_outcasts cid=382067008 sq0 ... x3
                //
                // The same squaddie dying three times, and the roster drifting to
                // "host 4, you 3" -- the guest applying the host's death and then
                // its own simulation killing the same element again. Over-killing
                // is worse than the missed death it was meant to fix, because it
                // removes units the host still has.
                //
                // Ask the engine whether the element is alive, and skip anything
                // already on its way out.
                bool alive = false;
                try { alive = el.IsAlive(); } catch { alive = el.GetHitpoints() > 0; }
                bool actorDying = false;
                try { actorDying = actor != null && actor.IsDying(); } catch { }
                if (alive && !actorDying)
                {
                    KillElementLikeEngine(el, m, 0);
                    try { actor.UpdateHitpoints(); } catch { }   // keep the unit total (health bar) live
                    MarkDeadIfNoLivingElements(actor);
                    Plugin.Logger.LogInfo($"[SYNC] Applied element death: actor@({m.AX},{m.AY}) el{m.Prm}");
                }
                break;
            }

            case "fmeta":
                SaveTransfer.HandleMeta(m);
                break;

            case "fchunk":
                SaveTransfer.HandleChunk(m);
                break;

            case "fdone":
                SaveTransfer.HandleDone(m);
                break;

            case "skill":
            {
                var actor = ResolveOrderActor(m);
                var skill = actor?.GetSkills()?.GetSkillByID(m.SkillId)?.TryCast<Skill>();
                // #4b: A MINION COMMAND NAMES THE MINION. The wardog's strike
                // (at_wardog_command_suicide) was relayed with the DRONE as its actor
                // -- the engine reports the minion as the command's user -- but the
                // skill lives on the squad that deployed it, so the host never ran the
                // strike and nobody applied its damage (hand test 2026-10-03 21:58).
                // Use the unit whose command is bound to that minion.
                if (actor != null && skill == null)
                {
                    var owner = MinionCommandOwner(actor, m.SkillId);
                    if (owner != null)
                    {
                        Plugin.Logger.LogInfo($"[SYNC] '{m.SkillId}' is a command for '{DesyncDetector.TemplateNameOf(actor)}' -- replaying it on its commander '{DesyncDetector.TemplateNameOf(owner)}'");
                        actor = owner;
                        skill = owner.GetSkills()?.GetSkillByID(m.SkillId)?.TryCast<Skill>();
                    }
                }
                var target = GetTile(m.TX, m.TY);
                if (actor == null || skill == null || target == null)
                {
                    UnresolvedOrder(m); // unit may still be mid-move -- retry shortly
                    return;
                }
                if (HoldForHostRound(m, actor)) return;   // #96
                // SAVE AND RESTORE, NEVER BLANKET-CLEAR.
                //
                // These were set true and reset to FALSE in the finally. If a
                // replay ever nests -- one handled message applying while another
                // is still in flight -- the inner finally cleared suppression
                // while the OUTER replay was still running, and from that point
                // the receiver applied its own damage rolls on top of the
                // authoritative ehp. That is a standing candidate for the squad
                // divergence that keeps coming back ("cannot resolve el8 (sq7) --
                // actor=found,7 els": the receiver killed members the sender still
                // had). Restoring the previous value makes nesting safe.
                GuestReload.NoteSkill(m, actor);   // host: a summon it causes is journaled (#62)
                bool prevApply = ApplyingRemote, prevSuppress = SuppressElementDamage;
                // AP BEFORE, SO "IT REPLAYED" CAN BE TOLD FROM "IT RAN".
                //
                // The log has only ever printed the AP left AFTER a replay, which
                // looks the same whether the engine executed the skill or
                // declined it outright. The rider divergence lived in that gap:
                // the guest logged every eject as applied while the leviathan's
                // AP never moved (120 -> 120, against 120 -> 100 on the host), so
                // the skill plainly never ran. A replay that costs nothing is the
                // signature of one that did nothing.
                int apBefore = -1;
                try { apBefore = actor.GetActionPoints(); } catch { }
                // START FROM THE SHOOTER'S AP (run 27: 9 of 38 relayed skills
                // REFUSED on the guest). The guest never runs the AI's turn start,
                // which refills AP, so an AI unit sat at 30 here against 100 on
                // the host and could not afford a 40 AP shot: the attack never
                // played (damage still came as "ehp"), and the AP never matched.
                if (m.Ap >= 0 && apBefore != m.Ap && ApValueIsSafe(actor, m.Ap))
                {
                    bool prevRemote = ApplyingRemote;
                    ApplyingRemote = true;
                    try { actor.SetActionPoints(m.Ap, true); apBefore = m.Ap; }
                    catch { }
                    finally { ApplyingRemote = prevRemote; }
                }
                // ...AND FROM THE SHOOTER'S USES. Limited-use skills (a pirate
                // chaingun salvo, grenades) count down per use and nothing relays
                // the count; r42's 4th salvo ran on the host and was REFUSED here
                // (IsUsable=False) with AP in hand. Same cure as AP.
                if (m.SkUses >= 0)
                {
                    try
                    {
                        var sk = actor.GetSkills()?.GetSkillByID(m.SkillId)?.TryCast<Skill>();
                        if (sk != null && sk.HasLimitedUses() && sk.GetUses() != m.SkUses)
                        {
                            Plugin.Logger.LogInfo($"[SYNC] '{m.SkillId}' uses aligned to the shooter's: {sk.GetUses()} -> {m.SkUses}");
                            sk.SetUses(m.SkUses);
                        }
                    }
                    catch { }
                }
                ArmDamageTailAt(m.TX, m.TY);  // late splash lands around the target
                ApplyingRemote = true;
                SuppressElementDamage = true; // replay for visuals/AP only; HP comes from "ehp"
                try
                {
                    Reseed(m.Seed);
                    // SAME CONTAINMENT AS THE "move" CASE BELOW, FOR THE SAME
                    // REASON. Skill.ApplyToTile raises a NullReferenceException
                    // when the thing it resolves against is gone -- seen 9 times
                    // in one battle on the HOST, replaying the guest's skills:
                    //   at Menace.Tactical.Skills.Skill.ApplyToTile
                    //   at Menace.Tactical.Skills.Skill.Use
                    // Unguarded, that escaped to the message loop, which abandons
                    // the ENTIRE message -- so anything batched behind the skill
                    // was dropped silently too. One lost skill replay is a far
                    // smaller thing than a lost message, and the damage itself
                    // arrives separately as "ehp"/"edie" regardless.
                    try
                    {
                        // Skill.Use RETURNS A BOOL AND IT WAS BEING THROWN AWAY --
                        // the same mistake Law 8 records for MoveTo, in the other
                        // half of the replay path. "Applied remote skill" was
                        // logged just as cheerfully for a skill the engine
                        // refused as for one it ran. Ask the engine why when it
                        // says no; IsUsableOn is its own answer, not our guess.
                        // REPLAY MUST IGNORE THE USABILITY CHECK.
                        //
                        // A relayed skill is judged against THIS machine's turn
                        // state, which is not the sender's. Measured at a
                        // leviathan eject: the host had the leviathan active in
                        // the enemy faction's activation (fac=7,
                        // active=enemy.alien_leviathan_bombardier) while the
                        // guest was still on the player's faction with a player
                        // vehicle active (fac=1, player=1). The engine therefore
                        // answered IsUsable=False and Use() did nothing --
                        // AP 120 -> 120 against the host's 120 -> 100 -- and the
                        // eight riders stayed inside the transport all battle.
                        //
                        // UsageParameter.IgnoreUsabilityCheck is the engine's own
                        // flag for this. The sender has ALREADY performed the
                        // action, so "may this be used right now" is a question
                        // about the wrong machine; the receiver's job is to
                        // reproduce it, not to re-authorise it.
                        //
                        // NOT UsageParameter.Free -- that would skip the AP cost
                        // and the two machines' AP would drift apart.
                        var prm = (UsageParameter)m.Prm | UsageParameter.IgnoreUsabilityCheck;
                        bool used = skill.Use(target, prm);
                        // Use() can return false for a skill it DID run: a
                        // pirate chaingun's last salvo of the turn (r42, r46) came
                        // back false with its 20 AP charged. The AP spent is the
                        // engine's own receipt -- only a replay that cost nothing
                        // was refused (#34).
                        if (!used)
                        {
                            int apNow = -1;
                            try { apNow = actor.GetActionPoints(); } catch { }
                            if (apBefore >= 0 && apNow >= 0 && apNow < apBefore) used = true;
                        }
                        if (!used)
                        {
                            bool usable = false, usableOn = false;
                            try { usable = skill.IsUsable(); } catch { }
                            try { usableOn = skill.IsUsableOn(target); } catch { }
                            Plugin.Logger.LogWarning(
                                $"[SYNC] Relayed skill {m.SkillId} at ({m.TX},{m.TY}) was REFUSED by the engine "
                                + $"(IsUsable={usable} IsUsableOn={usableOn}) -- it did not run here. "
                                + "Anything it would have done on the partner's machine has not happened on this one.");
                        }
                    }
                    catch (Exception skillEx)
                    {
                        // #93: the text, not just the type -- the first relayed
                        // vehicle_rotation of a turn throws on every client and the
                        // second one runs; the type alone never said why.
                        string why = (skillEx.Message ?? "").Replace("\r", " ").Replace("\n", " | ");
                        if (why.Length > 400) why = why.Substring(0, 400);
                        Plugin.Logger.LogWarning(
                            $"[SYNC] Relayed skill {m.SkillId} at ({m.TX},{m.TY}) threw inside the engine " +
                            $"({skillEx.GetType().Name}: {why}) -- visual replay skipped, damage still arrives as ehp/edie.");
                    }
                }
                finally { SuppressElementDamage = prevSuppress; ApplyingRemote = prevApply; }
                Plugin.Logger.LogInfo($"[SYNC] Applied remote skill {m.SkillId} at ({m.TX},{m.TY}), actor AP {apBefore} -> {actor.GetActionPoints()}");
                break;
            }

            case "move":
            {
                var actor = ResolveOrderActor(m);
                var dest = GetTile(m.TX, m.TY);
                if (actor == null || dest == null)
                {
                    UnresolvedOrder(m); // unit may still be mid-move -- retry shortly
                    return;
                }
                if (HoldForHostRound(m, actor)) return;   // #96
                var action = (MovementAction)m.MoveAction;
                bool prevApplyMv = ApplyingRemote, prevSuppressMv = SuppressElementDamage;
                ApplyingRemote = true;
                // A MOVE DEALS DAMAGE TOO -- OVERWATCH AND REACTION FIRE.
                //
                // The skill and move-with-skill replays suppressed element damage,
                // but this plain-move case did not, so a relayed walk past an
                // enemy rolled its OWN reaction fire on this machine. Those rolls
                // are per-machine, so the receiver killed squad members the
                // originator still had -- "cannot resolve el6 (sq7) -- actor=found,
                // 5 els" -- and squads diverged permanently, since a hard restore
                // cannot revive a dead squad MEMBER.
                //
                // Reaction damage belongs to the machine that rolled it and
                // arrives as "ehp"/"edie" like any other shot.
                SuppressElementDamage = true;
                try
                {
                    Reseed(m.Seed);

                    // A RELAYED "LEAVE" FOR A PASSENGER THAT IS ALREADY OUT.
                    //
                    // On the sender this move runs INSIDE the eject skill, and the
                    // skill is relayed too. When the replayed skill has already
                    // unloaded the passenger here, applying the Leave again is a
                    // second, different action: seen 2026-09-25 on three pirate
                    // truck unloads, each followed by the guest's roster keeping a
                    // truck the host no longer had. The refused-Leave fallback
                    // below still covers the other order (move before skill).
                    if ((((int)action & (int)MovementAction.Leave) != 0) && !ContainerSync.IsContained(actor))
                    {
                        var at = actor.GetLastTile()?.GetTilePos();
                        // The TILE is the truth here: right after an eject the
                        // passenger's GetLastTile() still reads the truck's tile
                        // while the target tile already holds it (both machines,
                        // mid-animation). Only a tile holding someone ELSE is a miss.
                        bool placed = (at != null && at.Value.x == m.TX && at.Value.y == m.TY)
                                   || ActorOn(dest)?.Pointer == actor.Pointer;
                        // ...and make the unit AGREE with its tile. On the host the
                        // Leave walk ends by updating m_LastTile; the replayed eject
                        // never does, so the guest's crew kept reporting the truck's
                        // tile (one state-hash mismatch per unload, run 10). SetTile
                        // is the engine's own write, with the host's value.
                        if (placed && (at == null || at.Value.x != m.TX || at.Value.y != m.TY))
                        {
                            try { actor.SetTile(dest); } catch { }
                            // Run 11: SetTile did not move m_LastTile (the value
                            // GetLastTile and the state hash read). The host's walk
                            // ends by writing it; write the same value here.
                            string how = "SetTile";
                            var now = actor.GetLastTile()?.GetTilePos();
                            if (now == null || now.Value.x != m.TX || now.Value.y != m.TY)
                            {
                                try { actor.m_LastTile = dest; how = "m_LastTile"; } catch { how = "neither"; }
                            }
                            Plugin.Logger.LogInfo($"[CONTAINER]   last tile set to ({m.TX},{m.TY}) via {how}");
                        }
                        // The host's Leave walk also ends the passenger's activation
                        // ("after MoveTo ... turnDone=True" on the host); the replay
                        // never does, and the corrector then fixed it every unload
                        // (TURN-DONE drift x3, runs 10-12). Same engine value here.
                        if (placed)
                        {
                            try { if (!actor.IsTurnDone()) actor.SetTurnDone(true); } catch { }
                        }
                        // ...BUT PUT IT WHERE THE HOST DID. The replayed eject leaves
                        // the crew on the TRUCK's tile here while the host's steps
                        // off onto the target tile (probe 2026-09-25: outcasts
                        // guest (34,30), host (34,31)). A walking Leave is refused
                        // once out, so place it the way the interrupted-move
                        // matcher does: teleport, only onto a free tile.
                        if (!placed && TileFree(dest))
                        {
                            MovementAction tp = default;
                            placed = actor.MoveTo(dest, ref tp, (MovementFlags)(1 | 2 | 4 | 8));
                        }
                        Plugin.Logger.LogInfo($"[CONTAINER] relayed Leave for '{DesyncDetector.TemplateNameOf(actor)}' already out "
                            + $"(at {(at == null ? "nowhere" : $"({at.Value.x},{at.Value.y})")}, host sent ({m.TX},{m.TY})) -- "
                            + (placed ? "placed on the host's tile" : "could not place, corrector will"));
                        break;
                    }

                    ApplyContainer(actor, m, dest);   // building entry, if any

                    // THE ENGINE'S PATHFINDER CAN THROW, AND IT TOOK THE WHOLE
                    // MESSAGE WITH IT.
                    //
                    // PathfindingProcess.FindPath dereferences the START tile
                    // without checking it, so Actor.MoveTo raises a
                    // NullReferenceException for an actor that is no longer
                    // standing anywhere -- one that has left the board or been
                    // removed. Seen once when the host relayed a SECOND move for
                    // a unit that had already finished (AP=0, turnDone=True):
                    //   Failed to apply remote message 'move'
                    //     at Menace.Tactical.PathfindingProcess.FindPath
                    //
                    // That exception escaped to the message loop, which abandons
                    // the ENTIRE message -- so anything batched behind this move
                    // was dropped too, silently. Guarding here costs one move
                    // instead: the position corrector reconciles it from the
                    // host's next snapshot, which is exactly what it is for.
                    //
                    // Not guarded by checking GetLastTile() first: that returns
                    // the LAST tile, which is still set for a unit that has left,
                    // so it would not have caught this and would only look like
                    // it had.
                    bool accepted;
                    try
                    {
                        accepted = actor.MoveTo(dest, ref action, (MovementFlags)m.MoveFlags);
                    }
                    catch (Exception moveEx)
                    {
                        Plugin.Logger.LogWarning(
                            $"[SYNC] Relayed move to ({m.TX},{m.TY}) threw inside the engine pathfinder " +
                            $"-- the unit is probably no longer on the board. Dropping this move; " +
                            $"the position corrector will reconcile it. ({moveEx.GetType().Name})");
                        break;
                    }
                    // Law 8: MoveTo is async and returns bool -- accepted is not
                    // arrived, and the result was being thrown away here. A
                    // relayed move that paths differently or stops short left
                    // the boards forked until a snapshot happened to notice.

                    // A REFUSED "Leave" IS AN EJECTION THIS MACHINE HAS MISSED.
                    //
                    // The partner has already taken this passenger out of its
                    // transport; the engine will not repeat that from a bare
                    // move, and the skill that would have is refused here too.
                    // Do it directly instead of leaving the unit aboard for the
                    // rest of the battle.
                    if (!accepted && (((int)action & (int)MovementAction.Leave) != 0))
                        accepted = ContainerSync.ForceLeaveContainer(actor, dest);

                    // CONTAINMENT MOVES ARE THE ONES THAT GO WRONG QUIETLY.
                    //
                    // A passenger being ejected arrives as a plain move carrying
                    // MovementAction.Leave. On the sender that call runs INSIDE
                    // the eject skill; here it runs on its own, and whether this
                    // engine will take a contained actor out of its container
                    // that way is exactly the question the rider divergence
                    // turns on. Say so in the log rather than infer it later
                    // from a roster that has already forked.
                    if ((((int)action & (int)(MovementAction.Enter | MovementAction.Leave)) != 0) || !accepted)
                    {
                        bool contained = false;
                        try { contained = ContainerSync.IsContained(actor); } catch { }
                        var landed = actor.GetLastTile()?.GetTilePos();
                        Plugin.Logger.LogInfo(
                            $"[CONTAINER] relayed move act={action} accepted={accepted} "
                            + $"'{DesyncDetector.TemplateNameOf(actor)}' -> ({m.TX},{m.TY}) "
                            + $"now at {(landed == null ? "(nowhere)" : $"({landed.Value.x},{landed.Value.y})")} "
                            + $"stillContained={contained}");
                    }
                    // An ENTRY is remembered: inside a container is where it should
                    // end (r159: 5 false "the partner has ejected it" per client for
                    // militia that had just walked into a building on every machine).
                    TrackRelayedMove(actor, m.TX, m.TY, accepted,
                        enter: (((int)action & (int)MovementAction.Enter) != 0));
                }
                finally { SuppressElementDamage = prevSuppressMv; ApplyingRemote = prevApplyMv; }
                Plugin.Logger.LogInfo($"[SYNC] Applied remote move to ({m.TX},{m.TY}){(m.CtX >= 0 ? $" entering container at ({m.CtX},{m.CtY})" : "")}, actor AP now {actor.GetActionPoints()}");
                break;
            }

            case "moveskill":
            {
                var actor = ResolveOrderActor(m);
                var dest = GetTile(m.TX, m.TY);
                if (actor == null || dest == null)
                {
                    UnresolvedOrder(m); // unit may still be mid-move -- retry shortly
                    return;
                }
                if (HoldForHostRound(m, actor)) return;   // #96
                var action = (MovementAction)m.MoveAction;
                bool prevApplyMs = ApplyingRemote, prevSuppressMs = SuppressElementDamage;
                ApplyingRemote = true;
                SuppressElementDamage = true; // any damage from the move-skill comes from "ehp"
                try
                {
                    Reseed(m.Seed);
                    ApplyContainer(actor, m, dest);   // building entry, if any
                    IMovementSkill ms = null;
                    if (!string.IsNullOrEmpty(m.SkillId))
                        ms = actor.GetSkills()?.GetSkillByID(m.SkillId)?.TryCast<IMovementSkill>();
                    if (ms != null)
                    {
                        actor.MoveToWithSkill(dest, ref action, ms, (MovementFlags)m.MoveFlags);
                        TrackRelayedMove(actor, m.TX, m.TY, true);
                    }
                    else
                        actor.MoveTo(dest, ref action, (MovementFlags)m.MoveFlags); // fallback: at least match position
                }
                finally { SuppressElementDamage = prevSuppressMs; ApplyingRemote = prevApplyMs; }
                Plugin.Logger.LogInfo($"[SYNC] Applied remote move-with-skill {m.SkillId} to ({m.TX},{m.TY})");
                break;
            }

            // WHERE THE UNIT ACTUALLY STOPPED.
            //
            // A move can be cut short: reaction fire and overwatch interrupt a
            // walk part-way. The destination the order asked for is then NOT
            // where the unit ended up, and the two machines will not interrupt
            // at the same step on their own. So the mover's machine reports its
            // real landing tile and the partner adopts it -- the authoritative
            // answer to "where did it stop", rather than each side guessing.
            case "land":
            {
                var actor = ResolveOrderActor(m);
                if (actor == null) { UnresolvedOrder(m); return; }
                if (HoldForHostRound(m, actor)) return;   // #96: behind a move still held
                // Already sent to exactly this tile: the walk gets there; a
                // teleport now would cut its animation short (sliding). Not
                // IsMoving() -- r100: the walk issued 2 ms earlier had not begun
                // that frame. The arrival check stays armed as the fallback.
                bool walkingThere = false;
                try
                {
                    foreach (var rm in _relayedMoves)
                        if (rm.accepted && rm.a != null && rm.a.Pointer == actor.Pointer && rm.x == m.AX && rm.y == m.AY) { walkingThere = true; break; }
                }
                catch { }
                if (!walkingThere) ClearRelayedMoveCheck(actor);   // this supersedes the requested destination

                // Final AP for the completed move, from the machine that made it.
                if (m.Ap >= 0)
                {
                    try
                    {
                        if (actor.GetActionPoints() != m.Ap && ApValueIsSafe(actor, m.Ap))
                        {
                            ApplyingRemote = true;
                            try { actor.SetActionPoints(m.Ap, true); }
                            finally { ApplyingRemote = false; }
                        }
                    }
                    catch { }
                }
                try
                {
                    // Inside a building/vehicle here: a landing tile (from an older
                    // sender, which still reports the doorstep) must not pull it out.
                    if (ContainerSync.IsContained(actor)) break;
                    var pos = actor.GetLastTile()?.GetTilePos();
                    if (pos != null && pos.Value.x == m.AX && pos.Value.y == m.AY) break;   // already right
                    if (walkingThere) break;
                    var dest = GetTile(m.AX, m.AY);
                    if (!TileFree(dest)) break;                     // let the healer sort it
                    ApplyingRemote = true;
                    try
                    {
                        MovementAction act = default;
                        if (actor.MoveTo(dest, ref act, (MovementFlags)(1 | 2 | 4 | 8)))
                            Plugin.Logger.LogInfo($"[SYNC] Partner's unit stopped at ({m.AX},{m.AY}) -- matched (interrupted move)");
                    }
                    finally { ApplyingRemote = false; }
                }
                catch { }
                break;
            }

            case "ap":
            {
                // Authoritative AP straight after the sender's action. Applying
                // it here is what stops the two sides from ever disagreeing,
                // rather than letting a snapshot notice and correct it later.
                var actor = ResolveOrderActor(m);
                if (actor == null || m.Ap < 0) return;   // unresolved: let it retry
                try
                {
                    if (actor.GetActionPoints() != m.Ap && ApValueIsSafe(actor, m.Ap))
                    {
                        ApplyingRemote = true;
                        try { actor.SetActionPoints(m.Ap, true); }
                        finally { ApplyingRemote = false; }
                    }
                }
                catch { }
                break;
            }

            case "mend":
                PartnerMissionOver = true;
                CoopNet.Status = "Mission over -- leaving the battle with the host";
                Plugin.Logger.LogInfo("[SESSION] Host's mission ended; leaving the battle to follow them out");
                HandleMissionEnd();
                break;

            case "skip":
            {
                var actor = ResolveOrderActor(m);
                if (actor == null)
                {
                    UnresolvedOrder(m); // unit may still be mid-move -- retry shortly
                    return;
                }
                if (HoldForHostRound(m, actor)) return;   // #96
                // #37: A SKIP IS NOT AN ESCAPE. On a client, the engine's SkipTurn
                // for a unit it holds as FLEEING runs the flight and removes the
                // unit. The host made that skip at its round start, where the same
                // squad recovered to Wavering 1 ms later and stayed (hand test
                // 2026-10-03 22:08:10: M2 still in round 7 applied the skip ->
                // dying=True at once, "escaped on THIS machine" 7 s later; r120
                // 20:56:41 the same). Escapes are the host's (FleeSync "flee"), so
                // a client only ends that unit's turn.
                bool clientFleeing = false;
                try
                {
                    clientFleeing = CoopNet.Mode != CoopMode.Host
                        && !(Ownership.Active && Ownership.IsKnownPlayerUnit(actor))
                        && actor.GetMoraleState() == MoraleState.Fleeing;
                }
                catch { }
                ApplyingRemote = true;
                try
                {
                    Reseed(m.Seed);
                    if (clientFleeing)
                    {
                        try { actor.SetTurnDone(true); actor.MarkAsHasActed(); } catch { }
                        Plugin.Logger.LogInfo($"[FLEE] Host skipped '{DesyncDetector.TemplateNameOf(actor)}' (fleeing here) -- ended its turn without the engine's flight; the host decides escapes");
                    }
                    else actor.SkipTurn();
                    EnsureTurnEnded(actor, "skip");
                }
                finally { ApplyingRemote = false; }
                Plugin.Logger.LogInfo($"[SYNC] Applied remote skip for actor at ({m.AX},{m.AY})");
                break;
            }

            case "notice":
                Plugin.Logger.LogWarning($"[SESSION] Host notice: {m.Data}");
                CoopRunner.ShowVersionWarning(m.Data ?? "", sticky: true);
                break;
            case "mloot":
                LootSync.OnHostLoot(m);
                break;
            case "hoststay":
                if (CoopNet.Mode == CoopMode.Guest) OnHostStay(m.Prm);
                break;
            case "done":
            {
                var actor = ResolveOrderActor(m);
                if (actor == null)
                {
                    UnresolvedOrder(m); // unit may still be mid-move -- retry shortly
                    return;
                }
                // IDEMPOTENT. A skipped unit arrives twice -- once as the "skip"
                // order, once as this turn-done -- and each application fires
                // the turn-end event, which hands turn control to the other
                // player. Applying it twice therefore ping-pongs control
                // (observed as back-to-back "Control -> Player 1 / Player 2
                // (activation finished)"). If the unit already holds the state
                // being sent, there is nothing to do.
                try
                {
                    if (actor.IsTurnDone() == (m.Prm != 0))
                    {
                        // Already done (the "skip" got here first) -- but the
                        // turn END may still be owed; EnsureTurnEnded is once-only.
                        if (m.Prm != 0)
                        {
                            ApplyingRemote = true;
                            try { EnsureTurnEnded(actor, "done"); } finally { ApplyingRemote = false; }
                            NotePartnerTurnEnded();
                        }
                        break;
                    }
                }
                catch { }

                // Mirror of the send-side rule: never let a partner un-finish a
                // unit our engine has already retired. Sender-side we no longer
                // emit these at all, but a partner on an older build still can,
                // and one such message is a permanent stall -- so refuse it here
                // too rather than trusting the wire.
                if (m.Prm == 0)
                {
                    Plugin.Logger.LogInfo($"[SYNC] Ignored remote turn-done=False for actor at ({m.AX},{m.AY}) -- resets are local-only");
                    break;
                }

                // Full ceremony, not just the flag: unit availability and the
                // turn bar update off the turn-end event, not the flag.
                ApplyingRemote = true;
                try
                {
                    Reseed(m.Seed);
                    actor.SetTurnDone(m.Prm != 0);
                    if (m.Prm != 0)
                    {
                        actor.MarkAsHasActed();
                        // Match the sender's AP: the local end-turn zeroed it
                        // there, and without this the two sides drift apart on
                        // that unit's action points for the rest of the battle.
                        try { if (actor.GetActionPoints() != m.MoveAction) actor.SetActionPoints(m.MoveAction, true); } catch { }
                        EnsureTurnEnded(actor, "done");
                        NotePartnerTurnEnded();
                    }
                }
                finally { ApplyingRemote = false; }
                Plugin.Logger.LogInfo($"[SYNC] Applied remote turn-done={m.Prm != 0} for actor at ({m.AX},{m.AY})");
                break;
            }
        }
    }
}

[HarmonyPatch]
internal static class ReplicationPatches
{
    // EnsureTurnEnded needs to know whether Actor.OnTurnEnd dispatched the
    // event itself; also logs the engine's own turn ends (once per unit per
    // round) so a run shows which machine ran which unit's.
    [HarmonyPatch(typeof(TacticalManager), nameof(TacticalManager.InvokeOnTurnEnd))]
    [HarmonyPrefix]
    private static void NoteInvokeOnTurnEnd(Actor _actor)
    {
        Replication.NoteTurnEndDispatched(_actor);
        if (!CoopNet.Connected || !BugProbes.On) return;
        try
        {
            if (Ownership.IsKnownPlayerUnit(_actor))
                Plugin.Logger.LogInfo($"[TURN] InvokeOnTurnEnd '{DesyncDetector.TemplateNameOf(_actor)}' mine={Ownership.IsLocal(_actor)} remote={Replication.ApplyingRemote}");
        }
        catch { }
    }

    // PROBE (capture tickets): does a partner's unit standing in a capture zone
    // reach CaptureArea.OnTurnEnd on this machine, and do tickets move?
    [HarmonyPatch(typeof(Il2CppMenace.Tactical.Objectives.CaptureArea), nameof(Il2CppMenace.Tactical.Objectives.CaptureArea.OnTurnEnd))]
    [HarmonyPrefix]
    private static void CaptureTurnEnd(Il2CppMenace.Tactical.Objectives.CaptureArea __instance, Actor _actor, bool _isActiveActor, ref int __state)
    {
        __state = -1;
        try { __state = __instance.m_CurrentTickets; } catch { }
    }

    [HarmonyPatch(typeof(Il2CppMenace.Tactical.Objectives.CaptureArea), nameof(Il2CppMenace.Tactical.Objectives.CaptureArea.OnTurnEnd))]
    [HarmonyPostfix]
    private static void CaptureTurnEndDone(Il2CppMenace.Tactical.Objectives.CaptureArea __instance, Actor _actor, bool _isActiveActor, int __state)
    {
        if (!CoopNet.Connected || !BugProbes.On) return;
        try
        {
            if (!Ownership.IsKnownPlayerUnit(_actor)) return;
            bool inArea = false;
            try { inArea = __instance.IsInObjectiveArea(_actor); } catch { }
            int after = __instance.m_CurrentTickets;
            // Unconditional for player units: a unit with NO line means the
            // listener never ran for it, which is the question.
            var at = _actor.GetLastTile()?.GetTilePos();
            Plugin.Logger.LogInfo($"[OBJ] CaptureArea.OnTurnEnd '{DesyncDetector.TemplateNameOf(_actor)}' mine={Ownership.IsLocal(_actor)} active={_isActiveActor} at=({at?.x},{at?.y}) inArea={inArea} tickets {__state} -> {after}");
        }
        catch { }
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.OnTurnEnd))]
    [HarmonyPrefix]
    private static void NoteActorTurnEnd(Actor __instance)
    {
        if (!CoopNet.Connected || !BugProbes.On) return;
        try
        {
            if (Ownership.IsKnownPlayerUnit(__instance))
                Plugin.Logger.LogInfo($"[TURN] OnTurnEnd '{DesyncDetector.TemplateNameOf(__instance)}' mine={Ownership.IsLocal(__instance)} remote={Replication.ApplyingRemote} ended={__instance.m_IsTurnEnded}");
        }
        catch { }
    }

    internal static (int x, int y) TilePos(Tile t)
    {
        var p = t.GetTilePos();
        return (p.x, p.y);
    }

    // Aim previews carry these flags and must never be replicated as real
    // orders. Real player orders are exactly params=Default; AI shots are
    // AutoAudioVolume (log-verified).
    private static bool IsPreview(UsageParameter p)
        => (p & (UsageParameter.Fake | UsageParameter.Free)) != 0;

    // SPEED LOCK. Blocking TacticalState.SpeedUpCurrentActorMovement only
    // closed the click-to-fast-forward door; the time scale is still reachable
    // by other paths, and the game was observed speeding up on its own mid-run.
    // Timing differences are not cosmetic here -- they move the exact moment a
    // reaction shot resolves relative to a walk, which is precisely where the
    // two machines can disagree. Clamp it instead of chasing every caller.
    [HarmonyPatch(typeof(Menace.Tactical.PathMover), nameof(Menace.Tactical.PathMover.SetTimeScale))]
    [HarmonyPrefix]
    private static void ClampTimeScale(ref float _timeScale)
    {
        if (!CoopNet.Connected) return;
        if (Plugin.LockGameSpeed == null || !Plugin.LockGameSpeed.Value) return;
        if (System.Math.Abs(_timeScale - 1f) < 0.001f) return;
        if (UnityEngine.Time.realtimeSinceStartup - _lastSpeedClampLogAt > 5f)
        {
            _lastSpeedClampLogAt = UnityEngine.Time.realtimeSinceStartup;
            Plugin.Logger.LogInfo($"[TURN] Held game speed at 1x (something asked for {_timeScale:0.##}x)");
            CoopNet.Status = "Speed-up is disabled in co-op (it changes when reaction fire resolves)";
        }
        _timeScale = 1f;
    }
    private static float _lastSpeedClampLogAt;

    // Speed is set per-ACTOR as well as globally. Clamping only
    // PathMover.SetTimeScale logged zero clamps while the game was visibly
    // speeding up, because this is a separate lever.
    [HarmonyPatch(typeof(Actor), nameof(Actor.SetTimeScale))]
    [HarmonyPrefix]
    private static void ClampActorTimeScale(ref float _timeScale)
    {
        if (!CoopNet.Connected) return;
        if (Plugin.LockGameSpeed == null || !Plugin.LockGameSpeed.Value) return;
        if (System.Math.Abs(_timeScale - 1f) < 0.001f) return;
        if (UnityEngine.Time.realtimeSinceStartup - _lastActorSpeedLogAt > 5f)
        {
            _lastActorSpeedLogAt = UnityEngine.Time.realtimeSinceStartup;
            Plugin.Logger.LogInfo($"[TURN] Held game speed at 1x (an actor asked for {_timeScale:0.##}x)");
        }
        _timeScale = 1f;
    }
    private static float _lastActorSpeedLogAt;

    [HarmonyPatch(typeof(Skill), nameof(Skill.Use))]
    [HarmonyPostfix]
    private static void SkillApFollowUp() => SendApFollowUp();

    // Any non-player shot opens the hold window, on BOTH machines: the host
    // fires its AI locally, the guest replays the relay, and each must hold its
    // own player's input for the same reason.
    [HarmonyPatch(typeof(Skill), nameof(Skill.Use))]
    [HarmonyPostfix]
    private static void NoteEnemyFire(Skill __instance, UsageParameter _parameters)
    {
        if (!CoopNet.Connected) return;
        if (IsPreview(_parameters)) return;

        // ASK WHO IS SHOOTING, NOT WHICH FLAGS THE SKILL CARRIES.
        //
        // IsPlayerOrder is "parameters == Default", so an AP-GRANTING skill (which
        // carries Free) was classified as ENEMY FIRE. That opened the AI-fire hold
        // window, which blocks player input on BOTH machines -- so using your own
        // AP skill froze the game. This is the AP-skill freeze.
        //
        // A unit is ours or it is not; that does not depend on audio or cost flags.
        try
        {
            var shooter = __instance?.GetActor();
            if (shooter != null && Ownership.Active && Ownership.IsKnownPlayerUnit(shooter)) return;
        }
        catch { }

        if (Replication.IsPlayerOrder(_parameters)) return;   // fallback when the actor is not to hand
        Replication.NoteAiFire();
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.MoveTo))]
    [HarmonyPostfix]
    private static void MoveApFollowUp() => SendApFollowUp();

    // Report the tile a unit actually came to rest on, once the walk is over.
    // This is what makes an INTERRUPTED move survive replication: the partner
    // stops its copy where ours stopped instead of completing the original path.
    [HarmonyPatch(typeof(TacticalState), nameof(TacticalState.OnMovementFinished))]
    [HarmonyPostfix]
    private static void ReportLanding(Actor _actor, Tile _tile)
    {
        if (!CoopNet.Connected || _actor == null || _tile == null) return;
        if (Replication.ApplyingRemote) return;
        // Only the machine that owns the move reports it -- otherwise the
        // replaying side would echo a landing straight back.
        if (Replication.IsReplayingMoveFor(_actor)) return;

        // A LANDING IS A FACT, NOT A COMMAND -- DO NOT GATE IT ON PERMISSION.
        //
        // This used to require ShouldBroadcastOrder(), which is false during a
        // sync pause, hold-fire, while the partner holds the action lock, while
        // deferred orders are pending, and WHEN TURN CONTROL IS WITH THE PARTNER.
        // A walk that happened to FINISH inside any of those windows -- most
        // plausibly just as control passed -- never reported where the unit came
        // to rest, so the partner's copy kept whatever tile its replay ended on.
        // Nothing else corrects it: snapshots flow host->guest only and skip
        // receiver-owned units. Seen as hash gaps that are exact multiples of 961
        // (2883 = three tiles of x) on a settled board.
        //
        // Those gates exist to stop a side ISSUING NEW ACTIONS. Reporting where
        // our own unit stopped is not an action, and suppressing it destroys
        // information no other channel carries. The sync pause is still honoured,
        // because during one the board is deliberately frozen.
        if (Replication.SyncPauseActive) return;
        // A MOVE INTO A BUILDING OR VEHICLE ENDS INSIDE IT, NOT ON THIS TILE
        // (r148 15:50:36: allied militia Entered the structure at (24,29); the
        // engine reported the doorstep (24,28) as its landing, and the clients
        // "matched the interrupted move" by teleporting it back OUT of the
        // building -- tile-less there for the rest of the battle). The Enter
        // itself travels as the move; there is no landing tile to report.
        try { if (ContainerSync.IsContained(_actor)) return; } catch { }
        try
        {
            var p = _tile.GetTilePos();
            // Carry AP here too. The "ap" follow-up fires right after MoveTo is
            // ACCEPTED, but movement cost is settled when the walk COMPLETES, so
            // for moves it was reporting a pre-move figure -- which is why AI
            // action points kept drifting (50 corrections a mission, and every
            // hash mismatch landing on the alien turn). This fires at completion,
            // when the number is final.
            var msg = new CoopMsg { T = "land", AX = p.x, AY = p.y, Ap = _actor.GetActionPoints() };
            Replication.StampIds(msg, _actor, isOrder: false);
            if (_actor.Pointer == _inMoveFor) { _heldLand = msg; return; }   // see _heldLand
            CoopNet.Send(msg);
        }
        catch { }
    }

    [HarmonyPatch(typeof(Skill), nameof(Skill.Use))]
    [HarmonyPrefix]
    private static void BroadcastSkill(Skill __instance, Tile _targetTile, UsageParameter _parameters)
    {
        // A PREVIEW IS NOT AN ORDER -- discard it before any gate sees it.
        //
        // Skill.Use fires with the Fake flag while the UI renders a preview. Testing
        // the broadcast gate first meant every preview was evaluated as a real
        // order, which spammed "Order NOT broadcast -- turn control is with the
        // partner" and let preview frames touch order-path state they have no
        // business touching.
        if (IsPreview(_parameters)) return;
        if (!Replication.ShouldBroadcastOrder()) return;
        if (Replication.IsPlayerOrder(_parameters) && Replication.AiFireInFlight()) return;   // mirrors LockBlockSkill
        // DO NOT FILTER PLAYER SKILLS BY USAGE PARAMETER.
        //
        // This used to send only UsageParameter.Default during the player turn. An
        // AP-GRANTING SKILL uses "Free" -- so it was NEVER relayed: the host gained
        // AP and acted again while the guest never learned the skill happened, and
        // the two turn cycles diverged from that moment. That is the AP-skill
        // freeze, and it was this line.
        //
        // The filter was really trying to exclude previews, and IsPreview already
        // does that above. Whether an order should be relayed is a question of
        // OWNERSHIP (MayBroadcastFor, below), not of which audio/cost flags the
        // engine happened to attach.
        var skillActor = __instance.GetActor();
        // Ownership guard applies to EVERY order type, not just skip/done -- the
        // host was relaying a 'skill' for a guest-owned unit ("TAKEOVER: this side
        // issued 'skill' for a unit it does NOT own ('player_squad.pike')").
        if (!Replication.MayBroadcastFor(skillActor)) return;
        // A SKILL THE SHOOTER CANNOT AFFORD IS NOT AN ORDER. The engine refuses it
        // here as well, so relaying it only made the partner refuse it too and log
        // a desync that was not one (run 30: all 5 "REFUSED" replays were 40 AP
        // weapons fired with 5-30 AP, by the AutoCombat brain).
        try
        {
            int cost = __instance.GetActionPointCost();
            int have = skillActor?.GetActionPoints() ?? 0;
            if (_parameters == UsageParameter.Default && cost > 0 && have < cost)
            {
                Plugin.Logger.LogInfo($"[SYNC] Not relaying {__instance.GetID()}: costs {cost} AP, actor has {have} -- the engine will refuse it here too");
                return;
            }
        }
        catch { }
        var actorTile = skillActor?.GetLastTile();
        if (actorTile == null || _targetTile == null) return;
        var (ax, ay) = TilePos(actorTile);
        var (tx, ty) = TilePos(_targetTile);
        Replication.AcquireActionLock();
        int seed = Replication.NewSeed();
        Replication.Reseed(seed); // before the original method consumes RNG
        MarkApFollowUp(__instance.GetAPConsumer());
        var msg = new CoopMsg { T = "skill", Seed = seed, AX = ax, AY = ay, TX = tx, TY = ty, SkillId = __instance.GetID(), Prm = (int)_parameters };
        // The shooter's AP BEFORE the use (prefix): the receiver starts the replay
        // from it (see the "skill" case).
        try { msg.Ap = skillActor.GetActionPoints(); } catch { }
        try { if (__instance.HasLimitedUses()) msg.SkUses = __instance.GetUses(); } catch { }
        Replication.StampIds(msg, __instance.GetActor());
        GuestReload.NoteSkill(msg, skillActor);   // host: a summon it causes is journaled (#62)
        CoopNet.Send(msg);
    }

    // A multi-tile move is executed by the game as REPEATED MoveTo calls -- one
    // per tile stepped, all with the SAME final destination but an advancing
    // current tile. The receiver applies the first relayed move fully (it jumps
    // straight to the destination), so relaying the per-tile continuations then
    // fails to resolve (the unit already left the intermediate tile) and its
    // position diverges -> cascade. Broadcast only the FIRST call of a move:
    // suppress a same-actor + same-destination call that follows immediately.
    private static System.IntPtr _lastMovePtr;
    private static int _lastMoveTX = int.MinValue, _lastMoveTY = int.MinValue;
    private static int _lastMoveAction = int.MinValue;
    private static float _lastMoveAt;

    private static bool IsMoveContinuation(Actor actor, int tx, int ty, MovementAction action)
    {
        float now = UnityEngine.Time.realtimeSinceStartup;
        int act = (int)action;
        bool sameMove = actor.Pointer == _lastMovePtr && tx == _lastMoveTX && ty == _lastMoveTY
                        && now - _lastMoveAt < 3f;
        // BOARDING/DISEMBARKING guard: MovementAction.Enter / Leave is the
        // containment decision itself (climb into the vehicle, step out of the
        // building) -- it arrives as a step to the SAME destination the walk
        // just used, so the plain "same actor + same tile" test would swallow
        // it and the partner's copy would stay outside the vehicle. An action
        // carrying Enter or Leave is never treated as a per-tile continuation.
        bool containment = (act & (int)(MovementAction.Enter | MovementAction.Leave)) != 0;
        bool same = sameMove && !(containment && act != _lastMoveAction);
        _lastMovePtr = actor.Pointer;
        _lastMoveTX = tx; _lastMoveTY = ty;
        _lastMoveAction = act; _lastMoveAt = now;
        return same;
    }

    // NEVER PUT A MOVE ON THE WIRE THAT THIS ENGINE WILL REFUSE.
    //
    // These broadcasts are PREFIXES -- they run before the engine attempts the
    // move, so an unreachable destination was relayed anyway. The pathfinder
    // then refused it locally ("Movement failed!" 326 times in one battle:
    // pike 107, darby 96, carda 63, tech 60) while the partner received an
    // order to a tile the unit never went to. That is precisely the 41x
    // "Order 'move' never resolved -- DESYNC" and the 41 resync requests: the
    // orders were unresolvable because the move never happened anywhere.
    //
    // The engine's own reachability test is the authority, so ask it first.
    internal static bool MoveIsPossible(Actor a, Tile dest)
    {
        if (a == null || dest == null) return false;
        try
        {
            // NOTE: this `Tactical` is the ROOT namespace, not Menace.Tactical.
            // Aliased at the top of the file so it resolves under both loaders.
            return Tactical.MovementSystem.IsMovementPossible(a, dest, false, out _);
        }
        catch { return true; }   // can't tell -- don't block real play
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.MoveTo))]
    [HarmonyPrefix]
    private static void BroadcastMove(Actor __instance, Tile _tile, ref MovementAction _action, MovementFlags _flags, out CoopMsg __state)
    {
        __state = null;
        // BOARDING PROBE (report 4). Before any of the broadcast gates, because a
        // gate that DROPS the move is itself a candidate cause and a probe placed
        // after them would never see it. Enter/Leave only, so this is quiet.
        if (BugProbes.On && (_action & (MovementAction.Enter | MovementAction.Leave)) != 0)
        {
            // The engine's own answer for "what is standing here". On an Enter the
            // destination tile IS the transport, so this names the container; on a
            // Leave it names whatever the passenger is stepping onto, which is
            // equally worth seeing.
            Actor container = null;
            try { container = _tile?.GetActor(); } catch { }
            BugProbes.Boarding((_action & MovementAction.Enter) != 0 ? "ENTER" : "LEAVE",
                               __instance, container, _tile);
        }

        if (!Replication.ShouldBroadcastOrder()) return;
        if (!Replication.MayBroadcastFor(__instance)) return;   // never drive the partner's unit
        if (HeldByEnemyFire(__instance)) return;   // mirrors LockBlockMove
        if (!MoveIsPossible(__instance, _tile)) return;
        var actorTile = __instance.GetLastTile();
        if (actorTile == null || _tile == null) return;
        var (ax, ay) = TilePos(actorTile);
        var (tx, ty) = TilePos(_tile);
        if (IsMoveContinuation(__instance, tx, ty, _action)) return; // per-tile continuation of one move
        Replication.AcquireActionLock();
        int seed = Replication.NewSeed();
        Replication.Reseed(seed);
        var msg = new CoopMsg { T = "move", Seed = seed, AX = ax, AY = ay, TX = tx, TY = ty, MoveAction = (int)_action, MoveFlags = (int)_flags };
        Replication.StampIds(msg, __instance);
        Replication.StampContainer(msg, __instance);
        __state = msg;   // sent by SendAcceptedMove, only if the engine takes it
        _inMoveFor = __instance.Pointer; _heldLand = null;
    }

    // ONLY A MOVE THE ENGINE ACCEPTED IS AN ORDER.
    //
    // The auto-combat move search calls MoveTo on candidate tiles and moves on
    // when it returns false. Every one of those attempts was relayed: 11 refused
    // moves for the guest's carrier in 20 ms (run 9, 2026-09-25), each re-tried
    // by the partner -- and any the partner's engine happened to ACCEPT would
    // have walked a unit the owner never moved (BUGS.md #11, "guest MoveTo
    // surplus"). The message is built in the prefix (it needs the start tile and
    // the pre-move RNG seed) and sent here. First priority so it precedes the AP
    // follow-up, which is marked only for accepted moves.
    [HarmonyPatch(typeof(Actor), nameof(Actor.MoveTo))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.First)]
    private static void SendAcceptedMove(Actor __instance, bool __result, CoopMsg __state)
    {
        var held = _heldLand; _heldLand = null; _inMoveFor = IntPtr.Zero;
        if (__state == null || !__result) { if (held != null) CoopNet.Send(held); return; }
        MarkApFollowUp(__instance);
        CoopNet.Send(__state);
        if (held != null) CoopNet.Send(held);   // after the move it belongs to
    }

    // LAND MUST NOT OVERTAKE ITS OWN MOVE. A move that completes instantly (the
    // host's AI moving out of view) fires OnMovementFinished INSIDE MoveTo, so
    // its "land" went out before this postfix sent the "move": the partners
    // teleported the unit to its destination, then the walk had nothing to do --
    // units sliding / popping (r99: ~40 per client in 8 rounds; in every run
    // since r88, v4.3.0 included). While we are inside an accepted-to-be MoveTo
    // for a unit, its landing waits for the move.
    private static IntPtr _inMoveFor;
    private static CoopMsg _heldLand;

    // The AI (and player "move & shoot") moves via MoveToWithSkill, a separate
    // entry point from plain MoveTo -- it must be relayed too or the guest never
    // sees those advances.
    [HarmonyPatch(typeof(Actor), nameof(Actor.MoveToWithSkill))]
    [HarmonyPrefix]
    private static void BroadcastMoveWithSkill(Actor __instance, Tile _tile, ref MovementAction _action, IMovementSkill _skill, MovementFlags _flags)
    {
        if (!Replication.ShouldBroadcastOrder()) return;
        if (!Replication.MayBroadcastFor(__instance)) return;   // never drive the partner's unit
        if (!MoveIsPossible(__instance, _tile)) return;   // see MoveIsPossible
        var actorTile = __instance.GetLastTile();
        if (actorTile == null || _tile == null) return;
        var (ax, ay) = TilePos(actorTile);
        var (tx, ty) = TilePos(_tile);
        if (IsMoveContinuation(__instance, tx, ty, _action)) return; // per-tile continuation of one move
        string skillId = null;
        try { skillId = _skill?.TryCast<Skill>()?.GetID(); } catch { }
        Replication.AcquireActionLock();
        int seed = Replication.NewSeed();
        Replication.Reseed(seed);
        MarkApFollowUp(__instance);
        var msg = new CoopMsg { T = "moveskill", Seed = seed, AX = ax, AY = ay, TX = tx, TY = ty, MoveAction = (int)_action, MoveFlags = (int)_flags, SkillId = skillId };
        Replication.StampIds(msg, __instance);
        Replication.StampContainer(msg, __instance);
        CoopNet.Send(msg);
    }

    // AP FOLLOW-UP.
    //
    // The broadcast prefixes fire BEFORE the action runs, so they cannot carry
    // the resulting action points. Rather than move the broadcast (which would
    // change order ordering), the actor is remembered here and its AP is sent
    // immediately afterwards, so the partner's copy of that unit ends the action
    // with exactly the sender's AP instead of its own re-derived figure.
    private static Actor _apFollowUp;

    internal static void MarkApFollowUp(Actor a) => _apFollowUp = a;

    internal static void SendApFollowUp()
    {
        var a = _apFollowUp;
        _apFollowUp = null;
        if (a == null || !CoopNet.Connected) return;
        try
        {
            var msg = new CoopMsg { T = "ap", Ap = a.GetActionPoints() };
            Replication.StampIds(msg, a, isOrder: false);
            var tile = a.GetLastTile();
            if (tile != null) { var p = tile.GetTilePos(); msg.AX = p.x; msg.AY = p.y; }
            CoopNet.Send(msg);
        }
        catch { }
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.SkipTurn))]
    [HarmonyPrefix]
    private static void BroadcastSkip(Actor __instance)
    {
        if (!Replication.ShouldBroadcastOrder()) return;
        if (!Replication.MayBroadcastFor(__instance)) return;   // see MayBroadcastFor
        var actorTile = __instance.GetLastTile();
        if (actorTile == null) return;
        var (ax, ay) = TilePos(actorTile);
        int seed = Replication.NewSeed();
        Replication.Reseed(seed);
        var msg = new CoopMsg { T = "skip", Seed = seed, AX = ax, AY = ay };
        Replication.StampIds(msg, __instance);
        CoopNet.Send(msg);
    }

    // ---- Authoritative health --------------------------------------------
    // Damage is RNG-based, so re-executing a shot on the other machine can roll
    // a different result -> health desync (the enemy the host hit stays at full
    // HP on the guest, which then trips the desync detector). Instead the side
    // that ACTUALLY dealt the damage (the one not applying a remote order)
    // broadcasts the resulting element HP, and the other side snaps to it. This
    // is symmetric: host shots, guest shots and relayed AI shots are all covered
    // by whoever executed the skill locally.
    // A MISSING TILE MUST NOT SILENCE A DAMAGE REPORT.
    //
    // This used to return FALSE when the actor had no tile, and both damage
    // broadcasters bail on that -- so every point of damage and EVERY DEATH to a
    // unit inside a CONTAINER (building, vehicle) was silently never sent.
    // Contained units have no tile by design, so the partner kept those squaddies
    // alive forever, and it could never be repaired afterwards because ApplyHp
    // only compares up to the shorter element list.
    //
    // Seen live: "ROSTER DIFFERS 'player_squad.pike': 2 living element(s) here,
    // host sent 1", a permanent hash gap of exactly 100, repeated resyncs that
    // fixed nothing, two SYNC PAUSEs and a stall at round 23.
    //
    // These messages carry stable identity (StampIds -> RId/CId, plus SqId), so
    // the tile is only a positional fallback. Report (-1,-1) when there is none:
    // ResolveOrderActor tries RId then CId first, and GetTile(-1,-1) is null, so
    // the fallback cannot mis-match anyone.
    private const int NoTile = -1;

    private static bool ActorTileOf(Element el, out int ax, out int ay, out Actor actor)
    {
        ax = ay = NoTile;
        actor = el?.GetEntity()?.TryCast<Actor>();
        if (actor == null) return false;          // genuinely unaddressable
        var tile = actor.GetLastTile();
        if (tile == null) return true;            // contained: identity still carries it
        (ax, ay) = TilePos(tile);
        return true;
    }

    // THE GUEST MAY NOT END THE MISSION.
    //
    // Mission state is host-authoritative. If the guest closes the battle on its
    // own -- its harness deciding the objectives are met, or a player hitting
    // Leave/Abort -- the two machines are in different places entirely: the host
    // plays on into a battle the guest has already left. Only the host decides,
    // and the guest follows when the host's "mend" arrives (see HandleMissionEnd).
    //
    // AllPlayerUnitsDead is allowed through: that is the engine reporting a fact
    // both machines reach independently, not a command, and blocking it would
    // leave the guest standing in a battle it has objectively lost.
    [HarmonyPatch(typeof(TacticalManager), nameof(TacticalManager.Finish))]
    [HarmonyPrefix]
    private static bool GuestMayNotFinish(TacticalFinishReason _finishReason)
    {
        if (!CoopNet.Connected || CoopNet.Mode == CoopMode.Host) return true;
        if (Replication.ApplyingRemote) return true;              // the host told us to
        if (_finishReason == TacticalFinishReason.AllPlayerUnitsDead) return true;
        if (_finishReason == TacticalFinishReason.LoadingSavegame) return true;

        Plugin.Logger.LogWarning($"[SESSION] Blocked a guest-side mission end ({_finishReason}) -- only the host ends the mission");
        CoopNet.Status = "Only the host can end the mission";
        return false;
    }

    // Block HP changes made by a replayed remote skill (visual/AP replay only) --
    // the real HP comes from the "ehp" delta, applied separately.
    [HarmonyPatch(typeof(Element), nameof(Element.SetHitpoints))]
    [HarmonyPrefix]
    private static bool SuppressReplayDamage(Element __instance, int _h)
    {
        Actor victim = null;
        try { victim = __instance?.GetEntity()?.TryCast<Actor>(); } catch { }
        bool swallow = Replication.ShouldSwallowDamage(victim);
        if (swallow && Replication.InPartnersHit && (Plugin.TraceDamage?.Value ?? false))
        {
            try
            {
                int delta = __instance.GetHitpoints() - _h;
                if (delta > 0)
                    Plugin.Logger.LogInfo($"[HITAUTH:{(CoopNet.Mode == CoopMode.Host ? "H" : "G")}] swallowed local copy of partner's hit "
                        + $"-{delta} on {DesyncDetector.TemplateNameOf(victim)} cid={DesyncDetector.CoopIdOf(victim)}");
            }
            catch { }
        }
        return !swallow;
    }

    // Brackets every element damage write with the attacker's identity -- see
    // Replication.BeginHit. REGISTERED via ReplicationPatches.
    [HarmonyPatch(typeof(Actor), nameof(Actor.OnDamageReceived))]
    [HarmonyPrefix]
    private static void EnterHit(Entity _attacker, Skill _skill, DamageInfo _damageInfo, ref long __state)
    {
        __state = Replication.BeginHit(_attacker);
        Replication.NoteHitDetail(_attacker, _skill, _damageInfo);
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.OnDamageReceived))]
    [HarmonyPostfix]
    private static void LeaveHit(long __state)
    {
        Replication.EndHit(__state);
    }

    // DEATH IS A SEPARATE PATH FROM DAMAGE, AND IT WAS NOT SUPPRESSED.
    //
    // SetHitpoints was guarded, but Element.Die() is reached directly by the
    // engine's own kill logic during a replayed skill -- so the replay killed
    // squad members that were still ALIVE on the shooter's machine. The squads
    // then diverged permanently and got worse with every exchange: the same unit
    // was seen going 6 -> 4 -> 1 -> 0 living elements while the shooter still
    // had 9, with damage packets arriving for squaddies that no longer existed
    // ("cannot resolve el8 (sq31) -- actor=found,6 els").
    //
    // A hard restore cannot undo this -- BOARD RESTORED reports "0 revived",
    // because reviving dead squad MEMBERS is not something it does -- so this
    // has to be prevented, not repaired. Deaths come only from the authoritative
    // "ehp"/"edie" messages, neither of which runs under this flag.
    [HarmonyPatch(typeof(Element), nameof(Element.Die))]
    [HarmonyPrefix]
    private static bool SuppressReplayDeath(Element __instance)
    {
        Actor victim = null;
        try { victim = __instance?.GetEntity()?.TryCast<Actor>(); } catch { }
        return !Replication.ShouldSwallowDamage(victim);
    }

    [HarmonyPatch(typeof(Element), nameof(Element.SetHitpoints))]
    [HarmonyPrefix]
    private static void BroadcastElementHp(Element __instance, int _h)
    {
        if (!CoopNet.Connected) return;
        if (Replication.ApplyingRemote && !Replication.HostDecidesHitInReplay
            && !StructureSync.HostOwnsCollapseHit(VictimOf(__instance))) return;
        if (TacticalManager.Get() == null) return;               // only in battle

        // NEVER BROADCAST DAMAGE THIS MACHINE IS ABOUT TO SWALLOW (law 7).
        //
        // The suppression prefix and this broadcast prefix sit on the SAME method.
        // When suppression skipped the damage locally, this still fired and sent a
        // delta for a hit that never happened here -- a phantom that the partner
        // then applied, so the host killed squaddies the guest still had. A local
        // gate must always have an identical broadcast gate.
        {
            Actor victimOfThis = null;
            try { victimOfThis = __instance.GetEntity()?.TryCast<Actor>(); } catch { }
            if (Replication.ShouldSwallowDamage(victimOfThis)) return;
        }

        int delta = __instance.GetHitpoints() - _h;              // positive == damage dealt
        if (delta <= 0) return;                                  // damage only; skips setup/heals

        // LOCAL-DAMAGE TRACE (dev only).
        //
        // Everything reaching here is damage THIS machine applied itself, not a
        // relayed packet (ApplyingRemote is excluded above). Diffing these
        // between the two logs shows exactly which hits one side dealt and the
        // other did not -- which is the open question behind the roster
        // divergence: the guest over-kills its own squad and under-kills enemies,
        // and status/DoT effects (acid_damage_short et al.) have no replication
        // path at all. Three theories have already been wrong here, so measure
        // rather than assume.
        if (Plugin.TraceDamage?.Value ?? false)   // per-hit: off by default, like DamageProbe
        {
            try
            {
                var owner = __instance.GetEntity()?.TryCast<Actor>();
                // STABLE IDENTITY IS WHAT MAKES THE TWO LOGS COMPARABLE.
                //
                // Template name plus squaddie id is NOT unique: every alien_stinger
                // is a one-member squad, so they are all "sq0". Diffing on that
                // compares different actors and produces nonsense -- it made an
                // element look like it went to 0 HP and then back to 9. RId/CId are
                // the ids the wire already uses to address a specific actor.
                int rid = -1, cid = -1;
                try { rid = DesyncDetector.RosterIndexOf(owner); } catch { }
                try { cid = DesyncDetector.CoopIdOf(owner); } catch { }
                var pos = owner?.GetLastTile()?.GetTilePos();
                Plugin.Logger.LogInfo(
                    $"[LOCALDMG:{(CoopNet.Mode == CoopMode.Host ? "H" : "G")}] " +
                    $"{DesyncDetector.TemplateNameOf(owner)} rid={rid} cid={cid} " +
                    $"at({pos?.x ?? -1},{pos?.y ?? -1}) sq{SafeSquaddieId(__instance)} " +
                    $"-{delta} -> {_h} (suppressed={Replication.SuppressElementDamage})");
            }
            catch { }
        }
        // Don't relay damage to a corpse. A dead element still receives
        // SetHitpoints during death processing, and the partner cannot resolve it
        // (its living list no longer holds that squaddie) -- which is where the
        // "cannot resolve el8 (sq7)" warnings came from on BOTH machines at once.
        try { if (!__instance.IsAlive()) return; } catch { }
        if (!ActorTileOf(__instance, out int ax, out int ay, out var actor)) return;
        // Send the DAMAGE DELTA, not the new absolute HP -- see the "ehp" apply
        // case: absolute values conflict when both players hit the same element.
        var msg = new CoopMsg { T = "ehp", AX = ax, AY = ay, Prm = __instance.GetElementIndex(), Seed = delta, SqId = SafeSquaddieId(__instance) };
        Replication.StampIds(msg, actor, isOrder: false);
        Replication.StampHit(msg);
        CoopNet.Send(msg);
    }

    [HarmonyPatch(typeof(Element), nameof(Element.Die))]
    [HarmonyPostfix]
    private static void BroadcastElementDie(Element __instance)
    {
        if (!CoopNet.Connected) return;
        if (Replication.ApplyingRemote && !Replication.HostDecidesHitInReplay
            && !StructureSync.HostOwnsCollapseHit(VictimOf(__instance))) return;
        if (TacticalManager.Get() == null) return;
        // A postfix runs even when the prefix skipped Die(): never announce a
        // death this machine swallowed (law 7, same as BroadcastElementHp).
        {
            Actor victimOfThis = null;
            try { victimOfThis = __instance.GetEntity()?.TryCast<Actor>(); } catch { }
            if (Replication.ShouldSwallowDamage(victimOfThis)) return;
        }
        if (!ActorTileOf(__instance, out int ax, out int ay, out var actor)) return;
        var msg = new CoopMsg { T = "edie", AX = ax, AY = ay, Prm = __instance.GetElementIndex(), SqId = SafeSquaddieId(__instance) };
        Replication.StampIds(msg, actor, isOrder: false);
        Replication.StampHit(msg);
        CoopNet.Send(msg);
    }

    private static Actor VictimOf(Element e)
    {
        try { return e?.GetEntity()?.TryCast<Actor>(); } catch { return null; }
    }

    private static int SafeSquaddieId(Element e)
    {
        try { return e.GetSquaddieId(); } catch { return -1; }
    }

    // Resolve a squad member the sender addressed. Prefer the stable squaddie
    // id; fall back to the list index only when the id is unavailable (an older
    // build, or an element the engine never gave one). See CoopMsg.SqId.
    internal static Element ResolveElement(Actor actor, CoopMsg m)
    {
        var els = actor?.GetElements();
        if (els == null) return null;

        // SQUADDIE IDS ARE ONLY UNIQUE IN PLAYER SQUADS. Every element of a pirate
        // squad reports id 0, so "first element with this id" sent every relayed
        // hit on an enemy squad to one member while the shooter spread them across
        // the squad -- the two machines then lost different members. Both senders
        // (ehp, edie) put the element's permanent slot in Prm, and slot + id
        // together are unique; a lone id match is still authoritative for player
        // squads, whose ids are unique.
        if (m.SqId >= 0)
        {
            Element onlyById = null;
            int idMatches = 0;
            for (int i = 0; i < els.Count; i++)
            {
                var e = els[i];
                if (e == null) continue;
                try
                {
                    if (e.GetSquaddieId() != m.SqId) continue;
                    if (m.Prm >= 0 && e.GetElementIndex() == m.Prm) return e;
                    idMatches++;
                    onlyById = e;
                }
                catch { }
            }
            if (idMatches == 1 && m.Prm < 0) return onlyById;
            if (idMatches == 1)
            {
                // Unique id, different slot: trust the id only when no other
                // living member could share it (player squads).
                bool shared = false;
                for (int i = 0; i < els.Count && !shared; i++)
                    try { shared = els[i] != null && els[i] != onlyById && els[i].GetSquaddieId() == m.SqId; } catch { }
                if (!shared && m.SqId > 0) return onlyById;
            }
            // Not among the living: that squaddie is already dead HERE. The id is
            // authoritative, so we must not fall back to the index and hit
            // somebody else -- but this is agreement, not divergence. Both
            // machines were reporting the identical "actor=found,7 els" for the
            // same squaddies, which is what proved these are packets about the
            // already-dead rather than a roster that had drifted apart.
            return null;
        }

        // No squaddie id: Prm is the permanent SLOT (GetElementIndex), not a
        // position in the living list -- indexing els[] by it hit the wrong member
        // as soon as anyone in the squad had died.
        if (m.Prm < 0) return null;
        for (int i = 0; i < els.Count; i++)
        {
            try { if (els[i] != null && els[i].GetElementIndex() == m.Prm) return els[i]; } catch { }
        }
        return null;
    }

    // Turn-done is broadcast ONLY from inside the player's explicit end-turn
    // commands. Engine-driven flag writes (round-start resets, auto-done on AP
    // exhaustion) happen deterministically on both machines already; echoing
    // them would double-fire turn-end events on the remote side.
    // ---- Manual turn ends (user-controlled) -------------------------------
    // The engine auto-ends a player unit's turn at engine-dependent moments
    // (AP exhausted, finishing an attack with no AP left) -- those moments can
    // differ across machines and were a standing desync source. While
    // connected, ONLY the explicit End Turn / Skip commands (or the partner's
    // relayed done) may end a player unit's turn.
    //
    // KEY (1.4.27): the AP-exhaustion auto-end calls TacticalState.EndTurn --
    // the SAME method as the End Turn button -- so the SetTurnDone-level filter
    // alone could not catch it. The reliable difference is user input: the
    // button/hotkey always fires within a fraction of a second of a click or
    // key press; the auto-end fires from attack-animation completion. Gate
    // EndTurn on recent input.
    private static float _lastAutoEndLogAt;
    private static float _lastOwnBlockAt;

    [HarmonyPatch(typeof(TacticalState), nameof(TacticalState.EndTurn))]
    [HarmonyPrefix]
    private static bool GateAutoEndTurn()
    {
        if (!CoopNet.Connected || !Plugin.ManualTurnEnd.Value) return true;
        if (Replication.ApplyingRemote) return true;
        if (UnityEngine.Time.realtimeSinceStartup - CoopRunner.LastUserInputAt < 0.35f) return true;
        if (UnityEngine.Time.realtimeSinceStartup - _lastAutoEndLogAt > 2f)
        {
            _lastAutoEndLogAt = UnityEngine.Time.realtimeSinceStartup;
            Plugin.Logger.LogInfo("[TURN] Suppressed automatic end-turn (ManualTurnEnd)");
            CoopNet.Status = "Turn NOT auto-ended -- press End Turn when the unit is finished";
        }
        return false;
    }
    // Skip had NO local gate at all: every other order type is held by
    // ActionBlockedByLock, but a skip went through even when its broadcast was
    // refused -- ending the unit here and nowhere else. Gate it like the rest.
    [HarmonyPatch(typeof(Actor), nameof(Actor.SkipTurn))]
    [HarmonyPrefix]
    private static bool HoldSkipWhenBlocked()
    {
        if (!Replication.ActionBlockedByLock()) return true;
        if (UnityEngine.Time.realtimeSinceStartup - _lastSkipHoldLogAt > 2f)
        {
            _lastSkipHoldLogAt = UnityEngine.Time.realtimeSinceStartup;
            Plugin.Logger.LogInfo("[TURN] Skip held -- cannot act right now (see status)");
        }
        return false;
    }
    private static float _lastSkipHoldLogAt;

    [HarmonyPatch(typeof(Actor), nameof(Actor.SkipTurn))]
    [HarmonyPrefix]
    private static bool SkipTurnFlagOn(Actor __instance)
    {
        // A PARTNER'S UNIT IS NEVER SKIPPED LOCALLY (BUGS #79). 0.7.17 hand test:
        // at the round-3 roll M2 called SkipTurn on M3's carrier itself
        // (remote=False). BlockAutoTurnDone refused the turn-done, but SkipTurn
        // had already spent its AP, so the owner's next move was refused on M2
        // and teleported in afterwards. Only the owner's relayed skip applies.
        try
        {
            if (CoopNet.Connected && !Replication.ApplyingRemote && __instance != null
                && Ownership.Active && Ownership.IsKnownPlayerUnit(__instance) && !Ownership.IsLocal(__instance))
            {
                Plugin.Logger.LogWarning($"[OWN] Refused local SkipTurn on partner's '{DesyncDetector.TemplateNameOf(__instance)}' -- caller:\n{Environment.StackTrace}");
                return false;
            }
        }
        catch { }
        Replication.InSkipTurn = true;
        return true;
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.SkipTurn))]
    [HarmonyPostfix]
    private static void SkipTurnFlagOff() => Replication.InSkipTurn = false;

    [HarmonyPatch(typeof(Actor), nameof(Actor.SetTurnDone))]
    [HarmonyPrefix]
    private static bool BlockAutoTurnDone(Actor __instance, bool _turnDone)
    {
        if (!CoopNet.Connected) return true;
        if (!_turnDone) return true;                              // resets always pass
        if (Replication.ApplyingRemote) return true;              // partner's explicit done

        // OWNERSHIP: ending YOUR turn must not end your PARTNER'S units.
        // The engine's end-turn finishes every unit of the player faction, so
        // one player pressing End Turn was marking the other player's squads
        // done -- locally AND, via the broadcast below, on their machine too.
        // That is the squad-takeover bug: the partner's units go inert, their
        // phase completes early, and the turn cycles drift apart.
        if (Ownership.Active && Ownership.IsKnownPlayerUnit(__instance) && !Ownership.IsLocal(__instance))
        {
            if (UnityEngine.Time.realtimeSinceStartup - _lastOwnBlockAt > 2f)
            {
                _lastOwnBlockAt = UnityEngine.Time.realtimeSinceStartup;
                Plugin.Logger.LogInfo($"[OWN] Not ending '{DesyncDetector.TemplateNameOf(__instance)}' -- your partner owns it");
                CoopNet.Status = "Your End Turn only ends YOUR units -- your partner ends theirs";
            }
            return false;
        }

        if (!Plugin.ManualTurnEnd.Value) return true;
        if (Replication.InExplicitEndTurn || Replication.InSkipTurn) return true;

        // ONLY PLAYER UNITS ARE HELD -- decided by WHAT THE UNIT IS, not the phase.
        //
        // This used to allow auto-done through only while !IsPlayerTurn(). Law 3:
        // that flag is activation-level and flickers, so it read "player turn"
        // while an AI unit was finishing and BLOCKED the AI's own done-flag. With
        // no done-flag the host never relayed 'aidone' (zero all battle), so the
        // guest's faction pump never learned the aliens had finished and stranded
        // on faction 7 waiting for units nobody would ever move. That is the freeze.
        //
        // ManualTurnEnd exists to stop a PLAYER's unit auto-ending; it was never
        // meant to touch the AI.
        if (Ownership.Active && !Ownership.IsKnownPlayerUnit(__instance)) return true;

        var tm = TacticalManager.Get();
        if (tm == null) return true;
        if (__instance.IsTurnDone()) return true;                 // no-op write
        // Engine-initiated auto-end of a player unit: suppressed.
        return false;
    }

    // During AI phases the host's engine marks each AI unit done as its
    // activation ends; relaying those flags lets the guest's engine consume
    // activations at the SAME cadence as the host (the round-drift fix) instead
    // of us blanket-completing whole factions.
    [HarmonyPatch(typeof(Actor), nameof(Actor.SetTurnDone))]
    [HarmonyPrefix]
    private static void BroadcastAiTurnDone(Actor __instance, bool _turnDone)
    {
        if (CoopNet.Mode != CoopMode.Host || !CoopNet.Connected) return;
        if (Replication.ApplyingRemote || !_turnDone) return;
        var tm = TacticalManager.Get();
        if (tm == null) return;

        // GATE ON WHAT THE UNIT IS, NOT ON THE PHASE FLAG.
        //
        // This used to skip unless tm.IsPlayerTurn() was false. But law 3: that
        // flag is ACTIVATION-level and flickers several times a round, so it could
        // read true at the exact moment an AI unit finished -- and the relay was
        // silently dropped. Result: the host sent ZERO aidone messages all battle,
        // the guest's faction pump never learned the AI had finished, and it
        // stranded on the alien faction waiting for units nobody would move.
        //
        // A unit is AI or it is not; that does not flicker.
        if (Ownership.Active && Ownership.IsKnownPlayerUnit(__instance)) return;
        if (__instance.IsTurnDone()) return;            // no-op write
        // A unit with NO TILE (garrisoned in a structure, riding a transport --
        // e.g. the settlers in a rescue mission) must still relay its done
        // flag: the guest's faction pump waits for EVERY unit of the faction to
        // be done, so one missing flag stalls that faction forever. Identity
        // (RId/CId) resolves these units; the tile is only a fallback.
        var t = __instance.GetLastTile();
        int ax = -1, ay = -1;
        if (t != null) (ax, ay) = TilePos(t);
        var msg = new CoopMsg { T = "aidone", AX = ax, AY = ay };
        Replication.StampIds(msg, __instance, isOrder: false);
        CoopNet.Send(msg);
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.SetTurnDone))]
    [HarmonyPrefix]
    private static void BroadcastTurnDone(Actor __instance, bool _turnDone)
    {
        // SKIP COUNTS AS AN EXPLICIT END TURN. The local gate above lets a
        // SkipTurn through (InExplicitEndTurn || InSkipTurn) but this broadcast
        // gate used to test InExplicitEndTurn alone -- the exact asymmetry
        // engine law 7 forbids. The consequence: any unit either player SKIPPED
        // went done locally and stayed PENDING on the partner forever, so that
        // player's faction never completed and both engines waited on each
        // other. That is the recurring "ends turn -> alien turn -> freeze".
        // Caught by the self-test trace: guest at mine=0, host still at
        // theirs=2, with the guest's ends logged as explicitEnd=False.
        if (!Replication.InExplicitEndTurn && !Replication.InSkipTurn) return;
        // A TURN-DONE RESET IS NEVER RELAYABLE. Only the SET is an order.
        //
        // The engine's start-of-round reset -- which clears turnDone on EVERY
        // actor on the board -- runs INSIDE TacticalState.EndTurn(), so it
        // happens while InExplicitEndTurn is still true and used to sail
        // straight through this gate as fourteen 'done' messages with Prm=0.
        // On a partner who has not crossed that round boundary yet, each one
        // UN-FINISHED a unit its engine had already retired. The engine never
        // re-activates a unit it has already run, so its faction sat at
        // left=1 forever while the sender moved on: host stuck at r=1 fac=1
        // waiting on a unit the guest had finished in r=1 and was no longer
        // going to touch. That is the freeze.
        //
        // Nothing is lost by dropping these. A reset is not a decision -- it
        // is derived state each engine reproduces for itself when IT reaches
        // the round boundary, and an un-done caused by an AP-granting skill is
        // reproduced by replaying the skill. Relay causes, never derived state.
        if (!_turnDone) return;
        if (!Replication.ShouldBroadcast()) return;
        if (!Replication.MayBroadcastFor(__instance)) return;   // see MayBroadcastFor
        if (__instance.IsTurnDone() == _turnDone) return;
        // ONLY our own faction's units. Ending the player turn also settles
        // other factions' units (civilians, allies) in the engine, and
        // broadcasting those made a player's end-turn mark host-authoritative
        // AI units done on the partner -- seen as "Sending 'done' with NO
        // identity -- unit 'civilian.worker'".
        {
            var tmf = TacticalManager.Get();
            if (tmf == null || __instance.GetFactionID() != tmf.GetActiveFactionID()) return;
        }
        // Second layer of the ownership rule: never tell the partner that THEIR
        // unit has finished. Only the side that owns a unit may end its turn.
        if (Ownership.Active && Ownership.IsKnownPlayerUnit(__instance) && !Ownership.IsLocal(__instance)) return;
        var actorTile = __instance.GetLastTile();
        if (actorTile == null) return;
        var (ax, ay) = TilePos(actorTile);
        int seed = Replication.NewSeed();
        Replication.Reseed(seed);
        // Carry the unit's AP with the turn-end. Ending a turn zeroes AP on the
        // machine that does it, but the receiver only set the flag -- so the two
        // sides disagreed about that unit's AP from then on. That is the source
        // of the recurring one-value drift the logs kept reporting
        // ("AP drift 'player_squad.darby': mine 0 -> host 28"), and it was being
        // "corrected" in the wrong direction, back to the stale value.
        int apNow = 0;
        try { apNow = __instance.GetActionPoints(); } catch { }
        var msg = new CoopMsg { T = "done", Seed = seed, AX = ax, AY = ay, Prm = _turnDone ? 1 : 0, MoveAction = apNow };
        Replication.StampIds(msg, __instance);
        CoopNet.Send(msg);
    }

    // The "click to speed up the current actor's movement" fast-forward cranks
    // PathMover's global time scale, which changes local animation/event timing
    // -- reaction fire and event ordering during a move resolve differently on a
    // sped-up machine than a normal-speed one, causing a desync. Disable it
    // entirely while connected (works normally again once disconnected).
    [HarmonyPatch(typeof(TacticalState), nameof(TacticalState.SpeedUpCurrentActorMovement))]
    [HarmonyPrefix]
    private static bool BlockSpeedUpWhileConnected()
    {
        if (CoopNet.Connected)
        {
            CoopNet.Status = "Turn speed-up is disabled in co-op (it causes desyncs)";
            return false;
        }
        return true;
    }

    [HarmonyPatch(typeof(TacticalState), nameof(TacticalState.EndTurn))]
    [HarmonyPrefix]
    private static void EndTurnScopeEnter() => Replication.InExplicitEndTurn = true;

    [HarmonyPatch(typeof(TacticalState), nameof(TacticalState.EndTurn))]
    [HarmonyPostfix]
    private static void EndTurnScopeExit() => Replication.InExplicitEndTurn = false;

    [HarmonyPatch(typeof(UITactical), nameof(UITactical.EndTurnAll))]
    [HarmonyPrefix]
    private static void EndTurnAllScopeEnter() => Replication.InExplicitEndTurn = true;

    [HarmonyPatch(typeof(UITactical), nameof(UITactical.EndTurnAll))]
    [HarmonyPostfix]
    private static void EndTurnAllScopeExit() => Replication.InExplicitEndTurn = false;

    // Add the CO-OP entry to the title screen menu whenever it (re)builds.
    [HarmonyPatch(typeof(TitleButtons), nameof(TitleButtons.Init))]
    [HarmonyPostfix]
    private static void AddCoopTitleMenuEntry(TitleButtons __instance)
    {
        try
        {
            CoopRunner.InsertTitleMenuEntry(__instance);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[UI] Title menu integration failed: {e.Message}");
        }
    }

    // Only the HOST launches missions; a connected guest must follow, not start
    // one itself (that would desync). Block the guest's manual launch, but
    // allow the mod's own auto-launch that follows the host (ModLaunching).
    [HarmonyPatch(typeof(MissionPrepUIScreen), nameof(MissionPrepUIScreen.StartMission))]
    [HarmonyPrefix]
    private static bool BlockGuestManualLaunch()
    {
        if (CoopNet.Mode == CoopMode.Guest && CoopNet.Connected && !SessionSync.ModLaunching)
        {
            CoopNet.Status = "Only the host can launch the mission -- you'll be pulled in automatically";
            Plugin.Logger.LogInfo("[SESSION] Blocked guest manual mission launch");
            return false; // skip the launch
        }

        // AND THE HOST MAY NOT LAUNCH WHILE THE GUEST IS STILL LOADING THE SAVE.
        //
        // The screen blocker raised when the sync went out stops a mouse, and
        // nothing else: the host still reached the battle two seconds after
        // sending the save and eight seconds before the guest finished loading
        // it (see SessionSync.SyncInFlight for the log). The mission is
        // generated from that save, so starting before it lands is starting a
        // battle the guest cannot reproduce.
        //
        // GuestStillLoading carries its own 90s failsafe, so a lost ack cannot
        // lock the host out of its campaign.
        if (SessionSync.GuestStillLoading)
        {
            CoopNet.Status = $"Wait -- your partner is still loading your game ({SessionSync.SyncWaitSeconds:F0}s)";
            Plugin.Logger.LogWarning(
                $"[SESSION] Blocked host mission launch: guest has been loading for {SessionSync.SyncWaitSeconds:F0}s. "
                + "Starting now would generate a battle the guest cannot reproduce.");
            return false; // skip the launch; the player can press it again once synced
        }
        return true;
    }

    // Capture which mission (and map seed) the host is launching so the guest
    // can be told to start the identical one after its sync load.
    [HarmonyPatch(typeof(MissionPrepUIScreen), nameof(MissionPrepUIScreen.StartMission))]
    [HarmonyPrefix]
    private static void CaptureMissionLaunch(MissionPrepUIScreen __instance)
    {
        var mission = __instance.GetMission();
        if (mission == null) return;
        // Hosting alone (#57): remember the launch so a mid-battle join can rebuild it.
        if (!CoopNet.Connected)
        {
            if (CoopNet.Mode == CoopMode.Host)
            {
                SessionSync.OnHostMissionLaunch(mission.GetIdx(), mission.GetSeed());
                // ...and generate it the way a joiner will regenerate it (hand test
                // 2026-10-04: unpinned solo launch, joiner's map 22% different).
                SessionSync.PinStrategyRandom(mission.GetSeed());
            }
            return;
        }

        // BOTH ROLES, not just the host. The guest launches through this same
        // method (SessionSync drives it with ModLaunching set), and pinning the
        // strategy RNG on only one machine achieves exactly nothing -- the point
        // is that the two enter mission generation from the same RNG position.
        // The mission seed is identical on both by this point: the host reports
        // its own, the guest applied it with Mission.SetSeed before opening prep.
        SessionSync.PinStrategyRandom(mission.GetSeed());
        // The board the enemies generate ONTO is pinned separately, in
        // MapgenPatches -- generation runs on its own schedule (previews included),
        // not at this instant.

        if (CoopNet.Mode != CoopMode.Host) return;
        SessionSync.OnHostMissionLaunch(mission.GetIdx(), mission.GetSeed());
    }

    // Prep screen opened: host announces it (guest mirrors); guest claims the
    // screen for auto-launch or viewer mode.
    [HarmonyPatch(typeof(MissionPrepUIScreen), nameof(MissionPrepUIScreen.OnOpened))]
    [HarmonyPostfix]
    private static void PrepOpened(MissionPrepUIScreen __instance)
    {
        // A mod-driven launch claims the screen on EITHER side: the host's
        // BattleSave resume uses the same auto-launch as the guest (run 34 timed
        // out because only the guest's branch handed the screen over).
        if (CoopNet.Mode != CoopMode.Guest && SessionSync.AutoLaunchPending)
        {
            SessionSync.OnGuestPrepOpened(__instance);
            return;
        }
        if (!CoopNet.Connected) return;
        if (CoopNet.Mode == CoopMode.Host)
            SessionSync.OnHostPrepOpened(__instance);
        else if (CoopNet.Mode == CoopMode.Guest)
            SessionSync.OnGuestPrepScreenOpened(__instance);
    }

    // NOTE: MissionPrepUIScreen.OnInactive can't be patched directly (it's a
    // virtual override that fails Harmony resolution and bricked the plugin).
    // Prep-exit is detected by polling UIScreen.IsActiveScreen() in
    // SessionSync.UpdateHostPrepWatch instead, plus the base UIScreen.OnInactive
    // hook below as a secondary trigger.

    // Live deployment mirroring: every placement the host commits is echoed to
    // the guest's mirrored planning screen.
    [HarmonyPatch(typeof(DeployedEntity), nameof(DeployedEntity.SetTilePosition))]
    [HarmonyPostfix]
    private static void DeployPositionSet(DeployedEntity __instance)
    {
        if (CoopNet.Mode != CoopMode.Host || !CoopNet.Connected) return;
        SessionSync.OnHostDeployPositionSet(__instance);
    }

    // Guests viewing the host's planning may not edit the roster themselves;
    // mirrored changes arriving from the host are exempt.
    private static bool GuestRosterEditAllowed()
        => !(CoopNet.Mode == CoopMode.Guest && CoopNet.Connected
             && SessionSync.ViewerPrepActive && !SessionSync.ApplyingRemoteDeploy);

    [HarmonyPatch(typeof(BattlePlan), nameof(BattlePlan.AddUnit), typeof(BaseUnitLeader), typeof(bool))]
    [HarmonyPrefix]
    private static bool BlockGuestAddUnit() => GuestRosterEditAllowed();

    [HarmonyPatch(typeof(BattlePlan), nameof(BattlePlan.AddUnit), typeof(BaseUnitLeader), typeof(UnityEngine.Vector2Int), typeof(bool))]
    [HarmonyPrefix]
    private static bool BlockGuestAddUnitAt() => GuestRosterEditAllowed();

    [HarmonyPatch(typeof(BattlePlan), nameof(BattlePlan.RemoveUnit))]
    [HarmonyPrefix]
    private static bool BlockGuestRemoveUnit() => GuestRosterEditAllowed();

    // Host authority: a connected guest may not change squad composition (add
    // or remove squaddies). Doing so diverges the two rosters -> desync. Blocked
    // at the data-change methods on BaseUnitLeader, but exempt mod-driven sync
    // loads, which legitimately rebuild the guest's roster from the host's save.
    private static bool GuestMayNotEditSquad()
    {
        if (CoopNet.Mode == CoopMode.Guest && CoopNet.Connected
            && !SessionSync.RemoteLoadInProgress && !Replication.ApplyingRemoteSquaddie)
        {
            CoopNet.Status = "Squad size is host-controlled -- only the host changes squaddies";
            return true;
        }
        return false;
    }

    [HarmonyPatch(typeof(BaseUnitLeader), nameof(BaseUnitLeader.TryAddSquaddie))]
    [HarmonyPrefix]
    private static bool BlockGuestAddSquaddie() => !GuestMayNotEditSquad();

    [HarmonyPatch(typeof(BaseUnitLeader), nameof(BaseUnitLeader.TryRemoveSquaddie))]
    [HarmonyPrefix]
    private static bool BlockGuestRemoveSquaddie() => !GuestMayNotEditSquad();

    // The other half of host authority: the host's squad-size changes mirror
    // live to the guest so its planning view matches (in-battle was already
    // correct via the save load). Only announce changes that actually took
    // (__result) and that the guest didn't just apply (ApplyingRemoteSquaddie).
    [HarmonyPatch(typeof(BaseUnitLeader), nameof(BaseUnitLeader.TryAddSquaddie))]
    [HarmonyPostfix]
    private static void MirrorAddSquaddie(BaseUnitLeader __instance, int _squaddieId, bool __result)
    {
        if (!__result || CoopNet.Mode != CoopMode.Host || !CoopNet.Connected || Replication.ApplyingRemoteSquaddie) return;
        SessionSync.OnHostSquaddieChanged(__instance, _squaddieId, true);
    }

    [HarmonyPatch(typeof(BaseUnitLeader), nameof(BaseUnitLeader.TryRemoveSquaddie))]
    [HarmonyPostfix]
    private static void MirrorRemoveSquaddie(BaseUnitLeader __instance, int _squaddieId, bool __result)
    {
        if (!__result || CoopNet.Mode != CoopMode.Host || !CoopNet.Connected || Replication.ApplyingRemoteSquaddie) return;
        SessionSync.OnHostSquaddieChanged(__instance, _squaddieId, false);
    }

    // Host roster changes mirror to the guest's planning view (only announce
    // operations that actually succeeded).
    [HarmonyPatch(typeof(BattlePlan), nameof(BattlePlan.AddUnit), typeof(BaseUnitLeader), typeof(bool))]
    [HarmonyPostfix]
    private static void MirrorAddUnit(BaseUnitLeader _unitLeader, bool __result)
    {
        if (!__result || CoopNet.Mode != CoopMode.Host || !CoopNet.Connected) return;
        SessionSync.OnHostPlanUnitChanged(_unitLeader, true);
    }

    [HarmonyPatch(typeof(BattlePlan), nameof(BattlePlan.AddUnit), typeof(BaseUnitLeader), typeof(UnityEngine.Vector2Int), typeof(bool))]
    [HarmonyPostfix]
    private static void MirrorAddUnitAt(BaseUnitLeader _unit, bool __result)
    {
        if (!__result || CoopNet.Mode != CoopMode.Host || !CoopNet.Connected) return;
        SessionSync.OnHostPlanUnitChanged(_unit, true);
    }

    [HarmonyPatch(typeof(BattlePlan), nameof(BattlePlan.RemoveUnit))]
    [HarmonyPostfix]
    private static void MirrorRemoveUnit(BaseUnitLeader _unitLeader, bool __result)
    {
        if (!__result || CoopNet.Mode != CoopMode.Host || !CoopNet.Connected) return;
        SessionSync.OnHostPlanUnitChanged(_unitLeader, false);
    }

    [HarmonyPatch(typeof(MissionPrepUIScreen), nameof(MissionPrepUIScreen.OnPreviewReady))]
    [HarmonyPostfix]
    private static void PreviewReadyForAutoLaunch(MissionPrepUIScreen __instance)
    {
        if (!SessionSync.AutoLaunchPending) return;   // guest entry or host resume
        SessionSync.OnGuestPreviewReady(__instance);
    }

    // Deployment sync at the unbypassable point: the game's own spawn call.
    [HarmonyPatch(typeof(BattlePlan), nameof(BattlePlan.DeployEntities))]
    [HarmonyPrefix]
    private static void SyncDeploymentAtSpawn(BattlePlan __instance)
    {
        // A host hosting ALONE records its deployment too (#57: a player may join mid-battle).
        if (!CoopNet.Connected && CoopNet.Mode != CoopMode.Host) return;
        if (CoopNet.Mode == CoopMode.Host)
        {
            // A resumed battle redeploys exactly as it was first deployed.
            if (BattleSave.Restoring) SessionSync.ApplyDeploymentToPlan(__instance);
            SessionSync.CaptureDeploymentFromPlan(__instance);
        }
        else if (CoopNet.Mode == CoopMode.Guest)
            SessionSync.ApplyDeploymentToPlan(__instance);
    }

    // Guests have no deployment authority: dragging units in the prep screen
    // is disabled while connected, so a guest cannot create a desync.
    [HarmonyPatch(typeof(MissionPrepDeployedEntity), nameof(MissionPrepDeployedEntity.OnDragStart))]
    [HarmonyPrefix]
    private static bool BlockGuestDragStart()
        => !(CoopNet.Mode == CoopMode.Guest && CoopNet.Connected);

    [HarmonyPatch(typeof(MissionPrepDeployedEntity), nameof(MissionPrepDeployedEntity.OnDragging))]
    [HarmonyPrefix]
    private static bool BlockGuestDragging()
        => !(CoopNet.Mode == CoopMode.Guest && CoopNet.Connected);

    [HarmonyPatch(typeof(MissionPrepDeployedEntity), nameof(MissionPrepDeployedEntity.OnDragEnd))]
    [HarmonyPrefix]
    private static bool BlockGuestDragEnd()
        => !(CoopNet.Mode == CoopMode.Guest && CoopNet.Connected);

    // Battle-start rendezvous: when the host's battle begins, ship the battle
    // autosave to the guest (SessionSync); when the guest's battle begins
    // (i.e. it loaded that save), signal readiness back.
    [HarmonyPatch(typeof(TacticalManager), nameof(TacticalManager.NextRound))]
    [HarmonyPrefix]
    private static void BattleStartRendezvous(TacticalManager __instance)
    {
        if (__instance.GetRound() != 0) return;
        if (!CoopNet.Connected)
        {
            if (CoopNet.Mode == CoopMode.Host) SessionSync.OnHostBattleStartedAlone();   // #57
            return;
        }
        if (CoopNet.Mode == CoopMode.Host)
        {
            SessionSync.OnHostBattleStarted();
            Ownership.AssignAtHostBattleStart();
        }
        else if (CoopNet.Mode == CoopMode.Guest)
        {
            Ownership.ApplyAtGuestBattleStart();
            // Don't signal "ready" at this first instant -- the battle sim has
            // only just begun and the guest isn't interactive yet. Arm a pending
            // ready that fires once the guest is genuinely settled in the battle
            // (SessionSync.UpdateGuestReady), so the host holds fire until then.
            SessionSync.OnGuestBattleStarted();
        }
    }

    // While the host is holding fire (its battle started but the guest hasn't
    // confirmed it is fully loaded in), the HOST is genuinely prevented from
    // acting -- not just shown a dimmed overlay. Any order it issues now would
    // never reach the still-loading guest and would desync the battle. This
    // enforces the "HOLD FIRE" pause instead of relying on the player to obey it.
    private static bool HostIsHolding()
        => SessionSync.HostHoldFire && CoopNet.Mode == CoopMode.Host
           && CoopNet.Connected && !Replication.ApplyingRemote;

    private static bool BlockedByHold(string what)
    {
        if (HostIsHolding())
        {
            CoopNet.Status = "HOLD FIRE -- wait for your partner to finish loading in";
            return true;
        }
        return false;
    }

    [HarmonyPatch(typeof(Skill), nameof(Skill.Use))]
    [HarmonyPrefix]
    private static bool HoldBlockSkill(UsageParameter _parameters)
        => IsPreview(_parameters) || !BlockedByHold("skill"); // previews still allowed

    [HarmonyPatch(typeof(Actor), nameof(Actor.MoveTo))]
    [HarmonyPrefix]
    private static bool HoldBlockMove() => !BlockedByHold("move");

    [HarmonyPatch(typeof(Actor), nameof(Actor.MoveToWithSkill))]
    [HarmonyPrefix]
    private static bool HoldBlockMoveSkill() => !BlockedByHold("move");

    // Action lock: block STARTING an action while the partner has one in flight.
    // Previews (aiming) are still allowed so the player can line up their move.
    [HarmonyPatch(typeof(Skill), nameof(Skill.Use))]
    [HarmonyPrefix]
    private static bool LockBlockSkill(Skill __instance, UsageParameter _parameters)
    {
        if (IsPreview(_parameters)) return true;                 // aiming is always allowed
        if (Replication.ActionBlockedByLock()) return false;

        // Decide "is this a player order" from the ACTOR, matching the broadcast
        // gate (law 7 -- the two must never disagree). Flags were the wrong test:
        // an AP-granting skill carries Free, so it was misread as an AI order here
        // and as ENEMY FIRE in NoteEnemyFire.
        bool playerOrder = Replication.IsPlayerOrder(_parameters);
        try
        {
            var shooter = __instance?.GetActor();
            if (shooter != null && Ownership.Active)
                playerOrder = Ownership.IsKnownPlayerUnit(shooter);
        }
        catch { }

        // Hold a PLAYER order while enemy fire is still resolving. AI orders are
        // deliberately exempt -- holding those would stop the host relaying its
        // own AI and deadlock the turn cycle.
        if (playerOrder && Replication.AiFireInFlight() && !Replication.ApplyingRemote)
        {
            CoopNet.Status = "Hold on -- enemy fire is still resolving";
            return false;
        }
        return true;
    }

    // Same rule for movement: a player must not walk through an enemy shot that
    // is still being resolved. Only player-owned units are held.
    private static bool HeldByEnemyFire(Actor a)
    {
        if (a == null || Replication.ApplyingRemote) return false;
        // ALONE = NOTHING TO WAIT FOR, AND THE AI IS NEVER HELD. The AI exemption
        // used to depend on Ownership.Active, which is false with no partner
        // connected -- and on the host AiFireInFlight() is true for the WHOLE AI
        // turn, so a host alone (or a solo game with the mod installed) had every
        // enemy move refused: hand test 2026-10-04, round 5 of a defense mission,
        // AP 100 after every MoveTo, the same truck re-trying (31,22)->(22,22).
        if (!CoopNet.Connected) return false;
        if (!Replication.AiFireInFlight()) return false;
        if (!Ownership.IsKnownPlayerUnit(a)) return false;
        CoopNet.Status = "Hold on -- enemy fire is still resolving";
        return true;
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.MoveTo))]
    [HarmonyPrefix]
    private static bool LockBlockMove(Actor __instance) => !MoveIsHeld(__instance);

    [HarmonyPatch(typeof(Actor), nameof(Actor.MoveToWithSkill))]
    [HarmonyPrefix]
    private static bool LockBlockMoveSkill(Actor __instance) => !MoveIsHeld(__instance);

    // AI MOVEMENT IS NEVER HELD -- exactly as AI skills are never held.
    //
    // LockBlockSkill has always exempted AI orders, because holding them stops the
    // host running its own AI and deadlocks the turn cycle. The MOVE gate never got
    // that exemption, so whenever the action lock was held (turn control with the
    // partner, a sync pause, the partner mid-action) EVERY move was blocked --
    // including the AI's.
    //
    // Reported from a two-machine test: "All AI units, friendly and enemy, do not
    // move at all. They take all actions except for movement." Same cause as "the
    // host is stuck on turn 1": an AI activation that cannot move never completes,
    // so the round never advances. It only became severe when the turn-hold
    // failsafe went from 8s to 90s -- one side now holds control long enough for
    // the block to be constant.
    //
    // NOTE: this helper deliberately sits BELOW the patch methods. Placing it
    // between a [HarmonyPatch] attribute and its method made the attribute bind to
    // THIS method instead, whose parameter Harmony cannot resolve -- the whole
    // ReplicationPatches class then failed with "IL Compile Error" and the game
    // could not load at all.
    private static bool MoveIsHeld(Actor a)
    {
        if (Replication.ApplyingRemote) return false;          // replay must always run
        try
        {
            // Only a PLAYER's move waits its turn; the AI's does not.
            if (Ownership.Active && !Ownership.IsKnownPlayerUnit(a)) return HeldByEnemyFire(a);
        }
        catch { }
        return Replication.ActionBlockedByLock() || HeldByEnemyFire(a);
    }

    [HarmonyPatch(typeof(TacticalState), nameof(TacticalState.EndTurn))]
    [HarmonyPrefix]
    private static bool HoldBlockEndTurn() => !BlockedByHold("end turn");

    [HarmonyPatch(typeof(UITactical), nameof(UITactical.EndTurnAll))]
    [HarmonyPrefix]
    private static bool HoldBlockEndTurnAll() => !BlockedByHold("end turn");

    // Soft ownership: units assigned to the partner cannot be selected here.
    // Remote order application is unaffected (it goes through Skill/MoveTo
    // directly, not through selection).
    [HarmonyPatch(typeof(Actor), nameof(Actor.IsSelectableByPlayer))]
    [HarmonyPostfix]
    private static void GateSelectionByOwnership(Actor __instance, ref bool __result)
    {
        if (!__result || !Ownership.Active || Replication.ApplyingRemote) return;
        if (Ownership.IsKnownPlayerUnit(__instance) && !Ownership.IsLocal(__instance))
            __result = false;
    }

    // Ending everyone's turn is a host prerogative; a guest doing it would
    // finish the host's units prematurely.
    [HarmonyPatch(typeof(UITactical), nameof(UITactical.EndTurnAll))]
    [HarmonyPrefix]
    private static bool HostOnlyEndTurnAll()
    {
        if (CoopNet.Mode == CoopMode.Guest && CoopNet.Connected && Ownership.Active)
        {
            CoopNet.Status = "Only the host can end the whole turn";
            return false;
        }
        return true;
    }

    // Track the mission-results screen on both roles (loads and sync saves
    // must not run under it); host additionally auto-syncs when it closes.
    [HarmonyPatch(typeof(Menace.UI.MissionResult.MissionResultUIScreen), nameof(Menace.UI.MissionResult.MissionResultUIScreen.OnOpened))]
    [HarmonyPostfix]
    private static void ResultScreenOpened()
    {
        SessionSync.ResultScreenOpen = true;
        // Tell the partner the battle is finished, so it stops waiting on AI
        // units this machine will never move again.
        if (CoopNet.Mode == CoopMode.Host && CoopNet.Connected)
        {
            LootSync.SendHostLoot();   // before "mend": the guest opens its result screen on "mend"
            CoopNet.Send(new CoopMsg { T = "mend" });
            Plugin.Logger.LogInfo("[SESSION] Mission finished -- told the guest to stop waiting on the battle");
        }
    }

    [HarmonyPatch(typeof(UIScreen), nameof(UIScreen.OnInactive))]
    [HarmonyPostfix]
    private static void ScreenDeactivated(UIScreen __instance)
    {
        // NOTE: MissionPrepUIScreen is intentionally NOT handled here -- its
        // OnInactive fires when a sub-screen (weapon swap) opens on top, which
        // would wrongly end planning. Prep-exit is detected by the
        // GetActiveScreen map-check poll in SessionSync.UpdateHostPrepWatch.
        if (CoopNet.Mode == CoopMode.Guest && CoopNet.Connected
            && __instance.TryCast<MissionPrepUIScreen>() != null)
        {
            // guest: only clear a mirror that fully closed
            SessionSync.OnGuestPrepCancelled();
            return;
        }

        if (__instance.TryCast<Menace.UI.MissionResult.MissionResultUIScreen>() != null)
        {
            SessionSync.ResultScreenOpen = false;
            if (CoopNet.Mode == CoopMode.Host && CoopNet.Connected)
                SessionSync.ScheduleAutoSync(3f, "mission results closed");
            return;
        }

        // Every strategy sub-screen that changes persistent state (items
        // bought/sold, units hired, gear equipped, ship/workshop upgrades,
        // operation rewards) diverges the guest until it re-syncs, and an
        // out-of-date item pool also breaks the equipment mirror -- push a
        // fresh sync when the host closes any of them. Pure-navigation screens
        // (system map, mission/operation select) are intentionally excluded.
        if (CoopNet.Mode != CoopMode.Host || !CoopNet.Connected) return;
        string reason = null;
        // Menu-close syncs are GONE (2.1.3). The armory container fires a close
        // on every tab rotation, so these queued a sync per menu visit -- each
        // load dumped the guest to mission select mid-browsing and made syncing
        // feel slow. Correctness never depended on them: the battle-entry save
        // transfer ships the full state (gear included) when it matters, and
        // the market/hiring economy is live-mirrored. Only true decision points
        // still sync:
        if (__instance.TryCast<Menace.UI.Strategy.OperationResultUIScreen>() != null) reason = "operation result closed";
        if (reason != null) SessionSync.ScheduleAutoSync(2f, reason);
    }

    // Faction trust changes on the host mirror live to the guest.
    [HarmonyPatch(typeof(StoryFaction), nameof(StoryFaction.ChangeTrust))]
    [HarmonyPostfix]
    private static void MirrorTrustChange(StoryFaction __instance, int _change)
    {
        if (CoopNet.Mode != CoopMode.Host || !CoopNet.Connected) return;
        SessionSync.OnHostTrustChanged(__instance, _change);
    }

    // Desync detector: both sides checksum the battle state at every faction
    // switch and exchange hashes.
    [HarmonyPatch(typeof(TacticalManager), nameof(TacticalManager.NextFaction))]
    [HarmonyPrefix]
    private static void DesyncCheckOnFactionSwitch(TacticalManager __instance)
    {
        if (!CoopNet.Connected) return;
        DesyncDetector.OnFactionSwitch(__instance);
    }

    // Guests may not load saves or continue campaigns while connected -- the
    // host drives the campaign; only mod-initiated sync loads are exempt.
    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.Load))]
    [HarmonyPrefix]
    private static bool BlockGuestLoad()
        => AllowLoad();

    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.ExecLoad))]
    [HarmonyPrefix]
    private static bool BlockGuestExecLoad()
        => AllowLoad();

    private static bool AllowLoad()
    {
        if (CoopNet.Mode == CoopMode.Guest && CoopNet.Connected && !SessionSync.RemoteLoadInProgress)
        {
            CoopNet.Status = "Loading is disabled while connected -- the host drives the campaign";
            Plugin.Logger.LogWarning("[SESSION] Blocked guest-initiated load while connected");
            return false;
        }
        // HOST save-scumming is supported (1.5.2+): a strategy-layer load simply
        // becomes the new truth -- the campaign watch detects the fresh state
        // and auto-syncs the guest to it. Only MID-BATTLE loads are blocked:
        // the save system captures no tactical state, so the guest could never
        // follow.
        // A FINISHED BATTLE IS NOT A MID-BATTLE.
        //
        // TacticalManager stays alive underneath the mission-results screen, so
        // this test called the mission "in progress" for as long as the results
        // were on screen and refused every load. The battle is over at that
        // point -- the results are literally being totalled -- and there is
        // nothing left for the guest to fail to follow.
        //
        // The cost of getting this wrong was the harness: its one way to reach
        // the next mission is a reload, and every attempt was refused here with
        // "Blocked host mid-battle load while connected".
        if (CoopNet.Mode == CoopMode.Host && CoopNet.Connected
            && !SessionSync.RemoteLoadInProgress && TacticalManager.Get() != null
            && !SessionSync.ResultScreenOpen)
        {
            CoopNet.Status = "Finish or abort the battle first -- mid-battle loads cannot be synced";
            Plugin.Logger.LogWarning("[SESSION] Blocked host mid-battle load while connected");
            return false;
        }
        return true;
    }

    // Host reseeds the shared RNG at every faction switch and shares the seed,
    // so AI faction turns start from an identical stream on both machines.
    [HarmonyPatch(typeof(TacticalManager), nameof(TacticalManager.NextFaction))]
    [HarmonyPrefix]
    private static void SyncSeedOnFactionSwitch(TacticalManager __instance)
    {
        if (CoopNet.Mode != CoopMode.Host || !CoopNet.Connected) return;
        int seed = Replication.NewSeed();
        Replication.Reseed(seed);
        CoopNet.Send(new CoopMsg { T = "seed", Seed = seed });
        Plugin.Logger.LogInfo($"[SYNC] Faction switch -- set and broadcast RNG seed {seed}");
    }

    // ---- Host-authoritative AI --------------------------------------------
    // The game's AI is inherently non-deterministic across machines: it thinks
    // on a background Task with a wall-clock timeout (AIFaction.RunWithTimeout)
    // and its state machine is paced by frame time (m_Time). Two machines will
    // therefore make different AI choices no matter how we seed the RNG -- this
    // is the root cause of enemies ending up in different positions. Fix: only
    // the HOST runs the AI; it relays every AI order (move/skill) and drives the
    // guest's faction advancement. The guest never runs its own AI.

    // Guest: suppress the AI processing tick entirely. ProcessAI is a concrete
    // method on TacticalManager (not a virtual override), so it is safe to
    // patch here.
    [HarmonyPatch(typeof(TacticalManager), nameof(TacticalManager.ProcessAI))]
    [HarmonyPrefix]
    private static bool GuestSuppressAI()
    {
        if (Replication.SyncPauseActive) return false;   // sync pause freezes AI too
        // A resumed battle is catching up to its saved round: the AI must not act
        // on the half-restored board (works solo too, unlike the sync pause).
        if (BattleSave.Restoring || GuestReload.LocalCatchingUp) return false;
        // Rollover pump window: the engine needs its own pipeline running to
        // perform the round rollover; AI behavior stays dead via AiPatches.
        if (Replication.RolloverPumpActive) return true;
        return !(CoopNet.Mode == CoopMode.Guest && CoopNet.Connected);
    }

    // Host: when a non-player faction's turn ends, tell the guest to advance
    // past it. CRITICAL: the game calls NextFaction MANY times within a single
    // AI faction's turn (once per AI unit) while the active faction stays the
    // same, so we must only signal on a REAL faction change -- otherwise we spam
    // the guest with advances and the two turn cycles desync into a deadlock.
    // The player -> AI transition is deliberately NOT relayed: it happens
    // deterministically on both machines from the replicated end-turn.
    private static int _factionBefore = int.MinValue;
    private static bool _factionBeforeWasPlayer;
    private static int _roundBefore;

    [HarmonyPatch(typeof(TacticalManager), nameof(TacticalManager.NextFaction))]
    [HarmonyPrefix]
    private static void CaptureFactionBeforeSwitch(TacticalManager __instance)
    {
        _factionBefore = __instance.GetActiveFactionID();
        _factionBeforeWasPlayer = __instance.IsPlayerTurn();
        _roundBefore = __instance.GetRound();
    }

    [HarmonyPatch(typeof(TacticalManager), nameof(TacticalManager.NextFaction))]
    [HarmonyPostfix]
    private static void HostBroadcastFactionAdvance(TacticalManager __instance)
    {
        if (CoopNet.Mode != CoopMode.Host || !CoopNet.Connected) return;
        // Phase first: sent in the same frame as the 'ctrl' handover that
        // preceded this switch, so the guest never sees one without the other.
        try { Replication.SendHostPhaseIfChanged(__instance); } catch { }
        int now = __instance.GetActiveFactionID();
        if (now == _factionBefore) return;      // same faction still active -- no real change
        if (_factionBeforeWasPlayer) return;    // left the players' turn: guest does it locally
        // Prm carries the host's CURRENT (post-switch) round: the guest uses it
        // to detect a round rollover and let its engine perform that natively.
        // MoveFlags/Seed name WHICH faction was left, and in which round, so a
        // guest that is still behind can apply it when it gets there instead of
        // to whatever faction it happens to be on (see OnRemoteFactionAdvance).
        CoopNet.Send(new CoopMsg { T = "aiadvance", Prm = __instance.GetRound(), MoveFlags = _factionBefore, Seed = _roundBefore });
        Plugin.Logger.LogInfo($"[SYNC] Host left AI faction {_factionBefore} -> {now} (round {__instance.GetRound()}); telling guest to advance");
    }

    // (1.4.21's GuestHoldLocalFactionCycle prefix lived here -- REMOVED in
    // 1.4.24. Holding the engine's own NextFaction wedged its internal
    // activation state and caused hard native hangs; see OnRemoteFactionAdvance
    // for the full story. Do not reintroduce a NextFaction block.)
}

// Guest-side AI shutdown at the FACTION level. TacticalManager.ProcessAI
// suppression alone is not enough: NextFaction itself invokes the newly-active
// AI faction's OnTurnStart, and on the guest that call can block forever (its
// AI pipeline is never pumped) -- this was the freeze at every round rollover.
// The guest must never run ANY part of the AI; the host relays all AI actions.
[HarmonyPatch]
internal static class AiPatches
{
    private static bool GuestConnected()
        => CoopNet.Mode == CoopMode.Guest && CoopNet.Connected;

    [HarmonyPatch(typeof(Menace.Tactical.AI.AIFaction), "OnTurnStart")]
    [HarmonyPrefix]
    private static bool GuestSkipAiTurnStart()
    {
        if (!GuestConnected()) return true;
        Plugin.Logger.LogInfo("[SYNC] Skipped AI faction turn-start (guest -- host runs the AI)");
        return false;
    }

    [HarmonyPatch(typeof(Menace.Tactical.AI.AIFaction), "Process")]
    [HarmonyPrefix]
    private static bool GuestSkipAiProcess(Menace.Tactical.AI.AIFaction __instance)
    {
        if (!GuestConnected()) return true;
        // Rollover pump window: Process is what drives the engine's own
        // faction/round advancement, so it must RUN -- but only while this
        // faction has nothing left to do (all units done). The moment fresh
        // (post-rollover) units exist, Process would make the AI act -- skip.
        if (Replication.RolloverPumpActive)
        {
            try
            {
                var actors = __instance.GetActors();
                if (actors != null)
                {
                    for (int i = 0; i < actors.Count; i++)
                        if (actors[i] != null && !actors[i].IsTurnDone()) return false;
                    return true;   // faction fully done: let the engine advance
                }
            }
            catch { }
        }
        return false;
    }

    [HarmonyPatch(typeof(Menace.Tactical.AI.AIFaction), "Think")]
    [HarmonyPrefix]
    private static bool GuestSkipAiThink()
        => !GuestConnected();
}

// Separate patch class: if this hook fails to bind after a game update, only
// spawn-id capture degrades -- the core replication hooks stay alive.
[HarmonyPatch]
internal static class SpawnPatches
{
    // Mid-battle reinforcements run through here on BOTH machines (host executes
    // the spawn skill, the guest replays it), so both capture the same
    // deterministic coop id at the spawn tile. Without this, spawned enemies
    // had no identity and were invisible to snapshots (observed live).
    [HarmonyPatch(typeof(Menace.Tactical.Skills.Effects.SpawnEntityEffectHandler), "SpawnEntity")]
    [HarmonyPostfix]
    private static void CaptureSpawn(Menace.Tactical.Skills.Effects.SpawnEntityEffectHandler __instance,
                                     Actor _user, Tile _targetTile)
    {
        if (!CoopNet.Connected) return;
        try
        {
            var list = __instance.m_SpawnedEntities;
            if (list == null || list.Count == 0) return;
            var spawned = list[list.Count - 1];
            DesyncDetector.CaptureSpawnedEntity(spawned, _targetTile);

            // OWNERSHIP FOLLOWS THE DEPLOYER. Identity capture above is not
            // enough: without this, Ownership.AdoptLatecomers hands anything a
            // player summons to the HOST half a second later, and a guest's drone
            // then does nothing and gets rewound. See Ownership.InheritFrom.
            // _user is the spawning skill's own parameter, so no guessing.
            var actor = spawned?.TryCast<Actor>();
            if (actor != null) Ownership.InheritFrom(actor, _user);
            GuestReload.NoteSummon(_user, spawned);
            GuestReload.NoteSummonLanded(_user);
        }
        catch { }
    }
}








