using System;
using HarmonyLib;
using Il2CppTactical.AI;

namespace MenaceCoop;

// ENEMY OFF-MAP ABILITIES ARE THE HOST'S (game v0.7.15).
//
// v0.7.15 gave the AI factions off-map abilities (rocket strikes, turret drops)
// through enemy activities. The AI schedules one from AIFaction.OnTurnStart ->
// AIOffmapAbilities.ScheduleOffmapAbilityUse, and the guest skips AIFaction
// turn starts (the host runs the AI), so without this the strike was scheduled
// on the host only: no warning zone on the guest, damage arriving from nowhere,
// and a summoned turret standing on one board.
//
// The host relays each schedule ("aioci": faction, ability slot, target tile,
// seed) and the guest runs the same call. From there DelayedOffmapSync takes
// over exactly as for a ship ability: the host's "ocisched" requirement is
// adopted by list index, and the host's ticks advance and fire it on both
// machines. The guest never schedules one of its own.
internal static class EnemyOffmapSync
{
    internal static bool Applying;
    private static int _seed;

    internal static bool BeforeSchedule(AIOffmapAbilities ai, EnemyOffmapAbility ability, Il2CppMenace.Tactical.Tile tile)
    {
        if (!CoopNet.Connected) return true;
        if (CoopNet.Mode == CoopMode.Guest)
        {
            if (Applying) return true;
            Plugin.Logger.LogWarning($"[AIOCI] Held a local enemy off-map schedule '{TemplateOf(ability)}' -- the host's is relayed");
            return false;
        }
        _seed = Replication.NewSeed();
        Replication.Reseed(_seed);
        return true;
    }

    internal static void AfterSchedule(AIOffmapAbilities ai, EnemyOffmapAbility ability, Il2CppMenace.Tactical.Tile tile)
    {
        if (!CoopNet.Connected || CoopNet.Mode != CoopMode.Host) return;
        try
        {
            var list = ai?.m_AvailableOffmapAbilities;
            int slot = -1;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && list[i].Pointer == ability.Pointer) { slot = i; break; }
            var p = tile?.GetTilePos();
            int fac = ai?.m_Faction?.GetIndex() ?? -1;
            CoopNet.Send(new CoopMsg
            {
                T = "aioci", Seed = _seed, Prm = slot, MIdx = fac,
                SkillId = TemplateOf(ability), TX = p?.x ?? -1, TY = p?.y ?? -1,
            });
            Plugin.Logger.LogInfo($"[AIOCI] Enemy faction {fac} scheduled '{TemplateOf(ability)}' (slot {slot}) at ({p?.x},{p?.y}) -> partner");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[AIOCI] Relaying an enemy off-map schedule failed: {e.Message}"); }
    }

    internal static void HandleRemote(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        try
        {
            var tm = Il2CppMenace.Tactical.TacticalManager.Get();
            var ai = tm?.GetFaction(m.MIdx)?.TryCast<Il2CppMenace.Tactical.AI.AIFaction>()?.GetOffmapAbilities();
            var list = ai?.m_AvailableOffmapAbilities;
            EnemyOffmapAbility ability = null;
            if (list != null)
            {
                if (m.Prm >= 0 && m.Prm < list.Count && TemplateOf(list[m.Prm]) == m.SkillId) ability = list[m.Prm];
                else
                    for (int i = 0; i < list.Count && ability == null; i++)
                        if (TemplateOf(list[i]) == m.SkillId) ability = list[i];
            }
            var tile = Replication.GetTileAt(m.TX, m.TY);
            if (ability == null || tile == null)
            {
                Plugin.Logger.LogWarning($"[AIOCI] Host's enemy '{m.SkillId}' (faction {m.MIdx}, slot {m.Prm}) at ({m.TX},{m.TY}) cannot be replayed here (ability={(ability != null)} tile={(tile != null)} abilities={list?.Count ?? -1})");
                return;
            }
            Applying = true;
            bool prev = Replication.ApplyingRemote;
            Replication.ApplyingRemote = true;
            try { Replication.Reseed(m.Seed); ai.ScheduleOffmapAbilityUse(ability, tile); }
            finally { Applying = false; Replication.ApplyingRemote = prev; }
            Plugin.Logger.LogInfo($"[AIOCI] Replayed host's enemy '{m.SkillId}' (faction {m.MIdx}) at ({m.TX},{m.TY}) | {DelayedOffmapSync.Describe()}");
        }
        catch (Exception e)
        {
            Applying = false;
            Plugin.Logger.LogWarning($"[AIOCI] Replaying the host's enemy '{m.SkillId}' threw: {e.GetType().Name}: {e.Message}");
        }
    }

    private static string TemplateOf(EnemyOffmapAbility a)
    {
        try { return a?.OffmapAbility?.TemplateId ?? "?"; } catch { return "?"; }
    }
}

[HarmonyPatch]
internal static class EnemyOffmapPatches
{
    [HarmonyPatch(typeof(AIOffmapAbilities), nameof(AIOffmapAbilities.ScheduleOffmapAbilityUse))]
    [HarmonyPrefix]
    private static bool Before(AIOffmapAbilities __instance, EnemyOffmapAbility _ability, Il2CppMenace.Tactical.Tile _tile)
        => EnemyOffmapSync.BeforeSchedule(__instance, _ability, _tile);

    [HarmonyPatch(typeof(AIOffmapAbilities), nameof(AIOffmapAbilities.ScheduleOffmapAbilityUse))]
    [HarmonyPostfix]
    private static void After(AIOffmapAbilities __instance, EnemyOffmapAbility _ability, Il2CppMenace.Tactical.Tile _tile)
        => EnemyOffmapSync.AfterSchedule(__instance, _ability, _tile);
}
