using System;
using HarmonyLib;
using Il2CppMenace.OffmapAbilities;
using Il2CppMenace.States;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling

namespace MenaceCoop;

// EACH PLAYER HAD THEIR OWN SHIP SUPPORT BUDGET.
//
// Player report (v3.14.0): "each player has a separate amount of support per
// operation, the host and the guest have 3 each, and we can call 6 strikes per
// operation together."
//
// That is exactly what the code did. A ship ability is an OffmapAbilityInstance
// carrying its own m_RemainingUses, decremented by the engine's ConsumeUse().
// It is strategy state (it has ProcessSaveState), so when the guest loads the
// host's save both machines start with the same instances and the same counts --
// and then each spends its own copy, because nothing ever crossed the wire. Two
// independent pools, double the artillery.
//
// This is a RULES break rather than a cosmetic desync: it doubles a resource the
// operation is balanced around, and it does it silently.
//
// NOT HOST-ONLY, unlike PerkSync. Either player may legitimately call a strike,
// and the whole point is that both draw from ONE pool, so a use has to travel in
// whichever direction it happened. That follows the engine law that authority
// belongs to the ACTOR, not the network role.
//
// Identity is InstanceId. It is assigned by ActiveOffmapAbilities' own
// m_NextInstanceId counter and serialised with the save, and the guest's strategy
// state IS the host's save, so the two machines agree on it. TemplateId is the
// fallback for the case where an id somehow does not resolve -- never position,
// and never list order.
//
// DELIBERATELY NOT RELAYED: RefillRemainingUses. A refill is derived state (it
// follows from the operation/mission transition, which both machines take from
// the same synced save), and CLAUDE.md's rule is to relay causes, not derived
// state. Relaying refills would also race with the local refill and could hand a
// player more uses than the operation allows -- the same bug in the other
// direction.
internal static class OffmapSync
{
    internal static bool Applying;

    // AN INSTANCE ID IS ONLY TRUSTED IF IT NAMES THE SAME ABILITY HERE. Run 45:
    // the host's instance 18 was a rocket sentry turret while the guest's 18 was
    // the strafing run it called, so the replay deployed a turret and the budget
    // relay charged the turret. Ids agree when both machines loaded the same
    // save; anything granted mid-battle can number differently. Template wins.
    internal static OffmapAbilityInstance Resolve(ActiveOffmapAbilities abilities, int id, string templateId)
    {
        if (abilities == null) return null;
        OffmapAbilityInstance inst = null;
        try { inst = abilities.GetInstance(id); } catch { }
        if (inst != null && (string.IsNullOrEmpty(templateId) || inst.TemplateId == templateId)) return inst;
        if (string.IsNullOrEmpty(templateId)) return null;
        OffmapAbilityInstance byTemplate = null;
        try { byTemplate = abilities.GetInstanceByTemplateId(templateId); } catch { }
        if (inst != null && byTemplate != null)
            Plugin.Logger.LogInfo($"[OCI] Instance {id} is '{inst.TemplateId}' here, not '{templateId}' -- matched by template (instance {byTemplate.InstanceId})");
        return byTemplate;
    }

    internal static void BroadcastUse(OffmapAbilityInstance inst)
    {
        if (Applying) return;
        if (!CoopNet.Connected || inst == null) return;

        // A save being loaded or applied can drive engine state that looks like a
        // player spending a strike. Never relay during a sync -- that would spend
        // the partner's budget for them, on a use that never happened.
        if (SessionSync.SyncInFlight || SessionSync.GuestStillLoading) return;

        try
        {
            int id = inst.InstanceId;
            string tid = inst.TemplateId ?? "";
            CoopNet.Send(new CoopMsg { T = "ociuse", Prm = id, Data = tid });
            Plugin.Logger.LogInfo(
                $"[OCI] Spent '{tid}' (instance {id}) -- {inst.RemainingUses} use(s) left here; telling partner");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[OCI] Could not relay a support use: {e.Message}");
        }
    }

