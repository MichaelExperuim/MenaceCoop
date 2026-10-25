using System;
using System.IO;
using HarmonyLib;
#if MELON
using Il2CppMenace.States;
using Il2CppMenace.Strategy;
using Il2CppMenace.Tactical;
using Il2CppMenace.UI;
using Il2CppMenace.UI.Strategy;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.States;
using Menace.Strategy;
using Menace.Tactical;
using Menace.UI;
using Menace.UI.Strategy;
#endif
using UnityEngine;
using IOPath = System.IO.Path;

namespace MenaceCoop;

// Full-state catch-up for late joins: the host saves its game through the
// game's own save system, ships the file over the wire, and the guest
// auto-loads it. Triggered automatically when a partner connects while the
// host has a campaign loaded, or manually via the panel button.
internal static class SessionSync
{
    private const string SyncSaveFile = "coop_sync.save";

    private static string _pendingPath;
    private static float _pendingSince;
    private static long _lastSize = -1;
    private static float _lastCheck;

    // Incoming load requests are queued newest-wins and executed only when no
    // other load can still be in flight -- two overlapping ExecLoads abort the
    // scene transition and dump the game to the main menu.
    private static string _queuedLoad;
    private static float _loadCooldownUntil;
    private static float _loadQueuedAt;

    // True once the on-screen blocker has been released for a load that is still
    // waiting. Re-armed whenever a new load is queued.
    private static bool _loadBlockerReleased;
    private static bool _resultCloseTried;

    // True only while the mod itself invokes ExecLoad; the guest-side load
    // block exempts these.
    internal static bool RemoteLoadInProgress;

    // True only while the mod's auto-launch invokes LaunchMission; the guest
    // manual-launch block exempts these (the guest must follow the host, not
    // launch a mission itself).
    internal static bool ModLaunching;

    // Mission rendezvous: host captures mission idx+seed at launch and sends
    // them after the battle save; guest auto-starts the same mission once its
    // sync load has settled.
    private static int _hostMissionIdx = -1;
    private static int _hostMissionSeed;
    private static string _hostDeploy = "";
    private static int _queuedMissionIdx = -1;
    private static float _queuedMissionAt = -999f;
    private const float QueuedMissionDeadline = 30f;
    private static bool _missionLookupLogged;

    private const string PrepScene = "MissionPreparation";
    private static bool _prepSceneRequested;
    private static int _prepSceneLoadedFrame = -1;

    // True once the prep scene is loaded and at least two frames have run since
    // (Unity starts a loaded scene's components before their first Update).
    private static bool PrepSceneReady()
    {
        // GetSceneByName is unusable through Il2CppInterop (r115: MissingMethodException
        // on ReadOnlySpan) -- walk the loaded scenes instead.
        bool loaded = false;
        try
        {
            int n = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int i = 0; i < n && !loaded; i++)
            {
                var s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                loaded = s.isLoaded && s.name == PrepScene;
            }
        }
        catch (Exception e)
        {
            // Never block a launch on the probe itself: fall back to opening now.
            Plugin.Logger.LogWarning($"[SESSION] Prep scene check unavailable ({e.GetType().Name}) -- opening prep directly");
            return true;
        }
        if (loaded)
        {
            if (_prepSceneLoadedFrame < 0) _prepSceneLoadedFrame = Time.frameCount;
            return Time.frameCount > _prepSceneLoadedFrame + 1;
        }
        _prepSceneLoadedFrame = -1;
        if (!_prepSceneRequested)
        {
            _prepSceneRequested = true;
            Plugin.Logger.LogInfo("[SESSION] Loading the mission prep scene before opening prep (cold start)");
            try { Menace.Tools.SceneManagerHelper.EnsureSceneIsLoaded(PrepScene, null, null); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[SESSION] Prep scene load threw: {e.Message}"); }
        }
        return false;
    }

    private static bool OperationMissionsLoaded()
    {
        try
        {
            var missions = StrategyState.Get()?.Operations?.GetCurrentOperation()?.GetMissions()
                ?.TryCast<Il2CppSystem.Collections.Generic.List<Mission>>();
            return missions != null && missions.Count > 0;
        }
        catch { return false; }
    }
    private static int _queuedMissionSeed;
    private static string _queuedDeploy = "";

    // Guest auto-launch: capture the prep screen when it opens, poll until its
    // mission preview exists (cached previews never fire OnPreviewReady), then
    // settle briefly and press the (programmatic) launch button.
    internal static bool AutoLaunchPending;
    private static float _autoLaunchDeadline;
    private static MissionPrepUIScreen _launchScreen;
    private static float _previewSeenAt = -1f;

    // Prep viewer: the guest mirrors the host's planning screen read-only, so
    // the shared cursor has a shared context. Purely visual -- battle-start
    // correctness still comes from the spawn-time deployment stamp.
    private static MissionPrepUIScreen _viewerScreen;
    private static bool _expectViewer;
    internal static bool ViewerPrepActive => _viewerScreen != null;

    // Tracked on both roles: no load may execute and no sync save may be
    // taken while a mission-results screen is up.
    internal static bool ResultScreenOpen;

    // Host: any campaign becoming loaded (Continue / Load / New Game) while
    // connected pushes state to the guest automatically.
    private static IntPtr _lastStrategyPtr = IntPtr.Zero;

    private static void UpdateCampaignWatch()
    {
        var strat = StrategyState.Get();
        IntPtr p = strat != null ? strat.Pointer : IntPtr.Zero;
        if (p != _lastStrategyPtr)
        {
            _lastStrategyPtr = p;
            if (p != IntPtr.Zero)
                ScheduleAutoSync(6f, "campaign (re)loaded"); // host+connected gated inside
        }

        // Starting/switching an operation creates state the guest doesn't
        // have; sync it over before any planning references its missions.
        if (CoopNet.Mode == CoopMode.Host && CoopNet.Connected)
        {
            var op = strat?.Operations?.GetCurrentOperation();
            IntPtr po = op != null ? op.Pointer : IntPtr.Zero;
            if (po != _lastOperationPtr)
            {
                _lastOperationPtr = po;
                if (po != IntPtr.Zero)
                    ScheduleAutoSync(2f, "operation changed");
            }
        }
    }

    private static IntPtr _lastOperationPtr = IntPtr.Zero;

    // Guest: after a sync load starts, watch for the new strategy state and
    // acknowledge to the host so its input blocker can lift.
    private static bool _awaitingLoadDone;
    private static IntPtr _preLoadStrategyPtr;
    private static int _preLoadProcessed;
    private static float _awaitingSince;

    private static void UpdateLoadAck()
    {
        if (!_awaitingLoadDone) return;
        var strat = StrategyState.Get();
        IntPtr p = strat != null ? strat.Pointer : IntPtr.Zero;
        // A NEW STATE OBJECT IS NOT A LOADED STATE (#65). The game creates the
        // StrategyState first and fills it in its ProcessSaveState coroutine
        // afterwards. On a cold-started client that coroutine ended only after
        // co-op had already opened mission prep (r116 Player.log: "Loaded strategy
        // state" after "Starting MissionPreview generation"), so prep ran against
        // a half-loaded campaign and never finished. Wait for the coroutine itself.
        // If the patch did not bind, the pointer change alone decides (old rule).
        bool processed = !SaveLoadPatches.Bound || SaveLoadPatches.Completed > _preLoadProcessed;
        if (p != IntPtr.Zero && p != _preLoadStrategyPtr && processed)
        {
            _awaitingLoadDone = false;
            CoopNet.Send(new CoopMsg { T = "synced" });
            CoopNet.Status = "Synced with host";
            Plugin.Logger.LogInfo("[SESSION] Sync load complete; acknowledged to host");
            StrategyMirror.OnGuestSyncLoaded();   // reopen a mirrored screen the load reset
            // keep the pause up if a mission entry is about to continue it
            if (_queuedMissionIdx < 0)
                CoopRunner.SetSyncBlocker(false);
        }
        else if (Time.realtimeSinceStartup - _awaitingSince > 120f)
        {
            _awaitingLoadDone = false;
            CoopRunner.SetSyncBlocker(false);
        }
    }

    // Post-battle auto-sync: after the host closes the mission-results screen
    // (loot/XP decisions final), push the fresh state to the guest so drift
    // never depends on someone remembering the sync button.
    private static float _autoSyncAt = -1f;

    internal static void ScheduleAutoSync(float delay, string reason)
    {
        if (CoopNet.Mode != CoopMode.Host || !CoopNet.Connected) return;
        _autoSyncAt = Time.realtimeSinceStartup + delay;
        Plugin.Logger.LogInfo($"[SESSION] Auto-sync scheduled ({reason})");
    }

    private static void UpdateAutoSync()
    {
        if (_autoSyncAt < 0f) return;
        float now = Time.realtimeSinceStartup;
        if (now < _autoSyncAt) return;
        if (ResultScreenOpen) return; // decisions still being made
        // A sync load RESETS the guest's view -- never fire one while the host
        // is inside a stateful strategy screen (browsing armory/workshop/market
        // etc.). It runs once the host is back on a navigation screen (system
        // map / mission select), which is exactly when a view reset is invisible.
        if (StrategyMirror.HostInStatefulScreen()) { _autoSyncAt = now + 2f; return; }
        if (StrategyState.Get() != null && TacticalManager.Get() == null)
        {
            _autoSyncAt = -1f;
            Plugin.Logger.LogInfo("[SESSION] Auto-syncing partner (post-battle)");
            StartSync();
        }
        else if (now > _autoSyncAt + 30f)
        {
            _autoSyncAt = -1f; // never settled on the strategy layer; give up quietly
        }
    }

    // Battle-start sync: when the host's battle begins, wait for the game's
    // battle autosave to land, then ship it to the guest with an auto-load.
    private static bool _battlePending;
    private static float _battleDeadline;
    private static DateTime _battleMinWriteUtc;
    private static string _battleCandidate;
    private static long _battleCandidateSize = -1;
    private static float _battleLastCheck;

    // Host hold-fire: true from the moment the host ships the battle save until
    // the guest confirms it is fully loaded in ("ready"). While true the host is
    // hard-blocked from acting (see Replication hold blocks). Failsafe-released
    // so a lost ack can't strand the host.
    internal static bool HostHoldFire;
    private static float _holdFireSince;
    // HOLD UNTIL THE GUEST IS ACTUALLY IN (user, 2026-09-26: "the pause menu for
    // host [should] hold until guest is in game, it appears not"). It was 12 s
    // while a guest takes ~40 s to load, so every battle opened with the host
    // free to play alone for half a minute. "45s froze the host's squad" was a
    // LOST ready, never a slow one -- the guest now re-sends "ready" until the
    // host answers, so this is only a last resort for a guest that never loads.
    private const float HoldFireFailsafe = 150f;
    private static float _guestReadySentAt;

