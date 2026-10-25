using System;
#if MELON
using Il2CppMenace.Tactical;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.Tactical;
#endif

namespace MenaceCoop;

// LAST-LINE RECOVERY FOR A BATTLE THAT HAS STOPPED PROGRESSING.
//
// Every other mechanism in the mod prevents a specific deadlock class. This one
// assumes we missed a class -- because we repeatedly did. Each fixed deadlock
// (stranded on an AI faction, stranded on the player faction, phantom units in
// a foreign faction, an unfinished unit at phase end, one activation out of
// step, downed units holding a faction, a whole-faction End Turn finishing the
// partner's units) looked completely different from the front, but from the
// outside they were identical: the board simply stopped changing while both
// processes stayed healthy and kept logging.
//
// So watch that one symptom directly. If nothing about the board has changed
// for a while, escalate -- the host re-pushes authoritative state, the guest
// asks for it. That is the same thing the FIX DESYNC button does, which has
// repeatedly recovered these stalls by hand; this just does it automatically.
//
// Deliberately conservative: the fingerprint changes on any move, any turn-end,
// any faction or round change, so normal play resets it constantly. Only a
// genuinely frozen board survives the full delay.
internal static class BattleWatchdog
{
    // HOW LONG A STILL BOARD IS TOLERATED BEFORE RECOVERY IS ATTEMPTED.
    //
    // This was 45s and it was too short, reported from play. Two separate things
    // were wrong with that number and only one of them was the number itself.
    //
    // The first was that the comment here used to claim 45s was long enough that a
    // thinking human would never be mistaken for a stall. That was untrue, and it
    // is now handled properly: deliberation is excluded by STATE, not by picking a
    // bigger delay (see the guard in Tick).
    //
    // The second is that even as a pure backstop the window was mean. Everything
    // it can now fire on is a genuine fault, and a genuine fault is not made worse
    // by waiting a little longer -- whereas interrupting a real game is immediate
    // and visible. So it is generous by default, and configurable, so nobody has
    // to wait on a rebuild to tune it.
    private static float StallSeconds
    {
        get
        {
            float v = Plugin.StallRecoverySeconds != null ? Plugin.StallRecoverySeconds.Value : 180f;
            return v < 30f ? 30f : v;   // a floor, so a stray 0 cannot make it fire constantly
        }
    }

    // Escalations spaced so a recovery has time to land before the next attempt.
    private static float RetrySeconds => StallSeconds < 60f ? 30f : 45f;

    private static int _fingerprint;
    private static float _changedAt;
    private static float _lastEscalation;
    private static int _escalations;

    internal static void Reset()
    {
        _fingerprint = 0;
        _changedAt = 0f;
        _lastEscalation = 0f;
        _escalations = 0;
    }

    // Everything that legitimately changes during play. Positions and turn-done
    // flags are the two that move on essentially every action.
    private static int Fingerprint(TacticalManager tm)
    {
        unchecked
        {
            int h = 17;
            try { h = h * 31 + tm.GetRound(); } catch { }
            try { h = h * 31 + (tm.GetActiveFaction()?.GetIndex() ?? -1); } catch { }
            try
            {
                var f = tm.GetPlayerFaction();
                var actors = f?.GetActors();
                for (int i = 0; actors != null && i < actors.Count; i++)
                {
                    var a = actors[i];
                    if (a == null) continue;
                    try
                    {
                        var p = a.GetLastTile()?.GetTilePos();
                        if (p != null) h = h * 31 + p.Value.x * 733 + p.Value.y;
                        h = h * 31 + (a.IsTurnDone() ? 1 : 0);
                        h = h * 31 + a.GetActionPoints();
                    }
                    catch { }
                }
            }
            catch { }
            return h;
        }
    }

    private static bool ResultScreenUp()
    {
        try
        {
            return Menace.UI.UIManager.Get()?.GetActiveScreen()
                ?.TryCast<Menace.UI.MissionResult.MissionResultUIScreen>() != null;
        }
        catch { return false; }
    }

