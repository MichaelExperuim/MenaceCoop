using System;
using System.Collections.Generic;
#if MELON
using Il2CppMenace.Tactical;
#else
using Menace.Tactical;
#endif

namespace MenaceCoop;

// FOG OF WAR DIFFERED BETWEEN MACHINES (user hand test 2026-10-02: "fog of war
// differs slightly between machine actually"; enemies "looked invisible" on one
// screen). Each machine computes fog from its own board, updating vision as a
// unit WALKS. Units co-op PLACES instead -- a landing correction, a snapshot
// position fix, a rejoin catch-up, an interrupted move matched by teleport --
// leave the fog where it was. So after any relayed move or correction, once the
// board is still, every machine recomputes vision from where its units actually
// stand (the engine's own RecalculateVisibilityBasedOnVision), at most once a
// second. Each recompute logs how many enemies changed visibility: a non-zero
// count is stale fog that was just healed.
internal static class FogSync
{
    private static bool _dirty;
    private static float _lastAt = -999f;
    private const float MinGap = 1f;

    internal static void MarkDirty(string t)
    {
        switch (t)
        {
            case "move": case "moveskill": case "land": case "skill":
            case "statesync": case "hardsync": case "edie": case "sdie":
            // An enemy that revealed itself by shooting is hidden again when its
            // activation ends -- on the host, by its own turn processing, which
            // the client skips. r109: a chaingun team stayed visible on the
            // clients for 3 rounds after the host's fog had it hidden. So
            // recompute at the same two moments: activation end, faction change.
            case "aidone": case "aiadvance":
                _dirty = true; break;
        }
    }

    // HOST-DECIDED FOG (#64, user 2026-10-04). The engine plays a move of a unit
    // hidden from the player instantly (r131: 22 hidden moves at 0.05 s, visible
    // ones 4.9 s), so wherever the machines disagree about fog, the same relayed
    // order runs on two clocks and everything sequenced after it drifts. The host
    // stamps its visibility of the acting AI unit on every order (StampIds); the
    // client adopts it before replaying, so every machine makes the same
    // instant-or-walk choice -- and shows what the host's fog shows.
    internal static int HostVis(Actor a)
    {
        try { return a.Is3DModelVisibleToPlayer() ? 2 : 1; } catch { return 0; }
    }

    internal static void ApplyHostVis(Actor a, int hostVis, string order)
    {
        if (CoopNet.Mode != CoopMode.Guest || hostVis == 0) return;
        try
        {
            if (Ownership.IsKnownPlayerUnit(a)) return;
            bool hostSees = hostVis == 2;
            bool iSee = a.Is3DModelVisibleToPlayer();
            if (iSee == hostSees) return;
            a.SetVisibility(hostSees ? Visibility.Visible : Visibility.Hidden);
            bool now = a.Is3DModelVisibleToPlayer();
            Plugin.Logger.LogInfo($"[VIS] Host-decided fog for '{order}': '{DesyncDetector.TemplateNameOf(a)}' "
                + $"{(iSee ? "visible" : "hidden")} here -> {(hostSees ? "visible" : "hidden")} like the host"
                + (now == hostSees ? "" : " -- DID NOT TAKE"));
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[VIS] host-decided fog failed: {e.Message}"); }
    }

    // The host's view adopted by PhantomWatch.AdoptHostView, held until the next
    // host report replaces it. Our own recompute below would otherwise undo it
    // every relayed order, and the enemy would flicker between the two views.
    private static readonly Dictionary<IntPtr, (Actor a, bool vis)> _overrides = new();

    internal static void SetOverrides(Dictionary<IntPtr, bool> want, Actor[] actors)
    {
        _overrides.Clear();
        if (want.Count == 0) return;
        foreach (var a in actors)
            if (a != null && want.TryGetValue(a.Pointer, out bool v)) _overrides[a.Pointer] = (a, v);
    }

    internal static bool HasOverride(IntPtr p) => _overrides.ContainsKey(p);

    // Every frame on a client, not only after our own recompute: the engine's own
    // vision updates re-reveal too, and a 3 s wait for the next host report was
    // a visible flicker (r133).
    private static int _reapplied;
    private static void ReapplyOverrides()
    {
        foreach (var kv in _overrides)
        {
            try
            {
                var (a, v) = kv.Value;
                if (a.IsDying()) continue;
                if ((a.VisibilityToPlayer == Visibility.Visible) != v)
                {
                    a.SetVisibility(v ? Visibility.Visible : Visibility.Hidden);
                    if (++_reapplied % 50 == 1)
                        Plugin.Logger.LogInfo($"[VIS] engine re-revealed '{DesyncDetector.TemplateNameOf(a)}' against the host's view -- held ({_reapplied} so far)");
                }
            }
            catch { }
        }
    }

    internal static void Tick()
    {
        if (_overrides.Count > 0 && CoopNet.Mode == CoopMode.Guest) ReapplyOverrides();
        // The HOST recomputes too (user 2026-10-04, r134: a friendly militia at the fog
        // edge was visible on every client and hidden on the host). Only clients used
        // to, so the host's view was the stale one -- its fog moves only on the
        // engine's own vision events. The AI's sight is separate (VisibilityToAI), and
        // HVis stamps keep clients on the host's instant-or-walk choice regardless.
        if (!_dirty || !CoopNet.Connected || CoopNet.Mode == CoopMode.Off) return;
        var tm = TacticalManager.Get();
        if (tm == null) { _dirty = false; return; }
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now - _lastAt < MinGap || Replication.AnyActorMoving()) return;
        _dirty = false;
        _lastAt = now;
        try
        {
            var before = VisibleEnemies(tm);
            tm.RecalculateVisibilityBasedOnVision();
            ReapplyOverrides();
            var after = VisibleEnemies(tm);
            int changed = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in after)
                if (!before.TryGetValue(kv.Key, out bool b) || b != kv.Value)
                {
                    changed++;
                    if (sb.Length < 300) sb.Append(sb.Length > 0 ? ", " : "").Append(kv.Value ? "now visible: " : "now hidden: ").Append(kv.Key);
                }
            if (changed > 0) Plugin.Logger.LogInfo($"[VIS] Fog recomputed: {changed} enemy visibility change(s) -- {sb}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[VIS] fog recompute failed: {e.Message}"); }
    }

    // template@(x,y) -> visible to the player
    private static System.Collections.Generic.Dictionary<string, bool> VisibleEnemies(TacticalManager tm)
    {
        var d = new System.Collections.Generic.Dictionary<string, bool>();
        int pf = -1;
        try { pf = tm.GetPlayerFaction()?.GetIndex() ?? -1; } catch { }
        foreach (var a in DesyncDetector.AllActors(tm))
        {
            try
            {
                if (a == null || a.IsDying() || a.GetFactionID() == pf || Ownership.IsKnownPlayerUnit(a)) continue;
                if (!PhantomWatch.IsHostile(tm, a)) continue;
                var p = a.GetLastTile()?.GetTilePos();
                if (p == null) continue;
                d[$"{DesyncDetector.TemplateNameOf(a)}@({p.Value.x},{p.Value.y})"] = a.VisibilityToPlayer == Visibility.Visible;
            }
            catch { }
        }
        return d;
    }
}
