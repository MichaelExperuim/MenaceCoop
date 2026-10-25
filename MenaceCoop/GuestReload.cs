using System;
using System.Collections.Generic;
using System.IO;
#if MELON
using Il2CppMenace.Tactical;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.Tactical;
#endif

namespace MenaceCoop;

// FIX DESYNC AS A RELOAD, AND REJOINING A BATTLE IN PROGRESS (BUGS.md #2, #1).
//
// A battle cannot be saved, so incremental reconciliation was the only repair
// there was -- and it cannot converge on a whole board: it pushes unit state onto
// whatever the guest's engine believes, and the engine's own bookkeeping (turn
// order, containment, spawned waves, structures) is outside its reach. Players
// reported the button "never fixes anything".
//
// So the guest is rebuilt from scratch instead, the way it entered in the first
// place:
//
//   1. The host freezes (sync pause, no timeout while this runs) and re-ships
//      the battle-start autosave plus the same mission/seed/deployment.
//   2. The guest leaves its battle, loads that save and relaunches the mission,
//      regenerating the pristine round-1 board. Identity is assigned exactly as
//      on first entry (slot index on a pristine board).
//   3. The guest asks to catch up. The host answers with its STATE JOURNAL --
//      the relayed messages whose effect is permanent state rather than an order
//      (reinforcement budgets, faction changes, unloads, destroyed structures,
//      objectives, ship-support uses) -- then its round and a full snapshot.
//   4. The guest rolls its rounds forward with the engine's own NextRound (never
//      m_Round: CLAUDE.md), which also rolls the same pinned reinforcement waves,
//      replays the journal, and applies the snapshot as a HARD restore twice.
//   5. The guest reports ready through the normal handshake; the host resumes.
//
// A guest that connects while the host is already in a battle takes the same
// path, which is the whole of the rejoin feature.
internal static class GuestReload
{
    // ---- HOST: how to regenerate this battle -------------------------------
    private static IntPtr _battle;
    private static string _saveName;
    private static int _missionIdx = -1, _seed;
    private static string _deploy = "";

    internal static void RecordBattleEntry(string saveName, int missionIdx, int seed, string deploy)
    {
        _battle = TacticalManager.Get()?.Pointer ?? IntPtr.Zero;
        _saveName = saveName;
        _missionIdx = missionIdx;
        _seed = seed;
        _deploy = deploy ?? "";
        Plugin.Logger.LogInfo($"[RELOAD] Battle entry recorded: save '{saveName}', mission idx={missionIdx}, seed={seed}");
    }

    internal static bool CanJoinMidBattle(TacticalManager tm) => CanReload(tm);

    private static bool CanReload(TacticalManager tm)
        => tm != null && _saveName != null && _missionIdx >= 0 && tm.Pointer == _battle
           && File.Exists(SaveTransfer.PathFor(_saveName));

    // Everything a later resume needs (BattleSave).
    internal static bool TryGetResumeData(TacticalManager tm, out BattleSave.Data d)
    {
        d = null;
        if (!CanReload(tm)) return false;
        d = new BattleSave.Data
        {
            SaveName = _saveName, MissionIdx = _missionIdx, Seed = _seed, Deploy = _deploy,
            GuestSlots = Ownership.LastGuestSlots,
            Round = tm.GetRound(), Faction = tm.GetActiveFactionID(),
            Snapshot = DesyncDetector.BuildSnapshotFor(tm),
        };
        if (_journalBattle == tm.Pointer)
            foreach (var j in _journal) d.Journal.Add(j.Clone());
        return true;
    }

    // ---- HOST: the state journal ------------------------------------------
    // Only messages whose effect OUTLIVES the moment -- never orders. Orders are
    // already folded into the snapshot (positions, HP, deaths).
    private static readonly HashSet<string> Journaled = new()
        { "reinfs", "setfac", "unload", "sdie", "obj", "ociuse", "flee",
          // Synced debug (spawn / kill / grant skill) changes the board for good
          // too. r103: the harness granted the deploy skill while a client was
          // out, so the rejoined client could not replay the journaled deploy.
          "dbg" };
    private static readonly List<CoopMsg> _journal = new();
    private static IntPtr _journalBattle;