    internal static void Tick()
    {
        if (!CoopNet.Connected) { Reset(); return; }

        TacticalManager tm;
        try { tm = TacticalManager.Get(); } catch { return; }
        if (tm == null) { Reset(); return; }

        float now = UnityEngine.Time.realtimeSinceStartup;

        // A DELIBERATE HOLD IS NOT A STALL.
        //
        // While we are waiting for the partner to enter the battle, or a sync
        // pause is in force, the board is SUPPOSED to be still -- firing here
        // would "recover" from working-as-intended and, worse, resync on top of
        // a partner who is still loading. Observed immediately: the watchdog
        // tripped at round 1 purely because the entry gate was doing its job.
        // A FINISHED MISSION IS NOT A STALL EITHER. Both players sat on the result
        // screen while the battle object still existed; after 180s the watchdog
        // declared the board stuck and opened a SYNC PAUSE with a hard resync --
        // over a battle that was already over, on the screen people stop to read.
        if (!SessionSync.PartnerBattleConfirmed
            || SessionSync.HostHoldFire
            || SessionSync.GuestEnteringBattle
            || Replication.SyncPauseActive
            || Replication.PartnerMissionOver
            || ResultScreenUp())
        {
            _changedAt = now;
            _escalations = 0;
            return;
        }

        // An animation in flight IS progress, even though the fingerprint has
        // not landed yet -- treating it as a stall would fire mid-move.
        bool moving = false;
        try { moving = TacticalManager.IsAnyActorMoving(); } catch { }

        int fp = Fingerprint(tm);
        if (fp != _fingerprint || moving)
        {
            _fingerprint = fp;
            _changedAt = now;
            _escalations = 0;
            return;
        }

        if (_changedAt <= 0f) { _changedAt = now; return; }

        // A PERSON THINKING IS NOT A STALLED BOARD.
        //
        // This was reported from play: someone stopped to consider a move, and the
        // board was resynced and the controller handed to their partner out from
        // under them. The fingerprint is built from moves, turn-ends and faction or
        // round changes -- so NOTHING a player does while deciding touches it.
        // Selecting a unit, panning the camera, opening the ability panel, weighing
        // two options: all of it leaves the fingerprint identical, and after 45
        // seconds this called it a frozen game and escalated.
        //
        // The distinction that actually matters is not how long it has been, but
        // whether the player holding control still HAS someone they could move. If
        // they do, the board is waiting on a human and that is not a fault -- so the
        // clock is held rather than merely reset, and no amount of thinking will
        // ever trip a recovery.
        //
        // A genuine lock-up still escalates: the starvation case that this exists to
        // catch is precisely one where the holder has nothing left to act with while
        // their partner is stuck waiting, and that reads false here.
        if (TurnControl.HolderCanStillAct(tm))
        {
            _changedAt = now;
            _escalations = 0;
            return;
        }

        if (now - _changedAt < StallSeconds) return;
        if (now - _lastEscalation < RetrySeconds) return;

        _lastEscalation = now;
        _escalations++;

        float stuckFor = now - _changedAt;
        Plugin.Logger.LogWarning(
            $"[WATCHDOG] Board unchanged for {stuckFor:F0}s -- recovering (attempt {_escalations}). " +
            $"round={SafeRound(tm)} faction={SafeFaction(tm)}");

        try
        {
            // FIRST ATTEMPT: assume the boards disagree, and reconcile them.
            if (_escalations == 1)
            {
                // SOFT only. This used to push a HARD restore (host) or start a
                // sync pause (guest), and a hard restore overwrites the guest's own
                // units and kills with the host's view of them -- players saw
                // enemies come back to life and their squads jump back to the
                // battle-start tiles right after a stalled guest turn (v4.3.0).
                // See RequestResync: nothing automatic applies a hard restore.
                DesyncDetector.RequestResync("watchdog: board stopped progressing", allowEscalation: false);
                CoopRunner.ShowDesyncWarning("Recovering from a stalled board...");
            }
            else
            {
                // LATER ATTEMPTS: a restore that reported zero corrections proved
                // the two boards are IDENTICAL, so this was never drift -- nobody
                // believes they may act. Seen live: the host sat on ctrl=them
                // waiting for the guest while the guest did not think it was the
                // player phase. Syncing state again cannot break that; handing
                // over the controller can.
                TurnControl.ForceRecoverControl();
                CoopRunner.ShowDesyncWarning("Breaking a stalled turn -- handing over control...");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[WATCHDOG] Recovery attempt failed: {e.Message}");
        }

        // Say so plainly if recovery is not working, rather than retrying in
        // silence -- a watchdog that never admits defeat hides the real bug.
        if (_escalations >= 3)
            Plugin.Logger.LogError(
                $"[WATCHDOG] Board STILL unchanged after {_escalations} recovery attempts " +
                $"({stuckFor:F0}s) -- this is an unhandled deadlock class, not drift.");
    }

    private static string SafeRound(TacticalManager tm)
    { try { return tm.GetRound().ToString(); } catch { return "?"; } }

    private static string SafeFaction(TacticalManager tm)
    { try { return (tm.GetActiveFaction()?.GetIndex() ?? -1).ToString(); } catch { return "?"; } }
}
