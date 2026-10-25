using System;
using System.Collections.Generic;
#if MELON
using Il2CppMenace.Tactical;
#else
using Menace.Tactical;
#endif

namespace MenaceCoop;

// CLIENTS PLAY THE HOST'S AI MOVES ONE AT A TIME (user 2026-10-02: "i dont want
// simultaneous since that can cause desyncs").
//
// The host resolves AI units it cannot see instantly, so a whole faction's moves
// arrive within half a second (r100: 5 units on M2 in 0.5 s) and each client
// started them all at once. Once a host AI move arrives while another is still
// animating, it -- and EVERY message after it, in arrival order -- waits here and
// plays back as the board goes idle. Order is never changed, so a later hit,
// turn hand-over or snapshot still lands after the move it follows; the client
// only runs a few seconds behind the host. Network / roster chatter that does not
// touch the board passes straight through. Failsafe: nothing waits over 20 s.
internal static class AiPacing
{
    private static readonly Queue<(CoopMsg m, float at)> _queue = new();
    private static float _holdUntil;
    private const float MoveStart = 0.3f;   // a walk issued this frame is not "moving" yet
    private const float Failsafe = 20f;

    private static readonly HashSet<string> PassThrough = new()
    {
        "hello", "seat", "players", "pname", "ping", "auth1", "auth2",
        "cursor", "tcursor", "fmeta", "fchunk", "fdone",
    };

    internal static int Count => _queue.Count;

    // True = queued (the caller must not apply it now).
    internal static bool Intercept(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest || m?.T == null) return false;
        if (TacticalManager.Get() == null) { if (_queue.Count > 0) Flush("left the battle"); return false; }
        if (PassThrough.Contains(m.T)) return false;
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (_queue.Count > 0) { _queue.Enqueue((m, now)); return true; }
        if (!IsAiMove(m)) return false;
        if (now < _holdUntil || Replication.AnyActorMoving())
        {
            _queue.Enqueue((m, now));
            return true;
        }
        _holdUntil = now + MoveStart;   // applied now; the next one waits for it
        return false;
    }

    // Every frame, after the receive loop.
    internal static void Tick()
    {
        if (_queue.Count == 0) return;
        if (TacticalManager.Get() == null) { Flush("left the battle"); return; }
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now - _queue.Peek().at > Failsafe) { Flush($"waited over {Failsafe:F0}s"); return; }
        while (_queue.Count > 0)
        {
            var m = _queue.Peek().m;
            if (IsAiMove(m))
            {
                if (now < _holdUntil || Replication.AnyActorMoving()) return;
                _queue.Dequeue();
                Apply(m);
                _holdUntil = now + MoveStart;
                return;   // one AI move per idle board
            }
            _queue.Dequeue();
            Apply(m);
        }
    }

    private static void Flush(string why)
    {
        Plugin.Logger.LogWarning($"[PACE] Playing {_queue.Count} queued message(s) at once: {why}");
        while (_queue.Count > 0) Apply(_queue.Dequeue().m);
    }

    private static void Apply(CoopMsg m)
    {
        try { Replication.Apply(m); }
        catch (Exception e) { Plugin.Logger.LogError($"[SYNC] Failed to apply remote message '{m.T}': {e}"); }
    }

    // A host AI unit's move (players' units are not paced).
    private static bool IsAiMove(CoopMsg m)
    {
        if (m.T != "move" && m.T != "moveskill") return false;
        try
        {
            var a = Replication.ResolveOrderActor(m);
            return a != null && !(Ownership.Active && Ownership.IsKnownPlayerUnit(a));
        }
        catch { return false; }
    }
}
