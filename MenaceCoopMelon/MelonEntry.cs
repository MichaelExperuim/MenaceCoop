using System;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(MenaceCoop.Plugin),
    MenaceCoop.Plugin.PluginName,
    MenaceCoop.Plugin.PluginVersion,
    MenaceCoop.Plugin.ModAuthor)]
// Must match what MelonLoader reports for the game ("Game Developer: Overhype
// Studios", "Game Name: Menace"), or the mod is refused as built for another game.
[assembly: MelonGame("Overhype Studios", "Menace")]
// DO NOT REMOVE. Without this MelonLoader patches every [HarmonyPatch] class in
// this assembly itself, and SafePatch below would then patch all 15 of them a
// SECOND time. See the comment on SafePatch for why we still patch by hand.
[assembly: HarmonyDontPatchAll]

namespace MenaceCoop;

// MELONLOADER ENTRY POINT.
//
// Replaces the BepInEx Plugin.cs with the SAME static surface -- Plugin.Logger,
// Plugin.Mode, Plugin.AutoCombat.Value and the rest -- so all 28 shared files
// compile unchanged under either loader. Only the glue differs:
//
//   settings  UserData\MelonPreferences.cfg, in [MenaceCoop*] sections,
//             instead of BepInEx\config\com.menacecoop.core.cfg
//   logging   MelonLoader\Latest.log instead of BepInEx\LogOutput.log
//   patching  see SafePatch
//
// Setting keys, defaults and descriptions are deliberately IDENTICAL to the
// BepInEx build's. Anything else would make the two builds behave differently
// for the same player and make a bug report impossible to attribute.
public class Plugin : MelonMod
{
    public const string PluginGuid = "com.menacecoop.core";
    public const string PluginName = "MenaceCoop";
    public const string ModAuthor = "MenaceCoop";
    // v4 STARTS THE MELONLOADER LINE.
    //
    // Same major.minor.patch scheme the BepInEx builds used, continued rather
    // than restarted, so a version number never means two different things. The
    // BepInEx build stays frozen at 3.14.0 as the last of its line. v5 is
    // reserved for more than two players (user, 2026-09-27).
    public const string PluginVersion = "5.3.0";   // v5: 2-4 players (user 2026-10-04: release as v5)

    internal static LogShim Logger;

    // READ BY THE AUTOCOMBAT BRAIN, which is compiled in from its own tree and
    // expects these two names on Plugin.
    //
    // Disabled is how MenaceAutoCombat stands down when it finds itself loaded
    // beside co-op. Co-op is the thing it stands down FOR, so here it is always
    // false -- but the brain and FastForward both test it, and a missing field
    // would mean forking their source.
    internal static bool Disabled;

    // A cheap, reliable fingerprint of the game BUILD -- Unity version plus the
    // il2cpp metadata file size, which changes with any code/type change. Used
    // to warn when two players are on different game versions (save format is
    // version-specific, so a mismatch breaks syncing).
    internal static string GameFingerprint = "unknown";

    // MODE IS A STRING HERE, NOT THE ENUM.
    //
    // MelonPreferences serialises through Tomlet, whose enum handling is not
    // something a release should depend on. Mode was only ever read inside this
    // file, so parsing it here costs nothing and removes the risk entirely.
    internal static MelonPreferences_Entry<string> ModeSetting;
    internal static CoopMode Mode = CoopMode.Off;

