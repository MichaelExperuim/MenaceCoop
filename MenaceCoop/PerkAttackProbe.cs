using System;
using HarmonyLib;
#if MELON
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;
using Il2CppMenace.Tactical.Skills.Effects;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.Tactical;
using Menace.Tactical.Skills;
using Menace.Tactical.Skills.Effects;
#endif

namespace MenaceCoop;

// BUGS.md #90 PROBE: perk-triggered attacks (counterattack, "Overtime", an MG
// suppression burst) play no animation on the other players' screens.
//
// The engine's handlers have no bodies in the decompile, so which call a perk
// attack goes through -- and whether our relay ever sees it -- has to be
// watched. Logs, on every machine:
//   - every Skill.Use that runs INSIDE another Skill.Use (a reaction), and
//     every non-Default use by a player unit (perk uses carry Free etc.),
//     with the outer use, owner, and the relay flags at that moment;
//   - CounterAttackHandler.OnBeingTargetedByAttack and AttackProcHandler.OnTargetHit.
// Gated on Dev/ProbeBugs like BugProbes; own class so a moved target disables
// only this probe.
[HarmonyPatch]
internal static class PerkAttackProbe
{
    private static int _depth, _depthFrame = -1;
    private static string _outer;

    private static string Who(Actor a)
    {
        if (a == null) return "(none)";
        string owner = !Ownership.Active || !Ownership.IsKnownPlayerUnit(a) ? "ai"
                     : Ownership.IsLocal(a) ? "mine" : "partner";
        string at = "";
        try { var p = a.GetLastTile()?.GetTilePos(); if (p != null) at = $"@({p.Value.x},{p.Value.y})"; } catch { }
        return $"'{DesyncDetector.TemplateNameOf(a)}'{at}/{owner}";
    }

    private static string Tag => CoopNet.Mode == CoopMode.Host ? "H" : "G";

    [HarmonyPatch(typeof(Skill), nameof(Skill.Use))]
    [HarmonyPrefix]
    private static void BeforeUse(Skill __instance, Tile _targetTile, UsageParameter _parameters)
    {
        if (!BugProbes.On || !CoopNet.Connected) return;
        if (((int)_parameters & (int)UsageParameter.Fake) != 0) return;   // UI preview
        int frame = UnityEngine.Time.frameCount;
        if (frame != _depthFrame) { _depth = 0; _depthFrame = frame; _outer = null; }
        try
        {
            var actor = __instance?.GetActor();
            string id = __instance?.GetID() ?? "?";
            bool playerNonDefault = actor != null && Ownership.Active && Ownership.IsKnownPlayerUnit(actor)
                                    && _parameters != UsageParameter.Default;
            if (_depth > 0 || playerNonDefault)
            {
                string tgt = "";
                try { var t = _targetTile?.GetTilePos(); if (t != null) tgt = $"({t.Value.x},{t.Value.y})"; } catch { }
                Plugin.Logger.LogWarning($"[PERKPROBE:{Tag}] use '{id}' by {Who(actor)} -> {tgt} params={_parameters} depth={_depth} "
                    + $"outer={_outer ?? "-"} applyingRemote={Replication.ApplyingRemote} suppress={Replication.SuppressElementDamage} "
                    + $"mayBroadcast={Replication.MayBroadcastFor(actor)}");
            }
            if (_depth == 0) _outer = $"'{id}' by {Who(actor)}";
        }
        catch { }
        _depth++;
    }

    [HarmonyPatch(typeof(Skill), nameof(Skill.Use))]
    [HarmonyPostfix]
    private static void AfterUse(Skill __instance, UsageParameter _parameters, bool __runOriginal, bool __result)
    {
        if (!BugProbes.On || !CoopNet.Connected) return;
        if (((int)_parameters & (int)UsageParameter.Fake) != 0) return;
        if (_depth > 0) _depth--;
        // Did a perk/engine reaction actually execute here, or did a prefix stop it?
        if (Replication.IsEngineReaction(_parameters))
        {
            string who = "?";
            try { who = Who(__instance.GetActor()); } catch { }
            Plugin.Logger.LogWarning($"[PERKPROBE:{Tag}] reaction '{__instance?.GetID()}' by {who} "
                + (__runOriginal ? $"RAN (result={__result})" : "BLOCKED by a prefix"));
        }
    }

    [HarmonyPatch(typeof(CounterAttackHandler), nameof(CounterAttackHandler.OnBeingTargetedByAttack))]
    [HarmonyPrefix]
    private static void CounterTargeted(CounterAttackHandler __instance, Entity _attacker, Skill _skill)
    {
        if (!BugProbes.On || !CoopNet.Connected) return;
        try
        {
            Actor attacker = null;
            try { attacker = _attacker?.TryCast<Actor>(); } catch { }
            Plugin.Logger.LogWarning($"[PERKPROBE:{Tag}] COUNTER trigger on {Who(__instance.GetActor())} by {Who(attacker)} "
                + $"skill='{_skill?.GetID()}' applyingRemote={Replication.ApplyingRemote} suppress={Replication.SuppressElementDamage}");
        }
        catch { }
    }

    [HarmonyPatch(typeof(AttackProcHandler), nameof(AttackProcHandler.OnTargetHit))]
    [HarmonyPrefix]
    private static void ProcHit(AttackProcHandler __instance, Skill _skill, Entity _targetEntity)
    {
        if (!BugProbes.On || !CoopNet.Connected) return;
        try
        {
            Actor target = null;
            try { target = _targetEntity?.TryCast<Actor>(); } catch { }
            Plugin.Logger.LogWarning($"[PERKPROBE:{Tag}] PROC on-hit for {Who(__instance.GetActor())} via '{_skill?.GetID()}' target {Who(target)} "
                + $"applyingRemote={Replication.ApplyingRemote} suppress={Replication.SuppressElementDamage}");
        }
        catch { }
    }
}
