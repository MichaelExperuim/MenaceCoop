using System;
using System.Collections.Generic;
using System.Text;
using Il2CppMenace.States;
using Il2CppMenace.Tactical;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling

namespace MenaceCoop;

// Soft unit ownership: each player may only select (and therefore order) the
// squads assigned to them. Pure input gating on each client -- the simulation
// itself is untouched, so this cannot introduce desyncs. Host assigns units by
// deployment-list slot (alternating) at battle start and broadcasts the
// guest's slots; each side gates Actor.IsSelectableByPlayer accordingly.
internal static class Ownership
{
    // v5: every player unit maps to the SEAT that commands it (Peers.cs). "Mine"
    // is seat == Peers.LocalSeat; at N=2 that is exactly the old local set.
    private static readonly Dictionary<IntPtr, int> _owner = new();
    private static bool _active;
    private static string _pendingGuestSlots; // guest: received before battle loaded
    private static string _pendingSeats;      // guest: per-slot seats, same moment

    internal static bool Active => _active && CoopNet.Connected;
    internal static int LocalCount => CountOf(Peers.LocalSeat);
    internal static int TotalCount => _owner.Count;
    internal static int CountOf(int seat)
    {
        int n = 0;
        foreach (var kv in _owner) if (kv.Value == seat) n++;
        return n;
    }

    // ---- A DROPPED PLAYER'S UNITS GO TO THE HOST, AND COME BACK (user, 2026-10-03)
    //
    // While a seat is disconnected its units sat idle and the round could not
    // finish without them. Now the host commands them until that player rejoins,
    // then they return -- keyed on the leave/rejoin events, never on a timer.
    // _lent remembers each borrowed unit's real seat; the canonical assignment
    // (_lastSeats) is never touched, so a reload rebuilds the true owners.
    private static readonly Dictionary<IntPtr, int> _lent = new();

    internal static bool IsLent(Actor a) => a != null && _lent.ContainsKey(a.Pointer);
    internal static int LentCount => _lent.Count;

    internal static void LendToHost(int seat)
    {
        if (seat == Peers.HostSeat) return;
        int n = 0;
        foreach (var k in new List<IntPtr>(_owner.Keys))
            if (_owner[k] == seat) { _owner[k] = Peers.HostSeat; _lent[k] = seat; n++; }
        if (n == 0) return;
        Plugin.Logger.LogWarning($"[OWN] {Peers.NameOf(seat)} (seat {seat}) is gone: the host commands their {n} unit(s) until they return");
        if (CoopNet.Mode == CoopMode.Host) CoopNet.Send(new CoopMsg { T = "olend", Prm = seat });
        CoopNet.Status = CoopNet.Mode == CoopMode.Host
            ? $"{Peers.NameOf(seat)} dropped -- you command their units until they return"
            : $"{Peers.NameOf(seat)} dropped -- the host commands their units";
    }

    internal static void ReturnLent(int seat)
    {
        int n = 0;
        foreach (var k in new List<IntPtr>(_lent.Keys))
            if (_lent[k] == seat) { if (_owner.ContainsKey(k)) _owner[k] = seat; _lent.Remove(k); n++; }
        if (n == 0) return;
        Plugin.Logger.LogInfo($"[OWN] {Peers.NameOf(seat)} (seat {seat}) is back: {n} unit(s) returned to them");
        if (CoopNet.Mode == CoopMode.Host) CoopNet.Send(new CoopMsg { T = "oreturn", Prm = seat });
    }

    internal static void Reset()
    {
        _lent.Clear();
        _owner.Clear();
        _aiSummons.Clear();
        _localAiSummons.Clear();
        _active = false;
    }

    internal static bool IsKnownPlayerUnit(Actor actor) => _owner.ContainsKey(actor.Pointer);
    internal static bool IsLocal(Actor actor) => _owner.TryGetValue(actor.Pointer, out int s) && s == Peers.LocalSeat;
    // The seat commanding this unit, or -1 for anything that is not a player unit.
    internal static int OwnerOf(Actor actor) => actor != null && _owner.TryGetValue(actor.Pointer, out int s) ? s : -1;

