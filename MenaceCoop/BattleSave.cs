using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
#if MELON
using Il2CppMenace.States;
using Il2CppMenace.Tactical;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.States;
using Menace.Tactical;
#endif

namespace MenaceCoop;

// A BATTLE THAT CAN BE SAVED AND RESUMED (BUGS.md #2, user request 2026-09-25).
//
// The game's save holds the strategy layer only; no tactical type is in it. But
// GuestReload already rebuilds a battle from exactly what the mod holds: the
// battle-start autosave, the mission/seed/deployment, the state journal, the
// round and a board snapshot. So a battle save is those, on disk:
//
//   coop_battle.save        a COPY of the battle-start autosave (the game's
//                           autosave ring evicts the original)
//   coop_battle.coopbattle  mission, seed, deployment, ownership, round,
//                           faction, snapshot, one journal message per line
//
// Resuming is the reload run on the HOST: load that save, relaunch the mission
// with the same seed and deployment, catch up locally (rounds, journal, hard
// snapshot), then reload the guest into it with the ordinary GuestReload.
internal static class BattleSave
{
    private const string SaveName = "coop_battle.save";
    private const string DataName = "coop_battle.coopbattle";

    private static string DataPath => SaveTransfer.PathFor(DataName);
    private static string SavePath => SaveTransfer.PathFor(SaveName);

    internal static bool Exists
    {
        get { try { return File.Exists(DataPath) && File.Exists(SavePath); } catch { return false; } }
    }

    internal static string Label
    {
        get
        {
            try
            {
                if (!Exists) return "none";
                foreach (var l in File.ReadLines(DataPath))
                    if (l.StartsWith("round=")) return $"round {l.Substring(6)}, {File.GetLastWriteTime(DataPath):HH:mm}";
            }
            catch { }
            return "?";
        }
    }

    // ---- save --------------------------------------------------------------
    internal static bool Save(out string why)
    {
        why = null;
        var tm = TacticalManager.Get();
        if (CoopNet.Mode == CoopMode.Guest) { why = "only the host saves the battle"; return false; }
        if (tm == null) { why = "not in a battle"; return false; }
        if (!GuestReload.TryGetResumeData(tm, out var d)) { why = "no battle-start save recorded for this battle"; return false; }
        try
        {
            File.Copy(SaveTransfer.PathFor(d.SaveName), SavePath, true);
            string jpg = System.IO.Path.ChangeExtension(SaveTransfer.PathFor(d.SaveName), ".jpg");
            if (File.Exists(jpg)) File.Copy(jpg, System.IO.Path.ChangeExtension(SavePath, ".jpg"), true);
            var lines = new List<string>
            {
                "v=1",
                $"round={d.Round}", $"faction={d.Faction}", $"idx={d.MissionIdx}", $"seed={d.Seed}",
                $"deploy={d.Deploy}", $"slots={d.GuestSlots ?? ""}", $"snap={d.Snapshot}",
            };
            foreach (var j in d.Journal) lines.Add("j=" + JsonSerializer.Serialize(j));
            File.WriteAllLines(DataPath, lines);
            Plugin.Logger.LogWarning($"[BATTLESAVE] Saved round {d.Round}: mission idx={d.MissionIdx}, {d.Journal.Count} journal entr{(d.Journal.Count == 1 ? "y" : "ies")}, snapshot {d.Snapshot.Length} chars");
            return true;
        }
        catch (Exception e) { why = e.Message; Plugin.Logger.LogError($"[BATTLESAVE] Save failed: {e}"); return false; }
    }

    // ---- resume ------------------------------------------------------------
    internal sealed class Data
    {
        public string SaveName;
        public int Round, Faction, MissionIdx, Seed;
        public string Deploy = "", GuestSlots, Snapshot = "";
        public List<CoopMsg> Journal = new();
    }

    private static Data _resume;
    internal static bool Restoring => _resume != null;
    private static float _resumeSince;
    private static IntPtr _battleBefore;
    private static float _battleSeenAt = -1f;
    private const float ResumeFailsafe = 180f;

