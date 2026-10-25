using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MelonLoader;

namespace MenaceCoop;

// WHAT IS ACTUALLY INSTALLED ON THIS MACHINE?  (MelonLoader build)
//
// Same job and same output shape as the BepInEx InstallHygiene.cs, which this
// replaces -- a support log has to answer "is this a clean install?" on its own.
// Three things genuinely differ under MelonLoader and they are why this is a
// separate file rather than a pile of #if:
//
//   1. THE SETTINGS FILE IS SHARED. BepInEx gave the mod its own
//      com.menacecoop.core.cfg, which a player could simply delete to get this
//      version's defaults. UserData\MelonPreferences.cfg belongs to EVERY melon,
//      so "delete the file" is advice that would wipe other mods' settings. We
//      name the sections to remove instead.
//   2. DUPLICATES LIVE IN Mods\, not BepInEx\plugins.
//   3. There is no ConfigFile to enumerate, so the entries are listed here
//      explicitly. That is deliberate: it depends on nothing but the fields in
//      MelonEntry.cs, and a setting added there without a line here shows up as
//      absent from the report rather than crashing the startup block.
//
// IT DOES NOT DELETE ANYTHING. Deleting files out of somebody's game directory
// is not a thing a mod should do quietly -- a wrong guess about which file is
// stale destroys another mod, and there is no undo.
internal static class InstallHygiene
{
    internal const string WrittenByKey = "ConfigWrittenByVersion";

    // Set when a second copy of this mod is found. Reported, NOT enforced:
    // refusing to start over it would lock players out of a working install for
    // what is usually a cosmetic problem.
    internal static bool DuplicateInstall { get; private set; }

    // Recorded before any CreateEntry call, because creating an entry writes the
    // section -- asking afterwards always answers "yes" and tells you nothing.
    private static bool _configPreexisted;

    internal static void NoteConfigPresence(string category)
    {
        try
        {
            string path = PrefsPath();
            _configPreexisted = path != null
                                && File.Exists(path)
                                && File.ReadAllText(path).Contains("[" + category + "]");
        }
        catch { _configPreexisted = false; }
    }

    internal static void Report(MelonPreferences_Entry<string> writtenBy)
    {
        try
        {
            var log = Plugin.Logger;
            log.LogInfo("[INSTALL] ---------------- installation ----------------");
            log.LogInfo($"[INSTALL] MenaceCoop {Plugin.PluginVersion} (MelonLoader) | game {Plugin.GameFingerprint}");

            string cfgPath = PrefsPath() ?? "?";

            if (!_configPreexisted)
            {
                log.LogInfo("[INSTALL] Settings: created fresh this run (all defaults)");
            }
            else if (writtenBy.Value != Plugin.PluginVersion)
            {
                // The case worth shouting about: values on disk were chosen by a
                // different build, and they are kept as they are.
                log.LogWarning($"[INSTALL] Settings were written by MenaceCoop {writtenBy.Value}, this is {Plugin.PluginVersion}.");
                log.LogWarning("[INSTALL] Existing values are KEPT, so any default changed since then is NOT in effect here.");
                log.LogWarning($"[INSTALL] To adopt this version's defaults, delete the [MenaceCoop*] sections from:");
                log.LogWarning($"[INSTALL]   {cfgPath}");
                log.LogWarning("[INSTALL] Do NOT delete the whole file -- every other mod keeps its settings there too.");
            }
            else
            {
                log.LogInfo($"[INSTALL] Settings: written by this version ({Plugin.PluginVersion})");
            }
            writtenBy.Value = Plugin.PluginVersion;

            // --- settings that are not at their shipped default -------------
            var changed = new List<string>();
            void Check<T>(MelonPreferences_Entry<T> e)
            {
                try
                {
                    if (e == null || e.Identifier == WrittenByKey) return;
                    if (!Equals(e.Value, e.DefaultValue))
                        changed.Add($"{e.Identifier} = {e.Value} (default {e.DefaultValue})");
                }
                catch { }
            }

            Check(Plugin.ModeSetting); Check(Plugin.HostAddress); Check(Plugin.Port);
            Check(Plugin.PartnerSteamId); Check(Plugin.ShareCursor); Check(Plugin.ShareLine);
            Check(Plugin.ShowPartnerCursor); Check(Plugin.ShowPartnerLine); Check(Plugin.ShowOwnerLabels);
            Check(Plugin.ManualTurnEnd); Check(Plugin.SequentialPhases);
            Check(Plugin.AutoCombat); Check(Plugin.LockGameSpeed); Check(Plugin.StallRecoverySeconds);
            Check(Plugin.PinMissionRng); Check(Plugin.CheckTerrain); Check(Plugin.CheckEnemyRoster);
            Check(Plugin.LiveStrategy); Check(Plugin.ShowPhaseBanner); Check(Plugin.ShowWarningBanner);
            Check(Plugin.ShowAlertBanner); Check(Plugin.SelfTestTrace); Check(Plugin.TraceDamage);
            Check(Plugin.SelfTestAutoPlay); Check(Plugin.SelfTestMaxRounds); Check(Plugin.SelfTestLoopMissions);

            if (changed.Count == 0)
            {
                log.LogInfo("[INSTALL] Settings: all at shipped defaults");
            }
            else
            {
                log.LogWarning($"[INSTALL] Settings differing from defaults ({changed.Count}):");
                foreach (var c in changed) log.LogWarning($"[INSTALL]   {c}");
            }

            ScanForDuplicates(log);

            log.LogInfo("[INSTALL] ----------------------------------------------");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[INSTALL] Report failed: {e.Message}");
        }
    }

