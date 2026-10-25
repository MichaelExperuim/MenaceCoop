using System;
using System.Text;
#if MELON
using Il2CppMenace.States;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;
using Il2CppMenace.UI;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.States;
using Menace.Tactical;
using Menace.Tactical.Skills;
using Menace.UI;
#endif

namespace MenaceCoop;

// SELF-TEST HARNESS -- lets a full mission be played and diagnosed without a human.
//
// Why this exists: every freeze so far was diagnosed from sparse, one-sided log
// lines, which is why ~10 "fixes" each addressed a symptom. Two things were
// missing and both are here:
//
//   1. TRACE -- one canonical, machine-comparable state line per machine.
//      Diffing host vs guest traces shows the EXACT moment they diverge and
//      what each side was waiting for, instead of guessing from side effects.
//
//   2. AUTOPLAY -- drives a mission end-to-end by selecting each owned unit
//      and ending its turn through the same entry point the End Turn button
//      uses. The enemy still acts normally, so the full turn cycle (the only
//      thing that ever deadlocks) runs unattended and repeatably.
//
// Both are OFF by default and are dev tooling only -- they never affect a
// normal play session.
internal static class SelfTest
{
    internal static bool TraceOn => Plugin.SelfTestTrace != null && Plugin.SelfTestTrace.Value;
    // THE DEV HARNESS CANNOT RUN ON A PLAYER'S INSTALL, WHATEVER THE CONFIG SAYS.
    //
    // It drives menus, loads saves and launches missions by itself. That is fine on
    // a test rig and completely wrong on someone's game -- a stray config value (or
    // a config carried over from a dev build) must never be able to make a player's
    // game start playing itself.
    //
    // So it additionally requires a marker file that only exists on a development
    // checkout. A shipped install has no such file, so the whole autopilot is
    // unreachable there no matter what SelfTestAutoPlay is set to.
    //
    // Auto combat is NOT gated by this -- it is a real player feature and only ever
    // commands units the player already owns.
    private static int _devRig = -1;   // -1 unknown, 0 no, 1 yes

    internal static bool OnDevRig
    {
        get
        {
            if (_devRig >= 0) return _devRig == 1;
            _devRig = 0;
            try
            {
                // The dev checkout sits beside the game install. How far up the
                // game root is depends on the loader: BepInEx loads us from
                // BepInEx\plugins (two folders down), MelonLoader from Mods (one).
#if MELON
                string root = System.IO.Path.GetDirectoryName(
                    System.IO.Path.GetDirectoryName(
                        typeof(SelfTest).Assembly.Location));        // ...\Mods -> game root
#else
                string root = System.IO.Path.GetDirectoryName(
                    System.IO.Path.GetDirectoryName(
                        System.IO.Path.GetDirectoryName(
                            typeof(SelfTest).Assembly.Location)));   // ...\BepInEx\plugins -> game root
#endif
                if (root != null && System.IO.Directory.Exists(System.IO.Path.Combine(root, "_CoopModDev")))
                    _devRig = 1;
            }
            catch { }
            if (_devRig == 0)
                Plugin.Logger.LogInfo("[AUTO] Dev harness unavailable (not a development install) -- auto combat is unaffected");
            return _devRig == 1;
        }
    }

    // The DEV harness: drives menus as well as combat, at test pacing.
    internal static bool DevAutoPlay =>
        OnDevRig && Plugin.SelfTestAutoPlay != null && Plugin.SelfTestAutoPlay.Value;

    // The PLAYER feature: fights your own squads and nothing else. Same code,
    // because it drives units through exactly the paths a human uses -- which is
    // what makes it replicate correctly in the first place.
    // Only the Auto Combat mod can fight now (co-op ships no brain, 2026-09-28).
    internal static bool PlayerAutoCombat => AutoCombatBridge.Delegated && AutoCombatBridge.TheirEnabled;

    internal static bool AutoOn => DevAutoPlay || PlayerAutoCombat;

    private static string _lastTrace = "";
    private static float _lastTraceAt;
    private static float _lastChangeAt;
    private static bool _stuckReported;

    // _nextAction / _actions / LockoutOverride were the old brain's action
    // throttle and its lockout escape hatch. All three went with AutoPlay on
    // 2026-09-25: the throttle had no reader left, and a TIMED escape from turn
    // control is the exact anti-pattern CLAUDE.md records from player reports --
    // deliberating is not idling, so handover is decided from board state, never
    // from a clock. MenaceAutoCombat paces itself.

