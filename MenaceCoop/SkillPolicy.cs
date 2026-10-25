using System;
using System.Collections.Generic;
using System.Text;
#if MELON
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.Tactical;
using Menace.Tactical.Skills;
#endif

namespace MenaceCoop;

// WHICH SKILLS ARE WORTH AN ACTION, AND WHEN.
//
// Two separate jobs live here, and the first exists because of the second.
//
// The CENSUS logs every skill the player's squads actually carry, once per
// battle, with the properties the brain can see. The weapon classifiers in this
// mod match on substrings of skill IDs -- "carbine", "rocket", "_mg" -- and
// those lists were written by guessing at MENACE's naming conventions. Guessing
// has already cost real bugs here, so the census prints the true vocabulary and
// the policy below gets tuned against it instead of against my assumptions.
//
// The POLICY decides whether a non-attack skill earns the action. The default is
// no. A squad that spends its activation posturing is worse than one that simply
// shoots, and that is not hypothetical -- an earlier build managed 18 crouches
// and 13 stand-ups against 14 shots in a single mission.
internal static class SkillPolicy
{
    private static bool _censusDone;
    private static int _censusRound = -1;

    internal static void Reset() { _censusDone = false; _censusRound = -1; }

    // Once per battle, list what the squads are carrying.
    internal static void CensusOnce(TacticalManager tm)
    {
        if (_censusDone || tm == null) return;
        try
        {
            int round = tm.GetRound();
            if (round == _censusRound) return;
            _censusRound = round;

            var actors = tm.GetPlayerFaction()?.GetActors();
            if (actors == null || actors.Count == 0) return;

            var seen = new HashSet<string>();
            var attacks = new StringBuilder();
            var decisions = new StringBuilder();
            var passives = new StringBuilder();
            for (int i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null) continue;
                var skills = a.GetSkills()?.GetAllSkills();
                for (int s = 0; skills != null && s < skills.Count; s++)
                {
                    var sk = skills[s]?.TryCast<Skill>();
                    if (sk == null) continue;
                    string id;
                    try { id = sk.GetID() ?? ""; } catch { continue; }
                    if (id.Length == 0 || !seen.Add(id)) continue;

                    bool atk = false, active = false, usable = false;
                    int cost = -1;
                    try { atk = sk.IsAttack(); } catch { }
                    try { active = sk.IsSkillType(SkillType.Active); } catch { }
                    try { usable = sk.IsUsable(); } catch { }
                    try { cost = sk.GetActionPointCost(); } catch { }

                    // GROUPED BY WHETHER IT IS A DECISION AT ALL.
                    //
                    // Asked directly: "some buffs are passive -- is overwatch
                    // passive or active?" A flat list cannot answer that, and the
                    // distinction is the single most important thing here. A
                    // PASSIVE is not a choice the AI can make: the engine applies
                    // it on its own (morale states, perks, equipment traits) and
                    // no amount of policy will or should touch it. Only ACTIVE
                    // non-attack skills are decisions, and there are far fewer of
                    // them than a raw list suggests.
                    var bucket = atk ? attacks : active ? decisions : passives;
                    bucket.Append($"\n    {id,-44} cost={cost,-4} " +
                              $"{(atk ? (IsAntiArmourWeapon(id) ? "[AP weapon]" : IsAntiInfantryWeapon(id) ? "[anti-infantry]" : "[UNCLASSIFIED -- tell me about this one]") : Verdict(id))}");
                }
            }

            if (attacks.Length == 0 && decisions.Length == 0 && passives.Length == 0) return;
            _censusDone = true;

            var sb = new StringBuilder();
            sb.Append($"[SKILLS] {seen.Count} distinct skills across your squads.\n");
            sb.Append($"\n  WEAPONS -- what it can shoot with:{(attacks.Length == 0 ? "\n    (none)" : attacks.ToString())}");
            sb.Append($"\n\n  DECISIONS -- active, non-attack, the only ones the AI can choose:{(decisions.Length == 0 ? "\n    (none)" : decisions.ToString())}");
            sb.Append($"\n\n  PASSIVE -- applied by the engine, never an AI choice ({CountLines(passives)}):{passives}");
            Plugin.Logger.LogWarning(sb.ToString());
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[SKILLS] census failed: {e.Message}"); }
    }

    private static int CountLines(StringBuilder sb)
    {
        int n = 0;
        for (int i = 0; i < sb.Length; i++) if (sb[i] == (char)10) n++;
        return n;
    }

    // What the policy thinks of a non-attack skill, printed alongside the census
    // so the two can be compared at a glance.
    private static string Verdict(string id)
    {
        if (IsStance(id)) return "[stance -- only to set up a heavy weapon]";
        // AP-granting skills are not listed by name here on purpose: the brain
        // detects them at runtime, by noticing a skill left the unit with MORE
        // action points than before, and never picks that one again. That is an
        // observation rather than a guess about naming, so it needs no list.
        if (IsChore(id)) return "[chore -- never worth a combat action]";
        if (IsWorthAnAction(id)) return "[USE -- genuinely worth the action]";
        return "[skip -- no combat value]";
    }