    // HAS THE PARTNER CONFIRMED IT IS ACTUALLY IN THIS BATTLE?
    //
    // Distinct from HostHoldFire, which is failsafe-released after 12s so a lost
    // ack can never freeze a HUMAN's squad at every battle start. Loading a save
    // into a battle regularly takes longer than that, so when the failsafe fires
    // the host resumes while the guest is still on the loading screen -- and the
    // automated harness, which needs no human courtesy, immediately raced ahead
    // and opened a round of drift before the guest had even entered.
    //
    // So the two needs are separated: the failsafe still unblocks a human, while
    // the harness waits on THIS flag, which is only ever set by the partner
    // actually reporting in. Cleared on every battle entry so a stale confirm
    // from the previous mission cannot green-light the next one.
    // v5: on the host, true only once EVERY connected client has reported in
    // (the ready handler sets it from Peers.AllClientsReady). Clearing it clears
    // the per-seat record too.
    private static bool _partnerBattleConfirmed;
    internal static bool PartnerBattleConfirmed
    {
        get => _partnerBattleConfirmed;
        set { _partnerBattleConfirmed = value; if (!value) Peers.ClearReady(); }
    }
    private static float _harnessWaitSince;
    // Generous: it only has to outlast a slow battle load, and waiting costs a
    // pause where the alternative costs a round of drift.
    private const float HarnessEntryFailsafe = 90f;

    // True once the harness may play. Waits for the partner's confirmation, but
    // NOT forever -- a gate with no failsafe is just a slower deadlock, which is
    // the mistake this whole system exists to stop making.
    internal static bool HarnessMayAct()
    {
        if (!CoopNet.Connected) return true;
        // A reload in progress is a known wait with its own failsafe -- never
        // count it against this one (run 31: a stale clock tripped it at once).
        if (GuestReload.HostActive || GuestReload.GuestCatchingUp || BattleSave.Restoring) { _harnessWaitSince = Time.realtimeSinceStartup; return false; }
        if (PartnerBattleConfirmed) return true;
        if (_harnessWaitSince <= 0f) _harnessWaitSince = Time.realtimeSinceStartup;
        if (Time.realtimeSinceStartup - _harnessWaitSince <= HarnessEntryFailsafe) return false;

        PartnerBattleConfirmed = true;   // stop re-reporting; play on
        Plugin.Logger.LogWarning(
            $"[SESSION] Partner never reported into the battle within {HarnessEntryFailsafe:F0}s -- " +
            $"the harness is proceeding anyway rather than stalling forever");
        return true;
    }

    // Guest-ready gating: the guest arms this when its battle sim starts, and
    // only sends "ready" once it is genuinely settled and interactive, so the
    // host's hold-fire lasts until the guest can actually play.
    private static bool _guestReadyPending;
    private static float _guestBattleStartAt;
    private const float GuestReadySettle = 3f;

    // Guest-entering-battle: true from the moment the guest is told to launch a
    // battle until its battle actually starts. The multi-step entry (sync load ->
    // mission prep -> launch -> battle scene) has brief gaps between the per-phase
    // pauses; this keeps ONE persistent load screen up across all of them so the
    // guest can't click out mid-entry (which desyncs). Failsafe-released.
    internal static bool GuestEnteringBattle;

    // THE GUEST IS LOADING OUR SAVE RIGHT NOW.
    //
    // Set when the sync save goes out, cleared by the guest's "synced" ack. The
    // screen blocker was already raised at the same moment, but a blocker only
    // stops a MOUSE -- it did not stop the mission being launched, and the logs
    // show exactly that:
    //
    //   20:21:29  Sync save sent (61834 bytes)
    //   20:21:31  screen -> UITactical            <- host entered the battle
    //   20:21:39  Partner confirmed sync complete <- guest was still loading
    //
    // The host began the mission eight seconds before the guest had finished
    // loading the save that mission is generated from. Same law as HostHoldFire,
    // one step earlier in the flow: an action we cannot tell our partner about
    // must not happen at all.
    internal static bool SyncInFlight;
    private static float SyncSentAt = -1f;

    // FAILSAFE. If the ack never arrives -- a dropped packet, a partner that
    // alt-F4'd -- the host must not be locked out of its own campaign forever.
    // A player who cannot play is a worse outcome than one who started early.
    private const float SyncHoldMax = 90f;

    internal static bool GuestStillLoading =>
        SyncInFlight && CoopNet.Connected && CoopNet.Mode == CoopMode.Host
        && SyncSentAt > 0f && Time.realtimeSinceStartup - SyncSentAt < SyncHoldMax;

    internal static float SyncWaitSeconds =>
        SyncSentAt > 0f ? Time.realtimeSinceStartup - SyncSentAt : 0f;

    internal static void ClearSyncInFlight()
    {
        SyncInFlight = false;
        SyncSentAt = -1f;
    }

    // True once the guest has fully settled into the battle (its "ready" was
    // sent). From this moment the HOST drives the faction cycle: the guest's
    // local advances are held and every host change is replayed 1:1.
    internal static bool GuestBattleReady;
    private static float _guestEnteringSince;
    private const float GuestEnterFailsafe = 60f;

    internal static void OnPartnerJoined()
    {
        if (CoopNet.Mode != CoopMode.Host) return;
        // REJOIN (BUGS.md #1): the partner connected while we are mid-battle. A
        // strategy sync cannot run here, but a reload can bring them in.
        if (TacticalManager.Get() != null)
        {
            // A battle the host entered BEFORE connecting (e.g. it loaded a
            // mission's launch autosave, which resumes straight into the battle)
            // has no battle-entry record, so the reload cannot run. The fallback
            // board push assumes the guest is in this battle; a guest at the
            // title has no board, so it only paused both games behind a sync
            // screen until the 12 s failsafe (user hand test 2026-10-02 17:45).
            // Say what to do instead.
            if (!GuestReload.CanJoinMidBattle(TacticalManager.Get()))
            {
                const string text = "The host is in a battle that started before the partner connected -- it cannot be joined. Finish or leave the battle, then the partner is synced. (Connect first, then load.)";
                Plugin.Logger.LogWarning("[SESSION] Partner joined during a battle the host started alone -- no battle-entry save, so they cannot be brought in; told both players");
                CoopRunner.ShowVersionWarning(text, sticky: true);
                CoopNet.Send(new CoopMsg { T = "notice", Data = text });
                CoopNet.Status = "Partner cannot join this battle -- finish or leave it";
                return;
            }
            GuestReload.Start("partner joined mid-battle", Peers.LastAdmitted);
            return;
        }
        if (StrategyState.Get() == null)
        {
            Plugin.Logger.LogInfo("[SESSION] Partner joined, no campaign loaded -- skipping auto-sync");
            return;
        }
        StartSync();
    }

    internal static void StartSync()
    {
        try
        {
            // CRITICAL: never start a second save while one is still being
            // written. SaveSystem.Save is an async coroutine that also rewrites
            // latest.save; two overlapping writes to the same file corrupt BOTH
            // -- which breaks all loading, not just co-op. With many auto-sync
            // triggers, overlap is easy; re-schedule instead.
            if (_pendingPath != null || _battlePending)
            {
                Plugin.Logger.LogInfo("[SESSION] A save is already in progress; deferring this sync");
                ScheduleAutoSync(2f, "deferred (save in progress)");
                return;
            }
            if (StrategyState.Get() == null)
            {
                CoopNet.Status = "No campaign loaded -- nothing to sync";
                return;
            }
            if (BattleSave.Restoring)
            {
                Plugin.Logger.LogInfo("[SESSION] Not syncing the strategy layer: a saved battle is being resumed");
                return;
            }
            if (TacticalManager.Get() != null)
            {
                CoopNet.Status = "Cannot sync mid-battle -- finish or abort the mission first";
                Plugin.Logger.LogWarning("[SESSION] Sync requested mid-battle; the save system only captures the strategy layer");
                return;
            }
            string path = SaveTransfer.PathFor(SyncSaveFile);
            SaveSystem.Save(SaveStateType.Manual, path, "Co-op sync");
            _pendingPath = path;
            _pendingSince = Time.realtimeSinceStartup;
            _lastSize = -1;
            _lastCheck = 0f;
            CoopNet.Status = "Preparing sync save...";
            Plugin.Logger.LogInfo($"[SESSION] Sync save requested -> {path}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[SESSION] Sync failed: {e}");
            CoopNet.Status = "Sync failed (see log)";
        }
    }

    // Guest: its battle sim just started. Arm the settle timer; the actual
    // "ready" is sent later by UpdateGuestReady once it is genuinely in.
    internal static void OnGuestBattleStarted()
    {
        // the guest is now in the battle -- end the persistent entry load screen
        GuestEnteringBattle = false;
        CoopRunner.SetSyncBlocker(false);
        _guestReadyPending = true;
        _guestBattleStartAt = Time.realtimeSinceStartup;
        // Arm the harness entry wait here too -- the guest reaches a battle by
        // this path, never through OnHostBattleStarted.
        PartnerBattleConfirmed = false;
        _harnessWaitSince = Time.realtimeSinceStartup;
        Plugin.Logger.LogInfo("[SESSION] Guest battle starting; entry load screen cleared, will confirm ready once settled");
        GuestReload.OnGuestBattleStarted();
    }

    // A reloaded guest confirms only once it has caught up with the host's
    // board, and never waits for IsPlayerTurn: the host is frozen until this
    // arrives, so its AI cannot hand the turn back.
    internal static void GuestReadyAfterCatchUp()
    {
        _guestReadyPending = false;
        GuestBattleReady = true;
        Replication.ResetPendingAdvances();
        CoopNet.Send(new CoopMsg { T = "ready" });
        _guestReadySentAt = Time.realtimeSinceStartup;
        Plugin.Logger.LogInfo("[SESSION] Guest caught up with the host's battle -- sent ready");
    }

    // Failsafe so a broken entry can't leave the guest stuck behind the load
    // screen forever.
    private static void UpdateGuestEnteringBattle()
    {
        if (!GuestEnteringBattle) return;
        if (!CoopNet.Connected || Time.realtimeSinceStartup - _guestEnteringSince > GuestEnterFailsafe)
        {
            GuestEnteringBattle = false;
            CoopRunner.SetSyncBlocker(false);
            Plugin.Logger.LogWarning("[SESSION] Guest battle-entry load screen released by failsafe");
        }
    }

