using System;
using System.Collections.Generic;
using Il2CppMenace.Tactical;
using Il2CppMenace.States;
using Il2CppMenace.Strategy;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
using Tactical = Il2CppTactical;   // the ROOT Tactical namespace (MovementSystem), not Menace.Tactical

namespace MenaceCoop;

// Battle-state checksums: both sides hash every actor's (tile, HP, AP) at each
// faction switch and exchange the results, keyed by round:faction. A mismatch
// means the simulations have drifted -- surfaced immediately instead of being
// discovered by eyeball. Order-independent hash (sum of per-actor hashes) so
// actor enumeration order cannot cause false positives.
internal static class DesyncDetector
{
    private static string _lastWarnedKey;

    private static float _lastSnapshotAt;
    private const float SnapshotEvery = 2f;

    // Stable cross-machine ids for AI/enemy actors (players use roster index).
    // An actor's id is derived from its SLOT in the engine's own faction actor
    // list -- deliberately NOT from its tile, which the two machines are not
    // guaranteed to agree on (see DeriveId). We capture it the first time we see
    // the actor (board still pristine) and cache it by pointer, so the id
    // survives the unit moving. Cleared when the battle (the TacticalManager
    // instance) changes.
    private static readonly Dictionary<IntPtr, int> _coopId = new();
    private static readonly HashSet<int> _usedCoopIds = new();
    private static IntPtr _lastTmPtr = IntPtr.Zero;

    // How many of each (faction, template) have arrived as mid-battle spawns.
    // This is the arrival ordinal that gives a reinforcement its identity.
    private static readonly Dictionary<(int, string), int> _spawnOrdinal = new();

    // When the last mid-battle arrival was captured. PhantomWatch waits this out
    // before believing a board difference: the two machines register a spawn a
    // moment apart, and during that gap one of them genuinely has nothing there.
    internal static float LastArrivalAt = -999f;

    // Registry of every actor we have identified, keyed by coop id. This is
    // ALSO the coverage for units parented outside the actors root (mid-battle
    // spawns): they are registered at their spawn event and stay reachable here
    // no matter where the scene keeps them.
    private static readonly Dictionary<int, Actor> _byId = new();

    // Every actor on the board: the actors-root children plus the registry.
    // NEVER use FindObjectsOfType here -- Unity rejects the Actor type ("type
    // has to be derived from UnityEngine.Object") and LOGS AN ERROR ON EVERY
    // CALL; in per-frame paths that error flood froze BOTH machines (seen live
    // in 1.4.21).
    internal static Actor[] AllActors(TacticalManager tm)
    {
        var seen = new HashSet<IntPtr>();
        var list = new List<Actor>(64);

        // PRIMARY: every faction's own actor list. This is the engine's own
        // bookkeeping, so it covers units the scene parents anywhere -- which
        // is how CIVILIANS (a third faction in rescue/defence missions) were
        // escaping identity assignment entirely and arriving as RId=-1 CId=-1.
        try
        {
            var factions = tm.GetFactions();
            if (factions != null)
                for (int f = 0; f < factions.Length; f++)
                {
                    var actors = factions[f]?.GetActors();
                    if (actors == null) continue;
                    for (int i = 0; i < actors.Count; i++)
                    {
                        var a = actors[i];
                        if (a != null && seen.Add(a.Pointer)) list.Add(a);
                    }
                }
        }
        catch { }

        try
        {
            var rooted = tm.GetActorsRoot()?.GetComponentsInChildren<Actor>(true);
            if (rooted != null)
                foreach (var a in rooted) if (a != null && seen.Add(a.Pointer)) list.Add(a);
        }
        catch { }

        foreach (var a in _byId.Values)
        {
            if (a == null) continue;
            try { if (seen.Add(a.Pointer)) list.Add(a); } catch { }
        }

        // UNITS INSIDE SOMETHING ARE STILL UNITS, AND THEY WERE INVISIBLE HERE.
        //
        // A garrisoned or embarked actor is not in its faction's actor list and
        // is not parented under the actors root, so it never reached this sweep
        // and never received a coop id. Every order for it then fell back to tile
        // addressing -- and a contained unit has NO TILE, so those missed too.
        // That is the reported "an enemy entered a structure and desynced
        // everything": entering a container removed the unit from the addressable
        // set entirely.
        //
        // Evidence from a live run: 'allied.local_forces_basic_infantry' appears
        // 78 times in the logs and never once in an arrival capture, and every
        // relayed container unload carried rid=-1 cid=-1. The occupancy counts
        // had already drifted -- host 4 passengers aboard, guest 3.
        //
        // Containers are reachable from the things holding them, so walk what we
        // already have and pull the occupants in.
        int held = list.Count;
        for (int i = 0; i < held; i++)
        {
            try
            {
                var holder = list[i];
                if (holder == null || !holder.IsContainingAnotherEntity()) continue;
                var insideActor = holder.GetContainedEntity()?.ToActor();
                if (insideActor != null && seen.Add(insideActor.Pointer)) list.Add(insideActor);
            }
            catch { }
        }

        return list.ToArray();
    }

