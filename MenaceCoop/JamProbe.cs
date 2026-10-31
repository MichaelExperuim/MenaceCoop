using HarmonyLib;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;
using Il2CppMenace.Tactical.Skills.Effects;
using Il2CppMenace.Tactical.Barks;

namespace MenaceCoop;

// User 2026-10-04 (r132): "a jammed weapon didnt go through". Co-op has never
// logged a jam. The jam roll happens INSIDE Skill.Use (JammingHandler.OnBeforeUse),
// after the shot was already relayed by the Skill.Use prefix, and clients replay
// with IgnoreUsabilityCheck -- suspected to skip the roll. Prove it before
// changing how shots are sent: one [JAM] line per roll and per element misfire,
// with the machine and whether it is a relayed replay.
[HarmonyPatch]
internal static class JamProbe
{
    [HarmonyPatch(typeof(JammingHandler), nameof(JammingHandler.OnBeforeUse))]
    [HarmonyPostfix]
    private static void Rolled(JammingHandler __instance, Actor _user, bool __result)
    {
        if (!BugProbes.On || !CoopNet.Connected) return;
        try
        {
            bool jammed = false; int chance = -1, uses = -1;
            try { jammed = __instance.IsJammed(); } catch { }
            try { chance = __instance.m_JamChance; } catch { }
            try { uses = __instance.GetUses(); } catch { }
            Plugin.Logger.LogInfo($"[JAM] roll '{DesyncDetector.TemplateNameOf(_user)}' result={__result} jammed={jammed} "
                + $"chance={chance} uses={uses} remote={Replication.ApplyingRemote} mode={CoopNet.Mode}");
        }
        catch { }
    }

    [HarmonyPatch(typeof(TacticalBarksManager), nameof(TacticalBarksManager.OnElementMalfunction))]
    [HarmonyPrefix]
    private static void Misfired(Element _element, Skill _skill)
    {
        if (!BugProbes.On || !CoopNet.Connected) return;
        try
        {
            Plugin.Logger.LogInfo($"[JAM] element MALFUNCTION skill={_skill?.GetID() ?? "?"} "
                + $"remote={Replication.ApplyingRemote} mode={CoopNet.Mode}");
        }
        catch { }
    }
}