    // Send "ready" only when the guest is truly loaded in: in a battle, on the
    // players' turn, and past a short settle window (lets deployment + the scene
    // finish so the host doesn't get a false "partner is in" the instant the sim
    // ticks over).
    private static void UpdateGuestReady()
    {
        // RE-SEND UNTIL ANSWERED. The host holds fire until our "ready" arrives
        // and answers it with its own; if either is lost the host would wait out
        // the failsafe. So keep saying it every few seconds until the host's
        // answer has set PartnerBattleConfirmed here.
        if (_guestReadySentAt > 0f && !PartnerBattleConfirmed && CoopNet.Connected
            && TacticalManager.Get() != null && Time.realtimeSinceStartup - _guestReadySentAt > 4f)
        {
            _guestReadySentAt = Time.realtimeSinceStartup;
            CoopNet.Send(new CoopMsg { T = "ready" });
            Plugin.Logger.LogInfo("[SESSION] Host has not answered our ready yet -- re-sent");
        }
        if (PartnerBattleConfirmed || TacticalManager.Get() == null) _guestReadySentAt = 0f;
        if (!_guestReadyPending) return;
        if (GuestReload.GuestCatchingUp) return;   // GuestReadyAfterCatchUp sends it
        var tm = TacticalManager.Get();
        if (tm == null)
        {
            // left/never entered the battle -- drop the pending ready
            _guestReadyPending = false;
            return;
        }
        if (!tm.IsPlayerTurn()) return;
        if (Time.realtimeSinceStartup - _guestBattleStartAt < GuestReadySettle) return;
        _guestReadyPending = false;
        // The battle state came from the host's save, so faction/round are
        // identical right now -- drop any queued advances from the load window
        // and let the host drive the cycle from this exact point.
        GuestBattleReady = true;
        // NOTE: the guest does NOT self-confirm here. Being settled says nothing
        // about where the PARTNER is -- observed live with the guest in the battle
        // at round 1 while the host had not entered at all. Confirmation is
        // mutual: each side waits for the other's "ready".
        Replication.ResetPendingAdvances();
        CoopNet.Send(new CoopMsg { T = "ready" });
        _guestReadySentAt = Time.realtimeSinceStartup;
        Plugin.Logger.LogInfo("[SESSION] Guest fully settled in battle -- sent ready; host now drives the faction cycle");
    }

    // Failsafe so a dropped "ready" can never strand the host: release the hold
    // if the host leaves the battle, disconnects, or the wait runs long.
    private static void UpdateHostHoldFire()
    {
        if (!HostHoldFire) return;
        bool release = !CoopNet.Connected
            || TacticalManager.Get() == null
            || Time.realtimeSinceStartup - _holdFireSince > HoldFireFailsafe;
        if (release)
        {
            HostHoldFire = false;
            // Not the overlay while a sync/rejoin pause still holds the board: r139
            // 09:24:16 this cleared "M3 IS REJOINING" on the host mid catch-up, so
            // the host looked unpaused for 25 s while the pause (correctly) held.
            if (!Replication.SyncPauseActive) CoopRunner.SetSyncBlocker(false);
            Plugin.Logger.LogWarning("[SESSION] Hold-fire released by failsafe (partner ready not received in time / battle ended)");
        }
    }

    // #57: a host that is HOSTING but alone still records how this battle began
    // (the mission-launch autosave + idx/seed/deploy), so a player who connects
    // mid-battle gets the normal rejoin instead of "battle started before the
    // partner connected". Records only: nothing is sent, nothing is held.
    private static bool _entryOnly;

    internal static void OnHostBattleStartedAlone()
    {
        _entryOnly = true;
        _battlePending = true;
        _battleDeadline = Time.realtimeSinceStartup + 45f;
        _battleMinWriteUtc = DateTime.UtcNow.AddSeconds(-15);
        _battleCandidate = null;
        _battleCandidateSize = -1;
        _battleLastCheck = 0f;
        Plugin.Logger.LogInfo("[SESSION] Host battle started with nobody connected -- recording its entry so players can join mid-battle");
    }

    internal static void OnHostBattleStarted()
    {
        _entryOnly = false;
        _hostPrepOpen = false;
        _battlePending = true;
        _battleDeadline = Time.realtimeSinceStartup + 45f;
        // the autosave may be written slightly before our battle-start hook fires
        _battleMinWriteUtc = DateTime.UtcNow.AddSeconds(-15);
        _battleCandidate = null;
        _battleCandidateSize = -1;
        _battleLastCheck = 0f;
        // Hold fire from the very moment the host's battle begins -- NOT only
        // after the autosave is found and shipped (that poll can take a couple of
        // seconds, during which the host was in the battle unheld and could act
        // before the guest even started loading).
        HostHoldFire = true;
        _holdFireSince = Time.realtimeSinceStartup;
        PartnerBattleConfirmed = false;   // this battle's confirm must be earned
        _harnessWaitSince = Time.realtimeSinceStartup;
        // The host IS in the battle as of this moment, so tell the guest. Without
        // this the confirmation only ever flowed guest -> host, and the guest had
        // nothing to wait on but its own arrival.
        CoopNet.Send(new CoopMsg { T = "ready" });
        CoopRunner.SetSyncBlocker(true, "PARTNER IS ENTERING THE BATTLE -- HOLD FIRE");
        CoopNet.Status = "Battle started -- HOLD FIRE until partner is in";
        Plugin.Logger.LogInfo("[SESSION] Host battle started; hold-fire engaged, shipping battle autosave to partner");
    }