    // A UNIT A PLAYER DEPLOYED BELONGS TO THAT PLAYER, NOT TO THE HOST.
    //
    // AdoptLatecomers below gives every mid-battle arrival to the HOST, which is
    // right for a rescued VIP (nobody deployed it, and an arbitrary but identical
    // rule on both machines beats a message that races the first order). It is
    // WRONG for something a player summoned, and that is a reported bug: "an
    // anti-tank drone, when used by a guest, does not cause damage and causes an
    // automatic rollback to the beginning of the turn."
    //
    // Both halves of that follow from the host adopting the guest's drone. Damage
    // ownership belongs to the SHOOTER'S machine, so with the drone filed as the
    // host's, the guest's drone hits were discarded and the host never made them;
    // then the next automatic hard resync pushed the host's board -- which has no
    // drone action in it -- over the guest's, and the player watched their turn
    // rewind.
    //
    // Inheriting from the deploying unit needs no wire traffic, for the same
    // reason adoption needs none: the skill use is already relayed, so both
    // machines run this with the same _user and reach the same answer
    // independently. Called from the spawn hook, which is a POSTFIX on the spawn
    // itself, so it always wins the race against the twice-a-second adoption scan.
    private static readonly System.Collections.Generic.HashSet<System.IntPtr> _aiSummons = new();
    // The AI summons one of OUR units deployed. The AI flies them, but only this
    // player holds the command skills that steer them (Effects.Minions.*).
    private static readonly System.Collections.Generic.HashSet<System.IntPtr> _localAiSummons = new();
    internal static bool DeployedByLocal(Actor a) => a != null && _localAiSummons.Contains(a.Pointer);

    internal static bool InheritFrom(Actor spawned, Actor source)
    {
        if (!Active || spawned == null || source == null) return false;
        try
        {
            // Only inherit from a unit we actually own the question of. An enemy
            // summoner's minion must keep falling through to the normal path.
            if (!_owner.TryGetValue(source.Pointer, out int srcSeat)) return false;
            if (_owner.ContainsKey(spawned.Pointer)) return false;   // already known

            // ONLY A SUMMON THAT JOINS THE DEPLOYER'S FACTION IS A PLAYER UNIT (r61).
            // Skybots (the player "drones") land in the ALLIED AI faction, like
            // sentry turrets, and the game's AI flies them. Filed as a player
            // unit, the turn logic found "a player unit stranded in faction 2",
            // ended its turn every round -- the drone never acted (#4b) -- and
            // AutoCombat tried to give it first aid. Left to the AI it is driven
            // by the host and its hits are the host's, like any AI shooter.
            int sf = -1, df = -1;
            try { sf = spawned.GetFactionID(); df = source.GetFactionID(); } catch { }
            if (sf != df)
            {
                _aiSummons.Add(spawned.Pointer);   // and AdoptLatecomers must not take it either
                if (srcSeat == Peers.LocalSeat) _localAiSummons.Add(spawned.Pointer);
                bool sel = false;
                try { sel = spawned.IsSelectableByPlayer(); } catch { }
                Plugin.Logger.LogInfo($"[OWN] '{DesyncDetector.TemplateNameOf(spawned)}' deployed by "
                    + $"'{DesyncDetector.TemplateNameOf(source)}' joined faction {sf}, not {df} -- the AI drives it (selectable={sel})");
                return false;
            }

            _owner[spawned.Pointer] = srcSeat;
            bool mine = srcSeat == Peers.LocalSeat;
            Plugin.Logger.LogInfo(
                $"[OWN] '{DesyncDetector.TemplateNameOf(spawned)}' deployed by "
                + $"'{DesyncDetector.TemplateNameOf(source)}' -> {(mine ? "YOURS" : "PARTNER'S")} seat {srcSeat} "
                + $"(inherited, not adopted) -- now {LocalCount}/{TotalCount}");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[OWN] Could not inherit ownership for a deployed unit: {e.Message}");
            return false;
        }
    }

