// SHARED BY EVERY MELONLOADER MOD IN THIS TREE (co-op, AutoCombat, IntroSkip).
// Compile it in with one csproj line:
//   <Compile Include="..\..\_Tools\Shared\PatchReport.cs" />
// and call it once AFTER patching (MelonLoader auto-patches before
// OnInitializeMelon; a mod that patches by hand calls it after its own loop):
//   MenaceShared.PatchReport.Log(HarmonyInstance, typeof(MyMod).Assembly,
//       s => LoggerInstance.Msg(s), s => LoggerInstance.Error(s));
//
// User requirement 2026-10-04: every mod lists its patches at startup and says
// plainly when one did not go through. It asks HARMONY which of this assembly's
// patch methods are installed on which engine methods -- no name modelling -- so
// a hook that silently never applied (co-op's LootSyncPatches was never on its
// hand list, for its whole life) is named in the log instead of discovered later.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace MenaceShared;

internal static class PatchReport
{
    // HarmonyLib.Harmony in full: the game's assemblies contribute a top-level
    // `Harmony` NAMESPACE, so the bare name does not compile in a melon.
    internal static void Log(HarmonyLib.Harmony harmony, Assembly asm, Action<string> info, Action<string> error)
    {
        try
        {
            // What Harmony actually installed, owned by this mod's Harmony id.
            var applied = new Dictionary<MethodInfo, List<string>>();   // patch method -> targets
            int targets = 0;
            foreach (var original in harmony.GetPatchedMethods())
            {
                var pi = HarmonyLib.Harmony.GetPatchInfo(original);
                if (pi == null) continue;
                bool ours = false;
                foreach (var p in pi.Prefixes.Concat(pi.Postfixes).Concat(pi.Transpilers).Concat(pi.Finalizers))
                {
                    if (p.owner != harmony.Id || p.PatchMethod == null) continue;
                    ours = true;
                    if (!applied.TryGetValue(p.PatchMethod, out var list)) applied[p.PatchMethod] = list = new List<string>();
                    list.Add($"{original.DeclaringType?.Name}.{original.Name}");
                }
                if (ours) targets++;
            }

            // What this assembly DECLARES as patch methods.
            var missing = new List<string>();
            int declared = 0, classes = 0;
            foreach (var cls in SafeTypes(asm))
            {
                if (!IsPatchClass(cls)) continue;
                var hooked = new List<string>();
                bool any = false;
                foreach (var m in cls.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    string kind = PatchKind(m);
                    if (kind == null) continue;
                    any = true;
                    declared++;
                    if (applied.TryGetValue(m, out var on)) hooked.AddRange(on);
                    else missing.Add($"{cls.Name}.{m.Name} ({kind}) -> {DeclaredTarget(cls, m)}");
                }
                if (!any) continue;
                classes++;
                info($"[PATCHES] {cls.Name}: {(hooked.Count == 0 ? "(nothing)" : string.Join(", ", hooked.Distinct()))}");
            }

            if (missing.Count == 0)
                info($"[PATCHES] OK: all {declared} patch method(s) in {classes} class(es) applied, on {targets} engine method(s)");
            else
            {
                error($"[PATCHES] {missing.Count} of {declared} patch method(s) DID NOT APPLY -- whatever they do is not happening:");
                foreach (var s in missing) error($"[PATCHES]   NOT APPLIED: {s}");
            }
        }
        catch (Exception e)
        {
            error($"[PATCHES] patch report could not complete: {e.GetType().Name}: {e.Message}");
        }
    }

    private static IEnumerable<Type> SafeTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
    }

    private static bool IsPatchClass(Type t)
    {
        if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0) return true;
        return t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Any(m => m.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0);
    }

    private static string PatchKind(MethodInfo m)
    {
        if (m.GetCustomAttributes(typeof(HarmonyPrefix), false).Length > 0 || m.Name == "Prefix") return "prefix";
        if (m.GetCustomAttributes(typeof(HarmonyPostfix), false).Length > 0 || m.Name == "Postfix") return "postfix";
        if (m.GetCustomAttributes(typeof(HarmonyTranspiler), false).Length > 0 || m.Name == "Transpiler") return "transpiler";
        if (m.GetCustomAttributes(typeof(HarmonyFinalizer), false).Length > 0 || m.Name == "Finalizer") return "finalizer";
        return null;
    }

    // Best-effort name of the intended target, for the error line only.
    private static string DeclaredTarget(Type cls, MethodInfo m)
    {
        Type t = null; string name = null;
        foreach (var a in cls.GetCustomAttributes(typeof(HarmonyPatch), false).Concat(m.GetCustomAttributes(typeof(HarmonyPatch), false)))
            if (a is HarmonyAttribute ha && ha.info != null)
            {
                t = ha.info.declaringType ?? t;
                name = ha.info.methodName ?? (ha.info.methodType == MethodType.Constructor ? ".ctor" : name);
            }
        return t == null && name == null ? "(target not named in attributes)" : $"{t?.Name ?? "?"}.{name ?? "?"}";
    }
}