    private static void UpdateBattleSync()
    {
        if (!_battlePending) return;
        float now = Time.realtimeSinceStartup;
        if (now > _battleDeadline)
        {
            _battlePending = false;
            Plugin.Logger.LogError("[SESSION] No fresh battle save appeared within 45s -- partner NOT synced. Are autosaves enabled?");
            CoopNet.Status = "Battle sync failed: no autosave found (see log)";
            return;
        }
        if (now - _battleLastCheck < 1f) return;
        _battleLastCheck = now;

        var dir = new DirectoryInfo(SaveTransfer.PathFor(""));
        if (!dir.Exists) return;
        FileInfo newest = null;
        foreach (var f in dir.GetFiles("*.save"))
        {
            if (f.LastWriteTimeUtc < _battleMinWriteUtc) continue;
            if (newest == null || f.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = f;
        }
        if (newest == null) return;

        if (newest.FullName != _battleCandidate || newest.Length != _battleCandidateSize || newest.Length == 0)
        {
            _battleCandidate = newest.FullName;
            _battleCandidateSize = newest.Length;
            return; // wait one more tick for the file to stabilize
        }

        _battlePending = false;
        if (_entryOnly)
        {
            _entryOnly = false;
            if (_hostMissionIdx >= 0) GuestReload.RecordBattleEntry(newest.Name, _hostMissionIdx, _hostMissionSeed, _hostDeploy);
            _hostMissionIdx = -1;
            return;
        }
        // RESUMING A SAVED BATTLE: record this battle's entry, but do not ship the
        // guest into round 1 -- the host catches up first, then GuestReload
        // brings the guest straight into the resumed board.
        if (BattleSave.Restoring)
        {
            if (_hostMissionIdx >= 0) GuestReload.RecordBattleEntry(newest.Name, _hostMissionIdx, _hostMissionSeed, _hostDeploy);
            _hostMissionIdx = -1;
            Plugin.Logger.LogInfo("[SESSION] Resuming a saved battle -- the guest joins after the host has caught up");
            return;
        }
        SaveTransfer.SendFile(newest.FullName);
        string jpg = IOPath.ChangeExtension(newest.FullName, ".jpg");
        if (File.Exists(jpg)) SaveTransfer.SendFile(jpg);
        CoopNet.Send(new CoopMsg { T = "loadreq", FName = newest.Name });
        if (_hostMissionIdx >= 0)
        {
            GuestReload.RecordBattleEntry(newest.Name, _hostMissionIdx, _hostMissionSeed, _hostDeploy);
            CoopNet.Send(new CoopMsg { T = "startmission", MIdx = _hostMissionIdx, Seed = _hostMissionSeed, Data = _hostDeploy });
            Plugin.Logger.LogInfo($"[SESSION] Sent mission auto-start (idx={_hostMissionIdx}, seed={_hostMissionSeed}, deploy=[{_hostDeploy}])");
            _hostMissionIdx = -1;
        }
        CoopNet.Status = "Battle save sent -- HOLD FIRE until partner is in";
        // hold-fire is already engaged from OnHostBattleStarted; don't restart
        // its failsafe clock here.
        Plugin.Logger.LogInfo($"[SESSION] Battle save '{newest.Name}' ({newest.Length} bytes) sent with load request");
    }

    internal static void OnHostMissionLaunch(int idx, int seed)
    {
        _hostMissionIdx = idx;
        _hostMissionSeed = seed;
        Plugin.Logger.LogInfo($"[SESSION] Host launching mission idx={idx} seed={seed}");
    }

    // PIN THE STRATEGY RNG SO BOTH MACHINES GENERATE THE SAME ENEMIES.
    //
    // The mission seed alone was never enough. The game draws from several
    // independent PseudoRandom streams -- BaseMapGenerator.m_Random for the map,
    // TacticalManager.s_Random for combat rolls (this mod already syncs that
    // one), and Operation.m_Random on the strategy layer. Nothing synced the
    // last of those.
    //
    // The evidence that it matters: on a diverged battle the two machines agreed
    // on most enemy SPAWN POSITIONS but disagreed on which squad stood at each
    // one -- the signature of a shared map draw and an unshared composition draw.
    //
    // CORRECTED IN v3.13.0: the "map generates identically from the mission seed"
    // half of that reading was wrong. It only looked true because the map stream
    // happened to agree in the runs available at the time. It does not agree in
    // general -- see PinMapgenRandom below, which pins the stream that actually
    // builds the board. This pin remains correct and necessary for composition;
    // it was simply never sufficient on its own.
    //
    // The streams drift because the two machines do different amounts of
    // strategy-layer work before launching. The guest opens mission prep twice
    // (once mirroring the host's planning screen, once for its own auto-launch)
    // and loads the sync save up to three times; the host opens prep once. Every
    // one of those can advance the operation's RNG, and it is never rewound.
    //
    // Re-initialising both to the mission seed at the launch instant makes the
    // prior history irrelevant: whatever each machine drew on the way here, they
    // enter generation from the same point. Deliberately in the LaunchMission
    // prefix and nowhere else, so it cannot disturb ordinary strategy play.
    internal static void PinStrategyRandom(int seed)
    {
        if (!Plugin.PinMissionRng.Value) return;
        // A HOST ALONE PINS TOO: a player may join this battle later, and their
        // rebuild regenerates it from the recorded seed (hand test 2026-10-04: host
        // launched alone, unpinned; the late joiner's map differed on 22% of tiles).
        if (!CoopNet.Connected && CoopNet.Mode != CoopMode.Host) return;
        try
        {
            var op = StrategyState.Get()?.Operations?.GetCurrentOperation();
            if (op == null) return;
            var rng = op.m_Random;
            if (rng == null) return;
            rng.Init(seed);
            Plugin.Logger.LogInfo($"[SYNC] Strategy RNG pinned to mission seed {seed} before launch");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[SYNC] Strategy RNG pin failed: {e.Message}");
        }
    }

    // PIN THE MAP GENERATORS SO BOTH MACHINES BUILD THE SAME BOARD.
    //
    // v3.13.0 field logs settled what PinStrategyRandom's comment had guessed
    // wrong. Pinning the strategy stream DID fix composition -- all three
    // battles in the report generated the same army, down to matching group
    // sizes (18 positions vs 18, 18 vs 18, 9 vs 9). What it did not fix, and
    // what nothing was pinning, is the MAP:
    //
    //     [TERRAIN] MAPS DIFFER -- 440 of 1764 tiles differ (24.9%)
    //               surface=119 cover=369 lineOfSight=0 structures=122
    //
    // Logged at round 1, before a shot was fired, so it is generation and not
    // battle damage. With different terrain the two boards then placed that
    // identical army on different tiles, which is what the roster check saw and
    // misreported as a different army.
    //
    // WHERE to intervene took some finding, because the obvious place does not
    // exist in this build. The decompile advertises Mission.MapgenTemplates, a
    // List<BaseMapGenerator> that could simply be walked and re-seeded -- but
    // that member is gone from the live v0.7.14 interop (the decompile under
    // _CoopModDev\decompiled_new predates the 2026-08-20 game update, so the API
    // index built from it is stale here; interop is the authority). Generators
    // are now built on demand by MapGenConfig.CreateGenerator(), so at launch
    // time there is no list to pin.
    //
    // What the current build does have is better. Every generation run is driven
    // by one MapgenInput, constructed per run as (Mission, IMapGenerator) and
    // carrying a single Rng that the whole pipeline draws from. Pinning THAT is
    // one interception instead of many, and it is naturally immune to the drift
    // that sank a call-counter approach: the guest opens mission prep twice and
    // so runs map PREVIEW generation more often than the host, which would
    // desynchronise any ordinal we tried to keep. Re-pinning at every
    // construction makes the number of previous runs irrelevant -- exactly the
    // property that makes PinStrategyRandom work.
    // ONCE PER GENERATION RUN, not once per call. Re-seeding partway through
    // would reset the stream mid-map and make things worse, so the input's own
    // identity is the guard.
    private static IntPtr _pinnedInput = IntPtr.Zero;
    private static IntPtr _pinnedRng = IntPtr.Zero;

    // The mission seed of the generation run in flight, for the per-generator pin
    // below (BaseMapGenerator.Init receives a seed but not the mission).
    private static int _currentMapgenSeed = int.MinValue;

    internal static void PinMapgenInput(Menace.Tactical.Mapgen.MapgenInput input)
    {
        if (!Plugin.PinMissionRng.Value) return;
        if (!CoopNet.Connected && CoopNet.Mode != CoopMode.Host) return;   // host alone too -- see PinStrategyRandom
        try
        {
            if (input == null) return;
            // IDENTITY BY *TWO* POINTERS, NOT ONE.
            //
            // IL2CPP recycles addresses, so a fresh MapgenInput can land on a
            // just-freed one and this guard would skip pinning that whole
            // generation run -- silently, and differently on each machine,
            // because the guest opens mission prep twice and therefore allocates
            // in a different order. Requiring the input AND its Rng to both match
            // makes a false "already pinned" far less likely. (The residual map
            // divergence reported against v3.14.0 is what sent us here.)
            var rng0 = input.Rng;
            if (input.Pointer == _pinnedInput && rng0 != null && rng0.Pointer == _pinnedRng) return;
            var rng = rng0;
            var mission = input.Mission;
            if (rng == null || mission == null) return;
            _pinnedInput = input.Pointer;
            _pinnedRng = rng.Pointer;
            int seed = mission.GetSeed();
            _currentMapgenSeed = seed;
            rng.Init(seed);
            // Deliberately quiet: this fires on preview passes too, several times
            // per mission, and a line per run would bury the battle log.
            if (seed != _lastMapgenSeedLogged)
            {
                _lastMapgenSeedLogged = seed;
                Plugin.Logger.LogInfo($"[SYNC] Mapgen RNG pinned to mission seed {seed}");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[SYNC] Mapgen RNG pin failed: {e.Message}");
        }
    }

    private static int _lastMapgenSeedLogged = int.MinValue;

    // ---- THE SECOND MAPGEN STREAM ------------------------------------------
    //
    // MapgenInput.Rng is not the only source of randomness in generation. Every
    // BaseMapGenerator carries its OWN `PseudoRandom m_Random`, seeded through the
    // engine's own `Init(int _seed)`. Pinning MapgenInput.Rng leaves that stream
    // completely untouched -- which is exactly the trap CLAUDE.md records: the
    // game draws from several independent PseudoRandom streams and syncing one
    // does not sync the rest.
    //
    // This is the leading suspect for the map divergence still reported against
    // v3.14.0 ("where the host has an impassable forest belt, the guest has a
    // road"): the pin that IS live fires from ChunkGenerator.ModifyBlueprint, so
    // chunk detail lands on a pinned stream and matches, while whatever the
    // generators' own streams decide earlier does not.
    //
    // Corrected at Init, on purpose: that is the engine's own seeding call, so we
    // are rewriting the seed at the point the engine seeds rather than resetting a
    // live stream from outside. ("Only ever write a value the engine would write
    // itself, at a point the engine would write it" -- the DD co-op project's rule,
    // learned there from several crashes.)
    //
    // THE ORIGINAL SEED IS LOGGED BEFORE IT IS REPLACED, so a host/guest log pair
    // still answers "did the engine's own per-generator seeds actually disagree?"
    // after the fix is in. Applying a fix must not destroy the evidence that it
    // was needed -- reading matching maps as proof a dead hook worked is the
    // mistake that let this survive a release.
    internal static bool PinGeneratorSeed(object generator, ref int seed)
    {
        if (!Plugin.PinMissionRng.Value || (!CoopNet.Connected && CoopNet.Mode != CoopMode.Host)) return false;
        if (_currentMapgenSeed == int.MinValue) return false;
        try
        {
            // Per generator TYPE, so two generators do not share one stream while
            // still agreeing across machines. Order-independent by construction:
            // it depends on the mission seed and the type name, never on how many
            // previews this machine happens to have run.
            string tn = generator?.GetType()?.Name ?? "?";
            int derived = _currentMapgenSeed ^ StableHash(tn);
            if (derived == seed) return false;         // engine already agreed
            Plugin.Logger.LogInfo($"[SYNC] Mapgen generator '{tn}' seed {seed} -> {derived} (mission {_currentMapgenSeed})");
            seed = derived;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[SYNC] Generator seed pin failed: {e.Message}");
            return false;
        }
    }

    // FNV-1a, written out deliberately. string.GetHashCode() is randomised PER
    // PROCESS in .NET Core, so using it here would hand the two machines different
    // seeds and manufacture the very divergence this code exists to remove.
    private static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
            return (int)(h & 0x7FFFFFFF);
        }
    }

    // THE MISSION'S SUPPLY BUDGET, FROM THE SOURCE THAT ACTUALLY HAS IT.
    //
    // Mission.GetMaxSupplies() returns m_MaxSupplies, a CACHED field that is
    // populated when the mission is launched -- so before launch it is ZERO.
    // Measured 2026-09-25 on the pinned save: maxSupplies=0 against
    // totalDeployCosts=1930, which made every possible loadout "over budget" and
    // is why START MISSION could never enable. (SaveState carries a
    // MISSION_MAX_SUPPLIES_FIX_VERSION, so that cache has a history of being
    // wrong in the game's own save migrations too.)
    //
    // StrategyState.GetMissionSupplies(mission) computes it, applying the
    // difficulty's PlayerBaseSupplies, the campaign multiplier and any
    // ChangeMissionSupplies effects. That is what the prep screen judges a
    // loadout against, so it is what we must use.
    //
    // This is not only a harness concern: the guest's auto-launch below passes
    // maxSupplies straight into LaunchMission, so it has been handing the engine
    // a zero budget on every mission the guest entered.
    internal static OperationResources MissionSupplies(Mission mission)
    {
        try
        {
            var computed = StrategyState.Get()?.GetMissionSupplies(mission);
            if (computed != null && computed.Value.GetAmount() > 0) return computed.Value;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[SESSION] GetMissionSupplies failed: {e.Message}"); }
        // Fall back to the cached field rather than to zero: after launch it is
        // populated and correct, and a wrong-but-nonzero budget beats a budget
        // that forbids every unit.
        try { return mission.GetMaxSupplies(); } catch { return new OperationResources(0); }
    }

    // HOST: load a save and relaunch one of its missions with a given seed and
    // deployment -- the guest's own auto-launch path, driven locally (BattleSave).
    internal static bool HostRelaunch(string saveName, int missionIdx, int seed, string deploy, out string why)
    {
        why = null;
        string path = SaveTransfer.PathFor(saveName);
        SaveState state = null;
        try
        {
            if (!SaveSystem.TryGetSaveState(path, out state) || state == null) { why = "the battle's save could not be read"; return false; }
        }
        catch (Exception e) { why = "the battle's save could not be read: " + e.Message; return false; }
        _queuedMissionIdx = missionIdx;
        _queuedMissionSeed = seed;
        _queuedDeploy = deploy ?? "";
        _queuedMissionAt = Time.realtimeSinceStartup;
        _queuedPrepIdx = -1;
        _hostWantsPrep = false;
        _loadCooldownUntil = Time.realtimeSinceStartup + 4f;
        Plugin.Logger.LogInfo($"[SESSION] Host relaunch: loading '{saveName}', then mission idx={missionIdx} seed={seed}");
        RemoteLoadInProgress = true;   // lets the host load out of a battle
        try { SaveSystem.ExecLoad(state); }
        finally { RemoteLoadInProgress = false; }
        return true;
    }

    internal static void HandleStartMission(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        _queuedMissionIdx = m.MIdx;
        _queuedMissionAt = Time.realtimeSinceStartup;
        _queuedMissionSeed = m.Seed;
        _queuedDeploy = m.Data ?? "";
        _queuedPrepIdx = -1; // launching supersedes any pending planning mirror
        _hostWantsPrep = false; // we're launching, not mirroring prep anymore
        GuestEnteringBattle = true;   // one persistent load screen across the whole entry
        _guestEnteringSince = Time.realtimeSinceStartup;
        CoopRunner.SetSyncBlocker(true, "ENTERING THE MISSION WITH HOST...");
        Plugin.Logger.LogInfo($"[SESSION] Mission auto-start queued (idx={m.MIdx}); waiting for sync load to settle");
    }

    // The BattlePlan's DeployedEntity list drives where player units spawn.
    // Both machines derive it from the same save, so list order matches.
    private static string SerializePlan(BattlePlan plan)
    {
        try
        {
            var entities = plan?.GetEntitiesToDeploy()?.TryCast<Il2CppSystem.Collections.Generic.List<DeployedEntity>>();
            if (entities == null) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < entities.Count; i++)
            {
                var p = entities[i].GetTilePosition();
                if (sb.Length > 0) sb.Append(',');
                sb.Append(p.x).Append(':').Append(p.y);
            }
            return sb.ToString();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[SESSION] Deployment serialize failed: {e.Message}");
            return "";
        }
    }

