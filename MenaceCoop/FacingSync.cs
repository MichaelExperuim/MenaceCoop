using System;
using System.Collections.Generic;
using HarmonyLib;
#if MELON
using Il2CppMenace.Tactical;
#else
using Menace.Tactical;
#endif

namespace MenaceCoop;

// FACING OF A PLAYER UNIT WAS NEVER RELAYED BY ITS OWNER.
//
// 2026-10-15, v0.7.17, 3-player hand test: M3 drove its troop carrier one tile
// toward a pirate it had just spotted. On M3 the move took 4.9 s and ended with
// the hull turned ~90 degrees; host and M2 replayed the same move in 2.4 s and
// kept the old facing. Same tile everywhere, different facing -- and facing
// decides cover and armour arcs. Nothing repaired it: DesyncDetector's facing
// correction is owner-scoped (never turns a unit this machine commands), and the
// owner never told anyone its result.
//
// Same rule as stance (SuppressionSync.AfterStance): the unit's authority -- its
// owner for player units -- relays the settled facing as an absolute value.
// Sent when a move of ours finishes and whenever the engine turns a standing unit
// (turn-to-threat). A receiver whose replayed move is still running holds the
// value until its unit stops, so the replay's own final turn cannot undo it.
internal static class FacingSync
{
    private static readonly Dictionary<IntPtr, Actor> _moving = new();
    private static readonly Dictionary<IntPtr, int> _lastSent = new();
    private static readonly Dictionary<IntPtr, (Actor a, int dir, float at)> _pending = new();
    private static readonly List<IntPtr> _done = new();

    private static bool Mine(Actor a)
        => Ownership.Active && Ownership.IsKnownPlayerUnit(a) && Ownership.IsLocal(a);

    [HarmonyPatch(typeof(Actor), nameof(Actor.MoveTo))]
    [HarmonyPostfix]
    private static void AfterMoveTo(Actor __instance, bool __result)
    {
        if (!__result || !CoopNet.Connected || Replication.ApplyingRemote || __instance == null) return;
        try { if (Mine(__instance)) _moving[__instance.Pointer] = __instance; } catch { }
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.SetDirection))]
    [HarmonyPostfix]
    private static void AfterSetDirection(Actor __instance)
    {
        if (!CoopNet.Connected || Replication.ApplyingRemote || __instance == null) return;
        try
        {
            // Mid-walk the path turns the unit every step; the move-done send
            // carries the result.
            if (TacticalManager.Get() == null || __instance.IsMoving() || !Mine(__instance)) return;
            Send(__instance);
        }
        catch { }
    }

    private static void Send(Actor a)
    {
        int dir = (int)a.GetDirection();
        if (_lastSent.TryGetValue(a.Pointer, out int last) && last == dir) return;
        _lastSent[a.Pointer] = dir;
        var p = a.GetLastTile()?.GetTilePos();
        var msg = new CoopMsg { T = "efacing", AX = p?.x ?? -1, AY = p?.y ?? -1, Prm = dir };
        Replication.StampIds(msg, a, isOrder: false);
        CoopNet.Send(msg);
        Plugin.Logger.LogInfo($"[FACING] sent '{DesyncDetector.TemplateNameOf(a)}' facing {dir}");
    }

    internal static void Handle(CoopMsg m)
    {
        var actor = Replication.ResolveOrderActor(m);
        if (actor == null) return;
        _pending[actor.Pointer] = (actor, m.Prm, UnityEngine.Time.realtimeSinceStartup);
    }

    // CoopRunner tick.
    internal static void Tick()
    {
        if (_moving.Count == 0 && _pending.Count == 0) return;
        if (!CoopNet.Connected) { _moving.Clear(); _pending.Clear(); _lastSent.Clear(); return; }

        _done.Clear();
        foreach (var kv in _moving)
        {
            try { if (kv.Value.IsMoving()) continue; Send(kv.Value); } catch { }
            _done.Add(kv.Key);
        }
        foreach (var k in _done) _moving.Remove(k);

        _done.Clear();
        float now = UnityEngine.Time.realtimeSinceStartup;
        foreach (var kv in _pending)
        {
            var (a, dir, at) = kv.Value;
            try
            {
                // Our replay of the owner's move may still be walking. Wait for
                // it, but not forever: a unit that never reports stopped is the
                // corrector's problem, not a reason to hold this value for good.
                if (a.IsMoving() && now - at < 15f) continue;
                if (!a.IsMoving() && (int)a.GetDirection() != dir)
                {
                    bool prev = Replication.ApplyingRemote;
                    Replication.ApplyingRemote = true;
                    try { a.SetDirection((Direction)dir); }
                    finally { Replication.ApplyingRemote = prev; }
                    Plugin.Logger.LogInfo($"[FACING] '{DesyncDetector.TemplateNameOf(a)}' facing -> {dir} (owner)");
                }
            }
            catch { }
            _done.Add(kv.Key);
        }
        foreach (var k in _done) _pending.Remove(k);
    }
}
