using System;
using System.Text;
using HarmonyLib;
#if MELON
using Il2CppMenace.Tactical;
using Il2CppMenace.OffmapAbilities;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.Tactical;
using Menace.OffmapAbilities;
#endif

namespace MenaceCoop;

// PROBES FOR THE FOUR REPORTS THAT CANNOT BE FIXED BY READING.
//
// Every stub in this engine has no method body, so behaviour is established by
// PROBING, never by inference -- a rule this project earned three separate times.
// Four player reports from v3.14.0 turn on behaviour we cannot see:
//
//   1. "an anti-tank drone, when used by a guest, does not cause damage"
//      SuicideDroneHandler is a SkillEventHandler, not a spawned unit, so the
//      ownership fix does not touch it. The suspicion is the unexplained
//      `IsUsable=True IsUsableOn=True` refusal class already recorded in the
//      handoff for launch_lrm5_indirect. PROBE: every effect handler's OnUse and
//      OnApply on both machines, with the user, the target and who owns it.
//
//   2. "reconnaissance drones do not reveal the fog of war to the other player"
//      PROBE: at every skill use that could grant vision, dump this machine's
//      view of the acting unit -- owner, faction, tile -- so a host/guest pair
//      shows whether the unit exists on both sides at all.
//
//   3. "the impact zone is not shown to the other player, as well as the impact
//      animation itself" (ship support). PROBE: the offmap ability's use, its
//      skill template and target, on both machines.
//
//   4. "if hostages controlled by the host board vehicles controlled by the
//      guest, it triggers desyncs". PROBE: every container enter/leave with BOTH
//      parties' ownership, which is the thing that makes this case different
//      from the rider bug already fixed.
//
// GATED ON A SETTING, DEFAULT OFF, AND STRIPPED BEFORE RELEASE. These are loud
// by design: a probe that samples is a probe that misses the one event you needed.
// Plugin.ProbeBugs must never ship enabled -- CLAUDE.md's rule about dev-harness
// settings applies to this file exactly.
internal static class BugProbes
{
    internal static bool On => Plugin.ProbeBugs != null && Plugin.ProbeBugs.Value;