    // Units we have already tried and failed to finish this round. Without this
    // the harness re-picked the same stuck unit every tick and the rest of the
    // squad never moved -- exactly "only one unit is acting".
    private static readonly System.Collections.Generic.HashSet<System.IntPtr> _stuckThisRound = new();
    private static int _stuckRound = -1;
    // Where each unit stood before its last move -- stops back-and-forth pacing.
    private static readonly System.Collections.Generic.Dictionary<System.IntPtr, (int x, int y)> _lastTileOf = new();
    private static System.IntPtr _lastPicked;
    private static int _lastPickedCount;

    private static string Side => CoopNet.Mode == CoopMode.Host ? "H" : "G";

    // Latched so the stand-down is announced once, not every frame.
    private static bool _standDownLogged;

    internal static void Tick()
    {
        if (!TraceOn && !AutoOn) return;
        var tm = TacticalManager.Get();
        if (tm == null) { _lastTrace = ""; _stuckReported = false; return; }

        // THE MISSION IS OVER. STOP PLAYING IT.
        //
        // The tactical layer stays alive underneath the results screen, so
        // nothing here noticed the mission had ended and auto-combat kept
        // issuing orders into a finished battle -- moving squads, waiting on
        // animations, reporting "idle 10s while holding units" -- while the
        // results screen sat on top waiting to be dismissed.
        //
        // That is what made the results screen look unpressable. Next was blamed
        // for needing "a real mouse", the harness was given up on and rewired to
        // reload a pinned save instead, and the campaign could therefore never
        // advance past mission one. The button was never the problem: the game
        // will not leave a battle that is still being played.
        if (SessionSync.ResultScreenOpen)
        {
            if (!_standDownLogged)
            {
                _standDownLogged = true;
                Plugin.Logger.LogInfo("[AUTO] Mission over (results screen up) -- auto-combat standing down");
            }
            return;
        }
        _standDownLogged = false;

        if (TraceOn) Trace(tm);
        // Answers, from the live board, whether the engine.s own target scorer is
        // reachable for units a human owns. Logs once per battle either way.
        EnemyBrain.ProbeOnce(tm);

        // THE TACTICAL BRAIN LIVES IN MenaceAutoCombat, and now lives there only.
        //
        // Co-op's ORIGINAL brain -- AutoPlay() and roughly 1,700 lines of
        // movement, target-matching, cover, patrol and revive helpers -- was
        // deleted 2026-09-25. MenaceAutoCombat was extracted from it and then
        // developed a long way past it (target scoring, objective focus,
        // formations, action settling, a turn watchdog), so the copy here was
        // strictly the worse of the two and had been unreachable under
        // MelonLoader since v5.0.0. It survived only as the BepInEx `#else`
        // branch; that project is gone too. The pre-deletion source is in
        // ..\baseline\src-v5.0.0-preCleanup\ if any of it is ever wanted back.
        //
        // It was extracted, not forked, so the brain still calls co-op's own
        // Ownership, CoopNet, CoopRunner and Replication by name, and
        // SinglePlayer.cs is excluded from this build -- which is what binds
        // those calls to the real ownership rules. Co-op keeps the TRACE and the
        // dev harness above; the brain only makes tactical decisions, and
        // AutoCombat=false still means nothing drives anything.
        //
        // ONE PIECE OF AutoPlay() IS STILL OURS. The brain decides how to fight;
        // it has no notion of ENDING a mission, because MenaceAutoCombat never
        // needed one -- a player closes their own battle. The harness does need
        // it, or a won mission runs on to the round cap. Host-only, dev-only.
        if (DevAutoPlay) DebugSync.HarnessScript(tm);
        if (DevAutoPlay && TryFinishCompletedMission(tm, UnityEngine.Time.realtimeSinceStartup)) return;
        // Combat itself comes from the Auto Combat mod (AutoCombatBridge); co-op
        // no longer carries a brain, so without that mod the harness cannot fight.
    }

