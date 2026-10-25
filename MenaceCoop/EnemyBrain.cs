using System;
#if MELON
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;
using Il2CppMenace.Tactical.Skills;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.Tactical;
using Menace.Tactical.AI;
using Menace.Tactical.Skills;
#endif

namespace MenaceCoop;

// BORROW THE GAME'S OWN TARGET SCORING INSTEAD OF GUESSING.
//
// Reported from play: "the enemy AI is way more advanced than our combat AI. is
// there a way to analyze the enemy AI and steal their algorithms." There is, and
// it is better than reimplementing: the engine's evaluator is public.
//
//   SkillBehavior.GetTargetValue(forImmediateUse, uses, target, goal,
//                                skill, from, targetedTile)
//
// That is the function the enemy uses to decide how good a given skill is
// against a given tile from a given position. Our auto-combat currently ranks
// targets with a hand-rolled toughness heuristic, which is why the enemy plays
// visibly better with the same information.
//
// The mod already borrows a sliver of this -- ReadAiWeights() reads
// AIConfig.WEIGHTS for cover/threat/distance -- so the precedent and the access
// both exist. This goes after the actual decision function.
//
// NOTHING IS CONSTRUCTED HERE, DELIBERATELY.
//
// An Agent needs an AIFaction, and building engine objects from a mod is how you
// earn an AccessViolationException -- which .NET Core cannot catch, so it takes
// the whole game with it. Every object used here is one the engine already made
// and handed us: Actor.GetAgent(), and the behaviours that agent already owns. If
// any of them is missing we return null and the caller keeps its old heuristic.
internal static class EnemyBrain
{
    // Whether the engine's scorer was reachable, resolved once per battle so a
    // dead end costs one probe rather than a lookup on every candidate tile.
    private static IntPtr _probedFor;
    private static bool _available;

    internal static bool Available => _available;

    internal static void Reset() { _probedFor = IntPtr.Zero; _available = false; }

    // Does this actor carry an engine Agent, and does that agent expose a
    // SkillBehavior we can ask? Player-controlled units may well not -- the
    // agent is the AI's own bookkeeping and there is no reason the engine has to
    // build one for a unit a human drives. That is exactly what this answers,
    // and it answers it from the live board rather than from assumption.
    internal static void ProbeOnce(TacticalManager tm)
    {
        if (tm == null || tm.Pointer == _probedFor) return;
        _available = false;

        try
        {
            int playerAgents = 0, playerActors = 0, behaviours = 0, skillBehaviours = 0;

            var pf = tm.GetPlayerFaction()?.GetActors();
            for (int i = 0; pf != null && i < pf.Count; i++)
            {
                var a = pf[i];
                if (a == null) continue;
                playerActors++;

                Agent ag = null;
                try { ag = a.GetAgent(); } catch { }
                if (ag == null) continue;
                playerAgents++;

                var bs = ag.GetBehaviors();
                if (bs == null) continue;
                behaviours += bs.Count;
                for (int b = 0; b < bs.Count; b++)
                {
                    var sb = bs[b]?.TryCast<SkillBehavior>();
                    if (sb != null) skillBehaviours++;
                }
            }

            // DO NOT ANSWER BEFORE THERE IS ANYTHING TO LOOK AT.
            //
            // The first run of this latched on the first tick a TacticalManager
            // existed and duly reported "0/0 of your units carry an Agent" --
            // which is true of an empty board and says nothing about the
            // question. Units arrive over several frames; the probe waits for
            // them rather than mistaking an empty board for a negative answer.
            if (playerActors == 0) return;
            _probedFor = tm.Pointer;

            _available = skillBehaviours > 0;

            Plugin.Logger.LogWarning(
                $"[BRAIN] Engine AI scorer probe: {playerAgents}/{playerActors} of your units carry an engine Agent, " +
                $"{behaviours} behaviour(s), {skillBehaviours} skill-behaviour(s) -> " +
                (_available ? "USABLE, auto-combat can score with the game's own evaluator"
                            : "NOT usable, keeping the built-in heuristic"));

            // Whether the enemy's own units carry one tells us if the mechanism
            // exists at all or only the player side is missing it -- a different
            // problem with a different answer.
            if (!_available)
            {
                int enemyAgents = 0, enemyActors = 0;
                var facs = tm.GetFactions();
                int pfIdx = -1;
                try { pfIdx = tm.GetPlayerFaction()?.GetIndex() ?? -1; } catch { }
                for (int f = 0; facs != null && f < facs.Length; f++)
                {
                    var fac = facs[f];
                    if (fac == null || fac.GetIndex() == pfIdx) continue;
                    var acts = fac.GetActors();
                    for (int i = 0; acts != null && i < acts.Count; i++)
                    {
                        if (acts[i] == null) continue;
                        enemyActors++;
                        try { if (acts[i].GetAgent() != null) enemyAgents++; } catch { }
                    }
                }
                Plugin.Logger.LogWarning(
                    $"[BRAIN] For comparison, {enemyAgents}/{enemyActors} ENEMY units carry an Agent.");
            }
        }
        catch (Exception e)
        {
            _available = false;
            Plugin.Logger.LogWarning($"[BRAIN] Scorer probe failed: {e.Message} -- keeping the built-in heuristic");
        }
    }

    // The engine's own opinion of using `skill` from `from` against `target`.
    // Returns null whenever anything is missing, which the caller must treat as
    // "no opinion" rather than "bad target" -- a zero would quietly veto every
    // shot the moment this stops resolving.
    internal static float? ScoreTarget(Actor actor, Skill skill, Tile target, Tile from, Goal goal = Goal.Damage)
    {
        if (!_available || actor == null || skill == null || target == null) return null;
        try
        {
            var ag = actor.GetAgent();
            var bs = ag?.GetBehaviors();
            if (bs == null) return null;

            for (int b = 0; b < bs.Count; b++)
            {
                var sb = bs[b]?.TryCast<SkillBehavior>();
                if (sb == null) continue;

                // Match the behaviour to the skill we are actually asking about;
                // scoring a rifle shot with the grenade behaviour's weights would
                // be worse than not asking.
                Skill owned = null;
                try { owned = sb.GetSkill(); } catch { }
                if (owned == null || owned.Pointer != skill.Pointer) continue;

                // Goal is an enum -- Damage, Suppression, Stun -- so this is not just
                // "score the shot", it is "score the shot FOR THIS PURPOSE". Damage is
                // the right question for an attack; suppression scoring is a separate
                // decision worth making later, once this is proven.
                return sb.GetTargetValue(true, 1, target, goal, skill, from, target);
            }
        }
        catch { }
        return null;
    }
}
