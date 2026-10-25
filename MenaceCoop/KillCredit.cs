using System;
using System.Collections.Generic;
using HarmonyLib;
#if MELON
using Il2CppMenace.Strategy;
using Il2CppMenace.Tactical;
#else
using Menace.Strategy;
using Menace.Tactical;
#endif

namespace MenaceCoop;

// A PARTNER'S KILL MUST COUNT FOR THE PARTNER'S SQUAD (player report
// 2026-10-12: "units killed by the guests do not count as kills after the
// mission"). Commander mood, the loot of a kill and "kill no more than N% of
// the enemies" objectives all read the unit statistics.
//
// A relayed element death already names its killer (Element.OnDeath with the
// attacker from the relay -- probed, ELEMENT-ONDEATH attacker=<guest squad>
// remote=True). The UNIT's death does not: when the relayed hit takes the last
// element, MarkDeadIfNoLivingElements runs Actor.Die(), which has no attacker,
// and UnitStatisticsListener.OnEntityDeath then credits nobody. So every relayed
// killing blow records its attacker here, and a unit death the engine reports
// with no killer gets that one.
internal static class KillCredit
{
    private static readonly Dictionary<IntPtr, Entity> _lastRelayedAttacker = new();
    private static IntPtr _battle;

    internal static void NoteRelayedKill(Element el, Entity attacker)
    {
        if (el == null || attacker == null) return;
        try
        {
            var tm = TacticalManager.Get();
            if (tm == null) return;
            if (_battle != tm.Pointer) { _battle = tm.Pointer; _lastRelayedAttacker.Clear(); _reported.Clear(); }
            var victim = el.GetEntity();
            if (victim != null) _lastRelayedAttacker[victim.Pointer] = attacker;
        }
        catch { }
    }

    // Units whose death the listener has been told of (by the engine or by us).
    private static readonly HashSet<IntPtr> _reported = new();

    // RUN 2 (2026-10-24, r146): the relayed death never reaches the listener at
    // all -- a spiderling killed by a guest's squad died through Actor.Die()
    // (remote) and no OnEntityDeath followed, while the host's own kills always
    // had one. So filling in a missing killer is not enough: after a relayed
    // unit death the listener is called here, once, with the relayed killer.
    internal static void AfterRelayedDie(Actor a)
    {
        if (a == null || !CoopNet.Connected || !Replication.ApplyingRemote) return;
        try
        {
            if (TacticalManager.Get()?.Pointer != _battle || _reported.Contains(a.Pointer)) return;
            if (!_lastRelayedAttacker.TryGetValue(a.Pointer, out var killer) || killer == null) return;
            UnitStatisticsListener.OnEntityDeath(a, killer);   // our prefix marks it reported and logs
            Plugin.Logger.LogInfo($"[KILL] '{DesyncDetector.TemplateNameOf(a)}' died to a relayed hit -- reported to the unit statistics for '{DesyncDetector.TemplateNameOf(killer.TryCast<Actor>())}'");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[KILL] crediting a relayed death failed: {e.Message}"); }
    }

    // A DEATH WITH NO KILLER CRASHED THE HOST (BUGS.md #98, r151 + r152). An
    // emptied enemy truck died with killer=none; the engine's OnEntityDeath,
    // entered through this patch, raised an IL2CPP exception that unwound into
    // the managed patch frame -- the runtime fail-fasts and the process hangs on
    // a hidden hard-error box ("Not responding", main thread in NtRaiseHardError).
    // Every earlier run had a killer. With none to credit, the listener has
    // nothing to record for the kill, so the call is skipped.
    internal static bool BeforeEntityDeath(Entity target, ref Entity killer)
    {
        FillKiller(target, ref killer);
        if (killer != null || target == null) return true;
        try { Plugin.Logger.LogInfo($"[KILL] '{DesyncDetector.TemplateNameOf(target.TryCast<Actor>())}' died with no killer -- engine OnEntityDeath skipped (#98)"); } catch { }
        return false;
    }

    internal static void FillKiller(Entity target, ref Entity killer)
    {
        try
        {
            var tm = TacticalManager.Get();
            if (tm != null && target != null)
            {
                if (_battle != tm.Pointer) { _battle = tm.Pointer; _lastRelayedAttacker.Clear(); _reported.Clear(); }
                _reported.Add(target.Pointer);
            }
        }
        catch { }
        if (BugProbes.On && CoopNet.Connected && target != null)
        {
            try { Plugin.Logger.LogInfo($"[KILL] OnEntityDeath '{DesyncDetector.TemplateNameOf(target.TryCast<Actor>())}' killer={(killer == null ? "none" : DesyncDetector.TemplateNameOf(killer.TryCast<Actor>()))} remote={Replication.ApplyingRemote}"); } catch { }
        }
        if (killer != null || target == null || !CoopNet.Connected) return;
        try
        {
            if (TacticalManager.Get()?.Pointer != _battle) return;
            if (!_lastRelayedAttacker.TryGetValue(target.Pointer, out var a) || a == null) return;
            killer = a;
            Plugin.Logger.LogInfo($"[KILL] '{DesyncDetector.TemplateNameOf(target.TryCast<Actor>())}' died to a relayed hit -- credited to '{DesyncDetector.TemplateNameOf(a.TryCast<Actor>())}'");
        }
        catch { }
    }
}

[HarmonyPatch]
internal static class KillCreditPatches
{
    [HarmonyPatch(typeof(UnitStatisticsListener), nameof(UnitStatisticsListener.OnEntityDeath))]
    [HarmonyPrefix]
    private static bool BeforeEntityDeath(Entity _target, ref Entity _killer) => KillCredit.BeforeEntityDeath(_target, ref _killer);

    [HarmonyPatch(typeof(Actor), nameof(Actor.Die))]
    [HarmonyPostfix]
    private static void AfterActorDie(Actor __instance) => KillCredit.AfterRelayedDie(__instance);
}