    // ---- 1. Canonical state line ------------------------------------------
    // Emitted whenever it CHANGES, plus a heartbeat every 5 s so a frozen
    // machine still proves it is alive (and frozen) rather than crashed.
    private static void Trace(TacticalManager tm)
    {
        string line;
        try { line = BuildTrace(tm); }
        catch (Exception e) { line = $"trace-failed {e.Message}"; }

        float now = UnityEngine.Time.realtimeSinceStartup;
        bool changed = line != _lastTrace;
        if (changed)
        {
            _lastTrace = line;
            _lastChangeAt = now;
            _stuckReported = false;
        }
        if (changed || now - _lastTraceAt > 5f)
        {
            _lastTraceAt = now;
            Plugin.Logger.LogInfo($"[TRACE:{Side}] {line}");
        }

        // Nothing about the tactical state has changed for 20 s while both
        // sides are connected: that IS the deadlock. Dump who owes what, once.
        if (!_stuckReported && CoopNet.Connected && now - _lastChangeAt > 20f)
        {
            _stuckReported = true;
            Plugin.Logger.LogWarning($"[TRACE:{Side}] STUCK for 20s on: {_lastTrace}");
            DumpPending(tm);
        }
    }

    private static string BuildTrace(TacticalManager tm)
    {
        int round = tm.GetRound();
        int fac = tm.GetActiveFactionID();
        bool pt = tm.IsPlayerTurn();

        int mine = 0, theirs = 0, ai = 0;
        var factions = tm.GetFactions();
        if (factions != null)
        {
            for (int i = 0; i < factions.Count; i++)
            {
                var f = factions[i];
                if (f == null) continue;
                var actors = f.GetActors();
                if (actors == null) continue;
                for (int j = 0; j < actors.Count; j++)
                {
                    var a = actors[j];
                    if (a == null) continue;
                    bool pending;
                    try { pending = !a.IsDying() && !a.IsTurnDone(); } catch { continue; }
                    if (!pending) continue;
                    if (Ownership.Active && Ownership.IsKnownPlayerUnit(a))
                    {
                        if (Ownership.IsLocal(a)) mine++; else theirs++;
                    }
                    else ai++;
                }
            }
        }

        string act;
        try { var a = tm.GetActiveActor(); act = a == null ? "-" : DesyncDetector.TemplateNameOf(a); } catch { act = "?"; }

        return $"r={round} fac={fac} player={(pt ? 1 : 0)} ctrl={(TurnControl.Active ? (TurnControl.IsMyTurn ? "me" : "them") : "-")} " +
               $"mine={mine} theirs={theirs} ai={ai} active={act}";
    }

    // Who exactly is holding the cycle up -- the question every freeze asked.
    private static void DumpPending(TacticalManager tm)
    {
        try
        {
            var sb = new StringBuilder();
            var factions = tm.GetFactions();
            if (factions == null) return;
            for (int i = 0; i < factions.Count; i++)
            {
                var f = factions[i];
                if (f == null) continue;
                int idx;
                try { idx = f.GetIndex(); } catch { idx = -1; }
                var actors = f.GetActors();
                if (actors == null) continue;
                for (int j = 0; j < actors.Count; j++)
                {
                    var a = actors[j];
                    if (a == null) continue;
                    try
                    {
                        if (a.IsDying() || a.IsTurnDone()) continue;
                        string own = Ownership.Active && Ownership.IsKnownPlayerUnit(a)
                            ? (Ownership.IsLocal(a) ? "MINE" : "THEIRS") : "ai";
                        sb.Append($" [f{idx} {DesyncDetector.TemplateNameOf(a)} {own} ap={a.GetActionPoints()}]");
                    }
                    catch { }
                }
            }
            Plugin.Logger.LogWarning($"[TRACE:{Side}] pending:{(sb.Length == 0 ? " (none)" : sb.ToString())}");

            // The ENGINE's own bookkeeping, not ours. When our done-flag count
            // says a faction is finished but GetAmountOfActorsLeftToAct still
            // reports work, the engine will never advance -- and that gap, not
            // the flags, is the thing to fix.
            var eng = new StringBuilder();
            for (int i = 0; i < factions.Count; i++)
            {
                var f = factions[i];
                if (f == null) continue;
                try { eng.Append($" f{f.GetIndex()}:left={f.GetAmountOfActorsLeftToAct()}"); } catch { }
            }
            try { eng.Append($" roundDone={tm.IsRoundDone()}"); } catch { }
            try { eng.Append($" lastUndonePlayer={tm.IsTurnOfLastUndonePlayerActor()}"); } catch { }
            try { eng.Append($" finishedPct={tm.GetUnitsFinishedTurnPct():0.00}"); } catch { }
            // A unit stuck in the MOVING state freezes everything downstream:
            // snapshots stop (the board never settles), player actions are held,
            // and the AI waits on it. Name it here rather than inferring it.
            try
            {
                bool moving = TacticalManager.IsAnyActorMoving();
                eng.Append($" anyMoving={moving}");
                if (moving)
                {
                    var mv = tm.GetMovingActors();
                    if (mv != null)
                        foreach (var m in mv)
                            if (m != null) eng.Append($" MOVING:{DesyncDetector.TemplateNameOf(m)}");
                }
            }
            catch { }
            try { eng.Append($" syncPaused={Replication.SyncPauseActive}"); } catch { }
            Plugin.Logger.LogWarning($"[TRACE:{Side}] engine:{eng}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[TRACE:{Side}] pending dump failed: {e.Message}"); }
    }