    private static Mission FindMissionByIdx(int idx)
    {
        var missions = StrategyState.Get()?.Operations?.GetCurrentOperation()?.GetMissions()
            ?.TryCast<Il2CppSystem.Collections.Generic.List<Mission>>();
        if (missions == null) return null;
        for (int i = 0; i < missions.Count; i++)
        {
            var mi = missions[i];
            if (mi != null && mi.GetIdx() == idx) return mi;
        }
        return null;
    }

    // ---- prep viewer (guest mirrors host's planning screen) ----

    private static bool _hostPrepOpen;
    private static MissionPrepUIScreen _hostPrepScreen;
    internal static bool HostPrepActive => _hostPrepOpen;

    internal static void OnHostPrepOpened(MissionPrepUIScreen screen)
    {
        var mission = screen?.GetMission();
        if (mission == null) return;
        _hostPrepOpen = true;
        _hostPrepScreen = screen;
        CoopNet.Send(new CoopMsg { T = "prepopen", MIdx = mission.GetIdx(), Seed = mission.GetSeed() });
        CoopRunner.SetSyncBlocker(true, "WAITING FOR PARTNER'S PLANNING VIEW...");
        Plugin.Logger.LogInfo($"[PREP] Host opened planning (mission idx={mission.GetIdx()}); partner mirroring");
    }

    // Reliable prep-exit detection. The host is still "in planning" while prep
    // OR any sub-screen opened from it (weapon/armory swap, loadout) is on top;
    // only a return to a strategy MAP screen means they truly left. Keying off
    // "prep is the top screen" wrongly ended planning (and killed equipment
    // sync) the moment the host opened a weapon-swap sub-screen.
    private static void UpdateHostPrepWatch()
    {
        if (CoopNet.Mode != CoopMode.Host || !_hostPrepOpen) return;
        UIScreen active;
        try { active = Menace.UI.UIManager.Get()?.GetActiveScreen(); }
        catch { return; }
        if (active == null) return;

        bool backOnMap = active.TryCast<Menace.UI.Strategy.MissionSelectUIScreen>() != null
                      || active.TryCast<Menace.UI.Strategy.SystemMapUIScreen>() != null
                      || active.TryCast<Menace.UI.Strategy.OperationSelectUIScreen>() != null;
        if (backOnMap)
        {
            _hostPrepScreen = null;
            OnHostPrepInactive();
        }
    }

    internal static void OnHostPrepClosed()
    {
        _hostPrepOpen = false;
        CoopNet.Send(new CoopMsg { T = "prepclose" });
    }

    // The prep screen deactivated on the host. Distinguish backing out (tell
    // the guest to close its mirror) from launching a mission (the startmission
    // flow drives the guest -- must NOT send prepclose or it fights the launch).
    internal static void OnHostPrepInactive()
    {
        if (!_hostPrepOpen) return;
        _hostPrepOpen = false;
        if (_hostMissionIdx >= 0)
        {
            Plugin.Logger.LogInfo("[PREP] Host prep closed for mission launch (no prepclose)");
            return;
        }
        CoopNet.Send(new CoopMsg { T = "prepclose" });
        CoopRunner.SetSyncBlocker(false);
        Plugin.Logger.LogInfo("[PREP] Host left planning back to strategy; told partner to close mirror");
    }

    // Prep-open requests are queued, NOT executed immediately: opening a
    // screen while a sync load is mid-transition kills both the load and the
    // screen (guest stuck on "Loading host's game...").
    private static int _queuedPrepIdx = -1;
    private static int _queuedPrepSeed;
    private static float _prepQueueExpiry;
    private static float _prepRetryAt;

    // Authoritative "does the host currently have planning open?" -- the guest
    // keeps its mirror iff this is true, regardless of message ordering. This
    // is what closes a mirror that finished opening AFTER the host already
    // backed out (the open is delayed by load-settle, so the host's exit can
    // easily beat it -- otherwise the guest is stranded in a dead prep screen).
    private static bool _hostWantsPrep;

    internal static void HandlePrepOpen(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        _hostWantsPrep = true;
        _queuedPrepIdx = m.MIdx;
        _queuedPrepSeed = m.Seed;
        _prepQueueExpiry = Time.realtimeSinceStartup + 120f;
        // show the guest a pause while it catches up into the host's planning
        // screen, instead of leaving it sitting on mission select (cleared when
        // the mirror opens, or on expiry / host cancel).
        CoopRunner.SetSyncBlocker(true, "ENTERING PLANNING WITH HOST...");
        Plugin.Logger.LogInfo($"[PREP] Planning mirror queued (mission idx={m.MIdx}); opens once loads settle");
    }

    private static void UpdatePrepMirror()
    {
        if (_queuedPrepIdx < 0) return;
        float now = Time.realtimeSinceStartup;
        if (now > _prepQueueExpiry)
        {
            Plugin.Logger.LogWarning($"[PREP] Gave up mirroring mission idx={_queuedPrepIdx} (state never arrived)");
            _queuedPrepIdx = -1;
            CoopRunner.SetSyncBlocker(false);
            return;
        }
        if (_viewerScreen != null || AutoLaunchPending || _expectViewer) { _queuedPrepIdx = -1; CoopRunner.SetSyncBlocker(false); return; }
        if (_queuedLoad != null) return;
        if (now < _loadCooldownUntil) return;
        if (_awaitingLoadDone) return;
        if (StrategyState.Get() == null || TacticalManager.Get() != null) return;
        if (now < _prepRetryAt) return;
        _prepRetryAt = now + 1f;

        // Do NOT consume the queue if the mission isn't known yet -- when the
        // host starts a new operation, its sync may still be in flight; keep
        // retrying until the state lands or the request expires.
        var target = FindMissionByIdx(_queuedPrepIdx);
        if (target == null) return;

        int seed = _queuedPrepSeed;
        int idx = _queuedPrepIdx;
        _queuedPrepIdx = -1;
        target.SetSeed(seed);
        _expectViewer = true;
        if (MissionPrepUIScreen.TryOpen(target))
        {
            CoopNet.Status = "Viewing host's mission planning...";
            Plugin.Logger.LogInfo($"[PREP] Mirroring host planning (mission idx={idx})");
        }
        else
        {
            _expectViewer = false;
            CoopRunner.SetSyncBlocker(false); // couldn't open -- don't leave the guest stuck behind a pause
        }
    }

    internal static void HandlePrepClose(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        _hostWantsPrep = false;
        _queuedPrepIdx = -1; // host backed out before we even opened
        CoopRunner.SetSyncBlocker(false); // lift the "entering planning" pause if it was up
        CloseViewerIfAny("host cancelled planning");
    }

    private static void CloseViewerIfAny(string why)
    {
        if (_viewerScreen == null) return;
        var screen = _viewerScreen;
        _viewerScreen = null;
        try { screen.Cancel(); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[PREP] Could not close mirrored planning: {e.Message}"); }
        CoopNet.Status = "Host closed mission planning";
        Plugin.Logger.LogInfo($"[PREP] Mirror closed ({why})");
    }

    // Safety net (called every frame): if the host is no longer in planning
    // but our mirror is (or just) open, close it. Catches every message
    // ordering, including a mirror that opened after the host's prepclose.
    private static void UpdatePrepWatchdog()
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        if (!_hostWantsPrep && _viewerScreen != null)
            CloseViewerIfAny("host already left planning");
    }

    // Guest side: prep screen opened -- claim it for auto-launch or viewer.
    internal static void OnGuestPrepScreenOpened(MissionPrepUIScreen screen)
    {
        if (AutoLaunchPending)
        {
            OnGuestPrepOpened(screen);
        }
        else if (_expectViewer)
        {
            _expectViewer = false;
            CoopRunner.SetSyncBlocker(false); // guest is now in the planning view
            // The host may have already backed out while we were opening; if so
            // close this immediately instead of stranding the guest in prep.
            if (!_hostWantsPrep)
            {
                try { screen.Cancel(); } catch { }
                Plugin.Logger.LogInfo("[PREP] Mirror opened after host left; closing immediately");
                return;
            }
            _viewerScreen = screen;
            CoopNet.Send(new CoopMsg { T = "prepready" }); // lifts the host's pause
        }
    }

    internal static void OnGuestPrepCancelled()
    {
        if (_viewerScreen != null)
        {
            _viewerScreen = null;
            Plugin.Logger.LogInfo("[PREP] Mirror closed locally");
        }
    }

