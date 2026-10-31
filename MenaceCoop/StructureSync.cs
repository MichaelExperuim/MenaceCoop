using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Objectives;

namespace MenaceCoop;

// STRUCTURE DESTRUCTION WAS NOT SYNCED (user, 2026-09-25).
//
// Structures had no relay at all ("Structures are not relayed ... both machines
// must keep simulating those themselves", Replication.ShouldSwallowDamage). Two
// machines rolling their own shots against the same wall destroy it at different
// times or not at all -- and a destroyed structure is cover, line of sight and
// passability, so it is a board divergence, plus the "do not destroy more than N
// structures" objectives count it.
//
// Same rule as units: whoever FIRED owns the outcome. A structure's destruction
// is decided on the machine that owns the attacker (a player unit's owner; the
// host for AI, hazards and anything unattributed). There it dies as normal and
// the tile is relayed ("sdie"); the other machine refuses its own local Die()
// for that structure and applies the relayed one instead. Damage short of
// destruction is still simulated on both sides -- only the kill is authoritative,
// which is what changes the board.
internal static class StructureSync
{
    // Last attacker per structure, from OnDamageReceived (Die has no attacker).
    private static readonly Dictionary<IntPtr, Entity> _lastAttacker = new();
    private static IntPtr _battle;

    internal static void NoteAttacker(Structure s, Entity attacker)
    {
        try
        {
            var tm = TacticalManager.Get();
            if (tm == null || s == null) return;
            if (_battle != tm.Pointer) { _battle = tm.Pointer; _lastAttacker.Clear(); _scored.Clear(); }
            _lastAttacker[s.Pointer] = attacker;
        }
        catch { }
    }

    internal static bool Authoritative(Structure s)
    {
        Entity attacker = null;
        if (s != null) _lastAttacker.TryGetValue(s.Pointer, out attacker);
        if (attacker == null) { try { attacker = s?.Killer; } catch { } }
        Actor a = null;
        try { a = attacker?.TryCast<Actor>(); } catch { }
        if (a != null && Ownership.Active && Ownership.IsKnownPlayerUnit(a)) return Ownership.IsLocal(a);
        return CoopNet.Mode == CoopMode.Host;
    }

    // By the structure list, not Tile.GetEntity(): run 23 found no structure
    // through tiles at all within 8 tiles of any unit.
    internal static Structure At(int x, int y)
    {
        try
        {
            var st = Replication.GetTileAt(x, y)?.GetEntity()?.TryCast<Structure>();
            if (st != null) return st;
        }
        catch { }
        try
        {
            var list = TacticalManager.Get()?.m_Structures;
            for (int i = 0; list != null && i < list.Count; i++)
            {
                var c = list[i];
                var p = c?.GetTile()?.GetTilePos();
                if (p != null && p.Value.x == x && p.Value.y == y) return c;
            }
        }
        catch { }
        return null;
    }

    internal static string Name(Structure s)
    {
        try { return s?.GetTemplate()?.name ?? "structure"; } catch { return "structure"; }
    }

    internal static string Where(Structure s)
    {
        try { var p = s.GetTile()?.GetTilePos(); return p == null ? "(?)" : $"({p.Value.x},{p.Value.y})"; } catch { return "(?)"; }
    }

    // AN APPLIED DESTRUCTION MUST STILL SCORE (BUGS #76, user 2026-10-08).
    // "Destroy N" objectives (eggs, pirate supplies, landing ships) count in
    // DestroyStructure.OnDeath(target, killer), which the engine reaches from
    // its own damage path -- not from a bare Die(). When the destruction we
    // apply came from the partner (a relayed "sdie", or a "sdiereq" because the
    // partner's copy of OUR shot brought it down first), the target vanished
    // here and was never counted: host's shot, ~1 in 3, mission stuck. So after
    // such a Die, any DestroyStructure that holds this target and did not move
    // its count is told about the death directly. One that did count is left
    // alone, so this can never count twice.
    // Every (objective, target) death DestroyStructure.OnDeath has been told of,
    // by the engine or by us. Pointers are per battle; cleared with _lastAttacker.
    private static readonly HashSet<(IntPtr, IntPtr)> _scored = new();
    internal static void NoteScored(DestroyStructure obj, Entity target)
    {
        try
        {
            var tm = TacticalManager.Get();
            if (tm == null || obj == null || target == null) return;
            if (_battle != tm.Pointer) { _battle = tm.Pointer; _lastAttacker.Clear(); _scored.Clear(); }
            _scored.Add((obj.Pointer, target.Pointer));
        }
        catch { }
    }