    // Deterministic string hash -- string.GetHashCode is randomized per process
    // in .NET, so it can NEVER be used for cross-machine ids.
    internal static int Fnv(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            if (s != null) foreach (char c in s) { h ^= c; h *= 16777619; }
            return (int)h;
        }
    }

    // CAN THIS UNIT BE REPOSITIONED AT ALL?
    //
    // A downed, stunned, dying, leaving-the-map, mid-walk or tower/vehicle-bound
    // unit will have EVERY MoveTo declined, even with force+teleport flags. The
    // healer did not check, so it retried the same impossible correction on
    // every snapshot, each failure counted toward the escalation, and it
    // eventually triggered a stop-the-world SYNC PAUSE -- a catastrophic-looking
    // event caused by a unit that was simply down.
    //
    // A unit in this state is not a desync: it is a unit that cannot move yet.
    // Skip it, and let a later snapshot correct it once it can.
    internal static bool CanBeRepositioned(Actor a, bool hard = false)
    {
        if (a == null) return false;
        try
        {
            if (a.IsDying() || a.IsLeavingMap() || a.IsStunned()) return false;
            if (a.IsMoving()) return false;              // let the walk finish first
            // ANY container, not just a tower -- a passenger riding a vehicle has
            // no tile to be teleported to either, and asking IsInsideTower() about
            // one is asking the wrong question (see ContainerSync.IsContained).
            if (ContainerSync.IsContained(a)) return false;
            // A HARD restore also moves inactive units: an emptied, abandoned
            // transport is inactive but still stands on a tile and gives cover
            // (run 32: every truck left on its spawn tile after a reload).
            //
            // ONLY FOR PLAYER UNITS (r147, 2026-10-24). An inactive ENEMY is not
            // an exception: riders a transport has just ejected stay dormant
            // (ContainerSync), and IsActive() is false for healthy enemies too.
            // r147: a chaingun team unloaded from a truck, its relayed move stopped
            // 4 tiles short on both clients (11,26 vs the host's 11,22), and the
            // arrival check and every soft snapshot skipped it here -- the drift
            // stood for the rest of the battle ("enemies displayed incorrectly").
            // 138/138 soft resyncs in r134-r146 moved 0 units.
            if (!hard && !a.IsActive() && Ownership.Active && Ownership.IsKnownPlayerUnit(a)) return false;
            // No tile and no elements left = dead here, only not yet out of the
            // list (r127: a militia the host had journaled dead; GetPath threw on
            // it in r126 and aborted the whole apply). Nothing to move.
            if (a.GetTile() == null && (a.GetElements()?.Count ?? 0) == 0) return false;
            return a.GetLastTile() != null;
        }
        catch { return false; }
    }

    // #66: the engine's own account of a refused snapshot move. MoveTo logs only
    // "Movement failed!"; IsMovementPossible and GetPath return a reason, and r126
    // caught GetPath THROWING inside MoveTo, so call it directly and record which.
    private static string MoveRefusalDiag(Actor a, Tile dest)
    {
        var sb = new System.Text.StringBuilder(" | why:");
        try
        {
            bool poss = Tactical.MovementSystem.IsMovementPossible(a, dest, true, out var reason);
            sb.Append($" possible={poss}/{reason}");
        }
        catch (Exception e) { sb.Append($" possible THREW {e.GetType().Name}"); }
        try
        {
            var r = Tactical.MovementSystem.GetPath(a, dest, Tactical.MoveBackwardsMode.Auto, (MovementFlags)(1 | 2 | 4 | 8));
            sb.Append(r == null ? " path=null" : $" path={r.Success}/{r.Reason} cost={r.Cost} start={(r.StartTile == null ? "null" : "ok")}");
        }
        catch (Exception e) { sb.Append($" path THREW {e.GetType().Name}: {e.Message.Split('\n')[0]}"); }
        try
        {
            var t = a.GetTile()?.GetTilePos();
            var lt = a.GetLastTile()?.GetTilePos();
            sb.Append($" tile={(t == null ? "null" : $"({t.Value.x},{t.Value.y})")} last={(lt == null ? "null" : $"({lt.Value.x},{lt.Value.y})")}");
            sb.Append($" skill={(a.m_MovementSkill == null ? "null" : "set")} props={(a.GetCurrentProperties() == null ? "null" : "set")}");
            var els = a.GetElements();
            int n = els == null ? -1 : els.Count, movers = 0;
            if (els != null) for (int i = 0; i < els.Count; i++) { try { if (els[i] != null && els[i].HasMover()) movers++; } catch { } }
            sb.Append($" elements={n} movers={movers}");
        }
        catch (Exception e) { sb.Append($" state THREW {e.GetType().Name}"); }
        return sb.ToString();
    }

    // #66: put a unit on a tile without the path search. SetTile moves the
    // occupancy but not m_LastTile (Replication run 11), so write that too, then
    // teleport each element so the models follow. True only if the tile holds the
    // unit and GetLastTile agrees afterwards.
    internal static bool PlaceDirectly(Actor a, Tile dest, int x, int y, out string how)
    {
        how = "";
        try
        {
            var old = a.GetLastTile();
            a.SetTile(dest);
            var lt = a.GetLastTile()?.GetTilePos();
            if (lt == null || lt.Value.x != x || lt.Value.y != y) { a.m_LastTile = dest; how += "SetTile+m_LastTile"; }
            else how += "SetTile";
            int tp = 0, tpFail = 0;
            var els = a.GetElements();
            if (els != null)
                for (int i = 0; i < els.Count; i++)
                {
                    try { els[i]?.TeleportTo(dest, -1, MovementAction.Teleport, null); tp++; }
                    catch { tpFail++; }
                }
            how += $" teleported {tp} element(s){(tpFail > 0 ? $", {tpFail} threw" : "")}";
            var onDest = Replication.ActorOn(dest);
            var onOld = old == null ? null : Replication.ActorOn(old);
            bool holds = onDest != null && onDest.Pointer == a.Pointer;
            bool leftOld = onOld == null || onOld.Pointer != a.Pointer;
            lt = a.GetLastTile()?.GetTilePos();
            bool agrees = lt != null && lt.Value.x == x && lt.Value.y == y;
            how += $" destHolds={holds} oldFreed={leftOld} lastTileAgrees={agrees}";
            return holds && agrees;
        }
        catch (Exception e) { how += $" THREW {e.GetType().Name}: {e.Message.Split('\n')[0]}"; return false; }
    }

    internal static string TemplateNameOf(Actor a)
    {
        try { return a?.GetTemplate()?.name ?? ""; } catch { return ""; }
    }

    // Stable cross-machine id: faction + unit TEMPLATE + slot ordinal, with a
    // tile-derived fallback for actors the faction lists do not hold. The
    // template dimension breaks ties between units that share a tile at capture
    // time (garrisons, transport passengers) -- the plain tile scheme skipped
    // those as collisions, leaving them permanently unidentifiable (the
    // recurring "(3,19) RId=-1 CId=-1" desyncs).
    // Ids MUST be positive. Every identity check in the mod reads a negative
    // value as "this unit has no id" -- CoopIdOf returns -1 for missing,
    // ResolveOrderActor only trusts CId >= 0, and snapshots count negatives as
    // unidentified. Hash arithmetic overflows into negatives roughly half the
    // time, so half of all units were silently identity-less: their orders and
    // death packets fell back to tile addressing and missed as soon as anything
    // moved. That is the "enemy dies for the host but lives for the client"
    // bug. Strip the sign, never return zero.
    private static int Positive(int h)
    {
        h &= 0x7FFFFFFF;
        return h == 0 ? 977 : h;
    }

    // IDENTITY MUST NOT CONTAIN POSITION.
    //
    // This used to hash faction + TILE + template. That held only while both
    // machines deployed enemies onto the same tiles -- and v3.13.0 field logs
    // proved they do not: the map generator is unsynced, so the two boards
    // placed an IDENTICAL army on different tiles (composition multisets matched
    // exactly, 18 groups vs 18, while every coordinate differed). Position-based
    // identity then assigned every enemy a different id on each machine, and
    // every relayed order missed:
    //
    //     [DESYNC] Resync requested: order 'aidone' at (10,19) unresolvable
    //
    // With no AI activation ever completing, the guest sat on an hourglass with
    // pass-turn greyed out for the rest of the battle. Fixing the map generator
    // (see SessionSync.PinMapgenRandom) removes the trigger, but identity that
    // is derived from position is fragile by construction -- a unit that moves
    // before capture breaks it just as thoroughly. So identity is now the
    // engine's OWN deterministic ordering: the actor's slot index inside its
    // faction's actor list, which both machines build identically from the same
    // deployment and which does not change when the unit walks away.
    private static int DeriveId(int factionId, string template, int ordinal)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + factionId;
            h = h * 31 + Fnv(MountAgnostic(template));
            h = h * 31 + ordinal;
            return Positive(h);
        }
    }

    // FOLD AWAY THE MOUNT SUFFIX.
    //
    // The pirate army generates mounted -- 'enemy.pirate_scavengers_on_heavy_truck'
    // -- and nine such variants ship (including workers_on_heavy_truck, so
    // civilians ride too). This is a HASH INPUT, not an attempt to name the
    // dismounted template: the mapping is not clean and must not be relied on to
    // be. 'pirate_boarding_commando_on_light_truck' strips to the SINGULAR
    // 'pirate_boarding_commando' while the squad on the board is the plural
    // 'pirate_boarding_commandos' -- two separate templates -- and stripping
    // 'pirate_outcasts_shotgun_on_light_truck' or
    // 'pirate_veteran_scavengers_tier_2_on_heavy_truck' yields names that exist
    // as no template at all.
    //
    // What it buys is insurance. Unloading SPAWNS a new actor rather than
    // rewriting a template in place, so an in-place change should not happen --
    // but if it ever does, an id derived from the mount-agnostic name survives
    // it. The cost of that insurance is a collision risk between a transport and
    // its passengers, which is why the arrival counter keys on this same string
    // (see CaptureArrival) instead of on the raw template.
    internal static string MountAgnostic(string template)
    {
        if (string.IsNullOrEmpty(template)) return template ?? "";
        int i = template.IndexOf("_on_", StringComparison.Ordinal);
        return i > 0 ? template.Substring(0, i) : template;
    }

    // Fallback for an actor that is NOT in any faction's actor list (parented
    // oddly, or already detached). It has no slot index to borrow, so tile
    // addressing is all that is left -- kept deliberately distinct from the
    // ordinal scheme above so the two can never collide.
    private static int DeriveIdByTile(Actor a, int x, int y)
    {
        unchecked
        {
            int h = 19;
            h = h * 31 + a.GetFactionID();
            h = h * 31 + x;
            h = h * 31 + y;
            h = h * 31 + Fnv(TemplateNameOf(a));
            return Positive(h);
        }
    }

    // Slot index of every actor that lives in a faction actor list, keyed by
    // pointer. Actors missing from this map are the out-of-list stragglers
    // handled by DeriveIdByTile.
    //
    // TIMING ASSUMPTION, and it is the same one the tile scheme already made:
    // ids are captured the FIRST time an actor is seen, with the board still
    // pristine. That matters here because the engine REMOVES a casualty from
    // GetActors() and parks it in GetDeadActors(), so every slot index after it
    // shifts down -- the same index != identity trap that GetElements() sets
    // (see the engine laws in CLAUDE.md). An actor already holding an id is
    // never re-derived, so a later shift cannot disturb it; what a shift would
    // corrupt is a FIRST assignment made after casualties, on a board the two
    // machines have depleted differently. PristineBoard() below detects exactly
    // that and such actors take the tile fallback instead.
    private static Dictionary<IntPtr, (int faction, int ordinal)> FactionSlots(TacticalManager tm)
    {
        var slots = new Dictionary<IntPtr, (int, int)>();
        try
        {
            var factions = tm.GetFactions();
            if (factions == null) return slots;
            for (int f = 0; f < factions.Length; f++)
            {
                var fac = factions[f];
                if (fac == null) continue;
                var actors = fac.GetActors();
                if (actors == null) continue;
                int fid;
                try { fid = fac.GetIndex(); } catch { fid = f; }
                for (int i = 0; i < actors.Count; i++)
                {
                    var a = actors[i];
                    if (a == null) continue;
                    try { slots[a.Pointer] = (fid, i); } catch { }
                }
            }
        }
        catch { }
        return slots;
    }

    // Has anything died yet? While this holds, both machines' faction actor
    // lists are the untouched deployment and slot index is a shared identity.
    private static bool PristineBoard(TacticalManager tm)
    {
        try
        {
            var factions = tm.GetFactions();
            if (factions == null) return false;
            for (int f = 0; f < factions.Length; f++)
            {
                var dead = factions[f]?.GetDeadActors();
                if (dead != null && dead.Count > 0) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static readonly HashSet<IntPtr> _collisionLogged = new();

    internal static void EnsureCoopIds(TacticalManager tm)
    {
        if (tm.Pointer != _lastTmPtr)
        {
            _coopId.Clear();
            _usedCoopIds.Clear();
            _collisionLogged.Clear();
            _byId.Clear();
            _spawnOrdinal.Clear();
            _initialPassDone = false;
            _structureScanDone = false;
            _lastTmPtr = tm.Pointer;
        }
        var actors = AllActors(tm);
        var slots = FactionSlots(tm);
        bool pristine = PristineBoard(tm);
        // The FIRST SWEEP of a battle is the opening deployment; anything that
        // joins a faction afterwards is an arrival. Note this deliberately does
        // NOT require a pristine board.
        //
        // It used to, and that made the whole arrival scheme fail open. If the
        // first sweep happened to land after any casualty anywhere, the flag
        // never set, CaptureArrival returned early every single time, and
        // reinforcements got no identity AT ALL -- silently, with nothing in the
        // log to say so. Measured across one battle: the host captured 20
        // arrivals and the guest 4, deriving completely different coop ids
        // (382167008 against 382067012) for the same units, which is what made
        // transports unresolvable and painted phantoms all over the guest's map.
        //
        // Pristine-ness still decides whether a unit takes a SLOT ordinal or the
        // tile fallback -- that part was right. It has no business deciding
        // whether arrivals are tracked.
        // THE BOUNDARY MUST BE THE SAME MOMENT ON BOTH MACHINES.
        //
        // This used to flip on the first sweep that saw any actor -- i.e. on
        // whenever the mod happened to look, which is not a shared event. A unit
        // already present when the guest first looked was filed as DEPLOYMENT and
        // given a slot/tile id, while the host, having looked earlier, saw the
        // same unit arrive and gave it an ARRIVAL id. Same unit, two schemes, two
        // ids, and every order for it missed.
        //
        // Measured: the host captured 'civilian.worker f3 #3' as an arrival and
        // the guest never did, producing 15 unresolved orders for that one
        // civilian -- which is the reported "civilians show up fine on host but
        // aren't syncing on guest".
        //
        // BattleRoster.BoardSettled is built to be the same instant on both
        // machines (round <= 1, plus a stable unit count held for two seconds) --
        // it is what the roster comparison already relies on. Use it, with a
        // round-2 fallback so a missed window cannot strand the flag forever.
        if (!_initialPassDone)
        {
            bool settled = false;
            try { settled = BattleRoster.BoardSettled(tm); } catch { }
            if (!settled) { try { settled = tm.GetRound() > 1; } catch { } }
            if (settled && actors.Length > 0) { _initialPassDone = true; ScanGarrisonsOnce(tm); }
        }
        for (int i = 0; i < actors.Length; i++)
        {
          // PER-ACTOR guard: one actor that throws (a transient/Ð´ying unit whose
          // tile or faction access fails) used to abort the whole pass, leaving
          // every unit AFTER it in the enumeration with no id at all -- which is
          // why five enemies kept showing up unidentified in the same snapshot.
          try
          {
            var a = actors[i];
            if (a == null) continue;
            var ptr = a.Pointer;
            if (_coopId.ContainsKey(ptr)) continue;
            if (RosterIndexOf(a) >= 0) continue;      // players -> roster index
            // Tile-less units (garrisoned in a structure / riding a transport)
            // were silently skipped before -- they then acted with no identity
            // and were invisible to snapshots. Fall back to their world
            // position, which is identical on both machines at capture time.
            int cx, cy;
            var tile = a.GetLastTile();
            if (tile != null) { var p = tile.GetTilePos(); cx = p.x; cy = p.y; }
            else
            {
                UnityEngine.Vector3 v;
                try { v = a.GetPos(); } catch { continue; }
                cx = (int)Math.Round(v.x); cy = (int)Math.Round(v.z);
            }
            // PREFER the position-free slot identity; fall back to the tile only
            // for actors outside every faction list, or for a first sighting on
            // a board that has already taken casualties (see FactionSlots).
            int id;
            if (slots.TryGetValue(ptr, out var slot) && pristine)
                id = DeriveId(slot.faction, TemplateNameOf(a), slot.ordinal);
            else
                id = DeriveIdByTile(a, cx, cy);
            // Two IDENTICAL units sharing a tile at capture time collide. We
            // used to give up here and leave them with NO id, which pushed them
            // onto the restore's fuzzy type+position matching -- and fuzzy
            // matching can put the WRONG unit in the right place.
            //
            // There IS a deterministic tie-break: we enumerate through the
            // engine's own per-faction actor lists (see AllActors), and those
            // are built identically on both machines from the same deterministic
            // deployment. So the Nth duplicate encountered here is the same unit
            // on both sides, and mixing that ordinal into the id gives it a
            // stable identity instead of none.
            int baseId = id, dup = 0;
            while (_usedCoopIds.Contains(id))
            {
                if (++dup > 64) break;
                unchecked { id = Positive(baseId ^ (dup * (int)0x9E3779B1)); }
            }
            if (_usedCoopIds.Contains(id))
            {
                if (_collisionLogged.Add(ptr))
                    Plugin.Logger.LogWarning($"[SYNC] coop-id exhausted at ({cx},{cy}) for '{TemplateNameOf(a)}' -- unit gets no stable id");
                continue;
            }
            if (dup > 0 && _collisionLogged.Add(ptr))
                Plugin.Logger.LogInfo($"[SYNC] coop-id tie-break #{dup} at ({cx},{cy}) for '{TemplateNameOf(a)}'");
            _coopId[ptr] = id;
            _usedCoopIds.Add(id);
            _byId[id] = a;
          }
          catch (Exception e) { Plugin.Logger.LogWarning($"[SYNC] id assignment skipped one actor: {e.Message}"); }
        }
    }

    // Mid-battle reinforcement: both machines execute the spawn (the host runs
    // the skill, the guest replays it), so capturing at the SPAWN EVENT gives a
    // deterministic cross-machine id from the spawn tile -- before the unit has
    // a chance to move (the failure mode of faction-switch capture).
    internal static void CaptureSpawnedEntity(Entity spawned, Tile tile)
    {
        try
        {
            var a = spawned?.TryCast<Actor>();
            if (a == null || tile == null) return;
            var tm = TacticalManager.Get();
            if (tm == null) return;
            if (tm.Pointer != _lastTmPtr)
            {
                _coopId.Clear(); _usedCoopIds.Clear(); _collisionLogged.Clear();
                _byId.Clear(); _spawnOrdinal.Clear(); _lastTmPtr = tm.Pointer;
            }
            if (_coopId.ContainsKey(a.Pointer)) return;
            CaptureArrival(a);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[SYNC] Spawn capture failed: {e.Message}"); }
    }

    // GARRISONS THAT START THE BATTLE INSIDE A BUILDING.
    //
    // These are reachable by nothing else we have. They are not in any faction's
    // actor list, not under the actors root, not occupants of any ACTOR (their
    // container is a Structure, which is an Entity, not an Actor), and they never
    // fire OnEnteredContainer because they were never outside to begin with.
    //
    // Measured live: 'allied.local_forces_basic_infantry' relaying an unload as
    // "NO identity -- at (0,0), faction 0". No tile, no faction list, no id, so
    // every order for it was unaddressable from the first frame.
    //
    // The only way in is through the map, so walk it -- ONCE per battle, not per
    // sweep, because this is a few thousand tile reads. Identity comes from the
    // CONTAINER's tile, which both machines agree on whenever the map does (and
    // the map is now pinned, so they do).
    private static bool _structureScanDone;

    private static void ScanGarrisonsOnce(TacticalManager tm)
    {
        if (_structureScanDone) return;
        _structureScanDone = true;
        try
        {
            var map = tm.GetMap();
            if (map == null) { _structureScanDone = false; return; }

            // Same bounds probe TerrainCheck uses: walk until a tile stops
            // existing. The engine exposes no dimension getter.
            int w = 0, h = 0;
            for (int x = 0; x < 512; x++) { if (map.GetTile(x, 0) == null) break; w = x + 1; }
            for (int z = 0; z < 512; z++) { if (map.GetTile(0, z) == null) break; h = z + 1; }
            if (w == 0 || h == 0) { _structureScanDone = false; return; }

            int found = 0;
            for (int x = 0; x < w; x++)
                for (int z = 0; z < h; z++)
                {
                    Entity ent;
                    try { ent = map.GetTile(x, z)?.GetEntity(); } catch { continue; }
                    if (ent == null) continue;
                    try
                    {
                        if (!ent.IsContainingAnotherEntity()) continue;
                        var occ = ent.GetContainedEntity()?.ToActor();
                        if (occ == null || _coopId.ContainsKey(occ.Pointer)) continue;
                        if (RosterIndexOf(occ) >= 0) continue;

                        int id = DeriveIdByTile(occ, x, z);
                        while (_usedCoopIds.Contains(id)) unchecked { id = Positive(id * 31 + 7919); }
                        _coopId[occ.Pointer] = id;
                        _usedCoopIds.Add(id);
                        _byId[id] = occ;
                        found++;
                        Plugin.Logger.LogInfo($"[SYNC] Garrison identified: '{TemplateNameOf(occ)}' "
                            + $"inside the structure at ({x},{z}) -> coop id {id}");
                    }
                    catch { }
                }
            if (found > 0) Plugin.Logger.LogInfo($"[SYNC] Garrison scan: {found} unit(s) inside structures identified");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[SYNC] Garrison scan failed: {e.Message}");
        }
    }

    // Has the opening deployment been given ids yet? Until it has, an actor
    // joining a faction is part of that deployment and takes a slot ordinal;
    // after it, the actor is an ARRIVAL and takes an arrival ordinal.
    private static bool _initialPassDone;

    // EVERY unit that joins a faction mid-battle, whatever created it.
    //
    // The old capture hooked one path only -- SpawnEntityEffectHandler, i.e.
    // reinforcements summoned by a SKILL. Mission-driven waves do not go through
    // it: ReinforcementGroup.HandleReinforcementGroup builds its units another
    // way entirely, so an arriving pirate transport got NO id at its arrival and
    // was left to the periodic EnsureCoopIds sweep -- which by then sees a board
    // with casualties on it, falls back to tile addressing, and hands the two
    // machines different ids for the same vehicle. That is the reported "half
    // the transports do not exist for the guest, and the ones that do never
    // unload": the vehicle is there, but every order addressed to it misses.
    //
    // BaseFaction.AddActor is the one door they all come through.
    internal static void CaptureArrival(Actor a)
    {
        try
        {
            if (a == null) return;
            var tm = TacticalManager.Get();
            if (tm == null) return;
            if (tm.Pointer != _lastTmPtr)
            {
                _coopId.Clear(); _usedCoopIds.Clear(); _collisionLogged.Clear();
                _byId.Clear(); _spawnOrdinal.Clear(); _lastTmPtr = tm.Pointer;
                _initialPassDone = false; _structureScanDone = false;
            }
            if (_coopId.ContainsKey(a.Pointer)) return;
            if (RosterIndexOf(a) >= 0) return;          // players use roster index

            // If the opening deployment has not been swept yet, sweep it NOW
            // rather than dropping this arrival. Returning early here was how
            // arrivals went unidentified for a whole battle.
            if (!_initialPassDone)
            {
                EnsureCoopIds(tm);
                if (_coopId.ContainsKey(a.Pointer)) return;   // it was part of the deployment
                if (!_initialPassDone) return;                // genuinely nothing on the board yet
            }

            int fid; try { fid = a.GetFactionID(); } catch { fid = -1; }
            string tmpl = TemplateNameOf(a);
            // COUNT ON THE SAME KEY THE ID HASHES, or the two can collide. A
            // transport and the squad it unloads normalise to the same string
            // (MountAgnostic), so counting them separately by RAW template would
            // hand both arrival ordinal 0 -- and therefore the same id. Sharing
            // one counter makes the truck #0 and its passengers #1, which both
            // machines derive identically now that the unload itself is relayed.
            var key = (fid, MountAgnostic(tmpl));
            _spawnOrdinal.TryGetValue(key, out int ord);
            _spawnOrdinal[key] = ord + 1;
            int id = DeriveId(fid, tmpl, 100000 + ord);
            while (_usedCoopIds.Contains(id)) unchecked { id = Positive(id * 31 + 7919); }
            _coopId[a.Pointer] = id;
            _usedCoopIds.Add(id);
            _byId[id] = a;
            LastArrivalAt = UnityEngine.Time.realtimeSinceStartup;
            Plugin.Logger.LogInfo($"[SYNC] Arrival captured: '{tmpl}' f{fid} #{ord} -> coop id {id}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[SYNC] Arrival capture failed: {e.Message}"); }
    }

    internal static int CoopIdOf(Actor a)
        => a != null && _coopId.TryGetValue(a.Pointer, out int id) ? id : -1;

    internal static Actor FindByCoopId(TacticalManager tm, int coopId)
    {
        // Direct registry lookup -- no enumeration (this runs per incoming
        // order). Also finds spawns parented outside the actors root.
        if (_byId.TryGetValue(coopId, out var a) && a != null) return a;
        // Registry miss (e.g. our ids were captured later than the sender's):
        // refresh once and retry.
        EnsureCoopIds(tm);
        return _byId.TryGetValue(coopId, out a) ? a : null;
    }

    internal static void OnFactionSwitch(TacticalManager tm)
    {
        try
        {
            EnsureCoopIds(tm);
            // ONLY THE HOST PUBLISHES A HASH, and the guest verifies it against
            // its board AT THAT MOMENT (see HandleRemote). The old scheme stored
            // both sides' hashes under a "round:faction" key and compared them
            // later, which could not work: with alternating activations that key
            // RECURS many times per round, so the two stored hashes routinely
            // came from different moments -- and the shared eviction queue,
            // holding one entry per store, deleted a freshly-written pair
            // whenever an older duplicate of the same key aged out. Net effect:
            // comparisons almost never happened (one checkpoint all session, no
            // mismatch ever reported) while real desyncs went unseen.
            // NEVER PUBLISH A HASH WHILE THE BOARD IS STILL MOVING.
            //
            // A unit mid-walk has not reached its tile yet, so the two machines
            // are legitimately in different places for a moment. Hashing then
            // compares one board mid-animation against another and reports a
            // "desync" that resolves itself a second later -- and repeated
            // mismatches escalate to a sync pause, which freezes both machines
            // over nothing. Wait for the board to settle; the next faction switch
            // will hash it.
            bool boardMoving = false;
            try { boardMoving = TacticalManager.IsAnyActorMoving(); } catch { }

            if (CoopNet.Mode == CoopMode.Host && !boardMoving)
            {
                string key = $"{tm.GetRound()}:{tm.GetActiveFactionID()}";
                CoopNet.Send(new CoopMsg { T = "hash", Data = key, Seed = ComputeStateHash(tm) });
            }

            // Desync fixer, phase 1: the host periodically ships an authoritative
            // snapshot of every actor's AP + per-element HP; the guest snaps to
            // it (see ApplySnapshot). This heals HP/AP drift as a safety net.
            // Position is NOT reconciled here -- units must already share a tile
            // for the by-tile addressing to land (phase 2 will tackle position).
            if (CoopNet.Mode == CoopMode.Host && CoopNet.Connected
                && UnityEngine.Time.realtimeSinceStartup - _lastSnapshotAt > SnapshotEvery
                && Replication.HitsQuiet())
            {
                _lastSnapshotAt = UnityEngine.Time.realtimeSinceStartup;
                SendSnapshot(tm);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[DESYNC] Hash computation failed: {e.Message}");
        }
    }

    // The host's hired-leader roster index of a battle actor -- a stable id that
    // matches the same unit on the guest even after positions have drifted. -1
    // for anything that isn't a player unit (AI etc.).
    internal static int RosterIndexOf(Actor a)
    {
        var ua = a?.TryCast<UnitActor>();
        var leader = ua?.GetLeader();
        if (leader == null) return -1;
        var hired = StrategyState.Get()?.Roster?.GetHiredLeaders()
            ?.TryCast<Il2CppSystem.Collections.Generic.List<BaseUnitLeader>>();
        if (hired == null) return -1;
        for (int i = 0; i < hired.Count; i++)
            if (hired[i]?.Pointer == leader.Pointer) return i;
        return -1;
    }

    internal static UnitActor FindByRosterIndex(int idx)
    {
        var hired = StrategyState.Get()?.Roster?.GetHiredLeaders()
            ?.TryCast<Il2CppSystem.Collections.Generic.List<BaseUnitLeader>>();
        if (hired == null || idx < 0 || idx >= hired.Count) return null;
        var leader = hired[idx];
        var deployed = TacticalState.Get()?.GetDeployedUnitLeaders();
        if (deployed == null || leader == null) return null;
        for (int i = 0; i < deployed.Count; i++)
            if (deployed[i]?.GetLeader()?.Pointer == leader.Pointer) return deployed[i];
        // A DOWNED squad is moved into the orphan faction and can drop out of the
        // deployed list while it still lies on the board (r90, BUGS #50). Search
        // every faction's actors before giving up: a miss here sends callers to
        // their tile fallback, which found the carrier parked on the same tile.
        var tm = TacticalManager.Get();
        if (tm == null) return null;
        foreach (var a in AllActors(tm))
        {
            var ua = a?.TryCast<UnitActor>();
            if (ua?.GetLeader()?.Pointer == leader.Pointer) return ua;
        }
        return null;
    }

    // Whether a unit found only by TILE may stand in for a snapshot / order entry.
    // Two units can share a tile (a vehicle and the infantry beside or under it),
    // so a tile hit must at least be the same template, and must not be a unit
    // that carries a DIFFERENT stable identity of its own (BUGS #50: the downed
    // Carda's entry landed on the troop carrier and wrote 8 HP over its 250).
    internal static bool TileFallbackFits(Actor a, int rosterIdx, int coopId, int tmplHash)
    {
        if (a == null) return false;
        try
        {
            if (tmplHash != 0 && Fnv(TemplateNameOf(a)) != tmplHash) return false;
            int rid = RosterIndexOf(a);
            if (rid >= 0 && rid != rosterIdx) return false;
            int cid = CoopIdOf(a);
            if (cid >= 0 && coopId >= 0 && cid != coopId) return false;
        }
        catch { return false; }
        return true;
    }

    // Faction ids with nothing left to act, as a '-' separated list. Empty
    // string when every faction still owes an activation.
    private static string FinishedFactionList(TacticalManager tm)
    {
        try
        {
            var factions = tm.GetFactions();
            if (factions == null) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < factions.Length; i++)
            {
                var f = factions[i];
                if (f == null) continue;
                if (f.GetAmountOfActorsLeftToAct() > 0) continue;
                // Resolve the faction's real ID from a member: the array index
                // is a POSITION, and the two numbering schemes do not match.
                int fid = int.MinValue;
                var acts = f.GetActors();
                if (acts != null)
                    for (int k = 0; k < acts.Count; k++)
                        if (acts[k] != null) { fid = acts[k].GetFactionID(); break; }
                if (fid == int.MinValue) continue;      // empty faction -- nothing to settle anyway
                if (sb.Length > 0) sb.Append('-');
                sb.Append(fid);
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    private static string BuildSnapshot(TacticalManager tm)
    {
        EnsureCoopIds(tm);
        var actors = AllActors(tm);
        int noId = 0;
        var sb = new System.Text.StringBuilder();
        // Header: the host's phase, round (diagnostic) and ACTIVE FACTION so a
        // guest whose cycle slipped can detect and correct it (see Tick).
        sb.Append("F:").Append(tm.IsPlayerTurn() ? 1 : 0).Append(':').Append(tm.GetRound())
          .Append(':').Append(tm.GetActiveFactionID())
          // FOURTH FIELD: which factions the host has GENUINELY FINISHED this
          // round -- every actor in them done. The guest's straggler failsafe
          // used to infer this from "the host's active faction differs from
          // mine", which is a completely different fact: the engine alternates
          // ONE player activation with ONE AI activation, so the host leaves
          // and re-enters the alien faction many times per round. The guest was
          // therefore cancelling every enemy that had not acted yet the moment
          // the host's FIRST enemy finished. Only the host can answer "is this
          // faction spent", so the host now says so explicitly.
          .Append(':').Append(FinishedFactionList(tm)).Append(';');
        for (int i = 0; i < actors.Length; i++)
        {
            var a = actors[i];
            if (a == null) continue;
            // DEAD units are included (dead=1): a guest whose copy is still
            // alive must be told to kill it -- "the bug that died did not move"
            // was exactly this gap. Tile-less units are included with x,y=-1
            // (HP/AP fixable by identity; no position to fix).
            bool dead = a.IsDying();
            int x = -1, y = -1;
            var tile = a.GetLastTile();
            if (tile != null) { var p = tile.GetTilePos(); x = p.x; y = p.y; }
            if (dead && x < 0) continue;   // dead and nowhere -- not addressable
            // A UNIT STILL WALKING HAS NO POSITION TO SEND YET (r148: a truck
            // driving in from the map edge was still on (0,29) here while the
            // client's replay had already put it on (5,24) -- the snapshot
            // teleported it back, the next one 180 ms later forward again). Its
            // landing ("land") carries the tile when the walk ends.
            if (!dead && x >= 0) { try { if (a.IsMoving()) { x = -1; y = -1; } } catch { } }
            // x,y,ap,hp0.hp1.hp2,rosterIdx,coopId,tmplHash,factionId,dead
            sb.Append(x).Append(',').Append(y).Append(',').Append(a.GetActionPoints()).Append(',');
            // HP is serialised as "squaddieId:hp" pairs, not bare positions.
            //
            // Index-based matching cannot reconcile two lists of different length --
            // ApplyHp compared only up to the SHORTER one, so once a squad diverged
            // by even one member the extra elements were never examined and the
            // difference became permanent. Deltas cannot heal it either (they only
            // converge from the same starting HP), which is why single stuck
            // elements persisted on unit after unit.
            var els = a.GetElements();
            if (els != null)
                for (int e = 0; e < els.Count; e++)
                {
                    if (e > 0) sb.Append('.');
                    int sq = -1, slot = -1;
                    try { sq = els[e]?.GetSquaddieId() ?? -1; } catch { }
                    try { slot = els[e]?.GetElementIndex() ?? -1; } catch { }
                    // "sq:hp:slot" -- the slot is what disambiguates enemy squads,
                    // whose members all share squaddie id 0 (see ApplyHp).
                    sb.Append(sq).Append(':').Append(els[e]?.GetHitpoints() ?? 0).Append(':').Append(slot);
                }
            int rid = RosterIndexOf(a), cid = CoopIdOf(a);
            if (rid < 0 && cid < 0)
            {
                noId++;   // will need fuzzy matching on restore
                Plugin.Logger.LogWarning($"[DESYNC] Snapshot: '{TemplateNameOf(a)}' (faction {a.GetFactionID()}) at ({x},{y}) has NO stable id");
            }
            sb.Append(',').Append(rid).Append(',').Append(cid)
              .Append(',').Append(Fnv(TemplateNameOf(a))).Append(',').Append(a.GetFactionID())
              .Append(',').Append(dead ? 1 : 0);
            // Combat state beyond position/HP/AP. Without these a "fixed" board
            // could still differ in every way the player can actually SEE --
            // whose turn is finished, who is pinned, who is panicking, who is
            // crouched -- which is why a resync could report success and change
            // nothing on screen. Floats are rounded to ints: exact parity is
            // meaningless here and decimal separators are locale-dependent.
            int sup = 0, mor = 0, stance = 0, done = a.IsTurnDone() ? 1 : 0;
            try { sup = (int)Math.Round(a.GetSuppression()); } catch { }
            try { mor = (int)Math.Round(a.GetMorale()); } catch { }
            try { stance = (int)a.GetStance(); } catch { }
            int dir = -1;
            try { dir = (int)a.GetDirection(); } catch { }
            sb.Append(',').Append(done).Append(',').Append(sup).Append(',').Append(mor)
              .Append(',').Append(stance).Append(',').Append(dir)
              .Append(',').Append(ContainerRef(a)).Append(';');
        }
        if (noId > 0)
            Plugin.Logger.LogWarning($"[DESYNC] Snapshot: {noId} unit(s) have NO stable id -- these can only be matched by type+position on restore");
        return sb.ToString();
    }

    internal static string BuildSnapshotFor(TacticalManager tm) => BuildSnapshot(tm);

    // SNAPSHOT FIELD 14: WHAT THIS UNIT IS RIDING IN. A rider's last tile is its
    // transport's tile, so position alone cannot tell "standing here" from
    // "aboard the truck parked here" -- run 37 dismounted a rider the host still
    // had aboard, leaving the guest one enemy up. -1 = not contained; >= 0 = the
    // container's coop id; <= -2 = a player container, roster index -(v+2);
    // -9 = contained in something without an id (a structure).
    private static int ContainerRef(Actor a)
    {
        try
        {
            if (!ContainerSync.IsContained(a)) return -1;
            var c = a.GetContainerEntity()?.TryCast<Actor>();
            if (c == null) return -9;
            int rid = RosterIndexOf(c);
            if (rid >= 0) return -(rid + 2);
            int cid = CoopIdOf(c);
            return cid >= 0 ? cid : -9;
        }
        catch { return -1; }
    }

    private static Actor ResolveContainerRef(TacticalManager tm, int cref)
    {
        if (cref >= 0) return FindByCoopId(tm, cref);
        if (cref <= -2 && cref != -9) return FindByRosterIndex(-cref - 2);
        return null;
    }
    internal static bool HardSnapshotPending => _pendingHard != null;

    // to: the client that asked (v5), or -1 for everyone.
    internal static void SendSnapshot(TacticalManager tm, int to = -1)
    {
        try { var sm = new CoopMsg { T = "statesync", Data = BuildSnapshot(tm) }; if (to > 0) CoopNet.SendTo(sm, to); else CoopNet.Send(sm); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[DESYNC] Snapshot build failed: {e.Message}"); }
    }

    // The HARD resync: same authoritative state, but the guest applies it with
    // aggressive fallback matching (template + faction + nearest tile) so even
    // units with NO stable id get corrected. Triggered manually by the panel
    // button, by a guest's "reqhard", or by auto-escalation when soft resyncs
    // keep failing.
    internal static void SendHardSnapshot(TacticalManager tm, int to = -1)
    {
        try
        {
            var hm = new CoopMsg { T = "hardsync", Data = BuildSnapshot(tm) };
            if (to > 0) CoopNet.SendTo(hm, to); else CoopNet.Send(hm);
            Plugin.Logger.LogInfo("[DESYNC] Hard resync snapshot sent");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[DESYNC] Hard snapshot failed: {e.Message}"); }
    }

    // ---- Continuous reconciliation ---------------------------------------
    // A snapshot must not be applied while units are animating (teleporting a
    // mid-move unit fights the movement system and makes things worse), and the
    // host's snapshot may predate an order the guest just sent (applying it
    // would yank the guest's own unit back, then the order lands and re-diverges).
    // So incoming snapshots are QUEUED (latest wins) and applied from Tick()
    // only when the board is idle and no order of ours is still in flight.
    private static string _pendingSnapshot;
    private static float _pendingSnapshotAt;
    private static float _lastPeriodicAt;
    private const float PeriodicEvery = 5f;   // host: continuous mid-turn healing

    // Host phase as of the latest snapshot (parsed immediately, even when the
    // body's application is deferred) -- used by the stranded-guest check.
    private static bool _hostInPlayerTurn;
    private static int _hostRound = -1;
    private static float _hostPhaseAt = -1f;

    // Hard snapshots bypass the staleness rules -- they are explicit "host
    // wins, fix everything" commands -- but still wait for board idle so the
    // teleports don't fight animations.
    private static string _pendingHard;

    internal static void QueueHardSnapshot(string data)
    {
        _pendingHard = data;
        if (data != null && data.StartsWith("F:")) ParseHeader(data);
    }

    private static int _hostFactionId = int.MinValue;
    private static int _lastRoundWarned = -1;

    // Has the host already moved on from the faction we're sitting on? Used by
    // the faction pump's stall failsafe (a done-flag we never received would
    // otherwise hold the guest on that faction forever).
    internal static int HostRound => _hostRound;

    // The host's "aiadvance" carries its round too, and arrives the moment it
    // rolls -- the snapshot only every few seconds. Run 25: the guest sat in
    // round 2 for 8 s ("looks spent but the host is on 2 -- waiting") while the
    // host was already playing round 3, so the host's round-3 orders landed in
    // the guest's round 2. Only ever raises it: a late message never lowers it.
    internal static void NoteHostRound(int r)
    {
        // Per battle: a round left over from the last mission must not stand in
        // for the host's round in this one (it would let the guest roll ahead).
        IntPtr battle = IntPtr.Zero;
        try { battle = TacticalManager.Get()?.Pointer ?? IntPtr.Zero; } catch { }
        if (battle != _hostRoundBattle) { _hostRoundBattle = battle; _hostRound = r; return; }
        if (r > _hostRound) _hostRound = r;
    }
    private static IntPtr _hostRoundBattle;

    // Is the HOST currently in the players' phase, per its latest snapshot?
    //
    // The two engines alternate activations and can sit one step apart: the
    // guest's cursor says "alien turn" while the host is already back on the
    // players, waiting for a guest unit that the guest will not move precisely
    // because its own cursor says otherwise. Neither side is wrong; they are
    // just out of step, and someone has to break the tie. The host's phase is
    // the authoritative answer to "whose turn is it".
    // Do we have a recent enough report to trust the host's phase at all?
    internal static bool HostPhaseKnown
        => _hostPhaseAt > 0 && UnityEngine.Time.realtimeSinceStartup - _hostPhaseAt < 15f;

    internal static bool HostIsInPlayerPhase
        => _hostInPlayerTurn && _hostPhaseAt > 0
           && UnityEngine.Time.realtimeSinceStartup - _hostPhaseAt < 15f;   // ignore stale reports

    // The host is running an AI activation (enemies, allied AI, the round
    // rollover) -- a fresh report, not merely "not known to be the players'".
    internal static bool HostInAiPhase => HostPhaseKnown && !_hostInPlayerTurn;

    // "hphase": the host's phase the moment it changes (plus a heartbeat).
    //
    // The snapshot header alone arrives every 5 s, and the host hands the guest
    // control the instant its own unit finishes -- in the same frame the engine
    // gives the next activation to the AI. So for up to 5 s the guest held
    // control, still read "host is in the player phase", and could shoot at
    // enemies that were visibly walking (player report 2026-09-30; hand test
    // 2026-09-27: ctrl=me on the guest for 6 s of a host AI activation).
    internal static void NoteHostPhase(bool inPlayerPhase)
    {
        _hostInPlayerTurn = inPlayerPhase;
        _hostPhaseAt = UnityEngine.Time.realtimeSinceStartup;
    }

    private static string _hostFinishedFactions = "";

    // Has the host FINISHED this faction for this round -- every actor in it
    // done, not merely "the host is looking at a different faction right now"?
    //
    // Only the former licenses the guest to cancel the turns of units it is
    // still waiting on. The old test was the latter, and because the engine
    // alternates one player activation with one AI activation, it was true
    // after the host's very first enemy acted -- so the guest force-finished
    // every enemy that had not moved yet, every round. Seen as "Cleared 9
    // unidentifiable units", with the guest at ai=2 while the host had ai=9,
    // and enemies that simply never acted on the guest's screen.
    internal static bool HostFinishedFaction(int myFactionId, int myRound)
        => _hostPhaseAt > 0
           && UnityEngine.Time.realtimeSinceStartup - _hostPhaseAt < PeriodicEvery * 2
           && _hostFactionId != int.MinValue
           && FactionListContains(_hostFinishedFactions, myFactionId)
           // NEVER let the failsafe push us AHEAD of the host. Completing
           // stragglers advances a faction, and doing that while already level
           // with (or ahead of) the host's round burns rounds -- exactly the
           // drift seen at host round 2 / guest round 4.
           && _hostRound > 0 && myRound <= _hostRound;

    private static bool FactionListContains(string list, int fid)
    {
        if (string.IsNullOrEmpty(list)) return false;
        var parts = list.Split('-');
        for (int i = 0; i < parts.Length; i++)
            if (int.TryParse(parts[i], out int v) && v == fid) return true;
        return false;
    }

    private static float _roundMismatchSince = -1f;

    private static void ParseHeader(string data)
    {
        int end = data.IndexOf(';');
        var head = (end > 2 ? data.Substring(2, end - 2) : data.Substring(2)).Split(':');
        _hostInPlayerTurn = head.Length > 0 && head[0] == "1";
        if (head.Length > 1 && int.TryParse(head[1], out int r)) { _hostRound = r; try { _hostRoundBattle = TacticalManager.Get()?.Pointer ?? IntPtr.Zero; } catch { } }
        if (head.Length > 2 && int.TryParse(head[2], out int fid)) _hostFactionId = fid;
        // Absent on an older partner build: leave the previous value rather than
        // clearing it, so a missing field never reads as "nothing is finished".
        if (head.Length > 3) _hostFinishedFactions = head[3];
        _hostPhaseAt = UnityEngine.Time.realtimeSinceStartup;
    }

    internal static void QueueSnapshot(string data)
    {
        _pendingSnapshot = data;
        _pendingSnapshotAt = UnityEngine.Time.realtimeSinceStartup;
        // Header "F:<phase>:<round>;" -- parsed immediately (even when the body
        // is deferred) so phase/round info is always fresh.
        if (data != null && data.StartsWith("F:")) ParseHeader(data);
    }

    // Any side that detects divergence (dropped order, unresolvable damage
    // packet) calls this: the host answers with an authoritative snapshot
    // immediately instead of waiting for the next faction switch. Throttled --
    // a burst (e.g. one missed machine-gun volley = 6+ packets) is one desync,
    // not six, and one snapshot heals all of it.
    private static float _lastResyncReqAt;
    private static readonly Queue<float> _failTimes = new();
    private static float _lastHardAt;

    // Did the most recent correction actually change anything? Drives the
    // no-op escalation guard above. Starts true so the very first escalation
    // path is available before any correction has been attempted.
    private static bool _lastResyncFixedSomething = true;
    private static float _lastNoOpReportAt;
    private static float _lastIdentityReportAt;

    internal static void RequestResync(string why, bool allowEscalation = true)
    {
        // The guest's board is being rebuilt; the catch-up snapshot replaces
        // anything a resync could do, and its half-built state trips every check.
        if (GuestReload.GuestCatchingUp) return;
        Plugin.Logger.LogWarning($"[DESYNC] Resync requested: {why}");
        float now = UnityEngine.Time.realtimeSinceStartup;

        // Escalation: repeated failures in a short window mean the SOFT resync
        // is not fixing it (typically an unidentifiable unit) -- go hard.
        //
        // A STALL MUST NEVER ESCALATE. The hard path opens a sync pause, and a
        // sync pause freezes AI processing on BOTH machines -- including the
        // host's AI, which is the only thing that can actually resolve a stall.
        // That produced a livelock: stall -> resync -> pause -> host AI frozen
        // -> still stalled -> repeat, with every restore reporting zero
        // corrections because the boards already matched.
        // A REPAIR THAT CHANGED NOTHING MUST NOT ESCALATE.
        //
        // Some differences are structurally UNFIXABLE by this machinery: a squad
        // roster that differs in length is the clearest case, because ApplyHp only
        // compares up to the SHORTER element list, so the extra member is never
        // even examined and every resync honestly reports "0 corrected". Escalating
        // on that produced "SYNC PAUSE started: repeated sync failures" -- a
        // stop-the-world freeze applied to something no amount of syncing can
        // touch, which is the same mistake as the AI-action-point hash, the
        // dead-squaddie packets and the squad-mismatch pause.
        //
        // If the last correction fixed nothing, the next one will fix nothing
        // either. Report it, keep playing.
        // AN UNRESOLVABLE ORDER MUST NEVER ESCALATE EITHER.
        //
        // This is the same family as the stall and the squad-length mismatch
        // above, and it was missed because it slips past the no-op guard: the
        // corrections DO fix something (a pile of HP drift), so
        // _lastResyncFixedSomething stays true and the escalation fires -- while
        // the thing that actually went wrong, an order naming a unit this
        // machine cannot identify, is untouched by any of it. A hard resync
        // addresses units BY ID, so when the ids themselves disagree it has
        // nothing to work with; it reports its corrections, the next order is
        // just as unresolvable, and the pause comes round again.
        //
        // Observed live: 266 unresolvable orders, 602 HP-drift corrections and
        // eleven stop-the-world pauses in one battle, on a board that looked
        // completely normal to both players -- "it pauses the game every other
        // turn but visually the game looks fine". Report it and keep playing;
        // the fix for an identity mismatch is in identity, not in pausing.
        bool identityFault = why != null && why.IndexOf("unresolvable", StringComparison.OrdinalIgnoreCase) >= 0;
        if (identityFault && allowEscalation)
        {
            if (now - _lastIdentityReportAt > 15f)
            {
                _lastIdentityReportAt = now;
                Plugin.Logger.LogWarning("[DESYNC] Not escalating: the fault is an unidentifiable unit, and a sync "
                    + "pause cannot restore identity. Corrections continue; see the [SYNC] 'never resolved' lines.");
            }
            allowEscalation = false;
        }

        // NOTHING AUTOMATIC MAY APPLY A HARD RESTORE (player report, v4.3.0:
        // "when the mod automatically tries to fix them, a lot of things break --
        // enemy units resurrect for the guest and all allied units return to
        // their battle-start positions"). A hard restore takes the HOST's board
        // as truth for every unit -- including the guest's own units and the
        // guest's own kills, which the host only knows through relays. When the
        // drift is the host MISSING the guest's actions, that "repair" rewinds
        // them: kills come back to life, moves snap back. Repeated failures now
        // stay soft (soft snapshots never revive and never move a unit owned by
        // the receiver); the hard path is reserved for an explicit player action.
        if (allowEscalation && _lastResyncFixedSomething)
        {
            _failTimes.Enqueue(now);
            while (_failTimes.Count > 0 && now - _failTimes.Peek() > 10f) _failTimes.Dequeue();
            if (_failTimes.Count >= 3 && now - _lastHardAt > 20f)
            {
                _lastHardAt = now;
                _failTimes.Clear();
                Plugin.Logger.LogWarning($"[DESYNC] Repeated sync failures ({why}) -- staying with soft corrections; "
                    + "an automatic hard restore would overwrite this machine's own units and kills.");
            }
        }
        else if (allowEscalation && !_lastResyncFixedSomething
                 && now - _lastNoOpReportAt > 30f)
        {
            _lastNoOpReportAt = now;
            Plugin.Logger.LogWarning(
                $"[DESYNC] Difference persists but the last correction changed nothing ({why}) -- " +
                $"not escalating, because a repair that fixes nothing cannot fix it by repeating.");
        }

        if (now - _lastResyncReqAt < 3f) return;
        _lastResyncReqAt = now;
        // Quiet by design -- the escalation path below and the correction
        // itself both speak up when there is something worth seeing.
        CoopNet.Status = $"Syncing from the host ({why})";
        if (CoopNet.Mode == CoopMode.Host)
        {
            var tm = TacticalManager.Get();
            if (tm != null) SendSnapshot(tm);      // guest reconciles to us
        }
        else
        {
            CoopNet.Send(new CoopMsg { T = "reqsync" });
        }
    }

    // ---- SYNC PAUSE ------------------------------------------------------
    // "Pause the entire game and force everyone back into position": both
    // machines freeze (no orders, no AI), the host pushes its full state, the
    // guest snaps to it, then both resume. Triggered by the panel button, by a
    // guest's request, or automatically when soft resyncs keep failing.
    private static float _syncPauseSince = -1f;
    private static bool _hostMustPush;
    private const float SyncPauseTimeout = 12f;

    internal static void StartSyncPause(string why, bool notifyPartner = true, bool hostPush = true)
    {
        // A guest mid-reload has no board to pause; the host is holding for it.
        if (GuestReload.GuestCatchingUp) return;
        if (!CoopNet.Connected) { CoopNet.Status = "Not connected -- nothing to resync"; return; }
        if (TacticalManager.Get() == null) { CoopNet.Status = "Not in a battle -- nothing to resync"; return; }
        if (Replication.SyncPauseActive) return;
        Plugin.Logger.LogWarning($"[DESYNC] SYNC PAUSE started: {why}");
        Replication.SyncPauseActive = true;
        _syncPauseSince = UnityEngine.Time.realtimeSinceStartup;
        _hostMustPush = hostPush && CoopNet.Mode == CoopMode.Host;
        CoopRunner.SetSyncBlocker(true, "DESYNC -- GAME PAUSED, FORCING EVERYONE BACK INTO POSITION");
        if (notifyPartner) CoopNet.Send(new CoopMsg { T = "spause", Data = why });
    }

    internal static void EndSyncPause(string why)
    {
        if (!Replication.SyncPauseActive) return;
        Replication.SyncPauseActive = false;
        _syncPauseSince = -1f;
        _hostMustPush = false;
        CoopRunner.SetSyncBlocker(false);
        Plugin.Logger.LogInfo($"[DESYNC] Sync pause ended: {why}");
        CoopRunner.ShowDesyncWarning("Resync complete -- game resumed");
    }

    // Back-compat entry point (panel button / escalation): now always the full
    // pause-and-reset experience.
    internal static void TriggerHardResync(string why) => StartSyncPause(why);

    // STRUCTURAL MISMATCH -- the squads themselves differ, not just their numbers.
    //
    // A soft resync corrects hitpoints, but it cannot bring back a squad member
    // that was killed on one machine and not the other: only a hard restore
    // revives. So when a damage packet names a squaddie this side does not have,
    // asking for a soft resync leaves the rosters permanently different and the
    // next packet fails the same way. Escalate straight to the repair that can
    // actually fix it, from whichever side noticed.
    private static float _lastStructuralReportAt;

    internal static void RepairStructuralMismatch(string why)
    {
        try
        {
            // DO NOT STOP THE WORLD FOR THIS.
            //
            // This used to escalate to a hard resync, which starts a SYNC PAUSE --
            // freezing BOTH machines. But a pause cannot fix a squad mismatch: a
            // hard restore reports "0 revived" because it does not resurrect dead
            // squad MEMBERS, only whole actors. So the game froze and the
            // divergence survived, which is the exact trap engine law 16 names --
            // a stop-the-world pause triggered by a condition the world has to
            // resolve. Observed live as "SYNC PAUSE started: squad mismatch at
            // (13,19)" with the board locked and nothing repaired.
            //
            // The rosters differing costs a squaddie's worth of HP accounting; a
            // freeze costs the session. Report it, keep playing, and let the
            // ordinary snapshots carry whatever they legitimately can.
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _lastStructuralReportAt < 15f) return;
            _lastStructuralReportAt = now;

            Plugin.Logger.LogWarning(
                $"[DESYNC] Squad rosters differ ({why}). NOT pausing -- a restore cannot revive a dead " +
                $"squad member, so freezing would cost the session and fix nothing. Play continues.");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[DESYNC] structural report failed: {e.Message}"); }
    }

    // Called every frame by CoopRunner.
    // Teleports issued by a restore, checked a moment later. MoveTo only
    // ACCEPTS a move; the tile changes when the movement finishes.
    private static readonly List<(Actor actor, int x, int y, string name)> _pendingMoves = new();
    private static float _verifyMovesAt = -1f;

    internal static bool RestoreMovesOutstanding => _pendingMoves.Count > 0;

    private static void VerifyPendingMoves()
    {
        if (_pendingMoves.Count == 0) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (_verifyMovesAt < 0) { _verifyMovesAt = now + 1.5f; return; }
        if (now < _verifyMovesAt) return;
        int stuck = 0;
        foreach (var (actor, x, y, name) in _pendingMoves)
        {
            try
            {
                var p = actor?.GetLastTile()?.GetTilePos();
                if (p != null && p.Value.x == x && p.Value.y == y) continue;
                // ACCEPTED BUT NEVER MOVED: an abandoned (neutral) transport takes the
                // MoveTo and does nothing (hand test 2026-10-04: emptied heavy truck
                // left on its spawn tile, cover at the wrong place). Place it.
                var dest = Replication.GetTile(x, y);
                var occ = dest == null ? null : Replication.ActorOn(dest);
                if (actor != null && dest != null && (occ == null || occ.Pointer == actor.Pointer)
                    && PlaceDirectly(actor, dest, x, y, out string how))
                {
                    Plugin.Logger.LogWarning($"[DESYNC]   PLACED '{name}' -> ({x},{y}) after its accepted move never landed | {how}");
                    continue;
                }
                stuck++;
                Plugin.Logger.LogWarning($"[DESYNC]   '{name}' never reached ({x},{y}) -- sitting at {(p == null ? "nowhere" : $"({p.Value.x},{p.Value.y})")}");
            }
            catch { }
        }
        if (stuck > 0)
            CoopRunner.ShowDesyncWarning($"WARNING: {stuck} unit(s) did not reach their restored position");
        else
            Plugin.Logger.LogInfo($"[DESYNC] All {_pendingMoves.Count} restored position(s) confirmed");
        _pendingMoves.Clear();
        _verifyMovesAt = -1f;
    }

    internal static void Tick()
    {
        VerifyPendingMoves();

        // A PAUSE MUST NOT SURVIVE LOSING THE PARTNER, AND MUST SAY SO.
        //
        // Everything below returns when disconnected -- including the 12s stuck-
        // pause failsafe -- so a pause begun just before a drop used to stay set
        // for the rest of the session and suppress the HOST's AI with it. See the
        // full chain on Replication.SyncPauseActive; that property now refuses to
        // read true while disconnected, so the freeze cannot happen either way.
        // This clears the backing state as well, and logs it: the bug survived a
        // release because the pause was invisible once set.
        if (!CoopNet.Connected)
        {
            if (_syncPauseSince > 0)
            {
                GuestReload.ClearRejoining();
                EndSyncPause("partner disconnected -- a resync nobody can confirm is abandoned");
                CoopRunner.ShowDesyncWarning("PARTNER DISCONNECTED during a resync -- the battle continues solo");
            }
            return;
        }
        var tm = TacticalManager.Get();
        if (tm == null) { _pendingSnapshot = null; _autoSaveRound = -1; return; }

        float now = UnityEngine.Time.realtimeSinceStartup;

        if (CoopNet.Mode == CoopMode.Host) AutoSaveOnNewRound(tm);

        // Sync pause management: the host pushes its full state the moment its
        // board is idle; a stuck pause auto-releases so a lost message can
        // never freeze the session for good.
        if (Replication.SyncPauseActive)
        {
            // A guest reload holds the pause for as long as the guest takes to
            // load (~40-60 s); GuestReload has its own failsafe.
            // Another player rejoining: no timer. The host ends it (rjdone, also
            // sent from its own reload failsafe), or the rejoining seat leaves
            // the roster, or we disconnect.
            if (_syncPauseSince > 0 && now - _syncPauseSince > SyncPauseTimeout
                && !GuestReload.HostActive && GuestReload.RejoiningName == null)
                EndSyncPause("timeout failsafe");
            else if (_hostMustPush && CoopNet.Mode == CoopMode.Host && Replication.BoardSettled())
            {
                _hostMustPush = false;
                SendHardSnapshot(tm);
            }
        }

        // Host: stream periodic snapshots so mid-turn drift heals within
        // seconds instead of at the next faction switch. Only send while our
        // own board is idle (a snapshot taken mid-animation has stale tiles)
        // and not right after applying a partner order (its effects may still
        // be settling).
        if (CoopNet.Mode == CoopMode.Host && !GuestReload.HostActive
            && now - _lastPeriodicAt > PeriodicEvery
            && Replication.BoardSettled()
            && Replication.HitsQuiet()
            && now - Replication.LastRemoteOrderAppliedAt > 2f)
        {
            _lastPeriodicAt = now;
            SendSnapshot(tm);
        }

        // NOTE: do NOT write tm.m_Round to match the host. Tried in 1.4.18 --
        // the round counter is load-bearing (TurnOrderPanel.UpdateFactions
        // indexes off round-derived state) and setting it mid-battle crashed
        // NextFaction with an ArgumentOutOfRangeException, breaking the guest's
        // battle entry. The one-round display drift must be fixed at its source
        // (faction-advance cycling) instead. _hostRound is still parsed for
        // logging/diagnosis only.

        // (The old stranded-on-AI-faction stepping failsafe is gone in 1.5.2 --
        // the faction pump advances the guest purely from replicated done-state,
        // so a "stranded" guest simply means the host hasn't finished that
        // faction yet. Mod code never calls NextFaction anymore.)

        // Round mismatch warning: rounds can no longer be silently off --
        // objectives follow the HOST's round. DEBOUNCED: during a rollover the
        // counters legitimately differ for a few seconds (the host rolls first;
        // our header info can also be up to a snapshot-interval stale), so only
        // a mismatch that PERSISTS is a real problem.
        if (CoopNet.Mode == CoopMode.Guest && _hostRound > 0
            && now - _hostPhaseAt < PeriodicEvery * 2)     // host info is fresh
        {
            int myRound = 0;
            try { myRound = tm.GetRound(); } catch { }
            bool mismatch = myRound != 0 && myRound != _hostRound;
            if (!mismatch || Replication.RolloverPumpActive)
                _roundMismatchSince = -1f;
            else if (_roundMismatchSince < 0)
                _roundMismatchSince = now;
            else if (now - _roundMismatchSince > 8f && _lastRoundWarned != myRound)
            {
                _lastRoundWarned = myRound;
                CoopRunner.ShowDesyncWarning($"ROUND MISMATCH: host is on round {_hostRound}, you are on {myRound} -- objectives follow the host");
                Plugin.Logger.LogWarning($"[SYNC] Round mismatch: host {_hostRound}, guest {myRound} (persisted 8s)");
            }
        }
        else _roundMismatchSince = -1f;

        // Guest: a pending HARD snapshot takes priority and ignores staleness --
        // it is an explicit "make my board match the host" command.
        if (_pendingHard != null
            && Replication.BoardSettled()
            && Replication.DeferredCount == 0)
        {
            var snap = _pendingHard;
            _pendingHard = null;
            _pendingSnapshot = null;   // superseded
            ApplySnapshot(snap, hard: true);
            // If this hard sync was part of a sync pause, confirm to the host
            // and resume both sides.
            // NOT during someone else's rejoin: that pause belongs to the host and
            // ends for EVERYONE at once on its rjdone (user 2026-10-04: "not partial
            // finishes but everyone"). A hard snapshot landing here mid-rejoin used
            // to end this machine's pause early and tell the host it was done.
            if (Replication.SyncPauseActive && GuestReload.RejoiningName == null)
            {
                CoopNet.Send(new CoopMsg { T = "spdone" });
                EndSyncPause("hard state applied");
            }
        }

        // Guest: apply the queued snapshot once it is safe to do so. A snapshot
        // that arrived before (or within a second of) our own last order was
        // built WITHOUT that order -- applying it would yank our unit straight
        // back. Discard it; the host's periodic stream sends a fresh one within
        // seconds anyway.
        if (_pendingSnapshot != null)
        {
            if (_pendingSnapshotAt < Replication.LastOrderSentAt + 1f)
            {
                _pendingSnapshot = null;               // stale -- predates our order
            }
            // THE SAME FOR A PARTNER'S MOVE (r148/r149: a truck's relayed move
            // replayed instantly here while this snapshot, built before it, was
            // held for a quiet board; applying it teleported the truck back to
            // its old tile and the next snapshot forward again, every truck).
            // A move that arrived after the snapshot is newer than it.
            else if (_pendingSnapshotAt < Replication.LastRemoteMoveAt)
            {
                _pendingSnapshot = null;               // stale -- predates a partner's move
            }
            else if (Replication.BoardSettled()
                && Replication.DeferredCount == 0
                && now - Replication.LastOrderSentAt > 2f)
            {
                var snap = _pendingSnapshot;
                _pendingSnapshot = null;
                _deferredSnapshotAt = _pendingSnapshotAt;
                try { ApplySnapshot(snap); }
                finally { _deferredSnapshotAt = -1f; }
            }
        }
    }

    // Guest: reconcile each actor to the host's authoritative state. Player units
    // are matched by roster index, AI units by their stable coop id, so even
    // if their POSITION drifted we snap them back to the right tile
    // (ForceTeleport) and fix AP/HP. In HARD mode, entries that fail identity
    // matching fall back to template+faction+nearest-tile matching, so even
    // unidentifiable units (capture collisions) get corrected.
    // localAllowed: a CHECKPOINT RESTORE rewinds both boards, so the host
    // applies it to itself as well -- otherwise "restore" would just mean
    // "drag the guest to the host's current reality", which is the one thing a
    // rewind must not do.
    private static float _lastSkipLogAt;

    // Units a routine snapshot has already tried to board this round (r160, #75).
    private static readonly HashSet<(IntPtr, int)> _softBoardTried = new();
    private static int SafeRound(TacticalManager tm) { try { return tm.GetRound(); } catch { return -1; } }

    internal static void ApplySnapshot(string data, bool hard = false, bool localAllowed = false)
    {
        if (!localAllowed && CoopNet.Mode != CoopMode.Guest) return;
        if (string.IsNullOrEmpty(data)) return;
        // Same race from this side: a soft snapshot arriving while this machine is
        // still landing a replayed hit describes the board before that hit. Skip
        // it; the next one (seconds away) heals anything real. Hard snapshots
        // (FIX DESYNC, sync pause) always apply.
        if (!hard && !Replication.HitsQuiet())
        {
            if (UnityEngine.Time.realtimeSinceStartup - _lastSkipLogAt > 5f)
            {
                _lastSkipLogAt = UnityEngine.Time.realtimeSinceStartup;
                Plugin.Logger.LogInfo("[DESYNC] Skipped a snapshot that arrived mid-hit (damage still landing here)");
            }
            return;
        }
        var tm = TacticalManager.Get();
        var map = tm?.GetMap();
        if (map == null) return;
        EnsureCoopIds(tm);   // make sure our AI ids exist before matching by them
        // Only trust the snapshot's turn-flags while it describes the round we
        // are actually playing (see ApplyCombatState).
        bool sameRound = false;
        // ...and the same ACTIVE FACTION (run 24): at a round rollover the host's
        // snapshot already said round 3 while its turn flags were still round 2's,
        // and settled a unit DONE at the start of the new round on the guest.
        try
        {
            int sr = RoundOfSnapshot(data);
            sameRound = sr > 0 && sr == tm.GetRound();
            int sf = FactionOfSnapshot(data);
            if (sameRound && sf >= 0 && sf != tm.GetActiveFactionID()) sameRound = false;
        }
        catch { }
        int fixedHpAp = 0, teleported = 0, hardMatched = 0, fixedState = 0, resurrected = 0, refused = 0;
        Replication.ApplyingRemote = true;
        try
        {
            // Pass 1: resolve every entry, fix HP/AP immediately, and COLLECT
            // position mismatches instead of fixing them inline.
            var moves = new List<(Actor actor, int x, int y)>();
            var claimed = new HashSet<IntPtr>();
            var unmatched = new List<(int x, int y, int ap, string hp, int tmpl, int fac, string[] fields)>();
            foreach (var entry in data.Split(';'))
            {
                if (string.IsNullOrEmpty(entry)) continue;
                var f = entry.Split(',');
                if (f.Length < 5) continue;
                if (!int.TryParse(f[0], out int x) || !int.TryParse(f[1], out int y)
                    || !int.TryParse(f[2], out int ap) || !int.TryParse(f[4], out int rosterIdx)) continue;
                int coopId = f.Length >= 6 && int.TryParse(f[5], out int cid) ? cid : -1;
                int tmpl = f.Length >= 7 && int.TryParse(f[6], out int th) ? th : 0;
                int fac = f.Length >= 8 && int.TryParse(f[7], out int fc) ? fc : int.MinValue;
                bool dead = f.Length >= 9 && f[8] == "1";

                Actor actor = null;
                if (rosterIdx >= 0)
                    actor = FindByRosterIndex(rosterIdx);        // player: stable roster id
                else if (coopId >= 0)
                    actor = FindByCoopId(tm, coopId);            // AI/enemy: stable id
                if (actor == null && x >= 0)
                {
                    var onTile = Replication.ActorOn(map.GetTile(x, y));  // fallback: by tile
                    if (TileFallbackFits(onTile, rosterIdx, coopId, tmpl)) actor = onTile;
                }
                if (actor == null || !claimed.Add(actor.Pointer))
                {
                    // No match (or the tile fallback hit an already-claimed
                    // unit). In hard mode these get a second chance below.
                    if (hard && x >= 0 && !dead) unmatched.Add((x, y, ap, f[3], tmpl, fac, f));
                    continue;
                }

                // Host says this unit is DEAD -- if our copy still lives, kill
                // it (its corpse stays where it stands; position is irrelevant).
                if (dead)
                {
                    if (!actor.IsDying()) fixedHpAp += KillActor(actor);
                    continue;
                }
                // Alive in the checkpoint but dead here -- rewind the death.
                // Only a HARD restore does this: routine snapshots must never
                // resurrect, since a snapshot in flight when a unit dies would
                // otherwise undo a legitimate kill.
                if (hard && actor.IsDying())
                {
                    int revived = ReviveActor(actor, f[3]);
                    if (revived > 0) { fixedState += revived; resurrected++; }
                }

                // OWNERSHIP-SCOPED AUTHORITY.
                //
                // The snapshot is the HOST's view, and for a unit the GUEST
                // controls that view is a replay -- the guest's own copy is the
                // one that actually simulated the action, including where an
                // overwatch interrupt stopped it. Overwriting it here lets a
                // replay overrule the original, which is how a legitimately
                // interrupted move got dragged to a tile it never reached.
                //
                // So routine snapshots leave a locally-owned player unit's
                // position and AP alone: those arrive authoritatively from the
                // owner's own "land" and "ap" messages. A HARD restore still
                // overrides everything -- that is the explicit "make my board
                // match the host" hammer, and it must have no exceptions.
                bool actorIsPlayerUnit = Ownership.Active && Ownership.IsKnownPlayerUnit(actor);
                bool ownedHere = !hard && Ownership.Active
                                 && Ownership.IsKnownPlayerUnit(actor) && Ownership.IsLocal(actor);

                // STANDING ON THE HOST, STILL ABOARD HERE. Unloads travel as
                // ordinary Leave moves, so a rebuilt board (GuestReload) has every
                // transport still full (run 32: host 1 loaded, guest 3), and a
                // contained unit is never repositioned below. A hard restore takes
                // it out onto the host's tile; the second pass can then move the
                // emptied vehicle.
                // Field 14 says whether the HOST has this unit aboard something.
                int cref = -1;
                bool containmentKnown = f.Length >= 15 && int.TryParse(f[14], out cref);
                bool hostAboard = containmentKnown && cref != -1;
                bool hereAboard = ContainerSync.IsContained(actor);
                // ROUTINE SNAPSHOTS BOARD TOO (r160 09:33:53, #75): the host's
                // outcasts entered a building at (23,15); both clients' engines
                // refused the relayed Enter and kept them on the doorstep (23,14).
                // A soft snapshot never boarded and never moves an aboard unit, so
                // the hash differed every round to the end of the mission. Not for
                // a unit owned here, not while its relayed move is still being
                // checked, and once per unit per round so a refusal cannot repeat.
                bool boardMismatch = hostAboard && !hereAboard && (rosterIdx >= 0 || coopId >= 0);
                bool softBoard = boardMismatch && !hard && !ownedHere && !Replication.IsReplayingMoveFor(actor)
                                 && _softBoardTried.Add((actor.Pointer, SafeRound(tm)));
                if (boardMismatch && (hard || softBoard))
                {
                    // Aboard on the host, standing here: board it, don't move it.
                    var container = ResolveContainerRef(tm, cref);
                    if (container != null && ContainerSync.EnterLikeEngine(actor, container)) teleported++;
                    // -9: a structure. The host's x,y is the building's tile.
                    else if (container == null && cref == -9 && x >= 0
                             && ContainerSync.EnterTileLikeEngine(actor, map.GetTile(x, y), $"structure at ({x},{y})")) teleported++;
                }
                if (hard && x >= 0 && (rosterIdx >= 0 || coopId >= 0) && hereAboard && !hostAboard)
                {
                    var dest = map.GetTile(x, y);
                    // The engine's own dismount first (a plain Leave move). The
                    // teleporting eject and SpawnContainedEntity are both refused
                    // for transport riders (runs 33, 35); kept as fallbacks.
                    // NO `continue`: casualties must reconcile whether or not the
                    // rider got out (run 35: riders kept their fresh full squads,
                    // 8 living against the save's 6, because this skipped ApplyHp).
                    // NEVER SpawnContainedEntity here: it SPAWNS a new copy of the
                    // rider rather than ejecting this one (run 38: an extra
                    // chaingun team on the guest only, seen by the host's
                    // PhantomWatch). A rider left aboard is the lesser fault.
                    if (ContainerSync.LeaveLikeEngine(actor, dest)) teleported++;
                    else if (dest != null && ContainerSync.ForceLeaveContainer(actor, dest)) teleported++;
                }

                // A rider's x,y is its transport's tile: never "move" it there.
                // A relayed move still waiting for its landing ("land" or the
                // arrival check) is settled by that, not by a snapshot that may
                // have been built before the move (r149 truck back-and-forth).
                bool moveInFlight = !hard && Replication.IsReplayingMoveFor(actor);
                if ((rosterIdx >= 0 || coopId >= 0) && x >= 0 && !ownedHere && !hostAboard && !moveInFlight && CanBeRepositioned(actor, hard))
                {
                    var curTile = actor.GetLastTile()?.GetTilePos();
                    if (curTile == null || curTile.Value.x != x || curTile.Value.y != y)
                        moves.Add((actor, x, y));
                }

                // AI ACTION POINTS DURING AN AI PHASE ARE PIPELINE LAG, NOT DRIFT.
                //
                // The host runs the AI and the guest replays it, so mid-phase the
                // two are legitimately at different points in the same sequence:
                // a snapshot catches a unit the host has already moved against one
                // the guest has not replayed yet. That produced the whole
                // "0 -> 100 / 20 -> 0" correction stream, ~50 a mission, every one
                // of which would have resolved itself a moment later.
                //
                // It is also correction for nothing: the guest never makes AI
                // decisions, it replays the host's orders whatever its local AP
                // says. So reconcile AI action points only once the player phase
                // is active and the sequence has actually landed.
                bool aiMidPhase = false;
                try
                {
                    aiMidPhase = !actorIsPlayerUnit && tm != null && !tm.IsPlayerTurn();
                }
                catch { }

                // AI ACTION POINTS ARE NOT WORTH CORRECTING, AND THE HASH AGREES.
                //
                // AI AP was dropped from the state hash because it drifts on the
                // guest while relayed orders are still landing and cannot affect
                // play -- the guest never makes AI decisions, it replays the
                // host's. Correcting it here was the same value judged by the
                // opposite standard: every mission logged a stream of "AP drift
                // 'enemy.alien_stinger': mine 4 -> host 100" repairs that changed
                // nothing anyone could see, while burying the corrections that do
                // matter. Only a HARD restore still touches it, since that is the
                // deliberate "make everything identical" hammer.
                bool aiApNotWorthFixing = !actorIsPlayerUnit && !hard;

                // PLAYER AP HAS ITS OWN AUTHORITATIVE CHANNEL -- DO NOT SECOND-GUESS IT.
                //
                // Snapshots already skip AP for units owned by the RECEIVER, but
                // they still overwrote it for the partner's units, racing the
                // 'ap' follow-up the acting machine sends. The result was the two
                // sides correcting the SAME unit in opposite directions and never
                // converging: "'player_squad.tech': mine 0 -> host 100" followed
                // by "'player_squad.tech': mine 100 -> host 0". Authority follows
                // the ACTOR, so the machine that spent the AP reports it and
                // nobody else gets a vote. A hard restore still overrides
                // everything -- that is the point of a hard restore.
                bool playerApOwnedElsewhere = actorIsPlayerUnit && !hard;

                if (actor.GetActionPoints() != ap && !ownedHere && !aiMidPhase
                    && !aiApNotWorthFixing && !playerApOwnedElsewhere
                    && Replication.ApValueIsSafe(actor, ap))
                {
                    // Name the drift. A recurring "1 HP/AP corrected" says
                    // nothing about its source; the unit and the two values do.
                    Plugin.Logger.LogInfo($"[DESYNC]   AP drift '{TemplateNameOf(actor)}' at ({x},{y}): mine {actor.GetActionPoints()} -> host {ap}");
                    actor.SetActionPoints(ap, true); fixedHpAp++;
                }
                fixedHpAp += ApplyHp(actor, f[3], TemplateNameOf(actor), x, y, sameRound);
                fixedState += ApplyCombatState(actor, f, hard, sameRound);
            }

            // HARD mode pass: match leftover entries to unclaimed local units of
            // the same template + faction, nearest first. This is what fixes
            // units that have no stable identity at all.
            if (hard && unmatched.Count > 0)
            {
                var pool = new List<Actor>();
                foreach (var a in AllActors(tm))
                {
                    if (a == null || a.IsDying() || claimed.Contains(a.Pointer)) continue;
                    if (RosterIndexOf(a) >= 0) continue;   // player units always match by roster
                    pool.Add(a);
                }
                foreach (var u in unmatched)
                {
                    Actor best = null; int bestDist = int.MaxValue; int bestIdx = -1;
                    for (int i = 0; i < pool.Count; i++)
                    {
                        var a = pool[i];
                        if (a.GetFactionID() != u.fac) continue;
                        if (Fnv(TemplateNameOf(a)) != u.tmpl) continue;
                        var p = a.GetLastTile()?.GetTilePos();
                        if (p == null) continue;
                        int d = Math.Abs(p.Value.x - u.x) + Math.Abs(p.Value.y - u.y);
                        if (d < bestDist) { bestDist = d; best = a; bestIdx = i; }
                    }
                    if (best == null) continue;
                    pool.RemoveAt(bestIdx);
                    claimed.Add(best.Pointer);
                    hardMatched++;
                    var cur = best.GetLastTile()?.GetTilePos();
                    if ((cur == null || cur.Value.x != u.x || cur.Value.y != u.y) && CanBeRepositioned(best))
                        moves.Add((best, u.x, u.y));
                    if (best.GetActionPoints() != u.ap) { best.SetActionPoints(u.ap, true); fixedHpAp++; }
                    fixedHpAp += ApplyHp(best, u.hp);
                    if (u.fields != null) fixedState += ApplyCombatState(best, u.fields, hard, sameRound);
                }
            }

            // Pass 2..n: teleport drifted units, MULTI-PASS. A unit's correct
            // tile is often occupied by ANOTHER drifted unit (that's exactly how
            // contested-tile forks look); each pass frees tiles for the next,
            // so chains resolve instead of being skipped.
            bool parked = false;
            for (int pass = 0; pass < 4 && moves.Count > 0; pass++)
            {
                bool progress = false;
                for (int i = 0; i < moves.Count; )
                {
                    var (actor, x, y) = moves[i];
                    var dest = map.GetTile(x, y);
                    var occupant = Replication.ActorOn(dest);
                    if (dest != null && (occupant == null || occupant.Pointer == actor.Pointer))
                    {
                        var from = actor.GetLastTile()?.GetTilePos();
                        MovementAction action = default;
                        // Force | ForceTeleport | AllowMoveThroughActors |
                        // SkipPathfinding. Plain ForceTeleport alone can be
                        // REFUSED when the destination is contested or has no
                        // valid path -- and the refusal was invisible, because
                        // MoveTo's bool result was ignored and counted as a
                        // success regardless. That is how a restore could report
                        // seven moved units and change nothing on screen.
                        // UNDER ApplyingRemote, like every replayed change. Without it the
                        // action lock (MoveIsHeld) treated the teleport as PLAYER input and
                        // refused it during the catching-up client's sync pause -- r124:
                        // every player unit and the carrier REFUSED, enemies (no lock) moved.
                        bool ok;
                        bool prevAr = Replication.ApplyingRemote;
                        Replication.ApplyingRemote = true;
                        string retry = "";
                        try
                        {
                            ok = actor.MoveTo(dest, ref action, (MovementFlags)(1 | 2 | 4 | 8));
                            // r123-r125: player units on a rejoined client refused every
                            // pass (not contained, not active, AP in hand). Probe one
                            // candidate cause: the round rolls mark every unit as having
                            // acted. Clear it for a single retry and record the outcome.
                            if (!ok)
                            {
                                bool acted = false;
                                try { acted = actor.HasActed(); } catch { }
                                retry = $" hasActed={acted} stance={actor.GetStance()}";
                                if (acted)
                                {
                                    try { actor.m_HasActed = false; } catch { }
                                    action = default;
                                    ok = actor.MoveTo(dest, ref action, (MovementFlags)(1 | 2 | 4 | 8));
                                    retry += ok ? " -> RETRY OK after clearing hasActed" : " -> retry still refused";
                                    if (!ok) { try { actor.m_HasActed = true; } catch { } }
                                }
                                if (ok) Plugin.Logger.LogWarning($"[DESYNC]   '{TemplateNameOf(actor)}' first refused{retry}");
                            }
                        }
                        // r126: MovementSystem.GetPath threw a NullReferenceException for a
                        // civilian mid-pass and the exception aborted the WHOLE apply
                        // ("Snapshot apply failed"). One unit's throw is that unit's refusal.
                        catch (Exception me)
                        {
                            ok = false;
                            retry += $" THREW {me.GetType().Name}";
                        }
                        finally { Replication.ApplyingRemote = prevAr; }
                        // #66: on a rejoined client the engine refuses every snapshot move of
                        // a PLAYER unit at round 6+ (r122-r126) and gives no reason. Ask the
                        // engine why, then place the unit directly: SetTile + m_LastTile +
                        // each element's TeleportTo -- the steps a ForceTeleport ends in,
                        // without the path search that is failing.
                        if (!ok)
                        {
                            retry += MoveRefusalDiag(actor, dest);
                            if (PlaceDirectly(actor, dest, x, y, out string how))
                            {
                                ok = true;
                                Plugin.Logger.LogWarning($"[DESYNC]   PLACED '{TemplateNameOf(actor)}' -> ({x},{y}) directly after the engine refused{retry} | {how}");
                            }
                            else retry += $" | direct placement failed: {how}";
                        }
                        string fromTxt = from == null ? "(nowhere)" : $"({from.Value.x},{from.Value.y})";
                        if (ok)
                        {
                            // MoveTo is ASYNCHRONOUS -- it ACCEPTS the move and
                            // the unit's tile updates when the movement
                            // completes, which is never within this call. The
                            // 2.8.2 "did it land?" check therefore ran too early
                            // and reported working teleports as REFUSED. Verify
                            // on a later frame instead (see VerifyPendingMoves).
                            Plugin.Logger.LogInfo($"[DESYNC]   moving '{TemplateNameOf(actor)}' {fromTxt} -> ({x},{y})");
                            teleported++;
                            _pendingMoves.Add((actor, x, y, TemplateNameOf(actor)));
                        }
                        else
                        {
                            refused++;
                            // WHY (r123: the troop carrier refused twice on a rejoined
                            // client, on a free tile, and every later order for it
                            // missed). Active/selected? moving? passengers aboard?
                            string why = "";
                            try
                            {
                                bool active = tm.GetActiveActor() != null && tm.GetActiveActor().Pointer == actor.Pointer;
                                var inside = actor.GetContainedEntity();
                                why = $" active={active} isActive={actor.IsActive()} moving={actor.IsMoving()} ap={actor.GetActionPoints()} "
                                    + $"turnDone={actor.IsTurnDone()} container={actor.IsContainerForEntities()} carrying={(inside == null ? "-" : (inside.TryCast<Actor>() is Actor ia ? TemplateNameOf(ia) : "entity"))}";
                            }
                            catch { }
                            Plugin.Logger.LogWarning($"[DESYNC]   REFUSED '{TemplateNameOf(actor)}' {fromTxt} -> ({x},{y}) (engine declined the move){why}{retry}");
                        }
                        progress = true;
                        moves.RemoveAt(i);
                        continue;
                    }
                    i++;
                }

                // No pass made progress: what is left is a CYCLE -- most often a
                // plain swap, where each unit stands on the other's tile, so no
                // destination can ever free itself. Break it by parking one unit
                // on a nearby empty tile; the freed destination lets the chain
                // resolve on the next pass, and the parked unit is corrected
                // then too (it is still in this list). Without this, swapped
                // units stayed swapped for the rest of the battle -- the
                // "could not be corrected (destination occupied)" report.
                if (!progress)
                {
                    // Park ONE blocker and then STOP for this snapshot. MoveTo is
                    // asynchronous (law 8), so the parked unit is still on its old
                    // tile for the remaining passes -- continuing just parks the
                    // same unit again and again and then reports the correction as
                    // failed. The freed tile is available to the next snapshot,
                    // which is where the chain actually unwinds.
                    parked = ParkOneBlocker(map, moves);
                    break;
                }
            }
            if (moves.Count > 0)
            {
                // Parking is a deliberate two-step, not a failure: the tile frees
                // up once the park lands and the next snapshot completes the
                // chain. Reporting it as "could not be corrected" made a working
                // mechanism look like a broken one.
                if (parked)
                    Plugin.Logger.LogInfo($"[DESYNC] {moves.Count} position(s) deferred to the next snapshot (parked a blocker to break a swap)");
                else
                    Plugin.Logger.LogWarning($"[DESYNC] {moves.Count} unit position(s) could not be corrected (destination occupied)");
            }

            // NOTE: the blanket "settle every AI unit of the active faction"
            // net from 2.9.2 has been REMOVED. It could mark enemies done that
            // still had a turn coming, which reads in game as the round rolling
            // over with the enemies never acting. Convergence now comes from
            // the host's own done-flags in the snapshot above, applied to AI
            // units in the safe direction only (set done, never re-open).
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[DESYNC] Snapshot apply failed: {e.Message}"); }
        finally
        {
            Replication.ApplyingRemote = false;
            // The snapshot is authoritative and has just placed these units, so
            // any relayed-move landing check still pending is stale -- letting it
            // fire would drag a unit back off the corrected tile.
            Replication.ClearRelayedMoveChecks();
        }
        // Feeds the no-op escalation guard in RequestResync: if a correction
        // changed nothing, repeating it cannot help and must not escalate to a
        // stop-the-world pause.
        _lastResyncFixedSomething = (teleported > 0 || fixedHpAp > 0 || fixedState > 0 || resurrected > 0);

        if (teleported > 0 || fixedHpAp > 0 || fixedState > 0 || hard)
        {
            string kind = hard ? "BOARD RESTORED" : "Resynced from host";
            Plugin.Logger.LogInfo($"[DESYNC] {kind}: {teleported} position(s), {fixedHpAp} HP/AP, {fixedState} combat-state value(s), {resurrected} revived, {hardMatched} matched without id, {refused} REFUSED");
            if (refused > 0)
                CoopRunner.ShowDesyncWarning($"WARNING: {refused} unit(s) could NOT be moved -- the board is only partly restored");
            // Routine background healing of a stray hitpoint is not news. Only
            // speak up for an explicit restore, a unit actually moving, or a
            // correction big enough to matter -- a banner every few seconds for
            // "1 HP/AP" trains the player to ignore the banner that matters.
            bool worthSaying = hard || teleported > 0 || resurrected > 0 || (fixedHpAp + fixedState) >= 5;
            if (worthSaying)
                CoopRunner.ShowDesyncWarning(
                    $"{kind}: {teleported} position(s), {fixedHpAp} HP/AP, {fixedState} state value(s)"
                    + (resurrected > 0 ? $", {resurrected} unit(s) brought back" : "")
                    + (hardMatched > 0 ? $", {hardMatched} matched by type" : ""));
        }
    }

    // Park the unit blocking the first stuck correction on any free tile near
    // it, so its destination frees up and the cycle can unwind. Returns false
    // when nothing could be parked (no free tile anywhere close).
    private static bool ParkOneBlocker(Map map, System.Collections.Generic.List<(Actor actor, int x, int y)> moves)
    {
        try
        {
            for (int i = 0; i < moves.Count; i++)
            {
                var dest = map.GetTile(moves[i].x, moves[i].y);
                var blocker = Replication.ActorOn(dest);
                if (blocker == null) continue;

                var cur = blocker.GetLastTile()?.GetTilePos();
                if (cur == null) continue;

                for (int r = 1; r <= 3; r++)
                    for (int dx = -r; dx <= r; dx++)
                        for (int dy = -r; dy <= r; dy++)
                        {
                            if (System.Math.Abs(dx) != r && System.Math.Abs(dy) != r) continue;  // ring only
                            var spot = map.GetTile(cur.Value.x + dx, cur.Value.y + dy);
                            if (!Replication.TileFree(spot)) continue;
                            MovementAction act = default;
                            if (!blocker.MoveTo(spot, ref act, (MovementFlags)(1 | 2 | 4 | 8))) continue;
                            Plugin.Logger.LogInfo(
                                $"[DESYNC]   parked '{TemplateNameOf(blocker)}' at ({cur.Value.x + dx},{cur.Value.y + dy}) to break a position swap");
                            return true;
                        }
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[DESYNC] park failed: {e.Message}"); }
        return false;
    }

    // Bring a unit BACK that the checkpoint says was alive. Death is normally
    // one-way -- there is no Revive API -- but the state that MAKES a unit dead
    // is reachable: the actor's dying flag, each element's destroyed flag and
    // hitpoints, and its membership in the faction's actor list. This is the
    // piece that turns "force the board to match" into a real rewind, and it is
    // deliberately best-effort: every step is guarded, and a unit that cannot
    // be restored is reported rather than left silently broken.
    private static int ReviveActor(Actor actor, string hpField)
    {
        int fixes = 0;
        try
        {
            actor.m_IsDying = false;
            var els = actor.GetElements();
            var hp = string.IsNullOrEmpty(hpField) ? new string[0] : hpField.Split('.', System.StringSplitOptions.RemoveEmptyEntries);
            if (els != null)
                for (int e = 0; e < els.Count && e < hp.Length; e++)
                {
                    var el = els[e];
                    // Entries are "sq:hp[:slot]" (bare "hp" in old payloads). A
                    // plain TryParse on the whole entry failed for every entry
                    // since the id format landed, so revive never restored HP.
                    string entry = hp[e];
                    int c1 = entry.IndexOf(':');
                    if (c1 >= 0)
                    {
                        int c2 = entry.IndexOf(':', c1 + 1);
                        entry = c2 < 0 ? entry.Substring(c1 + 1) : entry.Substring(c1 + 1, c2 - c1 - 1);
                    }
                    if (el == null || !int.TryParse(entry, out int h) || h <= 0) continue;
                    try { el.SetDestroyedState(false); } catch { }
                    try { el.SetGameObjectVisible(true); } catch { }
                    el.SetHitpoints(h);
                    fixes++;
                }

            // Die() removes the actor from its faction's roster; without this
            // it would be invisible to the turn cycle and to every later
            // snapshot -- alive on screen but unable to act.
            try
            {
                // MUST match the actor's faction by GetIndex(), NOT by calling
                // GetFaction(actor.GetFactionID()). Those are different numbering
                // schemes -- GetFaction(int) takes a POSITION in the factions
                // array, while the actor carries a faction ID -- so passing the
                // ID filed revived units into the wrong army. A revived player
                // unit landed in faction 2, where it sat forever holding up the
                // turn order: nothing in the player phase would finish a unit
                // that was no longer in the player faction. That produced the
                // "stuck on faction 2 waiting for player_squad.x" deadlocks on
                // both the host and the guest.
                var tmv = TacticalManager.Get();
                int wantId = actor.GetFactionID();
                Menace.Tactical.AI.BaseFaction fac = null;
                var all = tmv?.GetFactions();
                for (int i = 0; all != null && i < all.Length; i++)
                {
                    var cand = all[i];
                    if (cand == null) continue;
                    try { if (cand.GetIndex() == wantId) { fac = cand; break; } } catch { }
                }

                var list = fac?.GetActors();
                if (list != null)
                {
                    bool present = false;
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] != null && list[i].Pointer == actor.Pointer) { present = true; break; }
                    if (!present) list.Add(actor);
                }
                else
                {
                    Plugin.Logger.LogWarning($"[DESYNC] Revived '{TemplateNameOf(actor)}' but found no faction with id {wantId} -- leaving it out of the roster rather than filing it wrongly");
                }
            }
            catch { }

            // A revived AI unit must come back ALREADY DONE for this round.
            // Coming back "not done" re-opens an activation the host's AI will
            // never re-run, and the guest's pump would wait on it forever. It
            // acts again next round, when the engine resets the flags itself.
            if (RosterIndexOf(actor) < 0)
            {
                try { actor.SetTurnDone(true); actor.MarkAsHasActed(); } catch { }
            }
            Plugin.Logger.LogWarning($"[DESYNC] Revived a unit the checkpoint had alive ({fixes} element(s) restored)");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[DESYNC] Revive failed: {e.Message}"); }
        return fixes;
    }

    // Kill a unit the host reports as dead: zero and Die() every live element.
    // Runs under ApplyingRemote, so nothing is echoed back.
    private static int KillActor(Actor actor)
    {
        int fixes = 0;
        try
        {
            var els = actor.GetElements();
            if (els == null) return 0;
            for (int e = 0; e < els.Count; e++)
            {
                var el = els[e];
                if (el == null || el.GetHitpoints() <= 0) continue;
                el.SetHitpoints(0);
                el.Die();
                fixes++;
            }
            if (fixes > 0)
                Plugin.Logger.LogInfo($"[DESYNC] Killed locally-alive unit that is dead on the host ({fixes} element(s))");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[DESYNC] KillActor failed: {e.Message}"); }
        return fixes;
    }

    // Fields 9..12 of a snapshot entry: turn-done, suppression, morale, stance.
    // TURN-DONE is applied only in a HARD resync: it decides who may still act,
    // so applying it from a routine snapshot that was built a moment before the
    // guest's own unit moved could silently end that unit's turn. The button
    // (and the auto-escalation behind it) is an explicit "make my board match
    // the host", which is exactly when overriding it is correct.
    // Per (actor, field) count of consecutive attempts at the SAME target value.
    // Reset whenever the target changes -- that is new information, not a retry.
    private static readonly System.Collections.Generic.Dictionary<(System.IntPtr, int), (int want, int tries)> _stateTries = new();
    private static readonly string[] _stateNames = { "suppression", "morale", "stance", "facing" };

    internal static void ResetStateRetries() => _stateTries.Clear();

    private static bool Converging(Actor actor, int field, int want)
    {
        var key = (actor.Pointer, field);
        _stateTries.TryGetValue(key, out var prev);
        int tries = (prev.want == want) ? prev.tries + 1 : 1;
        _stateTries[key] = (want, tries);

        if (tries <= 3) return true;
        if (tries == 4)
            Plugin.Logger.LogWarning(
                $"[DESYNC] '{TemplateNameOf(actor)}' {_stateNames[field]} will not hold at {want} after 3 tries -- " +
                $"the engine keeps overwriting it. Giving up on this value so the resync stops looping.");
        return false;
    }

    // A DEFERRED SNAPSHOT IS OLDER THAN EVERY RELAY THAT ARRIVED WHILE IT WAITED
    // (BUGS #59). A soft snapshot waits for the board to settle -- the whole AI
    // phase -- while esup/estance/orders apply at once. r94 (3p): host relayed an
    // enemy's turn-start morale 55 -> 70, the queued snapshot then said 55 and was
    // applied, and the next one said 70 again: two resyncs from nothing. Messages
    // on one link arrive in send order, so a relay received after the snapshot is
    // newer than it; leave that unit's combat state to the relay.
    private static float _deferredSnapshotAt = -1f;

    private static int ApplyCombatState(Actor actor, string[] f, bool hard, bool sameRound)
    {
        int fixes = 0;
        if (!hard && _deferredSnapshotAt >= 0f
            && Replication.LastRelayAt.TryGetValue(actor.Pointer, out float relayAt)
            && relayAt > _deferredSnapshotAt)
            return 0;
        try
        {
            // TURN-DONE IS RESTORED FOR PLAYER UNITS ONLY.
            //
            // Rewinding an AI unit's done flag to "not done" re-opens an
            // activation that will never happen again: the host's AI already
            // ran it this round and only relays a done flag when it actually
            // acts. The guest's faction pump then waits forever for a unit that
            // nobody will move -- the freeze/deadlock seen after restoring.
            // Player units are safe because their turn ends are explicit
            // commands that replicate normally.
            if (f.Length >= 10 && int.TryParse(f[9], out int done))
            {
                bool want = done == 1;
                bool isPlayerUnit = RosterIndexOf(actor) >= 0;
                // Player units: DONE may also be applied from an ordinary
                // same-round snapshot -- it must not wait for a hard resync.
                //
                // This used to be `hard` only, justified by "player turn ends
                // are explicit commands that replicate normally". The logs
                // disprove that: 'done' and 'skip' orders DO get lost ("Order
                // 'done' ... never resolved"). When one is, nothing ever
                // reconciles it -- the guest believes a unit still has a turn
                // forever while the host has it done, and the two sides count
                // different numbers of live units (host mine=0/theirs=1 while
                // the guest saw mine=1/theirs=2). The host then hands control
                // to a player whose units are, on the host, already finished:
                // permanent deadlock with both processes healthy.
                //
                // Same safe direction as AI units -- mark DONE, never re-open.
                // Re-opening is what revives an activation nobody will run.
                // AI units: may only be marked DONE, never re-opened. Setting
                // an enemy back to "not done" revives an activation the host
                // will never run again (the 2.9.2 deadlock), while marking a
                // straggler done is exactly what unsticks a stalled faction --
                // and it comes from the host's own state rather than a guess.
                // AI done-flags may also be applied from an ORDINARY snapshot,
                // not just a hard resync -- as long as the snapshot is from the
                // round we are actually playing. This is what lets a routine
                // sync clear a stalled faction: the guest was waiting on a
                // done-flag it never received, the host's snapshot carries it,
                // and marking it done is the safe direction. Requiring the same
                // round stops a stale snapshot from settling units whose flags
                // the engine has since reset for a new round.
                // NEVER LET A SNAPSHOT FINISH A UNIT WE OWN.
                //
                // The wire already refuses to relay skip/done for a partner's unit
                // (MayBroadcastFor), but the SNAPSHOT carried turn-done for every
                // player unit regardless -- so the host's local engine auto-skip
                // leaked through and finished the GUEST's units for it. Measured:
                // the guest sat at mine=0/theirs=0 for 147 consecutive samples and
                // acted 9 times to the host's 33, with the board then stalling
                // (watchdog fired 6 times). Blocking one channel and leaving the
                // other open is the same mistake as the AI-AP hash, one layer down.
                //
                // Authority follows the ACTOR: our own units' turn-ends are ours to
                // declare. A hard restore still overrides, as always.
                bool ownedByUs = false;
                try { ownedByUs = Ownership.Active && Ownership.IsLocal(actor); } catch { }
                if (isPlayerUnit && ownedByUs && !hard) return fixes;

                bool allowed = (want && (hard || sameRound)) || (isPlayerUnit && hard);
                if (allowed && actor.IsTurnDone() != want)
                {
                    if (want && isPlayerUnit && !hard)
                        Plugin.Logger.LogInfo(
                            $"[DESYNC] '{TemplateNameOf(actor)}' settled DONE from the host's snapshot -- " +
                            $"its turn-end never arrived as an order");
                    else
                        // Name every correction (BUGS.md #14): the last unexplained
                        // "combat-state value(s)" were these, logged nowhere.
                        Plugin.Logger.LogInfo($"[DESYNC]   TURN-DONE drift '{TemplateNameOf(actor)}': mine {!want} -> host {want}");
                    actor.SetTurnDone(want);
                    if (want) actor.MarkAsHasActed();
                    fixes++;
                }
            }
            // A CORRECTION THAT NEVER STICKS MUST GIVE UP AND NAME ITSELF.
            //
            // These three are recomputed by the engine, so if our write does not
            // hold we re-apply it on every snapshot forever: the guest logs
            // "Resynced from host: ... 2 combat-state value(s)" every tick, sits
            // permanently in "syncing with host", and its harness never gets to
            // act -- a live deadlock with both processes healthy. Retry a few
            // times (a correction legitimately needs a tick or two to settle),
            // then stop and log WHICH field refuses to stick, so the underlying
            // cause is visible instead of hidden behind an infinite retry.
            // NAME THE FIELD AND THE UNIT, NOT JUST A COUNT.
            //
            // These used to be reported only as "N combat-state value(s)", which
            // says a difference existed and nothing else. A player asked why
            // suppression behaved differently on two of their units and the logs
            // could not answer it: suppression is NOT part of the state hash
            // (that covers position, hitpoints and player action points only), so
            // a suppression divergence is invisible until a snapshot happens to
            // be sent for some other reason, and then it is counted rather than
            // described. Position drift has always logged "HP drift ..." lines;
            // this is the same courtesy for the three fields that decide whether
            // a unit is pinned, panicking or crouched.
            string who = TemplateNameOf(actor);
            // EXPERIMENT (DESIGN-REVIEW.md proposal 2): with CorrectCombatState off,
            // these four are diagnostics only -- logged, not written -- except in a
            // hard restore. Their gameplay consequences (AP, position, HP) are hashed.
            // OBSERVED drift is logged identically in BOTH modes, before any write
            // decision: the experiment's endpoint is drift observed, not
            // corrections applied (which the switch zeroes by construction).
            if (!hard) LogCombatObserved(actor, f, who);
            bool writeState = hard || Plugin.CorrectCombatState == null || Plugin.CorrectCombatState.Value;
            if (!writeState) return fixes;
            if (f.Length >= 11 && int.TryParse(f[10], out int sup)
                && Math.Abs(actor.GetSuppression() - sup) >= 1f
                && Converging(actor, 0, sup))
            {
                Plugin.Logger.LogInfo($"[DESYNC]   SUPPRESSION drift '{who}': mine {actor.GetSuppression():F0} -> host {sup}");
                actor.SetSuppression(sup); fixes++;
            }
            if (f.Length >= 12 && int.TryParse(f[11], out int mor)
                && Math.Abs(actor.GetMorale() - mor) >= 1f
                && Converging(actor, 1, mor))
            {
                Plugin.Logger.LogInfo($"[DESYNC]   MORALE drift '{who}': mine {actor.GetMorale():F0} -> host {mor}");
                actor.SetMorale(mor); fixes++;
            }
            if (f.Length >= 13 && int.TryParse(f[12], out int st)
                && (int)actor.GetStance() != st
                && Converging(actor, 2, st))
            {
                Plugin.Logger.LogInfo($"[DESYNC]   STANCE drift '{who}': mine {(int)actor.GetStance()} -> host {st}");
                actor.SetStance((ActorStance)st, true); fixes++;
            }
            // FACING (BUGS.md #16): cover and firing arcs depend on it, and it
            // had no channel at all. Owner-scoped like position: never turn a unit
            // this machine commands on the host's say-so, except in a hard restore.
            bool ownDir = false;
            try { ownDir = Ownership.Active && Ownership.IsKnownPlayerUnit(actor) && Ownership.IsLocal(actor); } catch { }
            // An AI unit mid-activation is still turning on the host: two snapshots
            // 22 ms apart flipped one back and forth (run 35). Same pipeline lag as
            // AI action points -- settle it once the players' phase is back.
            bool aiTurning = false;
            try { aiTurning = !hard && RosterIndexOf(actor) < 0 && !TacticalManager.Get().IsPlayerTurn(); } catch { }
            if (f.Length >= 14 && int.TryParse(f[13], out int dir) && (hard || !ownDir) && !aiTurning
                && !actor.IsMoving() && (int)actor.GetDirection() != dir
                && Converging(actor, 3, dir))
            {
                Plugin.Logger.LogInfo($"[DESYNC]   FACING drift '{who}': mine {(int)actor.GetDirection()} -> host {dir}");
                actor.SetDirection((Direction)dir); fixes++;
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[DESYNC] Combat-state apply failed: {e.Message}"); }
        return fixes;
    }

    // Raw comparison, no guards, same in both CorrectCombatState modes: the
    // measurement for DESIGN-REVIEW.md proposal 2. Per-unit lines, so a unit that
    // stays off across snapshots (unrelayed writer) can be told from one that
    // comes and goes (sampling mid-change).
    private static void LogCombatObserved(Actor actor, string[] f, string who)
    {
        try
        {
            if (f.Length >= 11 && int.TryParse(f[10], out int sup) && Math.Abs(actor.GetSuppression() - sup) >= 1f)
                Plugin.Logger.LogInfo($"[DESYNC]   OBS SUPPRESSION '{who}': mine {actor.GetSuppression():F0} host {sup}");
            if (f.Length >= 12 && int.TryParse(f[11], out int mor) && Math.Abs(actor.GetMorale() - mor) >= 1f)
                Plugin.Logger.LogInfo($"[DESYNC]   OBS MORALE '{who}': mine {actor.GetMorale():F0} host {mor}");
            if (f.Length >= 13 && int.TryParse(f[12], out int st) && (int)actor.GetStance() != st)
                Plugin.Logger.LogInfo($"[DESYNC]   OBS STANCE '{who}': mine {(int)actor.GetStance()} host {st}");
            if (f.Length >= 14 && int.TryParse(f[13], out int dir) && !actor.IsMoving() && (int)actor.GetDirection() != dir)
                Plugin.Logger.LogInfo($"[DESYNC]   OBS FACING '{who}': mine {(int)actor.GetDirection()} host {dir}");
        }
        catch { }
    }

    private static int ApplyHp(Actor actor, string hpField, string name = null, int x = -1, int y = -1,
                               bool freshEnough = false)
    {
        int fixes = 0;
        var els = actor.GetElements();
        // An actor with NO living elements arrives as an empty field, and
        // "".Split('.') is ONE empty entry, not none: a unit dead on both machines
        // read as "0 living here, host sent 1" and re-corrected forever (run 14,
        // the alien queen). Empty means zero.
        var hp = string.IsNullOrEmpty(hpField) ? new string[0] : hpField.Split('.', System.StringSplitOptions.RemoveEmptyEntries);

        // SAY SO WHEN THE ROSTERS THEMSELVES DIFFER.
        //
        // The loop below compares only up to the SHORTER of the two lists, so a
        // squad with a different number of living members silently goes
        // half-unchecked -- and the state hash then keeps reporting a difference
        // this repair has quietly declined to see. That is why mismatches of
        // ~80-180 persisted with NO "HP drift" line anywhere in the log. Naming it
        // turns an invisible mismatch into evidence.
        // ONLY TRUST A ROSTER COMPARISON FROM THE ROUND WE ARE ACTUALLY IN.
        //
        // A snapshot is a POINT IN TIME. One taken while a unit was alive, applied
        // after it died here, reports a roster difference that never existed --
        // and the tell is that the differences appear in BOTH directions at random
        // ("0 living here, host sent 3" then "2 living here, host sent 1"). A
        // systematic over- or under-kill would be consistently one-signed.
        //
        // Warning on a stale snapshot sent me chasing a divergence that was an
        // artifact of timing, so the comparison is now gated on the snapshot
        // describing the round we are playing.
        if (els != null && name != null && els.Count != hp.Length && freshEnough)
            Plugin.Logger.LogWarning(
                $"[DESYNC]   ROSTER DIFFERS '{name}' at ({x},{y}): {els.Count} living element(s) here, " +
                $"host sent {hp.Length} (same round) -- reconciling by squaddie id");
        // NONE LEFT ON THE HOST = DEAD ON THE HOST. The member reconcile below
        // needs at least one host entry to read the key format from, so a unit the
        // host has wholly killed was never settled (hand test 2026-10-04: a
        // detonated wardog alive on the rejoined client, "1 living here, host sent
        // 0" every 5 s). Not for player squads: a downed squad is not a dead one.
        if (els != null && els.Count > 0 && hp.Length == 0 && freshEnough && !Ownership.IsKnownPlayerUnit(actor))
        {
            bool prevAr = Replication.ApplyingRemote;
            Replication.ApplyingRemote = true;
            try
            {
                actor.Die(false);
                if (name != null) Plugin.Logger.LogWarning($"[DESYNC]   '{name}' at ({x},{y}) has no living members on the host -- killed here too");
                fixes++;
            }
            catch { }
            finally { Replication.ApplyingRemote = prevAr; }
            return fixes;
        }
        // SAY WHICH ELEMENTS. Run 13 looped on this line 8+ times for a VIP that
        // died in a destroyed guest vehicle on the host only, and never once
        // settled -- with no way to tell why from the log. Once per actor.
        if (els != null && name != null && els.Count != hp.Length && freshEnough && _rosterDumped.Add(name + "@" + x + "," + y))
        {
            var mine = new System.Text.StringBuilder();
            for (int i = 0; i < els.Count; i++)
            {
                try { mine.Append($" [slot{els[i].GetElementIndex()} sq{els[i].GetSquaddieId()} hp{els[i].GetHitpoints()} alive={els[i].IsAlive()}]"); }
                catch { mine.Append(" [?]"); }
            }
            Plugin.Logger.LogWarning($"[DESYNC]     here:{mine}   host payload: {string.Join(" ", hp)}  (sq:hp:slot)");
        }

        if (els == null) return fixes;

        // MATCH BY PERMANENT SLOT WHEN THE PAYLOAD CARRIES ONE ("sq:hp:slot"),
        // ELSE BY SQUADDIE ID, ELSE BY INDEX FOR OLD-FORMAT PAYLOADS.
        //
        // Index matching stopped at the shorter list, so a squad that differed by
        // one member left the extra elements unexamined for ever. Matching by id
        // reconciles regardless of length or ordering -- but SQUADDIE IDS ARE ONLY
        // UNIQUE IN PLAYER SQUADS. Every element of a pirate squad reports id 0, so
        // the id match hit the FIRST living element every time: one verified run
        // logged 110 "HP drift 'enemy.pirate_outcasts' sq0" corrections, each one
        // writing a different element's HP onto element 0. The permanent slot
        // (GetElementIndex) is unique within an actor, so it wins when present.
        for (int e = 0; e < hp.Length; e++)
        {
            var part = hp[e];
            int colon = part.IndexOf(':');
            int colon2 = colon < 0 ? -1 : part.IndexOf(':', colon + 1);

            if (colon < 0)                                  // legacy "hp" form
            {
                if (e >= els.Count) continue;
                if (!int.TryParse(part, out int hOld) || els[e] == null) continue;
                if (els[e].GetHitpoints() == hOld) continue;
                if (name != null)
                    Plugin.Logger.LogInfo($"[DESYNC]   HP drift '{name}' at ({x},{y}) element {e}: mine {els[e].GetHitpoints()} -> host {hOld}");
                els[e].SetHitpoints(hOld); fixes++;
                continue;
            }

            if (!int.TryParse(part.Substring(0, colon), out int sq)) continue;
            string hpText = colon2 < 0 ? part.Substring(colon + 1) : part.Substring(colon + 1, colon2 - colon - 1);
            if (!int.TryParse(hpText, out int h)) continue;
            int slot = -1;
            if (colon2 > 0 && !int.TryParse(part.Substring(colon2 + 1), out slot)) slot = -1;

            Element match = null;
            for (int i = 0; i < els.Count; i++)
            {
                if (els[i] == null) continue;
                try
                {
                    bool same = slot >= 0 ? els[i].GetElementIndex() == slot : els[i].GetSquaddieId() == sq;
                    if (same) { match = els[i]; break; }
                }
                catch { }
            }
            if (match == null) continue;                    // already dead here; edie settles it
            if (match.GetHitpoints() == h) continue;

            if (name != null)
                Plugin.Logger.LogInfo($"[DESYNC]   HP drift '{name}' at ({x},{y}) sq{sq}{(slot >= 0 ? $" slot{slot}" : "")}: mine {match.GetHitpoints()} -> host {h}");
            match.SetHitpoints(h);
            if (h == 0) { try { match.Die(); } catch { } }   // host says this one is gone
            fixes++;
        }

        // A SQUADDIE MISSING FROM A SAME-ROUND PAYLOAD IS ONE THE HOST HAS KILLED.
        //
        // The host serialises only its LIVING elements, so a member it already
        // killed is simply absent -- matching by id finds nothing and skips it, and
        // nothing else ever tells us to kill it. That is why single elements stayed
        // stuck alive on unit after unit, delta after delta.
        //
        // Only on a fresh (same-round) payload, so a stale snapshot can never
        // reap someone who is legitimately still standing.
        if (freshEnough && hp.Length > 0 && hp[0].IndexOf(':') >= 0)
        {
            // Keyed on the permanent slot when present: with ids, a pirate squad's
            // members all share id 0, so a dead one was never noticed missing.
            int c0 = hp[0].IndexOf(':');
            bool bySlot = hp[0].IndexOf(':', c0 + 1) > 0;
            var hostIds = new System.Collections.Generic.HashSet<int>();
            foreach (var part in hp)
            {
                int c = part.IndexOf(':');
                if (c <= 0) continue;
                int c2 = part.IndexOf(':', c + 1);
                string keyText = bySlot ? (c2 > 0 ? part.Substring(c2 + 1) : null) : part.Substring(0, c);
                if (keyText != null && int.TryParse(keyText, out int id)) hostIds.Add(id);
            }
            // COPY FIRST: GetElements() is the LIVING list and shrinks as each one
            // dies, so killing while indexing it skipped every member after a kill
            // (hand test 2026-10-04 rejoin: outcasts 8 -> 6 -> 5 -> 4 over three
            // passes, dead pirates standing on the rejoined screen for 13 s).
            var living = new System.Collections.Generic.List<Element>();
            for (int i = 0; i < els.Count; i++) if (els[i] != null) living.Add(els[i]);
            foreach (var el in living)
            {
                int sq = -1;
                try { sq = bySlot ? el.GetElementIndex() : el.GetSquaddieId(); } catch { continue; }
                if (sq < 0 || hostIds.Contains(sq)) continue;
                if (name != null)
                    Plugin.Logger.LogWarning($"[DESYNC]   '{name}' {(bySlot ? "slot" : "sq")}{sq} is alive here but gone on the host -- settling it");
                try { el.SetHitpoints(0); el.Die(); } catch { }
                fixes++;
            }
        }
        // RECOMPUTE THE UNIT TOTAL, AND COUNT IT WHEN IT WAS STALE.
        //
        // The state hash reads Actor.GetHitpoints() -- the unit total -- while
        // everything above compares ELEMENTS. A total left stale by a bare element
        // write therefore hashed differently with every element already agreeing:
        // five "state hash mismatch" requests in one session, each answered with
        // "0 HP/AP ... the last correction changed nothing", while the player
        // watched two different health bars.
        try
        {
            int before = actor.GetHitpoints();
            actor.UpdateHitpoints();
            int after = actor.GetHitpoints();
            if (after != before)
            {
                if (name != null)
                    Plugin.Logger.LogInfo($"[DESYNC]   UNIT HP total stale '{name}' at ({x},{y}): {before} -> {after} (elements already agreed)");
                fixes++;
            }
        }
        catch { }
        return fixes;
    }

    // GUEST: verify the host's hash against our board RIGHT NOW. Only compare
    // when we are at the same round+faction and nothing is in flight -- a hash
    // taken mid-animation or with orders pending says nothing useful.
    internal static void HandleRemote(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest || string.IsNullOrEmpty(m.Data)) return;
        var tm = TacticalManager.Get();
        if (tm == null) return;
        string myKey;
        try { myKey = $"{tm.GetRound()}:{tm.GetActiveFactionID()}"; } catch { return; }
        if (myKey != m.Data) return;                       // different moment entirely
        if (!Replication.BoardSettled() || Replication.DeferredCount > 0) return;
        if (UnityEngine.Time.realtimeSinceStartup - Replication.LastOrderSentAt < 1f) return;

        int mine = ComputeStateHash(tm);
        if (mine == m.Seed)
        {
            // Provably identical -- tell the host so it can checkpoint.
            CoopNet.Send(new CoopMsg { T = "hashok", Data = m.Data });
            return;
        }
        if (m.Data == _lastWarnedKey) return;
        _lastWarnedKey = m.Data;
        // PROBE: the hash says THAT something differs, never WHAT. Send the rows
        // it was built from; the host diffs them against its own board and logs
        // the actor(s) behind it. (Recurring +100 AP at every odd round's enemy
        // phase, 2026-09-25, with the snapshot finding "0 HP/AP" to correct.)
        if (BugProbes.On) CoopNet.Send(new CoopMsg { T = "hashrows", Data = m.Data + "#" + string.Join(";", HashRows(tm)) });
        // NOT a banner-worthy event on its own. Hashes are compared at every
        // faction switch -- which, with alternating activations, happens
        // several times per round -- and a single hitpoint of drift trips it
        // just as loudly as a broken board. Announcing "DESYNC DETECTED"
        // mid-turn for something that heals itself in the same second trains
        // players to ignore the warning that matters. Log it, request the fix,
        // and let the RESULT decide whether to speak up (see ApplySnapshot,
        // which banners only for real corrections).
        Plugin.Logger.LogWarning($"[DESYNC] State hash differs at turn {m.Data}: mine {mine}, host {m.Seed} -- requesting correction");
        CoopNet.Status = "Correcting a small state difference from the host...";
        RequestResync($"state hash mismatch at {m.Data}");
    }

    // HOST: the guest confirmed our boards match at this moment -- the only
    // point at which a checkpoint is provably worth keeping.
    internal static void HandleHashOk(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Host) return;
        var tm = TacticalManager.Get();
        if (tm == null) return;
        if (UnityEngine.Time.realtimeSinceStartup - _checkpointAt < 3f) return;
        // v5: the boards are provably identical only when EVERY client matched
        // the same moment -- one client's "ok" says nothing about the others.
        string key = m.Data ?? "?";
        if (key != _okKey) { _okKey = key; _okSeats.Clear(); }
        _okSeats.Add(m.From >= 0 ? m.From : Peers.LinkSeat);
        foreach (var seat in HostLinks.Seats()) if (!_okSeats.Contains(seat)) return;
        CaptureCheckpoint(tm, key);
    }
    private static string _okKey;
    private static readonly System.Collections.Generic.HashSet<int> _okSeats = new();

    // ---- Verified-good checkpoints ----------------------------------------
    // The guest verifies the host's hash live and answers; when it confirms a
    // match the boards are provably identical -- the only moment a snapshot is
    // certainly worth keeping. The host holds the newest such
    // snapshot in memory and mirrors it to disk, so a battle can be restored
    // even after a crash or a quit (the base game cannot save mid-mission at
    // all -- see the changelog note on the save format).
    private static string _checkpoint;
    private static string _checkpointKey;
    private static float _checkpointAt = -1f;

    // A MANUAL battle save sits alongside the automatic checkpoint. Restore
    // prefers the manual one -- if you deliberately saved before a risky push,
    // a background checkpoint captured since must not silently replace it.
    private static string _manualSave;
    private static string _manualSaveKey;

    internal static bool HasCheckpoint => _manualSave != null || _checkpoint != null;
    internal static string CheckpointLabel =>
        _manualSave != null ? $"saved {_manualSaveKey}" :
        _checkpoint != null ? $"auto {_checkpointKey}" : "none";

    // HOST-ONLY: an explicit mid-battle save. The game cannot do this at all --
    // its own save format carries no tactical state (verified: not one tile,
    // actor, hitpoint or action-point value appears in a .save file) -- so this
    // reads the live board instead and writes it beside the game's saves.
    internal static void SaveBattleState()
    {
        if (CoopNet.Mode != CoopMode.Host) { CoopNet.Status = "Only the host saves the battle"; return; }
        var tm = TacticalManager.Get();
        if (tm == null) { CoopNet.Status = "Not in a battle -- nothing to save"; return; }
        try
        {
            _manualSave = BuildSnapshot(tm);
            _manualSaveKey = $"{tm.GetRound()}:{tm.GetActiveFactionID()}";
            try { System.IO.File.WriteAllText(BattleSavePath(), _manualSaveKey + "\n" + _manualSave); } catch { }
            Plugin.Logger.LogWarning($"[DESYNC] Battle state SAVED at turn {_manualSaveKey}");
            CoopNet.Status = $"Battle state saved (turn {_manualSaveKey})";
            CoopRunner.ShowDesyncWarning($"BATTLE STATE SAVED (turn {_manualSaveKey})");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[DESYNC] Battle save failed: {e.Message}");
            CoopNet.Status = "Battle save failed (see log)";
        }
    }

    // ---- Automatic per-round battle save ----------------------------------
    // A save is only usable inside the round it was taken in (the engine's turn
    // order cannot be rewound -- see ForceGuestToHostState). So the host takes a
    // fresh one at the START of every round and discards the previous round's,
    // which is now dead weight. Net effect: there is always exactly one save,
    // it always belongs to the current round, and RESTORE is therefore always
    // valid rather than sometimes refusing.
    private static int _autoSaveRound = -1;

    private static void AutoSaveOnNewRound(TacticalManager tm)
    {
        int round;
        try { round = tm.GetRound(); } catch { return; }
        if (round <= 0 || round == _autoSaveRound) return;
        // Wait for a settled board so the save is a clean picture of the round.
        if (!Replication.BoardSettled() || Replication.SyncPauseActive) return;
        // ROUND 1 is covered by this same path (the counter starts at -1, so the
        // first battle tick is already a "new round"), but at battle entry the
        // guest may still be loading and the board still initialising. Holding
        // until the entry hold-fire releases means round 1's save is taken with
        // both players actually in the battle, which is the state a restore
        // would have to put them back into.
        if (SessionSync.HostHoldFire || SessionSync.GuestEnteringBattle) return;
        _autoSaveRound = round;
        try
        {
            // Last round's saves cannot be restored any more -- drop them so a
            // stale entry can never be offered or mistakenly relied upon.
            if (_manualSave != null)
            {
                _manualSave = null; _manualSaveKey = null;
                try { System.IO.File.Delete(BattleSavePath()); } catch { }
            }
            _checkpoint = BuildSnapshot(tm);
            _checkpointKey = $"round {round} start";
            _checkpointAt = UnityEngine.Time.realtimeSinceStartup;
            try { System.IO.File.WriteAllText(CheckpointPath(), _checkpointKey + "\n" + _checkpoint); } catch { }
            Plugin.Logger.LogInfo($"[DESYNC] Auto-saved battle state at the start of round {round} (previous round's save discarded)");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[DESYNC] Round auto-save failed: {e.Message}"); }
    }

    // Header is "F:<phase>:<round>:<faction>;" -- pull the round out of it.
    private static int FactionOfSnapshot(string snap)
    {
        try
        {
            if (snap == null || !snap.StartsWith("F:")) return -1;
            int end = snap.IndexOf(';');
            var head = (end > 2 ? snap.Substring(2, end - 2) : snap.Substring(2)).Split(':');
            return head.Length > 2 && int.TryParse(head[2], out int f) ? f : -1;
        }
        catch { return -1; }
    }

    private static int RoundOfSnapshot(string snap)
    {
        try
        {
            if (snap == null || !snap.StartsWith("F:")) return 0;
            int end = snap.IndexOf(';');
            var head = (end > 2 ? snap.Substring(2, end - 2) : snap.Substring(2)).Split(':');
            return head.Length > 1 && int.TryParse(head[1], out int r) ? r : 0;
        }
        catch { return 0; }
    }

    internal static string BattleSavePath()
    {
        try { return SaveTransfer.PathFor("coop_battle_save.txt"); }
        catch { return "coop_battle_save.txt"; }
    }

    private static void CaptureCheckpoint(TacticalManager tm, string key)
    {
        if (CoopNet.Mode != CoopMode.Host) return;
        try
        {
            _checkpoint = BuildSnapshot(tm);
            _checkpointKey = key;
            _checkpointAt = UnityEngine.Time.realtimeSinceStartup;
            try { System.IO.File.WriteAllText(CheckpointPath(), key + "\n" + _checkpoint); } catch { }
            Plugin.Logger.LogInfo($"[DESYNC] Checkpoint captured at verified-good turn {key}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[DESYNC] Checkpoint capture failed: {e.Message}"); }
    }

    internal static string CheckpointPath()
    {
        try { return SaveTransfer.PathFor("coop_battle_checkpoint.txt"); }
        catch { return "coop_battle_checkpoint.txt"; }
    }

    // HOST-ONLY: push authoritative state to the guest and force it there.
    // Sends the CURRENT board by default; pass useCheckpoint to send the last
    // verified-good one instead.
    internal static void ForceGuestToHostState(bool useCheckpoint)
    {
        if (CoopNet.Mode != CoopMode.Host) { CoopNet.Status = "Only the host can force a resync"; return; }
        if (!CoopNet.Connected) { CoopNet.Status = "Not connected"; return; }
        var tm = TacticalManager.Get();
        if (tm == null) { CoopNet.Status = "Not in a battle"; return; }
        // Restore prefers YOUR save over the automatic checkpoint.
        string best = _manualSave ?? _checkpoint;

        // CROSS-ROUND RESTORES ARE REFUSED. A snapshot holds UNIT state only --
        // positions, HP, AP, turn-done. The engine's own round and turn-order
        // bookkeeping is not in it and cannot be rewound: the round counter is
        // load-bearing and writing it crashes the turn-order panel (learned the
        // hard way in 1.4.19). Restoring round 1 state into a round 2 engine
        // therefore hands out round-1 AP and done-flags to a turn order that has
        // already moved on -- which is exactly the "turns and AP not being
        // reset, broke instantly" failure. Within a round it is sound, which is
        // why same-turn restores work perfectly.
        if (useCheckpoint && best != null)
        {
            int snapRound = RoundOfSnapshot(best);
            int nowRound = 0;
            try { nowRound = tm.GetRound(); } catch { }
            if (snapRound > 0 && nowRound > 0 && snapRound != nowRound)
            {
                string msg = $"CANNOT RESTORE ACROSS ROUNDS -- that save is from round {snapRound}, you are on round {nowRound}";
                Plugin.Logger.LogWarning($"[DESYNC] {msg}");
                CoopNet.Status = msg;
                CoopRunner.ShowDesyncWarning(msg + ". Save again this round, or use FIX DESYNC.");
                return;
            }
        }

        bool restoring = useCheckpoint && best != null;
        if (useCheckpoint && best == null)
        {
            CoopNet.Status = "Nothing saved yet -- pushing current state instead";
            CoopRunner.ShowDesyncWarning("No battle state saved yet -- pushed current state instead");
        }
        string payload = restoring ? best : BuildSnapshot(tm);
        string what = restoring ? $"battle state ({CheckpointLabel})" : "current state";
        // Say what is about to happen, so a restore that legitimately changes
        // little is still visibly acknowledged rather than looking like a
        // button that did nothing.
        CoopRunner.ShowDesyncWarning(restoring
            ? $"RESTORING {CheckpointLabel} -- rewinding both boards..."
            : "PUSHING HOST BOARD TO GUEST...");
        StartSyncPause($"host forcing {what}");
        CoopNet.Send(new CoopMsg { T = "hardsync", Data = payload });
        // A checkpoint restore REWINDS: the host's own board must go back too,
        // including undoing deaths that happened after the checkpoint.
        if (restoring) ApplySnapshot(payload, hard: true, localAllowed: true);
        Plugin.Logger.LogWarning($"[DESYNC] Host forced guest to {what}{(restoring ? " (and rewound its own board)" : "")}");
    }

    // THE HASH MUST ONLY COVER THINGS A DIFFERENCE IN WHICH ACTUALLY MATTERS.
    //
    // It used to include GetActionPoints() for EVERY actor, AI included. AI action
    // points drift transiently on the guest while relayed orders are still being
    // applied -- the long-known ~19 AI-AP corrections per mission -- and that drift
    // cannot affect play at all, because the guest never makes AI decisions; it
    // replays the host's. So the hash reported "desync" over a value with no
    // gameplay meaning, several times per round (it is compared at every faction
    // switch, and alternating activations make that frequent).
    //
    // That was not merely noisy. Each mismatch requests a correction, and repeated
    // "failures" escalate to a SYNC PAUSE, which freezes BOTH machines -- so a
    // difference that changed nothing on screen produced a real stall. Exactly what
    // the user reported: "it said it was desynced, visually and in game it showed no
    // real impact", followed by the game stalling.
    //
    // Kept: position and HP for everything (a genuine divergence), and AP for
    // PLAYER units only, where it is authoritative and does change what a player
    // can do. Dropped: AI action points.
    private static List<string> HashRows(TacticalManager tm)
    {
        var rows = new List<string>();
        foreach (var a in AllActors(tm))
        {
            try
            {
                if (a == null || a.IsDying() || !a.IsAlive()) continue;
                var p = a.GetLastTile()?.GetTilePos() ?? default;
                string ap = RosterIndexOf(a) >= 0 && !a.IsTurnDone() ? a.GetActionPoints().ToString() : "-";
                rows.Add($"{TemplateNameOf(a)}@{p.x},{p.y} hp={a.GetHitpoints()} ap={ap}");
            }
            catch { }
        }
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    internal static void HandleHashRows(CoopMsg m)
    {
        try
        {
            var tm = TacticalManager.Get();
            if (tm == null || string.IsNullOrEmpty(m.Data)) return;
            int h = m.Data.IndexOf('#');
            string key = h < 0 ? "?" : m.Data.Substring(0, h);
            var theirs = new List<string>(h < 0 ? new string[0] : m.Data.Substring(h + 1).Split(';'));
            var mine = HashRows(tm);
            var onlyThem = new List<string>(theirs); foreach (var r in mine) onlyThem.Remove(r);
            var onlyMe = new List<string>(mine); foreach (var r in theirs) onlyMe.Remove(r);
            Plugin.Logger.LogWarning($"[PROBE:H] HASH-ROWS for {key} (partner's rows vs this board now): {onlyThem.Count} only there, {onlyMe.Count} only here");
            foreach (var r in onlyThem) Plugin.Logger.LogWarning("[PROBE:H]   partner: " + r);
            foreach (var r in onlyMe) Plugin.Logger.LogWarning("[PROBE:H]   here:    " + r);
        }
        catch { }
    }

    private static readonly System.Collections.Generic.HashSet<string> _rosterDumped = new();

    private static int ComputeStateHash(TacticalManager tm)
    {
        var actors = AllActors(tm);
        unchecked
        {
            int sum = 0;
            for (int i = 0; i < actors.Length; i++)
            {
                var a = actors[i];
                // Not alive counts as gone too: a dead actor whose dying flag
                // never got set (r146 flee zombie) is not a unit on the board.
                if (a == null || a.IsDying() || !a.IsAlive()) continue;
                var p = a.GetLastTile()?.GetTilePos() ?? default;
                int h = 17;
                h = h * 31 + p.x;
                h = h * 31 + p.y;
                h = h * 31 + a.GetHitpoints();
                // AP ONLY WHILE IT CAN STILL BE SPENT. A finished unit's leftover
                // AP is dead weight until the round refills it, and the two
                // machines disagree about it harmlessly: the host's engine zeroes
                // it on turn end, the relayed turn-done does not. Probed
                // 2026-09-25: every odd round, player_squad.lim host ap=0 vs guest
                // ap=100 -> a pause-and-resync that corrected nothing.
                if (RosterIndexOf(a) >= 0 && !a.IsTurnDone()) h = h * 31 + a.GetActionPoints();
                sum += h;
            }
            return sum;
        }
    }
}