    internal static void HandleRemote(CoopMsg m)
    {
        try
        {
            var abilities = Menace.States.StrategyState.Get()?.ActiveOffmapAbilities;
            if (abilities == null)
            {
                Plugin.Logger.LogWarning("[OCI] Partner spent support but there is no strategy state here to charge");
                return;
            }

            var inst = OffmapSync.Resolve(abilities, m.Prm, m.Data);
            if (inst == null)
            {
                Plugin.Logger.LogWarning(
                    $"[OCI] Partner spent support instance {m.Prm} ('{m.Data}') which does not exist here -- budgets will differ");
                return;
            }

            // Do not go below zero: if the two ever do drift, charging a pool
            // that is already empty would make the difference permanent and
            // invisible. Say so instead.
            if (inst.RemainingUses <= 0)
            {
                Plugin.Logger.LogWarning(
                    $"[OCI] Partner spent '{m.Data}' but it already has no uses left here -- budgets had already drifted");
                return;
            }

            Applying = true;
            try { inst.ConsumeUse(); }
            finally { Applying = false; }

            Plugin.Logger.LogInfo($"[OCI] Partner spent '{m.Data}' (instance {m.Prm}) -- {inst.RemainingUses} use(s) left here");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[OCI] Could not apply the partner's support use: {e.Message}");
        }
    }
}

// Separate patch class so a signature change here disables only the support
// budget sync (SafePatch catches per class) instead of taking anything else with
// it. REMEMBER TO REGISTER IT in MelonEntry's patchClasses list -- a patch class
// that is never handed to SafePatch is silently inert, and ApiCheck now walks
// that same list, so an unregistered class is invisible to the check as well.
[HarmonyPatch]
internal static class OffmapPatches
{
    // Postfix: the count has already gone down, so RemainingUses in the log is
    // the value the player can see, and a refused/failed use never reaches here.
    [HarmonyPatch(typeof(OffmapAbilityInstance), nameof(OffmapAbilityInstance.ConsumeUse))]
    [HarmonyPostfix]
    private static void AfterConsumeUse(OffmapAbilityInstance __instance)
    {
        OffmapSync.BroadcastUse(__instance);
    }
}

// SHIP-SUPPORT STRIKES THEMSELVES (BUGS.md #3b).
//
// Only the budget crossed the wire ("ociuse"); the strike did not. So the
// partner never saw the strike zone or the strike -- and worse, a strike the
// GUEST called hit nothing at all: its damage has no attacker, damage without
// one is the host's to decide (Replication.IsPartnersHit), so the guest's copy
// was swallowed and the host never ran the strike to decide it.
//
// Relay the whole player action -- OffmapAbilityAction.Execute(tile), which both
// immediate and delayed strikes go through -- and run it on the partner too. The
// existing hit ownership then settles damage exactly once (the host's copy, sent
// as ehp), and both machines hold the scheduled strike, so both see its zone.
// The replay must not spend a use: the sender's "ociuse" already charges the
// shared pool, so ConsumeUse is skipped while replaying.
internal static class OffmapStrikeSync
{
    internal static bool Replaying;
    // Inside OnTilesSelected: an Execute it makes internally must not send twice.
    internal static int SelectDepth;

    // THE PATH THE ENGINE ACTUALLY USES (run 44): a strafing run fires from
    // OffmapAbilityAction.OnTilesSelected(tiles) -- the click alone did nothing
    // and Execute(tile) never spent a use. Relay the selected tiles.
    internal static void BroadcastTiles(OffmapAbilityAction action, Il2CppSystem.Collections.Generic.List<Il2CppMenace.Tactical.Tile> tiles)
    {
        if (Replaying || SelectDepth > 0 || !CoopNet.Connected || tiles == null || tiles.Count == 0) return;
        try
        {
            if (Il2CppMenace.Tactical.TacticalManager.Get() == null) return;
            var inst = action?.m_OffmapAbilityInstance;
            if (inst == null) return;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < tiles.Count; i++)
            {
                var p = tiles[i]?.GetTilePos();
                if (p == null) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(p.Value.x).Append(':').Append(p.Value.y);
            }
            CoopNet.Send(new CoopMsg { T = "ocitiles", Prm = inst.InstanceId, SkillId = inst.TemplateId ?? "", Data = sb.ToString() });
            Plugin.Logger.LogInfo($"[OCI] Strike '{inst.TemplateId}' (instance {inst.InstanceId}) on {tiles.Count} tile(s) [{sb}] -- telling partner");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[OCI] Strike relay failed: {e.Message}"); }
    }

