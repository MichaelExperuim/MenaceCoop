using System;
using System.Collections.Generic;
using HarmonyLib;
#if MELON
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.Tactical;
using Menace.Tactical.Skills;
#endif

namespace MenaceCoop;

// DAMAGE EFFECTS AND STATUS EFFECTS WERE NEVER SYNCED (player reports
// 2026-10-18 / 2026-10-24).
//
// Vehicle module damage -- a destroyed engine or weapon, a fire that burns the
// vehicle up next turn -- is a DEFECT: the engine rolls one when a hit crosses
// a strength threshold (Actor.ApplyRandomDamageEffect) and adds its
// DamageEffect skill to the unit. Acid from bugs and target markings are
// StatusEffect skills added the same way. Only the machine that decided the hit
// ever had them: the partner's copy of the hit is suppressed (HP comes from
// ehp), so its engine never rolled anything. The fire a guest's RPG started
// never burned on the host, and module damage on allied vehicles showed only
// on the host.
//
// Same rule as damage: the machine that decides the hit decides its effects.
//   decider -- every DamageEffect / StatusEffect skill its engine adds to a
//              unit is relayed ("efx", by template name).
//   partner -- never rolls a defect for a hit that is not its own (the roll is
//              random and would pick a different one); adds the relayed skill.
// Effects that expire run out on every machine by the same turn processing, so
// removals are not relayed.
internal static class EffectSync
{
    internal static bool Applying;
    // A unit's own template skills (spawn, deploy): every machine adds those.
    internal static int AddingInherent;

    private static bool IsEffect(BaseSkill s)
    {
        try
        {
            var t = s?.GetTemplate()?.Type;
            return t == SkillType.DamageEffect || t == SkillType.StatusEffect;
        }
        catch { return false; }
    }

    // Is THIS machine the one deciding what is happening right now?
    private static bool WeDecide()
        => (!Replication.ApplyingRemote && !Replication.InPartnersHit) || Replication.HostDecidesHitInReplay;

    // Does this add come from a cause ONLY this machine runs? Effects that
    // every machine's engine adds by itself (deploy, turn processing) need no
    // relay -- r1 2026-10-24: 'effect.out_of_the_craters' at deploy was relayed
    // by all three and deduped by all three. Only this machine runs: its own hit,
    // its own action in flight (it holds the action lock), and on the host, the
    // AI's turn.
    private static bool OnlyWeCause()
    {
        if (Replication.InOwnHit || Replication.HostDecidesHitInReplay) return true;
        if (Replication.ApplyingRemote || Replication.InPartnersHit) return false;
        if (Replication.LockHeldByMe) return true;
        try { return CoopNet.Mode == CoopMode.Host && !TacticalManager.Get().IsPlayerTurn(); } catch { return false; }
    }

