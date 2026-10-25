using System.Collections.Generic;
using HarmonyLib;
#if MELON
using Il2CppMenace.Tactical;
#else
using Menace.Tactical;
#endif

namespace MenaceCoop;

// #64 / fog timing (user 2026-10-04: "desyncs caused from animation speedups when
// in fog"). Both speed levers are already clamped to 1x on every machine (r128:
// 17 clamps host and client alike), so what is left to measure is the MOVE
// itself: does a unit hidden by fog finish its walk sooner than a visible one,
// and does the engine snap it with PathMover.TeleportToTargetAndCancel? Logs one
// [FOGT] line per finished move with its duration and visibility at both ends.
[HarmonyPatch]
internal static class FogTimingProbe
{
    private static readonly Dictionary<System.IntPtr, (float at, bool vis, string dest)> _started = new();

    private static bool Vis(Actor a)
    {
        try { return a.Is3DModelVisibleToPlayer(); } catch { return false; }
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.MoveTo))]
    [HarmonyPostfix]
    private static void Started(Actor __instance, Tile _tile, bool __result)
    {
        if (!__result || !BugProbes.On || !CoopNet.Connected || __instance == null) return;
        try
        {
            var p = _tile?.GetTilePos();
            _started[__instance.Pointer] = (UnityEngine.Time.realtimeSinceStartup, Vis(__instance),
                                            p == null ? "?" : $"({p.Value.x},{p.Value.y})");
            _actors[__instance.Pointer] = __instance;
        }
        catch { }
    }

    // r129: a Harmony prefix on Actor.OnMovementFinished never fired (0 lines over
    // 57 moves -- the engine's call does not reach the patch), so poll instead:
    // a started move is done on the first frame its actor reports !IsMoving().
    private static readonly Dictionary<System.IntPtr, Actor> _actors = new();
    private static readonly List<System.IntPtr> _done = new();

    internal static void Tick()
    {
        if (_started.Count == 0) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        _done.Clear();
        foreach (var kv in _started)
        {
            if (!_actors.TryGetValue(kv.Key, out var a)) { _done.Add(kv.Key); continue; }
            bool moving;
            try { moving = a.IsMoving(); } catch { _done.Add(kv.Key); continue; }
            // A move accepted this frame may not have started moving yet.
            if (moving || now - kv.Value.at < 0.05f) continue;
            _done.Add(kv.Key);
            try
            {
                Plugin.Logger.LogInfo($"[FOGT] move done '{DesyncDetector.TemplateNameOf(a)}' -> {kv.Value.dest} "
                    + $"{now - kv.Value.at:0.00}s visStart={kv.Value.vis} visEnd={Vis(a)} "
                    + $"player={Ownership.IsKnownPlayerUnit(a)} host={CoopNet.Mode == CoopMode.Host}");
            }
            catch { }
        }
        foreach (var k in _done) { _started.Remove(k); _actors.Remove(k); }
    }

    [HarmonyPatch(typeof(PathMover), nameof(PathMover.TeleportToTargetAndCancel))]
    [HarmonyPrefix]
    private static void Snapped()
    {
        if (!BugProbes.On || !CoopNet.Connected) return;
        Plugin.Logger.LogInfo($"[FOGT] PathMover snapped to target (TeleportToTargetAndCancel) fastForward={SafeFF()}");
    }

    private static bool SafeFF()
    {
        try { return PathMover.IsFastForward(); } catch { return false; }
    }
}