    internal static void BroadcastClick(OffmapAbilityAction action, Il2CppMenace.Tactical.Tile tile)
    {
        if (Replaying || !CoopNet.Connected || tile == null) return;
        try
        {
            if (Il2CppMenace.Tactical.TacticalManager.Get() == null) return;
            var inst = action?.m_OffmapAbilityInstance;
            if (inst == null) return;
            var p = tile.GetTilePos();
            CoopNet.Send(new CoopMsg { T = "ociclick", Prm = inst.InstanceId, SkillId = inst.TemplateId ?? "", TX = p.x, TY = p.y });
            Plugin.Logger.LogInfo($"[OCI] Ability '{inst.TemplateId}' (instance {inst.InstanceId}) placed at ({p.x},{p.y}) -- telling partner");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[OCI] Click relay failed: {e.Message}"); }
    }

    internal static void HandleClick(CoopMsg m)
    {
        try
        {
            var abilities = Il2CppMenace.States.StrategyState.Get()?.ActiveOffmapAbilities;
            var inst = OffmapSync.Resolve(abilities, m.Prm, m.SkillId);
            var tile = Replication.GetTileAt(m.TX, m.TY);
            if (inst == null || tile == null)
            {
                Plugin.Logger.LogWarning($"[OCI] Partner's '{m.SkillId}' at ({m.TX},{m.TY}) cannot be replayed here (instance={(inst != null)} tile={(tile != null)})");
                return;
            }
            Replaying = true;
            OffmapSync.Applying = true;
            SelectDepth++;   // a selection it opens must not relay back
            try { new OffmapAbilityAction(inst).HandleLeftClickOnTile(tile, null); }
            finally { Replaying = false; OffmapSync.Applying = false; if (SelectDepth > 0) SelectDepth--; }
            Plugin.Logger.LogInfo($"[OCI] Replayed partner's '{m.SkillId}' at ({m.TX},{m.TY})");
        }
        catch (Exception e) { Replaying = false; OffmapSync.Applying = false; Plugin.Logger.LogWarning($"[OCI] Replaying the partner's '{m.SkillId}' threw: {e.GetType().Name}: {e.Message}"); }
    }

    internal static void HandleTiles(CoopMsg m)
    {
        try
        {
            var abilities = Il2CppMenace.States.StrategyState.Get()?.ActiveOffmapAbilities;
            var inst = OffmapSync.Resolve(abilities, m.Prm, m.SkillId);
            var list = new Il2CppSystem.Collections.Generic.List<Il2CppMenace.Tactical.Tile>();
            foreach (var xy in (m.Data ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = xy.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y))
                {
                    var t = Replication.GetTileAt(x, y);
                    if (t != null) list.Add(t);
                }
            }
            if (inst == null || list.Count == 0)
            {
                Plugin.Logger.LogWarning($"[OCI] Partner's strike '{m.SkillId}' on [{m.Data}] cannot be replayed here (instance={(inst != null)} tiles={list.Count})");
                return;
            }
            Replaying = true;
            OffmapSync.Applying = true;
            try { new OffmapAbilityAction(inst).OnTilesSelected(list); }
            finally { Replaying = false; OffmapSync.Applying = false; }
            Plugin.Logger.LogInfo($"[OCI] Replayed partner's strike '{m.SkillId}' on {list.Count} tile(s)");
        }
        catch (Exception e) { Replaying = false; OffmapSync.Applying = false; Plugin.Logger.LogWarning($"[OCI] Replaying the partner's strike threw: {e.GetType().Name}: {e.Message}"); }
    }

