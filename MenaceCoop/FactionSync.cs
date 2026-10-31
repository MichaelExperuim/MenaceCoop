using System;
using HarmonyLib;
using Il2CppMenace.Tactical;
using Menace = Il2CppMenace;

namespace MenaceCoop;

// AN EMPTIED ENEMY TRUCK CHANGES SIDES, AND ONLY THE HOST SAW IT.
//
// Found 2026-09-25 with probes: after special.vehicle_eject_passengers_free a
// pirate truck is left empty and the engine moves it to faction 0 (it stays on
// its tile, alive, one element). That decision comes out of the enemy side of
// the simulation, which runs only on the host, so the guest's truck stayed
// hostile -- the roster check then reported three trucks "on the guest only"
// and the guest's own squads kept a live target the host no longer had.
//
// So the host relays the RESULT of the engine's own SetFaction call for AI
// actors, and the guest makes the same call. Player units are left out: the
// downed-squad move to an orphan faction happens on both machines already, and
// Ownership (not faction) is what identifies them.
internal static class FactionSync
{
    internal static bool ChangedFired;

    private static void RemoveHud(Actor actor)
    {
        var hud = TacticalHudRef.Hud;
        if (hud == null) { Plugin.Logger.LogWarning("[FACTION] no tactical HUD captured -- abandoned truck keeps its bar"); return; }
        try
        {
            hud.RemoveEntityHUD(actor);
            Plugin.Logger.LogInfo($"[FACTION] '{DesyncDetector.TemplateNameOf(actor)}' abandoned -- HUD removed, as the host's AI does");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[FACTION] HUD remove failed: {e.GetType().Name}: {e.Message}"); }
    }

    // Which kind of side an index is (Neutral? Civilian?) -- what the UI colours by.
    internal static string TypeOf(int idx)
    {
        try
        {
            var fs = TacticalManager.Get()?.GetFactions();
            if (fs != null)
                for (int i = 0; i < fs.Length; i++)
                    if (fs[i] != null && fs[i].GetIndex() == idx) return fs[i].GetFactionType().ToString();
        }
        catch { }
        return "?";
    }

    internal static void HandleRemote(CoopMsg m)
    {
        var actor = Replication.ResolveOrderActor(m);
        if (actor == null)
        {
            Plugin.Logger.LogWarning($"[FACTION] cannot resolve actor at ({m.AX},{m.AY}) RId={m.RId} CId={m.CId} for faction {m.Prm}");
            return;
        }
        int before;
        try { before = actor.GetFactionID(); } catch { return; }
        if (before == m.Prm) return;   // already agrees
        bool prev = Replication.ApplyingRemote;
        Replication.ApplyingRemote = true;
        bool refreshed;
        try
        {
            ChangedFired = false;
            actor.SetFaction(m.Prm);
            // THE EMPTY TRUCK STILL LOOKED LIKE AN ENEMY (user, 2026-09-25): red
            // bar on a neutral truck. If SetFaction does not itself raise the
            // actor's faction-changed refresh (the host's engine may call it as a
            // separate step we never relay), raise it here -- once.
            refreshed = ChangedFired;
            if (!refreshed) actor.OnFactionChanged();
            // Run 18: the host's AIFaction.Process calls UITacticalHUD.RemoveEntityHUD
            // on the emptied truck right before SetFaction(0). The guest runs no AI,
            // so its truck kept a health bar. Faction 0 = abandoned: mirror it.
            if (m.Prm == 0) RemoveHud(actor);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[FACTION] SetFaction({m.Prm}) threw: {e.GetType().Name}"); return; }
        finally { Replication.ApplyingRemote = prev; }
        Plugin.Logger.LogInfo($"[FACTION] '{DesyncDetector.TemplateNameOf(actor)}' at ({m.AX},{m.AY}) faction {before} -> {m.Prm} [{TypeOf(m.Prm)}] (host); "
            + (refreshed ? "SetFaction refreshed it" : "called OnFactionChanged ourselves"));
    }
}

[HarmonyPatch]
internal static class FactionSyncPatches
{
    [HarmonyPatch(typeof(Actor), nameof(Actor.OnFactionChanged))]
    [HarmonyPostfix]
    private static void Changed(Actor __instance)
    {
        FactionSync.ChangedFired = true;
        if (BugProbes.On && CoopNet.Connected)
            Plugin.Logger.LogInfo($"[FACTION] OnFactionChanged '{DesyncDetector.TemplateNameOf(__instance)}' fac={__instance.GetFactionID()} remote={Replication.ApplyingRemote}");
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.SetFaction))]
    [HarmonyPrefix]
    private static void Before(Actor __instance, out int __state)
    {
        __state = int.MinValue;
        try { __state = __instance.GetFactionID(); } catch { }
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.SetFaction))]
    [HarmonyPostfix]
    private static void After(Actor __instance, int _f, int __state)
    {
        if (!CoopNet.Hosting || Replication.ApplyingRemote) return;   // alone too: journaled (#57)
        // 0 -> side is the engine placing a freshly spawned actor, which the
        // guest does itself; only a unit that already had a side is news.
        if (__state == _f || __state == int.MinValue || __state == 0) return;
        try
        {
            if (TacticalManager.Get() == null) return;
            if (Ownership.IsKnownPlayerUnit(__instance)) return;
            var p = __instance.GetLastTile()?.GetTilePos();
            var msg = new CoopMsg { T = "setfac", AX = p?.x ?? -1, AY = p?.y ?? -1, Prm = _f };
            Replication.StampIds(msg, __instance, isOrder: false);
            CoopNet.Send(msg);
            Plugin.Logger.LogInfo($"[FACTION] '{DesyncDetector.TemplateNameOf(__instance)}' at ({msg.AX},{msg.AY}) faction {__state} -> {_f}, relayed");
        }
        catch { }
    }
}

// The tactical HUD, captured from its own AddActor calls (the shipped build has
// no static accessor we can rely on), so the guest can remove an entity's HUD the
// way the host's AI does.
[HarmonyPatch]
internal static class TacticalHudRef
{
    internal static Menace.UI.UITacticalHUD Hud;

    [HarmonyPatch(typeof(Menace.UI.UITacticalHUD), "AddUnitHUD")]   // shipped name (the stale index says AddActor)
    [HarmonyPostfix]
    private static void Capture(Menace.UI.UITacticalHUD __instance) => Hud = __instance;
}