    // Assignment chosen by the host in the planning phase (deploy-list slot
    // indices owned by the GUEST). Null = fall back to alternating slots.
    internal static HashSet<int> PrepGuestSlots;
    // v5: the planning-phase choice as a seat per deploy slot (the prep panel's
    // player picker). Null = even split. Wins over PrepGuestSlots.
    internal static int[] PrepSeats;

    // The default split: slot by slot round the seats -- host, next seat, ...
    internal static int[] DefaultSeats(int slots)
    {
        var order = Peers.SessionSeats();
        // Harness scenario "spectator": the last seat gets no squad, so the
        // spectator path runs even on a mission with a squad per player.
        if (DebugSync.LocalOn && DebugSync.Has("spectator") && order.Count > 2)
            order = order.GetRange(0, order.Count - 1);
        var seats = new int[slots];
        for (int i = 0; i < slots; i++) seats[i] = order[i % order.Count];
        return seats;
    }

    // The battle whose squads were split at its start (with players present).
    private static IntPtr _startAssignedBattle;

    // A BATTLE THE HOST STARTED ALONE HAS NO SPLIT (AssignAtHostBattleStart returns
    // when nobody is connected), so a player joining it mid-battle got no squads
    // and the host kept commanding everything (hand test 2026-10-04 10:47). Split
    // evenly over everyone present now, on each join into such a battle, and tell
    // every machine. A battle split at its start keeps that split: a returning
    // player's units come back through the loan, not a re-split.
    internal static void AssignForLateJoin()
    {
        var tm = TacticalManager.Get();
        if (CoopNet.Mode != CoopMode.Host || tm == null) return;
        if (_startAssignedBattle == tm.Pointer) return;
        var leaders = TacticalState.Get()?.GetDeployedUnitLeaders();
        if (leaders == null || leaders.Count == 0) return;
        var order = Peers.SessionSeats();
        if (order.Count < 2) return;
        var seats = new int[leaders.Count];
        var guestSlots = new StringBuilder();
        for (int i = 0; i < leaders.Count; i++)
        {
            seats[i] = order[i % order.Count];
            if (seats[i] != Peers.HostSeat) { if (guestSlots.Length > 0) guestSlots.Append(','); guestSlots.Append(i); }
        }
        string seatCsv = string.Join(",", seats);
        BuildSets(SeatFn(seatCsv));
        _lastAppliedSlots = guestSlots.ToString();
        _lastSeats = seatCsv;
        CoopNet.Send(new CoopMsg { T = "own", Data = _lastAppliedSlots, Seats = seatCsv });
        Plugin.Logger.LogWarning($"[OWN] Battle started alone -- split its squads for the player(s) joining: seats [{seatCsv}] over [{string.Join(",", order)}]; host commands {LocalCount}/{TotalCount}");
        CoopNet.Status = $"You command {LocalCount} of {TotalCount} squads";
    }

