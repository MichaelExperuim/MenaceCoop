using System;
using System.Collections.Generic;
using UnityEngine;
#if MELON
using Il2CppMenace.Tactical;
using Il2CppMenace.States;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.Tactical;
using Menace.States;
#endif

namespace MenaceCoop;

// SYNCED DEBUG: TEST HOSTAGES, CONDITIONS, SHIP SUPPORT WITHOUT WAITING FOR A MAP
// TO HAND THEM TO YOU (user request, 2026-09-25).
//
// The game's own cheat menu (F2, unlocked here too) acts on ONE machine, and in
// co-op anything one machine does alone is a desync. So these commands are run
// by BOTH machines, in the host's order:
//
//   the host runs a command and broadcasts it ("dbg"); the guest runs the same
//   engine call on receipt. A guest's button sends a request ("dbgreq") and the
//   host runs it -- so there is one ordering, the host's.
//
// Spawns need no identity code of their own: every arrival passes through
// BaseFaction.AddActor, where co-op already gives mid-battle arrivals their
// arrival-ordinal id, and both machines spawn in the same order.
//
// DEV ONLY, BOTH SIDES. [MenaceCoop_Dev] SyncedDebug must be on on BOTH machines;
// a machine with it off refuses commands and says so. Default off, never shipped on.
internal static class DebugSync
{
    internal static bool LocalOn => Plugin.SyncedDebug != null && Plugin.SyncedDebug.Value;

    // ---- template picker ----------------------------------------------------
    private static List<string> _templates = new();
    private static int _pick;

    internal static string Picked => _templates.Count == 0 ? "(none loaded)" : _templates[_pick % _templates.Count];

    internal static void Cycle(int dir)
    {
        if (_templates.Count == 0) RefreshTemplates();
        if (_templates.Count == 0) return;
        _pick = ((_pick + dir) % _templates.Count + _templates.Count) % _templates.Count;
    }