    internal static void OnHostDeployPositionSet(DeployedEntity entity)
    {
        // only meaningful while planning on the strategy layer
        if (TacticalManager.Get() != null || StrategyState.Get() == null) return;
        var list = StrategyState.Get()?.BattlePlan?.GetEntitiesToDeploy()
            ?.TryCast<Il2CppSystem.Collections.Generic.List<DeployedEntity>>();
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (e != null && e.Pointer == entity.Pointer)
            {
                var p = entity.GetTilePosition();
                CoopNet.Send(new CoopMsg { T = "dpos", MIdx = i, TX = p.x, TY = p.y });
                return;
            }
        }
    }

    // Best-effort nudge for the mirrored prep screen to re-read unit data
    // after a remote equipment change.
    internal static void RefreshViewerUnits()
    {
        try { _viewerScreen?.ProcessUnitChanges(true); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[EQUIP] viewer refresh failed: {e.Message}"); }

        // ProcessUnitChanges(true) re-reads the unit list but does not redraw an
        // OPEN unit window's equipment slots. Reported: "equipment like grenades
        // isn't synced visually" -- the mirror placed the grenade (logged as
        // "Mirrored place: accessory.scrap_bomb_frag_grenade") and the guest's
        // window kept showing the old loadout. Redraw the window itself.
        try
        {
            var window = _viewerScreen?.m_UnitSelector?.GetUnitWindow();
            if (window != null)
            {
                window.Refresh();
                window.GetEquipment()?.UpdateSquaddiesElements();
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[EQUIP] unit window refresh failed: {e.Message}"); }
    }

    internal static void HandleDpos(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest || _viewerScreen == null) return;
        try
        {
            var list = StrategyState.Get()?.BattlePlan?.GetEntitiesToDeploy()
                ?.TryCast<Il2CppSystem.Collections.Generic.List<DeployedEntity>>();
            if (list == null || m.MIdx < 0 || m.MIdx >= list.Count)
            {
                Plugin.Logger.LogWarning($"[PREP] dpos slot {m.MIdx} out of range");
                return;
            }
            var ent = list[m.MIdx];
            ApplyingRemoteDeploy = true;
            try { ent.SetTilePosition(new Vector2Int(m.TX, m.TY)); }
            finally { ApplyingRemoteDeploy = false; }
            _viewerScreen.m_UnitSelector?.GetDeployedEntity(ent)?.SnapToTilePosition();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[PREP] Could not mirror deployment move: {e.Message}");
        }
    }

    internal static bool ApplyingRemoteDeploy;

    // Roster (deploy-list) membership mirroring: units the host adds to or
    // removes from the battle plan appear/disappear on the guest's mirror.
    // Units are identified by index in the hired-leaders roster, which both
    // machines derive from the same save.
    private static Il2CppSystem.Collections.Generic.List<BaseUnitLeader> GetHiredLeaders()
        => StrategyState.Get()?.Roster?.GetHiredLeaders()
            ?.TryCast<Il2CppSystem.Collections.Generic.List<BaseUnitLeader>>();

    internal static void OnHostPlanUnitChanged(BaseUnitLeader leader, bool added)
    {
        if (leader == null || TacticalManager.Get() != null || StrategyState.Get() == null) return;
        var hired = GetHiredLeaders();
        if (hired == null) return;
        for (int i = 0; i < hired.Count; i++)
        {
            var h = hired[i];
            if (h != null && h.Pointer == leader.Pointer)
            {
                CoopNet.Send(new CoopMsg { T = added ? "dadd" : "drem", MIdx = i });
                return;
            }
        }
    }

    internal static void HandlePlanUnitChange(CoopMsg m, bool added)
    {
        if (CoopNet.Mode != CoopMode.Guest || _viewerScreen == null) return;
        try
        {
            var hired = GetHiredLeaders();
            var plan = StrategyState.Get()?.BattlePlan;
            if (hired == null || plan == null || m.MIdx < 0 || m.MIdx >= hired.Count) return;
            var leader = hired[m.MIdx];
            if (leader == null) return;
            // The prep screen changes a unit's deployment in TWO layers: the
            // BattlePlan (data) AND the on-map marker (visual, owned by the unit
            // selector). We previously mirrored only the data half, so the
            // guest's spectator map never showed the unit appear/disappear.
            // Drive both, matching the game's own add/remove order (add:
            // data->marker; remove: marker->data). AddUnit/RemoveUnit return
            // false and no-op if the plan already matches, so this can't dupe.
            var selector = _viewerScreen.m_UnitSelector;
            ApplyingRemoteDeploy = true;
            try
            {
                if (added)
                {
                    plan.AddUnit(leader, false);
                    if (selector != null && selector.GetDeployedEntity(leader) == null)
                        selector.AddDeployedUnitToMap(leader);
                }
                else
                {
                    if (selector != null)
                    {
                        var slot = selector.GetSlot(leader);
                        if (slot != null) selector.RemoveDeployedUnitFromMap(slot);
                    }
                    plan.RemoveUnit(leader);
                }
            }
            finally { ApplyingRemoteDeploy = false; }
            _viewerScreen.ProcessUnitChanges(true);
            Plugin.Logger.LogInfo($"[PREP] Mirrored roster {(added ? "add" : "remove")} (hired unit {m.MIdx})");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[PREP] Roster mirror failed: {e.Message}");
        }
    }

    // Squad-size (squaddie) live mirror: the host's add/remove of a squaddie is
    // applied on the guest's roster too, keeping squad sizes matched in the
    // planning view. Units are addressed by hired-roster index; squaddies by
    // their (save-stable) squaddie id.
    internal static void OnHostSquaddieChanged(BaseUnitLeader leader, int squaddieId, bool added)
    {
        if (leader == null) return;
        var hired = GetHiredLeaders();
        if (hired == null) return;
        for (int i = 0; i < hired.Count; i++)
        {
            var h = hired[i];
            if (h != null && h.Pointer == leader.Pointer)
            {
                CoopNet.Send(new CoopMsg { T = added ? "sqadd" : "sqrem", MIdx = i, Prm = squaddieId });
                return;
            }
        }
    }

    internal static void HandleSquaddieChange(CoopMsg m, bool added)
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        try
        {
            var hired = GetHiredLeaders();
            if (hired == null || m.MIdx < 0 || m.MIdx >= hired.Count) return;
            var leader = hired[m.MIdx];
            if (leader == null) return;
            Replication.ApplyingRemoteSquaddie = true;
            try
            {
                if (added) leader.TryAddSquaddie(m.Prm);
                else leader.TryRemoveSquaddie(m.Prm);
            }
            finally { Replication.ApplyingRemoteSquaddie = false; }
            // refresh the planning mirror's unit card if it's open
            try { _viewerScreen?.ProcessUnitChanges(true); } catch { }
            Plugin.Logger.LogInfo($"[PREP] Mirrored squaddie {(added ? "add" : "remove")} (unit {m.MIdx}, squaddie {m.Prm})");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[PREP] Squaddie mirror failed: {e.Message}");
        }
    }

    // Faction trust (relations) live mirror: host trust changes apply on the
    // guest immediately instead of waiting for the next full sync.
    internal static bool ApplyingRemoteTrust;

    internal static void OnHostTrustChanged(StoryFaction faction, int change)
    {
        if (ApplyingRemoteTrust || faction == null || change == 0) return;
        var template = faction.GetTemplate();
        if (template == null) return;
        CoopNet.Send(new CoopMsg { T = "trust", MIdx = (int)template.FactionType, Seed = change });
        Plugin.Logger.LogInfo($"[SYNC] Mirrored trust change {change:+#;-#;0} ({template.FactionType})");
    }

    internal static void HandleTrust(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        try
        {
            var faction = StrategyState.Get()?.StoryFactions?.GetFaction((StoryFactionType)m.MIdx);
            if (faction == null)
            {
                Plugin.Logger.LogWarning($"[SYNC] Trust change for unknown faction {m.MIdx} (will heal at next sync)");
                return;
            }
            ApplyingRemoteTrust = true;
            try { faction.ChangeTrust(m.Seed); }
            finally { ApplyingRemoteTrust = false; }
            Plugin.Logger.LogInfo($"[SYNC] Applied trust change {m.Seed:+#;-#;0} ({(StoryFactionType)m.MIdx})");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[SYNC] Trust mirror failed: {e.Message}");
        }
    }

    // ---- end prep viewer ----

    // Fires the queued mission start once the guest is back on a settled
    // strategy layer (sync load done, not in a battle).
    private static void UpdateMissionStart()
    {
        if (_queuedMissionIdx < 0) return;
        if (_queuedLoad != null) return;
        // The save we are entering from must be fully processed first (#65).
        if (_awaitingLoadDone) return;

        // A QUEUED MISSION MUST NOT BE STARVED BY INCOMING SAVES.
        //
        // Every save load pushes _loadCooldownUntil 20 seconds further out, and
        // the host sends the battle autosave AFTER the mission auto-start -- so
        // each arriving file deferred the launch again and the guest sat on the
        // strategy layer while the host played the battle alone. Seen in the
        // harness: "Mission auto-start queued (idx=4); waiting for sync load to
        // settle", then two more loads, then nothing, while the host's own trace
        // read mine=0 theirs=0 for the whole mission.
        //
        // The cooldown is there to let a load settle, which is worth a short
        // wait -- but not an unbounded one. Past the deadline the launch goes
        // ahead regardless.
        bool starved = Time.realtimeSinceStartup - _queuedMissionAt > QueuedMissionDeadline;
        if (Time.realtimeSinceStartup < _loadCooldownUntil && !starved) return;
        if (starved && Time.realtimeSinceStartup < _loadCooldownUntil)
            Plugin.Logger.LogWarning("[SESSION] Mission auto-start has waited "
                + $"{(int)(Time.realtimeSinceStartup - _queuedMissionAt)}s behind repeated save loads -- launching anyway");
        var strategy = StrategyState.Get();

        if (strategy == null || TacticalManager.Get() != null) return;

        int idx = _queuedMissionIdx;
        int seed = _queuedMissionSeed;
        _queuedMissionIdx = -1;
        try
        {
            Mission target = FindMissionByIdx(idx);
            // A COLD START IS NOT READY YET (r104, crash + relaunch): the guest
            // loaded the battle save straight from the title, acked the load, and
            // looked for the mission while the game was still loading its
            // operation/mission templates (5 s later) -- one miss, no retry, and
            // everyone waited on a client that never entered the battle. Keep
            // the launch queued while the operation has no missions loaded yet;
            // fail only if they are loaded and this one is not among them.
            if (target == null && !OperationMissionsLoaded())
            {
                if (!_missionLookupLogged)
                    Plugin.Logger.LogWarning($"[SESSION] Mission idx={idx} not in the operation yet (game still loading) -- retrying");
                _missionLookupLogged = true;
                _queuedMissionIdx = idx;
                return;
            }
            _missionLookupLogged = false;
            if (target == null)
            {
                Plugin.Logger.LogError($"[SESSION] Cannot find mission idx={idx} in current operation -- auto-start failed");
                CoopNet.Status = "Mission auto-start failed (see log)";
                CoopRunner.SetSyncBlocker(false);
                return;
            }
            // THE PREP SCENE MUST BE UP BEFORE PREP OPENS (#65, crash rejoin).
            // In play, mission select loads it long before a mission is opened.
            // A client relaunched after a crash jumps from a bare Strategy scene
            // straight to prep: TryOpen then loads the scene and starts the map
            // preview inside the scene-loaded callback, before the scene's own
            // components have started -- the generator coroutine then waits
            // forever (r112-r114: identical state for 60 s, no error). So load
            // it first and open once it is loaded and a frame has passed.
            if (!PrepSceneReady()) { _queuedMissionIdx = idx; return; }
            _prepSceneRequested = false;
            // Mission.Start() alone NREs: the prep screen must generate the
            // map preview first. Drive the game's own flow: open prep, wait
            // for OnPreviewReady (patched), then invoke LaunchMission.
            target.SetSeed(seed);
            AutoLaunchPending = true;
            _autoLaunchDeadline = Time.realtimeSinceStartup + 60f;
            Plugin.Logger.LogInfo($"[SESSION] Opening mission prep for auto-launch (idx={idx} seed={seed})");
            CoopNet.Status = "Entering the mission with host...";
            if (!MissionPrepUIScreen.TryOpen(target))
            {
                AutoLaunchPending = false;
                Plugin.Logger.LogError("[SESSION] MissionPrepUIScreen.TryOpen returned false -- auto-launch failed");
                CoopNet.Status = "Mission auto-start failed (see log)";
                CoopRunner.SetSyncBlocker(false);
            }
        }
        catch (Exception e)
        {
            AutoLaunchPending = false;
            Plugin.Logger.LogError($"[SESSION] Mission auto-start failed: {e}");
            CoopNet.Status = "Mission auto-start failed (see log)";
            CoopRunner.SetSyncBlocker(false);
        }
    }

    // Called from the OnOpened postfix on the guest when auto-launch is
    // pending: remember the screen so the update loop can poll its preview.
    internal static void OnGuestPrepOpened(MissionPrepUIScreen screen)
    {
        _launchScreen = screen;
        _previewSeenAt = -1f;
        Plugin.Logger.LogInfo("[SESSION] Mission prep opened; waiting for preview");
    }

    // Accelerator: freshly generated previews announce themselves.
    internal static void OnGuestPreviewReady(MissionPrepUIScreen screen)
    {
        _launchScreen = screen;
        if (_previewSeenAt < 0f) _previewSeenAt = Time.realtimeSinceStartup;
        Plugin.Logger.LogInfo("[SESSION] Mission preview ready; launching shortly");
    }

    // #65 (crash rejoin): on a cold-started client the preview never arrives and
    // nothing is logged. Log the generator's own state each time it CHANGES,
    // so the stall shows where it stopped: scheduled but never started,
    // started and died, or its object inactive (coroutines never run).
    private static string _lastPreviewState;

    private static void LogPreviewState(MissionPrepUIScreen screen)
    {
        string s;
        try
        {
            var mp = screen.GetMissionPreview();
            if (mp == null) s = "MissionPreview=null";
            else
            {
                string Idx(Mission m) => m == null ? "-" : m.GetIdx().ToString();
                s = $"active={mp.isActiveAndEnabled} go={(mp.gameObject != null ? mp.gameObject.activeInHierarchy.ToString() : "null")} "
                  + $"scene='{mp.gameObject?.scene.name}' sched={Idx(mp.m_ScheduledMission)} gen={Idx(mp.m_CurrentlyGeneratedMission)} "
                  + $"ready={Idx(mp.m_ReadyMission)} coroutine={(mp.m_CurrentCoroutine != null)} idx={mp.m_CurrentCoroutineIdx} "
                  + $"needsUpdate={screen.m_PreviewNeedsUpdate}";
                string screens = "";
                try
                {
                    var open = UIManager.Get()?.m_OpenScreens;
                    for (int i = 0; open != null && i < open.Count; i++)
                        screens += (screens.Length > 0 ? "," : "") + open[i]?.GetIl2CppType()?.Name;
                }
                catch { }
                bool overlay = false;
                try { overlay = UIManager.Get()?.IsLoadingOverlayVisible() ?? false; } catch { }
                s += $" overlay={overlay} screens=[{screens}]";
            }
        }
        catch (Exception e) { s = $"threw {e.GetType().Name}: {e.Message}"; }
        if (s == _lastPreviewState) return;
        _lastPreviewState = s;
        Plugin.Logger.LogInfo($"[SESSION] Preview state: {s}");
    }

    private static void UpdateAutoLaunch()
    {
        if (!AutoLaunchPending) return;
        float now = Time.realtimeSinceStartup;
        if (now > _autoLaunchDeadline)
        {
            AutoLaunchPending = false;
            _launchScreen = null;
            Plugin.Logger.LogError("[SESSION] Auto-launch timed out waiting for mission prep");
            CoopNet.Status = "Mission auto-start timed out (see log)";
            CoopRunner.SetSyncBlocker(false);
            return;
        }
        if (_launchScreen == null) return;

        // Cached previews never fire OnPreviewReady; poll for the result too.
        if (_previewSeenAt < 0f)
        {
            try
            {
                if (_launchScreen.GetMission()?.GetPreview() != null)
                {
                    _previewSeenAt = now;
                    Plugin.Logger.LogInfo("[SESSION] Mission preview present (polled); launching shortly");
                }
                else LogPreviewState(_launchScreen);
            }
            catch { }
            return;
        }
        if (now < _previewSeenAt + 2f) return;

        var screen = _launchScreen;
        _launchScreen = null;
        AutoLaunchPending = false;
        try
        {
            var mission = screen.GetMission();
            // NOT mission.GetMaxSupplies(): that cache is zero before launch, so
            // the guest was entering every mission with a zero budget. See
            // MissionSupplies above.
            var maxSupplies = MissionSupplies(mission);
            var deployCosts = screen.UpdateSupplies(maxSupplies);
            Plugin.Logger.LogInfo($"[SESSION] Launch budget: maxSupplies={maxSupplies.GetAmount()} deployCosts={deployCosts.GetAmount()}");
            Plugin.Logger.LogInfo("[SESSION] Invoking StartMission on prep screen");
            ModLaunching = true;
            try { screen.StartMission(deployCosts, maxSupplies); }
            finally { ModLaunching = false; }
            // the game's own battle loading takes over from here
            CoopRunner.SetSyncBlocker(false);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[SESSION] LaunchMission failed: {e}");
            CoopNet.Status = "Mission auto-start failed (see log)";
            CoopRunner.SetSyncBlocker(false);
        }
    }

    // Deployment sync at the only spot that cannot be bypassed: the game's own
    // spawn call. Host records its final positions; guest overwrites its plan
    // with them -- even a manual guest launch inherits the host's deployment.
    internal static void CaptureDeploymentFromPlan(BattlePlan plan)
    {
        _hostDeploy = SerializePlan(plan);
        Plugin.Logger.LogInfo($"[SESSION] Captured host deployment at spawn: [{_hostDeploy}]");
    }

    internal static void ApplyDeploymentToPlan(BattlePlan plan)
    {
        if (string.IsNullOrEmpty(_queuedDeploy)) return;
        string data = _queuedDeploy;
        _queuedDeploy = "";
        var entities = plan?.GetEntitiesToDeploy()?.TryCast<Il2CppSystem.Collections.Generic.List<DeployedEntity>>();
        if (entities == null)
        {
            Plugin.Logger.LogWarning("[SESSION] No deploy list on plan; cannot apply host deployment");
            return;
        }
        var parts = data.Split(',');
        if (parts.Length != entities.Count)
            Plugin.Logger.LogWarning($"[SESSION] Deployment count mismatch: host sent {parts.Length}, guest has {entities.Count} -- applying common prefix");
        int n = Math.Min(parts.Length, entities.Count);
        for (int i = 0; i < n; i++)
        {
            var xy = parts[i].Split(':');
            entities[i].SetTilePosition(new Vector2Int(int.Parse(xy[0]), int.Parse(xy[1])));
        }
        Plugin.Logger.LogInfo($"[SESSION] Applied host deployment positions to {n} unit(s) at spawn");
    }

    // Saving runs as a coroutine inside the game; wait until the file exists
    // and its size is stable before shipping it. Called every frame.
    internal static void Update()
    {
        // Battle over (or never in one): release the host-driven cycle hold.
        if (GuestBattleReady && TacticalManager.Get() == null)
            GuestBattleReady = false;

        UpdateCampaignWatch();
        UpdateBattleSync();
        UpdateGuestReady();
        UpdateHostHoldFire();
        UpdateGuestEnteringBattle();
        UpdateLoadQueue();
        UpdateLoadAck();
        UpdateMissionStart();
        UpdateAutoLaunch();
        UpdateAutoSync();
        UpdateHostPrepWatch();
        UpdatePrepMirror();
        UpdatePrepWatchdog();
        if (_pendingPath == null) return;
        float now = Time.realtimeSinceStartup;
        if (now - _pendingSince > 20f)
        {
            Plugin.Logger.LogError("[SESSION] Sync save never materialized -- aborting");
            CoopNet.Status = "Sync save timed out (see log)";
            _pendingPath = null;
            return;
        }
        if (now - _lastCheck < 0.5f) return;
        _lastCheck = now;

        var fi = new FileInfo(_pendingPath);
        if (!fi.Exists) return;
        if (fi.Length == 0 || fi.Length != _lastSize)
        {
            _lastSize = fi.Length;
            return;
        }

        string path = _pendingPath;
        _pendingPath = null;
        SaveTransfer.SendFile(path);
        string jpg = IOPath.ChangeExtension(path, ".jpg");
        if (File.Exists(jpg)) SaveTransfer.SendFile(jpg);
        CoopNet.Send(new CoopMsg { T = "loadreq", FName = IOPath.GetFileName(path) });
        CoopNet.Status = "Sync sent -- partner is loading your game";
        CoopRunner.SetSyncBlocker(true); // lifted by the guest's "synced" ack
        SyncInFlight = true;
        SyncSentAt = Time.realtimeSinceStartup;
        Plugin.Logger.LogInfo($"[SESSION] Sync save sent ({fi.Length} bytes) with load request");
    }

    internal static void HandleLoadRequest(CoopMsg m)
    {
        // Only guests follow load commands; the host is the authority and
        // must never be yanked out of its game by a remote request.
        if (CoopNet.Mode != CoopMode.Guest)
        {
            Plugin.Logger.LogWarning("[SESSION] Ignoring remote load request (this side is not a guest)");
            return;
        }
        string name = IOPath.GetFileName(m.FName ?? "");
        if (!SaveTransfer.CompletedFiles.Contains(name))
        {
            Plugin.Logger.LogError($"[SESSION] Load requested for '{name}' but that transfer did not complete cleanly");
            CoopNet.Status = "Sync aborted: transfer incomplete";
            return;
        }
        bool wasEmpty = _queuedLoad == null;
        if (!wasEmpty)
            Plugin.Logger.LogInfo($"[SESSION] Load of '{_queuedLoad}' superseded by newer '{name}'");
        _queuedLoad = name;

        // A NEW LOAD IS A NEW WAIT.
        //
        // This used to refresh the clock only when nothing was queued, so a load
        // that superseded another inherited the older one's start time -- and the
        // pair that arrive back to back at the end of a mission (the post-battle
        // strategy sync, then the next battle's autosave) are exactly that case.
        // The second load was judged against a clock that had already been
        // running, tripped the 25s "still waiting" failsafe almost at once, and
        // the guest gave up on entering the battle:
        //
        //   Load of 'coop_sync.save' superseded by newer 'auto_...save'
        //   Mission auto-start queued (idx=3); waiting for sync load to settle
        //   Sync load still waiting after 25s -- released the on-screen blocker
        //
        // From there the host was in mission two and the guest was not, which is
        // what the ROUND RUNAWAY spam downstream was reporting.
        _loadQueuedAt = Time.realtimeSinceStartup;
        _loadBlockerReleased = false;                 // a new load re-arms the blocker
        CoopRunner.SetSyncBlocker(true, "SYNCING WITH HOST -- PLEASE WAIT");
    }

    private static void UpdateLoadQueue()
    {
        if (_queuedLoad == null) return;
        if (Time.realtimeSinceStartup < _loadCooldownUntil) return;
        if (_awaitingLoadDone) return;   // the previous load is still being processed

        // NEVER LEAVE THE PLAYER LOCKED BEHIND A BLOCKER THAT HAS NO ESCAPE.
        //
        // The blocker goes up when a load is queued and only comes down when
        // that load runs -- but the load waits for the results screen, which may
        // sit there indefinitely. The result was a guest frozen on "SYNCING WITH
        // HOST -- PLEASE WAIT" with nothing happening and no way out.
        //
        // The load itself must still wait (loading into the results screen hangs
        // the game), so what gets released is the BLOCKER, not the wait: the
        // player gets their game back, the load still happens the moment the
        // screen closes.
        if (_loadQueuedAt > 0 && Time.realtimeSinceStartup - _loadQueuedAt > 25f && !_loadBlockerReleased)
        {
            _loadBlockerReleased = true;
            CoopRunner.SetSyncBlocker(false);
            CoopNet.Status = "Waiting for the host to leave the results screen -- you can keep playing";
            Plugin.Logger.LogWarning("[SESSION] Sync load still waiting after 25s -- released the on-screen blocker so the game is not locked");
        }

        // The results screen is a genuine transition -- ExecLoad into it hangs
        // the game -- so always wait for it to be dismissed.
        //
        // BUT ON THE GUEST, NOBODY DISMISSES IT.
        //
        // When the host's mission ends the guest follows it out with
        // Finish(Leave), which opens the guest's OWN results screen. The guest
        // does not drive the campaign UI, so that screen sits there forever, this
        // wait never ends, and the load for the next mission is never taken. The
        // host meanwhile starts mission 3 and plays on alone -- seen live as the
        // guest pinned in mission 1 logging ROUND RUNAWAY nearly two hundred
        // times while the roster check correctly reported "NOT IN THE SAME
        // BATTLE".
        //
        // Closing it here is not a campaign decision: the campaign has already
        // moved on and this is the guest's own local screen, showing a mission
        // that is over. A short grace period first, so a human guest gets a look
        // at their results before the next mission pulls them in.
        if (ResultScreenOpen)
        {
            if (CoopNet.Mode == CoopMode.Guest && _loadQueuedAt > 0
                && Time.realtimeSinceStartup - _loadQueuedAt > 6f && !_resultCloseTried)
            {
                _resultCloseTried = true;
                try
                {
                    var rs = UIManager.Get()?.GetActiveScreen()?.TryCast<Menace.UI.MissionResult.MissionResultUIScreen>();
                    if (rs != null)
                    {
                        rs.CloseScreen();
                        Plugin.Logger.LogWarning("[SESSION] Closed our results screen -- the host has moved on to the next mission");
                    }
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"[SESSION] Could not close the results screen: {e.Message}"); }
            }
            return;
        }
        _resultCloseTried = false;
        // Normally wait until we've left our own battle before loading. But if
        // the host loaded an earlier (strategy) save while we're still in a
        // battle it has abandoned, our battle will never resolve in sync --
        // force the load after a grace period so the guest isn't stuck forever.
        // A reload is the host deliberately pulling us out of this battle -- no grace.
        if (TacticalManager.Get() != null && Time.realtimeSinceStartup - _loadQueuedAt < 12f
            && !GuestReload.GuestCatchingUp) return;
        if (TacticalManager.Get() != null)
            Plugin.Logger.LogInfo("[SESSION] Forcing sync load out of a battle the host has left");

        string name = _queuedLoad;
        string path = SaveTransfer.PathFor(name);

        // The save may be transiently unreadable (host still flushing it, or a
        // shared Saves folder mid-write). Retry a few times before giving up.
        bool parsed = false;
        SaveState state = null;
        try
        {
            parsed = SaveSystem.TryGetSaveState(path, out state);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[SESSION] TryGetSaveState threw: {e}");
        }
        if (!parsed || state == null)
        {
            _loadParseRetries++;
            if (_loadParseRetries <= 3)
            {
                _loadCooldownUntil = Time.realtimeSinceStartup + 1f; // try again shortly
                Plugin.Logger.LogWarning($"[SESSION] Sync save not readable yet (attempt {_loadParseRetries}); file exists={File.Exists(path)} size={(File.Exists(path) ? new FileInfo(path).Length : -1)}");
                return; // keep _queuedLoad set
            }
            _queuedLoad = null;
            _loadParseRetries = 0;
            Plugin.Logger.LogError($"[SESSION] Could not parse synced save at '{path}' after retries");
            CoopNet.Status = "Sync load failed -- ask host to re-sync";
            CoopRunner.SetSyncBlocker(false);
            return;
        }
        _loadParseRetries = 0;
        _queuedLoad = null;
        // A reload's mission launch follows at once and nothing else is queued, so
        // the long settle window only kept the host frozen (run 31: 20 s idle).
        // No settle timer when the game's own load-finished signal is available:
        // the next load / mission start / prep mirror waits on _awaitingLoadDone
        // (user rule 2026-10-03: wait on the real condition, not N seconds).
        _loadCooldownUntil = SaveLoadPatches.Bound ? 0f
            : Time.realtimeSinceStartup + (GuestReload.GuestCatchingUp ? 4f : 20f);
        // the load tears down any mirrored planning screen
        _viewerScreen = null;
        _expectViewer = false;
        // A mirror request still QUEUED is meant to open after this load (the
        // host opened planning, then sent the save). Clearing the flag made the
        // mirror think the host had left and close itself at once (r118).
        if (_queuedPrepIdx < 0) _hostWantsPrep = false;

        Plugin.Logger.LogInfo($"[SESSION] Auto-loading synced save '{name}'");
        CoopNet.Status = "Loading host's game...";
        CoopRunner.SetSyncBlocker(true, "LOADING HOST'S GAME...");
        _awaitingLoadDone = true;
        _preLoadStrategyPtr = StrategyState.Get()?.Pointer ?? IntPtr.Zero;
        _preLoadProcessed = SaveLoadPatches.Completed;
        _awaitingSince = Time.realtimeSinceStartup;
        RemoteLoadInProgress = true;
        try { SaveSystem.ExecLoad(state); }
        finally { RemoteLoadInProgress = false; }
    }

    private static int _loadParseRetries;
}

