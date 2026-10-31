using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppMenace.States;
using Il2CppMenace.Strategy;
using Il2CppMenace.UI;
using Il2CppMenace.UI.Strategy;
using Menace = Il2CppMenace;

namespace MenaceCoop;

// PLAYERS DEPLOY THEIR OWN SQUADS (user 2026-10-02: "make it so users can deploy
// their troops where they like and sync it to host and everyone else, ideally
// instantly"). Same shape as loadouts (EquipmentSync): a client drags a unit it
// owns in the prep picker; the move happens on its own map at once; the
// position writes of that drop -- one unit, or two when the drop SWAPS with
// another -- go to the host as one request ("dposreq"); the host checks the
// player owns the dragged unit (a swapped partner may be anyone's: it only moves
// to the tile the dragged unit left), applies them, and the existing "dpos"
// mirror shows the result on every map. Refused: the host resends its positions
// for those slots, which puts the requester's map back. Moves of a unit the
// player does not own are undone locally.
internal static class DeploySync
{
    private static readonly List<(int slot, int x, int y, int ox, int oy, bool own)> _batch = new();
    private static int _batchFrame = -1;

    private static List<DeployedEntity> Entities()
    {
        var l = StrategyState.Get()?.BattlePlan?.GetEntitiesToDeploy()
            ?.TryCast<Il2CppSystem.Collections.Generic.List<DeployedEntity>>();
        var r = new List<DeployedEntity>();
        for (int i = 0; l != null && i < l.Count; i++) r.Add(l[i]);
        return r;
    }

    private static bool ClientInPrep =>
        CoopNet.Mode == CoopMode.Guest && CoopNet.Connected && SessionSync.ViewerPrepActive
        && !SessionSync.ApplyingRemoteDeploy && !SessionSync.RemoteLoadInProgress
        && Menace.Tactical.TacticalManager.Get() == null;

    internal static bool LocalOwns(DeployedEntity e)
    {
        try { var l = e?.GetUnitLeader(); return l != null && CoopRunner.PrepSeatOfLeader(l.Pointer) == Peers.LocalSeat; }
        catch { return false; }
    }

    // Client, from the SetTilePosition hook: record one write of this frame's drop.
    // Only a player's DRAG counts: the game also sets positions itself (auto-
    // placement when prep opens), and those must never overwrite the host's.
    private static bool _dragging;
    private static int _dragEndFrame = -10;
    internal static void DragStarted() => _dragging = true;
    internal static void DragEnded() { _dragging = false; _dragEndFrame = UnityEngine.Time.frameCount; }
    private static bool InDrag => _dragging || UnityEngine.Time.frameCount <= _dragEndFrame + 2;

    internal static void OnClientSet(DeployedEntity e, UnityEngine.Vector2Int old)
    {
        if (!ClientInPrep || e == null || !InDrag) return;
        var list = Entities();
        int slot = list.FindIndex(x => x != null && x.Pointer == e.Pointer);
        if (slot < 0) return;
        var p = e.GetTilePosition();
        if (p.x == old.x && p.y == old.y) return;
        if (_batchFrame != UnityEngine.Time.frameCount) { Flush(); _batchFrame = UnityEngine.Time.frameCount; }
        _batch.Add((slot, p.x, p.y, old.x, old.y, LocalOwns(e)));
    }

    // Every frame (CoopRunner): send -- or undo -- the previous frame's drop.
    internal static void Tick()
    {
        if (_batch.Count > 0 && _batchFrame != UnityEngine.Time.frameCount) Flush();
    }

    private static void Flush()
    {
        if (_batch.Count == 0) return;
        var b = new List<(int slot, int x, int y, int ox, int oy, bool own)>(_batch);
        _batch.Clear();
        if (b.Exists(m => m.own))
        {
            var parts = new List<string>();
            foreach (var m in b) parts.Add($"{m.slot}:{m.x}:{m.y}");
            CoopNet.Send(new CoopMsg { T = "dposreq", Data = string.Join(";", parts) });
            Plugin.Logger.LogInfo($"[DEPLOY] Moved our unit(s) -- {string.Join(", ", parts)}; sent to the host");
            return;
        }
        // Someone else's unit: put it back.
        var list = Entities();
        SessionSync.ApplyingRemoteDeploy = true;
        try
        {
            foreach (var m in b)
                if (m.slot < list.Count) list[m.slot]?.SetTilePosition(new UnityEngine.Vector2Int(m.ox, m.oy));
        }
        finally { SessionSync.ApplyingRemoteDeploy = false; }
        SnapAll();
        CoopNet.Status = "You can only deploy your own squads";
        Plugin.Logger.LogInfo("[DEPLOY] Undid a move of a unit that is not ours");
    }