    internal static void Broadcast(OffmapAbilityAction action, Il2CppMenace.Tactical.Tile tile)
    {
        if (Replaying || SelectDepth > 0 || !CoopNet.Connected || tile == null) return;
        try
        {
            if (Il2CppMenace.Tactical.TacticalManager.Get() == null) return;
            var inst = action?.m_OffmapAbilityInstance;
            if (inst == null) return;
            var p = tile.GetTilePos();
            CoopNet.Send(new CoopMsg { T = "ociexec", Prm = inst.InstanceId, Data = inst.TemplateId ?? "", TX = p.x, TY = p.y });
            Plugin.Logger.LogInfo($"[OCI] Strike '{inst.TemplateId}' (instance {inst.InstanceId}) at ({p.x},{p.y}) -- telling partner");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[OCI] Strike relay failed: {e.Message}"); }
    }

    internal static void HandleRemote(CoopMsg m)
    {
        try
        {
            var abilities = Il2CppMenace.States.StrategyState.Get()?.ActiveOffmapAbilities;
            var inst = OffmapSync.Resolve(abilities, m.Prm, m.Data);
            var tile = Replication.GetTileAt(m.TX, m.TY);
            if (inst == null || tile == null)
            {
                Plugin.Logger.LogWarning($"[OCI] Partner's strike '{m.Data}' at ({m.TX},{m.TY}) cannot be replayed here (instance={(inst != null)} tile={(tile != null)})");
                return;
            }
            Replaying = true;
            OffmapSync.Applying = true;
            try { new OffmapAbilityAction(inst).Execute(tile); }
            finally { Replaying = false; OffmapSync.Applying = false; }
            Plugin.Logger.LogInfo($"[OCI] Replayed partner's strike '{m.Data}' at ({m.TX},{m.TY})");
        }
        catch (Exception e) { Replaying = false; OffmapSync.Applying = false; Plugin.Logger.LogWarning($"[OCI] Replaying the partner's strike threw: {e.GetType().Name}: {e.Message}"); }
    }
}

[HarmonyPatch]
internal static class OffmapStrikePatches
{
    // Every spawn in a battle passes through here (crates, turrets, troops,
    // cover, wrecks, drones). One line per spawn on each machine, so the two
    // logs can be diffed: same template, same tile, same faction, on both.
    [HarmonyPatch(typeof(Il2CppMenace.Tactical.TacticalManager), nameof(Il2CppMenace.Tactical.TacticalManager.InvokeOnEntitySpawned))]
    [HarmonyPostfix]
    private static void EntitySpawned(Il2CppMenace.Tactical.Entity _entity)
    {
        if (!CoopNet.Connected) return;
        try
        {
            var a = _entity?.TryCast<Il2CppMenace.Tactical.Actor>();
            var p = (a?.GetLastTile() ?? _entity?.GetTile())?.GetTilePos();
            string name = a != null ? DesyncDetector.TemplateNameOf(a) : (_entity?.ToString() ?? "?");
            int fac = -1;
            try { fac = a?.GetFactionID() ?? -1; } catch { }
            Plugin.Logger.LogInfo($"[SPAWNED] {name} at ({p?.x},{p?.y}) fac={fac} actor={(a != null)} replaying={OffmapStrikeSync.Replaying} remote={Replication.ApplyingRemote}");
        }
        catch { }
    }

    // THE CLICK IS THE OTHER ENTRY POINT (run 47). Single-tile abilities --
    // supply drop, sentry turrets, airdropped troops, cover -- are spent by
    // HandleLeftClickOnTile itself, so relaying only OnTilesSelected left every
    // one of them on the caller's machine alone (host-only conscripts whose
    // orders the guest could not resolve; a guest-only turret). Relay the click
    // when it SPENT a use; a click that only opens area selection spends
    // nothing and the selection relays itself. Selection nested inside a click
    // must not send a second copy.
    [HarmonyPatch(typeof(OffmapAbilityAction), nameof(OffmapAbilityAction.HandleLeftClickOnTile))]
    [HarmonyPrefix]
    private static void BeforeClick(OffmapAbilityAction __instance, out int __state)
    {
        __state = -1;
        try { __state = __instance?.m_OffmapAbilityInstance?.RemainingUses ?? -1; } catch { }
        OffmapStrikeSync.SelectDepth++;
    }

    [HarmonyPatch(typeof(OffmapAbilityAction), nameof(OffmapAbilityAction.HandleLeftClickOnTile))]
    [HarmonyFinalizer]
    private static void AfterClick(OffmapAbilityAction __instance, Il2CppMenace.Tactical.Tile _tile, int __state)
    {
        if (OffmapStrikeSync.SelectDepth > 0) OffmapStrikeSync.SelectDepth--;
        try
        {
            int now = __instance?.m_OffmapAbilityInstance?.RemainingUses ?? -1;
            if (__state >= 0 && now >= 0 && now < __state) OffmapStrikeSync.BroadcastClick(__instance, _tile);
        }
        catch { }
    }

    [HarmonyPatch(typeof(OffmapAbilityAction), nameof(OffmapAbilityAction.Execute))]
    [HarmonyPrefix]
    private static void BeforeExecute(OffmapAbilityAction __instance, Il2CppMenace.Tactical.Tile _currentTile)
        => OffmapStrikeSync.Broadcast(__instance, _currentTile);

    // The replay never spends: the sender's own ConsumeUse is relayed as "ociuse".
    [HarmonyPatch(typeof(OffmapAbilityInstance), nameof(OffmapAbilityInstance.ConsumeUse))]
    [HarmonyPrefix]
    private static bool SkipConsumeWhileReplaying() => !OffmapStrikeSync.Replaying;