    internal static MelonPreferences_Entry<string> HostAddress;
    internal static MelonPreferences_Entry<int> Port;
    internal static MelonPreferences_Entry<string> PartnerSteamId;
    internal static MelonPreferences_Entry<string> SessionPassword;
    internal static MelonPreferences_Entry<string> PlayerName;
    internal static MelonPreferences_Entry<string> InstallId;
    internal static MelonPreferences_Entry<int> MaxPlayers;
    internal static MelonPreferences_Entry<bool> ShareCursor;
    internal static MelonPreferences_Entry<bool> ShareLine;
    internal static MelonPreferences_Entry<bool> ShowPartnerCursor;
    internal static MelonPreferences_Entry<bool> ShowPartnerLine;
    internal static MelonPreferences_Entry<bool> ShowOwnerLabels;
    internal static MelonPreferences_Entry<bool> SelfTestLoopMissions;
    internal static MelonPreferences_Entry<bool> SteamLobbyProbe;
    internal static MelonPreferences_Entry<bool> SteamSelfTest;
    internal static MelonPreferences_Entry<bool> SyncedDebug;
    internal static MelonPreferences_Entry<string> SyncedDebugScenario;
    internal static MelonPreferences_Entry<bool> ManualTurnEnd;
    internal static MelonPreferences_Entry<bool> LiveStrategy;
    internal static MelonPreferences_Entry<bool> SequentialPhases;
    internal static MelonPreferences_Entry<bool> AutoCombat;
    internal static MelonPreferences_Entry<bool> LockGameSpeed;
    internal static MelonPreferences_Entry<float> StallRecoverySeconds;
    internal static MelonPreferences_Entry<bool> ShowPhaseBanner;
    internal static MelonPreferences_Entry<bool> ShowWarningBanner;
    internal static MelonPreferences_Entry<bool> ShowAlertBanner;
    internal static MelonPreferences_Entry<bool> CheckEnemyRoster;
    internal static MelonPreferences_Entry<bool> PinMissionRng;
    internal static MelonPreferences_Entry<bool> CheckTerrain;
    internal static MelonPreferences_Entry<string> ConfigWrittenBy;
    internal static MelonPreferences_Entry<bool> SelfTestTrace;
    internal static MelonPreferences_Entry<bool> TraceDamage;
    internal static MelonPreferences_Entry<bool> ProbeBugs;
    internal static MelonPreferences_Entry<bool> CorrectCombatState;
    internal static MelonPreferences_Entry<bool> SelfTestAutoPlay;
    internal static MelonPreferences_Entry<int> SelfTestMaxRounds;
    internal static MelonPreferences_Entry<int> SelfTestPlayers;
    // Read by the MenaceAutoCombat brain compiled in from its own tree; it logs
    // every decision behind this. Kept under the same name it has there.
    internal static MelonPreferences_Entry<bool> Verbose;
    // Also read by the brain: how it scores targets and weapons.
    internal static MelonPreferences_Entry<string> ScoringMode;

    // Sections of UserData\MelonPreferences.cfg. Every melon shares that one
    // file, so ours are namespaced -- an unprefixed [Network] would collide with
    // the next mod that wants the same obvious word.
    private const string CatMain = "MenaceCoop";
    private const string CatNet = "MenaceCoop_Network";
    private const string CatUI = "MenaceCoop_UI";
    private const string CatDev = "MenaceCoop_Dev";