    internal static bool Resume(out string why)
    {
        why = null;
        if (CoopNet.Mode == CoopMode.Guest && CoopNet.Connected) { why = "only the host resumes a battle"; return false; }
        if (Restoring) { why = "already resuming"; return false; }
        if (!Exists) { why = "no saved battle"; return false; }
        var d = new Data { SaveName = SaveName };
        try
        {
            foreach (var l in File.ReadLines(DataPath))
            {
                int eq = l.IndexOf('=');
                if (eq < 0) continue;
                string k = l.Substring(0, eq), v = l.Substring(eq + 1);
                switch (k)
                {
                    case "round": d.Round = int.Parse(v); break;
                    case "faction": d.Faction = int.Parse(v); break;
                    case "idx": d.MissionIdx = int.Parse(v); break;
                    case "seed": d.Seed = int.Parse(v); break;
                    case "deploy": d.Deploy = v; break;
                    case "slots": d.GuestSlots = v.Length > 0 ? v : null; break;
                    case "snap": d.Snapshot = v; break;
                    case "j": d.Journal.Add(JsonSerializer.Deserialize<CoopMsg>(v)); break;
                }
            }
        }
        catch (Exception e) { why = "the battle save could not be read: " + e.Message; return false; }

        if (!SessionSync.HostRelaunch(SaveName, d.MissionIdx, d.Seed, d.Deploy, out why)) return false;
        _resume = d;
        _resumeSince = UnityEngine.Time.realtimeSinceStartup;
        _battleBefore = TacticalManager.Get()?.Pointer ?? IntPtr.Zero;
        _battleSeenAt = -1f;
        if (d.GuestSlots != null) Ownership.PrepGuestSlots = ParseSlots(d.GuestSlots);
        CoopRunner.SetSyncBlocker(true, "RESUMING THE SAVED BATTLE -- PLEASE WAIT");
        Plugin.Logger.LogWarning($"[BATTLESAVE] Resuming the battle saved at round {d.Round} (mission idx={d.MissionIdx})");
        return true;
    }

    private static HashSet<int> ParseSlots(string s)
    {
        var set = new HashSet<int>();
        foreach (var p in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(p, out int i)) set.Add(i);
        return set;
    }

    // CoopRunner tick: once the relaunched battle is up and settled, catch up.
    internal static void Tick()
    {
        if (_resume == null) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now - _resumeSince > ResumeFailsafe)
        {
            Plugin.Logger.LogError("[BATTLESAVE] Resume abandoned: the battle never came back up");
            End(false);
            return;
        }
        if (GuestReload.LocalCatchingUp) return;
        var tm = TacticalManager.Get();
        if (tm == null || tm.Pointer == _battleBefore) return;
        int round = 0;
        try { round = tm.GetRound(); } catch { }
        if (round < 1 || !Replication.BoardSettled() || TacticalManager.IsAnyActorMoving()) { _battleSeenAt = -1f; return; }
        if (_battleSeenAt < 0f) { _battleSeenAt = now; return; }
        if (now - _battleSeenAt < 4f) return;
        var d = _resume;
        _battleBefore = tm.Pointer;   // start once
        GuestReload.StartLocal(d.Round, d.Faction, d.Snapshot, d.Journal);
    }

    // GuestReload's local catch-up finished.
    internal static void OnLocalCatchUpDone(bool ok)
    {
        if (_resume == null) return;
        End(ok);
        if (ok && CoopNet.Connected) GuestReload.Start("battle resumed from a save");
    }

    private static void End(bool ok)
    {
        _resume = null;
        CoopRunner.SetSyncBlocker(false);
        CoopRunner.ShowDesyncWarning(ok ? "Saved battle resumed" : "Could not resume the saved battle -- see the log");
        Plugin.Logger.LogWarning($"[BATTLESAVE] Resume {(ok ? "complete" : "FAILED")}");
    }
}
