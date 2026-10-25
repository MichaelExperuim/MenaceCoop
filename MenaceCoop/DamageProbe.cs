using System;
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

// WHERE DOES DAMAGE ACTUALLY GO?
//
// The mod has always relayed element damage through Element.SetHitpoints,
// broadcasting "ehp"/"edie" packets so the two machines could correct each
// other. Across every harness run that channel logs ZERO lines on both
// machines: the patch binds, the signature still matches the shipped build, and
// the method is simply never called. It has been vestigial the whole time.
//
// That matters because combat is now the ONLY place the two machines part.
// Generation is provably identical -- terrain fingerprints match, enemy
// manifests match (14 units), and the reinforcement draw agrees exactly for
// rounds 1 and 2. From round 3 the host's enemy strength falls while the
// guest's does not:
//
//     round 3   host 1025   guest 1105
//     round 4   host  730   guest 1285
//
// The host is killing things the guest never registers, and every remaining
// symptom -- diverging waves, drifting rosters, mismatched objectives -- follows
// from that one fact.
//
// THIS FILE MEASURES, IT DOES NOT FIX. Three theories about this path have
// already been wrong, so before relaying anything the question is whether the
// two machines even see the same damage events. Actor.OnDamageReceived and
// Actor.OnElementDeath are the engine's real entry points; logging both sides
// and diffing them answers it. Diagnostic only, gated on its own TraceDamage
// switch (off by default) -- it used to ride SelfTestTrace, which ships ON, and
// logged every hit on both machines for every player.
internal static class DamageProbe
{
    private static bool On => Plugin.TraceDamage?.Value ?? false;
    private static string Side => CoopNet.Mode == CoopMode.Host ? "H" : "G";

    internal static void NoteDamage(Actor victim, Entity attacker, Skill skill, int amount)
    {
        if (!On || !CoopNet.Connected) return;
        try
        {
            Plugin.Logger.LogInfo($"[DMGPROBE:{Side}] hit {Id(victim)} by '{SkillId(skill)}' amount={amount} "
                + $"remote={Replication.ApplyingRemote}");
        }
        catch { }
    }

    internal static void NoteDeath(Actor victim, Element element, Skill skill, int applied)
    {
        if (!CoopNet.Connected) return;
        int sq = -1;
        try { sq = element?.GetSquaddieId() ?? -1; } catch { }

        if (On)
        {
            try
            {
                Plugin.Logger.LogInfo($"[DMGPROBE:{Side}] DEATH {Id(victim)} sq{sq} by '{SkillId(skill)}' "
                    + $"applied={applied} remote={Replication.ApplyingRemote}");
            }
            catch { }
        }

        // RelayDeath is withdrawn -- see RelayDeathWithdrawn below.
    }

    // WITHDRAWN -- UNREFERENCED. Kept for the reasoning, not called.
    //
    // The "edie" packet had a complete receive path that had never received
    // anything, because the SENDER was bolted to Element.SetHitpoints, a method
    // this build never calls. Connecting it to Actor.OnElementDeath made the
    // channel live for the first time.
    //
    // This was built to fix the guest's enemy strength staying high while the
    // host's fell. That turned out to be a different bug entirely: the wave draw
    // read _concurrentEnemyStrength from each machine's own lagging board, so the
    // two budgeted differently no matter how promptly deaths were replicated.
    // Once the budget was made host-authoritative, drift went to zero.
    //
    // Relaying deaths was never demonstrated to be necessary, and it was
    // demonstrably harmful: the guest applied the host's death and then its own
    // simulation killed the same element again, logging the same squaddie dying
    // three times and leaving the roster at "host 4, you 3" -- one unit SHORT of
    // the host, the opposite of the problem it was meant to solve. Tightening the
    // guard to IsAlive() stopped the duplicates but did not make the relay useful.
    //
    // The probe above still runs, because knowing that the two machines resolve
    // the same squaddie's death with applied=2 against applied=4 is worth having
    // on record. What follows from it is that damage TIMING differs while the
    // outcome converges -- which is precisely why relaying the outcome is the
    // wrong intervention.
    private static void RelayDeathWithdrawn(Actor victim, Element element, int sq)
    {
        if (CoopNet.Mode != CoopMode.Host) return;
        if (Replication.ApplyingRemote) return;      // replaying, not originating
        if (victim == null || element == null) return;
        try
        {
            int ax = -1, ay = -1;
            var t = victim.GetLastTile();
            if (t != null) { var p = t.GetTilePos(); ax = p.x; ay = p.y; }

            int idx = -1;
            try { idx = element.GetElementIndex(); } catch { }

            var msg = new CoopMsg { T = "edie", AX = ax, AY = ay, Prm = idx, SqId = sq };
            Replication.StampIds(msg, victim, isOrder: false);
            CoopNet.Send(msg);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[DMGPROBE] Death relay failed: {e.Message}"); }
    }

    // Identity in the same terms orders use, so a diff of the two logs lines up
    // with the unresolved-order reports rather than needing to be correlated by
    // eye.
    private static string Id(Actor a)
    {
        if (a == null) return "?";
        int rid = -1, cid = -1;
        try { rid = DesyncDetector.RosterIndexOf(a); } catch { }
        try { cid = DesyncDetector.CoopIdOf(a); } catch { }
        return $"{DesyncDetector.TemplateNameOf(a)} rid={rid} cid={cid}";
    }

    private static string SkillId(Skill s)
    {
        try { return s?.GetID() ?? "-"; } catch { return "-"; }
    }
}

[HarmonyPatch]
internal static class DamageProbePatches
{
    // REGISTER IN Plugin.Load -- an unregistered patch class is silently inert.
    [HarmonyPatch(typeof(Actor), nameof(Actor.OnDamageReceived))]
    [HarmonyPostfix]
    private static void AfterDamage(Actor __instance, Entity _attacker, Skill _skill)
    {
        DamageProbe.NoteDamage(__instance, _attacker, _skill, -1);
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.OnElementDeath))]
    [HarmonyPostfix]
    private static void AfterElementDeath(Actor __instance, Element _element, Skill _skill,
                                          int _damageAppliedToElement)
    {
        DamageProbe.NoteDeath(__instance, _element, _skill, _damageAppliedToElement);
    }
}