    // Called for every message the host SENDS and every one it APPLIES.
    internal static void Note(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Host || m?.T == null || !Journaled.Contains(m.T)) return;
        if (m.T == "dbg" && TryParseGrant(m.Data, out int gx, out int gy, out _))
        {
            // A grant names the unit by its tile NOW; by catch-up it has moved.
            // Stamp who it is, so the replay can find it wherever it stands.
            var c = m.Clone();
            try { var a = Replication.GetTileAt(gx, gy)?.GetActor(); if (a != null) Replication.StampIds(c, a, false); } catch { }
            AddToJournal(c);
            return;
        }
        AddToJournal(m);
    }

    private static bool TryParseGrant(string data, out int x, out int y, out string skill)
    {
        x = y = 0; skill = null;
        var a = data?.Split('|');
        if (a == null || a.Length != 4 || a[0] != "grantskill") return false;
        skill = a[3];
        return int.TryParse(a[1], out x) && int.TryParse(a[2], out y);
    }

    // Catch-up: re-aim a stamped grant at the unit's tile on this (snapshotted) board.
    private static void RetargetGrant(CoopMsg m)
    {
        if (m.T != "dbg" || !TryParseGrant(m.Data, out _, out _, out string skill)) return;
        if (m.RId < 0 && m.CId == -1) return;
        var a = Replication.ResolveOrderActor(m);
        var t = a?.GetLastTile();
        if (t == null) return;
        var (x, y) = ReplicationPatches.TilePos(t);
        m.Data = $"grantskill|{x}|{y}|{skill}";
    }

    // ---- HOST: unloads (#57/#67) ------------------------------------------------
    // An AI transport unloads by giving each passenger a Leave MOVE, relayed as an
    // ordinary order and therefore never journaled. While the crew lives, the
    // snapshot ejects it on a rebuilt board; once it is DEAD the snapshot has no
    // row for it, the rebuild keeps it aboard, and its replayed death happened
    // inside the vehicle -- no bodies, the "loaded" truck never placed (hand test
    // 2026-10-04 10:55/10:58). Every accepted Leave is journaled, alone or not.
    internal static void NoteLeave(Actor a, Tile dest)
    {
        var tm = TacticalManager.Get();
        if (CoopNet.Mode != CoopMode.Host || tm == null || a == null || dest == null) return;
        if (_replaying || LocalCatchingUp) return;
        try { if (tm.GetRound() < 1) return; } catch { return; }
        var p = dest.GetTilePos();
        var m = new CoopMsg { T = "leave", AX = p.x, AY = p.y };
        Replication.StampIds(m, a, isOrder: false);
        AddToJournal(m);
        Plugin.Logger.LogInfo($"[RELOAD] Journaled unload: '{DesyncDetector.TemplateNameOf(a)}' -> ({p.x},{p.y}) cid={m.CId} rid={m.RId}");
    }

    // Catch-up replay of a journaled unload: the engine's own dismount.
    internal static void ApplyLeave(CoopMsg m)
    {
        var a = Replication.ResolveOrderActor(m);
        if (a == null) { Plugin.Logger.LogInfo($"[RELOAD] Journaled unload to ({m.AX},{m.AY}) cid={m.CId}: not on this board"); return; }
        if (!ContainerSync.IsContained(a)) return;   // already out
        var t = Replication.GetTile(m.AX, m.AY);
        bool ok = t != null && (ContainerSync.LeaveLikeEngine(a, t) || ContainerSync.ForceLeaveContainer(a, t));
        Plugin.Logger.LogInfo($"[RELOAD] Replayed unload: '{DesyncDetector.TemplateNameOf(a)}' -> ({m.AX},{m.AY}){(ok ? "" : " -- REFUSED, still aboard")}");
    }

    private static void AddToJournal(CoopMsg m)
    {
        var tm = TacticalManager.Get();
        if (tm == null) return;
        if (tm.Pointer != _journalBattle) { _journalBattle = tm.Pointer; _journal.Clear(); }
        _journal.Add(m.Clone());
    }

    // ---- HOST: summons (#62) ------------------------------------------------
    // A skybot -- any minion a PLAYER unit deploys -- comes from a deploy SKILL,
    // and orders are not journaled, so a rejoined client never had it (r99: 48
    // "never resolved" orders for the drones' coop ids). The host remembers the
    // last skill message (its own, or one it applied for a client); when that
    // skill spawns something for the same actor, the skill itself is journaled.
    // Replaying it re-runs the engine's own deploy, which also binds the
    // move/strike commands to the new minion -- a bare spawn would not. Coop ids
    // count per faction+template, so replaying in journal order gives the same id.
    // AI summons stay out: their waves are journaled as reinfs already.
    private static CoopMsg _lastSkill, _lastSummonJournaled;
    private static IntPtr _lastSkillActor;

    internal static void NoteSkill(CoopMsg m, Actor a)
    {
        if (CoopNet.Mode != CoopMode.Host || m == null || a == null) return;
        _lastSkill = m;
        _lastSkillActor = a.Pointer;
    }

    internal static void NoteSummon(Actor user, Entity spawned)
    {
        if (CoopNet.Mode != CoopMode.Host || user == null || spawned == null || _lastSkill == null) return;
        if (_replaying || LocalCatchingUp) return;
        if (user.Pointer != _lastSkillActor || ReferenceEquals(_lastSkill, _lastSummonJournaled)) return;
        if (!Ownership.IsKnownPlayerUnit(user)) return;
        AddToJournal(_lastSkill);
        _lastSummonJournaled = _lastSkill;
        Plugin.Logger.LogInfo($"[RELOAD] Journaled summon: {_lastSkill.SkillId} by {DesyncDetector.TemplateNameOf(user)} -> ({_lastSkill.TX},{_lastSkill.TY})");
    }

    // ---- HOST: crushed half-cover (#61) --------------------------------------
    // A vehicle driving through fences/props destroys their half-cover on every
    // machine that SEES it drive. A rejoined client only gets the vehicle
    // teleported to where it ended up, so that cover stays standing there (r98 +
    // r99: the same 5 tiles on the carrier's path, rejoined client 0 -> 8 cover).
    // Every removal during the battle is journaled by tile + the cover's world
    // position; catch-up removes the matching instance.
    internal static void NoteHalfCoverRemoved(Tile tile, HalfCover hc, bool mirror)
    {
        if (CoopNet.Mode != CoopMode.Host || tile == null || hc == null) return;
        if (_replaying || LocalCatchingUp) return;
        var tm = TacticalManager.Get();
        if (tm == null || tm.GetRound() < 1) return;   // map generation / deployment
        var (x, y) = ReplicationPatches.TilePos(tile);
        var p = hc.m_Position;
        AddToJournal(new CoopMsg { T = "hcrm", TX = x, TY = y, CX = p.x, CY = p.z, Prm = mirror ? 1 : 0 });
        Plugin.Logger.LogInfo($"[COVER] Half-cover removed at ({x},{y}) -- journaled");
    }

    // ---- HOST: whole-unit deaths (#66) ----------------------------------------
    // The snapshot only settles ELEMENTS of units both boards list; a unit the
    // host destroyed outright stayed alive on a rejoined client (hand test
    // 2026-10-03 22:02: light truck, chaingun team, heavy truck all killed before
    // M3's crash, all back on M3 -> MAPS DIFFER + ENEMY FORCES DIFFER, and FIX
    // DESYNC would have done the same). Every death on the host is journaled,
    // including ones applied from a client's relay; escapes keep their own "flee".
    internal static void NoteActorDeath(Actor a)
    {
        if (CoopNet.Mode != CoopMode.Host || a == null) return;
        if (_replaying || LocalCatchingUp) return;
        var tm = TacticalManager.Get();
        if (tm == null || tm.GetRound() < 1) return;
        try { if (a.IsDying()) return; } catch { }
        if (FleeSync.IsEscape(a)) return;   // journaled as "flee" by FleeSync
        var p = a.GetLastTile()?.GetTilePos();
        var m = new CoopMsg { T = "adie", AX = p?.x ?? -1, AY = p?.y ?? -1 };
        Replication.StampIds(m, a, isOrder: false);
        AddToJournal(m);
        Plugin.Logger.LogInfo($"[RELOAD] Journaled death: '{DesyncDetector.TemplateNameOf(a)}' at ({m.AX},{m.AY}) cid={m.CId}");
    }

    // Catch-up replay of a journaled death.
    internal static void ApplyActorDeath(CoopMsg m)
    {
        var a = Replication.ResolveOrderActor(m);
        if (a == null) { Plugin.Logger.LogInfo($"[RELOAD] Journaled death at ({m.AX},{m.AY}) cid={m.CId}: not on this board"); return; }
        try { if (a.IsDying()) return; } catch { }
        // STILL ABOARD ON THE FIRST PASS = ITS UNLOAD IS STILL PLAYING. A replayed
        // "leave" is a real (animated) dismount; dying now would die inside the
        // vehicle (no bodies). The deaths re-run after the first snapshot pass.
        // ...or still MOVING: a dismount counts as "out" the moment it starts, and a
        // death 20 ms later left no body (hand test 2026-10-04 11:02).
        bool midMove = false;
        try { midMove = a.IsMoving(); } catch { }
        if ((ContainerSync.IsContained(a) || midMove) && !_deathsRetried)
        {
            Plugin.Logger.LogInfo($"[RELOAD] '{DesyncDetector.TemplateNameOf(a)}' still aboard -- its death waits for the second pass");
            return;
        }
        // ONCE PER UNIT. A dead unit is not "dying", so the retry pass (and the
        // host's duplicate entries) killed the light truck a SECOND time 6 s after
        // the first, and its wreck/cover never appeared (hand test 2026-10-04 08:56).
        if (!_deathsApplied.Add(a.Pointer)) return;
        try { if ((a.GetElements()?.Count ?? 0) == 0) return; } catch { }
        bool prev = Replication.ApplyingRemote;
        Replication.ApplyingRemote = true;
        try
        {
            // DIE WHERE IT DIED. Deaths replay in pass 1, before the snapshot has
            // moved anyone, so a vehicle died on its battle-start spawn tile and left
            // its wreck (and cover) there (hand test 2026-10-04: light truck wreck at
            // (31,22) on the rejoined client, (15,21) on the host -- MAPS DIFFER).
            // OUT OF ITS VEHICLE FIRST. An unload the host made while ALONE is not
            // journaled, so the crew can still be aboard here; killing it aboard
            // left no bodies (hand test 2026-10-04 10:51: chaingun crew died "inside"
            // the truck at (31,22), host had them dead at (7,21)).
            string ejected = "";
            if (ContainerSync.IsContained(a) && m.AX >= 0)
            {
                var deathTile = Replication.GetTile(m.AX, m.AY);
                ejected = deathTile != null && ContainerSync.ForceLeaveContainer(a, deathTile) ? " (ejected from its vehicle first)" : " (still aboard -- eject refused)";
            }
            var at = a.GetLastTile()?.GetTilePos();
            string moved = ejected;
            if (m.AX >= 0 && at != null && (at.Value.x != m.AX || at.Value.y != m.AY))
            {
                var dest = Replication.GetTile(m.AX, m.AY);
                var occ = dest == null ? null : Replication.ActorOn(dest);
                if (dest != null && (occ == null || occ.Pointer == a.Pointer)
                    && DesyncDetector.PlaceDirectly(a, dest, m.AX, m.AY, out _))
                    moved = $" (moved from ({at.Value.x},{at.Value.y}) first)";
                else moved = $" (could NOT move it from ({at.Value.x},{at.Value.y}) first)";
            }
            // THE LIVE KILL'S ROAD, NOT A BARE Die(). A vehicle's wreck (Tile.Wreckage,
            // its cover) comes from the element death; Actor.Die() alone skips it --
            // r142: both replayed trucks died on the right tiles and left NO wreck
            // (MAPS DIFFER cover 15->0 on exactly those tiles), while the live
            // relay's element-death path made one on every other machine.
            var living = new List<Element>();
            try { var els = a.GetElements(); if (els != null) for (int i = 0; i < els.Count; i++) if (els[i] != null) living.Add(els[i]); } catch { }
            foreach (var el in living)
            {
                int hp = 0;
                try { hp = el.GetHitpoints(); el.SetHitpoints(0); } catch { }
                Replication.KillElementLikeEngine(el, null, System.Math.Max(hp, 1));
            }
            try { a.UpdateHitpoints(); } catch { }
            Replication.MarkDeadIfNoLivingElements(a);
            bool stillUp = false;
            try { stillUp = !a.IsDying() && (a.GetElements()?.Count ?? 0) > 0; } catch { }
            if (stillUp) a.Die(false);
            Plugin.Logger.LogInfo($"[RELOAD] Replayed death: '{DesyncDetector.TemplateNameOf(a)}' at ({m.AX},{m.AY}){moved}{(stillUp ? " [bare Die fallback]" : "")}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[RELOAD] Replaying a death threw: {e.Message}"); }
        finally { Replication.ApplyingRemote = prev; }
    }

    // Entries addressed by a unit's CURRENT tile (synced debug: "unit at (x,y)")
    // or cast from one (a summon) must wait until the first snapshot pass has put
    // every unit where the host has it (r107: all 7 harness grants missed on the
    // battle-start board, so the journaled deploy had no skill to replay).
    // setfac waits too: an abandoned transport must be moved first.
    private static bool _lateReplayed, _deathsRetried;
    private static readonly HashSet<IntPtr> _deathsApplied = new();
    private static int _summonsAwaited;

    // Client, mid catch-up: a replayed deploy's minion has spawned.
    internal static void NoteSummonLanded(Actor user)
    {
        if (!GuestCatchingUp || _summonsAwaited <= 0 || user == null || !Ownership.IsKnownPlayerUnit(user)) return;
        _summonsAwaited--;
        Plugin.Logger.LogInfo($"[RELOAD] Summon landed ({_summonsAwaited} still to come)");
    }

    private static bool AfterFirstPass(CoopMsg b) => b.T == "setfac" || b.T == "dbg" || b.T == "skill";

    private static bool ReplayHalfCoverRemoval(CoopMsg m)
    {
        var tile = TacticalManager.Get()?.GetMap()?.GetTile(m.TX, m.TY);
        var list = tile?.GetHalfCoverInstances();
        if (list == null) return false;
        for (int i = 0; i < list.Count; i++)
        {
            var hc = list[i];
            if (hc == null) continue;
            var p = hc.m_Position;
            if (Math.Abs(p.x - m.CX) > 0.05f || Math.Abs(p.z - m.CY) > 0.05f) continue;
            try { hc.OnDie(); } catch (Exception e) { Plugin.Logger.LogWarning($"[COVER] OnDie threw: {e.Message}"); }
            try { if (tile.GetHalfCoverInstances().Contains(hc)) tile.RemoveHalfCoverInstance(hc, m.Prm == 1); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[COVER] remove threw: {e.Message}"); }
            return true;
        }
        return false;   // already gone here (removed by the mirror call, or never generated)
    }

    // ---- HOST: run a reload -----------------------------------------------
    internal static bool HostActive;
    private static float _hostSince;
    private const float HostFailsafe = 240f;

    // v5: the client being reloaded (-1 = every client: a host-side FIX DESYNC or
    // battle resume reloads them all). A client's own request or a mid-battle
    // join reloads only that client.
    private static int _target = -1;
    // ---- EVERYONE: "player X is rejoining" pause (user, 2026-10-03) -----------
    // While one client reloads into the battle, the host holds the board -- and
    // the OTHER clients used to just sit there with no word why. Now the host
    // tells them (rjhold / rjdone); every screen shows who is rejoining and the
    // sync pause refuses actions until they are back.
    internal static string RejoiningName { get; private set; }
    private static int _rejoiningSeat = -1;

    internal static void OtherRejoining(CoopMsg m)
    {
        if (m.Prm == Peers.LocalSeat) return;   // the one rejoining has its own screen
        RejoiningName = string.IsNullOrEmpty(m.Data) ? "A player" : m.Data;
        _rejoiningSeat = m.Prm;
        Plugin.Logger.LogWarning($"[RELOAD] {RejoiningName} is rejoining -- pausing until they are back");
        DesyncDetector.StartSyncPause($"{RejoiningName} rejoining", notifyPartner: false, hostPush: false);
        CoopRunner.SetSyncBlocker(true, $"{RejoiningName.ToUpperInvariant()} IS REJOINING -- PLEASE WAIT");
        CoopNet.Status = $"{RejoiningName} is rejoining -- please wait";
    }

    internal static void OtherRejoined(CoopMsg m)
    {
        if (m.Prm == Peers.LocalSeat || RejoiningName == null) return;
        string who = RejoiningName;
        ClearRejoining();
        DesyncDetector.EndSyncPause($"{who} rejoined");
        CoopRunner.ShowDesyncWarning($"{who} is back -- game resumed");
    }

    // Disconnect: drop the name with the pause.
    internal static void ClearRejoining() { RejoiningName = null; _rejoiningSeat = -1; }

    // The one rejoining dropped out again: nobody is coming back, resume.
    internal static void OnRoster(ICollection<int> seats)
    {
        if (RejoiningName == null || _rejoiningSeat < 0 || seats.Contains(_rejoiningSeat)) return;
        string who = RejoiningName;
        ClearRejoining();
        DesyncDetector.EndSyncPause($"{who} left again");
        CoopRunner.ShowDesyncWarning($"{who} left -- game resumed");
    }

    private static void Out(CoopMsg m)
    {
        if (_target > 0) CoopNet.SendTo(m, _target); else CoopNet.Send(m);
    }

    internal static void Start(string why, int target = -1)
    {
        if (CoopNet.Mode != CoopMode.Host)
        {
            CoopNet.Send(new CoopMsg { T = "reqreload", Data = why });
            CoopNet.Status = "Asked the host to reload you into the battle";
            Plugin.Logger.LogInfo($"[RELOAD] Asked the host for a reload ({why})");
            return;
        }
        var tm = TacticalManager.Get();
        if (!CoopNet.Connected || tm == null) { CoopNet.Status = "Not in a co-op battle -- nothing to reload"; return; }
        if (HostActive)
        {
            // TWO DROPPED PLAYERS (user 2026-10-04). A second one reconnecting while
            // the first is still loading in used to be IGNORED -- connected, never
            // brought into the battle. Queue it: EndHost brings it in next, and the
            // pause holds unbroken until the last one is fully in.
            if (target > 0 && target != _target && !_rejoinQueue.Contains(target))
            {
                _rejoinQueue.Add(target);
                Plugin.Logger.LogWarning($"[RELOAD] {Peers.NameOf(target)} reconnected while {RejoiningName} is still loading in -- queued ({_rejoinQueue.Count} waiting); everyone stays paused");
            }
            else Plugin.Logger.LogInfo($"[RELOAD] Already reloading the guest; ignoring '{why}'");
            return;
        }
        if (!CanReload(tm))
        {
            // No battle-entry record (e.g. the host itself loaded mid-way): the old
            // unit-state push is all there is.
            Plugin.Logger.LogWarning($"[RELOAD] Cannot reload ({why}): no battle-entry save for this battle -- pushing the host board instead");
            DesyncDetector.ForceGuestToHostState(false);
            return;
        }

        HostActive = true;
        _target = target;
        _hostSince = UnityEngine.Time.realtimeSinceStartup;
        int round = 0;
        try { round = tm.GetRound(); } catch { }
        Plugin.Logger.LogWarning($"[RELOAD] Reloading the guest into the battle ({why}); host round {round}, journal {_journal.Count} entr{(_journal.Count == 1 ? "y" : "ies")}");
        DesyncDetector.StartSyncPause($"guest reloading the battle ({why})", notifyPartner: false, hostPush: false);
        RejoiningName = target > 0 ? Peers.NameOf(target) : "Your partner";
        CoopRunner.SetSyncBlocker(true, $"{RejoiningName.ToUpperInvariant()} IS REJOINING -- PLEASE WAIT");
        CoopNet.Status = $"{RejoiningName} is rejoining -- please wait";
        // The other clients pause too (the one rejoining ignores it by seat).
        if (target > 0) CoopNet.Send(new CoopMsg { T = "rjhold", Prm = target, Data = RejoiningName });
        // v5: un-ready only the client being reloaded; the others are still in.
        if (target > 0) Peers.Unready(target);
        else SessionSync.PartnerBattleConfirmed = false;

        // Order matters: "reload" arms the guest's catch-up BEFORE its load runs.
        Out(new CoopMsg { T = "reload", Prm = round, Data = why });
        string path = SaveTransfer.PathFor(_saveName);
        SaveTransfer.SendFile(path, _target);
        string jpg = System.IO.Path.ChangeExtension(path, ".jpg");
        if (File.Exists(jpg)) SaveTransfer.SendFile(jpg, _target);
        Out(new CoopMsg { T = "loadreq", FName = _saveName });
        if (target > 0) Ownership.AssignForLateJoin();   // battle started alone: give the joiner squads
        string slots = Ownership.LastGuestSlots;
        if (slots != null) Out(new CoopMsg { T = "own", Data = slots, Seats = Ownership.LastSeats });
        Out(new CoopMsg { T = "startmission", MIdx = _missionIdx, Seed = _seed, Data = _deploy });
    }

    private static void HandleCatchRequest()
    {
        var tm = TacticalManager.Get();
        if (CoopNet.Mode != CoopMode.Host || tm == null) return;
        if (!HostActive)
        {
            // A guest that restarted mid-reload, or a stray request: still answer
            // -- a catch-up is always safe to give -- but hold while it runs.
            HostActive = true;
            _hostSince = UnityEngine.Time.realtimeSinceStartup;
            DesyncDetector.StartSyncPause("guest catching up", notifyPartner: false, hostPush: false);
        }
        foreach (var j in _journal)
        {
            var c = j.Clone();
            c.T = "cu:" + j.T;
            c.To = -1;
            Out(c);
        }
        int round = tm.GetRound();
        Out(new CoopMsg
        {
            T = "catchup", Prm = round, MIdx = tm.GetActiveFactionID(),
            Data = DesyncDetector.BuildSnapshotFor(tm)
        });
        Plugin.Logger.LogInfo($"[RELOAD] Guest asked to catch up: sent {_journal.Count} journal entr{(_journal.Count == 1 ? "y" : "ies")} + round {round} + snapshot");
    }

    // The guest's "ready" after a catch-up ends the reload (Replication "ready").
    internal static void OnPartnerReady(int from = -1)
    {
        if (!HostActive) return;
        if (_target > 0 && from > 0 && from != _target) return;   // another client's ready
        EndHost("guest caught up and ready");
        // The map fingerprint was compared against the guest's OLD battle; check
        // the regenerated one.
        try { TerrainCheck.Publish(TacticalManager.Get()); } catch { }
    }

    private static readonly List<int> _rejoinQueue = new();

    private static void EndHost(string why)
    {
        HostActive = false;
        // NEXT IN THE QUEUE, WITHOUT RELEASING THE PAUSE. Seats that left again
        // are dropped. Start() re-sends rjhold for the next player, which also
        // pauses the one who just finished; rjdone goes out only when the queue
        // is empty, so everyone resumes together.
        var seats = new HashSet<int>(Peers.SessionSeats());
        _rejoinQueue.RemoveAll(s => !seats.Contains(s));
        if (_rejoinQueue.Count > 0 && CoopNet.Connected)
        {
            int next = _rejoinQueue[0];
            _rejoinQueue.RemoveAt(0);
            Plugin.Logger.LogWarning($"[RELOAD] {RejoiningName} is in ({why}); bringing in {Peers.NameOf(next)} next -- pause held");
            Start("partner joined mid-battle (queued)", next);
            if (HostActive) return;   // started: the pause carries on
        }
        if (_target > 0 && CoopNet.Connected) CoopNet.Send(new CoopMsg { T = "rjdone", Prm = _target, Data = RejoiningName });
        RejoiningName = null;
        DesyncDetector.EndSyncPause(why);
        CoopRunner.SetSyncBlocker(false);
        float took = UnityEngine.Time.realtimeSinceStartup - _hostSince;
        Plugin.Logger.LogWarning($"[RELOAD] Host resumed after {took:F0}s: {why}");
    }

    // ---- GUEST: catch up ---------------------------------------------------
    private enum Phase { Idle, WaitBattle, WaitSettle, WaitData, Rounds, Replay, Snapshot, Snapshot2 }
    private static Phase _phase = Phase.Idle;
    internal static bool GuestCatchingUp => _phase != Phase.Idle;
    private static float _armedAt, _phaseAt, _nextStepAt;
    private static IntPtr _oldBattle;
    private static int _targetRound, _targetFaction, _roundTries, _lastRound, _dropped;
    private static string _snapshot;
    private static readonly List<CoopMsg> _buffer = new();
    private const float GuestFailsafe = 240f;
    // Our own replay of the journal goes through Replication.Apply, and must not
    // be caught by the drop filter it exists to get past.
    private static bool _replaying;

    private static void Replay(CoopMsg m)
    {
        _replaying = true;
        try { Replication.Apply(m); }
        finally { _replaying = false; }
    }

    // ---- HOST: catch up ITSELF (BattleSave resume) ---------------------------
    // The same phases the guest runs, fed from the save instead of the wire.
    private static bool _local;
    internal static bool LocalCatchingUp => _local && _phase != Phase.Idle;

    internal static void StartLocal(int round, int faction, string snapshot, List<CoopMsg> journal)
    {
        _local = true;
        _armedAt = UnityEngine.Time.realtimeSinceStartup;
        _buffer.Clear();
        foreach (var j in journal) _buffer.Add(j.Clone());
        _targetRound = round;
        _targetFaction = faction;
        _snapshot = snapshot;
        _dropped = 0;
        _roundTries = 0;
        _lastRound = -1;
        Step(Phase.Rounds, $"resuming a saved battle: round {round}, {_buffer.Count} journal entr{(_buffer.Count == 1 ? "y" : "ies")}");
        foreach (var b in _buffer)
            if (b.T == "reinfs") Replay(b);
    }

    private static void QueueRestore()
    {
        // The guest's restore waits in DesyncDetector for an idle board; the host
        // restores its own board directly (localAllowed), the checkpoint path.
        if (_local) DesyncDetector.ApplySnapshot(_snapshot, hard: true, localAllowed: true);
        else DesyncDetector.QueueHardSnapshot(_snapshot);
    }

    private static void Arm(CoopMsg m)
    {
        _phase = Phase.WaitBattle;
        _armedAt = _phaseAt = UnityEngine.Time.realtimeSinceStartup;
        _oldBattle = TacticalManager.Get()?.Pointer ?? IntPtr.Zero;
        _buffer.Clear();
        _snapshot = null;
        _dropped = 0;
        _targetRound = m.Prm;
        SessionSync.GuestBattleReady = false;
        SessionSync.PartnerBattleConfirmed = false;
        CoopRunner.SetSyncBlocker(true, "RELOADING THE BATTLE FROM THE HOST -- PLEASE WAIT");
        CoopNet.Status = "Reloading the battle from the host...";
        Plugin.Logger.LogWarning($"[RELOAD] Host is reloading us into its battle ({m.Data}); host round {m.Prm}");
    }

    // SessionSync.OnGuestBattleStarted -- the regenerated battle is up.
    internal static void OnGuestBattleStarted()
    {
        if (_phase != Phase.WaitBattle) return;
        var tm = TacticalManager.Get();
        if (tm == null || tm.Pointer == _oldBattle) return;
        Step(Phase.WaitSettle, "fresh battle started");
        CoopRunner.SetSyncBlocker(true, "CATCHING UP WITH THE HOST'S BATTLE -- PLEASE WAIT");
    }

    private static void Step(Phase p, string why)
    {
        _phase = p;
        _phaseAt = UnityEngine.Time.realtimeSinceStartup;
        Plugin.Logger.LogInfo($"[RELOAD] -> {p}: {why}");
    }

    // Replication.Apply calls this first; true = consumed.
    internal static bool Intercept(CoopMsg m)
    {
        if (m?.T == null || _replaying) return false;
        if (CoopNet.Mode == CoopMode.Host)
        {
            switch (m.T)
            {
                case "reqreload": Start("guest asked: " + (m.Data ?? "FIX DESYNC"), m.From); return true;
                case "catchreq": if (!HostActive) _target = m.From; HandleCatchRequest(); return true;
            }
            Note(m);
            return false;
        }
        switch (m.T)
        {
            case "reload": Arm(m); return true;
            case "catchup":
                if (_phase != Phase.WaitData) { Plugin.Logger.LogWarning($"[RELOAD] Catch-up data arrived in phase {_phase}; ignored"); return true; }
                _targetRound = m.Prm;
                _targetFaction = m.MIdx;
                _snapshot = m.Data;
                DesyncDetector.NoteHostRound(m.Prm);
                _roundTries = 0;
                _lastRound = -1;
                Step(Phase.Rounds, $"host round {m.Prm}, {_buffer.Count} journal entr{(_buffer.Count == 1 ? "y" : "ies")}");
                // Reinforcement budgets must be known BEFORE the rounds that draw them roll.
                foreach (var b in _buffer)
                    if (b.T == "reinfs") Replay(b);
                return true;
        }
        if (m.T.StartsWith("cu:"))
        {
            var c = m.Clone();
            c.T = m.T.Substring(3);
            _buffer.Add(c);
            return true;
        }
        if (_phase != Phase.Idle && DropWhileCatchingUp.Contains(m.T))
        {
            if (_dropped++ == 0) Plugin.Logger.LogInfo($"[RELOAD] Dropping live board traffic until caught up (first: '{m.T}')");
            return true;
        }
        return false;
    }

    // Live traffic describing the host's CURRENT board, which the guest's
    // half-rebuilt board cannot interpret yet. The catch-up snapshot supersedes it.
    private static readonly HashSet<string> DropWhileCatchingUp = new()
    {
        "statesync", "hardsync", "hash", "hashok", "hashrows", "objprog", "aiadvance", "aidone",
        "spause", "spdone", "esup", "estance", "efacing", "efx", "ehp", "edie", "skill", "move", "moveskill",
        "skip", "done", "setfac", "sdie", "obj", "ociuse", "unload", "reinfs", "eseen", "roster",
    };

    // CoopRunner tick.
    internal static void Tick()
    {
        if (CoopNet.Mode == CoopMode.Host)
        {
            // The client being reloaded dropped again: nobody to wait for.
            if (HostActive && _target > 0 && !Peers.SessionSeats().Contains(_target))
                EndHost("the rejoining player left again");
            if (HostActive && (!CoopNet.Connected
                || UnityEngine.Time.realtimeSinceStartup - _hostSince > HostFailsafe
                || TacticalManager.Get() == null))
                EndHost(!CoopNet.Connected ? "partner disconnected" : "failsafe -- the guest never caught up");
            if (!_local) return;   // the host runs the phases only when resuming a save
        }
        if (_phase == Phase.Idle) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        if ((!_local && !CoopNet.Connected) || now - _armedAt > GuestFailsafe)
        {
            Plugin.Logger.LogError($"[RELOAD] Catch-up abandoned in phase {_phase} ({(CoopNet.Connected ? "failsafe" : "disconnected")})");
            Finish(false);
            return;
        }
        if (now < _nextStepAt) return;
        var tm = TacticalManager.Get();
        if (tm == null) return;
        if (_phase == Phase.WaitBattle) { OnGuestBattleStarted(); return; }

        bool settled = Replication.BoardSettled() && !TacticalManager.IsAnyActorMoving();
        switch (_phase)
        {
            case Phase.WaitSettle:
                if (now - _phaseAt < 4f || !settled) return;
                CoopNet.Send(new CoopMsg { T = "catchreq" });
                Step(Phase.WaitData, "board settled; asked the host for its state");
                return;

            case Phase.WaitData:
                // Re-ask once if the answer went missing.
                if (now - _phaseAt > 20f) { CoopNet.Send(new CoopMsg { T = "catchreq" }); _phaseAt = now; Plugin.Logger.LogWarning("[RELOAD] No catch-up data after 20s -- asked again"); }
                return;

            case Phase.Rounds:
            {
                if (!settled) return;
                int r = tm.GetRound();
                if (r >= _targetRound)
                {
                    Step(Phase.Replay, $"at round {r}");
                    return;
                }
                if (r == _lastRound && ++_roundTries > 3)
                {
                    Plugin.Logger.LogError($"[RELOAD] The engine will not roll past round {r} (target {_targetRound}) -- continuing with the snapshot anyway");
                    Step(Phase.Replay, "round roll gave up");
                    return;
                }
                if (r != _lastRound) { _lastRound = r; _roundTries = 0; }
                Plugin.Logger.LogInfo($"[RELOAD] Rolling round {r} -> {r + 1} (target {_targetRound})");
                // THE ENGINE'S OWN ROLLOVER, NOT NextRound. NextRound bumps the
                // counter and leaves the active faction at 0, and nothing moves it
                // on: run 32's guest sat on "r=3 fac=0" while the host played round
                // 4. CheckRound on a spent round is the same call the guest already
                // uses to follow the host, and it opens the next round properly.
                // So: finish every unit's turn, then ask for the check.
                Replication.ApplyingRemote = true;
                try
                {
                    foreach (var a in DesyncDetector.AllActors(tm))
                    {
                        if (a == null) continue;
                        try { if (a.IsDying() || a.IsTurnDone()) continue; a.SetTurnDone(true); a.MarkAsHasActed(); } catch { }
                    }
                    tm.CheckRound();
                    if (tm.GetRound() == r && _roundTries >= 2)
                    {
                        Plugin.Logger.LogWarning($"[RELOAD] CheckRound did not roll round {r} -- falling back to NextRound");
                        tm.NextRound();
                    }
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"[RELOAD] Round roll threw: {e.Message}"); }
                finally { Replication.ApplyingRemote = false; }
                _nextStepAt = now + 1.5f;
                return;
            }

            case Phase.Replay:
            {
                if (!settled) return;
                int n = 0, covers = 0;
                foreach (var b in _buffer)
                {
                    // setfac waits for the first snapshot pass: an abandoned
                    // transport must be moved to the host's tile while still active.
                    if (b.T == "reinfs" || AfterFirstPass(b)) continue;
                    if (b.T == "hcrm") { if (ReplayHalfCoverRemoval(b)) covers++; continue; }
                    try { Replay(b); n++; }
                    catch (Exception e) { Plugin.Logger.LogWarning($"[RELOAD] Journal '{b.T}' threw: {e.Message}"); }
                }
                Plugin.Logger.LogInfo($"[RELOAD] Replayed {n} journal entr{(n == 1 ? "y" : "ies")}, removed {covers} crushed half-cover(s)");
                _nextStepAt = now + 2f;
                QueueRestore();
                Step(Phase.Snapshot, "journal replayed; hard snapshot queued");
                return;
            }

            case Phase.Snapshot:
                if (DesyncDetector.HardSnapshotPending || !settled) return;
                if (!_lateReplayed)
                {
                    _lateReplayed = true;
                    int f = 0, summons = 0;
                    foreach (var b in _buffer)
                        if (AfterFirstPass(b))
                        {
                            try { RetargetGrant(b); Replay(b); f++; if (b.T == "skill") summons++; }
                            catch (Exception e) { Plugin.Logger.LogWarning($"[RELOAD] {b.T} threw: {e.Message}"); }
                        }
                    if (f > 0) Plugin.Logger.LogInfo($"[RELOAD] Replayed {f} faction change(s) / summon(s) / debug action(s) after the first pass");
                    // A deploy spawns its minion after the skill's animation. r108:
                    // the drone landed after the final pass and stayed on its
                    // deploy tile while the host's had moved -- so the second pass
                    // waits until every replayed deploy has actually spawned
                    // (counted in NoteSummonLanded), then places them.
                    _summonsAwaited = summons;
                    if (summons > 0)
                        Plugin.Logger.LogInfo($"[RELOAD] Waiting for {summons} summon(s) to land before the second pass");
                }
                if (_summonsAwaited > 0) return;
                // DEATHS AGAIN, NOW THAT SUMMONS EXIST. A minion that was deployed and
                // then died was "not on this board" when its death replayed (deaths go
                // in pass 1, deploys after it) -- and the deploy then brought it back
                // alive (hand test 2026-10-04: a detonated wardog). A death already
                // applied is skipped: the unit is no longer on the board.
                if (!_deathsRetried)
                {
                    _deathsRetried = true;
                    foreach (var b in _buffer)
                        if (b.T == "adie") { try { ApplyActorDeath(b); } catch { } }
                }
                _lateReplayed = false;
                _deathsRetried = false;
                // Twice: the first pass revives, kills, ejects and moves; units it
                // could not place because a tile was still taken land on the second.
                _nextStepAt = now + 2f;
                QueueRestore();
                Step(Phase.Snapshot2, "first pass applied; second pass queued");
                return;

            case Phase.Snapshot2:
                if (DesyncDetector.HardSnapshotPending || !settled) return;
                // FULLY IN, NOT NEARLY IN. Every accepted restore move is checked a
                // moment later and re-placed if it never landed; "ready" (which ends
                // everyone's pause) waits for that check. Hand test 2026-10-04: an
                // abandoned truck's "never reached" was the LAST line of a catch-up
                // and play resumed on a board that was still wrong.
                if (DesyncDetector.RestoreMovesOutstanding) return;
                Finish(true);
                return;
        }
    }

    private static void Finish(bool ok)
    {
        var tm = TacticalManager.Get();
        int r = -1;
        try { r = tm?.GetRound() ?? -1; } catch { }
        float took = UnityEngine.Time.realtimeSinceStartup - _armedAt;
        _phase = Phase.Idle;
        _lateReplayed = false;
        _deathsRetried = false;
        _deathsApplied.Clear();
        _summonsAwaited = 0;
        _buffer.Clear();
        _snapshot = null;
        int fac = -1;
        try { fac = tm?.GetActiveFactionID() ?? -1; } catch { }
        if (ok) Plugin.Logger.LogWarning($"[RELOAD] Caught up in {took:F0}s: round {r} (host {_targetRound}), faction {fac} (host {_targetFaction}), dropped {_dropped} live message(s) meanwhile");
        // UNITS PLACED BY TELEPORT LEAVE STALE COVER. r98 (4p rejoin): the catch-up
        // snapshot put the troop carriers where the host had driven them, but the
        // cover a vehicle gives its neighbours is only recomputed on a real move --
        // "MAPS DIFFER" at the 5 tiles round them (cover 2 -> 14), i.e. different
        // hit chances on the rejoined client. Recompute once, now the board is final.
        if (ok)
        {
            try { tm?.GetMap()?.UpdateCover(); Plugin.Logger.LogInfo("[RELOAD] Cover recomputed after catch-up"); }
            catch (System.Exception e) { Plugin.Logger.LogWarning($"[RELOAD] Cover recompute failed: {e.Message}"); }
        }
        CoopRunner.SetSyncBlocker(false);
        if (_local)
        {
            _local = false;
            BattleSave.OnLocalCatchUpDone(ok);
            return;
        }
        if (tm != null) SessionSync.GuestReadyAfterCatchUp();
        CoopRunner.ShowDesyncWarning(ok ? "Battle reloaded from the host" : "Battle reload failed -- see the log");
    }
}

