using System;
using HarmonyLib;
using Il2CppMenace.Tactical;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling

namespace MenaceCoop;

// TURN CONTROL â€” one human acts per player activation.
//
// The engine does NOT run "all player units, then all enemies". It uses
// ALTERNATING ACTIVATIONS: the player faction gets an activation (one unit
// acts), then an AI faction gets one, then the player again. The earlier
// design fought that -- it made each player finish EVERY one of their units
// before the other could play, which the engine has no notion of, and that
// mismatch produced the mutual waits and freezes.
//
// So control is granted per ACTIVATION instead: while the player faction is
// active, exactly one human may issue orders; when their unit finishes,
// control passes to the other human if they still have someone to move. The
// engine's own cycle is never touched -- it hands out activations exactly as
// it always did, and we only decide who is holding the controller.
//
// The host is the authority: it decides who holds control and tells the guest,
// so both machines can never disagree about it.
internal static class TurnControl
{
    internal static bool Enabled => Plugin.SequentialPhases.Value;

    // THE SEAT HOLDING THE CONTROLLER (v5: was a host/guest bool). Host is
    // Player 1 = seat 0, guest Player 2 = seat 1 -- fixed so all screens agree.
    // Only the host ever chooses it; clients only hear it ("ctrl").
    private static int _holder = Peers.HostSeat;
    private static int _lastRound = -1;

    // When the current holder took control -- drives the anti-starvation failsafe.
    private static float _controlHeldSince;

    // Last time the side holding control actually did something -- a local order,
    // or a relayed one arriving from the partner. Kept because the watchdog reads
    // it, but it no longer decides whose turn it is.
    internal static float LastHolderActivity;

    internal static void NoteHolderActivity() => LastHolderActivity = UnityEngine.Time.realtimeSinceStartup;

    internal static bool Active
    {
        get
        {
            if (!Enabled || !CoopNet.Connected) return false;
            // The battle scene (and its TacticalManager) is still loaded under
            // the mission result screen, so without this the green "YOUR TURN"
            // banner stayed up over the results (r88 screenshot).
            if (SessionSync.ResultScreenOpen) return false;
            var tm = TacticalManager.Get();
            try
            {
                if (tm == null || !tm.IsPlayerTurn()) return false;

                // THE LOCAL CURSOR IS NOT THE AUTHORITY ON WHOSE TURN IT IS.
                //
                // The guest's cursor can still say "player phase" while the host
                // is running its enemies -- so the banner sat on "YOUR TURN --
                // PLAYER 2" while aliens were visibly acting and the host was
                // overriding that player's turn. The host's phase decides.
                if (CoopNet.Mode == CoopMode.Guest
                    && DesyncDetector.HostPhaseKnown && !DesyncDetector.HostIsInPlayerPhase)
                    return false;

                return true;
            }
            catch { return false; }
        }
    }

    internal static bool IsMyTurn => !Active || _holder == Peers.LocalSeat;
    internal static int Holder => _holder;

    internal static string StatusText
    {
        get
        {
            if (!Active) return null;
            // The holder's display name, or "PLAYER n" when they set none.
            string who = Peers.NameOf(_holder).ToUpperInvariant();
            return IsMyTurn ? $"YOUR TURN  --  {who}" : $"{who} IS ACTING  --  PLEASE WAIT";
        }
    }

    internal static void Reset()
    {
        _holder = Peers.HostSeat; _lastRound = -1; _controlHeldSince = 0f;
        _passRequestedAt = -1f; _passRetries = 0;
    }