    // Probe: where a delayed strike is actually scheduled (the zone the partner
    // must also see).
    [HarmonyPatch(typeof(DelayedOffmapAbilities), nameof(DelayedOffmapAbilities.Schedule))]
    [HarmonyPrefix]
    private static void Scheduled(DelayedOffmapAbilities __instance, OffmapAbilityInstance _instance, Il2CppMenace.Tactical.Tile _target)
    {
        DelayedOffmapSync.BeforeSchedule(__instance);
        try
        {
            var p = _target?.GetTilePos();
            Plugin.Logger.LogInfo($"[OCI] Schedule '{_instance?.TemplateId}' at ({p?.x},{p?.y}) (replaying={OffmapStrikeSync.Replaying})");
        }
        catch { }
    }

    [HarmonyPatch(typeof(DelayedOffmapAbilities), nameof(DelayedOffmapAbilities.Schedule))]
    [HarmonyPostfix]
    private static void AfterScheduled(DelayedOffmapAbilities __instance) => DelayedOffmapSync.AfterSchedule(__instance);

    // Probe: area strikes pick tiles through a second path; log it so a run shows
    // whether any strike bypasses Execute.
    [HarmonyPatch(typeof(OffmapAbilityAction), nameof(OffmapAbilityAction.OnTilesSelected))]
    [HarmonyPrefix]
    private static void TilesSelected(OffmapAbilityAction __instance, Il2CppSystem.Collections.Generic.List<Il2CppMenace.Tactical.Tile> _selectedTiles)
    {
        OffmapStrikeSync.BroadcastTiles(__instance, _selectedTiles);
        OffmapStrikeSync.SelectDepth++;
    }

    [HarmonyPatch(typeof(OffmapAbilityAction), nameof(OffmapAbilityAction.OnTilesSelected))]
    [HarmonyFinalizer]
    private static void TilesSelectedDone() { if (OffmapStrikeSync.SelectDepth > 0) OffmapStrikeSync.SelectDepth--; }
}

// DELAYED SHIP ABILITIES ARE HOST-DRIVEN (BUGS.md #35).
//
// A delayed ability (turrets, troops, cover, supply drops, AirPower runs)
// advances on DelayedOffmapAbilities.OnTurnEnd / OnRoundStart and fires when its
// progress completes. Each machine advanced its own copy on its own turn ends, so
// it fired at a different moment and with different RNG state -- run 49: the
// rocket turret arrived 33 s later on the guest (the host's orders for it found
// nothing) and both turrets landed a tile apart.
//
// Same answer as the AI: only the host advances them. Each host call is relayed
// with a fresh RNG seed; the guest ignores its own and replays the host's, so the
// ability fires at the same point in the turn order, from the same RNG state.
internal static class DelayedOffmapSync
{
    internal static bool Applying;

    private static bool Active(Il2CppMenace.Tactical.TacticalManager tm)
    {
        // Every tick while connected: an ability scheduled mid-turn must see the
        // same sequence of ticks on both machines from its first one.
        return CoopNet.Connected && tm != null && tm.GetDelayedOffmapAbilities() != null;
    }

    // Host prefix: seed and relay. Guest prefix: refuse unless replaying.
    internal static bool BeforeTurnEnd(Il2CppMenace.Tactical.Actor actor, bool isActive)
    {
        if (!CoopNet.Connected || Il2CppMenace.Tactical.TacticalManager.Get() == null) return true;
        // Guest: the host runs every unit's turn end, the guest's included
        // (Replication.EnsureTurnEnded), and relays the tick; never our own.
        if (CoopNet.Mode == CoopMode.Guest) return Applying;
        var tm = Il2CppMenace.Tactical.TacticalManager.Get();
        if (!Active(tm)) return true;
        if (_pending != null) return true;   // nested inside another tick: that one relays
        int seed = Replication.NewSeed();
        Replication.Reseed(seed);
        var msg = new CoopMsg { T = "ocitick", Seed = seed, Prm = isActive ? 1 : 0, MoveFlags = 1 };
        try { var p = actor?.GetLastTile()?.GetTilePos(); msg.AX = p?.x ?? -1; msg.AY = p?.y ?? -1; } catch { }
        Replication.StampIds(msg, actor, isOrder: false);
        // Sent from the POSTFIX, with the per-ability arguments this call hands
        // out captured on the way (AbilityTurnEnd below).
        _pending = msg;
        _cap = new System.Text.StringBuilder();
        return true;
    }

