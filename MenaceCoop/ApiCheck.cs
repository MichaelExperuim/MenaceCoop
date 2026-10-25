using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
#if MELON
using Il2CppMenace.Tactical;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.Tactical;
#endif

namespace MenaceCoop;

// API CHECK -- answers "did this game update break the mod?" in the first second
// of a run, in one line, instead of at an NRE forty minutes into a battle.
//
// WHY THIS EXISTS. v0.7.11 broke MenaceCoop by removing a single method,
// BaseTile.GetPos(). Nothing said so at load: the mod started, the log looked
// healthy, and the breakage only surfaced at runtime inside a mission. That is
// the most expensive shape a failure can have, because a mid-battle NRE reads
// like a desync bug and gets investigated as one.
//
// WHAT IT CATCHES:
//
//   1. PATCH TARGETS THAT NO LONGER RESOLVE. This is the real gap. A direct call
//      like actor.GetLastTile() is verified by the COMPILER, because the mod is
//      built against the interop assemblies the loader generated from the shipped
//      game -- so a clean build already proves every called member exists. A
//      [HarmonyPatch] target is different: `nameof` compiles fine and the lookup
//      happens at RUNTIME, so a renamed or re-signatured method is invisible
//      until Harmony tries to patch it. 131 patch declarations over 38 engine
//      types is the mod's real coupling surface, and this is what watches it.
//
//   2. A PLAYER ON A DIFFERENT GAME BUILD FROM THE ONE WE COMPILED AGAINST.
//      Our build proves the API against OUR game. A player who updates before we
//      do gets failures we have never seen. The probes below re-check the engine
//      primitives co-op cannot work without, on THEIR machine.
//
// WHAT IT CANNOT CATCH, ever: BEHAVIOUR. Every hard bug in this project has been
// semantic, not structural -- a creature's footprint collapsing to its anchor
// while it moves, damage landing after Skill.Use returns, GetElements() shrinking
// with casualties. The signature is identical and the meaning changed. This check
// will report a clean bill of health in all of those cases. It tells you the mod
// is still WIRED to the engine; it says nothing about whether the engine still
// behaves the way the mod assumes. Do not read more into a PASS than that.
//
// ---------------------------------------------------------------------------
// TWO FALSE ALARMS ON ITS FIRST RUN, 2026-09-25, AND WHAT THEY CHANGED.
//
// The first version resolved patch targets itself, with AccessTools, and reported
// `Actor.OnDamageReceived` gone three times. It is not gone -- the mod patches it
// via nameof(Actor.OnDamageReceived), which the COMPILER checks, so the member
// provably exists. The resolver was modelling Harmony's attribute rules and
// getting them wrong.
//
// It also reported `TacticalManager.GetActors` missing. That one never existed:
// the probe list had been hand-written from a grep of the api-index, `GetActors`
// matched `GetActorsRoot`, and the index is flying its STALE banner anyway.
//
// A checker that cries wolf is worse than no checker, because the first false
// alarm teaches everyone to skim past the line. So neither mistake is merely
// fixed here, both are made impossible:
//
//   - Patch targets are no longer resolved by us. We collect what the attributes
//     ASK for and compare it against what Harmony ACTUALLY PATCHED, read back
//     from the Harmony instance. Harmony's own resolution is the authority.
//   - Every probe is written `nameof(Type.Member)`, so a name that does not exist
//     is a BUILD error on our machine and can never reach a player's log as a
//     runtime warning. That is what turned the GetActors mistake into a typo the
//     compiler catches.
// ---------------------------------------------------------------------------
internal static class ApiCheck
{
    // The game build this DLL was compiled against. ADVISORY ONLY: a mismatch is
    // normal and harmless most of the time (the game updates far more often than
    // it breaks us), so this warns and never blocks. Its value is triage -- a
    // player's log states outright that they are not on our build, which is the
    // first question worth asking about any report we cannot reproduce.
    // Bump when the dev install updates. Format is Plugin.GameFingerprint's.
    internal const string BuiltAgainstGame = "v0.7.17+22362|15961908";

    // ENGINE PRIMITIVES CO-OP CANNOT WORK WITHOUT.
    //
    // Types are `typeof` and members are `nameof`, so BOTH halves are compiler-
    // checked here and only the lookup happens on the player's machine. A removed
    // type or member becomes a build error for us -- which is the right outcome,
    // and it is the reason this list cannot rot into false alarms.
    //
    // Chosen by fan-in, measured over the mod's ~1,900 engine call sites. BaseTile
    // leads because BaseTile is exactly what v0.7.11 took away.
    private static readonly (Type type, string member)[] Probes =
    {
        (typeof(BaseTile),        nameof(BaseTile.GetTilePos)),
        (typeof(Entity),          nameof(Entity.GetElements)),
        (typeof(Element),         nameof(Element.GetSquaddieId)),
        (typeof(Element),         nameof(Element.Die)),
        (typeof(Actor),           nameof(Actor.GetLastTile)),
        (typeof(Actor),           nameof(Actor.IsTurnDone)),
        (typeof(Actor),           nameof(Actor.IsDying)),
        (typeof(Actor),           nameof(Actor.GetActionPoints)),
        (typeof(Actor),           nameof(Actor.MoveTo)),
        (typeof(Actor),           nameof(Actor.OnDamageReceived)),
        (typeof(TacticalManager), nameof(TacticalManager.GetRound)),
        (typeof(TacticalManager), nameof(TacticalManager.IsPlayerTurn)),
        (typeof(TacticalManager), nameof(TacticalManager.GetActorsRoot)),
    };