    // Does that seat still have a unit able to act?
    private static bool HasUnitsLeft(TacticalManager tm, int seat)
    {
        try
        {
            if (!Ownership.Active) return true;      // unknown -- assume yes

            // MUST look at the PLAYER faction, not the active one.
            //
            // The active faction is whatever the engine's cursor is on -- often
            // an AI faction, and sometimes faction 2 where downed units live. In
            // those moments this found no player units on EITHER side, so control
            // was never handed over: the host kept it, and the guest sat unable
            // to move its squad all battle (121 host actions to 6 guest ones).
            var actors = (tm.GetPlayerFaction() ?? tm.GetActiveFaction())?.GetActors();
            if (actors == null) return false;
            for (int i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || a.IsDying() || a.IsTurnDone()) continue;
                if (!Ownership.IsKnownPlayerUnit(a)) continue;
                // A FLEEING SQUAD CANNOT BE COMMANDED (player report 2026-10-20:
                // Carda fleeing and uncontrollable, the game waited on them). It
                // used to be skipped here, but in the player faction the engine
                // still waits on it, so nobody got control and the phase never
                // ended (r157 round 6 hang). It counts: control goes to its owner,
                // whose SettleIncapacitatedUnits ends its turn through the engine.
                if (Ownership.OwnerOf(a) == seat) return true;
            }
        }
        catch { }
        return false;
    }

    // IS THE PLAYER HOLDING CONTROL STILL ABLE TO MOVE SOMEONE?
    //
    // The watchdog needs this to tell a person thinking from a board that has
    // genuinely locked up. Both machines can answer it without a new message:
    // control assignment is mirrored and unit state is replicated, so host and
    // guest reach the same verdict independently.
    internal static bool HolderCanStillAct(TacticalManager tm)
    {
        if (!Enabled || !CoopNet.Connected || tm == null) return false;
        try { return HasUnitsLeftFor(tm, _holder); }
        catch { return false; }
    }

    private static bool HolderMidActivation(TacticalManager tm)
    {
        try
        {
            var a = tm.GetActiveActor();
            if (a == null || !Ownership.Active || !Ownership.IsKnownPlayerUnit(a)) return false;
            return Ownership.OwnerOf(a) == _holder && a.HasActed() && !a.IsTurnDone();
        }
        catch { return false; }
    }

    private static bool HasUnitsLeftFor(TacticalManager tm, int seat)
    {
        bool mine = seat == Peers.LocalSeat;

        // A PARTNER WHO IS NOT IN THE BATTLE YET CANNOT ACT.
        //
        // The host can see the guest's units in its own roster from the moment the
        // battle starts, so "they still have units to move" was true well before
        // the guest had finished loading -- and control was handed to a player who
        // could not possibly use it. The host then sat holding two of its own units
        // and did nothing while the enemy shot at it, which is exactly what the
        // user described.
        if (!mine && CoopNet.Connected && !SessionSync.PartnerBattleConfirmed && !Peers.IsReady(seat)) return false;
        // v5: a player who has left holds no turn (their squads wait for a rejoin).
        if (!mine && !Peers.IsConnected(seat)) return false;

        return HasUnitsLeft(tm, seat);
    }

    // THE TURN ORDER: seat after seat, host first. Exists only on the host --
    // clients never compute a next holder, they are told (DeveloperDocs/10-N-PLAYER-DESIGN
    // §4 Phase 0 item 3), so they cannot disagree about it.
    private static System.Collections.Generic.List<int> Rotation() => Peers.SessionSeats();

    // The next seat after the holder that still has a unit to move, or -1.
    private static int NextWithUnits(TacticalManager tm)
    {
        var order = Rotation();
        int at = order.IndexOf(_holder);
        for (int k = 1; k < order.Count; k++)
        {
            int seat = order[((at < 0 ? 0 : at) + k) % order.Count];
            if (seat != _holder && HasUnitsLeftFor(tm, seat)) return seat;
        }
        return -1;
    }

    // The seat straight after the holder, units or not (an explicit pass).
    private static int NextInOrder()
    {
        var order = Rotation();
        int at = order.IndexOf(_holder);
        return order.Count == 0 ? Peers.HostSeat : order[((at < 0 ? -1 : at) + 1) % order.Count];
    }

    internal static void Tick()
    {
        if (!Enabled || !CoopNet.Connected) return;
        UpdatePassRequest();
        var tm = TacticalManager.Get();
        if (tm == null) { _lastRound = -1; return; }
        int round;
        try { round = tm.GetRound(); } catch { return; }

        // New round: the lead alternates so neither player always opens.
        //
        // But NEVER reassign control away from a player who still has units to
        // move. The host can be several rounds ahead of the guest, and every
        // round change was re-keying control by the HOST's round parity --
        // repeatedly snatching it back before the guest could use it. The guest
        // ended up holding one unfinished unit at round 1 while the host reached
        // round 15, with "Order NOT broadcast -- turn control is with the
        // partner" every time it tried.
        if (round != _lastRound)
        {
            _lastRound = round;
            if (CoopNet.Mode == CoopMode.Host)
            {
                // The lead rotates through the seats: round 1 the host, round 2
                // the next seat, ... -- at two players, odd rounds host, even guest.
                var order = Rotation();
                int lead = order.Count == 0 ? 0 : ((round - 1) % order.Count + order.Count) % order.Count;
                int wanted = order.Count == 0 ? Peers.HostSeat : order[lead];
                // A spectator (no squad) never opens: next seat with units.
                for (int k = 1; k < order.Count && !HasUnitsLeftFor(tm, wanted); k++)
                    wanted = order[(lead + k) % order.Count];
                // "Still has units" is true for EVERYONE at a round start (all
                // refreshed), so that test kept the last actor of each round as
                // the next one's opener -- r96 (4p): P1 opened all six rounds and
                // acted twice running at every boundary. Keep the holder only
                // when they are mid-activation: their active unit has acted.
                if (wanted != _holder && HolderMidActivation(tm))
                {
                    Plugin.Logger.LogInfo($"[TURN] Round {round}: keeping control with Player {_holder + 1} -- they still have units to move");
                }
                else
                {
                    _holder = wanted;
                    _controlHeldSince = UnityEngine.Time.realtimeSinceStartup;
                    Broadcast();
                    Plugin.Logger.LogInfo($"[TURN] Round {round}: Player {_holder + 1} acts first");
                }
            }
        }

        // HAND-OVER MUST NOT DEPEND ON THE LOCAL CURSOR.
        //
        // These checks used to require `Active`, which is true only while THIS
        // machine's cursor sits on the player faction. When the host's cursor was
        // on faction 2 or 7 it never evaluated hand-over at all, so control could
        // never leave it -- while the guest, whose cursor WAS on the player
        // faction, sat blocked on "ctrl=them" for the whole battle (121 host
        // actions to 6 guest ones).
        //
        // Control is about which PLAYER may act, not about where the engine's
        // cursor happens to be, so it is decided whenever we are in a battle.
        if (CoopNet.Mode != CoopMode.Host) return;

        if (!HasUnitsLeftFor(tm, _holder))
        {
            int next = NextWithUnits(tm);
            if (next >= 0) { PassControl("holder has no units left", next); return; }
        }

        // NO TIMER DECIDES WHOSE TURN IT IS.
        //
        // A failsafe used to sit here that passed control once the holder had held
        // it for 90s with 20s of no ORDERS. It was already raised to those numbers
        // from 8s for exactly the reason it is now gone entirely: it kept firing on
        // people who were simply thinking. Deliberating is not idling, and this
        // could not tell the two apart, because the only thing it counted as
        // activity was issuing an order -- selecting a unit, panning the camera or
        // comparing two moves were all invisible to it.
        //
        // The rule above ("holder has no units left") is the real handover, and it
        // reads BOARD STATE rather than elapsed time: the moment a player has
        // nothing left to move, their partner gets control. A player who still has
        // units keeps the turn for as long as they want it. Taking the turn off
        // someone mid-thought is a worse failure than making their partner wait,
        // and PASS TURN is always there for anyone who wants to hand over early.
        _controlHeldSince = UnityEngine.Time.realtimeSinceStartup;
    }

    // HOST ONLY: a player unit finished, so the other human gets the next
    // activation -- provided they have someone to move.
    internal static void OnPlayerUnitFinished(TacticalManager tm)
    {
        if (!Enabled || CoopNet.Mode != CoopMode.Host || !CoopNet.Connected) return;
        int next = NextWithUnits(tm);
        if (next >= 0) PassControl("activation finished", next);
    }

    // WATCHDOG ESCALATION: break a pure control deadlock.
    //
    // When the board has frozen but a hard resync reports zero corrections, the
    // two machines hold identical state -- so it is not drift, it is that
    // NEITHER side believes it may act (host sitting on ctrl=them while the
    // guest does not think it is the player phase). No amount of state syncing
    // fixes that; someone has to be handed the controller.
    //
    // Host-only, because control assignment is the host's to make.
    internal static void ForceRecoverControl()
    {
        if (!Enabled || CoopNet.Mode != CoopMode.Host || !CoopNet.Connected) return;
        var tm = TacticalManager.Get();
        if (tm == null) return;

        // Give it to a side that actually has something to move; prefer the side
        // that is NOT holding it now, since the current holder has demonstrably
        // failed to use it.
        int other = NextWithUnits(tm);
        if (other >= 0) { PassControl("watchdog: control deadlock", other); return; }
        if (HasUnitsLeftFor(tm, _holder))
        {
            // Holder does have units but is not acting -- re-broadcast so a lost
            // 'ctrl' message cannot be what is stranding them.
            _controlHeldSince = UnityEngine.Time.realtimeSinceStartup;
            Broadcast();
            Plugin.Logger.LogWarning($"[TURN] watchdog: re-broadcast control to Player {_holder + 1} (deadlock, holder still has units)");
            return;
        }

        // Nobody has units left, yet the phase never ended -- that is what the
        // phase-end check exists for, so run it rather than sitting here.
        Plugin.Logger.LogWarning("[TURN] watchdog: no side has units left but the phase did not end -- forcing the phase-end check");
        Replication.CheckPlayerPhaseEnd();
    }

    // EXPLICIT HANDOVER -- the player says "I'm done", no inference required.
    //
    // Control normally passes on the ACTIVATION-FINISHED event; the timer is only a
    // backstop for when that event never arrives. A button removes the guesswork
    // entirely for the case the player knows best: they have finished thinking.
    //
    // The host owns control assignment, so the guest asks and the host performs it.
    internal static void RequestPassTurn()
    {
        if (!Enabled || !CoopNet.Connected) return;
        if (!IsMyTurn)
        {
            CoopNet.Status = "It is not your turn to pass";
            return;
        }
        if (CoopNet.Mode == CoopMode.Host)
        {
            PassControl("host passed the turn", PassTarget());
            Replication.NoteHostPassedTurn();
            return;
        }

        // GUEST: this used to be fire-and-forget, and that is why "I pressed end
        // turn and nothing happened, I had to reconnect" was a recurring report.
        // One dropped 'passreq' -- or a host that momentarily disagreed about who
        // held control -- ended the exchange permanently: no ack, no retry, no
        // log line, and a guest waiting on a reply that would never come.
        _passRequestedAt = UnityEngine.Time.realtimeSinceStartup;
        _passRetries = 0;
        CoopNet.Send(new CoopMsg { T = "passreq" });
        Plugin.Logger.LogInfo("[TURN] Pass-turn requested from host");
        CoopNet.Status = "Passing turn to your partner...";
    }

    // Guest: an unanswered pass request, retried rather than abandoned. Control
    // changing is the ack -- HandleRemoteControl clears this.
    private static float _passRequestedAt = -1f;
    private static int _passRetries;
    private const float PassRetryAfter = 2.5f;
    private const int PassMaxRetries = 4;

    private static void UpdatePassRequest()
    {
        if (_passRequestedAt < 0f || CoopNet.Mode != CoopMode.Guest) return;
        // Phase ended under us, or control moved: either way the request is done.
        if (!Active || !IsMyTurn) { _passRequestedAt = -1f; return; }
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now - _passRequestedAt < PassRetryAfter) return;

        if (_passRetries >= PassMaxRetries)
        {
            _passRequestedAt = -1f;
            Plugin.Logger.LogError("[TURN] Pass-turn was never acknowledged after "
                + PassMaxRetries + " attempts -- the host is not accepting the handover.");
            CoopNet.Status = "Pass turn not acknowledged -- press it again or use FIX DESYNC";
            CoopRunner.ShowDesyncWarning(
                "Your END TURN was not acknowledged by the host. Press it again; if it keeps failing, use FIX DESYNC.");
            return;
        }
        _passRetries++;
        _passRequestedAt = now;
        CoopNet.Send(new CoopMsg { T = "passreq" });
        Plugin.Logger.LogWarning($"[TURN] Pass-turn unanswered -- retry {_passRetries}/{PassMaxRetries}");
    }

    // HOST: a client asked to hand the turn on.
    internal static void HandlePassRequest(CoopMsg m)
    {
        if (!Enabled || CoopNet.Mode != CoopMode.Host) return;
        int from = m != null && m.From >= 0 ? m.From : Peers.LinkSeat;
        if (_holder != from)
        {
            // NEVER SILENTLY DROP THIS. The old code returned here on the
            // grounds that control was "not theirs to pass" -- but the guest
            // only sends this because it believes it HOLDS control, so reaching
            // this line means the two sides disagree about who is acting. That
            // is precisely the state the guest cannot escape on its own, and
            // staying quiet left reconnecting as the only way out. Re-broadcast
            // instead: the guest's view corrects itself and play continues.
            Plugin.Logger.LogWarning($"[TURN] Player {from + 1} asked to pass but Player {_holder + 1} holds control "
                + "-- control state disagreed; re-broadcasting to resync them");
            Broadcast();
            return;
        }
        PassControl("partner passed the turn", PassTarget());
    }

    // An explicit pass goes to the next seat with something to move; if nobody
    // has, to the next seat anyway (at two players: always the partner, as before).
    private static int PassTarget()
    {
        var tm = TacticalManager.Get();
        int next = tm != null ? NextWithUnits(tm) : -1;
        return next >= 0 ? next : NextInOrder();
    }

    private static void PassControl(string why, int to)
    {
        _holder = to;
        _controlHeldSince = UnityEngine.Time.realtimeSinceStartup;
        Broadcast();
        Plugin.Logger.LogInfo($"[TURN] Control -> Player {_holder + 1} ({why})");
        CoopRunner.ShowPhaseChange(StatusText);
    }

    // Prm is the PLAYER NUMBER (seat + 1): 1 host, 2 guest -- what older builds send.
    private static void Broadcast()
        => CoopNet.Send(new CoopMsg { T = "ctrl", Prm = _holder + 1 });

    internal static void HandleRemoteControl(CoopMsg m)
    {
        if (!Enabled || CoopNet.Mode == CoopMode.Host) return;
        int now = m.Prm - 1;
        // The ack for a pending pass request -- including the corrective
        // re-broadcast the host sends when the two sides disagreed.
        _passRequestedAt = -1f;
        if (now < 0 || now >= Peers.MaxSeats) { Plugin.Logger.LogWarning($"[TURN] ctrl names impossible player {m.Prm}; ignored"); return; }
        if (now == _holder) return;
        _holder = now;
        Plugin.Logger.LogInfo($"[TURN] Control -> Player {m.Prm} (host)");
        CoopRunner.ShowPhaseChange(StatusText);
    }
}

[HarmonyPatch]
internal static class TurnControlPatches
{
    // Any player unit finishing its turn ends that activation -- whether it was
    // ended explicitly, by the unit running out of things to do, or by the
    // partner's relayed turn-end. The host re-decides who acts next.
    [HarmonyPatch(typeof(Actor), nameof(Actor.SetTurnDone))]
    [HarmonyPostfix]
    private static void OnTurnDone(Actor __instance, bool _turnDone)
    {
        if (!_turnDone || CoopNet.Mode != CoopMode.Host) return;
        try
        {
            var tm = TacticalManager.Get();
            if (tm == null || !tm.IsPlayerTurn()) return;
            if (!Ownership.Active || !Ownership.IsKnownPlayerUnit(__instance)) return;
            TurnControl.OnPlayerUnitFinished(tm);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[TURN] control handover failed: {e.Message}"); }
    }
}