    // ...\Mods\MenaceCoop.dll -> ...\Mods
    private static string ModsDir()
    {
        try { return Path.GetDirectoryName(typeof(InstallHygiene).Assembly.Location); }
        catch { return null; }
    }

    private static string PrefsPath()
    {
        try
        {
            string mods = ModsDir();
            string root = mods == null ? null : Path.GetDirectoryName(mods);
            return root == null ? null : Path.Combine(root, "UserData", "MelonPreferences.cfg");
        }
        catch { return null; }
    }

    // A second copy of this mod loads a second set of Harmony patches over the
    // same engine methods. The result is not "the newer one wins" -- it is both,
    // in an order nobody chose.
    private static void ScanForDuplicates(LogShim log)
    {
        try
        {
            string dir = ModsDir();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            var mine = Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories)
                .Where(f => Path.GetFileNameWithoutExtension(f)
                                .IndexOf("menacecoop", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            // Disabled/renamed leftovers are the usual culprit, and a .dll is
            // loaded whatever it is called.
            var stale = Directory.GetFiles(dir, "*MenaceCoop*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (mine.Count > 1)
            {
                DuplicateInstall = true;
                log.LogError("[INSTALL] ***** MORE THAN ONE MenaceCoop MOD INSTALLED *****");
                foreach (var f in mine) log.LogError($"[INSTALL]   {f}");
                log.LogError("[INSTALL] Delete all but one and restart. Two copies patch the same engine");
                log.LogError("[INSTALL] methods twice, in an order nobody chose.");
            }
            else if (mine.Count == 1)
            {
                log.LogInfo($"[INSTALL] Mod: {mine[0]}");
            }

            foreach (var f in stale)
                log.LogWarning($"[INSTALL] Leftover file from an older install (safe to delete): {f}");

            // THE OTHER HALF OF THE SAME PROBLEM, AND THE ONE PLAYERS HIT.
            // MenaceAutoCombat contains this exact auto-combat brain; beside
            // co-op it drives every squad twice per frame. It disables itself,
            // but only from its first Update, so saying so here is what makes
            // the startup log explain a first-frame double-move.
            foreach (var f in Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories))
            {
                string n = Path.GetFileNameWithoutExtension(f);
                if (n.IndexOf("MenaceAutoCombat", StringComparison.OrdinalIgnoreCase) < 0) continue;
                // Runs after AutoCombatBridge.TryInstall, so this reports the outcome.
                if (AutoCombatBridge.Delegated)
                {
                    log.LogInfo($"[INSTALL] MenaceAutoCombat is installed ({f}) and bridged: its brain drives, co-op's stands down.");
                    continue;
                }
                log.LogWarning($"[INSTALL] MenaceAutoCombat is also installed ({f}) but predates the co-op bridge.");
                log.LogWarning("[INSTALL] It stands itself down on its first Update and co-op's built-in brain drives;");
                log.LogWarning("[INSTALL] update it to use it in co-op.");
            }
        }
        catch (Exception e) { log.LogWarning($"[INSTALL] Mod scan failed: {e.Message}"); }
    }
}