    private static List<(DestroyStructure obj, int before)> SnapshotObjectives(Structure s)
    {
        var list = new List<(DestroyStructure, int)>();
        try
        {
            var mgr = TacticalManager.Get()?.GetMission()?.Objectives;
            if (mgr == null || s == null) return list;
            int count = mgr.GetCount(new Il2CppSystem.Nullable<ObjectiveState>());
            for (int i = 0; i < count; i++)
            {
                var ds = mgr.Get(i)?.TryCast<DestroyStructure>();
                var targets = ds?.m_Targets;
                if (targets == null) continue;
                for (int t = 0; t < targets.Count; t++)
                    if (targets[t] != null && targets[t].Pointer == s.Pointer) { list.Add((ds, ds.m_TargetsDestroyed)); break; }
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[STRUCT] objective snapshot failed: {e.Message}"); }
        return list;
    }

    private static void ScoreIfMissed(Structure s, List<(DestroyStructure obj, int before)> snap)
    {
        if (snap.Count == 0) return;
        Entity killer = null;
        try { _lastAttacker.TryGetValue(s.Pointer, out killer); if (killer == null) killer = s.Killer; } catch { }
        foreach (var (obj, before) in snap)
        {
            try
            {
                // Already seen by the objective -- now, or earlier from our own
                // damage path while the Die itself was refused (the count moved
                // before this snapshot). Either way: counted, never twice.
                if (obj.m_TargetsDestroyed != before || _scored.Contains((obj.Pointer, s.Pointer)))
                {
                    Plugin.Logger.LogInfo($"[OBJ] '{Name(s)}' {Where(s)} counted by the engine ({before} -> {obj.m_TargetsDestroyed})");
                    continue;
                }
                obj.OnDeath(s, killer);
                Plugin.Logger.LogInfo($"[OBJ] '{Name(s)}' {Where(s)} destroyed via relay was NOT counted -- credited it ({before} -> {obj.m_TargetsDestroyed}, killer {(killer == null ? "none" : "known")})");
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[OBJ] crediting '{Name(s)}' failed: {e.Message}"); }
        }
    }

    internal static void HandleRemote(CoopMsg m)
    {
        try
        {
            var s = At(m.AX, m.AY);
            if (s == null)
            {
                Plugin.Logger.LogInfo($"[STRUCT] partner destroyed the structure at ({m.AX},{m.AY}); none standing here (already gone)");
                return;
            }
            var snap = SnapshotObjectives(s);
            // Die() also runs the engine's collapse damage on the garrison --
            // see SwallowCollapseHit for who keeps it.
            bool prev = Replication.ApplyingRemote;
            Replication.ApplyingRemote = true;
            ApplyingRelay = true;
            try { s.Die(false); }
            finally { Replication.ApplyingRemote = prev; ApplyingRelay = false; }
            ScoreIfMissed(s, snap);
            Plugin.Logger.LogInfo($"[STRUCT] '{StructureSync.Name(s)}' at ({m.AX},{m.AY}) destroyed (partner's shot)");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[STRUCT] apply failed: {e.Message}"); }
    }

    // Set ONLY while applying a relayed "sdie". The Die prefix used to bypass on
    // the global Replication.ApplyingRemote, so a structure whose destruction
    // happened to land inside any other remote apply skipped the ownership
    // check AND the relay.
    internal static bool ApplyingRelay;
    internal static bool ForceAuthority;

    // A RELAYED COLLAPSE MUST NOT HIT THE GARRISON A SECOND TIME ("double tap",
    // player report 2026-10-23: a squad in a tower the enemy destroyed died at
    // once and left no bodies). Structure.Die() runs the engine's own collapse
    // damage on the units inside. The machine that owned the shot already dealt
    // that damage and relayed it (ehp/edie), so applying it again while the
    // relayed Die runs killed the squad twice over, still contained.
    //
    // One exception: damage to a PLAYER unit is always the host's to decide (the
    // guest never originates it), so when the guest's shot brought the building
    // down, the host keeps its own copy of the collapse damage and relays it.
    internal static bool HostOwnsCollapseHit(Actor victim)
        => ApplyingRelay && victim != null && CoopNet.Mode == CoopMode.Host
           && Ownership.Active && Ownership.IsKnownPlayerUnit(victim);

    internal static bool SwallowCollapseHit(Actor victim)
        => ApplyingRelay && victim != null && !HostOwnsCollapseHit(victim);

    // STRUCTURE HP DRIFTS. Damage short of destruction is simulated on both
    // machines, so the same shot can bring a structure down on one side and not
    // the other. Under "the shooter's machine decides" alone, a kill that only
    // the NON-owner saw was refused there and never came from the owner: the
    // structure stood on one screen and was gone on the other (hand test
    // 2026-10-02 18:03:47, guest's rocket organ vs the shelter at (26,26): host
    // "would die here ... waiting for theirs", guest never destroyed it). The
    // non-owner now asks; the owner destroys its copy and relays as usual, so
    // both agree on destroyed. Erring towards destroyed is deliberate: it is
    // what at least one honest simulation of the shot produced.
    internal static void RequestDie(Structure s)
    {
        try
        {
            var p = s?.GetTile()?.GetTilePos();
            if (p == null) return;
            CoopNet.Send(new CoopMsg { T = "sdiereq", AX = p.Value.x, AY = p.Value.y });
        }
        catch { }
    }

    internal static void HandleDieRequest(CoopMsg m)
    {
        try
        {
            var s = At(m.AX, m.AY);
            if (s == null) return;                       // already gone here: our sdie is on its way / was sent
            Plugin.Logger.LogInfo($"[STRUCT] '{Name(s)}' at ({m.AX},{m.AY}) came down in the partner's simulation of our shot -- destroying it here too");
            var snap = SnapshotObjectives(s);
            ForceAuthority = true;                       // never bounce the request back (both sides could each think the other fired)
            try { s.Die(false); }                        // goes through the Die patch and relays "sdie"
            finally { ForceAuthority = false; }
            ScoreIfMissed(s, snap);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[STRUCT] die request failed: {e.Message}"); }
    }
}

[HarmonyPatch]
internal static class StructureSyncPatches
{
    [HarmonyPatch(typeof(Structure), nameof(Structure.OnDamageReceived))]
    [HarmonyPrefix]
    private static void Hit(Structure __instance, Entity _attacker) => StructureSync.NoteAttacker(__instance, _attacker);

    // Recorded only when the call actually COUNTED, so a death the objective was
    // told of but rejected (e.g. no killer) can still be credited later.
    [HarmonyPatch(typeof(DestroyStructure), nameof(DestroyStructure.OnDeath))]
    [HarmonyPrefix]
    private static void BeforeScore(DestroyStructure __instance, out int __state)
    {
        __state = int.MinValue;
        try { __state = __instance.m_TargetsDestroyed; } catch { }
    }

    [HarmonyPatch(typeof(DestroyStructure), nameof(DestroyStructure.OnDeath))]
    [HarmonyPostfix]
    private static void Scored(DestroyStructure __instance, Entity _target, int __state)
    {
        try { if (__state != int.MinValue && __instance.m_TargetsDestroyed != __state) StructureSync.NoteScored(__instance, _target); } catch { }
    }

    // Refuse a local destruction the partner owns; the relayed one will arrive.
    [HarmonyPatch(typeof(Structure), nameof(Structure.Die))]
    [HarmonyPrefix]
    private static bool BeforeDie(Structure __instance, out bool __state)
    {
        __state = false;
        if (StructureSync.ApplyingRelay || TacticalManager.Get() == null) return true;
        // Host alone: it is the authority on everything, and the kill is journaled (#57).
        if (!CoopNet.Connected) { __state = CoopNet.Hosting; return true; }
        if (StructureSync.ForceAuthority || StructureSync.Authoritative(__instance)) { __state = true; return true; }
        Plugin.Logger.LogInfo($"[STRUCT] '{StructureSync.Name(__instance)}' at {StructureSync.Where(__instance)} would die here, but the partner owns that shot -- asked them to destroy it");
        StructureSync.RequestDie(__instance);
        return false;
    }

    [HarmonyPatch(typeof(Structure), nameof(Structure.Die))]
    [HarmonyPostfix]
    private static void AfterDie(Structure __instance, bool __state)
    {
        if (!__state) return;
        try
        {
            var p = __instance.GetTile()?.GetTilePos();
            if (p == null) return;
            CoopNet.Send(new CoopMsg { T = "sdie", AX = p.Value.x, AY = p.Value.y });
            Plugin.Logger.LogInfo($"[STRUCT] '{StructureSync.Name(__instance)}' at ({p.Value.x},{p.Value.y}) destroyed here -- relayed");
        }
        catch { }
    }
}
