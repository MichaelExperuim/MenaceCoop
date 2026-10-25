using System;
using HarmonyLib;
using Il2CppMenace.Items;
using Il2CppMenace.States;
using UnityEngine;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling

namespace MenaceCoop;

// THE HOST'S LOOT IS THE LOOT (user 2026-10-02).
//
// Each machine rolls its own mission loot, so the two result screens listed
// different items (hand test 17:34: 6 items vs 4). Only the host's ever counts:
// the post-mission sync ships the host's save, which replaces the guest's whole
// inventory. So this is DISPLAY ONLY -- the guest's result screen shows the
// host's list instead of a roll that is about to be thrown away.
//
// Ordering: the host sends "mloot" from its result-screen OnOpened, right
// before "mend"; the guest leaves the battle on "mend", so the list is already
// here when the guest's own result screen opens (prefix below swaps it in).
internal static class LootSync
{
    private static string[] _hostLoot;
    private static int _hostLootMission = -1;

    // HOST: called from the result-screen OnOpened postfix, before "mend".
    internal static void SendHostLoot()
    {
        if (CoopNet.Mode != CoopMode.Host || !CoopNet.Connected) return;
        try
        {
            var result = StrategyState.Get()?.GetLastMissionResult();
            var loot = result?.GetLoot();
            if (result == null || loot == null) return;
            var ids = new string[loot.Count];
            for (int i = 0; i < loot.Count; i++) ids[i] = loot[i]?.GetID() ?? "";
            CoopNet.Send(new CoopMsg { T = "mloot", MIdx = result.GetMissionIdx(), Data = string.Join("|", ids) });
            Plugin.Logger.LogInfo($"[LOOT] Sent the host's mission loot ({ids.Length} item(s)): {string.Join(", ", ids)}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[LOOT] send failed: {e.Message}"); }
    }

    // GUEST: the host's list arrives.
    internal static void OnHostLoot(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        _hostLoot = string.IsNullOrEmpty(m.Data) ? new string[0] : m.Data.Split('|');
        _hostLootMission = m.MIdx;
        Plugin.Logger.LogInfo($"[LOOT] Host's mission loot received ({_hostLoot.Length} item(s))");
    }

    // GUEST: just before the result screen draws, replace our roll with the host's.
    internal static void ApplyBeforeResultScreen()
    {
        if (CoopNet.Mode != CoopMode.Guest || !CoopNet.Connected) return;
        var ids = _hostLoot;
        _hostLoot = null;
        try
        {
            var result = StrategyState.Get()?.GetLastMissionResult();
            var loot = result?.m_Loot;
            if (result == null || loot == null)
            {
                Plugin.Logger.LogWarning($"[LOOT] Result screen opened with no {(result == null ? "last mission result" : "loot list")} on this machine yet -- host list ({ids?.Length ?? -1} item(s)) not applied");
                return;
            }
            if (ids == null)
            {
                Plugin.Logger.LogWarning("[LOOT] Result screen opened before the host's loot arrived -- showing this machine's own roll (the host's items are what you keep)");
                return;
            }
            if (_hostLootMission != result.GetMissionIdx())
            {
                Plugin.Logger.LogWarning($"[LOOT] Host's loot is for mission {_hostLootMission}, this result is {result.GetMissionIdx()} -- not applied");
                return;
            }
            var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<BaseItemTemplate>());
            var built = new Il2CppSystem.Collections.Generic.List<BaseItemTemplate>();
            int missing = 0;
            foreach (var id in ids)
            {
                if (string.IsNullOrEmpty(id)) continue;
                BaseItemTemplate found = null;
                for (int i = 0; i < all.Length && found == null; i++)
                {
                    var t = all[i]?.TryCast<BaseItemTemplate>();
                    if (t != null && t.GetID() == id) found = t;
                }
                if (found != null) built.Add(found); else missing++;
            }
            // Never swap in a partial list: a missing template means the two
            // installs differ, and the guest's own roll is then the more honest display.
            if (missing > 0)
            {
                Plugin.Logger.LogWarning($"[LOOT] {missing} of the host's loot item(s) have no template here -- showing this machine's own roll");
                return;
            }
            int before = loot.Count;
            loot.Clear();
            for (int i = 0; i < built.Count; i++) loot.Add(built[i]);
            Plugin.Logger.LogInfo($"[LOOT] Result screen shows the host's loot: {built.Count} item(s) (this machine had rolled {before})");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[LOOT] apply failed: {e.Message}"); }
    }
}

// Own patch class: if the game changes this screen, only the loot display is lost.
[HarmonyPatch]
internal static class LootSyncPatches
{
    [HarmonyPatch(typeof(Menace.UI.MissionResult.MissionResultUIScreen), nameof(Menace.UI.MissionResult.MissionResultUIScreen.OnOpened))]
    [HarmonyPrefix]
    private static void BeforeResultScreen() => LootSync.ApplyBeforeResultScreen();
}