    // THE TICK'S ARGUMENT IS THIS MACHINE'S BOARD (run 52).
    //
    // Replaying the host's tick was not enough: each ability's OnTurnEnd is
    // handed "the fraction of units that have finished their turn", which the
    // manager counts on the board of the machine running it. The guest ran the
    // host's tick a few ms later with its own count, progressed less, and its
    // rocket turret came down 21 s (one tick) after the host's, a tile away.
    // So the host captures what each ability was handed and the guest's replay
    // hands over exactly that.
    private static CoopMsg _pending;
    private static System.Text.StringBuilder _cap;
    private static readonly System.Collections.Generic.Queue<(float pct, int delay, int avail)> _replay = new();
    private static int _replayed;

    internal static void AfterTurnEnd()
    {
        var msg = _pending;
        _pending = null;
        if (msg == null) return;
        msg.SkillId = _cap?.ToString() ?? "";
        _cap = null;
        CoopNet.Send(msg);
        if (msg.SkillId.Length > 0) Plugin.Logger.LogInfo($"[OCI] Delayed tick -> partner ({msg.AX},{msg.AY}): {msg.SkillId} | {Describe()}");
    }

    internal static void AbilityTurnEnd(ref float pct, ref int delay, ref int avail)
    {
        if (!CoopNet.Connected) return;
        if (CoopNet.Mode == CoopMode.Host)
        {
            _cap?.Append(System.FormattableString.Invariant($"{pct:R};{delay};{avail}|"));
            return;
        }
        if (!Applying || _replay.Count == 0) return;
        var v = _replay.Dequeue();
        pct = v.pct; delay = v.delay; avail = v.avail;
        _replayed++;
    }

    // THE THRESHOLD IS ALSO THIS MACHINE'S BOARD (run 53).
    //
    // Identical tick arguments still left the guest behind: per tick the host's
    // supply drop gained pct*0.75, the guest's pct*0.625. The difference is set
    // when the ability is SCHEDULED -- the constructor takes a "units finished
    // turn" requirement read off the board at that instant, and the guest's
    // replayed click sees a different board. So the host sends each new entry's
    // requirement (whoever clicked), and the guest adopts it by list index.
    private static int _schedBefore;
    private static readonly System.Collections.Generic.Dictionary<int, (string tmpl, float req)> _schedPending = new();

    private static string TemplateOf(DelayedOffmapAbility a)
    {
        try { return a?.GetTemplate()?.name ?? "?"; } catch { return "?"; }
    }

    internal static void BeforeSchedule(DelayedOffmapAbilities d)
    {
        _inSchedule = true;
        try { _schedBefore = d?.m_Abilities?.Count ?? 0; } catch { _schedBefore = 0; }
    }