    // Templates the game has LOADED -- everything this battle and its mission can
    // field, plus whatever else is resident. Filtered to things worth spawning.
    internal static void RefreshTemplates()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<EntityTemplate>());
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i]?.TryCast<EntityTemplate>();
                string n = t?.name;
                if (string.IsNullOrEmpty(n)) continue;
                if (n.StartsWith("enemy.") || n.StartsWith("civilian") || n.StartsWith("allied.")
                    || n.Contains("hostage") || n.Contains("vip") || n.StartsWith("player_"))
                    names.Add(n);
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[DEBUG] template scan failed: " + e.Message); }
        _templates = new List<string>(names);
        if (_pick >= _templates.Count) _pick = 0;
        Plugin.Logger.LogInfo($"[DEBUG] {_templates.Count} spawnable template(s) loaded");
    }

    private static T FindByName<T>(string name) where T : UnityEngine.Object
    {
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<T>());
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i]?.TryCast<T>();
                if (t != null && t.name == name) return t;
            }
        }
        catch { }
        return null;
    }

    internal static List<string> Names<T>(Func<string, bool> keep) where T : UnityEngine.Object
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<T>());
            for (int i = 0; i < all.Length; i++)
            {
                string n = all[i]?.name;
                if (!string.IsNullOrEmpty(n) && keep(n)) names.Add(n);
            }
        }
        catch { }
        return new List<string>(names);
    }

    // Every loaded skill with "drone" in its name, given to the unit at the cursor.
    internal static void GrantDronesAtCursor()
    {
        var t = CursorTile();
        if (t == null) { CoopNet.Status = "Debug: hover a unit first"; return; }
        var p = t.GetTilePos();
        foreach (var n in Names<Menace.Tactical.Skills.SkillTemplate>(x => x.Contains("drone")))
            Request($"grantskill|{p.x}|{p.y}|{n}");
    }

    internal static void GrantAllSupport() => Request("grantoci|*");

    // DRONE CENSUS (user, 2026-09-27: "I want drones to be syncable, all of them").
    // Once per session: load every entity template and name every drone-like unit
    // and every skill that spawns, commands or detonates one -- player AND enemy
    // (Rogue Army's exploding drones never appear in the pirate test mission, so
    // their names are needed to spawn them on purpose).
    private static bool _droneCensusDone;
    internal static void DroneCensus()
    {
        if (_droneCensusDone) return;
        _droneCensusDone = true;
        FindTemplate("__load_all__");   // forces the full entity-template load
        var ents = Names<EntityTemplate>(n => n.Contains("drone") || n.Contains("skybot") || n.Contains("wardog")
                                             || n.Contains("minion") || n.Contains("_bot") || n.Contains("mine"));
        var skills = Names<Menace.Tactical.Skills.SkillTemplate>(n => n.Contains("drone") || n.Contains("skybot")
                                             || n.Contains("wardog") || n.Contains("suicide") || n.Contains("minion")
                                             || n.Contains("detonat") || n.Contains("explode"));
        Plugin.Logger.LogInfo($"[DEBUG] DRONE CENSUS units ({ents.Count}): {string.Join(" ", ents)}");
        Plugin.Logger.LogInfo($"[DEBUG] DRONE CENSUS skills ({skills.Count}): {string.Join(" ", skills)}");
    }

    private static bool _entitiesLoaded;
    private static EntityTemplate FindTemplate(string name)
    {
        var t = FindTemplateLoaded(name);
        if (t != null || _entitiesLoaded) return t;
        // Not loaded by this campaign (queens, r62): load every entity template.
        _entitiesLoaded = true;
        try
        {
            var type = Il2CppInterop.Runtime.Il2CppType.Of<EntityTemplate>();
            string folder = Menace.Tools.DataTemplateLoader.GetBaseFolder(type);
            var loaded = Resources.LoadAll(folder ?? "", type);
            Plugin.Logger.LogInfo($"[DEBUG] loaded {loaded?.Length ?? 0} entity template(s) from Resources '{folder}'");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[DEBUG] entity template load threw {e.GetType().Name}: {e.Message}"); }
        return FindTemplateLoaded(name);
    }

    private static EntityTemplate FindTemplateLoaded(string name)
    {
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<EntityTemplate>());
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i]?.TryCast<EntityTemplate>();
                if (t != null && t.name == name) return t;
            }
        }
        catch { }
        return null;
    }

    // ---- entry points -------------------------------------------------------
    internal static Tile CursorTile()
    {
        try { return TacticalState.Get()?.GetCurrentTile(); } catch { return null; }
    }

    internal static void SpawnAtCursor()
    {
        var t = CursorTile();
        if (t == null) { CoopNet.Status = "Debug: hover a tile first"; return; }
        var p = t.GetTilePos();
        Request($"spawn|{Picked}|{p.x}|{p.y}");
    }

    internal static void KillAtCursor()
    {
        var t = CursorTile();
        if (t == null) { CoopNet.Status = "Debug: hover a unit first"; return; }
        var p = t.GetTilePos();
        Request($"kill|{p.x}|{p.y}");
    }

    internal static void RefillSupport() => Request("refill");

    internal static void Request(string cmd)
    {
        if (!LocalOn) return;
        if (!CoopNet.Connected || CoopNet.Mode == CoopMode.Host) { Execute(cmd, broadcast: true); return; }
        CoopNet.Send(new CoopMsg { T = "dbgreq", Data = cmd });
        Plugin.Logger.LogInfo($"[DEBUG] asked the host to run '{cmd}'");
    }

    internal static void HandleRequest(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Host) return;
        if (!LocalOn) { Refuse(m.Data); return; }
        Execute(m.Data, broadcast: true);
    }

    internal static void HandleCommand(CoopMsg m)
    {
        if (!LocalOn) { Refuse(m.Data); return; }
        Execute(m.Data, broadcast: false);
    }

    private static void Refuse(string cmd)
    {
        Plugin.Logger.LogWarning($"[DEBUG] refused '{cmd}': SyncedDebug is OFF on this machine -- turn it on on BOTH");
        CoopNet.Status = "Partner used synced debug, but it is OFF here";
    }

    // The game's own cheat menu. Its effects are NOT synced: use it to look, and
    // the synced buttons to change the board. v0.7.16 removed DevSettings (the
    // flag array this used to flip); the menu is now the CheatPanel opened by the
    // game's "Toggle Cheat Menu" key binding, so there is nothing left to unlock.
    internal static void UnlockGameCheats()
    {
        try
        {
            Plugin.Logger.LogInfo("[DEBUG] game cheat menu: bind \"Toggle Cheat Menu\" in the game's key settings -- its effects are one-machine only");
            CoopNet.Status = "Cheat menu: use the game's Toggle Cheat Menu key binding";
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[DEBUG] could not unlock game cheats: " + e.Message); }
    }

    // ---- execution: the SAME call on both machines ---------------------------
    private static void Execute(string cmd, bool broadcast)
    {
        var tm = TacticalManager.Get();
        string[] a = (cmd ?? "").Split('|');
        bool ok = false;
        bool prev = Replication.ApplyingRemote;
        // Everything here is a command both machines run themselves, so none of
        // its side effects may be relayed a second time.
        Replication.ApplyingRemote = true;
        try
        {
            switch (a[0])
            {
                case "spawn" when (a.Length == 4 || a.Length == 5) && tm != null:
                {
                    var tmpl = FindTemplate(a[1]);
                    var tile = Replication.GetTileAt(int.Parse(a[2]), int.Parse(a[3]));
                    if (tmpl == null || tile == null) { Plugin.Logger.LogWarning($"[DEBUG] spawn: template '{a[1]}' or tile missing here"); break; }
                    var fac = a.Length == 5 && Enum.TryParse(a[4], out FactionType forced) ? forced : FactionFor(tm, a[1]);
                    ok = tm.TrySpawnUnit(fac, tmpl, tile, out Actor spawned) && spawned != null;
                    Plugin.Logger.LogInfo($"[DEBUG] spawn '{a[1]}' as {fac} at ({a[2]},{a[3]}): {(ok ? "OK" : "REFUSED by engine")}");
                    break;
                }
                case "kill" when a.Length == 3 && tm != null:
                {
                    var tile = Replication.GetTileAt(int.Parse(a[1]), int.Parse(a[2]));
                    var actor = tile?.GetActor();
                    if (actor == null) { Plugin.Logger.LogWarning($"[DEBUG] kill: nobody at ({a[1]},{a[2]}) here"); break; }
                    actor.Die();
                    ok = true;
                    Plugin.Logger.LogInfo($"[DEBUG] killed '{DesyncDetector.TemplateNameOf(actor)}' at ({a[1]},{a[2]})");
                    break;
                }
                case "grantskill" when a.Length == 4 && tm != null:
                {
                    var tile = Replication.GetTileAt(int.Parse(a[1]), int.Parse(a[2]));
                    var actor = tile?.GetActor();
                    var st = FindByName<Menace.Tactical.Skills.SkillTemplate>(a[3]);
                    if (st == null) { EnsureSkillTemplatesLoaded(); st = FindByName<Menace.Tactical.Skills.SkillTemplate>(a[3]); }
                    if (actor == null || st == null) { Plugin.Logger.LogWarning($"[DEBUG] grantskill: unit at ({a[1]},{a[2]}) or skill '{a[3]}' missing here"); break; }
                    // An ORIGIN, not null: every skill the engine creates comes from
                    // something, and CreateSkill(null) threw an NRE for every
                    // template tried (drones, grenades). Rooted at the template.
                    Menace.Tactical.Skills.Skill skill = null;
                    string step = "CreateSkill(origin)";
                    try
                    {
                        skill = st.CreateSkill(new Il2CppSystem.Nullable<Menace.Strategy.Origin>(new Menace.Strategy.Origin(st, -1, null, -1)));
                        step = "SkillContainer.Add";
                        ok = skill != null && actor.GetSkills().Add(skill);
                    }
                    catch (Exception e) { Plugin.Logger.LogWarning($"[DEBUG] grantskill: {step} threw {e.GetType().Name}: {e.Message}"); }
                    Plugin.Logger.LogInfo($"[DEBUG] grant skill '{a[3]}' to '{DesyncDetector.TemplateNameOf(actor)}' at ({a[1]},{a[2]}): {(ok ? "OK" : "refused")}");
                    break;
                }
                case "grantoci" when a.Length == 2:
                {
                    var abilities = StrategyState.Get()?.ActiveOffmapAbilities;
                    if (abilities == null) { Plugin.Logger.LogWarning("[DEBUG] grantoci: no strategy state here"); break; }
                    int n = 0;
                    foreach (var name in a[1] == "*" ? Names<Menace.OffmapAbilities.OffmapAbilityTemplate>(_ => true) : new List<string> { a[1] })
                    {
                        var t = FindByName<Menace.OffmapAbilities.OffmapAbilityTemplate>(name);
                        if (t == null) continue;
                        try { abilities.Add(t); n++; } catch { }
                    }
                    ok = n > 0;
                    Plugin.Logger.LogInfo($"[DEBUG] granted {n} ship support abilit(ies) ('{a[1]}')");
                    break;
                }
                case "refill":
                {
                    var list = StrategyState.Get()?.ActiveOffmapAbilities?.m_OffmapAbilityInstances;
                    int n = 0;
                    if (list != null)
                        for (int i = 0; i < list.Count; i++) { try { list[i]?.RefillRemainingUses(); n++; } catch { } }
                    ok = n > 0;
                    Plugin.Logger.LogInfo($"[DEBUG] refilled {n} ship support abilit(ies)");
                    break;
                }
                default:
                    Plugin.Logger.LogWarning($"[DEBUG] unknown or malformed command '{cmd}'");
                    break;
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[DEBUG] '{cmd}' threw: {e.GetType().Name}: {e.Message}"); }
        finally { Replication.ApplyingRemote = prev; }

        if (broadcast && ok && CoopNet.Connected) CoopNet.Send(new CoopMsg { T = "dbg", Data = cmd });
        CoopNet.Status = $"Debug: {a[0]} {(ok ? "done" : "failed")}{(broadcast && ok && CoopNet.Connected ? " (both machines)" : "")}";
    }

    // HARNESS SCENARIO: set up, on demand, the three reports no early vanilla
    // campaign produces by itself (BUGS.md #3, #4b, #6), then let a run show
    // what happens. Once per battle, from round 2, only on the player phase:
    //
    //   HOST  a. grant every ship support ability (both machines)
    //         b. spawn a VIP as a PLAYER unit beside the GUEST's vehicle
    //         c. give drone skills to one host squad and one guest squad
    //         d. order the VIP INTO the guest's vehicle      -> report #6
    //   GUEST e. launch a drone at the nearest enemy          -> report #4b
    //
    // Everything after (a)-(c) is an ordinary order from the unit's owner, so it
    // travels the normal relay paths -- which is exactly what is under test.
    private static IntPtr _battle;
    private static int _hostStep;
    private static int _battleSaveStep;   // per session, not per battle
    private static float _nextTry;

    // "rejoin" (BUGS.md #1): the GUEST drops its connection at round 3 and
    // reconnects 10 s later, mid-battle. The host's hello handler must reload it
    // into the battle (GuestReload). Once per session.
    private static bool _renamed;
    private static int _rejoinStep;
    private static float _rejoinAt;

    // "rejoinmany" (user 2026-10-04): the same client drops and rejoins at rounds
    // 3, 5 and 7 -- each only after the previous catch-up has fully finished.
    private static int _rejoinCount;

    private static void GuestRejoin(TacticalManager tm)
    {
        if (tm == null) return;
        int[] rounds = Has("rejoinmany") ? new[] { 3, 5, 7 } : new[] { Has("rejoinlate") ? 8 : 3 };
        if (_rejoinStep >= 2)
        {
            if (_rejoinCount >= rounds.Length) return;
            _rejoinStep = 0;   // next scheduled drop
        }
        float now = Time.realtimeSinceStartup;
        if (_rejoinStep == 0)
        {
            if (CoopNet.Mode != CoopMode.Guest || !CoopNet.Connected || !SessionSync.GuestBattleReady) return;
            if (GuestReload.GuestCatchingUp) return;   // never drop mid catch-up
            // "rejoinlate": round 8, after units have died (#66 death replay).
            try { if (tm.GetRound() < rounds[_rejoinCount]) return; } catch { return; }
            _rejoinCount++;
            Plugin.Logger.LogWarning($"[DEBUG] scenario rejoin: drop {_rejoinCount} of {rounds.Length}");
            _rejoinStep = 1;
            // "rejoin2": the last TWO seats drop together; the second-to-last comes
            // back 4 s after the last, i.e. while the first is still mid catch-up
            // (~25 s), so the host's rejoin QUEUE is exercised (user 2026-10-04).
            int last = System.Math.Max(1, (Plugin.SelfTestPlayers?.Value ?? 2) - 1);
            float delay = 10f + (Peers.LocalSeat == last ? 0f : 4f);
            _rejoinAt = now + delay;
            Plugin.Logger.LogWarning($"[DEBUG] scenario rejoin: guest DISCONNECTING mid-battle (reconnects in {delay:0} s)");
            CoopNet.Stop();
            return;
        }
        if (now < _rejoinAt) return;
        _rejoinStep = 2;
        Plugin.Logger.LogWarning($"[DEBUG] scenario rejoin: guest RECONNECTING to {Plugin.HostAddress.Value}:{Plugin.Port.Value}");
        CoopNet.Start(CoopMode.Guest, Plugin.HostAddress.Value, Plugin.Port.Value);
    }

    // "oci" (BUGS.md #3a): one ship-support pool for two players. Round 2: the
    // host grants every ability (synced), then spends one use; round 3: the guest
    // spends one. Each "[OCI] ... use(s) left" line must agree across the logs.
    private static int _ociStep;
    private static float _ociAt;

    private static void OciScenario(TacticalManager tm)
    {
        if (_ociStep >= 3) return;
        int r;
        try { r = tm.GetRound(); } catch { return; }
        float now = Time.realtimeSinceStartup;
        bool host = CoopNet.Mode == CoopMode.Host;
        if (host && _ociStep == 0 && r >= 2) { _ociStep = 1; _ociAt = now + 3f; Request("grantoci|*"); return; }
        if (host && _ociStep == 1 && now >= _ociAt) { _ociStep = 3; SpendOne("host"); return; }
        if (!host && _ociStep == 0 && r >= 3 && SessionSync.GuestBattleReady) { _ociStep = 3; SpendOne("guest"); }
    }

    // "strike" (BUGS.md #3b): round 2 the host grants every ship-support ability
    // (synced) and calls a real strike through the engine's own action on the
    // enemy nearest its units; round 3 the guest calls one. Damage must land once
    // and on both machines, and both must log the scheduled strike.
    private static int _strikeStep;
    private static float _strikeAt;

    private static void StrikeScenario(TacticalManager tm)
    {
        if (_strikeStep >= 3) return;
        int r;
        try { r = tm.GetRound(); } catch { return; }
        float now = Time.realtimeSinceStartup;
        bool host = CoopNet.Mode == CoopMode.Host;
        if (host && _strikeStep == 0 && r >= 2) { _strikeStep = 1; _strikeAt = now + 3f; Request("grantoci|*"); return; }
        if (host && _strikeStep == 1 && now >= _strikeAt && OurPlayerPhase(tm)) { _strikeStep = 3; CallStrike(tm, "host"); return; }
        if (!host && _strikeStep == 0 && r >= 3 && SessionSync.GuestBattleReady && OurPlayerPhase(tm)) { _strikeStep = 3; CallStrike(tm, "guest"); }
    }

    private static void CallStrike(TacticalManager tm, string who, string[] only = null, bool nearSelf = false)
    {
        Actor ours = null;
        foreach (var a in DesyncDetector.AllActors(tm))
            if (a != null && !a.IsDying() && Ownership.IsKnownPlayerUnit(a) && Ownership.IsLocal(a)
                && !DesyncDetector.TemplateNameOf(a).Contains("vehicle")) { ours = a; break; }
        // Spawning abilities (crates, turrets, troops, cover) go on a free tile
        // two steps from our own unit; strikes go on the nearest enemy.
        var target = ours == null ? null : nearSelf ? FreeTileNear(ours, 2) : NearestEnemyTile(tm, ours);
        var list = StrategyState.Get()?.ActiveOffmapAbilities?.m_OffmapAbilityInstances;
        if (target == null || list == null) { Plugin.Logger.LogWarning($"[DEBUG] scenario strike ({who}): no target or no abilities"); return; }
        // A REAL STRIKE, not whatever is first (run 43 picked an AT minefield).
        // Prefer strafing runs / barrages / bombardments; log the whole menu once.
        string[] prefer = only ?? new[] { "strafe", "strafing", "gun_run", "gunship", "barrage", "bombard", "artillery", "missile", "strike", "orbital", "laser" };
        Menace.OffmapAbilities.OffmapAbilityInstance pick = null;
        int pickRank = int.MaxValue;
        var menu = new System.Text.StringBuilder();
        for (int i = 0; i < list.Count; i++)
        {
            var inst = list[i];
            if (inst == null || inst.RemainingUses <= 0) continue;
            string id = inst.TemplateId ?? "";
            menu.Append(id.Replace("offmap_ability.", "")).Append(' ');
            int rank = Array.FindIndex(prefer, p => only != null ? id.EndsWith("." + p) : id.Contains(p));
            if (rank < 0 || (only == null && id.Contains("mine"))) rank = 1000;
            if (only != null && rank == 1000) continue;
            if (rank < pickRank) { pickRank = rank; pick = inst; }
        }
        Plugin.Logger.LogInfo($"[DEBUG] scenario strike ({who}): available: {menu}");
        if (pick == null) { Plugin.Logger.LogWarning($"[DEBUG] scenario strike ({who}): nothing with uses left"); return; }
        var tp = target.GetTilePos();
        int before = pick.RemainingUses;
        Plugin.Logger.LogInfo($"[DEBUG] scenario strike ({who}): '{pick.TemplateId}' ({pick.OffmapAbilityType}, delay {pick.GetDelayInRounds()} rounds) at ({tp.x},{tp.y}), {before} use(s) before");
        // Drive it as a player does -- a click on the tile -- then fall back to
        // the area-selection callback and the bare Execute. Stop at the first that
        // actually spends a use; the log names which path the engine takes.
        var action = new Menace.States.OffmapAbilityAction(pick);
        var paths = new (string name, Action run)[]
        {
            ("HandleLeftClickOnTile", () => action.HandleLeftClickOnTile(target, ours)),
            ("OnTilesSelected", () => { var l = new Il2CppSystem.Collections.Generic.List<Tile>(); l.Add(target); action.OnTilesSelected(l); }),
            ("Execute", () => action.Execute(target)),
        };
        foreach (var (name, run) in paths)
        {
            try { run(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[DEBUG] scenario strike ({who}): {name} threw {e.GetType().Name}: {e.Message}"); }
            int after = pick.RemainingUses;
            Plugin.Logger.LogInfo($"[DEBUG] scenario strike ({who}): after {name}: uses {before} -> {after}");
            if (after < before) return;
        }
        Plugin.Logger.LogWarning($"[DEBUG] scenario strike ({who}): no path spent a use -- the strike did not fire");
    }

    private static void SpendOne(string who)
    {
        var list = StrategyState.Get()?.ActiveOffmapAbilities?.m_OffmapAbilityInstances;
        if (list == null) { Plugin.Logger.LogWarning($"[DEBUG] scenario oci ({who}): no ship support list"); return; }
        for (int i = 0; i < list.Count; i++)
        {
            var inst = list[i];
            if (inst == null || inst.RemainingUses <= 0) continue;
            Plugin.Logger.LogInfo($"[DEBUG] scenario oci ({who}): spending '{inst.TemplateId}' (instance {inst.InstanceId}, {inst.RemainingUses} left before)");
            inst.ConsumeUse();
            return;
        }
        Plugin.Logger.LogWarning($"[DEBUG] scenario oci ({who}): nothing with uses left ({list.Count} abilities)");
    }

    // "drones" (BUGS.md #4). The player drones are SKYBOTS: 'deploy_recon_skybot'
    // (recon, #4c fog) and 'deploy_at_wardog_skybot' (anti-tank, #4b damage).
    // A campaign loads only the templates its squads carry, and this one carries
    // neither -- which is why every search for "drone" found nothing (r41) -- so
    // the skill templates are loaded from Resources first (both machines).
    //   round 2  host grants both deploy skills to one host and one guest squad
    //   round 3+ each side, on its own turn: deploy the wardog, then the recon
    //   round 4+ each side: recon moves toward the nearest enemy; wardog attacks it
    // Every few seconds both sides log the skybots they see (owner, tile) and
    // which enemies near a skybot the player faction has detected (#4c).
    private static int _droneStep;
    private static int _droneAct;
    private static float _droneNextAct, _droneScanAt;
    private static string _droneSeen = "";
    private static bool _skillsLoaded;
    private static readonly string[] DroneDeploy = { "active.deploy_recon_skybot", "active.deploy_at_wardog_skybot" };
    private static readonly string[] DroneCommand = { "active.recon_skybot_command_move", "active.at_wardog_command_suicide" };
    // The wardog also has a MOVE command (census r81); without it a wardog whose
    // target walks out of its 4-tile strike reach can never close in.
    private static readonly string[] DroneExtraCommand = { null, "active.at_wardog_command_move" };

    internal static void EnsureSkillTemplatesLoaded()
    {
        if (_skillsLoaded) return;
        _skillsLoaded = true;
        try
        {
            var type = Il2CppInterop.Runtime.Il2CppType.Of<Menace.Tactical.Skills.SkillTemplate>();
            string folder = Menace.Tools.DataTemplateLoader.GetBaseFolder(type);
            var loaded = Resources.LoadAll(folder ?? "", type);
            Plugin.Logger.LogInfo($"[DEBUG] loaded {loaded?.Length ?? 0} skill template(s) from Resources '{folder}'");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[DEBUG] skill template load threw {e.GetType().Name}: {e.Message}"); }
    }

    // #4b by hand (user flies the wardog, 2026-10-03): the skybot census -- which
    // drone each command skill is bound to, usable or not, on every machine --
    // also runs outside the drone scenario whenever ProbeBugs is on.
    internal static void ProbeSkybotsTick()
    {
        if (!BugProbes.On || !CoopNet.Connected || Has("drones") || Has("dronegrant")) return;
        float now = Time.realtimeSinceStartup;
        if (now < _droneScanAt) return;
        _droneScanAt = now + 4f;
        var tm = TacticalManager.Get();
        if (tm != null) ScanSkybots(tm);
    }

    private static bool IsSkybot(Actor a)
    {
        string n = DesyncDetector.TemplateNameOf(a);
        return n.Contains("skybot") || n.Contains("wardog") || n.Contains("drone");
    }

    private static void DroneScenario(TacticalManager tm)
    {
        int r;
        try { r = tm.GetRound(); } catch { return; }
        float now = Time.realtimeSinceStartup;
        if (now >= _droneScanAt) { _droneScanAt = now + 4f; ScanSkybots(tm); }
        bool host = CoopNet.Mode == CoopMode.Host;
        if (host && _droneStep == 0 && r >= 2 && OurPlayerPhase(tm))
        {
            _droneStep = 1;
            EnsureSkillTemplatesLoaded();
            DroneCensus();
            // ONE DRONE KIND PER UNIT, as with a real drone item (r69): a unit
            // holding both kinds had its recon "move" command steer whichever
            // minion it deployed LAST -- its wardog. So per side, the first
            // infantry unit carries the recon, the second the wardog.
            var mine = new System.Collections.Generic.List<Actor>();
            var theirs = new System.Collections.Generic.List<Actor>();
            foreach (var x in DesyncDetector.AllActors(tm))
            {
                if (x == null || x.IsDying() || !Ownership.IsKnownPlayerUnit(x)) continue;
                if (DesyncDetector.TemplateNameOf(x).Contains("vehicle")) continue;
                var side = Ownership.IsLocal(x) ? mine : theirs;
                if (side.Count < 2) side.Add(x);
            }
            string log = "";
            foreach (var side in new[] { mine, theirs })
                for (int k = 0; k < side.Count; k++)
                {
                    var gp = side[k].GetLastTile()?.GetTilePos();
                    if (gp == null) continue;
                    // The drone item also gives the squad the COMMAND skill
                    // (Effects.Minions.CommandMove / CommandUseSkill): that is how
                    // a player steers a deployed skybot. Without it nothing can.
                    Request($"grantskill|{gp.Value.x}|{gp.Value.y}|{DroneDeploy[k]}");
                    Request($"grantskill|{gp.Value.x}|{gp.Value.y}|{DroneCommand[k]}");
                    if (DroneExtraCommand[k] != null) Request($"grantskill|{gp.Value.x}|{gp.Value.y}|{DroneExtraCommand[k]}");
                    log += $" '{DesyncDetector.TemplateNameOf(side[k])}'={DroneDeploy[k]}";
                }
            Plugin.Logger.LogInfo($"[DEBUG] scenario drones: one kind per unit --{log}");
            return;
        }
        if (!Has("drones")) return;   // dronegrant: the brain takes it from here
        // Act the moment our phase opens, before AutoCombat spends the AP.
        bool ours = OurPlayerPhase(tm);
        if (ours && !_droneWasOurs) _droneNextAct = 0f;
        _droneWasOurs = ours;
        if (r < 3 || now < _droneNextAct || !ours) return;
        if (!host && !SessionSync.GuestBattleReady) return;
        _droneNextAct = now + 3f;
        // Count only real attempts (r65: 12 tries ran out while the holder had no
        // AP left in the phase, so the wardog was never deployed at all).
        if (_droneAct < 2)
        {
            _deployAttempted = false;
            if (DeploySkybot(tm, DroneDeploy[_droneAct])) { _droneAct++; _droneTries = 0; }
            else if (_deployAttempted && ++_droneTries > 3) { _droneAct++; _droneTries = 0; }
            return;
        }
        if (r < 4) return;
        // ~2 minutes of tries: a spent holder has to wait for the next phase.
        if (!_reconDone) _reconDone = CommandSkybot(tm, "recon") || ++_reconTries > 40;
        if (!_wardogDone) _wardogDone = CommandSkybot(tm, "wardog") || ++_wardogTries > 40;
    }
    private static int _droneTries, _reconTries, _wardogTries;
    private static bool _reconDone, _wardogDone, _droneWasOurs, _deployAttempted;

    // True only when the engine accepted the use.
    private static bool UseSkill(Actor a, string idPart, Tile target, string what)
    {
        var all = a?.GetSkills()?.GetAllSkills();
        if (all == null || target == null) return false;
        for (int i = 0; i < all.Count; i++)
        {
            var s = all[i]?.TryCast<Menace.Tactical.Skills.Skill>();
            if (s == null || !(s.GetID() ?? "").Contains(idPart)) continue;
            bool usable = false, usableOn = false, ok = false;
            try { usable = s.IsUsable(); } catch { }
            try { usableOn = s.IsUsableOn(target, a.GetLastTile()); } catch { }
            // r64: Use returned false at full AP on every tile, on one side only.
            // Log who the engine thinks is active (Use needs the active unit, and
            // AutoCombat switches it) at the moment of the call.
            string active = "?";
            try { var aa = TacticalManager.Get()?.GetActiveActor(); active = aa == null ? "none" : (aa.Pointer == a.Pointer ? "self" : DesyncDetector.TemplateNameOf(aa)); } catch { }
            // Co-op's own Skill.Use vetoes (hold, action lock, enemy fire in flight)
            // are silent except for the status line: capture both around the call.
            bool locked = false, aiFire = false;
            try { locked = Replication.ActionBlockedByLock(); aiFire = Replication.AiFireInFlight(); } catch { }
            string before = CoopNet.Status;
            try { ok = s.Use(target, Menace.Tactical.Skills.UsageParameter.Default); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[DEBUG] scenario drones: {what} threw {e.GetType().Name}: {e.Message}"); }
            if (!ok) Plugin.Logger.LogInfo($"[DEBUG] scenario drones: refused -- lock={locked} aiFire={aiFire} status '{before}' -> '{CoopNet.Status}'");
            var tp = target.GetTilePos();
            Plugin.Logger.LogInfo($"[DEBUG] scenario drones ({(CoopNet.Mode == CoopMode.Host ? "host" : "guest")}): {what} -- '{DesyncDetector.TemplateNameOf(a)}' used '{s.GetID()}' at ({tp.x},{tp.y}) usable={usable} usableOn={usableOn} active={active} -> {ok}, AP now {a.GetActionPoints()}");
            return ok;
        }
        return false;
    }

    // The ACTIVE unit only (r58: a Use on a unit that is not the active one
    // returned false at full AP), onto the first adjacent tile the engine takes.
    private static bool DeploySkybot(TacticalManager tm, string skill)
    {
        Actor a = null;
        foreach (var u in DesyncDetector.AllActors(tm))
        {
            if (u == null || u.IsDying() || !Ownership.IsKnownPlayerUnit(u) || !Ownership.IsLocal(u) || u.IsTurnDone()) continue;
            if (IsSkybot(u) || u.GetActionPoints() < 40 || !HasSkill(u, skill)) continue;
            a = u; break;
        }
        if (a == null) return false;
        if (!MakeActiveFirst(tm, a)) return false;
        _deployAttempted = true;
        var userTile = a.GetLastTile();
        var fp = userTile?.GetTilePos();
        if (fp == null) return false;
        // The skill's own range and shape (r63: the recon refused every tile 1-2
        // away at full AP -- it has a minimum range).
        Menace.Tactical.Skills.Skill sk = null;
        var all = a.GetSkills()?.GetAllSkills();
        for (int i = 0; i < (all?.Count ?? 0) && sk == null; i++)
        {
            var x = all[i]?.TryCast<Menace.Tactical.Skills.Skill>();
            if (x != null && (x.GetID() ?? "").Contains(skill)) sk = x;
        }
        if (sk == null) return false;
        int min = 1, max = 2;
        try { min = Math.Max(1, sk.GetMinRange()); max = Math.Min(10, Math.Max(min, sk.GetMaxRange())); } catch { }
        Plugin.Logger.LogInfo($"[DEBUG] scenario drones: '{skill}' range {min}-{max}");
        int tries = 0;
        for (int d = min; d <= max && tries < 40; d++)
            for (int dx = -d; dx <= d && tries < 40; dx++)
                for (int dy = -d; dy <= d && tries < 40; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != d) continue;
                    var t = Replication.GetTileAt(fp.Value.x + dx, fp.Value.y + dy);
                    if (!Replication.TileFree(t)) continue;
                    bool inRange = false;
                    try { inRange = sk.IsInRange(t, userTile, true) && sk.IsUsableOn(t, userTile); } catch { }
                    if (!inRange) continue;
                    tries++;
                    if (UseSkill(a, skill, t, "deploy")) return true;
                    if (a.GetActionPoints() < 40) return false;
                }
        return false;
    }

    // The command skills ('recon_skybot_command_move', 'at_wardog_command_suicide')
    // are NOT on the skybot (r60: it has none) -- they are handed to the squad
    // that deployed it. So: find our skybot, then whichever of our units holds
    // the command, make it the active actor and use it.
    private static bool CommandSkybot(TacticalManager tm, string kind)
    {
        Actor bot = null;
        foreach (var a in DesyncDetector.AllActors(tm))
        {
            // Skybots are AI-driven (faction 2, r62), so never IsLocal: find the
            // one OUR unit deployed (r63 looked for an owned one and found none).
            if (a == null || a.IsDying() || !IsSkybot(a)) continue;
            if (!Ownership.IsLocal(a) && !Ownership.DeployedByLocal(a)) continue;
            if (DesyncDetector.TemplateNameOf(a).Contains(kind == "recon" ? "recon" : "wardog")) { bot = a; break; }
        }
        if (bot == null) { Plugin.Logger.LogInfo($"[DEBUG] scenario drones: no own '{kind}' skybot to command yet"); return false; }
        var enemy = NearestEnemyTile(tm, bot);
        if (enemy == null) return false;
        Tile target = enemy;
        if (kind == "recon")
        {
            // Halfway toward the enemy, to reveal what the squads cannot see.
            var fp = bot.GetLastTile().GetTilePos(); var ep = enemy.GetTilePos();
            target = Replication.GetTileAt((fp.x + ep.x) / 2, (fp.y + ep.y) / 2) ?? enemy;
        }
        string cmd = kind == "recon" ? "command_move" : "command_suicide";
        bool holderSpent = false;
        foreach (var u in DesyncDetector.AllActors(tm))
        {
            if (u == null || u.IsDying() || !Ownership.IsLocal(u)) continue;
            if (!HasSkill(u, cmd)) continue;
            // r64: the holder had already spent its AP (AutoCombat moves it
            // first) and the engine refused the 40 AP command every time.
            if (u.IsTurnDone() || u.GetActionPoints() < 40) { holderSpent = true; continue; }
            if (!MakeActiveFirst(tm, u)) return false;
            return UseSkill(u, cmd, target, kind + " command");
        }
        if (holderSpent) { Plugin.Logger.LogInfo($"[DEBUG] scenario drones: '{cmd}' holder has no AP left this phase"); return false; }
        Plugin.Logger.LogInfo($"[DEBUG] scenario drones: nobody of ours holds a '{cmd}' skill");
        return ListSkills(bot);
    }

    // SWITCH, THEN USE ON A LATER TICK (r66). SetActiveActor + Use in the same
    // frame returned false 80 times at full AP on a valid tile, although
    // GetActiveActor already read back the new unit; every success in r63-r66
    // was by a unit that was active BEFORE the call. So: switch, come back in
    // 0.3 s, and use only if it is still the active one (AutoCombat also switches).
    private static bool MakeActiveFirst(TacticalManager tm, Actor a)
    {
        try
        {
            if (tm.GetActiveActor()?.Pointer == a.Pointer && _activatedAt > 0f && Time.realtimeSinceStartup - _activatedAt >= 0.25f)
                return true;
            if (tm.GetActiveActor()?.Pointer != a.Pointer || _activatedAt <= 0f)
            {
                tm.SetActiveActor(a, false);
                _activatedAt = Time.realtimeSinceStartup;
            }
        }
        catch { return false; }
        _droneNextAct = Time.realtimeSinceStartup + 0.3f;
        return false;
    }
    private static float _activatedAt;

    private static bool HasSkill(Actor a, string idPart)
    {
        var all = a?.GetSkills()?.GetAllSkills();
        for (int i = 0; i < (all?.Count ?? 0); i++)
            if ((all[i]?.TryCast<Menace.Tactical.Skills.Skill>()?.GetID() ?? "").Contains(idPart)) return true;
        return false;
    }

    private static bool ListSkills(Actor a)
    {
        var all = a?.GetSkills()?.GetAllSkills();
        string ids = "";
        for (int i = 0; i < (all?.Count ?? 0); i++) ids += (all[i]?.TryCast<Menace.Tactical.Skills.Skill>()?.GetID() ?? "?") + " ";
        Plugin.Logger.LogInfo($"[DEBUG] scenario drones: '{DesyncDetector.TemplateNameOf(a)}' has no matching skill; it has: {ids}");
        return true;   // do not retry forever
    }

    private static string SkillList(Actor a, string filter)
    {
        var keys = filter?.Split('|');
        var all = a?.GetSkills()?.GetAllSkills();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < (all?.Count ?? 0); i++)
        {
            var sk = all[i]?.TryCast<Menace.Tactical.Skills.Skill>();
            string id = sk?.GetID() ?? "?";
            if (keys != null && Array.FindIndex(keys, k => id.Contains(k)) < 0) continue;
            sb.Append(id.Replace("active.", ""));
            if (keys != null)   // command skills: what the AutoCombat brain will make of them
            {
                bool atk = false, use = false;
                try { atk = sk.IsAttack(); } catch { }
                try { use = sk.IsUsable(); } catch { }
                sb.Append($"(attack={atk} usable={use})");
            }
            sb.Append(',');
        }
        return sb.ToString().TrimEnd(',');
    }

    // #4b: WHY IS THE WARDOG'S STRIKE NEVER USABLE? For every minion-command skill
    // a unit holds: which minion its handler is bound to (m_Minion), whether the
    // engine calls it usable, and -- for CommandUseSkill -- the skill it asks the
    // minion to use and whether the minion has it. Tells a missing binding (a
    // co-op/harness problem) from a missing strike skill on the spawned drone.
    private static string CommandBindings(Actor u)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var all = u.GetSkills()?.GetAllSkills();
            for (int i = 0; i < (all?.Count ?? 0); i++)
            {
                var s = all[i]?.TryCast<Menace.Tactical.Skills.Skill>();
                if (s == null) continue;
                var hs = s.GetSkillEventHandlers();
                for (int j = 0; j < (hs?.Length ?? 0); j++)
                {
                    var h = hs[j];
                    if (h == null) continue;
                    var mv = h.TryCast<Menace.Tactical.Skills.Effects.Minions.CommandMoveHandler>();
                    var us = h.TryCast<Menace.Tactical.Skills.Effects.Minions.CommandUseSkillHandler>();
                    if (mv == null && us == null) continue;
                    Actor m = mv != null ? mv.m_Minion : us.m_Minion;
                    bool usable = false;
                    try { usable = mv != null ? mv.IsUsable() : us.IsUsable(); } catch { }
                    string mn = m == null ? "NONE" : $"{DesyncDetector.TemplateNameOf(m)}@{m.GetLastTile()?.GetTilePos().x},{m.GetLastTile()?.GetTilePos().y}";
                    sb.Append($"<{s.GetID()} -> minion={mn} usable={usable}");
                    if (us != null)
                    {
                        string want = "?";
                        try { want = us.m_Template?.SkillToUse?.name ?? "null"; } catch { }
                        string has = m == null ? "-" : SkillList(m, null);
                        sb.Append($" wants={want} minionSkills=[{has}]");
                    }
                    sb.Append("> ");
                }
            }
        }
        catch (Exception e) { sb.Append($"<bindings threw {e.GetType().Name}> "); }
        return sb.ToString();
    }

    private static void ScanSkybots(TacticalManager tm)
    {
        try
        {
            int pf = tm.GetPlayerFaction().GetIndex();
            var sb = new System.Text.StringBuilder();
            foreach (var a in DesyncDetector.AllActors(tm))
            {
                if (a == null || a.IsDying() || !IsSkybot(a) || DesyncDetector.TemplateNameOf(a).StartsWith("enemy.")) continue;
                var p = a.GetLastTile()?.GetTilePos();
                if (p == null) continue;
                bool sel = false, pc = false, pcai = false;
                try { sel = a.IsSelectableByPlayer(); } catch { }
                try { pc = a.IsPlayer(); pcai = a.IsPlayerOrPlayerAI(); } catch { }
                sb.Append($"{DesyncDetector.TemplateNameOf(a)}@({p.Value.x},{p.Value.y}) mine={Ownership.IsLocal(a)} fac={a.GetFactionID()} sel={sel} pc={pc} pcAI={pcai} skills={SkillList(a, null)} [");
                foreach (var e in DesyncDetector.AllActors(tm))
                {
                    if (e == null || e.IsDying() || Ownership.IsKnownPlayerUnit(e) || IsSkybot(e) || e.GetFactionID() == a.GetFactionID()) continue;
                    var q = e.GetLastTile()?.GetTilePos();
                    if (q == null || Math.Abs(q.Value.x - p.Value.x) + Math.Abs(q.Value.y - p.Value.y) > 10) continue;
                    bool det = false;
                    try { det = e.IsDetectedByFaction(pf); } catch { }
                    sb.Append($"({q.Value.x},{q.Value.y}){(det ? "seen" : "hidden")} ");
                }
                sb.Append("] ");
            }
            // Command skills handed to our squads after a deploy, if any.
            foreach (var u in DesyncDetector.AllActors(tm))
            {
                if (u == null || u.IsDying() || !Ownership.IsKnownPlayerUnit(u) || IsSkybot(u)) continue;
                string k = SkillList(u, "skybot|wardog|command|drone");
                if (k.Length > 0) sb.Append($"{DesyncDetector.TemplateNameOf(u)} holds {k} ");
                sb.Append(CommandBindings(u));
            }
            string s = sb.ToString();
            if (s == _droneSeen) return;
            _droneSeen = s;
            if (s.Length > 0) Plugin.Logger.LogInfo($"[DEBUG] SKYBOTS now: {s}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[DEBUG] skybot scan threw {e.Message}"); }
    }

    // Tiles already aimed at this battle: a delayed ability has not landed yet,
    // so its tile still looks free (run 50 put a turret and a bunker on one tile).
    private static readonly HashSet<(int, int)> _aimed = new();
    private static IntPtr _aimedBattle;

    private static Tile FreeTileNear(Actor from, int r)
    {
        var tmp = TacticalManager.Get()?.Pointer ?? IntPtr.Zero;
        if (tmp != _aimedBattle) { _aimedBattle = tmp; _aimed.Clear(); }
        var fp = from.GetLastTile()?.GetTilePos();
        if (fp == null) return null;
        for (int d = r; d >= 1; d--)
            for (int dx = -d; dx <= d; dx++)
                for (int dy = -d; dy <= d; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != d) continue;
                    var t = Replication.GetTileAt(fp.Value.x + dx, fp.Value.y + dy);
                    var key = (fp.Value.x + dx, fp.Value.y + dy);
                    if (_aimed.Contains(key)) continue;
                    try { if (t != null && t.GetActor() == null && !t.HasStructure()) { _aimed.Add(key); return t; } } catch { }
                }
        return null;
    }

    // "ocimix" (user, 2026-09-26: "supply crates and turrets which spawn more
    // stuff"): the ship abilities that SPAWN things, alternately from each side,
    // onto a free tile beside our own unit. Round 2 host / 3 guest / 4 host / 5
    // guest. Each must spawn once per machine, identically, with one owner.
    private static readonly (int round, bool host, string id)[] OciMix =
    {
        (2, true,  "supply_drop"),
        (3, false, "deploy_mmg_sentry_turret"),
        (4, true,  "deploy_airdropped_conscripts"),
        (5, false, "deploy_cover"),
        (6, true,  "deploy_rocket_sentry_turret"),
        (7, false, "deploy_airdropped_regulars"),
        (8, true,  "deploy_bunker"),
        (9, false, "emergency_smoke_screen"),
    };
    private static int _mixStep;
    private static float _mixGrantAt = -1f;

    private static void OciMixScenario(TacticalManager tm)
    {
        int r;
        try { r = tm.GetRound(); } catch { return; }
        bool host = CoopNet.Mode == CoopMode.Host;
        float now = Time.realtimeSinceStartup;
        if (host && _mixGrantAt < 0f && r >= 2) { _mixGrantAt = now + 3f; Request("grantoci|*"); return; }
        if (_mixGrantAt < 0f && host) return;
        if (host && now < _mixGrantAt) return;
        while (_mixStep < OciMix.Length && OciMix[_mixStep].host != host) _mixStep++;
        if (_mixStep >= OciMix.Length) return;
        var step = OciMix[_mixStep];
        if (r < step.round || !OurPlayerPhase(tm)) return;
        if (!host && !SessionSync.GuestBattleReady) return;
        _mixStep++;
        CallStrike(tm, (host ? "host" : "guest") + " ocimix", new[] { step.id }, nearSelf: true);
    }

    // "wreck" (BUGS.md #5): at round 2 each side destroys the enemy vehicle
    // nearest one of its own units through the engine's damage path, attacker =
    // that unit -- so the kill is this machine's and reaches the partner only as
    // relayed "ehp"/"edie". Both sides log every tile holding Tile.Wreckage;
    // the two lists must match.
    private static bool _wreckDone, _wreckSpawned;

    private static bool IsEnemyVehicle(Actor e)
    {
        if (e == null || e.IsDying() || Ownership.IsKnownPlayerUnit(e)) return false;
        string n = DesyncDetector.TemplateNameOf(e);
        return n.Contains("truck") || n.Contains("vehicle") || n.Contains("tank");
    }

    private static bool AnyEnemyVehicle(TacticalManager tm)
    {
        foreach (var e in DesyncDetector.AllActors(tm)) if (IsEnemyVehicle(e)) return true;
        return false;
    }

    private static void SpawnWreckTargets(TacticalManager tm)
    {
        string truck = null;
        foreach (var n in _templates) if (n.StartsWith("enemy.") && n.Contains("truck")) { truck = n; break; }
        truck ??= "enemy.pirate_scavengers_on_heavy_truck";
        Actor hostUnit = null, clientUnit = null;
        foreach (var x in DesyncDetector.AllActors(tm))
        {
            if (x == null || x.IsDying() || !Ownership.IsKnownPlayerUnit(x)) continue;
            if (Ownership.IsLocal(x)) hostUnit ??= x; else clientUnit ??= x;
        }
        foreach (var u in new[] { hostUnit, clientUnit })
        {
            var up = u?.GetLastTile()?.GetTilePos();
            var t = up == null ? null : FreeTileNear(up.Value.x, up.Value.y, 4);
            if (t == null) { Plugin.Logger.LogWarning("[DEBUG] scenario wreck: no free tile beside a player unit"); continue; }
            var p = t.GetTilePos();
            Plugin.Logger.LogInfo($"[DEBUG] scenario wreck: spawning '{truck}' at ({p.x},{p.y}) beside '{DesyncDetector.TemplateNameOf(u)}'");
            Request($"spawn|{truck}|{p.x}|{p.y}");
        }
    }
    private static float _wreckScanAt;
    private static string _wreckSeen = "";

    private static void WreckScenario(TacticalManager tm)
    {
        float now = Time.realtimeSinceStartup;
        if (now >= _wreckScanAt) { _wreckScanAt = now + 3f; ScanWrecks(tm); }
        if (_wreckDone) return;
        int r;
        try { r = tm.GetRound(); } catch { return; }
        if (r < 2 || !OurPlayerPhase(tm)) return;
        if (CoopNet.Mode != CoopMode.Host && !SessionSync.GuestBattleReady) return;
        // NO VEHICLES ON THIS MISSION (r141: "no own unit or no enemy vehicle" --
        // the pinned harness mission is infantry only). The host brings two by the
        // synced spawn at round 2 -- one beside a host unit, one beside a client
        // unit -- and each side destroys its nearest from round 3.
        if (!AnyEnemyVehicle(tm))
        {
            if (CoopNet.Mode == CoopMode.Host && !_wreckSpawned) { _wreckSpawned = true; SpawnWreckTargets(tm); }
            return;
        }
        if (_wreckSpawned && r < 3) return;   // let the spawned trucks land everywhere first
        _wreckDone = true;

        Actor mine = null;
        foreach (var x in DesyncDetector.AllActors(tm))
            if (x != null && !x.IsDying() && Ownership.IsKnownPlayerUnit(x) && Ownership.IsLocal(x)) { mine = x; break; }
        var mp = mine?.GetLastTile()?.GetTilePos();
        Actor truck = null; int bestD = int.MaxValue;
        foreach (var e in DesyncDetector.AllActors(tm))
        {
            if (e == null || e.IsDying() || Ownership.IsKnownPlayerUnit(e) || ContainerSync.IsContained(e)) continue;
            string n = DesyncDetector.TemplateNameOf(e);
            if (!n.Contains("truck") && !n.Contains("vehicle") && !n.Contains("tank")) continue;
            var p = e.GetLastTile()?.GetTilePos();
            if (p == null || mp == null) continue;
            int d = Math.Abs(p.Value.x - mp.Value.x) + Math.Abs(p.Value.y - mp.Value.y);
            if (d < bestD) { bestD = d; truck = e; }
        }
        if (mine == null || truck == null) { Plugin.Logger.LogWarning("[DEBUG] scenario wreck: no own unit or no enemy vehicle"); return; }
        var tp = truck.GetLastTile()?.GetTilePos();
        string how = "OnDamageReceived";
        try
        {
            var di = new DamageInfo { Damage = 9999, ArmorPenetration = 999, IsAbleToPenetrate = true, TargetTile = truck.GetLastTile() };
            truck.OnDamageReceived(mine, null, di);
        }
        catch (Exception ex) { how = "OnDamageReceived threw " + ex.Message; }
        bool alive = false;
        try { alive = truck.IsAlive() && !truck.IsDying(); } catch { }
        if (alive)
        {
            // Fallback: the engine's per-element kill, still with this machine's
            // unit as the attacker so the relay treats it as ours.
            how += " left it alive -> element OnDeath";
            var els = truck.GetElements();
            for (int i = (els?.Count ?? 0) - 1; i >= 0; i--)
            {
                var el = els[i];
                if (el == null) continue;
                try
                {
                    int hp = el.GetHitpoints();
                    el.SetHitpoints(0);
                    el.OnDeath(mine, new DamageInfo { Damage = hp }, hp, null);
                }
                catch (Exception ex) { how += " threw " + ex.Message; }
            }
        }
        try { alive = truck.IsAlive() && !truck.IsDying(); } catch { }
        Plugin.Logger.LogInfo($"[DEBUG] scenario wreck: '{DesyncDetector.TemplateNameOf(truck)}' at ({tp?.x},{tp?.y}) destroyed by '{DesyncDetector.TemplateNameOf(mine)}' via {how}; alive now={alive}");
    }

    private static void ScanWrecks(TacticalManager tm)
    {
        try
        {
            var map = tm.GetMap();
            if (map == null) return;
            int w = 0, h = 0;
            for (int x = 0; x < 512; x++) { if (map.GetTile(x, 0) == null) break; w = x + 1; }
            for (int z = 0; z < 512; z++) { if (map.GetTile(0, z) == null) break; h = z + 1; }
            var sb = new System.Text.StringBuilder();
            for (int x = 0; x < w; x++)
                for (int z = 0; z < h; z++)
                {
                    try { if (map.GetTile(x, z)?.Wreckage != null) sb.Append($"({x},{z})"); } catch { }
                }
            string s = sb.ToString();
            if (s == _wreckSeen) return;
            _wreckSeen = s;
            Plugin.Logger.LogInfo($"[DEBUG] WRECKS now: {(s.Length == 0 ? "none" : s)}");
        }
        catch { }
    }

    private static Tile NearestEnemyTile(TacticalManager tm, Actor from)
    {
        var fp = from.GetLastTile()?.GetTilePos();
        if (fp == null) return null;
        Tile best = null; int bestD = int.MaxValue;
        foreach (var e in DesyncDetector.AllActors(tm))
        {
            if (e == null || e.IsDying() || Ownership.IsKnownPlayerUnit(e) || ContainerSync.IsContained(e)) continue;
            // Skybots and turrets are allied AI, not player units (r64: the
            // recon picked ITSELF as the nearest enemy, distance 0).
            try { if (e.IsAlliedWithPlayer()) continue; } catch { }
            var t = e.GetLastTile();
            var p = t?.GetTilePos();
            if (p == null) continue;
            int d = Math.Abs(p.Value.x - fp.Value.x) + Math.Abs(p.Value.y - fp.Value.y);
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }

    // "queen" (BUGS.md #22, the element revive loop seen in a queen fight): an
    // alien queen beside the GUEST's infantry at round 2, so both players fight
    // it. Watch for "0 living here, host sent" and revive lines.
    private static bool _queenDone;

    private static void QueenScenario(TacticalManager tm)
    {
        if (_queenDone || CoopNet.Mode != CoopMode.Host) return;
        try { if (tm.GetRound() < 2) return; } catch { return; }
        if (!OurPlayerPhase(tm)) return;
        _queenDone = true;
        Actor guestSquad = null;
        foreach (var x in DesyncDetector.AllActors(tm))
        {
            if (x == null || x.IsDying() || !Ownership.IsKnownPlayerUnit(x) || Ownership.IsLocal(x)) continue;
            if (DesyncDetector.TemplateNameOf(x).Contains("vehicle")) continue;
            guestSquad = x; break;
        }
        var gp = guestSquad?.GetLastTile()?.GetTilePos();
        var qt = gp == null ? null : FreeTileNear(gp.Value.x, gp.Value.y, 4);
        if (qt == null) { Plugin.Logger.LogWarning("[DEBUG] scenario queen: no guest infantry or free tile"); return; }
        var qp = qt.GetTilePos();
        Plugin.Logger.LogInfo($"[DEBUG] scenario queen: warrior queen beside '{DesyncDetector.TemplateNameOf(guestSquad)}' at ({qp.x},{qp.y})");
        Request($"spawn|enemy.alien_01_big_warrior_queen|{qp.x}|{qp.y}");
    }

    // "rout" (BUGS.md #37, user 2026-09-27: "what happens when a unit retreats
    // off the map"). Rounds 2, 4 and 6 the host drops the enemy squad nearest
    // our units to morale 1. That write is the host's AI-morale relay
    // (SuppressionSync.RelayHostAi), so both machines agree on 1 -- and the next
    // hit, from EITHER player, breaks it on the shooter's machine. Then the
    // fleeing squad's retreat plays out. Each target is named once so the
    // ROSTER / MORALE-STATE lines on both logs can be followed to the end.
    private static int _routRound = -1, _routCount, _pinRound = -1;
    private static readonly List<Actor> _pinnedFleeing = new();
    private static void RoutScenario(TacticalManager tm)
    {
        int r;
        try { r = tm.GetRound(); } catch { return; }
        // Keep the FAR targets broken until they leave the board: a squad forced
        // to 0 recovers ~20 morale at its next turn start and stops fleeing
        // before it reaches the edge (r80), so the escape itself never ran.
        if (r != _pinRound && OurPlayerPhase(tm))
        {
            _pinRound = r;
            foreach (var a in _pinnedFleeing)
                try { if (a != null && !a.IsDying() && a.GetMorale() > 0f) { a.SetMorale(0f); Plugin.Logger.LogInfo($"[DEBUG] scenario rout: kept '{DesyncDetector.TemplateNameOf(a)}' fleeing (round {r})"); } }
                catch { }
        }
        if (r < 2 || r == _routRound || r % 2 != 0 || _routCount >= 3 || !OurPlayerPhase(tm)) return;
        _routRound = r;
        // Every other target is the enemy FARTHEST from our units: a squad broken
        // under our guns is shot dead before it reaches the edge (r71, r78), so
        // the escape itself (FleeSync) never ran. Far away, it gets to run.
        bool far = (_routCount % 2) == 1;
        Actor best = null; int bestD = far ? -1 : int.MaxValue;
        foreach (var e in DesyncDetector.AllActors(tm))
        {
            if (e == null || e.IsDying() || Ownership.IsKnownPlayerUnit(e) || ContainerSync.IsContained(e)) continue;
            try { if (e.IsAlliedWithPlayer() || e.GetMorale() <= 1f) continue; } catch { continue; }
            string n = DesyncDetector.TemplateNameOf(e);
            if (n.Contains("truck") || n.Contains("vehicle") || n.Contains("structure")) continue;
            var ep = e.GetLastTile()?.GetTilePos();
            if (ep == null) continue;
            int nearest = int.MaxValue;
            foreach (var u in DesyncDetector.AllActors(tm))
            {
                if (u == null || u.IsDying() || !Ownership.IsKnownPlayerUnit(u)) continue;
                var up = u.GetLastTile()?.GetTilePos();
                if (up == null) continue;
                nearest = Math.Min(nearest, Math.Abs(up.Value.x - ep.Value.x) + Math.Abs(up.Value.y - ep.Value.y));
            }
            if (nearest == int.MaxValue) continue;
            if (far ? nearest > bestD : nearest < bestD) { bestD = nearest; best = e; }
        }
        if (best == null) { Plugin.Logger.LogInfo($"[DEBUG] scenario rout: round {r}, no enemy squad to break"); return; }
        _routCount++;
        var bp = best.GetLastTile().GetTilePos();
        float before = best.GetMorale();
        // Near targets: morale 1, broken by the next hit. Far targets: 0 --
        // FLEEING at once (r79: at 1 a squad 24 tiles away is never hit, so it
        // never breaks and the escape never runs).
        best.SetMorale(far ? 0f : 1f);
        if (far) _pinnedFleeing.Add(best);
        Plugin.Logger.LogInfo($"[DEBUG] scenario rout #{_routCount}: '{DesyncDetector.TemplateNameOf(best)}' at ({bp.x},{bp.y}), {bestD} from our nearest unit: morale {before:F0} -> {best.GetMorale():F0}, living={best.GetElements()?.Count ?? -1}, state={best.GetMoraleState()}");
    }

    // "prout" (BUGS.md #97): rounds 3 and 6 the host breaks one of its OWN
    // infantry squads (morale 0). Its engine flees it into the fleeing faction and
    // rallies it at a later turn end; every client must follow both through the
    // owner's mstate relay -- look for "faction 2 -> 1 (owner's)" on the clients.
    private static int _proutRound = -1;
    private static void PlayerRoutScenario(TacticalManager tm)
    {
        int r;
        try { r = tm.GetRound(); } catch { return; }
        if ((r != 3 && r != 6) || r == _proutRound || !OurPlayerPhase(tm)) return;
        _proutRound = r;
        foreach (var a in DesyncDetector.AllActors(tm))
        {
            try
            {
                if (a == null || a.IsDying() || !Ownership.IsKnownPlayerUnit(a) || !Ownership.IsLocal(a)) continue;
                if (ContainerSync.IsContained(a) || a.GetMorale() <= 1f) continue;
                string n = DesyncDetector.TemplateNameOf(a);
                if (!n.StartsWith("player_squad.")) continue;
                float before = a.GetMorale();
                a.SetMorale(0f);
                Plugin.Logger.LogInfo($"[DEBUG] scenario prout: broke our own '{n}' (round {r}): morale {before:F0} -> {a.GetMorale():F0}, state={a.GetMoraleState()}, faction={a.GetFactionID()}");
                return;
            }
            catch { }
        }
        Plugin.Logger.LogInfo($"[DEBUG] scenario prout: round {r}, no squad of ours to break");
    }

    internal static void HarnessScript(TacticalManager tm)
    {
        if (LocalOn && Has("prout") && tm != null && CoopNet.Connected && CoopNet.Mode == CoopMode.Host) PlayerRoutScenario(tm);
        BugProbes.TickFleeing(tm);
        // "dronegrant": grant the drone skills only and let the AutoCombat brain
        // deploy and command them (user, 2026-09-27: drive drones like a player).
        if (LocalOn && (Has("drones") || Has("dronegrant")) && tm != null && CoopNet.Connected) DroneScenario(tm);
        if (LocalOn && Has("strike") && tm != null && CoopNet.Connected) StrikeScenario(tm);
        if (LocalOn && Has("ocimix") && tm != null && CoopNet.Connected) OciMixScenario(tm);
        // v5 "rename": the last seat renames itself at round 2; every log must
        // then show the new name in its roster ("[PEER] Players: ...").
        if (LocalOn && Has("rename") && !_renamed && CoopNet.Mode != CoopMode.Host
            && Peers.LocalSeat == System.Math.Max(1, (Plugin.SelfTestPlayers?.Value ?? 2) - 1))
        {
            int rr = -1; try { rr = tm.GetRound(); } catch { }
            if (rr >= 2)
            {
                _renamed = true;
                Plugin.Logger.LogWarning("[DEBUG] scenario rename: renaming this player to 'Renamed-" + Peers.LocalSeat + "'");
                Peers.SetLocalName("Renamed-" + Peers.LocalSeat);
            }
        }
        // v5: only the LAST seat drops, so at three players the other client
        // shows whether a rejoin leaves it undisturbed (at two: the guest, as before).
        if (LocalOn && (Has("rejoin") || Has("rejoinlate") || Has("rejoin2") || Has("rejoinmany")) && CoopNet.Mode != CoopMode.Host
            && (Peers.LocalSeat == System.Math.Max(1, (Plugin.SelfTestPlayers?.Value ?? 2) - 1)
                || (Has("rejoin2") && Peers.LocalSeat == System.Math.Max(1, (Plugin.SelfTestPlayers?.Value ?? 2) - 2))))
            GuestRejoin(tm);
        if (LocalOn && Has("oci") && tm != null && CoopNet.Connected) OciScenario(tm);
        if (LocalOn && Has("wreck") && tm != null && CoopNet.Connected) WreckScenario(tm);
        if (LocalOn && Has("queen") && tm != null && CoopNet.Connected) QueenScenario(tm);
        if (LocalOn && Has("rout") && tm != null && CoopNet.Connected && CoopNet.Mode == CoopMode.Host) RoutScenario(tm);
        if (!LocalOn || tm == null || !CoopNet.Connected) return;
        if (_battle != tm.Pointer) { _battle = tm.Pointer; _hostStep = 0; _nextTry = 0; }
        float now = Time.realtimeSinceStartup;
        if (now < _nextTry) return;
        try { if (tm.GetRound() < 2) return; } catch { return; }
        if (!OurPlayerPhase(tm)) return;

        if (CoopNet.Mode == CoopMode.Host) HostSteps(tm, now);
    }

    private static bool OurPlayerPhase(TacticalManager tm)
    {
        try
        {
            if (Replication.SyncPauseActive || !TurnControl.IsMyTurn) return false;
            return tm.GetActiveFactionID() == tm.GetPlayerFaction().GetIndex();
        }
        catch { return false; }
    }

    // [MenaceCoop_Dev] SyncedDebugScenario. DEFAULT "none": a clean mission.
    // Injected units confuse the AutoCombat brain and its logs (user, 2026-09-26:
    // "remove debug artifacts if not in use"), so a scenario runs only when a run
    // is staged for it:
    //   "revive"    -- one hostile beside the host's infantry, so a squad leader
    //                  goes down and AutoCombat's revive path runs.
    //   "structure" -- the host destroys one structure locally at round 2; the
    //                  guest can only learn of it through StructureSync's relay.
    // Removed 2026-09-26 (served their purpose): the VIP boarding the guest's
    // carrier (#6, never reproduced), OCI/drone grants (drone grant threw every
    // run), and the old "all"/"vip" scenarios.
    private static string Scenario => (Plugin.SyncedDebugScenario?.Value ?? "none").Trim().ToLowerInvariant();
    // Scenarios combine: "rejoin,grenades,oci" runs all three in one mission.
    internal static bool Has(string s) => Array.IndexOf(Scenario.Split(','), s) >= 0;

    private static void HostSteps(TacticalManager tm, float now)
    {
        // "battlesave": save at round 3, resume it at round 4 -- the host relaunches,
        // catches up to round 3, then reloads the guest in (BattleSave). ONCE per
        // session: the resumed battle is a new battle and would otherwise loop.
        if (Has("battlesave"))
        {
            if (_battleSaveStep >= 2) return;
            int r;
            try { r = tm.GetRound(); } catch { return; }
            if (_battleSaveStep == 0 && r >= 3)
            {
                _battleSaveStep = 1;
                bool ok = BattleSave.Save(out string why);
                Plugin.Logger.LogInfo($"[DEBUG] scenario battlesave: saved at round {r}: {(ok ? "ok" : why)}");
                if (!ok) _battleSaveStep = 2;
            }
            else if (_battleSaveStep == 1 && r >= 4)
            {
                _battleSaveStep = 2;
                bool ok = BattleSave.Resume(out string why);
                Plugin.Logger.LogInfo($"[DEBUG] scenario battlesave: resuming at round {r}: {(ok ? "started" : why)}");
            }
            return;
        }
        // "grenades" (for the AutoCombat chat, user request 2026-09-26): the test
        // campaign's squads carry none. At round 2, one host and one guest
        // infantry squad get the frag grenade (pirates use it, so its template is
        // loaded) and the AT bundle if this campaign has loaded that one too.
        // Synced, so both machines hold the same skill for relayed throws.
        if (Has("grenades"))
        {
            if (_hostStep != 0) return;
            _hostStep = 1;
            Actor mine = null, theirs = null;
            foreach (var x in DesyncDetector.AllActors(tm))
            {
                if (x == null || x.IsDying() || !Ownership.IsKnownPlayerUnit(x)) continue;
                if (DesyncDetector.TemplateNameOf(x).Contains("vehicle")) continue;
                if (Ownership.IsLocal(x)) mine ??= x; else theirs ??= x;
            }
            foreach (var who in new[] { mine, theirs })
            {
                var gp = who?.GetLastTile()?.GetTilePos();
                if (gp == null) continue;
                foreach (var skill in new[] { "active.throw_scrap_bomb_frag_tier1", "active.throw_anti_tank_grenade_tier1_grenade_bundle" })
                    Request($"grantskill|{gp.Value.x}|{gp.Value.y}|{skill}");
            }
            Plugin.Logger.LogInfo($"[DEBUG] scenario grenades: granted to '{DesyncDetector.TemplateNameOf(mine)}' (host) and '{DesyncDetector.TemplateNameOf(theirs)}' (guest)");
            return;
        }
        // "perks" (BUGS.md #90): at round 2 every player infantry squad, the
        // host's and the guests', gets the counterattack and Overtime perks, so
        // enemy fire triggers perk attacks for PerkAttackProbe to watch.
        if (Has("perks"))
        {
            if (_hostStep != 0) return;
            try { if (tm.GetRound() < 2) return; } catch { return; }
            _hostStep = 1;
            int n = 0;
            foreach (var x in DesyncDetector.AllActors(tm))
            {
                if (x == null || x.IsDying() || !Ownership.IsKnownPlayerUnit(x)) continue;
                if (DesyncDetector.TemplateNameOf(x).Contains("vehicle")) continue;
                var gp = x.GetLastTile()?.GetTilePos();
                if (gp == null) continue;
                foreach (var perk in new[] { "perk.counter_strike", "perk.overtime_murdock" })
                    Request($"grantskill|{gp.Value.x}|{gp.Value.y}|{perk}");
                n++;
            }
            Plugin.Logger.LogInfo($"[DEBUG] scenario perks: counter_strike + overtime_murdock requested for {n} player squad(s)");
            return;
        }
        if (_hostStep != 0) return;
        // "reload": at round 3, press FIX DESYNC -- the guest is rebuilt from the
        // battle-start save and has to roll two rounds to catch up (BUGS.md #2).
        if (Has("reload"))
        {
            try { if (tm.GetRound() < 3) return; } catch { return; }
            _hostStep = 1;
            Plugin.Logger.LogInfo("[DEBUG] scenario reload: reloading the guest into the battle");
            GuestReload.Start("harness scenario");
            return;
        }
        _hostStep = 1;
        if (Has("structure")) { DestroyStructureNearUs(tm); return; }
        if (!Has("revive")) return;

        RefreshTemplates();
        Actor hostSquad = null;
        foreach (var x in DesyncDetector.AllActors(tm))
        {
            if (x == null || x.IsDying() || !Ownership.IsKnownPlayerUnit(x) || !Ownership.IsLocal(x)) continue;
            if (DesyncDetector.TemplateNameOf(x).Contains("vehicle")) continue;
            hostSquad = x; break;
        }
        var hp = hostSquad?.GetLastTile()?.GetTilePos();
        if (hp == null) { Plugin.Logger.LogWarning("[DEBUG] scenario revive: no host infantry to stage beside"); return; }
        string foe = null;
        foreach (var n in _templates) if (n.StartsWith("enemy.") && !n.Contains("truck") && !n.Contains("vehicle")) { foe = n; break; }
        var ft = FreeTileNear(hp.Value.x, hp.Value.y, 3);
        if (foe == null || ft == null) { Plugin.Logger.LogWarning("[DEBUG] scenario revive: no hostile template or free tile"); return; }
        var p = ft.GetTilePos();
        Plugin.Logger.LogInfo($"[DEBUG] scenario revive: '{foe}' beside '{DesyncDetector.TemplateNameOf(hostSquad)}'");
        Request($"spawn|{foe}|{p.x}|{p.y}");
    }

    // STRUCTURE RELAY TEST (BUGS.md #21): destroy one structure near a host unit
    // the way a host-side kill would -- locally, NOT as a broadcast debug command,
    // so the only way the guest learns of it is StructureSync's "sdie" relay.
    // Also moves any "destroy no more than N structures" counter (objprog).
    private static void DestroyStructureNearUs(TacticalManager tm)
    {
        Structure best = null; int bestD = int.MaxValue; Vector2Int bestP = default;
        var list = tm.m_Structures;
        int n = list?.Count ?? 0;
        foreach (var x in DesyncDetector.AllActors(tm))
        {
            if (x == null || !x.IsAlive() || !Ownership.IsKnownPlayerUnit(x) || !Ownership.IsLocal(x)) continue;
            var p = x.GetLastTile()?.GetTilePos();
            if (p == null) continue;
            for (int i = 0; i < n; i++)
            {
                var st = list[i];
                try
                {
                    if (st == null || !st.IsAlive()) continue;
                    var q = st.GetTile()?.GetTilePos();
                    if (q == null) continue;
                    int d = Math.Max(Math.Abs(q.Value.x - p.Value.x), Math.Abs(q.Value.y - p.Value.y));
                    if (d < bestD) { bestD = d; best = st; bestP = new Vector2Int(q.Value.x, q.Value.y); }
                }
                catch { }
            }
        }
        if (best == null) { Plugin.Logger.LogWarning($"[DEBUG] scenario: no living structure among {n} -- structure relay not exercised"); return; }
        Plugin.Logger.LogInfo($"[DEBUG] scenario: destroying structure '{StructureSync.Name(best)}' at ({bestP.x},{bestP.y}), {bestD} tiles from our nearest unit, {n} on the map -- locally; the guest must learn it from the relay");
        try { best.Die(false); } catch (Exception e) { Plugin.Logger.LogWarning("[DEBUG] scenario: structure Die threw " + e.Message); }
    }

    private static Tile FreeTileNear(int x, int y, int r)
    {
        for (int d = 1; d <= r + 1; d++)
            for (int dx = -d; dx <= d; dx++)
                for (int dy = -d; dy <= d; dy++)
                {
                    var t = Replication.GetTileAt(x + dx, y + dy);
                    try { if (t != null && t.GetActor() == null && (dx != 0 || dy != 0)) return t; } catch { }
                }
        return null;
    }

    // Whose side does a spawned template go on? The side of any unit already
    // fielding it; otherwise read it off the name.
    private static FactionType FactionFor(TacticalManager tm, string template)
    {
        try
        {
            foreach (var x in DesyncDetector.AllActors(tm))
                if (x != null && DesyncDetector.TemplateNameOf(x) == template) return x.GetFaction();
        }
        catch { }
        if (template.StartsWith("enemy.")) return FactionType.Pirates;
        if (template.StartsWith("allied.")) return FactionType.AlliedLocalForces;
        if (template.StartsWith("player_")) return FactionType.Player;
        return FactionType.Civilian;   // civilians, hostages, VIPs
    }
}