// MAP GENERATION DETERMINISM.
//
// Separate patch class so a signature change in MapgenInput disables only this
// hook (SafePatch catches per class) instead of taking the whole replication
// layer down with it. REMEMBER TO REGISTER IT IN Plugin.Load -- a patch class
// that is never handed to SafePatch is silently inert, and this file has been
// bitten by exactly that before.
[HarmonyPatch]
internal static class MapgenPatches
{
    // Postfix, not prefix: the constructor is what CREATES the Rng, so there is
    // nothing to re-seed until it has run.
    //
    // KEPT, BUT NOT TRUSTED ALONE. Across every run so far this hook has never
    // once logged -- the class binds, the strategy pin beside it logs fine, and
    // this produces nothing. IL2CPP constructor patching is the least reliable
    // thing Harmony does here, and a pin that silently never runs is worse than
    // no pin, because we were reading matching maps as evidence it worked.
    [HarmonyPatch(typeof(Menace.Tactical.Mapgen.MapgenInput), MethodType.Constructor,
                  typeof(Mission), typeof(Menace.Tactical.Mapgen.IMapGenerator))]
    [HarmonyPostfix]
    private static void MapgenInputCreated(Menace.Tactical.Mapgen.MapgenInput __instance)
    {
        SessionSync.PinMapgenInput(__instance);
    }