    // Host side, at its own battle start.
    internal static void AssignAtHostBattleStart()
    {
        if (CoopNet.Mode != CoopMode.Host || !CoopNet.Connected) return;
        var leaders = TacticalState.Get()?.GetDeployedUnitLeaders();
        if (leaders == null || leaders.Count == 0)
        {
            Plugin.Logger.LogWarning("[OWN] No deployed unit leaders at battle start; ownership disabled this battle");
            return;
        }

        var chosen = PrepGuestSlots;
        PrepGuestSlots = null;
        var prepSeats = PrepSeats;
        PrepSeats = null;

        // SQUADS SPLIT EVENLY, deploy slot by slot round the seats (user 2026-10-02):
        // slot 0 host, slot 1 the next seat, ... At two players that is the old
        // odd/even split. A player beyond the squad count gets none -- a spectator.
        // The prep panel's per-squad choice is still the two-player toggle, so it
        // is honoured only at two players (Phase 3: a per-squad player picker).
        var order = Peers.SessionSeats();
        int n = order.Count;
        // The prep panel's per-squad picks, if they still fit this battle: same
        // squad count, and every seat named is still here.
        bool usePrep = prepSeats != null && prepSeats.Length == leaders.Count;
        if (usePrep) foreach (var ps in prepSeats) if (!order.Contains(ps)) { usePrep = false; break; }
        if (prepSeats != null && !usePrep)
            Plugin.Logger.LogInfo($"[OWN] Prep-panel picks no longer fit ({prepSeats.Length} picks, {leaders.Count} squads, seats [{string.Join(",", order)}]) -> even split");
        // A battle resume carries only the old two-player guest list.
        bool useChosen = !usePrep && chosen != null && n == 2;
        var guestSlots = new StringBuilder();
        var seats = new int[leaders.Count];
        for (int i = 0; i < leaders.Count; i++)
        {
            seats[i] = usePrep ? prepSeats[i]
                     : useChosen ? (chosen.Contains(i) ? order[1] : Peers.HostSeat)
                     : order[i % n];
            if (seats[i] != Peers.HostSeat)
            {
                if (guestSlots.Length > 0) guestSlots.Append(',');
                guestSlots.Append(i);
            }
        }
        foreach (var seat in order)
            if (Array.IndexOf(seats, seat) < 0)
                Plugin.Logger.LogInfo($"[OWN] {Peers.NameOf(seat)} (seat {seat}) gets no squad this battle -- SPECTATOR");
        string seatCsv = string.Join(",", seats);
        BuildSets(SeatFn(seatCsv));
        _startAssignedBattle = TacticalManager.Get()?.Pointer ?? IntPtr.Zero;
        _lastAppliedSlots = guestSlots.ToString();   // kept, so it can be rebuilt
        _lastSeats = seatCsv;
        // Data stays the guest-slot list so an older guest still reads it.
        CoopNet.Send(new CoopMsg { T = "own", Data = guestSlots.ToString(), Seats = seatCsv });
        Plugin.Logger.LogInfo($"[OWN] Host assignment ({(usePrep ? "prep-panel picks" : useChosen ? "host-chosen" : $"even split over {n}")}): {LocalCount}/{TotalCount} host units; guest slots [{guestSlots}]; seats [{seatCsv}]");
        CoopNet.Status = $"You command {LocalCount} of {TotalCount} squads";
        if (LocalCount == 0 || LocalCount == TotalCount)
            Plugin.Logger.LogWarning("[OWN] One player commands ZERO squads this battle (host-chosen assignment)");
    }

    // Guest side: assignment can arrive while still loading; stash and apply
    // once the guest's battle actually starts.
    internal static void HandleOwn(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        _pendingGuestSlots = m.Data ?? "";
        _pendingSeats = m.Seats;
        // ALREADY IN THE BATTLE: apply now. A re-split for a player joining mid-
        // battle (AssignForLateJoin) reaches the clients already playing; stashed
        // "for battle start" it never applied, so M3 kept the old split and showed
        // M2 as a SPECTATOR while the host and M2 had M2 commanding a squad (hand
        // test 2026-10-04 11:07).
        bool inBattle = TacticalManager.Get() != null && SessionSync.GuestBattleReady && !GuestReload.GuestCatchingUp;
        Plugin.Logger.LogInfo($"[OWN] Received ownership assignment (slots [{_pendingGuestSlots}], seats [{m.Seats ?? "-"}]); {(inBattle ? "applying now (mid-battle)" : "applying at battle start")}");
        if (inBattle) ApplyAtGuestBattleStart();
    }

    internal static void ApplyAtGuestBattleStart()
    {
        if (CoopNet.Mode != CoopMode.Guest || _pendingGuestSlots == null) return;
        _lastAppliedSlots = _pendingGuestSlots;      // kept, so it can be rebuilt
        _lastSeats = _pendingSeats;
        _pendingGuestSlots = null;
        _pendingSeats = null;
        BuildSets(StoredSeatFn());
        Plugin.Logger.LogInfo($"[OWN] Guest (seat {Peers.LocalSeat}) owns {LocalCount}/{TotalCount} units");
        CoopNet.Status = $"You command {LocalCount} of {TotalCount} squads";
    }

