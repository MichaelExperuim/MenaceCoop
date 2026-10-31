using HarmonyLib;
using Il2CppMenace.States;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;
using Il2CppMenace.UI;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling

namespace MenaceCoop;

// Phase 0: passive instrumentation. Logs every player order and turn transition
// so we can verify these are the right hook points for command replication.
[HarmonyPatch]
internal static class InstrumentationPatches
{
    private static string TileStr(Tile tile)
    {
        if (tile == null) return "<null>";
        var p = tile.GetTilePos();
        return $"({p.x},{p.y})";
    }

    private static string ActorStr(Actor actor)
    {
        if (actor == null) return "<null>";
        return $"{actor.GetType().Name}@{TileStr(actor.GetLastTile())}";
    }

    [HarmonyPatch(typeof(Skill), nameof(Skill.Use))]
    [HarmonyPrefix]
    private static void SkillUse(Skill __instance, Tile _targetTile, UsageParameter _parameters)
    {
        Plugin.Logger.LogInfo($"[ORDER] Skill.Use target={TileStr(_targetTile)} params={_parameters} skill={__instance?.ToString() ?? "<null>"}");
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.MoveTo))]
    [HarmonyPrefix]
    private static void MoveTo(Actor __instance, Tile _tile, ref MovementAction _action, MovementFlags _flags)
    {
        Plugin.Logger.LogInfo($"[ORDER] Actor.MoveTo actor={ActorStr(__instance)} dest={TileStr(_tile)} action={_action} flags={_flags}");
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.SkipTurn))]
    [HarmonyPrefix]
    private static void SkipTurn(Actor __instance)
    {
        Plugin.Logger.LogInfo($"[ORDER] Actor.SkipTurn actor={ActorStr(__instance)}");
    }

    [HarmonyPatch(typeof(TacticalManager), nameof(TacticalManager.NextFaction))]
    [HarmonyPrefix]
    private static void NextFaction(TacticalManager __instance)
    {
        Plugin.Logger.LogInfo($"[TURN] NextFaction (round {__instance.GetRound()}, activeFaction {__instance.GetActiveFactionID()})");
    }

    [HarmonyPatch(typeof(TacticalManager), nameof(TacticalManager.NextRound))]
    [HarmonyPrefix]
    private static void NextRound(TacticalManager __instance)
    {
        Plugin.Logger.LogInfo($"[TURN] NextRound -> {__instance.GetRound() + 1}");
    }

    // End-turn chain forensics: reveals what the End Turn button and End All
    // actually call, so replication can hook the right level.
    [HarmonyPatch(typeof(TacticalState), nameof(TacticalState.EndTurn))]
    [HarmonyPrefix]
    private static void EndTurn()
    {
        Plugin.Logger.LogInfo("[TURN] TacticalState.EndTurn() called");
    }

    [HarmonyPatch(typeof(UITactical), nameof(UITactical.EndTurnAll))]
    [HarmonyPrefix]
    private static void EndTurnAll()
    {
        Plugin.Logger.LogInfo("[TURN] UITactical.EndTurnAll() called");
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.SetTurnDone))]
    [HarmonyPrefix]
    private static void SetTurnDone(Actor __instance, bool _turnDone)
    {
        var p = __instance.GetLastTile()?.GetTilePos();
        Plugin.Logger.LogInfo($"[TURN] SetTurnDone({_turnDone}) actor@({p?.x},{p?.y}) remote={Replication.ApplyingRemote} explicitEnd={Replication.InExplicitEndTurn}");
    }

    // AP forensics: log the acting unit's AP after every real (non-fake) skill
    // use and every move, so host/guest logs can be diffed to find where AP
    // diverges.
    [HarmonyPatch(typeof(Skill), nameof(Skill.Use))]
    [HarmonyPostfix]
    private static void LogSkillAP(Skill __instance, Tile _targetTile, UsageParameter _parameters)
    {
        if ((_parameters & UsageParameter.Fake) != 0) return;
        var actor = __instance.GetActor();
        if (actor == null) return;
        var p = actor.GetLastTile()?.GetTilePos();
        Plugin.Logger.LogInfo($"[AP] after Skill.Use({__instance.GetID()}, params={_parameters}) actor@({p?.x},{p?.y}) AP={actor.GetActionPoints()} turnDone={actor.IsTurnDone()} remote={Replication.ApplyingRemote}");
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.MoveTo))]
    [HarmonyPostfix]
    private static void LogMoveAP(Actor __instance, Tile _tile)
    {
        Plugin.Logger.LogInfo($"[AP] after MoveTo dest=({_tile?.GetTilePos().x},{_tile?.GetTilePos().y}) AP={__instance.GetActionPoints()} turnDone={__instance.IsTurnDone()} remote={Replication.ApplyingRemote}");
    }
}