    // THE ONE THAT SHOULD ACTUALLY FIRE. An ordinary instance method that takes
    // the MapgenInput, called early in generation -- no constructor involved.
    // PinMapgenInput only acts on the first sighting of each input, so calling it
    // from a method invoked many times per run is safe.
    [HarmonyPatch(typeof(Menace.Tactical.Mapgen.ChunkGenerator),
                  nameof(Menace.Tactical.Mapgen.ChunkGenerator.ModifyBlueprint))]
    [HarmonyPrefix]
    private static void BeforeModifyBlueprint(Menace.Tactical.Mapgen.MapgenInput _input)
    {
        SessionSync.PinMapgenInput(_input);
    }

    // EVERY GENERATOR'S OWN STREAM. Init is non-virtual and public on
    // BaseMapGenerator, so this single hook covers ChunkGenerator,
    // EnvironmentFeatureGenerator, EnvironmentPropGenerator and the rest -- no
    // per-subclass list to keep in step with the game. A prefix taking `ref int`
    // rewrites the seed the engine is about to use.
    //
    // Unlike the constructor hook above, this is an ordinary instance method:
    // patching it is the reliable kind, not the kind that binds and never fires.
    // The API check will now say at load whether both are actually patched.
    [HarmonyPatch(typeof(Menace.Tactical.Mapgen.BaseMapGenerator),
                  nameof(Menace.Tactical.Mapgen.BaseMapGenerator.Init))]
    [HarmonyPrefix]
    private static void BeforeGeneratorInit(Menace.Tactical.Mapgen.BaseMapGenerator __instance, ref int _seed)
    {
        SessionSync.PinGeneratorSeed(__instance, ref _seed);
    }
}

// WHEN A SAVE HAS REALLY FINISHED LOADING (#65). SaveSystem creates the new
// StrategyState and then fills it in the StrategyState.ProcessSaveState coroutine;
// the load is done when that coroutine's MoveNext returns false. Also logs what the
// coroutine is waiting on whenever that changes, so a slow load shows why.
[HarmonyPatch]
internal static class SaveLoadPatches
{
    internal static int Completed;
    private static string _lastStep;
    private static int _stepLines;
    private static bool? _bound;

    // True once Harmony reports our postfix on the coroutine. If the class failed
    // to bind, SessionSync falls back to the old "new state object" rule instead
    // of waiting for a signal that can never come.
    internal static bool Bound
    {
        get
        {
            if (_bound.HasValue) return _bound.Value;
            try
            {
                var m = TargetMethod();
                var info = m != null ? HarmonyLib.Harmony.GetPatchInfo(m) : null;
                _bound = info != null && info.Postfixes.Count > 0;
            }
            catch { _bound = false; }
            Plugin.Logger.LogInfo($"[SESSION] Save-load completion hook {(_bound.Value ? "bound" : "NOT bound -- using the state-object rule")}");
            return _bound.Value;
        }
    }

    // The coroutine is a compiler-generated class whose number moves whenever the
    // game adds a method to StrategyState (_d__91 in 0.7.15, _d__96 in 0.7.16), so
    // it is found by name prefix rather than compiled in. A rename then costs this
    // one log line, not the build.
    private static System.Type _coroutineType;
    private static System.Type CoroutineType()
    {
        if (_coroutineType != null) return _coroutineType;
        foreach (var t in typeof(StrategyState).GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            if (t.Name.StartsWith("_ProcessSaveState_d__")) { _coroutineType = t; break; }
        return _coroutineType;
    }

    private static System.Reflection.MethodBase TargetMethod()
    {
        var t = CoroutineType();
        return t != null ? AccessTools.Method(t, "MoveNext") : null;
    }

    [HarmonyPostfix]
    private static void AfterStep(object __instance, bool __result)
    {
        try
        {
            if (!__result)
            {
                Completed++;
                Plugin.Logger.LogInfo($"[SESSION] Game finished processing the loaded save (#{Completed})");
                _lastStep = null;
                _stepLines = 0;
                return;
            }
            var tr = Traverse.Create(__instance);
            var cur = tr.Property("__2__current").GetValue<Il2CppSystem.Object>();
            string step = $"state={tr.Property("__1__state").GetValue<int>()} waits={(cur == null ? "frame" : cur.GetIl2CppType()?.Name)}";
            if (step == _lastStep || _stepLines >= 20) return;
            _lastStep = step;
            _stepLines++;
            Plugin.Logger.LogInfo($"[SESSION] Loading save: {step}");
        }
        catch { }
    }
}

