using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
#if MELON
using Il2CppMenace.Strategy;
using Il2CppMenace.UI;
using Il2CppMenace.UI.Strategy;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.Strategy;
using Menace.UI;
using Menace.UI.Strategy;
#endif
using UnityEngine.UIElements;

namespace MenaceCoop;

// UI AUTOPILOT -- drives the host from the title screen into a battle so the
// turn-cycle test can run unattended. Dev tooling; gated on SelfTestAutoPlay
// and host-only (the guest follows the host through the sync path it always
// uses, which is itself part of what we are testing).
//
// The engine's buttons are `InteractiveElement`s, and OnClicked(MouseButton) is
// public -- so a button can be pressed exactly as a mouse press would, without
// simulating input. Elements register themselves here when their click action
// is installed, which is how we can find them by name later.
internal static class UiAutopilot
{
    // Every live clickable. Registered at CONSTRUCTION -- the click-action
    // setter is virtual and subclasses override it, so hooking the base setter
    // caught nothing. The pickable element does not exist yet at that point, so
    // names are resolved lazily at lookup time instead of stored.
    private static readonly List<InteractiveElement> _clickables = new();

    internal static void Register(InteractiveElement el)
    {
        if (el == null) return;
        _clickables.Add(el);
        if (_clickables.Count > 4000) _clickables.RemoveRange(0, 2000);
    }

    // The pickable child is called "Pickable" on every single button -- the
    // identifying name is on its PARENT (e.g. Root/.../PlanMissionButton/Pickable).
    private static string NameOf(InteractiveElement el)
    {
        try
        {
            var pe = el?.m_PickableElement;
            if (pe == null) return "";
            string own = pe.name ?? "";
            if (own != "Pickable") return own;
            return pe.parent?.name ?? own;
        }
        catch { return ""; }
    }

    private static string _lastScreen = "";
    private static float _nextStep;
    private static bool _loadRequested;
    private static bool _resultStopLogged;
    private static float _screenSince;

    private static string TopScreen()
    {
        try
        {
            var open = UIManager.Get()?.m_OpenScreens;
            if (open == null || open.Count == 0) return "";
            for (int i = open.Count - 1; i >= 0; i--)
                if (open[i] != null) return open[i].GetIl2CppType().Name;
        }
        catch { }
        return "";
    }

    // Once the autopilot decides it cannot reach a battle, it must STOP touching
    // the UI. Retrying forever is how a single bad navigation became a
    // per-frame exception spiral.
    private static bool _stopped;

    private static void StopWithReason(string why)
    {
        if (_stopped) return;
        _stopped = true;
        Plugin.Logger.LogWarning($"[UIAUTO] STOPPING: {why}. Load a save that is parked on a ready mission and re-run.");
    }