    // BUGS.md #37: follow every squad that has broken, on both machines. r69's
    // guest lost three fleeing members with no death, right after replaying the
    // host's hit on them. Log each change in its living count, and the moment it
    // leaves the actor list (retreated off the map, or removed).
    private static readonly System.Collections.Generic.Dictionary<IntPtr, (Actor a, string name, int living)> _fleeing = new();
    private static float _fleeCheckAt;
    internal static void TrackFleeing(Actor a)
    {
        try { if (!_fleeing.ContainsKey(a.Pointer)) _fleeing[a.Pointer] = (a, DesyncDetector.TemplateNameOf(a), a.GetElements()?.Count ?? -1); }
        catch { }
    }
    internal static void TickFleeing(TacticalManager tm)
    {
        if (!On || _fleeing.Count == 0 || tm == null) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now < _fleeCheckAt) return;
        _fleeCheckAt = now + 0.25f;
        var present = new System.Collections.Generic.HashSet<IntPtr>();
        foreach (var x in DesyncDetector.AllActors(tm)) if (x != null) present.Add(x.Pointer);
        foreach (var key in new System.Collections.Generic.List<IntPtr>(_fleeing.Keys))
        {
            var (a, name, was) = _fleeing[key];
            if (!present.Contains(key))
            {
                Plugin.Logger.LogWarning($"[PROBE:{Side}] FLEEING '{name}' left the board (had {was} living) remote={Replication.ApplyingRemote} partnersHit={Replication.InPartnersHit}");
                _fleeing.Remove(key);
                continue;
            }
            int now2 = -1;
            try { now2 = a.GetElements()?.Count ?? -1; } catch { }
            if (now2 == was) continue;
            string at = "?";
            try { var p = a.GetLastTile()?.GetTilePos(); if (p != null) at = $"({p.Value.x},{p.Value.y})"; } catch { }
            Plugin.Logger.LogWarning($"[PROBE:{Side}] FLEEING '{name}' at {at} living {was} -> {now2} dying={a.IsDying()} remote={Replication.ApplyingRemote} partnersHit={Replication.InPartnersHit}");
            _fleeing[key] = (a, name, now2);
        }
    }

    // Which side wrote the line. Reading a host/guest pair is the whole method,
    // so every line must say which log it belongs to without cross-referencing.
    private static string Side => CoopNet.Mode == CoopMode.Host ? "H" : (CoopNet.Mode == CoopMode.Guest ? "G" : "-");

    internal static string Who(Actor a)
    {
        if (a == null) return "null";
        try
        {
            string own = !Ownership.Active ? "own?" : (Ownership.IsKnownPlayerUnit(a)
                ? (Ownership.IsLocal(a) ? "MINE" : "PARTNER") : "notplayer");
            string pos = "nowhere";
            try { var t = a.GetLastTile(); if (t != null) { var p = t.GetTilePos(); pos = $"({p.x},{p.y})"; } } catch { }
            return $"'{DesyncDetector.TemplateNameOf(a)}' {own} fac={SafeFac(a)} at {pos}";
        }
        catch (Exception e) { return $"unreadable({e.Message})"; }
    }

    private static int SafeFac(Actor a) { try { return a.GetFactionID(); } catch { return -1; } }

    private static string TileStr(Tile t)
    {
        if (t == null) return "null";
        try { var p = t.GetTilePos(); return $"({p.x},{p.y})"; } catch { return "unreadable"; }
    }

    // ---- 1 + 2: skill effect handlers, on both machines ---------------------
    // OnUse fires on the acting machine AND on the receiver when the skill is
    // replayed, so a host/guest pair tells us whether the effect ran at all on
    // the partner, and with what target.
    internal static void EffectUse(object handler, Actor user, Tile target)
    {
        if (!On) return;
        try
        {
            Plugin.Logger.LogWarning(
                $"[PROBE:{Side}] EFFECT-USE {handler?.GetType()?.Name} user={Who(user)} target={TileStr(target)} "
                + $"remote={Replication.ApplyingRemote}");
        }
        catch { }
    }

    internal static void EffectApply(object handler, Actor user, Tile target, bool isHit)
    {
        if (!On) return;
        try
        {
            Plugin.Logger.LogWarning(
                $"[PROBE:{Side}] EFFECT-APPLY {handler?.GetType()?.Name} user={Who(user)} target={TileStr(target)} "
                + $"isHit={isHit} remote={Replication.ApplyingRemote}");
        }
        catch { }
    }

    // ---- 3: ship support ----------------------------------------------------
    internal static void OffmapUse(OffmapAbilityInstance inst)
    {
        if (!On) return;
        try
        {
            Plugin.Logger.LogWarning(
                $"[PROBE:{Side}] OFFMAP-USE '{inst?.TemplateId}' instance={inst?.InstanceId} "
                + $"remaining={inst?.RemainingUses} skill='{SafeSkill(inst)}' type={SafeType(inst)}");
        }
        catch { }
    }

    private static string SafeSkill(OffmapAbilityInstance i)
    { try { return i?.SkillTemplate?.name ?? "null"; } catch { return "?"; } }

    private static string SafeType(OffmapAbilityInstance i)
    { try { return i == null ? "?" : i.OffmapAbilityType.ToString(); } catch { return "?"; } }

    // ---- 4: cross-owned boarding -------------------------------------------
    // The rider bug was one faction's transport ejecting its own passengers.
    // THIS case is a unit one player owns climbing into a unit the OTHER player
    // owns, which is a different question: who is authoritative for the move
    // afterwards. Both owners are printed for exactly that reason.
    internal static void Boarding(string what, Actor passenger, Actor container, Tile at)
    {
        if (!On) return;
        try
        {
            Plugin.Logger.LogWarning(
                $"[PROBE:{Side}] BOARD-{what} passenger={Who(passenger)} container={Who(container)} at={TileStr(at)} "
                + $"remote={Replication.ApplyingRemote} crossOwned={CrossOwned(passenger, container)}");
        }
        catch { }
    }

    // ---- 5: a truck that empties and vanishes on one machine only ----------
    // 2026-09-25: after special.vehicle_eject_passengers_free the host's empty
    // pirate truck left the roster (EntityTemplate.DespawnIfEmpty) and the
    // guest's stayed. Which call removes it, and does the guest's replay (run
    // with SuppressElementDamage) swallow it? Every death of a NON-player actor
    // is printed with the flags that decide swallowing.
    internal static void Death(string what, Entity e, bool quiet)
    {
        if (!On) return;
        try
        {
            var a = e?.TryCast<Actor>();
            if (a != null && Ownership.Active && Ownership.IsKnownPlayerUnit(a)) return;
            Plugin.Logger.LogWarning(
                $"[PROBE:{Side}] DIE-{what} {Who(a)} quiet={quiet} remote={Replication.ApplyingRemote} "
                + $"suppress={Replication.SuppressElementDamage} swallow={Replication.ShouldSwallowDamage(a)}");
        }
        catch { }
    }

    internal static string WhoEl(Element el)
    {
        try
        {
            var a = el?.GetEntity()?.TryCast<Actor>();
            int sq = -1, slot = -1;
            try { sq = el.GetSquaddieId(); } catch { }
            try { slot = el.GetElementIndex(); } catch { }
            return $"{Who(a)} sq{sq} slot{slot}";
        }
        catch { return "?"; }
    }

    private static string CrossOwned(Actor a, Actor b)
    {
        try
        {
            if (!Ownership.Active || a == null || b == null) return "?";
            bool ka = Ownership.IsKnownPlayerUnit(a), kb = Ownership.IsKnownPlayerUnit(b);
            if (!ka || !kb) return "no(one side is not a player unit)";
            return Ownership.OwnerOf(a) == Ownership.OwnerOf(b) ? "no(same owner)" : "YES";
        }
        catch { return "?"; }
    }
}

