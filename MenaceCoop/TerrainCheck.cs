using System;
using System.Collections.Generic;
using System.Text;
#if MELON
using Il2CppMenace.Tactical;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.Tactical;
#endif

namespace MenaceCoop;

// IS IT EVEN THE SAME MAP?
//
// The mod has never checked. Not one line of it touched terrain: the guest
// relaunches the mission from the host's mission seed and the two machines are
// assumed to generate identical ground. Exactly the same bet that turned out to
// be losing for the enemy roster (see BattleRoster), made for the larger and
// more visible half of the board.
//
// A player reported it directly after a terrain update: "the game update added
// and updated terrain and these new or changed entities did not sync between our
// games either in the planning mission screen or in battle."
//
// Terrain divergence is worse than it sounds, because terrain is not scenery
// here. Cover decides whether a shot lands, line-of-sight decides what each
// player can see at all, and surface decides where a unit may walk. Two players
// on subtly different maps will disagree about hit chances and about which
// squares are reachable, and every one of those disagreements is invisible --
// the state hash covers actor position, hitpoints and player action points, and
// nothing else. A map made of different rock has always hashed as identical.
//
// So: fingerprint every tile, ship it once at battle start, and diff. Like the
// roster check this DETECTS rather than repairs -- regenerating one machine's
// terrain to match the other's is not something the engine exposes -- but it
// names the tiles, which is the difference between a bug report and a fix.
internal static class TerrainCheck
{
    // Probed, not assumed. GetTile's bool overload is bounds-checked, so the
    // real extent is discovered by walking until the rows run out. Missions in
    // the reported logs ran to roughly (40,39); the ceiling is generous and only
    // costs a bounded number of failed probes once per battle.
    private const int MaxDim = 256;

    // NOT WALKABILITY. IsValidMovementDestination is board state, not terrain: a
    // tile with a unit standing on it is not a valid destination, so including it
    // made the map report differences wherever the two sides had units in
    // different places. The first real catch was twenty tiles differing on
    // "walkable" alone, at exactly the tiles the enemies were standing on -- a
    // roster difference wearing a terrain costume.
    //
    // Three bytes a tile: surface, cover, flags. At the sizes this game uses
    // that is a few tens of KB before base64 -- the file transfer in this same
    // message channel moves 256KB chunks, so shipping the whole grid rather than
    // a hash costs nothing and buys the ability to say WHICH tiles differ.
    private const int Stride = 3;

    private static string _pending;
    private static bool _checkedThisBattle;
    private static IntPtr _lastTm;

    internal static bool Mismatched { get; private set; }

    internal static void Reset()
    {
        _pending = null;
        _checkedThisBattle = false;
        Mismatched = false;
    }

    private static bool TryTile(Map map, int x, int z, out Tile tile)
    {
        tile = null;
        try { tile = map.GetTile(x, z); } catch { return false; }
        return tile != null;
    }

    // "W,H,<base64>" -- dimensions travel with the payload so a size difference
    // is itself reportable rather than a decoding accident.
    internal static string Build(TacticalManager tm)
    {
        try
        {
            var map = tm?.GetMap();
            if (map == null) return null;

            int w = 0, h = 0;
            for (int x = 0; x < MaxDim; x++) { if (!TryTile(map, x, 0, out _)) break; w = x + 1; }
            for (int z = 0; z < MaxDim; z++) { if (!TryTile(map, 0, z, out _)) break; h = z + 1; }
            if (w == 0 || h == 0) return null;

            var bytes = new byte[w * h * Stride];
            for (int x = 0; x < w; x++)
            {
                for (int z = 0; z < h; z++)
                {
                    if (!TryTile(map, x, z, out var t)) continue;
                    int i = (x * h + z) * Stride;
                    try { bytes[i] = (byte)(int)t.GetSurfaceType(); } catch { }
                    try { bytes[i + 1] = (byte)(t.GetCoverMask() & 0xFF); } catch { }

                    int flags = 0;
                    try { if (t.IsBlockingLineOfSight()) flags |= 1; } catch { }
                    try { if (t.HasStructure()) flags |= 2; } catch { }
                    try { if (t.HasEnvironmentFeature()) flags |= 4; } catch { }
                    bytes[i + 2] = (byte)flags;
                }
            }
            return BattleRoster.BattleId(tm) + "#" + w + "," + h + "," + Convert.ToBase64String(bytes);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("[TERRAIN] Build failed: " + e.Message);
            return null;
        }
    }