    // Seat of each deploy slot, from the per-slot list when the host sent one,
    // else from the guest-slot list (an older host): listed slots are the client's.
    private static Func<int, int> SeatFn(string seatCsv)
    {
        var seats = new List<int>();
        foreach (var s in seatCsv.Split(',', StringSplitOptions.RemoveEmptyEntries))
            seats.Add(int.TryParse(s, out int v) ? v : Peers.HostSeat);
        return i => i < seats.Count ? seats[i] : Peers.HostSeat;
    }

    private static Func<int, int> StoredSeatFn()
    {
        if (!string.IsNullOrEmpty(_lastSeats)) return SeatFn(_lastSeats);
        var slots = ParseSlots(_lastAppliedSlots);
        int client = CoopNet.Mode == CoopMode.Host ? Peers.LinkSeat : Peers.LocalSeat;
        return i => slots.Contains(i) ? client : Peers.HostSeat;
    }

    private static void BuildSets(Func<int, int> seatOfSlot)
    {
        Reset();
        var leaders = TacticalState.Get()?.GetDeployedUnitLeaders();
        if (leaders == null || leaders.Count == 0)
        {
            Plugin.Logger.LogWarning("[OWN] No deployed unit leaders; ownership disabled this battle");
            return;
        }
        for (int i = 0; i < leaders.Count; i++)
        {
            var leader = leaders[i];
            if (leader == null) continue;
            _owner[leader.Pointer] = seatOfSlot(i);
        }
        _active = _owner.Count > 0;
    }

    // Called every frame from CoopRunner: ownership only exists inside the
    // battle it was assigned for.
    // The assignment we last applied, KEPT rather than consumed. See below.
    private static string _lastAppliedSlots;
    private static string _lastSeats;   // per-slot seats; null from an older host
    // Host: re-sent to a guest being reloaded into the battle (GuestReload).
    internal static string LastGuestSlots => _lastAppliedSlots;
    internal static string LastSeats => _lastSeats;
    private static float _noBattleSince = -1f;

    internal static void UpdateLifecycle()
    {
        var tm = TacticalManager.Get();

        // LOSING OWNERSHIP MID-BATTLE LOCKS A PLAYER OUT COMPLETELY.
        //
        // Reported as "the guest can't shoot or do anything", and the trace says
        // it exactly: r=4 ctrl=me mine=0 theirs=0 ai=4 -- four player squads on
        // the board and not one of them recognised as a player unit, in a battle
        // that had logged "Guest owns 2/4 units" at the start. Ownership had been
        // reset and never rebuilt, because assignment only ever happened at
        // battle start.
        //
        // Everything downstream keys off IsKnownPlayerUnit, so an empty set does
        // not merely disable the selection gate -- it makes the turn machinery
        // skip those units entirely, so their turns can never be ended and the
        // player sits there unable to act. That is the cascade.
        //
        // Two causes, two guards. The reset used to fire the instant
        // TacticalManager.Get() returned null, which it does transiently during a
        // resync or a reload, so require the battle to be gone for a sustained
        // moment before believing it. And if we find ourselves in a battle with
        // no ownership but a remembered assignment, rebuild from it.
        if (tm == null)
        {
            if (_active)
            {
                if (_noBattleSince < 0f) _noBattleSince = UnityEngine.Time.realtimeSinceStartup;
                else if (UnityEngine.Time.realtimeSinceStartup - _noBattleSince > 3f)
                {
                    Reset();
                    _lastAppliedSlots = null;
                    _lastSeats = null;
                    _adopted = 0;
                    _noBattleSince = -1f;
                    Plugin.Logger.LogInfo("[OWN] Battle over; ownership reset");
                }
            }
            return;
        }

        _noBattleSince = -1f;

        if (!_active && CoopNet.Connected && _lastAppliedSlots != null)
        {
            BuildSets(StoredSeatFn());
            if (_active)
                Plugin.Logger.LogWarning($"[OWN] Ownership had been lost mid-battle -- rebuilt from the stored "
                    + $"assignment [{_lastAppliedSlots}] seats [{_lastSeats ?? "-"}]: {LocalCount}/{TotalCount} units");
        }

        AdoptLatecomers();
    }