    // Ordered so a support log reads top-down: this runs BEFORE patching, because
    // if a primitive is gone the patch lines that follow are noise.
    internal static void CheckPrimitives(string runningGame)
    {
        try
        {
            var lost = new List<string>();
            foreach (var (type, member) in Probes)
            {
                // PER-PROBE, NOT PER-PASS. AccessTools THROWS on an ambiguous
                // match, and one throw used to abort the entire primitive check:
                // "primitive check could not complete: Ambiguous match ...
                // Actor:OnDamageReceived", with twelve other probes never run.
                //
                // An ambiguous match is also not a failure -- it means the member
                // exists MORE THAN ONCE. Treating it as present is the correct
                // answer, and a check that abandons itself on the first overload
                // is a check nobody can rely on.
                bool present;
                try
                {
                    present = AccessTools.Method(type, member) != null
                              || AccessTools.DeclaredMethod(type, member) != null;
                }
                catch { present = true; }   // ambiguous => it is there, several times
                if (!present) lost.Add($"{type.Name}.{member}");
            }

            if (lost.Count == 0)
                Plugin.Logger.LogInfo($"[API] OK: all {Probes.Length} engine primitive(s) present");
            else
            {
                Plugin.Logger.LogError(
                    $"[API] {lost.Count} ENGINE PRIMITIVE(S) MISSING -- co-op cannot work correctly on this game build:");
                foreach (var n in lost) Plugin.Logger.LogError($"[API]   gone: {n}");
            }

            if (!string.IsNullOrEmpty(runningGame) && runningGame != BuiltAgainstGame)
                Plugin.Logger.LogWarning(
                    $"[API] Built against game {BuiltAgainstGame}, running {runningGame}. " +
                    "Usually fine -- noted because it is the first thing to check on a report that will not reproduce.");
        }
        catch (Exception e)
        {
            // A DIAGNOSTIC MUST NEVER BE THE THING THAT BREAKS THE RUN.
            Plugin.Logger.LogWarning($"[API] primitive check could not complete: {e.Message}");
        }
    }

    // Runs AFTER patching, and compares INTENT against OUTCOME:
    //   intent  = every (type, method) a [HarmonyPatch] attribute asks for
    //   outcome = every method the Harmony instance says it patched
    // Anything asked for and not patched is a hook that is silently doing nothing.
    //
    // Reading the outcome from Harmony is the whole point: it removes our own
    // resolution from the equation, which is what produced the OnDamageReceived
    // false alarm above.
    internal static void VerifyPatches(HarmonyLib.Harmony harmony, Type[] patchClasses)
    {
        try
        {
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            int declarations = 0, uninterpretable = 0;

            foreach (var cls in patchClasses ?? Array.Empty<Type>())
            {
                if (cls == null) continue;
                foreach (var attr in AllPatchAttrs(cls))
                {
                    declarations++;
                    var t = attr.declaringType;
                    var m = attr.methodName;

                    // CONSTRUCTOR PATCHES CARRY NO methodName, and skipping them
                    // would hide the single most fragile hook we have. The mapgen
                    // RNG pin patches MapgenInput's constructor and a previous
                    // session recorded that it "has never once logged" -- kept
                    // anyway, and matching maps were then read as evidence it
                    // worked. Naming it here is how that question gets answered at
                    // load instead of by inference from a silent log.
                    if (m == null && (attr.methodType ?? MethodType.Normal) == MethodType.Constructor)
                        m = ".ctor";

                    if (t == null || string.IsNullOrEmpty(m)) { uninterpretable++; continue; }
                    wanted.Add($"{t.FullName}|{m}");
                }
            }

            var got = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in harmony.GetPatchedMethods())
            {
                if (m?.DeclaringType == null) continue;
                string name = m.Name;
                got.Add($"{m.DeclaringType.FullName}|{name}");
                // A property patch shows up as get_X / set_X while the attribute
                // asked for X. Record the bare name too rather than report a hook
                // that is plainly working.
                if (name.StartsWith("get_", StringComparison.Ordinal) ||
                    name.StartsWith("set_", StringComparison.Ordinal))
                    got.Add($"{m.DeclaringType.FullName}|{name.Substring(4)}");
            }

            var dead = new List<string>();
            foreach (var w in wanted) if (!got.Contains(w)) dead.Add(w.Replace("|", "."));

            if (dead.Count == 0)
            {
                Plugin.Logger.LogInfo(
                    $"[API] OK: {declarations} patch declaration(s) over {wanted.Count} distinct engine method(s), " +
                    $"all patched (Harmony reports {got.Count} method(s))" +
                    (uninterpretable > 0 ? $"; {uninterpretable} declaration(s) not checkable by name" : ""));
            }
            else
            {
                Plugin.Logger.LogError(
                    $"[API] {dead.Count} of {wanted.Count} ENGINE METHOD(S) WE PATCH WERE NOT PATCHED -- " +
                    "the game has changed under this mod and everything those hooks did silently stops:");
                foreach (var d in dead) Plugin.Logger.LogError($"[API]   not patched: {d}");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[API] patch verification could not complete: {e.Message}");
        }
    }

    // Class-level and method-level [HarmonyPatch] attributes, flattened. No
    // merging and no signature work: this only needs the type and the name, which
    // is the granularity the comparison happens at.
    private static IEnumerable<HarmonyMethod> AllPatchAttrs(Type cls)
    {
        foreach (var a in cls.GetCustomAttributes(typeof(HarmonyPatch), true))
            if (a is HarmonyAttribute ha && ha.info != null) yield return ha.info;

        foreach (var m in cls.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Static | BindingFlags.Instance
                                         | BindingFlags.DeclaredOnly))
            foreach (var a in m.GetCustomAttributes(typeof(HarmonyPatch), true))
                if (a is HarmonyAttribute ha && ha.info != null) yield return ha.info;
    }
}