    // ============================================================================
    // OBSERVED SKILLS ONLY. NOTHING HERE IS GUESSED.
    // ============================================================================
    //
    // Every id below was seen in a real census of squads in a real battle. There
    // used to be a good deal more -- overwatch, ambush, rally, inspire, first_aid,
    // focus_fire, and others -- none of which exist in this game as far as anyone
    // has actually verified. I invented them as plausible-sounding names, which is
    // the same habit that produced several of this mod's worst bugs, and it is
    // especially poor when the census was built precisely to stop guessing.
    //
    // Rule from here: if it has not been observed, it does not appear in this file.
    // An unrecognised skill is simply never chosen, and an unrecognised weapon gets
    // no match bonus and falls back to range and damage. Both are safe failures.
    // The census flags anything UNCLASSIFIED so real names can be added on sight.
    //
    // Combat comes first. Skills are secondary and are held to a strict allow-list.

    // Observed: active.destroy_wreckage (70AP, demolish scenery),
    //           active.infantry.cut_fence (30AP), special.infantry_crawl (0AP).
    // Active and usable, none of them shoot anything, and a naive picker will
    // spend a whole activation on one mid-firefight.
    private static bool IsChore(string id) =>
        id.Contains("destroy_wreckage") || id.Contains("cut_fence") || id.Contains("infantry_crawl");

    internal static bool IsWorthAnAction(string id) => IsUrgent(id) || IsOptionalBuff(id);

    // Observed: special.stop_bleedout (40AP). Runs ahead of shooting and ignores
    // the spare-points rule -- a squaddie bleeding out is a shooter lost for the
    // rest of the campaign, not just this turn.
    internal static bool IsUrgent(string id) =>
        id.Contains("stop_bleedout");

    // Observed: perk.taking_command (60AP). The only optional buff that exists so
    // far, so this list is exactly one entry long.
    internal static bool IsOptionalBuff(string id) => IsLeaderBuff(id);

    internal static bool IsLeaderBuff(string id) =>
        id.Contains("taking_command");

    // Observed: active.stance.deploy, active.stance.get_up, special.stance.deployed.
    internal static bool IsStance(string id) =>
        id.Contains("stance") || id.Contains("deploy") || id.Contains("get_up");

    // BUFF ON THE APPROACH, FIGHT WHEN ENGAGED.
    //
    // Most buffs need something to use them against, so they wait for contact --
    // buffing across an empty map is how an earlier build managed ten support
    // skills against two attacks.
    //
    // The leader's buff is the opposite: a leader standing in a firefight should
    // be shooting, not handing out orders, because that activation is a gun the
    // squad does not get for the round. Orders are given before contact, when a
    // free action is genuinely free.
    internal static bool MayUseNow(string id, bool enemyInRange)
    {
        if (IsUrgent(id)) return true;               // saving someone never waits
        if (IsLeaderBuff(id)) return !enemyInRange;  // orders before the shooting
        return enemyInRange;                         // everything else needs a target
    }

    // CROUCHING IS FOR HEAVY WEAPONS, NOT FOR ITS OWN SAKE.
    //
    // Stance was blocked outright, which stopped the crouch/stand loop but also
    // meant a machine gun never set up -- the one thing stance genuinely exists
    // for. Allowed for exactly that: the unit carries a weapon worth deploying,
    // and there is something to shoot.
    internal static bool StanceIsWorthIt(Actor a, bool enemyInRange)
    {
        if (!enemyInRange) return false;
        try
        {
            var skills = a.GetSkills()?.GetAllSkills();
            for (int i = 0; skills != null && i < skills.Count; i++)
            {
                var sk = skills[i]?.TryCast<Skill>();
                if (sk == null) continue;
                try { if (!sk.IsAttack()) continue; } catch { continue; }
                string id; try { id = sk.GetID() ?? ""; } catch { continue; }
                if (IsHeavyWeapon(id)) return true;
            }
        }
        catch { }
        return false;
    }

    // Observed: active.medium_mg_suppressive_fire.
    private static bool IsHeavyWeapon(string id) =>
        id.Contains("_mg") || id.Contains("suppressive");

    // Observed: active.launch_rocket (60AP).
    private static bool IsAntiArmourWeapon(string id) =>
        id.Contains("rocket") || id.Contains("launch");

    // Observed: active.fire_lf_carbine_tier1 (40AP),
    //           active.medium_mg_suppressive_fire (40AP).
    private static bool IsAntiInfantryWeapon(string id) =>
        id.Contains("carbine") || id.Contains("_mg") || id.Contains("suppressive");
}
