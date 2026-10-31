using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Objectives;

namespace MenaceCoop;

// OBJECTIVE PROGRESS, NOT JUST OUTCOMES (user, 2026-09-25: "do objective progress
// and side missions sync? ... host takes priority").
//
// ObjectiveSync already makes every objective's STATE the host's (primary and
// secondary alike; the guest may not complete or fail one on its own). But the
// counters shown while a mission runs -- "Do not destroy more than 1 structures
// (0/2)", rounds held, units rescued -- are computed from each machine's own
// board, and each objective type keeps its own private counters, so there is no
// one field to copy.
//
// What the player SEES is the HUD text, so that is what is made the host's: the
// host sends each objective's HUD text and progress string whenever either
// changes (polled every 2 s -- cheap, and it catches progress changes that raise
// no event), and on the guest Objective.GetTranslatedHUDText returns the host's.
// The guest's own counters keep running underneath, but nothing is decided from
// them: completion and failure still come only from the host.
internal static class ObjectiveProgressSync
{
    private static readonly Dictionary<int, string> _sent = new();
    private static readonly Dictionary<int, string> _hostHud = new();
    private static IntPtr _battle;
    private static float _nextPoll;
    internal static bool Applying;

    private static ObjectiveManager Mgr() => TacticalManager.Get()?.GetMission()?.Objectives;

    private static void ResetIfNewBattle()
    {
        var tm = TacticalManager.Get();
        if (tm == null || _battle == tm.Pointer) return;
        _battle = tm.Pointer; _sent.Clear(); _hostHud.Clear();
    }

    // Host: called every frame from CoopRunner; polls every 2 s.
    internal static void Tick()
    {
        if (!CoopNet.Connected || CoopNet.Mode != CoopMode.Host) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now < _nextPoll) return;
        _nextPoll = now + 2f;
        try
        {
            if (TacticalManager.Get() == null) return;
            // The guest loads into the battle ~40 s after the host; anything sent
            // before then lands on no objectives and would never be re-sent.
            if (!SessionSync.PartnerBattleConfirmed) return;
            ResetIfNewBattle();
            var mgr = Mgr();
            if (mgr == null) return;
            int n = mgr.GetCount(new Il2CppSystem.Nullable<ObjectiveState>());
            for (int i = 0; i < n; i++)
            {
                var o = mgr.Get(i);
                if (o == null) continue;
                string hud = "", prog = "";
                try { hud = o.GetTranslatedHUDText() ?? ""; } catch { }
                try { prog = o.GetProgressAsString() ?? ""; } catch { }
                string payload = hud + "|~|" + prog;   // plain separator: control chars in the JSON payload are a risk
                if (_sent.TryGetValue(i, out var last) && last == payload) continue;
                _sent[i] = payload;
                CoopNet.Send(new CoopMsg { T = "objprog", Prm = i, Data = payload });
                Plugin.Logger.LogInfo($"[OBJ] #{i} progress sent: '{hud}' {prog}");
            }
        }
        catch (Exception e)
        {
            if (!_failLogged) { _failLogged = true; Plugin.Logger.LogWarning($"[OBJ] progress poll failed: {e.GetType().Name}: {e.Message}"); }
        }
    }
    private static bool _failLogged;

    internal static void HandleRemote(CoopMsg m)
    {
        if (CoopNet.Mode == CoopMode.Host) return;
        try
        {
            ResetIfNewBattle();
            var mgr = Mgr();
            if (mgr == null) return;
            int n = mgr.GetCount(new Il2CppSystem.Nullable<ObjectiveState>());
            if (m.Prm < 0 || m.Prm >= n) return;
            var o = mgr.Get(m.Prm);
            if (o == null) return;
            string data = m.Data ?? "";
            int sep = data.IndexOf("|~|");
            string hud = sep < 0 ? data : data.Substring(0, sep);
            string prog = sep < 0 ? "" : data.Substring(sep + 3);
            _hostHud[m.Prm] = hud;
            // Let the objective UI redraw from the text it will now read.
            Applying = true;
            try { o.InvokeObjectiveProgressChanged(); } catch { }
            finally { Applying = false; }
            Plugin.Logger.LogInfo($"[OBJ] #{m.Prm} progress from host: '{hud}' {prog}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[OBJ] progress apply failed: {e.Message}"); }
    }

    internal static bool TryHostHud(Objective o, out string hud)
    {
        hud = null;
        if (CoopNet.Mode != CoopMode.Guest || !CoopNet.Connected || _hostHud.Count == 0) return false;
        try
        {
            var mgr = Mgr();
            if (mgr == null) return false;
            int n = mgr.GetCount(new Il2CppSystem.Nullable<ObjectiveState>());
            for (int i = 0; i < n; i++)
                if (mgr.Get(i)?.Pointer == o.Pointer) return _hostHud.TryGetValue(i, out hud) && !string.IsNullOrEmpty(hud);
        }
        catch { }
        return false;
    }
}

[HarmonyPatch]
internal static class ObjectiveProgressPatches
{
    [HarmonyPatch(typeof(Objective), nameof(Objective.GetTranslatedHUDText))]
    [HarmonyPostfix]
    private static void HostText(Objective __instance, ref string __result)
    {
        if (ObjectiveProgressSync.TryHostHud(__instance, out var hud)) __result = hud;
    }
}