    internal static void Publish(TacticalManager tm)
    {
        LogLight();
        if (!Plugin.CheckTerrain.Value) return;
        if (tm == null) return;
        try
        {
            string payload = Build(tm);
            if (payload == null) return;
            CoopNet.Send(new CoopMsg { T = "terrain", Data = payload });
            var d = payload.Substring(payload.IndexOf('#') + 1).Split(',');
            Plugin.Logger.LogInfo("[TERRAIN] Sent map fingerprint: " + d[0] + "x" + d[1] + " tiles");
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[TERRAIN] Publish failed: " + e.Message); }
    }

    // v0.7.15 daylight cycle: the light condition changes vision, so both
    // machines must fight under the same one. Logged on both (host at publish,
    // guest on receipt) for log comparison.
    private static void LogLight()
    {
        try
        {
            var op = Menace.States.StrategyState.Get()?.Operations?.GetCurrentOperation();
            var mission = op?.GetCurrentMission();
            Plugin.Logger.LogInfo($"[LIGHT] Battle light: mission={mission?.GetLightCondition()} operation={op?.GetCurrentLightCondition()} (idx {op?.GetCurrentLightConditionIndex()})");
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[LIGHT] read failed: " + e.Message); }
    }

    internal static void OnRemote(string payload)
    {
        LogLight();
        if (!Plugin.CheckTerrain.Value) return;
        _pending = payload ?? "";
        _checkedThisBattle = false;
        TryCompare();
    }

    internal static void Tick()
    {
        PublishPerBattle();
        if (_pending == null || _checkedThisBattle) return;
        TryCompare();
    }

    // Per battle, not per session -- see BattleRoster.PublishPerBattle for why
    // hanging this off the join handshake meant only the first mission of a
    // session was ever checked.
    private static IntPtr _publishedFor;

    private static void PublishPerBattle()
    {
        if (CoopNet.Mode != CoopMode.Host || !CoopNet.Connected) return;
        if (!SessionSync.PartnerBattleConfirmed) return;
        var tm = TacticalManager.Get();
        if (tm == null || tm.Pointer == _publishedFor) return;
        if (Build(tm) == null) return;                  // map not up yet
        // Same settle gate as the roster, so both manifests describe the board at
        // the same moment rather than one mid-spawn and one after.
        if (!BattleRoster.BoardSettled(tm)) return;
        _publishedFor = tm.Pointer;
        Publish(tm);
    }

    private static void TryCompare()
    {
        var tm = TacticalManager.Get();
        if (tm == null) return;
        if (tm.Pointer != _lastTm) { _lastTm = tm.Pointer; Mismatched = false; }

        string mine = Build(tm);
        if (string.IsNullOrEmpty(mine)) return;      // map not up yet

        _checkedThisBattle = true;
        string theirs = _pending;
        _pending = null;

        if (mine == theirs)
        {
            var d = mine.Substring(mine.IndexOf('#') + 1).Split(',');
            Plugin.Logger.LogInfo("[TERRAIN] Map fingerprints MATCH (" + d[0] + "x" + d[1] + ") -- both machines generated the same ground");
            return;
        }

        Mismatched = true;
        // Two different missions is not a terrain fault -- see BattleRoster.BattleId.
        if (BattleRoster.DifferentBattle(theirs, mine, out string ba, out string bb))
        {
            BattleRoster.NotSameBattle(ba, bb);
            return;
        }
        Report(theirs, mine);
    }

    private static void Report(string hostText, string mineText)
    {
        Plugin.Logger.LogError("[TERRAIN] ================= MAPS DIFFER =================");

        if (!TryDecode(hostText, out int hw, out int hh, out var hb) ||
            !TryDecode(mineText, out int mw, out int mh, out var mb))
        {
            Plugin.Logger.LogError("[TERRAIN] A fingerprint could not be decoded -- treat the maps as different.");
            Announce();
            return;
        }

        if (hw != mw || hh != mh)
        {
            Plugin.Logger.LogError("[TERRAIN] MAP SIZE DIFFERS: host " + hw + "x" + hh + ", you " + mw + "x" + mh + ".");
            Plugin.Logger.LogError("[TERRAIN] These are not the same map at all.");
            Announce();
            return;
        }

        int surf = 0, cover = 0, los = 0, structure = 0, feature = 0, total = 0;
        var samples = new List<string>();

        for (int x = 0; x < mw; x++)
        {
            for (int z = 0; z < mh; z++)
            {
                int i = (x * mh + z) * Stride;
                bool dS = hb[i] != mb[i];
                bool dC = hb[i + 1] != mb[i + 1];
                int hf = hb[i + 2], mf = mb[i + 2];
                if (!dS && !dC && hf == mf) continue;

                total++;
                if (dS) surf++;
                if (dC) cover++;
                if (((hf ^ mf) & 1) != 0) los++;
                if (((hf ^ mf) & 2) != 0) structure++;
                if (((hf ^ mf) & 4) != 0) feature++;

                if (samples.Count < 12)
                {
                    var what = new List<string>();
                    if (dS) what.Add("surface " + hb[i] + "->" + mb[i]);
                    if (dC) what.Add("cover " + hb[i + 1] + "->" + mb[i + 1]);
                    if (((hf ^ mf) & 1) != 0) what.Add("line-of-sight");
                    if (((hf ^ mf) & 2) != 0) what.Add("structure");
                    if (((hf ^ mf) & 4) != 0) what.Add("environment feature");
                    samples.Add("(" + x + "," + z + "): " + string.Join(", ", what));
                }
            }
        }

        int all = mw * mh;
        Plugin.Logger.LogError("[TERRAIN] " + total + " of " + all + " tiles differ (" +
            (all > 0 ? (total * 100.0 / all).ToString("F1") : "0") + "%).");
        Plugin.Logger.LogError("[TERRAIN]   surface=" + surf + " cover=" + cover + " lineOfSight=" + los +
            " structures=" + structure + " features=" + feature);
        Plugin.Logger.LogError("[TERRAIN] Cover and line-of-sight decide hit chances and what each player can see,");
        Plugin.Logger.LogError("[TERRAIN] so the two of you will disagree about shots that should have connected.");
        foreach (var s in samples) Plugin.Logger.LogError("[TERRAIN]   " + s);
        if (total > samples.Count) Plugin.Logger.LogError("[TERRAIN]   ... first " + samples.Count + " shown");
        Announce();
    }

    private static void Announce()
    {
        Plugin.Logger.LogError("[TERRAIN] Abandon this mission and relaunch it.");
        Plugin.Logger.LogError("[TERRAIN] ===============================================");
        CoopNet.Status = "MAPS DIFFER between the two machines";
        CoopRunner.ShowVersionWarning(
            "THE TWO PLAYERS ARE ON DIFFERENT MAPS -- cover and line of sight will disagree.");
    }

    private static bool TryDecode(string text, out int w, out int h, out byte[] bytes)
    {
        w = h = 0; bytes = null;
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            int __h = text.IndexOf('#');
            if (__h >= 0) text = text.Substring(__h + 1);
            int a = text.IndexOf(','); if (a < 0) return false;
            int b = text.IndexOf(',', a + 1); if (b < 0) return false;
            if (!int.TryParse(text.Substring(0, a), out w)) return false;
            if (!int.TryParse(text.Substring(a + 1, b - a - 1), out h)) return false;
            bytes = Convert.FromBase64String(text.Substring(b + 1));
            return bytes.Length >= w * h * Stride;
        }
        catch { return false; }
    }
}