// Separate patch class: if any of these hooks fails to bind, ONLY probing is lost
// (SafePatch catches per class). Registered in MelonEntry's patchClasses list, so
// ApiCheck also reports whether these targets still resolve.
[HarmonyPatch]
internal static class BugProbePatches
{
    // The suicide (anti-tank) drone specifically -- report 1.
    [HarmonyPatch(typeof(Menace.Tactical.Skills.Effects.SuicideDroneHandler),
                  nameof(Menace.Tactical.Skills.Effects.SuicideDroneHandler.OnUse))]
    [HarmonyPostfix]
    private static void DroneOnUse(Menace.Tactical.Skills.Effects.SuicideDroneHandler __instance,
                                   Actor _user, Tile _targetTile)
        => BugProbes.EffectUse(__instance, _user, _targetTile);

    [HarmonyPatch(typeof(Menace.Tactical.Skills.Effects.SuicideDroneHandler),
                  nameof(Menace.Tactical.Skills.Effects.SuicideDroneHandler.OnApply))]
    [HarmonyPostfix]
    private static void DroneOnApply(Menace.Tactical.Skills.Effects.SuicideDroneHandler __instance,
                                     Actor _user, Tile _targetTile, bool _isHit)
        => BugProbes.EffectApply(__instance, _user, _targetTile, _isHit);

    // Ship support -- report 3. Prefix so the count shown is the pre-spend one and
    // the line lands before OffmapSync's own relay line.
    [HarmonyPatch(typeof(OffmapAbilityInstance), nameof(OffmapAbilityInstance.ConsumeUse))]
    [HarmonyPrefix]
    private static void OffmapUsed(OffmapAbilityInstance __instance) => BugProbes.OffmapUse(__instance);

