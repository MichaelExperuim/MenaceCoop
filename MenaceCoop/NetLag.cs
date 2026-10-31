#if COOP_DEVTOOLS
using System.Collections.Generic;
using UnityEngine;

namespace MenaceCoop;

// SIMULATED SLOW LINK (dev builds only).
//
// Nearly every test runs two to four copies of the game on one machine, where a
// message arrives the frame it is sent -- so nothing that races the network ever
// loses in the harness. A Nexus report (2026-10-30: guest thrown out at mission
// start, or in with no bottom bar) came from exactly such a race over a real
// connection. This holds each received message for SimLatencyMs (+ up to
// SimJitterMs) before CoopRunner hands it to Replication, keeping arrival order
// like TCP does. Receive side only, on every machine, so a round trip costs
// about twice the setting. Not compiled into a release (COOP_DEVTOOLS).
internal static class NetLag
{
    private static readonly Queue<(CoopMsg msg, float due)> _held = new();
    private static readonly System.Random _rng = new();
    private static float _lastDue;
    private static string _announced;

    internal static bool TryNext(out CoopMsg msg)
    {
        int lat = Plugin.SimLatencyMs?.Value ?? 0;
        int jit = Plugin.SimJitterMs?.Value ?? 0;
        if (CoopNet.Mode == CoopMode.Off) _held.Clear();
        if (lat <= 0 && jit <= 0 && _held.Count == 0)
            return CoopNet.Incoming.TryDequeue(out msg);

        string setting = $"{lat}+{jit}";
        if (setting != _announced)
        {
            _announced = setting;
            Plugin.Logger.LogWarning($"[NETLAG] [DEVTOOLS] Simulating a slow link: received messages held {lat} ms + up to {jit} ms jitter (dev setting)");
        }

        float now = Time.realtimeSinceStartup;
        while (CoopNet.Incoming.TryDequeue(out var m))
        {
            float due = now + (lat + (jit > 0 ? _rng.Next(jit + 1) : 0)) / 1000f;
            if (due < _lastDue) due = _lastDue;   // never overtake an earlier message
            _lastDue = due;
            _held.Enqueue((m, due));
        }
        if (_held.Count > 0 && _held.Peek().due <= now)
        {
            msg = _held.Dequeue().msg;
            return true;
        }
        msg = null;
        return false;
    }
}
#endif
