using System.Collections.Generic;
using System.Text;
#if MELON
using Il2CppMenace.Tactical;
using Menace = Il2CppMenace;
#else
using Menace.Tactical;
#endif

namespace MenaceCoop;

// #64 (r135): clients' engines DETECT enemies the host's do not -- re-detected
// every frame, never the other way round -- with every unit on the same tile.
// So one vision INPUT differs. Log them all on every machine at the same moments
// (each PhantomWatch publish, board still), only when changed, so a host/client
// diff names it: per non-player actor its concealment, whether this machine
// detects it, and which player units have line of sight to it, with their
// vision and detection.
internal static class FogInputProbe
{
    private static string _last;

    internal static void Sample(TacticalManager tm)
    {
        if (!BugProbes.On || tm == null) return;
        try
        {
            int pf = -1;
            try { pf = tm.GetPlayerFaction()?.GetIndex() ?? -1; } catch { }
            var actors = DesyncDetector.AllActors(tm);
            var mine = new List<Actor>();
            foreach (var a in actors)
                if (a != null && (a.GetFactionID() == pf || Ownership.IsKnownPlayerUnit(a)) && a.GetLastTile() != null) mine.Add(a);

            var sb = new StringBuilder();
            foreach (var u in mine)
            {
                var p = u.GetCurrentProperties();
                var t = u.GetLastTile().GetTilePos();
                sb.Append($"P {Short(u)}@({t.x},{t.y}) v={p?.GetVision()} d={p?.GetDetection()}; ");
            }
            foreach (var e in actors)
            {
                if (e == null || e.GetFactionID() == pf || Ownership.IsKnownPlayerUnit(e)) continue;
                var et = e.GetLastTile();
                if (et == null || e.IsDying()) continue;
                var ep = et.GetTilePos();
                int conc = -1, tconc = -1;
                try { conc = e.GetCurrentProperties()?.GetConcealment() ?? -1; } catch { }
                try { tconc = e.GetTileConcealment(); } catch { }
                bool det = false, vis = false;
                try { det = e.IsDetectedByFaction(pf); } catch { }
                try { vis = e.VisibilityToPlayer == Menace.Tactical.Visibility.Visible; } catch { }
                var los = new StringBuilder();
                foreach (var u in mine)
                {
                    try
                    {
                        int dist = u.GetDistanceTo(et);
                        int vr = u.GetCurrentProperties()?.GetVision() ?? 0;
                        if (dist > vr + 2) continue;
                        if (u.HasLineOfSightTo(e, u.GetLastTile(), et)) los.Append($"{Short(u)}:{dist} ");
                    }
                    catch { }
                }
                if (!det && !vis && los.Length == 0) continue;   // nobody near it: not a fog-edge question
                sb.Append($"E {Short(e)}@({ep.x},{ep.y}) c={conc} tc={tconc} det={(det ? 1 : 0)} vis={(vis ? 1 : 0)} los=[{los.ToString().TrimEnd()}]; ");
            }
            string s = sb.ToString();
            if (s == _last) return;
            _last = s;
            Plugin.Logger.LogInfo($"[VISIN] r{tm.GetRound()} {s}");
        }
        catch (System.Exception ex) { Plugin.Logger.LogWarning($"[VISIN] sample failed: {ex.Message}"); }
    }

    private static string Short(Actor a)
    {
        var n = DesyncDetector.TemplateNameOf(a);
        int dot = n.LastIndexOf('.');
        return dot >= 0 ? n.Substring(dot + 1) : n;
    }
}