    private static string DesyncNames(Actor a) => DesyncDetector.TemplateNameOf(a);

    // END A MISSION WHOSE OBJECTIVES ARE MET.
    //
    // "Everything dead" is not the win condition -- the objectives are. Fighting
    // on after they are complete is what produced 71-round missions.
    //
    // HOST ONLY. Mission end is host-authoritative (the guest is told via the
    // "mend" relay and pulled along by the post-mission sync); if both machines
    // decided independently to finish, they could do it at different moments and
    // diverge on what happened in between.
    private static float _lastObjectiveCheck;
    private static bool _finishRequested;
    private static bool _objectiveLookupLogged;
    private static Menace.Tactical.Objectives.ObjectiveState _lastObjectiveState
        = (Menace.Tactical.Objectives.ObjectiveState)(-1);

    private static bool TryFinishCompletedMission(TacticalManager tm, float now)
    {
        if (CoopNet.Connected && CoopNet.Mode != CoopMode.Host) return false;
        if (_finishRequested) return false;
        if (now - _lastObjectiveCheck < 2f) return false;
        _lastObjectiveCheck = now;

        try
        {
            var mission = Menace.States.StrategyState.Get()?.GetCurrentOperation()?.GetCurrentMission();
            var objectives = mission?.Objectives;

            // Five missions ran to 46 rounds without this ever firing. Report
            // what is actually being read -- a null mission and an Ongoing
            // objective are completely different problems, and "it never
            // triggered" does not distinguish them.
            if (objectives == null)
            {
                if (!_objectiveLookupLogged)
                {
                    _objectiveLookupLogged = true;
                    Plugin.Logger.LogWarning($"[AUTO] No objectives reachable (mission={(mission == null ? "NULL" : "found")}) -- cannot end on objectives");
                }
                return false;
            }

            var state = objectives.GetPrimaryObjectivesState();
            if (state != _lastObjectiveState)
            {
                _lastObjectiveState = state;
                Plugin.Logger.LogInfo($"[AUTO] Primary objectives now: {state}");
            }
            // A FAILED PRIMARY ENDS THE MISSION JUST AS DEFINITIVELY AS A WON ONE.
            //
            // This only accepted Completed, so a mission whose primary objective
            // FAILED was never closed out -- the engine stopped activating units
            // and the board simply sat there. Caught live: both machines byte
            // identical at round 14 (f7:left=4, same pending list, same AP), so
            // not a desync at all; the mission was over and nothing ended it. The
            // watchdog could not help either, because there was no divergence to
            // reconcile and no controller to hand over.
            bool primaryDecided =
                state == Menace.Tactical.Objectives.ObjectiveState.Completed
                || state == Menace.Tactical.Objectives.ObjectiveState.Failed;
            if (!primaryDecided) return false;

            // ONLY THE HOST ENDS A MISSION.
            //
            // Mission state is host-authoritative (see the authority model), but
            // this harness runs on BOTH machines, so the guest could reach the
            // same conclusion a moment earlier and close the battle out from
            // under the host -- two machines deciding independently when the
            // mission is over is a split brain, not a race worth winning.
            // The guest leaves when the host's "mend" tells it to.
            if (CoopNet.Connected && CoopNet.Mode != CoopMode.Host) return false;

            // Let anything in flight land before ending, so the partner is not
            // mid-order when the mission closes.
            if (TacticalManager.IsAnyActorMoving()) return false;

            _finishRequested = true;
            Plugin.Logger.LogWarning(
                $"[AUTO] Primary objectives {state} -- ending the mission rather than leaving the board to sit");
            tm.Finish(TacticalFinishReason.Leave);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[AUTO] could not end the completed mission: {e.Message}");
            return false;
        }
    }