    public override void OnInitializeMelon()
    {
        Logger = new LogShim(LoggerInstance);
        GameFingerprint = ComputeGameFingerprint();
        Logger.LogInfo($"Game build fingerprint: {GameFingerprint}");

        // BEFORE ANY CreateEntry CALL, for the same reason the BepInEx build
        // does it before Bind: creating an entry writes the section, so asking
        // afterwards whether it already existed always answers yes.
        InstallHygiene.NoteConfigPresence(CatMain);

        var main = MelonPreferences.CreateCategory(CatMain, "MenaceCoop");
        var net = MelonPreferences.CreateCategory(CatNet, "MenaceCoop - Network");
        var ui = MelonPreferences.CreateCategory(CatUI, "MenaceCoop - UI");
        var dev = MelonPreferences.CreateCategory(CatDev, "MenaceCoop - Dev");

        ConfigWrittenBy = main.CreateEntry(InstallHygiene.WrittenByKey, PluginVersion, InstallHygiene.WrittenByKey,
            "Which MenaceCoop version last wrote these settings. Used to warn when settings were carried over from an older install -- a value already present in MelonPreferences.cfg is never replaced by a changed default.");

        ModeSetting = net.CreateEntry("Mode", "Off", "Mode",
            "Auto-start role on launch: Off (use the in-game panel), Host, Guest");
        HostAddress = net.CreateEntry("HostAddress", "127.0.0.1", "HostAddress",
            "Guest only: IP address of the host machine");
        Port = net.CreateEntry("Port", 27555, "Port",
            "TCP port for the co-op session");
        PartnerSteamId = net.CreateEntry("PartnerSteamId", "", "PartnerSteamId",
            "Steam P2P: your partner's SteamID64 (both players enter each other's)");
        SessionPassword = net.CreateEntry("SessionPassword", "", "SessionPassword",
            "Direct IP: the session password -- both players must enter the same one. Required to host or join over direct IP. It is never sent over the network (challenge-response), and the connection is encrypted with a key derived from it.");
        PlayerName = net.CreateEntry("PlayerName", "", "PlayerName",
            "The name other players see for you. Empty = \"Player N\".");
        MaxPlayers = net.CreateEntry("MaxPlayers", 8, "MaxPlayers",
            "Host only: most players in a session, the host included (2-8). Default 8 (user 2026-10-02).");
        InstallId = net.CreateEntry("InstallId", "", "InstallId",
            "Generated on first launch; lets the host give you your own squads back when you rejoin. Leave it alone -- and do not copy it to another install.");
        if (string.IsNullOrWhiteSpace(InstallId.Value))
        {
            InstallId.Value = Guid.NewGuid().ToString("N");
            try { MelonPreferences.Save(); } catch (Exception e) { Logger.LogWarning($"Could not save the new InstallId: {e.Message}"); }
        }
        Peers.LocalKey = InstallId.Value.Trim();

        ShareCursor = main.CreateEntry("ShareCursor", true, "ShareCursor",
            "Broadcast your cursor to your partner (toggle in the co-op panel)");
        // OFF BY DEFAULT. The move line is a niche coordination aid and reads as
        // visual noise to most people; the CURSOR stays on because that is the
        // one that tells you where your partner is actually looking.
        ShareLine = main.CreateEntry("ShareLine", false, "ShareLine",
            "Broadcast your in-battle move line to your partner. Off by default.");
        ShowPartnerCursor = main.CreateEntry("ShowPartnerCursor", true, "ShowPartnerCursor",
            "Show your partner's cursor on your screen");
        ShowPartnerLine = main.CreateEntry("ShowPartnerLine", false, "ShowPartnerLine",
            "Show your partner's in-battle move line on your screen. Off by default.");
        ShowOwnerLabels = main.CreateEntry("ShowOwnerLabels", true, "ShowOwnerLabels",
            "Show a HOST/GUEST tag above every player unit in battle, so it is always clear which player commands which squad");
        ManualTurnEnd = main.CreateEntry("ManualTurnEnd", true, "ManualTurnEnd",
            "While connected, player units never end their turn automatically (AP exhausted / after attacking) -- only the explicit End Turn or Skip commands end a unit's turn. Auto-ends fired at engine-dependent moments and were a desync source.");
        SequentialPhases = main.CreateEntry("SequentialPlayerPhases", true, "SequentialPlayerPhases",
            "One human acts per player activation: while the player faction is active only the controlling player may issue orders, and control passes to the other when their unit finishes. This matches the engine, which alternates one player activation with one AI activation. Turn off to let both players act at once (the original free-for-all, and the root of most ordering desyncs).");
        // NO INTRO SKIP IN CO-OP (user, 2026-09-27): the intro skip moved to
        // MenaceIntroSkip, which owns it and its fix for content mods (BUGS #30).
        // MenaceIntroSkip >= 2.3.0 stands down only while the type
        // MenaceCoop.IntroSkipPatches exists in this assembly -- it no longer
        // does, so that mod drives. Old code: baseline\removed-2026-09-27\.
        Logger.LogInfo("[INTRO] Co-op no longer skips the intro -- install MenaceIntroSkip 2.3.0 or newer for that (older versions stand down for co-op, so nothing skips it).");
        ScoringMode = main.CreateEntry("AutoCombatTargeting", "blend", "AutoCombatTargeting",
            "How auto combat picks a target and weapon. blend: the game's hit chance / expected damage / threat numbers blended with the enemy AI's own valuation (default). engine: the game's numbers only. ai: the enemy AI's valuation only.");
        AutoCombat = main.CreateEntry("AutoCombat", false, "AutoCombat",
            "AUTO COMBAT: the mod fights YOUR squads for you -- advance to cover, shoot the toughest target in range, use support skills, end the turn. It only ever controls your own units, so you and your partner can each choose independently. Take over at any time: any click or key press pauses it briefly. Basic tactics, not a clever general.");
        LockGameSpeed = main.CreateEntry("LockGameSpeed", true, "LockGameSpeed",
            "Force normal (1x) movement/animation speed while connected. Speeding up changes animation and event timing, so reaction fire and interrupts resolve at different moments on each machine -- a known desync source.");
        StallRecoverySeconds = main.CreateEntry("StallRecoverySeconds", 180f, "StallRecoverySeconds",
            "How long the board may sit completely unchanged before the mod tries to recover it (seconds). This only ever runs when the player holding control has NOTHING left to move -- a player who still has units is never interrupted, however long they take to think. Minimum 30.");
        PinMissionRng = main.CreateEntry("PinMissionRng", true, "PinMissionRng",
            "Re-seed the operation's random number generator from the mission seed on BOTH machines immediately before a mission launches, so the enemy force is generated from the same starting point. Without this the two players can end up fighting different armies. Turn off only if enemy generation misbehaves in some other way.");
        CheckTerrain = main.CreateEntry("CheckTerrain", true, "CheckTerrain",
            "At the start of every battle, compare the generated MAP on both machines -- surface, cover, line-of-sight blockers, structures, environment features and walkability, tile by tile. Terrain is not scenery: cover decides whether a shot lands and line-of-sight decides what each player can see.");
        CheckEnemyRoster = main.CreateEntry("CheckEnemyRoster", true, "CheckEnemyRoster",
            "At the start of every battle, compare the AI force on both machines and warn loudly if they differ. This detects the case where the two players are fighting different armies -- it cannot repair it, but it tells you before anyone dies for it.");
        LiveStrategy = main.CreateEntry("LiveStrategyMirror", true, "LiveStrategyMirror",
            "Live two-screen strategy layer: the host's black market / hiring screens and every economy change (money, items, market stock) mirror to the guest as they happen. The guest views; the host drives.");

        ShowPhaseBanner = ui.CreateEntry("ShowPhaseBanner", true, "ShowPhaseBanner",
            "Show the green 'YOUR TURN / PLAYER N IS ACTING' banner at the top of the screen during battle.");
        // OFF BY DEFAULT -- these two are DEBUGGING output, not gameplay UI.
        // Everything they say is still written to the log in full, and both can
        // be switched on from the DEBUGGING section of the co-op panel.
        ShowWarningBanner = ui.CreateEntry("ShowWarningBanner", false, "ShowWarningBanner",
            "DEBUGGING. Show the orange warning banner (blocked actions, desync corrections). Off by default; corrections still happen and are still logged.");
        ShowAlertBanner = ui.CreateEntry("ShowAlertBanner", false, "ShowAlertBanner",
            "DEBUGGING. Show the red alert banner across the top of the screen (different game versions, enemy forces differing, maps differing). Off by default.");

        // ON BY DEFAULT, INCLUDING IN RELEASES. A player report without logs
        // costs a round trip and usually cannot be acted on at all.
        SelfTestTrace = dev.CreateEntry("SelfTestTrace", true, "SelfTestTrace",
            "Log one canonical tactical-state line per change (round/faction/pending units) so host and guest logs can be diffed to the exact frame they diverge. Also reports a 20s stall with a dump of who still owes a turn.");
        TraceDamage = dev.CreateEntry("TraceDamage", false, "TraceDamage",
            "Log every hit and every element death on both machines, with the unit's coop identity, so the two logs can be diffed hit by hit. Very verbose. Development diagnosis only.");
        ProbeBugs = dev.CreateEntry("ProbeBugs", false, "ProbeBugs",
            "Diagnosis for four specific reported bugs: drone effects, ship support, and a unit boarding a transport the OTHER player owns. Logs every one of those events on both machines with who owns what. Very verbose. Development diagnosis only; must be OFF in a release.");
        CorrectCombatState = dev.CreateEntry("CorrectCombatState", true, "CorrectCombatState",
            "Let the host's periodic snapshot overwrite the guest's suppression, morale, stance and facing (none of which are in the desync hash). Off = those drifts are only logged. Experiment switch (DESIGN-REVIEW.md proposal 2); a hard restore always applies them.");
        SelfTestAutoPlay = dev.CreateEntry("SelfTestAutoPlay", false, "SelfTestAutoPlay",
            "Automated play-through: ends the turn of one owned unit per second so a mission runs unattended. Development testing only.");
        SelfTestMaxRounds = dev.CreateEntry("SelfTestMaxRounds", 30, "SelfTestMaxRounds",
            "Developer harness only. Abandon a mission that is still running after this many rounds and move on to the next one. 0 disables the cap.");
        SelfTestPlayers = dev.CreateEntry("SelfTestPlayers", 2, "SelfTestPlayers",
            "Developer harness only. Players in the test session, the host included; the host's autopilot waits until all have connected.");
        Verbose = dev.CreateEntry("VerboseAutoCombatLog", false, "VerboseAutoCombatLog",
            "Log every decision auto combat makes. Off by default: useful for reporting a bad decision, noisy otherwise.");
        SyncedDebug = dev.CreateEntry("SyncedDebug", false, "SyncedDebug",
            "Developer testing only. Adds SYNCED DEBUG buttons to the co-op panel in battle (spawn a unit / hostage at the cursor, kill a unit, refill ship support) that run on BOTH machines. Must be on for both players. Never leave on for real play.");
        SyncedDebugScenario = dev.CreateEntry("SyncedDebugScenario", "none", "SyncedDebugScenario",
            "Developer harness only. What the harness stages at round 2 when SyncedDebug is on: none (a clean mission, the default), revive (a hostile beside the host's infantry so a leader goes down), structure (the host destroys one structure to test the relay).");
        SelfTestLoopMissions = dev.CreateEntry("SelfTestLoopMissions", false, "SelfTestLoopMissions",
            "After a mission ends, reload and launch the NEXT one instead of stopping. Off by default: replaying one map makes behaviour far easier to observe than a map that changes under you between runs.");
        // Steam dev switches: not created in release builds (the code that reads
        // them is compiled out there too), so they never show in a player's cfg.
#if COOP_DEVTOOLS
        SteamLobbyProbe = dev.CreateEntry("SteamLobbyProbe", false, "SteamLobbyProbe",
            "Developer only. At the title screen, open a Steam lobby by itself, log what Steam answers (lobby id, members, callbacks), then close it again.");
        SteamSelfTest = dev.CreateEntry("SteamSelfTest", false, "SteamSelfTest",
            "Developer only. Lets two game windows on ONE Steam account connect to each other over Steam (lobby and all), told apart by network channel. Both windows need it on. Never needed by players.");
#endif

        Mode = ParseMode(ModeSetting.Value);

        // Co-op ships no auto-combat brain of its own (user, 2026-09-28). If the
        // Auto Combat mod is installed, ITS brain gets our rules through the bridge. Here, not later: its conflict check runs
        // on its runner's first Update and must see the bridge already in place.
        AutoCombatBridge.TryInstall();

        // Before patching: if this run dies during patching, the log still says
        // what was installed, which is the first thing anyone needs to know.
        InstallHygiene.Report(ConfigWrittenBy);

        // PATCHED BY HAND, NOT BY MelonLoader.
        //
        // MelonLoader would PatchAll this assembly for us, and its HarmonyInit
        // does catch per patch class -- but it patches in reflection order and
        // says nothing about what succeeded. This list is the mod's load order,
        // and the "Patched X" line for each is what a support log is read for.
        // [HarmonyDontPatchAll] above stops the loader doing it as well.
        //
        // It is ONE list, not fifteen calls, because ApiCheck walks the same list
        // to verify the hooks still resolve. Two copies would drift, and a stale
        // copy in the checker is worse than no checker: it would report a clean
        // bill of health for hooks it never looked at.
        var patchClasses = new[]
        {
            typeof(InstrumentationPatches),
            typeof(ReplicationPatches),
            typeof(EquipmentPatches), typeof(PrepPortraits), typeof(DeploySyncPatches),
            typeof(SpawnPatches),
            typeof(AiPatches),
            typeof(StrategyMirrorPatches),
            typeof(TurnControlPatches),
            typeof(UiAutopilotPatches),
            typeof(MapgenPatches),
            typeof(ReinforcementPatches),
            typeof(ContainerPatches),
            typeof(PerkPatches),
            typeof(ObjectivePatches),
            typeof(DamageProbePatches),
            typeof(OffmapPatches), typeof(OffmapStrikePatches), typeof(DelayedOffmapPatches),
            typeof(EnemyOffmapPatches),
            typeof(ConversationPatches), typeof(ConversationChoicePatch),
            typeof(SaveModCheckPatches),
            typeof(FactionSyncPatches), typeof(TacticalHudRef),
            typeof(SuppressionSyncPatches),
            typeof(StructureSyncPatches),
            typeof(ObjectiveProgressPatches),
            typeof(BugProbePatches), typeof(BleedOutProbe),
            typeof(FleeSyncPatches),
            typeof(CoverJournalPatches),
            typeof(SaveLoadPatches),
            typeof(FogTimingProbe),
            typeof(FacingSync),
            typeof(EffectSyncPatches), typeof(KillCreditPatches),
            typeof(LootSyncPatches),
            typeof(JamProbe),
        };

        // A [HarmonyPatch] class missing from the list above was silently never
        // patched: LootSyncPatches (the client's result-screen loot, BUGS #55)
        // for its whole life, and a probe on 2026-10-04. The list still sets the
        // order; anything it forgot is appended and named as an error, never lost.
        var listed = new System.Collections.Generic.HashSet<Type>(patchClasses);
        var forgotten = new System.Collections.Generic.List<Type>();
        try
        {
            foreach (var t in typeof(Plugin).Assembly.GetTypes())
                if (!listed.Contains(t) && t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0)
                    forgotten.Add(t);
        }
        catch (Exception e) { Logger.LogWarning($"Could not scan for unlisted patch classes: {e.Message}"); }
        foreach (var t in forgotten)
            Logger.LogError($"Patch class {t.Name} is not in MelonEntry's patch list -- patched last; add it to the list");
        if (forgotten.Count > 0) patchClasses = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(patchClasses, forgotten));