    // Host: apply a player's drop.
    internal static void HandleRequest(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Host || m.From <= 0 || string.IsNullOrEmpty(m.Data)) return;
        var list = Entities();
        var moves = new List<(int slot, int x, int y)>();
        foreach (var part in m.Data.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = part.Split(':');
            if (f.Length == 3 && int.TryParse(f[0], out int s) && int.TryParse(f[1], out int x) && int.TryParse(f[2], out int y)
                && s >= 0 && s < list.Count && list[s] != null)
                moves.Add((s, x, y));
        }
        string why = null;
        if (!SessionSync.HostPrepActive) why = "deployment can only change during mission planning";
        else if (moves.Count == 0) why = "nothing to move";
        else
        {
            // Every unit moved must be the player's, or a swap partner going to
            // the tile one of the player's units left.
            var vacated = new HashSet<(int, int)>();
            foreach (var mv in moves)
                if (CoopRunner.PrepSeatOfLeader(list[mv.slot].GetUnitLeader()?.Pointer ?? IntPtr.Zero) == m.From)
                { var p = list[mv.slot].GetTilePosition(); vacated.Add((p.x, p.y)); }
            if (vacated.Count == 0) why = "that squad is not assigned to you";
            foreach (var mv in moves)
            {
                bool own = CoopRunner.PrepSeatOfLeader(list[mv.slot].GetUnitLeader()?.Pointer ?? IntPtr.Zero) == m.From;
                if (!own && !vacated.Contains((mv.x, mv.y))) { why = "that squad is not assigned to you"; break; }
            }
        }
        if (why == null)
        {
            foreach (var mv in moves) list[mv.slot].SetTilePosition(new UnityEngine.Vector2Int(mv.x, mv.y));   // mirrors as "dpos"
            SnapAll();
            Plugin.Logger.LogInfo($"[DEPLOY] {Peers.NameOf(m.From)} deployed: {m.Data}");
            return;
        }
        Plugin.Logger.LogInfo($"[DEPLOY] Refused {Peers.NameOf(m.From)}'s deployment ({m.Data}): {why}");
        CoopNet.SendTo(new CoopMsg { T = "dposrej", Data = why }, m.From);
        foreach (var mv in moves)   // put the requester's map back
        {
            var p = list[mv.slot].GetTilePosition();
            CoopNet.SendTo(new CoopMsg { T = "dpos", MIdx = mv.slot, TX = p.x, TY = p.y }, m.From);
        }
    }

    internal static void HandleRefusal(CoopMsg m)
    {
        CoopNet.Status = "Deployment refused: " + (m.Data ?? "");
        Plugin.Logger.LogInfo($"[DEPLOY] Host refused our deployment: {m.Data}");
    }

    // Redraw every unit marker on an open prep map (host's own, after a player's move).
    internal static void SnapAll()
    {
        try
        {
            var open = UIManager.Get()?.m_OpenScreens;
            for (int i = 0; open != null && i < open.Count; i++)
            {
                var ps = open[i]?.TryCast<MissionPrepUIScreen>();
                var sel = ps?.m_UnitSelector;
                if (sel == null) continue;
                foreach (var e in Entities()) sel.GetDeployedEntity(e)?.SnapToTilePosition();
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[DEPLOY] map redraw failed: {e.Message}"); }
    }
}

[HarmonyPatch]
internal static class DeploySyncPatches
{
    [HarmonyPatch(typeof(MissionPrepDeployedEntity), nameof(MissionPrepDeployedEntity.OnDragStart))]
    [HarmonyPostfix]
    private static void DragStart() => DeploySync.DragStarted();

    [HarmonyPatch(typeof(MissionPrepDeployedEntity), nameof(MissionPrepDeployedEntity.OnDragEnd))]
    [HarmonyPostfix]
    private static void DragEnd() => DeploySync.DragEnded();

    [HarmonyPatch(typeof(DeployedEntity), nameof(DeployedEntity.SetTilePosition))]
    [HarmonyPrefix]
    private static void Before(DeployedEntity __instance, out UnityEngine.Vector2Int __state)
    {
        __state = default;
        try { __state = __instance.GetTilePosition(); } catch { }
    }

    [HarmonyPatch(typeof(DeployedEntity), nameof(DeployedEntity.SetTilePosition))]
    [HarmonyPostfix]
    private static void After(DeployedEntity __instance, UnityEngine.Vector2Int __state)
        => DeploySync.OnClientSet(__instance, __state);
}