    // Confirm any yes/no dialog that is blocking play. Returns true if one was
    // handled this tick.
    private static bool ConfirmOpenDialog()
    {
        try
        {
            var ui = UIManager.Get();
            if (ui == null || !ui.IsDialogOpen()) return false;
            var dlg = ui.GetCurrentDialog();
            var simple = dlg?.TryCast<SimpleDialog>();
            if (simple?.m_YesButton == null) return false;

            Plugin.Logger.LogInfo("[UIAUTO] confirming dialog (Yes)");
            simple.m_YesButton.OnClicked(MouseButton.LeftMouse);
            return true;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[UIAUTO] dialog confirm failed: {e.Message}"); }
        return false;
    }

    private static bool HasActiveOperation()
    {
        try
        {
            var op = Menace.States.StrategyState.Get()?.GetCurrentOperation();
            return op != null;
        }
        catch { return false; }
    }

    // Every save on this machine is parked on the planet screen with no
    // operation running -- the test runs consumed the ones that existed. Adopt
    // the first available operation so a mission can be reached again. Tried
    // once, and if it does not take, the caller stops rather than looping.
    private static bool _operationAttempted;

    // Set when a reload is issued for the next mission; cleared once the fresh
    // operation has actually been handed a different mission to run.
    private static bool _rotateAfterLoad;

    // CLOSING THE RESULTS LEAVES NOTHING BEHIND IT.
    //
    // The old note in this file said force-closing the results screen "strands
    // the game on an empty screen", and it was right about the symptom: the
    // screen closes, EmptyUIScreen is all that remains, and nothing brings the
    // strategy layer back on its own. The conclusion drawn from that was to stop
    // closing it and reload a save instead -- which restarted the same mission
    // and dragged the guest through a desync every time.
    //
    // The screen just has to be asked for. Closing is correct; it simply is not
    // the whole move.
    private static bool _needStrategyScreen;
    private static float _strategyWaitSince;
    private static bool _waitedForPlayers;

    // A MISSION THE HARNESS CANNOT WIN MUST NOT HOLD THE WHOLE RUN.
    //
    // Auto-combat marches far better than it shoots: one mission reached round 54
    // having killed five enemies, with four still standing and both round-limit
    // objectives long since failed. The turn cycle was flawless throughout --
    // host and guest in lockstep every round, no watchdog, no runaway -- so
    // nothing was broken; the fight simply was not going to end.
    //
    // Coverage is the reason the harness loops at all, and one unwinnable map can
    // eat an entire test session. Leaving is the same exit the guest already uses
    // when it follows the host out, and it lands on the results screen, which the
    // normal path then closes on its way to the next mission.
    private static bool _abandonLogged;

    private static bool AbandonIfDragging()
    {
        try
        {
            int cap = Plugin.SelfTestMaxRounds?.Value ?? 30;
            if (cap <= 0) return false;

            var tm = Menace.Tactical.TacticalManager.Get();
            if (tm == null) { _abandonLogged = false; return false; }
            if (tm.GetRound() <= cap) return false;
            if (_abandonLogged) return false;

            _abandonLogged = true;
            Plugin.Logger.LogWarning($"[UIAUTO] Mission still running at round {tm.GetRound()} (cap {cap}) -- abandoning it so the run can move on.");
            tm.Finish(Menace.Tactical.TacticalFinishReason.Leave);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[UIAUTO] could not abandon the mission: {e.Message}");
            return false;
        }
    }

    private static bool ReturnToStrategy(string screen)
    {
        // Already somewhere useful again -- nothing to do.
        if (!string.IsNullOrEmpty(screen) && !screen.StartsWith("Empty"))
        {
            _needStrategyScreen = false;
            _strategyWaitSince = 0f;
            return false;
        }

        float now = UnityEngine.Time.realtimeSinceStartup;
        if (_strategyWaitSince == 0f) { _strategyWaitSince = now; return true; }
        if (now - _strategyWaitSince < 3f) return true;      // give the close time to land

        _needStrategyScreen = false;
        _strategyWaitSince = 0f;
        try
        {
            // The screen's OWN opener, not UIManager.OpenScreen by name.
            //
            // Opening it by prefab name gives OnOpened no parameters and it
            // throws a NullReferenceException on the way up. The screen came up
            // anyway, so it looked like it worked -- but a screen that opens by
            // way of an exception is not something to build a test loop on.
            MissionSelectUIScreen.Open();
            Plugin.Logger.LogWarning("[UIAUTO] Results closed -- reopening the strategy layer to pick the next mission");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[UIAUTO] could not reopen the strategy layer: {e.Message}"); }
        return true;
    }

    private static void RotateMission()
    {
        try
        {
            var op = Menace.States.StrategyState.Get()?.GetCurrentOperation();
            var missions = op?.m_Missions;
            if (op == null || missions == null || missions.Count == 0) return;   // load not landed yet

            _rotateAfterLoad = false;

            if (missions.Count == 1)
            {
                Plugin.Logger.LogWarning("[UIAUTO] This operation offers ONE mission, so looping replays it. " +
                                         "Broader coverage needs a save on an operation with several missions, or a different campaign.");
                return;
            }

            int i = _missionPick % missions.Count;
            var chosen = missions[i];
            if (chosen == null) return;
            op.SetCurrentMission(chosen);
            Plugin.Logger.LogWarning($"[UIAUTO] Next mission: {i + 1}/{missions.Count} (idx={chosen.GetIdx()} seed={chosen.GetSeed()})");
        }
        catch (Exception e)
        {
            _rotateAfterLoad = false;
            Plugin.Logger.LogWarning($"[UIAUTO] could not rotate the mission: {e.Message}");
        }
    }

    // WHAT ENEMIES CAN THIS SAVE ACTUALLY PUT ON A BOARD?
    //
    // Every test this project has run has been against one alien operation on one
    // 42x42 map, which is a poor basis for claiming anything works. The save knows
    // what else it could offer -- each operation carries its enemy faction and its
    // mission list -- and nothing was ever asking it.
    //
    // Logged once per campaign load so picking a human/pirate operation to test
    // against is a matter of reading the log rather than clicking through menus.
    private static bool _operationsListed;

    internal static void ListOperations()
    {
        if (_operationsListed) return;
        try
        {
            var ops = Menace.States.StrategyState.Get()?.Operations;
            if (ops == null) return;

            var lines = new List<string>();
            for (int i = 0; i < 12; i++)
            {
                Operation op = null;
                try { op = ops.GetAvailableOperationByIdx(i); } catch { }
                if (op == null) continue;

                string enemy = "?", client = "?";
                int missions = 0;
                try { enemy = op.GetEnemyFaction()?.name ?? "?"; } catch { }
                try { client = op.GetClientFaction()?.name ?? "?"; } catch { }
                try { missions = op.m_Missions?.Count ?? 0; } catch { }
                lines.Add($"#{i} enemy={enemy} client={client} missions={missions}");
            }
            if (lines.Count == 0) return;

            _operationsListed = true;
            Plugin.Logger.LogWarning("[UIAUTO] Operations available in this save:");
            foreach (var l in lines) Plugin.Logger.LogWarning("[UIAUTO]   " + l);

            var cur = Menace.States.StrategyState.Get()?.GetCurrentOperation();
            if (cur != null)
            {
                string e = "?";
                try { e = cur.GetEnemyFaction()?.name ?? "?"; } catch { }
                Plugin.Logger.LogWarning($"[UIAUTO] Current operation fights: {e}");
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[UIAUTO] could not list operations: {e.Message}"); }
    }

    private static bool TryActivateAnOperation()
    {
        if (_operationAttempted) return false;
        _operationAttempted = true;
        try
        {
            var ops = Menace.States.StrategyState.Get()?.Operations;
            if (ops == null) return false;

            // Index accessor rather than GetAvailableOperations(): the interop
            // IReadOnlyList exposes no usable Count.
            for (int i = 0; i < 8; i++)
            {
                Operation chosen = null;
                try { chosen = ops.GetAvailableOperationByIdx(i); } catch { }
                if (chosen == null) continue;
                ops.SetCurrentOperation(chosen);
                if (HasActiveOperation())
                {
                    Plugin.Logger.LogInfo($"[UIAUTO] adopted available operation #{i} so a mission can be started");
                    return true;
                }
            }
            Plugin.Logger.LogWarning("[UIAUTO] no operations available to start");
            return false;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[UIAUTO] could not start an operation: {e.Message}");
            return false;
        }
    }

    internal static void Tick()
    {
        // DEV HARNESS ONLY. Auto-combat is a player feature that fights their
        // squads; it must never start driving their menus, loading saves or
        // launching missions for them.
        if (!SelfTest.DevAutoPlay) return;

        // BOTH sides must answer dialogs: the guest uses the End Turn path too,
        // and its confirmation popup would block its units just the same. This
        // sits above the guest early-out deliberately.
        if (ConfirmOpenDialog()) return;

        if (_stopped) return;
        if (CoopNet.Mode == CoopMode.Guest) return;   // host drives; guest mirrors
        // v5 rigs: wait for EVERY expected player before driving anything, or the
        // mission starts with the late ones outside it.
        int want = System.Math.Max(2, Plugin.SelfTestPlayers?.Value ?? 2) - 1;
        if (CoopNet.Mode == CoopMode.Host && CoopNet.ClientCount < want)
        {
            if (!_waitedForPlayers) Plugin.Logger.LogInfo($"[UIAUTO] waiting for {want} client(s) ({CoopNet.ClientCount} connected)");
            _waitedForPlayers = true;
            return;
        }
        if (_waitedForPlayers) { _waitedForPlayers = false; Plugin.Logger.LogInfo($"[UIAUTO] all {want} client(s) connected -- driving"); }
        // A saved battle is being resumed: SessionSync drives the load, prep and
        // launch itself, with the saved mission and seed. Hands off.
        if (BattleSave.Restoring || SessionSync.AutoLaunchPending) return;

        string screen = TopScreen();
        if (screen != _lastScreen)
        {
            _lastScreen = screen;
            _pressAttempts.Clear();          // fresh screen, fresh press budget
            // Fresh roster-filling budget too, so looping to a second mission is
            // not judged on the first one's attempts.
            _assignIdleTicks = 0;
            _assignGaveUp = false;
            _screenSince = UnityEngine.Time.realtimeSinceStartup;
            _nextStep = UnityEngine.Time.realtimeSinceStartup + 2f;  // let it settle
            Plugin.Logger.LogInfo($"[UIAUTO] screen -> {screen}");
            // Never walk the splash: on v0.7.15 doing so (GetRootElement + tree
            // walk) on the splash's first frames made IntroSkip's skip throw a
            // NullReferenceException in UIScreen.Cleanup -- every harness host
            // (r85-r89), never a guest or a player launch. Nothing to click there.
            if (screen != "SplashUIScreen" && screen != "EmptyUIScreen")
                DumpClickables(screen);
        }

        // ROTATE THE MISSION BEFORE ANY SCREEN GETS A CHANCE TO PICK ONE.
        //
        // The rotation used to live in the MissionSelectUIScreen step, and that
        // step never ran: the reloaded save opens mission select and moves to
        // the prep screen inside the harness's own two-second settle delay, so
        // by the time it looked, the mission had already been chosen. Every
        // "next mission" was therefore the same mission -- idx=1, seed 7719273,
        // the same map and the same ten aliens, run after run, which is no
        // coverage at all.
        //
        // Setting it on the operation right after the load lands is ahead of
        // every screen that reads it.
        if (_rotateAfterLoad) RotateMission();
        if (_needStrategyScreen && ReturnToStrategy(screen)) return;
        if (AbandonIfDragging()) return;

        if (UnityEngine.Time.realtimeSinceStartup < _nextStep) return;
        _nextStep = UnityEngine.Time.realtimeSinceStartup + 2f;

        try { Step(screen); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[UIAUTO] step failed on {screen}: {e.Message}"); }

        // Stuck on a screen the steps above don't know, with no battle running:
        // try for mission select anyway rather than sitting there. Campaign
        // progress changes which screens a save opens on, and the point of the
        // run is to reach combat.
        if (_lastScreen == screen && Menace.Tactical.TacticalManager.Get() == null
            && screen != "MissionResultUIScreen" && screen != "MissionPrepUIScreen"
            && screen != "MissionSelectUIScreen" && screen != "TitleUIScreen"
            && !screen.StartsWith("Empty") && !screen.StartsWith("Splash")
            && UnityEngine.Time.realtimeSinceStartup - _screenSince > 15f)
        {
            _screenSince = UnityEngine.Time.realtimeSinceStartup;
            Plugin.Logger.LogWarning($"[UIAUTO] unhandled screen '{screen}' -- looking for a way to mission select");
            PressNav("MissionSelectButton");
        }
    }

    private static void Step(string screen)
    {
        switch (screen)
        {
            // Title: load the most recent save. This is the "run a save on host"
            // step -- no campaign setup, no menu walking.
            case "TitleUIScreen":
            {
                if (_loadRequested) return;

                // PREFER THE PINNED BASELINE. Loading "the latest save" made
                // every run start from wherever the previous run left the
                // campaign -- so runs drifted until the operation had no
                // missions left and the harness could not reach combat at all.
                // coop_test.save is a fixed starting point, restored before each
                // run, so runs are comparable.
                SaveState save = null;
                try
                {
                    // Build the path from the save folder directly --
                    // GetSaveFilePath did not resolve this name and the pinned
                    // save was silently skipped in favour of "latest".
                    string pinned = System.IO.Path.Combine(
                        SaveSystem.GetAndCreateSaveFileFolderPath(), "coop_test.save");
                    if (SaveSystem.TryGetSaveState(pinned, out var s) && s != null)
                    {
                        save = s;
                        Plugin.Logger.LogInfo("[UIAUTO] loading pinned test save 'coop_test.save'");
                    }
                }
                catch { }

                if (save == null && SaveSystem.TryGetLatestSaveState(out var latest) && latest != null)
                {
                    save = latest;
                    Plugin.Logger.LogInfo("[UIAUTO] no pinned test save -- loading latest");
                }

                if (save != null) { _loadRequested = true; SaveSystem.Load(save); }
                else Plugin.Logger.LogWarning("[UIAUTO] no save to load");
                return;
            }

            // Any strategy-layer screen: head for mission select. Which one the
            // save opens on depends on campaign progress, so this must not
            // assume a single entry screen.
            case "StrategyUIScreen":
            case "SystemMapUIScreen":
            case "OperationSelectUIScreen":
            case "ShipUIScreen":
                // NEVER navigate to mission select without an active operation.
                // That screen resolves its background scene from the operation,
                // so with none it throws inside BeforeActivate -- every frame,
                // forever (3131 NREs in one run). The nav button is pressable
                // even when the destination cannot build, so check first.
                if (!HasActiveOperation() && !TryActivateAnOperation())
                {
                    StopWithReason("this save has no active operation and none is available to start");
                    return;
                }
                PressNav("MissionSelectButton");
                return;

            // Both mission screens expose their own button as a field, so the
            // real button is pressed directly -- no element search, no guessing
            // at UXML names, and it stays correct if the layout changes.
            case "MissionSelectUIScreen":
                // The plan button only exists once a mission POI has been
                // clicked, and whether the save opens with one already selected
                // depends on campaign progress. If it is absent, open the
                // deployment screen straight from the operation's mission list
                // instead of trying to click a map pin.
                // WHEN LOOPING, PICK A MISSION -- DO NOT TAKE THE PRE-SELECTED ONE.
                //
                // Plan Mission plans whatever the save already has selected, and
                // the harness reloads the same pinned save every cycle, so every
                // "next mission" was the same mission: idx=1, seed 7719273, the
                // same forty-two-by-forty-two map against the same ten aliens,
                // run after run. The rotation in OpenFirstMissionDirectly existed
                // the whole time and was unreachable, because Plan Mission was
                // tried first and always succeeded.
                //
                // Coverage is the entire point of looping, so when it is on the
                // rotating path goes first.
                if (Plugin.SelfTestLoopMissions?.Value ?? false)
                {
                    OpenFirstMissionDirectly();
                    return;
                }
                // v0.7.15: the button exists before any mission is picked, and
                // pressing it then throws inside the game's own handler (r85).
                var selectScreen = TopScreenAs<MissionSelectUIScreen>();
                if (selectScreen?.m_SelectedPoi != null
                    && Press(selectScreen.m_PlanMissionButton, "PlanMission")) return;
                OpenFirstMissionDirectly();
                return;

            // THE PREP SCREEN CAN ARRIVE WITH AN EMPTY BATTLE PLAN, AND THEN
            // START MISSION IS DISABLED FOREVER.
            //
            // Every save the harness had used until 2026-09-19 happened to have
            // squads already assigned to its next mission, so pressing the button
            // was the whole job. The save made to test vehicles and reinforcement
            // waves does not, and the harness sat on `LaunchMission not
            // interactable yet` for the rest of the run -- sync and the launch
            // gate were both fine, it simply never filled the mission in.
            //
            // Assigning units is not a harness-only concern, and that is the
            // reason this is safe: BattlePlan.AddUnit / RemoveUnit are patched
            // (Replication.MirrorAddUnit) and relay `dadd` / `drem`, which
            // SessionSync.HandlePlanUnitChange applies on the guest across BOTH
            // layers -- the plan and the on-map marker. So filling the roster the
            // normal way keeps the two machines fielding the same army. Doing it
            // by writing m_EntitiesToDeploy directly would not.
            case "MissionPrepUIScreen":
            {
                var prep = TopScreenAs<MissionPrepUIScreen>();
                // Saves that already have squads assigned reach Press on the
                // first tick exactly as before: nothing below runs for them.
                if (prep != null && !LaunchEnabled(prep) && AssignSquads(prep)) return;
                Press(prep?.m_StartMissionButton, "StartMission");
                return;
            }

            // Clicking through the results is what releases the guest: the host
            // only pushes the post-mission sync when this screen CLOSES. Leaving
            // it open strands the guest in a finished battle.
            case "MissionResultUIScreen":
            {
                var rs = TopScreenAs<Menace.UI.MissionResult.MissionResultUIScreen>();

                // LET THE SCREEN FINISH BEING BORN BEFORE PRESSING IT.
                //
                // BaseButton carries m_CreationFrameCount, which is how a UI
                // stops a click from the previous screen falling through onto a
                // button that has just appeared under the cursor. Pressing on the
                // very first tick lands inside that guard and is discarded.
                //
                // The old code pressed immediately, burned its whole retry
                // budget inside the guard window, and concluded the button
                // "needs a real mouse". It also never reset that budget, so once
                // spent it was spent for the rest of the session.
                //
                // This screen also has TWO pages -- mission summary, then the
                // unit roster -- so the first successful Next flips
                // m_ShowUnitsWindow rather than leaving. Logging that flag makes
                // "the click was swallowed" distinguishable from "the click
                // worked and there was simply another page".
                if (UnityEngine.Time.realtimeSinceStartup - _screenSince < 2f) return;

                bool page = false;
                try { page = rs?.m_ShowUnitsWindow ?? false; } catch { }
                if (page != _lastResultPage)
                {
                    _lastResultPage = page;
                    _pressAttempts.Remove("ResultNext");   // it responded: earn a fresh budget
                    Plugin.Logger.LogInfo($"[UIAUTO] ResultNext worked -- result screen now showing {(page ? "the unit roster" : "the mission summary")}");
                }

                if (Press(rs?.m_NextButton, "ResultNext")) return;

                // LEAVE THE SCREEN, DO NOT RELOAD A SAVE.
                //
                // Reloading was the old way out and it was the wrong way out. It
                // rebuilds the strategy layer from disk, which puts the campaign
                // back exactly where the save was -- so the "next" mission was the
                // same mission, idx and seed unchanged, restarting rather than
                // rotating. Worse, it yanks the host onto a fresh strategy layer
                // while the guest is still finishing the old battle, and the guest
                // spends the next minute logging ROUND RUNAWAY trying to follow.
                //
                // Closing the screen is the route a player takes: results close,
                // the strategy layer comes back, a new mission gets picked there,
                // and the guest joins through the ordinary mission-start handoff
                // it already handles correctly.
                //
                // The old note here said force-closing "strands the game on an
                // empty screen". That was observed while auto-combat was still
                // playing the finished battle underneath -- the game had a live
                // tactical layer it could not leave. With that fixed, the close
                // has somewhere to go.
                if (rs != null && UnityEngine.Time.realtimeSinceStartup - _screenSince > 6f)
                {
                    if (_battleSeenSinceRestart)
                    {
                        _missionsCompleted++;
                        _battleSeenSinceRestart = false;
                        Plugin.Logger.LogWarning($"[UIAUTO] Mission {_missionsCompleted} complete -- closing the results to get back to the strategy layer.");
                    }
                    if (!(Plugin.SelfTestLoopMissions?.Value ?? false))
                    {
                        StopWithReason($"mission {_missionsCompleted} complete -- staying on one mission (Dev/SelfTestLoopMissions is off)");
                        return;
                    }
                    _missionPick++;                  // a different mission on the way back in
                    _rotateAfterLoad = true;         // applied as soon as the strategy layer is up
                    try { rs.CloseScreen(); }
                    catch (Exception e) { Plugin.Logger.LogWarning($"[UIAUTO] CloseScreen failed: {e.Message}"); }
                    _needStrategyScreen = true;      // closing lands on nothing; see ReturnToStrategy
                    return;
                }
                return;
            }
        }
    }

    // Every strategy screen derives from StrategyUIScreen, which owns the real
    // navigation bar (StrategyNavigation.m_Buttons). The button objects identify
    // themselves by their pickable element's parent name, so they can be found
    // without relying on the array's order or on the clickable registry (which
    // never captured them -- these are NavigationButtons, built through a ctor
    // the registry hook does not see).
    private static bool PressNav(string buttonName)
    {
        try
        {
            var nav = TopScreenAs<StrategyUIScreen>()?.m_Navigation;
            var buttons = nav?.m_Buttons;
            if (buttons == null) { Plugin.Logger.LogWarning("[UIAUTO] no strategy navigation on this screen"); return false; }

            var names = new StringBuilder();
            for (int i = 0; i < buttons.Length; i++)
            {
                var b = buttons[i];
                if (b == null) continue;
                string n = NameOf(b);
                names.Append(' ').Append(n.Length == 0 ? "(?)" : n);
                if (!string.Equals(n, buttonName, StringComparison.OrdinalIgnoreCase)) continue;
                return Press(b, buttonName);
            }
            Plugin.Logger.LogWarning($"[UIAUTO] nav button '{buttonName}' not found. Available:{names}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[UIAUTO] nav press failed: {e.Message}"); }
        return false;
    }

    internal static int MissionsCompleted => _missionsCompleted;
    private static int _missionsCompleted;
    private static int _missionPick;          // rotate which mission we take

    // Guards against counting missions that never happened: a restart only
    // counts once a real battle has run since the previous one.
    private static bool _battleSeenSinceRestart;
    private static int _failedRestarts;


    private static bool _directOpenLogged;

    private static void OpenFirstMissionDirectly()
    {
        try
        {
            var op = Menace.States.StrategyState.Get()?.GetCurrentOperation();
            if (op == null)
            {
                StopWithReason("there is no current operation");
                return;
            }

            // The mission the save is parked on is the operation's CURRENT
            // mission, not an entry in m_Missions -- that list came back empty on
            // every save tried, while the screen still had a mission ready.
            try
            {
                var current = op.GetCurrentMission();
                if (current != null && MissionPrepUIScreen.TryOpen(current))
                {
                    Plugin.Logger.LogInfo("[UIAUTO] opened deployment for the operation's current mission");
                    return;
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[UIAUTO] current-mission open failed: {e.Message}"); }

            var missions = op.m_Missions;
            if (missions == null || missions.Count == 0)
            {
                StopWithReason("the operation has no current mission and an empty mission list");
                return;
            }
            // Rotate the starting point so consecutive runs take DIFFERENT
            // missions from the same operation where one is available, rather
            // than replaying the identical fight every time.
            for (int n = 0; n < missions.Count; n++)
            {
                int i = (n + _missionPick) % missions.Count;
                var mission = missions[i];
                if (mission == null) continue;
                if (!MissionPrepUIScreen.TryOpen(mission)) continue;

                // TELL THE SESSION LAYER WHICH MISSION THIS IS.
                //
                // Opening deployment directly skips the normal launch flow, so
                // SessionSync never learned the mission index -- and without it
                // the host never sends the auto-start, so the GUEST NEVER JOINS
                // THE BATTLE. It then sat outside receiving orders for units it
                // did not have ("never resolved" on every one). Any run in that
                // state is worthless: the host was playing alone.
                try
                {
                    SessionSync.OnHostMissionLaunch(mission.GetIdx(), mission.GetSeed());
                    Plugin.Logger.LogInfo($"[UIAUTO] registered mission idx={mission.GetIdx()} with the session layer");
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"[UIAUTO] could not register the mission: {e.Message}"); }

                Plugin.Logger.LogInfo($"[UIAUTO] opened deployment for mission {i + 1}/{missions.Count} directly");
                return;
            }
            if (!_directOpenLogged)
            {
                _directOpenLogged = true;
                Plugin.Logger.LogWarning($"[UIAUTO] none of the {missions.Count} mission(s) would open");
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[UIAUTO] direct mission open failed: {e.Message}"); }
    }

    private static T TopScreenAs<T>() where T : UIScreen
    {
        try
        {
            var open = UIManager.Get()?.m_OpenScreens;
            if (open == null) return null;
            for (int i = open.Count - 1; i >= 0; i--)
            {
                var s = open[i];
                if (s == null) continue;
                var cast = s.TryCast<T>();
                if (cast != null) return cast;
            }
        }
        catch { }
        return null;
    }

    // ---- Filling the battle plan -------------------------------------------

    // The button's own answer, which is what Press checks. m_IsLoadoutValid is
    // the screen's internal reason and is only logged, never trusted here: the
    // button is what actually has to go live.
    private static bool LaunchEnabled(MissionPrepUIScreen prep)
    {
        try
        {
            var b = prep?.m_StartMissionButton;
            return b != null && b.IsInteractable();
        }
        catch { return false; }
    }

    private static int _assignIdleTicks;
    private static bool _assignGaveUp;

    // WHY IS START MISSION STILL DEAD? Everything the prep screen weighs, in one
    // line each, so the answer is in the log instead of in the next guess.
    // Deliberately verbose and one-shot: it fires once per stuck prep screen.
    private static void DumpPrepState(MissionPrepUIScreen prep, BattlePlan plan, Menace.Strategy.Mission mission,
                                      Il2CppSystem.Collections.Generic.List<BaseUnitLeader> hired)
    {
        try
        {
            var max = SessionSync.MissionSupplies(mission);
            Plugin.Logger.LogWarning("[UIAUTO] ---- PREP STATE (why LaunchMission is disabled) ----");
            Plugin.Logger.LogWarning($"[UIAUTO]   loadoutValid={prep.m_IsLoadoutValid}  planUnits={PlanCount(plan)}  hired={hired.Count}");
            try { Plugin.Logger.LogWarning($"[UIAUTO]   maxSupplies={max.GetAmount()}  totalDeployCosts={plan.GetTotalDeployCosts().GetAmount()}"); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[UIAUTO]   supplies unreadable: {e.Message}"); }
            // Validate() is the plan's own verdict. Whatever it returns is the
            // shortfall the screen is unhappy about, if the gate is resources.
            try { Plugin.Logger.LogWarning($"[UIAUTO]   plan.Validate()={plan.Validate().GetAmount()}"); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[UIAUTO]   Validate() threw: {e.Message}"); }
            try { Plugin.Logger.LogWarning($"[UIAUTO]   UpdateSupplies(max)={prep.UpdateSupplies(max).GetAmount()}"); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[UIAUTO]   UpdateSupplies threw: {e.Message}"); }

            // Per deployed unit: is it actually ON the map, and where? A unit in
            // the plan but with no valid tile is the leading suspect -- the
            // deployment zone is a real constraint and AddDeployedUnitToMap does
            // not promise to satisfy it.
            var list = plan.GetEntitiesToDeploy()?.TryCast<Il2CppSystem.Collections.Generic.List<DeployedEntity>>();
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var de = list[i];
                    if (de == null) { Plugin.Logger.LogWarning($"[UIAUTO]   deployed[{i}] = null"); continue; }
                    string where;
                    try { var p = de.GetTilePosition(); where = $"({p.x},{p.y})"; } catch (Exception e) { where = $"unreadable ({e.Message})"; }
                    Plugin.Logger.LogWarning($"[UIAUTO]   deployed[{i}] at {where}");
                }
            }
            Plugin.Logger.LogWarning("[UIAUTO] ---- end PREP STATE ----");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[UIAUTO] PREP STATE dump failed: {e.Message}"); }
    }

    // GetEntitiesToDeploy returns an Il2Cpp IReadOnlyList, which carries no Count
    // of its own -- it has to be cast to the concrete List first. Same cast
    // SessionSync uses on this exact call. Diagnostic only, so -1 on failure.
    private static int PlanCount(BattlePlan plan)
    {
        try
        {
            return plan?.GetEntitiesToDeploy()
                ?.TryCast<Il2CppSystem.Collections.Generic.List<DeployedEntity>>()?.Count ?? -1;
        }
        catch { return -1; }
    }

    // Add hired units to the mission until START MISSION goes live. Returns true
    // when it changed something, so the caller waits a tick and lets the screen
    // settle before pressing.
    //
    // Deliberately one pass per tick rather than one unit per tick: each add
    // relays to the guest and repaints the prep screen, and a 20-unit roster
    // would otherwise take 20 seconds of the run to fill.
    private static bool AssignSquads(MissionPrepUIScreen prep)
    {
        if (_assignGaveUp) return false;
        try
        {
            // Fully qualified: StrategyState is in Menace.STATES, which this file
            // does not import, and adding the using pulls in names that collide
            // with Menace.UI.Strategy. The `Menace = Il2CppMenace` alias at the
            // top is what makes this spelling work under both loaders.
            var state = Menace.States.StrategyState.Get();
            var plan = state?.BattlePlan;
            var hired = state?.Roster?.GetHiredLeaders()
                ?.TryCast<Il2CppSystem.Collections.Generic.List<BaseUnitLeader>>();
            var mission = prep.GetMission();
            // Still being born. Not a failure -- say nothing and come back.
            if (plan == null || hired == null || mission == null) return false;

            // NOT mission.GetMaxSupplies(): it is a cache populated at launch and
            // reads ZERO beforehand, which made every loadout over budget and is
            // exactly why START MISSION stayed dead. See SessionSync.MissionSupplies.
            var maxSupplies = SessionSync.MissionSupplies(mission);
            int added = 0, refused = 0;

            // TRIM BEFORE FILLING. A save can arrive with its plan ALREADY over
            // budget -- measured on the pinned save: 5 pre-assigned units costing
            // 1930 against 1441. The fill loop skips units already in the plan,
            // so it only ever tried adding MORE, each of which also failed, and
            // the harness gave up on a loadout that was invalid from the start.
            // Remove from the end of the hired list until the plan fits; RemoveUnit
            // relays `drem` to the guest, so both machines stay on one roster.
            int trimmed = 0;
            for (int i = hired.Count - 1; i >= 0; i--)
            {
                if (!prep.UpdateSupplies(maxSupplies).IsBigger(maxSupplies)) break;
                var leader = hired[i];
                if (leader == null || !plan.ContainsUnit(leader)) continue;
                try
                {
                    var sel = prep.m_UnitSelector;
                    var slot = sel?.GetSlot(leader);
                    if (slot != null) sel.RemoveDeployedUnitFromMap(slot);
                }
                catch { }
                if (plan.RemoveUnit(leader))
                {
                    trimmed++;
                    prep.ProcessUnitChanges(true);
                    prep.UpdateLaunchButton();
                }
            }
            if (trimmed > 0)
            {
                _assignIdleTicks = 0;
                Plugin.Logger.LogInfo(
                    $"[UIAUTO] Plan was OVER BUDGET -- removed {trimmed} unit(s); cost now {prep.UpdateSupplies(maxSupplies).GetAmount()} of {maxSupplies.GetAmount()}, "
                    + $"launch={(LaunchEnabled(prep) ? "ENABLED" : "still disabled")}");
                return true;   // let the screen settle; the fill pass runs next tick
            }

            for (int i = 0; i < hired.Count; i++)
            {
                if (LaunchEnabled(prep)) break;
                var leader = hired[i];
                if (leader == null || plan.ContainsUnit(leader)) continue;

                // ASK THE ENGINE, DO NOT PREDICT. A leader can be unavailable for
                // this mission for reasons we have no business modelling (wounded,
                // committed elsewhere, out of slots). AddUnit returning false IS
                // the filter, and it is the same one the player's own clicks go
                // through.
                if (!plan.AddUnit(leader, false)) { refused++; continue; }

                // Two layers, same order the game's own add uses: plan, then the
                // on-map marker. The guest mirror does exactly this.
                try
                {
                    var sel = prep.m_UnitSelector;
                    if (sel != null && sel.GetDeployedEntity(leader) == null)
                        sel.AddDeployedUnitToMap(leader);
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"[UIAUTO] marker for added unit failed: {e.Message}"); }

                prep.ProcessUnitChanges(true);
                prep.UpdateLaunchButton();
                added++;

                // OVER BUDGET: back the last one out and stop. Supplies are a
                // real constraint -- carrying on adding would keep the loadout
                // invalid and the button dead, which is the bug we are fixing.
                if (prep.UpdateSupplies(maxSupplies).IsBigger(maxSupplies))
                {
                    try
                    {
                        var sel = prep.m_UnitSelector;
                        var slot = sel?.GetSlot(leader);
                        if (slot != null) sel.RemoveDeployedUnitFromMap(slot);
                    }
                    catch { }
                    plan.RemoveUnit(leader);
                    prep.ProcessUnitChanges(true);
                    prep.UpdateLaunchButton();
                    added--;
                    Plugin.Logger.LogInfo("[UIAUTO] Stopped filling the mission: one more unit would exceed supplies");
                    break;
                }
            }

            if (added > 0)
            {
                _assignIdleTicks = 0;
                int n = PlanCount(plan);
                Plugin.Logger.LogInfo(
                    $"[UIAUTO] Assigned {added} unit(s) to the mission (plan now {n}, {refused} unavailable), launch={(LaunchEnabled(prep) ? "ENABLED" : "still disabled")} valid={prep.m_IsLoadoutValid}");
                return true;
            }

            // Nothing left to add and the button is still dead. Dump WHY before
            // giving up: the first attempt at this stopped with "8 hired, 0
            // refused, plan holds 5, loadoutValid=False", which says the count is
            // fine and tells us nothing about the actual gate. These are the
            // numbers the screen itself judges the loadout on.
            if (_assignIdleTicks == 0) DumpPrepState(prep, plan, mission, hired);

            if (++_assignIdleTicks >= 5)
            {
                _assignGaveUp = true;
                StopWithReason(
                    $"could not fill this mission: {hired.Count} hired unit(s), {refused} refused by the engine, " +
                    $"plan holds {PlanCount(plan)}, loadoutValid={prep.m_IsLoadoutValid}");
            }
            return false;
        }
        catch (Exception e)
        {
            _assignGaveUp = true;
            Plugin.Logger.LogError($"[UIAUTO] Assigning squads failed: {e}");
            return false;
        }
    }

    private static readonly Dictionary<string, int> _pressAttempts = new();
    private static readonly Dictionary<string, float> _lastPressAt = new();
    private static bool _lastResultPage;

    private static bool Press(InteractiveElement button, string label)
    {
        if (button == null) { Plugin.Logger.LogWarning($"[UIAUTO] {label} button not available yet"); return false; }
        try
        {
            if (!button.IsInteractable()) { Plugin.Logger.LogInfo($"[UIAUTO] {label} not interactable yet"); return false; }

            // One attempt per second. A tick-rate burst spends every retry inside
            // the button.s own creation guard and proves nothing.
            float __now = UnityEngine.Time.realtimeSinceStartup;
            if (_lastPressAt.TryGetValue(label, out float __prev) && __now - __prev < 1f) return false;
            _lastPressAt[label] = __now;

            _pressAttempts.TryGetValue(label, out int tries);
            _pressAttempts[label] = tries + 1;

            // A REAL MOUSE SENDS A PRESS AND A RELEASE, NOT A "CLICK".
            //
            // OnClicked works for the TextButtons on the mission screens, so it
            // was assumed to be THE way to press a button. It is not: the result
            // screen's Next ignored a dozen OnClicked calls and ignored its own
            // left-click action invoked directly, and the conclusion drawn at the
            // time was that the button "requires a real mouse" and the run simply
            // had to stop there. That cost the harness the ability to finish a
            // mission at all -- every run reloaded a pinned save instead, so the
            // campaign never advanced and every test played the same operation
            // against the same faction.
            //
            // What a real mouse actually produces is OnPressStart followed by
            // OnPressEnd. Buttons that act on the release see nothing at all from
            // OnClicked. So the press PAIR is tried first now, because it is the
            // faithful reproduction, and the other two are fallbacks rather than
            // the primary route.
            // With one attempt a second this is a 12-second window, not a burst.
            if (tries >= 12) return false;

            // OnClicked FIRST. It is what actually works.
            //
            // The press/release pair looks more faithful to a real mouse and was
            // tried first for exactly that reason -- and it has never once moved
            // a button in this UI, including the results screen's Next, which
            // responded to OnClicked on the very next attempt after five wasted
            // press/release tries. Leading with it only spends the retry budget.
            if (tries < 6)
            {
                Plugin.Logger.LogInfo($"[UIAUTO] pressing {label}");
                button.OnClicked(MouseButton.LeftMouse);
                return true;
            }

            if (tries < 9)
            {
                Plugin.Logger.LogInfo($"[UIAUTO] {label}: OnClicked ignored {tries}x -- trying press+release");
                button.OnPressStart(MouseButton.LeftMouse);
                button.OnPressEnd(MouseButton.LeftMouse);
                return true;
            }

            var action = button.GetOnLeftClickedAction();
            if (action != null)
            {
                Plugin.Logger.LogInfo($"[UIAUTO] {label}: still ignored after {tries} tries -- invoking its handler directly");
                action.Invoke(button);
                return true;
            }
            return false;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[UIAUTO] {label} press failed: {e.Message}"); return false; }
    }

    // Click the first live element whose name contains any of the hints.
    private static bool ClickFirst(string screen, params string[] hints)
    {
        for (int i = _clickables.Count - 1; i >= 0; i--)
        {
            var el = _clickables[i];
            if (el == null) continue;
            string name = NameOf(el);
            if (name.Length == 0) continue;
            string lower = name.ToLowerInvariant();
            bool match = false;
            foreach (var h in hints) if (lower.Contains(h)) { match = true; break; }
            if (!match) continue;
            try
            {
                if (!el.IsInteractable()) continue;
                Plugin.Logger.LogInfo($"[UIAUTO] clicking '{name}' on {screen}");
                el.OnClicked(MouseButton.LeftMouse);
                return true;
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[UIAUTO] click '{name}' failed: {e.Message}"); }
        }
        Plugin.Logger.LogWarning($"[UIAUTO] no clickable matching [{string.Join(",", hints)}] on {screen}");
        return false;
    }

    // Names are not documented anywhere -- dump them so the hints above can be
    // corrected from a real run instead of guessed.
    private static void DumpClickables(string screen)
    {
        try
        {
            var seen = new HashSet<string>();
            var sb = new StringBuilder();
            for (int i = _clickables.Count - 1; i >= 0 && seen.Count < 60; i--)
            {
                string name = NameOf(_clickables[i]);
                if (name.Length == 0 || !seen.Add(name)) continue;
                sb.Append(' ').Append(name);
            }
            Plugin.Logger.LogInfo($"[UIAUTO] clickables on {screen}:{sb}");
        }
        catch { }
        DumpElementTree(screen);
    }

    // The InteractiveElement registry only catches buttons whose click action is
    // installed through the base method. Walking the screen's actual UIElements
    // tree shows everything that is really there, named as the UXML names it --
    // which is what the click hints have to match.
    private static void DumpElementTree(string screen)
    {
        try
        {
            var open = UIManager.Get()?.m_OpenScreens;
            if (open == null || open.Count == 0) return;
            UIScreen top = null;
            for (int i = open.Count - 1; i >= 0 && top == null; i--) top = open[i];
            var root = top?.GetRootElement();
            if (root == null) return;

            var sb = new StringBuilder();
            int count = 0;
            Walk(root, sb, ref count, 0);
            Plugin.Logger.LogInfo($"[UIAUTO] elements on {screen} ({count}):{sb}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[UIAUTO] element dump failed: {e.Message}"); }
    }

    private static void Walk(VisualElement ve, StringBuilder sb, ref int count, int depth)
    {
        if (ve == null || count > 150 || depth > 12) return;
        try
        {
            string n = ve.name;
            if (!string.IsNullOrEmpty(n)) { sb.Append(' ').Append(n); count++; }
            int kids = ve.childCount;
            for (int i = 0; i < kids; i++) Walk(ve.ElementAt(i), sb, ref count, depth + 1);
        }
        catch { }
    }
}

[HarmonyPatch]
internal static class UiAutopilotPatches
{
    // Register at construction: this is the one entry point every button and
    // every subclass of it must pass through.
    [HarmonyPatch(typeof(InteractiveElement), MethodType.Constructor, typeof(string))]
    [HarmonyPostfix]
    private static void OnConstructed(InteractiveElement __instance)
    {
        if (!SelfTest.DevAutoPlay) return;
        UiAutopilot.Register(__instance);
    }
}