        // ABOVE the patch lines on purpose: if an engine primitive is gone, the
        // patch lines below are noise, and a support log is read top-down.
        ApiCheck.CheckPrimitives(GameFingerprint);

        var harmony = HarmonyInstance ?? new HarmonyLib.Harmony(PluginGuid);
        foreach (var cls in patchClasses) SafePatch(harmony, cls);

        // AFTER, necessarily: it compares what the attributes asked for against
        // what Harmony actually patched, and only Harmony can answer the second
        // half. Modelling Harmony's resolution ourselves is what produced a false
        // alarm on this check's first run.
        ApiCheck.VerifyPatches(harmony, patchClasses);
        // Every patch method by name, from Harmony itself (user 2026-10-04).
        MenaceShared.PatchReport.Log(harmony, typeof(Plugin).Assembly,
            s => Logger.LogInfo(s), s => Logger.LogError(s));

        // A client connects only once the title screen is up, as a player would
        // from the co-op panel (r105: a relaunched client auto-connected during
        // the splash, loaded the battle mid-boot and hung on mission prep).
        if (Mode == CoopMode.Guest) CoopRunner.PendingAutoConnect = true;
        else if (Mode != CoopMode.Off)
            CoopNet.Start(Mode, HostAddress.Value, Port.Value);