// Separate patch class (#61): if this hook stops binding after a game update,
// only the crushed-cover journal degrades.
[HarmonyLib.HarmonyPatch]
internal static class CoverJournalPatches
{
    [HarmonyLib.HarmonyPatch(typeof(Tile), nameof(Tile.RemoveHalfCoverInstance))]
    [HarmonyLib.HarmonyPrefix]
    private static void JournalHalfCoverRemoval(Tile __instance, HalfCover _hc, bool _mirror)
    {
        try { GuestReload.NoteHalfCoverRemoved(__instance, _hc, _mirror); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[COVER] journal failed: {e.Message}"); }
    }

    // #66: whole-unit deaths for the reload journal. Own hook, NOT FleeSync's: that
    // one returns early under ApplyingRemote, which is how a client's kill reaches
    // the host. Prefix: IsDying() is still false here, so the death is new.
    // #57/#67: every accepted Leave (an unload) for the reload journal.
    [HarmonyLib.HarmonyPatch(typeof(Actor), nameof(Actor.MoveTo))]
    [HarmonyLib.HarmonyPostfix]
    private static void JournalLeave(Actor __instance, Tile _tile, ref MovementAction _action, bool __result)
    {
        if (!__result || ((int)_action & (int)MovementAction.Leave) == 0) return;
        try { GuestReload.NoteLeave(__instance, _tile); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[RELOAD] unload journal failed: {e.Message}"); }
    }

    [HarmonyLib.HarmonyPatch(typeof(Actor), nameof(Actor.Die))]
    [HarmonyLib.HarmonyPrefix]
    private static void JournalDeath(Actor __instance)
    {
        try { GuestReload.NoteActorDeath(__instance); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[RELOAD] death journal failed: {e.Message}"); }
    }
}