    internal static void AfterSchedule(DelayedOffmapAbilities d)
    {
        _inSchedule = false;
        if (!CoopNet.Connected || d?.m_Abilities == null) return;
        var list = d.m_Abilities;
        if (CoopNet.Mode == CoopMode.Host)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = _schedBefore; i < list.Count; i++)
                if (list[i] != null) sb.Append(System.FormattableString.Invariant($"{i};{TemplateOf(list[i])};{list[i].m_ReqUnitsFinishedTurnPct:R}|"));
            if (sb.Length == 0) return;
            CoopNet.Send(new CoopMsg { T = "ocisched", SkillId = sb.ToString() });
            Plugin.Logger.LogInfo($"[OCI] Scheduled -> partner: {sb}");
            return;
        }
        for (int i = _schedBefore; i < list.Count; i++)
            if (_schedPending.TryGetValue(i, out var v)) { Adopt(list, i, v.tmpl, v.req); _schedPending.Remove(i); }
    }

    internal static void HandleSched(CoopMsg m)
    {
        var list = Il2CppMenace.Tactical.TacticalManager.Get()?.GetDelayedOffmapAbilities()?.m_Abilities;
        foreach (var part in (m.SkillId ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = part.Split(';');
            if (f.Length != 3 || !int.TryParse(f[0], out int i)) continue;
            float req = float.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture);
            // Host clicked: this arrives before the replayed click schedules it
            // here. Guest clicked: it is already in the list.
            if (list != null && i < list.Count && TemplateOf(list[i]) == f[1]) Adopt(list, i, f[1], req);
            else _schedPending[i] = (f[1], req);
        }
    }

    private static void Adopt(Il2CppSystem.Collections.Generic.List<DelayedOffmapAbility> list, int i, string tmpl, float req)
    {
        var a = list[i];
        if (a == null || TemplateOf(a) != tmpl)
        {
            Plugin.Logger.LogWarning($"[OCI] Host's schedule #{i} '{tmpl}' does not match '{TemplateOf(a)}' here -- not adopted");
            return;
        }
        float mine = a.m_ReqUnitsFinishedTurnPct;
        a.m_ReqUnitsFinishedTurnPct = req;
        Plugin.Logger.LogInfo(System.FormattableString.Invariant($"[OCI] Adopted host's requirement for #{i} '{tmpl}': {mine:R} -> {req:R}"));
    }

    internal static string Describe()
    {
        try
        {
            var list = Il2CppMenace.Tactical.TacticalManager.Get()?.GetDelayedOffmapAbilities()?.m_Abilities;
            if (list == null || list.Count == 0) return "none pending";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (a == null) continue;
                string id = "?";
                try { id = a.GetTemplate()?.name ?? "?"; } catch { }
                sb.Append(System.FormattableString.Invariant($"{id.Replace("offmap_ability.", "")}:{a.m_CurrentState}/r{a.m_ProgressInRounds}/{a.m_ProgressPercentage:0.###}/req{a.m_ReqUnitsFinishedTurnPct:0.###} "));
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception e) { return "describe threw " + e.Message; }
    }

    internal static bool BeforeRoundStart(int round)
    {
        if (!CoopNet.Connected || Il2CppMenace.Tactical.TacticalManager.Get() == null) return true;
        if (CoopNet.Mode == CoopMode.Guest) return Applying;
        var tm = Il2CppMenace.Tactical.TacticalManager.Get();
        if (!Active(tm)) return true;
        int seed = Replication.NewSeed();
        Replication.Reseed(seed);
        CoopNet.Send(new CoopMsg { T = "ocitick", Seed = seed, Prm = round, MoveFlags = 2 });
        _inRoundStart = true;
        Plugin.Logger.LogInfo($"[OCI] Delayed round-start {round} -> partner | {Describe()}");
        return true;
    }

    // UPDATEABILITIES IS THE OTHER TRIGGER (run 55).
    //
    // Besides each tick, the engine calls UpdateAbilities on its own -- at a
    // faction switch, for one -- and that recomputes progress from this
    // machine's board and fires anything ready. The guest's local switch fired
    // its supply drop 2 s before the host's. So outside a tick, a schedule or a
    // replay (where the host's matching call happens in the same place) only
    // the host runs it, and relays it seeded ("ociupd").
    private static bool _inSchedule, _inRoundStart;
    private static int _updLogged, _updBlocked;
    private static string _updBefore;

    private static bool Nested => _pending != null || Applying || _inSchedule || _inRoundStart || OffmapStrikeSync.Replaying;

    internal static void EndRoundStart() => _inRoundStart = false;

    internal static bool BeforeUpdate()
    {
        _updBefore = null;
        if (!CoopNet.Connected || Il2CppMenace.Tactical.TacticalManager.Get() == null) return true;
        string s = Describe();
        if (s == "none pending") return true;
        if (!Nested)
        {
            if (CoopNet.Mode == CoopMode.Guest)
            {
                if (_updBlocked++ < 20) Plugin.Logger.LogInfo($"[OCI] Held a local UpdateAbilities -- the host's is relayed | {s}");
                return false;
            }
            int seed = Replication.NewSeed();
            Replication.Reseed(seed);
            CoopNet.Send(new CoopMsg { T = "ociupd", Seed = seed });
        }
        if (_updLogged < 80) _updBefore = s;
        return true;
    }

    internal static void HandleUpdate(CoopMsg m)
    {
        var d = Il2CppMenace.Tactical.TacticalManager.Get()?.GetDelayedOffmapAbilities();
        if (d == null) return;
        Applying = true;
        bool prev = Replication.ApplyingRemote;
        Replication.ApplyingRemote = true;
        try { Replication.Reseed(m.Seed); d.UpdateAbilities(); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[OCI] Relayed UpdateAbilities threw: {e.GetType().Name}: {e.Message}"); }
        finally { Applying = false; Replication.ApplyingRemote = prev; }
    }

    internal static void AfterUpdate()
    {
        if (_updBefore == null || !BugProbes.On) return;
        string after = Describe();
        bool changed = after != _updBefore;
        if (!changed && _updLogged >= 30) return;
        _updLogged++;
        Plugin.Logger.LogInfo($"[OCI] UpdateAbilities tick={_pending != null} applying={Applying} sched={_inSchedule} roundStart={_inRoundStart} replaying={OffmapStrikeSync.Replaying} changed={changed} | {_updBefore}{(changed ? " => " + after : "")}");
    }

    internal static void HandleRemote(CoopMsg m)
    {
        var tm = Il2CppMenace.Tactical.TacticalManager.Get();
        var d = tm?.GetDelayedOffmapAbilities();
        if (d == null) return;
        Applying = true;
        bool prev = Replication.ApplyingRemote;
        Replication.ApplyingRemote = true;
        try
        {
            Replication.Reseed(m.Seed);
            if (m.MoveFlags == 2) { d.OnRoundStart(m.Prm); Plugin.Logger.LogInfo($"[OCI] Delayed round-start {m.Prm} from host | {Describe()}"); }
            else
            {
                var actor = Replication.ResolveOrderActor(m);
                if (actor == null) { Plugin.Logger.LogWarning($"[OCI] Delayed-ability tick for an unknown actor at ({m.AX},{m.AY}) -- skipped"); return; }
                _replay.Clear();
                _replayed = 0;
                int sent = 0;
                foreach (var part in (m.SkillId ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    var f = part.Split(';');
                    if (f.Length != 3) continue;
                    sent++;
                    _replay.Enqueue((float.Parse(f[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(f[1]), int.Parse(f[2])));
                }
                d.OnTurnEnd(actor, m.Prm != 0);
                if (sent > 0)
                    Plugin.Logger.LogInfo($"[OCI] Delayed tick from host ({m.AX},{m.AY}): {m.SkillId} (used {_replayed}/{sent}{(_replay.Count > 0 ? ", " + _replay.Count + " LEFT OVER" : "")}) | {Describe()}");
                _replay.Clear();
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[OCI] Delayed-ability tick threw: {e.GetType().Name}: {e.Message}"); }
        finally { Applying = false; Replication.ApplyingRemote = prev; }
    }
}

[HarmonyPatch]
internal static class DelayedOffmapPatches
{
    [HarmonyPatch(typeof(DelayedOffmapAbilities), nameof(DelayedOffmapAbilities.OnTurnEnd))]
    [HarmonyPrefix]
    private static bool TurnEnd(Il2CppMenace.Tactical.Actor _actor, bool _isActiveActor)
        => DelayedOffmapSync.BeforeTurnEnd(_actor, _isActiveActor);

    [HarmonyPatch(typeof(DelayedOffmapAbilities), nameof(DelayedOffmapAbilities.UpdateAbilities))]
    [HarmonyPrefix]
    private static bool UpdateGate() => DelayedOffmapSync.BeforeUpdate();

    [HarmonyPatch(typeof(DelayedOffmapAbilities), nameof(DelayedOffmapAbilities.UpdateAbilities))]
    [HarmonyFinalizer]
    private static void UpdateProbeDone() => DelayedOffmapSync.AfterUpdate();

    [HarmonyPatch(typeof(DelayedOffmapAbilities), nameof(DelayedOffmapAbilities.OnTurnEnd))]
    [HarmonyFinalizer]
    private static void TurnEndDone() => DelayedOffmapSync.AfterTurnEnd();

    [HarmonyPatch(typeof(DelayedOffmapAbility), nameof(DelayedOffmapAbility.OnTurnEnd))]
    [HarmonyPrefix]
    private static void AbilityTurnEnd(ref float _unitsFinishedTurnPct, ref int _delayInRounds, ref int _availableDurationInRounds)
        => DelayedOffmapSync.AbilityTurnEnd(ref _unitsFinishedTurnPct, ref _delayInRounds, ref _availableDurationInRounds);

    [HarmonyPatch(typeof(DelayedOffmapAbilities), nameof(DelayedOffmapAbilities.OnRoundStart))]
    [HarmonyPrefix]
    private static bool RoundStart(int _round) => DelayedOffmapSync.BeforeRoundStart(_round);

    [HarmonyPatch(typeof(DelayedOffmapAbilities), nameof(DelayedOffmapAbilities.OnRoundStart))]
    [HarmonyFinalizer]
    private static void RoundStartDone() => DelayedOffmapSync.EndRoundStart();
}