    // A patrol destination well away from where the unit currently stands.
    //
    // Sweeps toward map extremities rather than wandering: each unit takes a
    // different quadrant (seeded by its pointer) and the choice rotates every few
    // rounds, so a squad covers the map instead of clumping. Falls back to
    // progressively nearer tiles when the far one is not a real map tile, since
    // map dimensions are not exposed directly.
    // PATROL TOWARD PLACES, NOT IN DIRECTIONS.
    //
    // This used to pick a compass direction from wherever the unit was standing
    // and walk that way. Reported from play: "squads are sitting in the bottom
    // right corner... ai combat is excellent when they spawn in the middle but
    // not when they spawn at the bottom or a side."
    //
    // That is this function, exactly. A squad in the bottom-right corner whose
    // seeded direction points further into the corner gets a null tile for every
    // reach and therefore never patrols at all -- it stands still. Near an edge
    // only two or three of the eight directions land on the map, so the unit
    // flips between them round after round: the "rotating side to side".
    //
    // Absolute destinations fix both. A corner unit is far from every anchor but
    // the one it is sitting on, so there is always somewhere to go, and "away
    // from here" is a property of the map rather than of the compass.
    private static (int w, int h) _mapBounds;
    private static IntPtr _boundsFor;

    private static (int w, int h) MapBounds(TacticalManager tm, Map map)
    {
        if (tm.Pointer == _boundsFor && _mapBounds.w > 0) return _mapBounds;
        int w = 0, h = 0;
        try
        {
            for (int x = 0; x < 256; x++) { if (map.GetTile(x, 0) == null) break; w = x + 1; }
            for (int y = 0; y < 256; y++) { if (map.GetTile(0, y) == null) break; h = y + 1; }
        }
        catch { }
        if (w > 0 && h > 0) { _mapBounds = (w, h); _boundsFor = tm.Pointer; }
        return (w, h);
    }

    // ---- WEAPON / TARGET MATCHING -------------------------------------------
    //
    // Right weapon for the right target. The picker previously preferred the
    // COSTLIEST usable weapon, which meant rockets fired at spiderling swarms --
    // an expensive anti-armour shot spent on a cheap target it barely suits,
    // while the machine gun that shreds swarms sat unused.
    //
    // Classified by skill id because the engine exposes no damage-type getter we
    // can rely on here. Unknown weapons score neutrally, so a template we have
    // not seen is never actively mis-used.

    private static Actor FindMyPendingUnit(TacticalManager tm)
    {
        int round;
        try { round = tm.GetRound(); } catch { round = _stuckRound; }
        if (round != _stuckRound) { _stuckRound = round; _stuckThisRound.Clear(); }

        // ALWAYS look at the PLAYER faction, not the engine's current cursor.
        // With the cursor on an AI faction this returned nothing at all, so the
        // whole squad sat idle.
        var actors = (tm.GetPlayerFaction() ?? tm.GetActiveFaction())?.GetActors();
        if (actors == null) return null;

        Actor first = null;
        for (int i = 0; i < actors.Count; i++)
        {
            var a = actors[i];
            if (a == null) continue;
            try
            {
                if (a.IsDying() || a.IsTurnDone()) continue;
                // NEVER act before ownership is assigned.
                //
                // This used to fall through to "any unit" while Ownership was
                // still being applied at battle start -- so the host drove the
                // guest's squad and issued skips for units it does not own
                // (TAKEOVER warnings for pike and darby). Waiting a moment costs
                // nothing; taking over the partner's squad is the bug this whole
                // ownership system exists to prevent.
                if (!Ownership.Active) continue;
                if (!Ownership.IsKnownPlayerUnit(a)) continue;
                if (!Ownership.IsLocal(a)) continue;              // never touch the partner's
                if (_stuckThisRound.Contains(a.Pointer)) continue; // already proved unfinishable
                if (first == null) first = a;
            }
            catch { }
        }

        // If we keep being handed the SAME unit and it never finishes, park it
        // and move to the next one. One unfinishable unit must not cost the
        // whole squad its turn.
        if (first != null)
        {
            if (first.Pointer == _lastPicked)
            {
                if (++_lastPickedCount > 12)
                {
                    _stuckThisRound.Add(first.Pointer);
                    _lastPicked = System.IntPtr.Zero;
                    _lastPickedCount = 0;
                    Plugin.Logger.LogWarning(
                        $"[AUTO:{Side}] '{DesyncNames(first)}' will not finish its turn -- moving on to the rest of the squad");
                    return FindMyPendingUnit(tm);
                }
            }
            else { _lastPicked = first.Pointer; _lastPickedCount = 0; }
        }
        return first;
    }

    internal static void Reset()
    {
        _lastTrace = ""; _stuckReported = false;
        _finishRequested = false;   // a fresh mission may be started
    }
}