        // Stated loudly and unambiguously: a bug report is worthless if we cannot
        // tell which build produced it.
        Logger.LogWarning($"===== {PluginName} v{PluginVersion} (MelonLoader) =====");
        Logger.LogWarning($"===== game {GameFingerprint} | AutoMode={Mode} | AutoCombat={AutoCombat.Value} =====");
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded. AutoMode={Mode}");
    }

    // The per-frame runner is an injected MonoBehaviour, exactly as under BepInEx.
    // Created at LATE init: Unity's first 'Start' has run by then, which is what
    // BepInEx's AddComponent<T>() waited for.
    public override void OnLateInitializeMelon()
    {
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<CoopRunner>();
            var go = new GameObject("MenaceCoopRunner");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<CoopRunner>();
        }
        catch (Exception e)
        {
            Logger.LogError($"Could not start the co-op runner: {e}");
        }
    }

    public override void OnDeinitializeMelon() => CoopNet.Shutdown();

    private static CoopMode ParseMode(string raw)
    {
        if (Enum.TryParse(raw, true, out CoopMode m)) return m;
        // Never fail to load over a typo in a settings file; say so and go Off.
        MelonLogger.Warning($"[MenaceCoop] Mode '{raw}' is not one of Off/Host/Guest -- starting Off.");
        return CoopMode.Off;
    }

    private static string ComputeGameFingerprint()
    {
        try
        {
            string ver = UnityEngine.Application.version;
            string metaPath = System.IO.Path.Combine(
                UnityEngine.Application.dataPath, "il2cpp_data", "Metadata", "global-metadata.dat");
            long sz = System.IO.File.Exists(metaPath) ? new System.IO.FileInfo(metaPath).Length : 0;
            return $"{ver}|{sz}";
        }
        catch { return "unknown"; }
    }

    // Guard each patch class so one unresolvable hook can't abort the whole
    // plugin (which would leave the mod dead with an empty log).
    // HarmonyLib.Harmony in full: the game's own assemblies contribute a
    // top-level `Harmony` NAMESPACE, so the bare name is ambiguous here.
    private static void SafePatch(HarmonyLib.Harmony harmony, Type patchClass)
    {
        try
        {
            harmony.PatchAll(patchClass);
            Logger.LogInfo($"Patched {patchClass.Name}");
        }
        catch (Exception e)
        {
            Logger.LogError($"Patch class {patchClass.Name} failed (some hooks disabled): {e.Message}");
        }
    }
}

// BepInEx's ManualLogSource call shapes over MelonLoader's logger, so the 543
// log calls in the shared code need no loader switch.
internal sealed class LogShim
{
    private readonly MelonLogger.Instance _log;
    public LogShim(MelonLogger.Instance log) { _log = log; }
    public void LogInfo(object msg) => _log.Msg(msg?.ToString());
    public void LogMessage(object msg) => _log.Msg(msg?.ToString());
    public void LogDebug(object msg) => _log.Msg(msg?.ToString());
    public void LogWarning(object msg) => _log.Warning(msg?.ToString());
    public void LogError(object msg) => _log.Error(msg?.ToString());
    public void LogFatal(object msg) => _log.Error(msg?.ToString());
}