    private static HashSet<int> ParseSlots(string csv)
    {
        var slots = new HashSet<int>();
        if (string.IsNullOrEmpty(csv)) return slots;
        foreach (var s in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(s, out int v)) slots.Add(v);
        return slots;
    }

    private static int _adopted;

    // UNITS THAT JOIN THE PLAYER SIDE AFTER DEPLOYMENT HAVE NO OWNER.
    //
    // BuildSets runs once, at battle start, over GetDeployedUnitLeaders(). Any
    // unit that becomes player-controllable LATER is in neither set -- so
    // IsKnownPlayerUnit is false for it, and the selection gate
    // (GateSelectionByOwnership) only ever blocks units it knows about. The
    // result is a unit BOTH players can select and order at the same time,
    // which is the reported hostage-rescue desync: the escorted VIP is exactly
    // such a unit, and two machines issuing orders for the same actor is the
    // worst case this mod has -- it is not a missed order, it is two conflicting
    // ones applied in different sequences on each side.
    //
    // Adoption fixes it without any wire traffic, which matters because a
    // mid-battle assignment message would race the first order. Both machines
    // apply the same rule independently -- the HOST owns anything that arrives
    // unassigned -- so they agree by construction, with no round trip to get
    // wrong. Host authority is already the tie-break everywhere else here.
    //
    // Adding them to the owner map is what makes the rest of the mod treat them
    // properly: turn accounting, the takeover detector and the desync healer all
    // key off IsKnownPlayerUnit, and all of them were silently skipping the VIP.
    private static float _nextAdoptScan;

    private static void AdoptLatecomers()
    {
        if (!Active) return;
        var tm = TacticalManager.Get();
        if (tm == null) return;
        // Twice a second. A unit becoming controllable is a mission-script event,
        // not a per-frame one, and the alternative is walking every actor on the
        // board 60 times a second for the whole battle.
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now < _nextAdoptScan) return;
        _nextAdoptScan = now + 0.5f;

        try
        {
            bool host = CoopNet.Mode == CoopMode.Host;

            // NOT just the player faction. A rescued VIP or hostage can be
            // player-CONTROLLABLE while the engine still files them under a
            // civilian faction -- and faction membership is not a reliable test
            // of "is this ours" in this engine anyway (a downed unit gets moved
            // out of the player faction entirely). The engine's own answer to
            // "can this player drive it" is IsSelectableByPlayer, so ask that.
            // Our own postfix on it is a no-op for a unit we do not yet know, so
            // reading it here returns the engine's unmodified verdict.
            var all = DesyncDetector.AllActors(tm);
            int playerFac = -1;
            try { playerFac = tm.GetPlayerFaction()?.GetIndex() ?? -1; } catch { }

            for (int i = 0; i < all.Length; i++)
            {
                var a = all[i];
                if (a == null) continue;
                var ptr = a.Pointer;
                if (_owner.ContainsKey(ptr) || _aiSummons.Contains(ptr)) continue;

                bool ours = false;
                try { ours = playerFac >= 0 && a.GetFactionID() == playerFac; } catch { }
                if (!ours) { try { ours = a.IsSelectableByPlayer(); } catch { } }
                if (!ours) continue;

                _owner[ptr] = Peers.HostSeat;
                _adopted++;
                Plugin.Logger.LogInfo(
                    $"[OWN] Adopted late player unit '{DesyncDetector.TemplateNameOf(a)}' -> "
                    + (host ? "HOST (yours)" : "HOST (partner's)") + $" -- now {LocalCount}/{TotalCount}");
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[OWN] Adoption pass failed: {e.Message}"); }
    }
}