    internal static void OnAdded(SkillContainer c, BaseSkill skill)
    {
        if (Applying || AddingInherent > 0 || !CoopNet.Connected || TacticalManager.Get() == null) return;
        if (!IsEffect(skill) || !OnlyWeCause()) return;
        try
        {
            Actor a = null;
            try { a = skill.GetActor(); } catch { }
            if (a == null) { try { a = c?.m_Owner?.TryCast<Actor>(); } catch { } }
            if (a == null) return;
            string id = skill.GetTemplate()?.name;
            if (string.IsNullOrEmpty(id)) return;
            var p = a.GetLastTile()?.GetTilePos();
            var msg = new CoopMsg { T = "efx", AX = p?.x ?? -1, AY = p?.y ?? -1, SkillId = id };
            Replication.StampIds(msg, a, isOrder: false);
            CoopNet.Send(msg);
            Plugin.Logger.LogInfo($"[EFX] '{id}' added to '{DesyncDetector.TemplateNameOf(a)}' here -> partner");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[EFX] relay failed: {e.Message}"); }
    }

    // A defect roll for a hit the partner decides: theirs arrives as "efx".
    internal static bool MayRollDefect(Actor victim)
    {
        if (!CoopNet.Connected || TacticalManager.Get() == null) return true;
        if (Replication.ShouldSwallowDamage(victim)) return false;
        return WeDecide();
    }

    internal static void HandleRemote(CoopMsg m)
    {
        var actor = Replication.ResolveOrderActor(m);
        if (actor == null)
        {
            Plugin.Logger.LogWarning($"[EFX] '{m.SkillId}' for a unit at ({m.AX},{m.AY}) that is not here -- skipped");
            return;
        }
        try
        {
            var skills = actor.GetSkills();
            if (skills == null) return;
            var have = skills.GetSkillByID(m.SkillId, null);
            if (have != null && !have.IsStacking())
            {
                Plugin.Logger.LogInfo($"[EFX] '{m.SkillId}' already on '{DesyncDetector.TemplateNameOf(actor)}' here");
                return;
            }
            var st = FindTemplate(m.SkillId);
            if (st == null)
            {
                Plugin.Logger.LogWarning($"[EFX] no skill template '{m.SkillId}' here -- cannot add it to '{DesyncDetector.TemplateNameOf(actor)}'");
                return;
            }
            // An ORIGIN, not null: CreateSkill(null) throws (DebugSync grantskill).
            var skill = st.CreateSkill(new Il2CppSystem.Nullable<Menace.Strategy.Origin>(new Menace.Strategy.Origin(st, -1, null, -1)));
            bool ok;
            bool prev = Replication.ApplyingRemote;
            Applying = true;
            Replication.ApplyingRemote = true;
            try { ok = skill != null && skills.Add(skill); }
            finally { Replication.ApplyingRemote = prev; Applying = false; }
            Plugin.Logger.LogInfo($"[EFX] '{m.SkillId}' added to '{DesyncDetector.TemplateNameOf(actor)}' (partner's){(ok ? "" : " -- REFUSED by the engine")}");
        }
        catch (Exception e)
        {
            Applying = false;
            Plugin.Logger.LogWarning($"[EFX] applying '{m.SkillId}' failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static readonly Dictionary<string, SkillTemplate> _templates = new();

    private static SkillTemplate FindTemplate(string name)
    {
        if (_templates.TryGetValue(name, out var hit) && hit != null) return hit;
        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 1) DebugSync.EnsureSkillTemplatesLoaded();
            try
            {
                foreach (var t in UnityEngine.Resources.FindObjectsOfTypeAll<SkillTemplate>())
                {
                    string n = null;
                    try { n = t?.name; } catch { }
                    if (!string.IsNullOrEmpty(n)) _templates[n] = t;
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[EFX] template scan failed: {e.Message}"); }
            if (_templates.TryGetValue(name, out hit) && hit != null) return hit;
        }
        return null;
    }
}

[HarmonyPatch]
internal static class EffectSyncPatches
{
    [HarmonyPatch(typeof(SkillContainer), nameof(SkillContainer.Add))]
    [HarmonyPostfix]
    private static void AfterAdd(SkillContainer __instance, BaseSkill _skill, bool __result)
    {
        if (__result) EffectSync.OnAdded(__instance, _skill);
    }

    [HarmonyPatch(typeof(SkillContainer), nameof(SkillContainer.AddInherentSkills))]
    [HarmonyPrefix]
    private static void BeforeInherent() => EffectSync.AddingInherent++;

    [HarmonyPatch(typeof(SkillContainer), nameof(SkillContainer.AddInherentSkills))]
    [HarmonyFinalizer]
    private static void AfterInherent() { if (EffectSync.AddingInherent > 0) EffectSync.AddingInherent--; }

    [HarmonyPatch(typeof(Actor), nameof(Actor.ApplyRandomDamageEffect), new[] { typeof(float), typeof(Entity), typeof(Skill) })]
    [HarmonyPrefix]
    private static bool BeforeDefectRoll(Actor __instance) => EffectSync.MayRollDefect(__instance);
}