    // Morale after a kill -- probe 7 (BUGS.md #19): guest 4 vs host 65 on an enemy
    // whose sister squad had just died. Which machine applies which morale event,
    // and under which flags? Non-player units only; before and after values.
    [HarmonyPatch(typeof(Actor), nameof(Actor.ApplyMorale))]
    [HarmonyPrefix]
    private static void MoraleBefore(Actor __instance, out float __state)
    {
        __state = -1f;
        try { __state = __instance.GetMorale(); } catch { }
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.ApplyMorale))]
    [HarmonyPostfix]
    private static void MoraleAfter(Actor __instance, MoraleEvent _event, float _value, float __state)
    {
        if (!BugProbes.On) return;
        try
        {
            if (Ownership.Active && Ownership.IsKnownPlayerUnit(__instance)) return;
            Plugin.Logger.LogWarning($"[PROBE:{(CoopNet.Mode == CoopMode.Host ? "H" : "G")}] MORALE '{DesyncDetector.TemplateNameOf(__instance)}' {_event} {_value:F0}: {__state:F0} -> {__instance.GetMorale():F0} remote={Replication.ApplyingRemote} suppress={Replication.SuppressElementDamage}");
        }
        catch { }
    }

    // BUGS.md #37: a routing squad vanished on the shooter's machine only (r69),
    // three members gone with no Element death. Which machines see the morale-STATE
    // change, and how many members are alive at that instant? (The snapshot's
    // ROSTER DIFFERS line reports the counts afterwards.)
    [HarmonyPatch(typeof(TacticalManager), nameof(TacticalManager.InvokeOnMoraleStateChanged))]
    [HarmonyPostfix]
    private static void MoraleStateChanged(Actor _actor, MoraleState _moraleState)
    {
        if (!BugProbes.On) return;
        try
        {
            Plugin.Logger.LogWarning($"[PROBE:{(CoopNet.Mode == CoopMode.Host ? "H" : "G")}] MORALE-STATE '{DesyncDetector.TemplateNameOf(_actor)}' -> {_moraleState} morale={_actor?.GetMorale():F0} living={_actor?.GetElements()?.Count ?? -1} dying={_actor?.IsDying()} remote={Replication.ApplyingRemote}");
            if (_actor != null && _moraleState == MoraleState.Fleeing) BugProbes.TrackFleeing(_actor);
        }
        catch { }
    }

    // Empty-truck despawn -- probe 5. Prefixes, so a swallowed death still logs.
    [HarmonyPatch(typeof(Actor), nameof(Actor.Die))]
    [HarmonyPrefix]
    private static void ActorDie(Actor __instance, bool _quiet) => BugProbes.Death("ACTOR", __instance, _quiet);

    [HarmonyPatch(typeof(Element), nameof(Element.Die))]
    [HarmonyPrefix]
    private static void ElementDie(Element __instance)
    {
        Entity e = null;
        try { e = __instance?.GetEntity(); } catch { }
        BugProbes.Death("ELEMENT", e, false);
    }

    // BUGS.md #28, bodies missing on the partner's machine. The engine's own kill
    // never calls Element.Die() (run 30: zero local DIE-ELEMENT lines), while the
    // relay does. Which of these does a local kill run, in what order, and with
    // what DamageInfo? That is the sequence a relayed death must reproduce.
    [HarmonyPatch(typeof(Element), nameof(Element.OnDeath))]
    [HarmonyPrefix]
    private static void ElementOnDeath(Element __instance, Entity _attacker, DamageInfo _damageInfo, int _damageAppliedToElement, Menace.Tactical.Skills.Skill _skill)
    {
        if (!BugProbes.On) return;
        try
        {
            string di = _damageInfo == null ? "null"
                : $"fatality={_damageInfo.FatalityType} vis={_damageInfo.DamageVisualizationType} dmg={_damageInfo.Damage} elDestroyed={_damageInfo.IsElementDestroyed} targetDestroyed={_damageInfo.IsTargetDestroyed} aoe={_damageInfo.IsAoE}";
            Plugin.Logger.LogWarning($"[PROBE:{(CoopNet.Mode == CoopMode.Host ? "H" : "G")}] ELEMENT-ONDEATH {BugProbes.WhoEl(__instance)} attacker={DesyncDetector.TemplateNameOf(_attacker?.TryCast<Actor>())} skill={_skill?.GetID()} applied={_damageAppliedToElement} {di} remote={Replication.ApplyingRemote}");
        }
        catch { }
    }

    [HarmonyPatch(typeof(Element), nameof(Element.OnDeathComplete))]
    [HarmonyPrefix]
    private static void ElementDeathComplete(Element __instance)
    {
        if (!BugProbes.On) return;
        try { Plugin.Logger.LogWarning($"[PROBE:{(CoopNet.Mode == CoopMode.Host ? "H" : "G")}] ELEMENT-DEATHCOMPLETE {BugProbes.WhoEl(__instance)} remote={Replication.ApplyingRemote}"); }
        catch { }
    }

    [HarmonyPatch(typeof(Element), nameof(Element.CleanUpOnDeath))]
    [HarmonyPrefix]
    private static void ElementCleanUp(Element __instance)
    {
        if (!BugProbes.On) return;
        try { Plugin.Logger.LogWarning($"[PROBE:{(CoopNet.Mode == CoopMode.Host ? "H" : "G")}] ELEMENT-CLEANUP {BugProbes.WhoEl(__instance)} remote={Replication.ApplyingRemote}"); }
        catch { }
        // #37: WHO REMOVES A FLEEING SOLDIER ON A CLIENT? A live element (hp > 0)
        // cleaned up outside any relay is an escape this machine decided by itself.
        // The game's own (IL2CPP) stack names the engine entry point to hold.
        try
        {
            if (CoopNet.Mode == CoopMode.Guest && !Replication.ApplyingRemote && __instance.GetHitpoints() > 0
                && _fleeStacks < 6)
            {
                _fleeStacks++;
                var st = new Il2CppSystem.Diagnostics.StackTrace(true);
                Plugin.Logger.LogWarning($"[PROBE:G] FLEE-STACK live element cleaned up locally:\n{st.ToString()}");
                Plugin.Logger.LogWarning($"[PROBE:G] FLEE-STACK managed:\n{Environment.StackTrace}");
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[PROBE:G] FLEE-STACK failed: {e.Message}"); }
    }
    private static int _fleeStacks;
}

// Leader bleed-out (AutoCombat's revive path; the user reports revival never
// happens). Where and when does a leader start bleeding out, on each machine?
// Own class so a signature mismatch only drops this probe.
[HarmonyPatch]
internal static class BleedOutProbe
{
    [HarmonyPatch(typeof(TacticalManager), nameof(TacticalManager.InvokeOnBleedingOut))]
    [HarmonyPostfix]
    private static void Bleeding(Menace.Strategy.BaseUnitLeader _leader, int _remainingRounds)
    {
        if (!BugProbes.On) return;
        try
        {
            Plugin.Logger.LogWarning($"[PROBE:{(CoopNet.Mode == CoopMode.Host ? "H" : "G")}] BLEEDOUT leader='{_leader?.ToString()}' rounds={_remainingRounds} isBleeding={_leader?.IsBleedingOut()} remote={Replication.ApplyingRemote}");
        }
        catch { }
    }
}
