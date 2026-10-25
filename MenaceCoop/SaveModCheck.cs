using System;
using System.IO;
using HarmonyLib;
using Il2CppMenace.Strategy;

namespace MenaceCoop;

// WAS THIS SAVE MADE WITH THE MODS WE ARE RUNNING NOW? (user, 2026-10-01)
//
// The partner check in the "hello" handler (ModList) compares the two players
// with each other. It cannot see the case that bit the test rigs: both machines
// matched each other perfectly, and NEITHER matched the mods the campaign was
// made with -- the save came from an install running the Modpack Loader and the
// Jiangyu Loader, the rigs had neither. A content-mod campaign opened without its
// mods still loads, and units come up wrong (no weapons, missing templates), so
// it reads as a co-op bug.
//
// So every save the game writes gets a sidecar, "<save>.coopmods", holding the
// mod list that was running (ModList.Local), and every load compares the save's
// list with this machine's. A difference is logged and shown as a banner before
// the campaign opens. Like the partner check this only WARNS: a player may have
// removed a cosmetic mod on purpose. Saves made before this check have no
// sidecar and are not judged.
internal static class SaveModCheck
{
    private const string Ext = ".coopmods";

    internal static void AfterSave(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        try { File.WriteAllText(filePath + Ext, ModList.Local ?? ""); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[MODS] Could not record the mod list beside '{Path.GetFileName(filePath)}': {e.Message}"); }
    }

    internal static void BeforeLoad(SaveState save)
    {
        string path = null;
        try { path = save?.GetFilePath(); } catch { }
        if (string.IsNullOrEmpty(path)) return;
        string name = Path.GetFileName(path);
        string side = path + Ext;
        if (!File.Exists(side))
        {
            Plugin.Logger.LogInfo($"[MODS] '{name}' has no recorded mod list (saved before this check, or by another install) -- not compared");
            return;
        }
        string recorded;
        try { recorded = File.ReadAllText(side); } catch { return; }
        string diff = ModList.Diff(recorded, "this machine", "the save", "save");
        if (diff == null)
        {
            Plugin.Logger.LogInfo($"[MODS] '{name}' was made with the same mods running now ({ModList.Count})");
            return;
        }
        // A version bump alone (IntroSkip 2.3.0 -> 2.3.2) is normal between
        // sessions: log it, but the banner is for mods that are MISSING or EXTRA.
        if (!diff.Contains("only "))
        {
            Plugin.Logger.LogWarning($"[MODS] '{name}': same mods as when saved, some at other versions -- {diff}");
            return;
        }
        Plugin.Logger.LogError($"[MODS] ***** '{name}' WAS MADE WITH DIFFERENT MODS ***** {diff}");
        Plugin.Logger.LogError("[MODS] Content mods add units and items; a campaign opened without its mods can load with units missing weapons or templates.");
        CoopRunner.ShowVersionWarning(
            $"THIS SAVE WAS MADE WITH DIFFERENT MODS -- {diff} Install the same mods, or expect missing units/items.", sticky: true);
    }
}

[HarmonyPatch]
internal static class SaveModCheckPatches
{
    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.Save))]
    [HarmonyPostfix]
    private static void Saved(string _filePath) => SaveModCheck.AfterSave(_filePath);

    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.Load))]
    [HarmonyPrefix]
    private static void Loading(SaveState _saveState) => SaveModCheck.BeforeLoad(_saveState);
}
