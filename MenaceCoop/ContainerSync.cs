using System;
using HarmonyLib;
using Il2CppMenace.Tactical;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling

namespace MenaceCoop;

// VEHICLES AND OTHER CONTAINERS.
//
// The engine models a vehicle as a CONTAINER, the same way it models a building:
// EntityContainerType is { None, Building, Vehicle, Forest, Tower }, and an
// Entity can hold another Entity inside it. A pirate reinforcement wave arrives
// as mounted squads -- 'enemy.pirate_scavengers_on_heavy_truck' and friends --
// and unloading them materialises the contained entity.
//
// The mod handled exactly one half of this. Entering a container is stamped onto
// move messages (Replication.StampContainer, on both MoveTo and MoveToWithSkill,
// so the AI is covered). LEAVING one was replicated nowhere.
//
// That asymmetry is fatal specifically because of how AI works here: the guest
// does not run AI AT ALL (AiPatches skips OnTurnStart, Process and Think -- the
// host runs it and relays the results). So when the host's AI decides to unload a
// transport, the guest has no mechanism to reach the same decision on its own.
// It never unloads. That is the reported "the other half arrives but does not
// unload reinforcements, unlike for the host" -- the guest's truck is not stuck,
// it was simply never told.
internal static class ContainerSync
{
    // Guard so the guest's replay of an unload does not bounce back to the host.
    internal static bool ApplyingUnload;

    // WITHDRAWN -- UNREFERENCED. Unloading already replicates on its own.
    //
    // This relay was built on the assumption that a transport dropping its
    // passengers was an AI decision the guest could never reach, because the
    // guest runs no AI. That assumption was wrong: the unload IS a skill --
    // 'special.vehicle_eject_passengers_free' -- and skills already replicate
    // through the ordinary order path. The harness logs settle it outright:
    //
    //   host   Skill.Use(special.vehicle_eject_passengers_free ...) remote=False
    //   guest  Skill.Use(special.vehicle_eject_passengers_free ...) remote=True
    //
    // The guest was replaying every eject correctly the whole time. This relay
    // was a SECOND trigger for an event that already had one, and because the
    // mounted squad is a [TransientActor] with no coop id it could never resolve
    // anyway -- 20 relays sent, 0 applied, 14 "target not found" warnings a
    // battle, all of it noise on top of a mechanism that was already working.
    //
    // Deleted rather than repaired. The rest of this file earns its place: the
    // container-type test that IsInsideTower got wrong, the occupancy census,
    // and the identity work that makes contained units addressable at all.
    //
    // Two findings from the attempt are worth keeping even though the code is
    // not. Entity.ToActor() THROWS for a non-actor rather than returning null,
    // so every civilian leaving a building raised "Unable to cast Structure to
    // Actor" -- 93 a side. And MayBroadcastFor answers true for any AI unit on
    // BOTH machines, which is correct for moves (the guest never runs the AI
    // that would produce one) but wrong for anything fired off local simulation:
    // without a role check the guest relayed its own structure unloads straight
    // back at the host.
    internal static void BroadcastUnload(Entity container, FactionType faction)
    {
        if (ApplyingUnload) return;
        if (container == null) return;
        // A HOST ALONE STILL RECORDS IT (#57). Nobody to send to, but a player who
        // joins later rebuilds from the battle start, where the crew is still
        // aboard: without the journaled unload the replayed crew death happened
        // INSIDE the truck (no bodies) and the truck, still "loaded", was never
        // placed (hand test 2026-10-04 10:55).
        bool hostAlone = CoopNet.Mode == CoopMode.Host && !CoopNet.Connected;
        if (!hostAlone && !Replication.ShouldBroadcastOrder()) return;
        try
        {
            // Only actors (vehicles/transports) carry a decision worth relaying.
            bool isActor = false;
            try { isActor = container.IsActor(); } catch { }
            if (!isActor) return;

            var asActor = container.ToActor();
            if (asActor == null) return;

            // ONE DIRECTION ONLY. MayBroadcastFor answers "true" for any AI unit
            // on BOTH machines -- correct for moves, because the guest never runs
            // the AI that would produce one, but not here: this fires off local
            // simulation, so without a role check the guest relays right back.
            if (!Ownership.IsKnownPlayerUnit(asActor) && CoopNet.Mode != CoopMode.Host) return;
            if (!hostAlone && !Replication.MayBroadcastFor(asActor)) return;

            var msg = new CoopMsg { T = "unload", Prm = (int)faction };
            Replication.StampIds(msg, asActor);
            // Carry the template so the receiver has something to match on when
            // id addressing fails (see NearestLoadedTransport).
            msg.SkillId = DesyncDetector.TemplateNameOf(asActor);

            var tile = asActor.GetLastTile() ?? container.GetTile();
            if (tile != null) { var p = tile.GetTilePos(); msg.AX = p.x; msg.AY = p.y; }
            else { msg.AX = -1; msg.AY = -1; }

            CoopNet.Send(msg);
            Plugin.Logger.LogInfo($"[CONTAINER] Relayed unload of '{DesyncDetector.TemplateNameOf(asActor)}' "
                + $"at ({msg.AX},{msg.AY}) rid={msg.RId} cid={msg.CId} faction={faction}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[CONTAINER] Unload broadcast failed: {e.Message}"); }
    }

    // GUEST: perform the unload the host just performed.
    internal static void ApplyUnload(CoopMsg m, Actor resolved)
    {
        try
        {
            Entity ent = resolved;
            if (ent == null && m.AX >= 0 && m.AY >= 0)
            {
                // The tile's ACTOR first: a transport is an actor, and a tile's
                // Entity is the structure standing on it, which is a different
                // thing entirely. Only checking GetEntity() was why the tile
                // fallback never rescued a single one of these.
                var t = Replication.GetTileAt(m.AX, m.AY);
                Entity byTile = null;
                try { byTile = t?.GetActor(); } catch { }
                if (byTile == null) { try { byTile = t?.GetEntity(); } catch { } }
                ent = byTile;
            }
            // LAST RESORT: THE NEAREST LOADED TRANSPORT OF THE SAME KIND.
            //
            // Measured: 20 of 24 relays arrived with rid=-1 cid=-1 and an empty
            // tile, while the guest's own census showed it was holding two or
            // three loaded transports the whole time. The vehicles existed; only
            // the addressing failed. Refusing to act on that is how a battle ends
            // up with the host's transports empty and the guest's still full.
            //
            // Matching on template and proximity is weaker than an id, so it is
            // only reached once both of those have failed -- and it is bounded to
            // a loaded transport of the SAME template, which on a normal board is
            // unambiguous. Logged as a fallback so it never looks like a clean
            // resolve.
            if (ent == null && !string.IsNullOrEmpty(m.SkillId))
            {
                ent = NearestLoadedTransport(m.SkillId, m.AX, m.AY);
                if (ent != null)
                    Plugin.Logger.LogWarning($"[CONTAINER] Unload matched by FALLBACK (template '{m.SkillId}' "
                        + $"nearest to ({m.AX},{m.AY})) -- id addressing failed");
            }

            if (ent == null)
            {
                // NAME THE REASON. "Not found" alone left no way to tell an id
                // mismatch from a unit that had simply moved, and the guest
                // failed every single relay in the first live run with nothing
                // in the log to distinguish the two.
                Plugin.Logger.LogWarning($"[CONTAINER] Unload target not found at ({m.AX},{m.AY}) "
                    + $"rid={m.RId} cid={m.CId} (id lookup {(m.RId >= 0 || m.CId >= 0 ? "MISSED" : "not supplied")}, "
                    + "tile empty) -- transport will stay loaded");
                return;
            }
            if (!ent.IsContainingAnotherEntity())
            {
                // Already unloaded (a duplicate relay, or the guest got there via
                // a replayed skill). Not an error -- just nothing left to do.
                return;
            }
            ApplyingUnload = true;
            Replication.ApplyingRemote = true;
            try { ent.SpawnContainedEntity((FactionType)m.Prm); }
            finally { Replication.ApplyingRemote = false; ApplyingUnload = false; }
            Plugin.Logger.LogInfo($"[CONTAINER] Unloaded transport at ({m.AX},{m.AY})");
        }
        catch (Exception e)
        {
            ApplyingUnload = false;
            Plugin.Logger.LogWarning($"[CONTAINER] Unload apply failed: {e.Message}");
        }
    }

    // TRANSPORT CENSUS -- so that SILENCE IS READABLE.
    //
    // Everything above rests on one unproven inference: that
    // Entity.SpawnContainedEntity is how a transport unloads. It is the only
    // plausible candidate in the API, but it was never confirmed against a
    // running game. If it is the wrong hook, the host relays nothing, the guest
    // applies nothing, and BOTH logs stay silent -- which looks exactly like
    // "no transport has unloaded yet". Two earlier runs on this project were
    // lost to watching for a string that was never going to print.
    //
    // So count the loaded transports on the board instead, and report the count
    // whenever it CHANGES. That makes the diagnosis unambiguous:
    //   loaded>0 then dropping to 0, with no [CONTAINER] relay  -> wrong hook
    //   loaded>0 and never dropping                             -> nothing unloaded
    //   counts differing between host and guest                 -> wave diverged
    private static float _nextCensus;
    private static int _lastLoaded = -1;
    private static readonly System.Collections.Generic.Dictionary<string, int> _lastCover = new();

    // Riders the engine ejected onto a substitute tile, and the tile the partner
    // actually used. Settled by SettlePlacements once the tile frees up.
    private static readonly System.Collections.Generic.List<(Actor who, Tile want, float until)> _pendingPlacements = new();

    internal static void ClearPendingPlacements() => _pendingPlacements.Clear();

    // Queued from ContainerPatches (a different class), so this is the door in.
    internal static void OwePlacement(Actor who, Tile want)
    {
        if (who == null || want == null) return;
        for (int i = 0; i < _pendingPlacements.Count; i++)
            if (_pendingPlacements[i].who != null && _pendingPlacements[i].who.Pointer == who.Pointer)
            { _pendingPlacements[i] = (who, want, UnityEngine.Time.realtimeSinceStartup + 180f); return; }
        _pendingPlacements.Add((who, want, UnityEngine.Time.realtimeSinceStartup + 180f));
    }

    // RETRY THE OWED PLACEMENTS.
    //
    // A redirected eject leaves the rider one or two tiles from where the
    // partner put it, because the partner's tile is covered by the transport
    // here. Nothing else in the mod will fix that: the relayed move is refused
    // for the same reason, and the position corrector skips a destination that
    // has an actor on it ("leave it to the healer"). So this is the healer.
    //
    // Runs off the census tick, so it is a handful of cheap checks every two
    // seconds and only while something is actually owed.
    private static void SettlePlacements()
    {
        if (_pendingPlacements.Count == 0) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        for (int i = _pendingPlacements.Count - 1; i >= 0; i--)
        {
            var (who, want, until) = _pendingPlacements[i];
            try
            {
                if (who == null || want == null) { _pendingPlacements.RemoveAt(i); continue; }
                var wp = want.GetTilePos();
                var at = who.GetLastTile()?.GetTilePos();
                if (at != null && at.Value.x == wp.x && at.Value.y == wp.y)
                {
                    _pendingPlacements.RemoveAt(i);
                    Plugin.Logger.LogInfo($"[CONTAINER] '{DesyncDetector.TemplateNameOf(who)}' is now on ({wp.x},{wp.y}) as the partner has it");
                    continue;
                }
                if (now > until)
                {
                    _pendingPlacements.RemoveAt(i);
                    Plugin.Logger.LogWarning(
                        $"[CONTAINER] gave up moving '{DesyncDetector.TemplateNameOf(who)}' to ({wp.x},{wp.y}) -- "
                        + $"it is at {(at == null ? "(nowhere)" : $"({at.Value.x},{at.Value.y})")}. Positions differ for this unit.");
                    continue;
                }
                // Dead or gone: nothing to settle.
                bool dying = false;
                try { dying = who.IsDying(); } catch { }
                if (dying) { _pendingPlacements.RemoveAt(i); continue; }
                // Only try while the tile is actually free, so this never fights
                // whatever is standing there.
                Actor occ = null;
                try { occ = want.GetActor(); } catch { }
                if (occ != null && occ.Pointer != who.Pointer) continue;
                if (who.IsMoving()) continue;

                MovementAction act = default;
                bool prev = Replication.ApplyingRemote;
                Replication.ApplyingRemote = true;
                try { who.MoveTo(want, ref act, (MovementFlags)(1 | 2 | 4 | 8)); }
                finally { Replication.ApplyingRemote = prev; }
            }
            catch { _pendingPlacements.RemoveAt(i); }
        }
    }

    internal static void Tick()
    {
        if (!CoopNet.Connected) return;
        var tm = TacticalManager.Get();
        if (tm == null) { _lastLoaded = -1; return; }
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now < _nextCensus) return;
        _nextCensus = now + 2f;

        SettlePlacements();

        try
        {
            int loaded = 0, contained = 0;
            var all = DesyncDetector.AllActors(tm);
            for (int i = 0; i < all.Length; i++)
            {
                var a = all[i];
                if (a == null) continue;
                try { if (a.IsContainingAnotherEntity()) loaded++; } catch { }
                try { if (a.IsContainedWithinAnotherEntity()) contained++; } catch { }
            }
            if (loaded != _lastLoaded)
            {
                _lastLoaded = loaded;
                // ROUND IS THE POINT OF THIS LINE, not the count. The two
                // machines' enemy strength splits at round 3 every run, and
                // strength is what the reinforcement draw budgets from -- so if
                // transports unload a round apart, that alone explains the split
                // without any damage divergence. Comparing the two logs on round
                // answers it; comparing counts alone never could.
                int round = -1;
                try { round = tm.GetRound(); } catch { }
                Plugin.Logger.LogInfo($"[CONTAINER] Census r{round}: {loaded} loaded transport(s), "
                    + $"{contained} passenger(s) aboard  ({(CoopNet.Mode == CoopMode.Host ? "H" : "G")})");
            }

            // WHEN DOES THE FOOTPRINT DIVERGE?
            //
            // Measured at the first eject: the guest registers the leviathan
            // across a 3x3 of tiles (all reporting a=LEV,e=LEV) while the host
            // registers exactly one, same anchor tile and same facing=S. That is
            // a multi-tile REGISTRATION difference, and tile occupancy feeds
            // movement, line of sight and pathing -- not just ejects.
            //
            // Whether the two start equal and one changes, or they differ from
            // the first frame, decides where to look: a placement path, or a
            // divergence introduced later by something we replay. Report the
            // covered-tile count for every loaded transport, but only when it
            // CHANGES, so this is a handful of lines a battle and not a flood.
            for (int i = 0; i < all.Length; i++)
            {
                var a = all[i];
                if (a == null) continue;
                bool holds = false;
                try { holds = a.IsContainingAnotherEntity(); } catch { }
                if (!holds) continue;
                var at = a.GetLastTile()?.GetTilePos();
                if (at == null) continue;
                int cover = 0;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        var t = Replication.GetTileAt(at.Value.x + dx, at.Value.y + dy);
                        if (t == null) continue;
                        Actor occ = null;
                        try { occ = t.GetActor(); } catch { }
                        if (occ != null && occ.Pointer == a.Pointer) cover++;
                    }
                string key = DesyncDetector.TemplateNameOf(a);
                _lastCover.TryGetValue(key, out int prev);
                if (prev == cover) continue;
                _lastCover[key] = cover;
                int r2 = -1;
                try { r2 = tm.GetRound(); } catch { }
                Plugin.Logger.LogInfo(
                    $"[CONTAINER] Footprint r{r2} '{key}'@({at.Value.x},{at.Value.y}) covers {cover} tile(s)"
                    + $"  ({(CoopNet.Mode == CoopMode.Host ? "H" : "G")})");
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[CONTAINER] Census failed: {e.Message}"); }
    }

    private static Entity NearestLoadedTransport(string template, int x, int y)
    {
        try
        {
            var tm = TacticalManager.Get();
            if (tm == null) return null;
            var all = DesyncDetector.AllActors(tm);
            Actor best = null; int bestD = int.MaxValue;
            for (int i = 0; i < all.Length; i++)
            {
                var a = all[i];
                if (a == null) continue;
                try
                {
                    if (!a.IsContainingAnotherEntity()) continue;
                    if (DesyncDetector.TemplateNameOf(a) != template) continue;
                    var t = a.GetLastTile();
                    int d = 0;
                    if (t != null)
                    {
                        var p = t.GetTilePos();
                        d = System.Math.Abs(p.x - x) + System.Math.Abs(p.y - y);
                    }
                    if (d < bestD) { bestD = d; best = a; }
                }
                catch { }
            }
            return best;
        }
        catch { return null; }
    }

    // IS THIS UNIT INSIDE SOMETHING?
    //
    // The mod asked Actor.IsInsideTower(), which is one container type out of
    // five. A squad riding a truck is EntityContainerType.Vehicle, and there is
    // no reason to expect a method named for towers to answer for it. Two places
    // depended on the answer and both fail quietly when it is wrong: the desync
    // healer would try to reposition a passenger that has no tile to stand on,
    // and the roster check would drop passengers off the manifest entirely.
    //
    // Entity.IsContainedWithinAnotherEntity() is the general question. Keep the
    // tower test as a fallback so a throwing interop call cannot make this
    // report "not contained" for something that plainly is.
    internal static bool IsContained(Actor a)
    {
        if (a == null) return false;
        try { if (a.IsContainedWithinAnotherEntity()) return true; } catch { }
        try { return a.IsInsideTower(); } catch { return false; }
    }

    // WHAT SHAPE IS "8 PASSENGERS ABOARD"?
    //
    // Entity exposes m_ContainedEntity / GetContainedEntity() -- SINGULAR -- yet
    // a leviathan carries eight riders, so the eight are held some way this mod
    // has never looked at: a chain (each passenger containing the next), a list
    // behind a singular accessor, or eight entities each pointing UP at the same
    // container with no downward list at all. Which one it is decides the fix,
    // because SpawnContainedEntity(FactionType) cannot be aimed at a passenger.
    //
    // Dumped once per battle, not per eject: eight of these would bury the
    // ordinary log, and one is enough to settle the shape.
    private static bool _containmentDumped;
    internal static void ResetContainmentDump() => _containmentDumped = false;

    private static void DumpContainment(Entity container, Actor passenger)
    {
        if (_containmentDumped) return;
        _containmentDumped = true;
        try
        {
            string cname = "?";
            try { cname = container.IsActor() ? DesyncDetector.TemplateNameOf(container.ToActor()) : "non-actor"; } catch { }
            bool holds = false;
            try { holds = container.IsContainingAnotherEntity(); } catch { }
            Plugin.Logger.LogInfo($"[CONTAINER] SHAPE: container '{cname}' IsContainingAnotherEntity={holds}");

            // Walk down from the container as far as the singular accessor goes.
            var cur = container;
            for (int depth = 0; depth < 12 && cur != null; depth++)
            {
                Entity next = null;
                try { next = cur.GetContainedEntity(); } catch { }
                if (next == null) { Plugin.Logger.LogInfo($"[CONTAINER] SHAPE:   depth {depth}: GetContainedEntity() = null (chain ends)"); break; }
                string nname = "?";
                try { nname = next.IsActor() ? DesyncDetector.TemplateNameOf(next.ToActor()) : "non-actor"; } catch { }
                Plugin.Logger.LogInfo($"[CONTAINER] SHAPE:   depth {depth}: contains '{nname}'");
                if (next.Pointer == cur.Pointer) break;   // self-reference guard
                cur = next;
            }

            // And up from the passenger the partner actually named.
            string pname = DesyncDetector.TemplateNameOf(passenger);
            string upname = "?";
            try
            {
                var up = passenger.GetContainerEntity();
                upname = up == null ? "null"
                       : (up.IsActor() ? DesyncDetector.TemplateNameOf(up.ToActor()) : "non-actor");
            }
            catch { }
            Plugin.Logger.LogInfo($"[CONTAINER] SHAPE: passenger '{pname}' GetContainerEntity() = '{upname}'");

            // How many actors on this board currently point at this container --
            // the count the fix has to be able to address individually.
            int pointingHere = 0;
            try
            {
                var tm = TacticalManager.Get();
                var actors = tm?.GetActorsRoot()?.GetComponentsInChildren<Actor>(true);
                if (actors != null)
                    for (int i = 0; i < actors.Length; i++)
                    {
                        if (actors[i] == null) continue;
                        Entity up = null;
                        try { up = actors[i].GetContainerEntity(); } catch { }
                        if (up != null && up.Pointer == container.Pointer) pointingHere++;
                    }
            }
            catch { }
            Plugin.Logger.LogInfo($"[CONTAINER] SHAPE: {pointingHere} actor(s) on the board name this container as theirs");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[CONTAINER] SHAPE dump failed: {e.Message}"); }
    }

    // THE EJECT THE GUEST CANNOT REPLAY.
    //
    // A passenger leaving its transport reaches the partner as an ordinary move
    // carrying MovementAction.Leave, and that move is REFUSED here: the engine
    // will not walk an actor out of a container on its own. The skill that would
    // have done it is refused too -- measured, not assumed:
    //
    //   Relayed skill ...eject_passengers_free REFUSED (IsUsable=False IsUsableOn=False)
    //   Applied remote skill ...eject_passengers_free, actor AP 120 -> 120
    //   [CONTAINER] relayed move act=Leave accepted=False 'riders_2' -> (35,11)
    //               now at (34,15) stillContained=True
    //
    // IsUsable is False because the guest runs no AI, so the transport is never
    // in the activation that permits its own eject skill. Waiting for a replay
    // that cannot happen left all 8 riders parked inside the leviathan for the
    // whole battle while the host fought them on the board -- 18 enemy positions
    // against 14, every relayed order to those riders missing.
    //
    // So perform the ejection directly with the engine's own primitive, then put
    // the passenger on the tile the partner actually used. SpawnContainedEntity
    // takes no tile -- it releases the NEXT passenger wherever the engine likes --
    // so the placement is a second step, and which passenger came out is logged
    // rather than assumed: both machines load the same 8 riders in the same order
    // and the host ejects them in that order, but that is an inference about the
    // engine's queue and the log is what will confirm or break it.
    // The reverse (run 37): the host has this unit aboard, ours is standing on the
    // board. Board it with the engine's own Enter move onto the container's tile;
    // a teleporting Enter as the fallback.
    internal static bool EnterLikeEngine(Actor passenger, Actor container)
        => EnterTileLikeEngine(passenger, container?.GetLastTile(), DesyncDetector.TemplateNameOf(container));

    // A STRUCTURE has no actor id (snapshot container ref -9), only the tile the
    // host's unit reports -- the building's. Same Enter move onto that tile.
    internal static bool EnterTileLikeEngine(Actor passenger, Tile t, string what)
    {
        if (passenger == null || t == null) return false;
        string who = DesyncDetector.TemplateNameOf(passenger);
        bool prev = Replication.ApplyingRemote;
        Replication.ApplyingRemote = true;
        try
        {
            MovementAction act = MovementAction.Enter;
            bool ok = false;
            try { ok = passenger.MoveTo(t, ref act, (MovementFlags)(1 | 4)); } catch { }
            if (!ok)
            {
                act = MovementAction.Enter | MovementAction.Teleport;
                try { ok = passenger.MoveTo(t, ref act, (MovementFlags)(1 | 2 | 4 | 8)); } catch { }
            }
            Plugin.Logger.LogInfo($"[CONTAINER] '{who}' boarding '{what}' like the engine: {(ok ? "accepted" : "REFUSED")}");
            return ok;
        }
        finally { Replication.ApplyingRemote = prev; }
    }

    // THE ENGINE'S OWN DISMOUNT: MoveTo with MovementAction.Leave and
    // Force|AllowMoveThroughActors, onto a tile beside the transport -- exactly the
    // call the host's AI makes (run 31: "action=Leave flags=Force,
    // AllowMoveThroughActors"). Leave|Teleport with every flag set is refused.
    // Tries the host's tile, then each free neighbour of the transport; the
    // restore's second pass then places the rider exactly. Asynchronous: the
    // rider is out a few frames later, not on return.
    internal static bool LeaveLikeEngine(Actor passenger, Tile want)
    {
        Entity container = null;
        try { container = passenger?.GetContainerEntity(); } catch { }
        if (container == null) return false;
        // A ONE-STEP EXIT, NOT A WALK. Leaving straight for the host's tile walks
        // there on this unit's AP and can stop short (run 39: stopped 8 tiles out,
        // then the placing teleport was refused). Step out beside the transport;
        // the restore's next pass places it. The host's tile first only when it
        // is itself beside the transport.
        var tiles = new System.Collections.Generic.List<Tile>();
        try
        {
            var ct = container.TryCast<Actor>()?.GetLastTile()?.GetTilePos();
            if (ct != null)
            {
                var wp = want?.GetTilePos();
                if (wp != null && Math.Abs(wp.Value.x - ct.Value.x) <= 1 && Math.Abs(wp.Value.y - ct.Value.y) <= 1) tiles.Add(want);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var t = Replication.GetTileAt(ct.Value.x + dx, ct.Value.y + dy);
                        if (Replication.TileFree(t)) tiles.Add(t);
                    }
            }
        }
        catch { }
        if (want != null && !tiles.Contains(want)) tiles.Add(want);   // last resort
        string who = DesyncDetector.TemplateNameOf(passenger);
        bool prev = Replication.ApplyingRemote;
        Replication.ApplyingRemote = true;
        try
        {
            foreach (var t in tiles)
            {
                MovementAction act = MovementAction.Leave;
                bool ok = false;
                try { ok = passenger.MoveTo(t, ref act, (MovementFlags)(1 | 4)); } catch { }
                if (!ok) continue;
                var p = t.GetTilePos();
                Plugin.Logger.LogInfo($"[CONTAINER] '{who}' dismounting like the engine -> ({p.x},{p.y})");
                return true;
            }
        }
        finally { Replication.ApplyingRemote = prev; }
        Plugin.Logger.LogWarning($"[CONTAINER] '{who}': the engine refused a Leave onto {tiles.Count} candidate tile(s)");
        return false;
    }

    // ReleaseLikeUnload (SpawnContainedEntity from a restore) was REMOVED
    // 2026-09-26: it spawns a NEW copy of the passenger instead of ejecting the
    // existing one (run 38: an extra enemy on the guest only). Do not bring it back.

    internal static bool ForceLeaveContainer(Actor passenger, Tile dest)
    {
        if (passenger == null || dest == null) return false;
        if (!IsContained(passenger)) return false;

        string who = DesyncDetector.TemplateNameOf(passenger);
        Entity container = null;
        try { container = passenger.GetContainerEntity(); } catch { }
        if (container == null)
        {
            Plugin.Logger.LogWarning($"[CONTAINER] '{who}' is contained but reports no container -- cannot eject it here");
            return false;
        }

        // ATTEMPT 1 WAS WRONG AND IS DISABLED -- read this before trying again.
        //
        // It called container.SpawnContainedEntity(faction) and assumed the
        // engine would release the passenger the partner had just ejected,
        // because both machines load the same 8 riders and the host ejects them
        // in order. Measured 2026-09-18: the intended passenger was STILL aboard
        // after every one of the 8 calls. Whether a DIFFERENT passenger came out
        // is not known -- the transport census never changed from "8 aboard" on
        // either machine, on the host even while it was demonstrably ejecting
        // riders, so that counter does not answer this question.
        //
        // So the call is not made. An unchoosable release that MIGHT put the
        // wrong rider on the board is worse than the divergence it is meant to
        // repair: a wrong unit placed here is a new disagreement, whereas a
        // passenger left aboard is the one we already know how to detect.
        //
        // SpawnContainedEntity(FactionType) takes no passenger and no tile, so
        // it cannot be aimed, and the call is not made at all.
        //
        // ATTEMPT 2 -- ASK THE ENGINE FOR THE MOVE IT WAS NEVER ASKED FOR.
        //
        // Two paths have tried to move this passenger and each supplied half of
        // what a teleporting exit needs:
        //
        //   the relayed move   MovementAction.Leave,   normal flags   -> refused
        //   the corrector      MovementAction.Default, teleport flags -> skipped
        //                      (and Default is not a leave at all)
        //
        // MovementAction is [Default=0, Enter=1, Leave=2, Backwards=4,
        // Teleport=8] -- a flags enum -- so Leave|Teleport is a combination
        // neither has asked for: "take this unit out of its container, and do it
        // without walking". Try that before touching engine fields by hand.
        //
        // This attempt is safe in a way attempt 1 was not: it names the
        // passenger, so the worst case is that it fails. It can never put the
        // WRONG unit on the board.
        MovementAction act = MovementAction.Leave | MovementAction.Teleport;
        bool placed = false;
        try { placed = passenger.MoveTo(dest, ref act, (MovementFlags)(1 | 2 | 4 | 8)); }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[CONTAINER] Leave|Teleport threw for '{who}' ({e.GetType().Name})");
        }

        bool stillAboard = IsContained(passenger);
        var p = dest.GetTilePos();
        if (placed && !stillAboard)
        {
            Plugin.Logger.LogInfo($"[CONTAINER] Ejected '{who}' here to match the partner -> ({p.x},{p.y})");
            return true;
        }

        Plugin.Logger.LogWarning(
            $"[CONTAINER] Leave|Teleport did not free '{who}' -> ({p.x},{p.y}): "
            + $"accepted={placed} stillContained={stillAboard}");
        ReportPassengerFacts(passenger, container);
        DumpContainment(container, passenger);

        // ATTEMPT 3 -- DETACH, THEN PLACE.
        //
        // Why this is not the field-surgery gamble attempt 1 was. The footprint
        // timeline shows BOTH machines register this creature across 9 tiles at
        // rest, and that the registration collapses to 1 while it is moving:
        //
        //   host   @(34,15) covers 9  ->  @(34,15) covers 1
        //   guest  @(34,15) covers 9  ->  @(34,13) covers 1  ->  @(34,10) covers 9
        //
        // The host's AI ejects DURING that transient window, so its target tiles
        // are free and IsValidEjectTile says yes. The guest replays the
        // transport's move instantly, its footprint is back to 9 before the
        // eject arrives, and every target is occupied by the creature itself.
        // An ordering difference, not broken state.
        //
        // Which matters, because it tells us the END STATE IS LEGAL: on the host
        // the rider ends up standing on a tile the settled creature also covers.
        // So we are not forcing an impossible board, we are reproducing a board
        // the engine itself produces -- it is only the entry CHECK, evaluated at
        // a moment the sender never evaluated it at, that refuses.
        //
        // MoveTo cannot do it (a contained actor declines every move), and the
        // eject skill's own placement is what refused. So detach this one named
        // passenger and put it where the partner put it. Still conservative: it
        // names the passenger, it verifies afterwards, and it reports honestly
        // if the engine does not agree.
        // DETACH-BY-HAND IS DISABLED. It half-works, which is worse than not
        // working: clearing m_ContainerEntity reports freed=True, but the
        // passenger is dormant and nothing will then move it, so it sits on the
        // parking tile detached from its transport -- a second wrong state on
        // top of the first. The eject redirect in ProbeEjectBefore is the live
        // approach; this is kept only as the record of what was measured.
        return false;
#pragma warning disable CS0162 // unreachable while the detach path is disabled
        try
        {
            passenger.m_ContainerEntity = null;
            try { passenger.OnLeftContainer(); } catch { }

            bool freed = !IsContained(passenger);

            // DETACH FIRST, THEN MOVE -- in that order, and it matters.
            //
            // Measured: clearing m_ContainerEntity reports freed=True, but
            // SetTile(dest) does NOT relocate the unit -- it stayed on the
            // parking tile at (34,15) every time. SetTile evidently sets a
            // reference without doing the tile bookkeeping a real placement
            // needs.
            //
            // MoveTo does that bookkeeping, and the reason it refused earlier no
            // longer holds: it declines a CONTAINED actor, and this one is no
            // longer contained. Same teleport flags the relayed-move corrector
            // uses (Force | ForceTeleport | AllowMoveThroughActors |
            // SkipPathfinding), with a plain Default action now that leaving is
            // already done.
            if (freed)
            {
                // AN INACTIVE ACTOR DOES NOT MOVE.
                //
                // Detaching reports freed=True, yet neither MoveTo nor SetTile
                // relocates the unit -- it sits on the parking tile every time.
                // The FACTS probe recorded these riders as active=False: they
                // are dormant actors, and the engine's own eject is presumably
                // what wakes them. Try waking it before moving it, and say
                // plainly whether that was the missing step.
                // Actor derives from Entity : Il2CppSystem.Object -- it is NOT a
                // MonoBehaviour, so there is no gameObject to activate and
                // IsActive() has no setter. Record it and move on; the engine's
                // own eject is what wakes these units, which is why the real fix
                // belongs in the OnUse prefix below rather than here.
                bool wasActive = false;
                try { wasActive = passenger.IsActive(); } catch { }
                Plugin.Logger.LogInfo($"[CONTAINER] '{who}' detached; IsActive={wasActive}");

                MovementAction tact = default;
                bool moved = false;
                try { moved = passenger.MoveTo(dest, ref tact, (MovementFlags)(1 | 2 | 4 | 8)); }
                catch (Exception me)
                {
                    Plugin.Logger.LogWarning($"[CONTAINER] post-detach MoveTo threw for '{who}' ({me.GetType().Name})");
                }
                if (!moved)
                {
                    // Last resort, and logged as such: put the reference where it
                    // belongs even if the engine would not walk it there.
                    try { passenger.SetTile(dest); } catch { }
                }
            }
            var now = passenger.GetLastTile()?.GetTilePos();
            bool landed = now != null && now.Value.x == p.x && now.Value.y == p.y;
            if (freed && landed)
            {
                Plugin.Logger.LogInfo($"[CONTAINER] Detached and placed '{who}' -> ({p.x},{p.y}) to match the partner");
                return true;
            }
            Plugin.Logger.LogWarning(
                $"[CONTAINER] Detach of '{who}' did not take: freed={freed} "
                + $"at={(now == null ? "(nowhere)" : $"({now.Value.x},{now.Value.y})")} wanted=({p.x},{p.y})");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[CONTAINER] Detach threw for '{who}' ({e.GetType().Name}: {e.Message})");
        }
        return false;
#pragma warning restore CS0162
    }

    // One compact line per failed eject, so all 8 riders are described across a
    // battle without eight full SHAPE dumps. The first dump showed ZERO actors
    // under the actors root naming the leviathan as their container, while the
    // passenger we hold names it -- so the riders are not where a board scan
    // looks, and that is also why the roster check reads them as NOTHING.
    private static void ReportPassengerFacts(Actor passenger, Entity container)
    {
        try
        {
            string who = DesyncDetector.TemplateNameOf(passenger);
            bool within = false, tower = false, active = false;
            try { within = passenger.IsContainedWithinAnotherEntity(); } catch { }
            try { tower = passenger.IsInsideTower(); } catch { }
            try { active = passenger.IsActive(); } catch { }
            var at = passenger.GetLastTile()?.GetTilePos();
            string head = "?";
            try
            {
                var h = container.GetContainedEntity();
                head = h == null ? "null" : (h.IsActor() ? DesyncDetector.TemplateNameOf(h.ToActor()) : "non-actor");
            }
            catch { }
            Plugin.Logger.LogInfo(
                $"[CONTAINER] FACTS '{who}': within={within} tower={tower} active={active} "
                + $"at={(at == null ? "(nowhere)" : $"({at.Value.x},{at.Value.y})")} containerHead='{head}'");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[CONTAINER] FACTS failed: {e.Message}"); }
    }
}

[HarmonyPatch]
internal static class ContainerPatches
{
    // REGISTER IN Plugin.Load -- an unregistered patch class is silently inert.
    // The parameter name MUST be the engine's own (_factionType). Harmony binds
    // postfix arguments by name, and a mismatch does not fail at compile time --
    // it fails at PATCH time with "IL Compile Error (unknown location)", which
    // SafePatch swallows into a one-line warning. Named wrong ('_faction'), this
    // whole class was silently inert on the first load after it was written.
    // MEASUREMENT ONLY -- no relay. Records WHEN each machine unloads, so the
    // two logs can be compared on round. This is the open question: the enemy
    // strength split at round 3 opens BEFORE any element death, and an unload
    // changes counted strength, so a one-round lag would produce exactly the
    // divergence we keep seeing. Diagnostic, not a fix -- the relay this file
    // once had is withdrawn for good reasons recorded above.
    [HarmonyPatch(typeof(Entity), nameof(Entity.SpawnContainedEntity))]
    [HarmonyPostfix]
    private static void ProbeUnload(Entity __instance)
    {
        // Dev diagnostic, one line per unload on both machines -- ~50 a mission.
        if (!CoopNet.Connected || !(Plugin.TraceDamage?.Value ?? false)) return;
        try
        {
            var tm = TacticalManager.Get();
            int round = -1;
            try { round = tm?.GetRound() ?? -1; } catch { }
            string who = "?";
            try { who = __instance.IsActor() ? DesyncDetector.TemplateNameOf(__instance.ToActor()) : "structure"; }
            catch { }
            Plugin.Logger.LogInfo($"[UNLOAD:{(CoopNet.Mode == CoopMode.Host ? "H" : "G")}] r{round} "
                + $"'{who}' released its passengers (remote={Replication.ApplyingRemote})");
        }
        catch { }
    }

    // WHY THE EJECT RUNS BUT PLACES NOBODY.
    //
    // EjectEntityHandler.OnUse is the actual ejection -- NOT
    // Entity.SpawnContainedEntity, which ProbeUnload above hooks. That was an
    // explicitly unproven inference and it is now disproved: the host fires
    // SpawnContainedEntity at r0/r1 during battle SETUP, on the riders
    // themselves, before the guest's battle has even begun. It builds squad
    // contents; it does not unload transports.
    //
    // Since UsageParameter.IgnoreUsabilityCheck was added to the replay, the
    // eject skill genuinely RUNS on the guest -- AP now tracks the host exactly
    // (120 -> 100 -> 80 -> 60 -> 40) -- yet all 8 riders stay aboard. So OnUse
    // is entered and then declines to place anyone. The engine exposes its own
    // reasons: IsValidEjectTile(entity, tile, checkEntranceTiles) and
    // OnVerifyTarget. Ask them on the way in rather than guess.
    // Where the partner actually put this passenger, while we let the engine
    // eject it somewhere it will accept. Null when no substitution is active.
    private static Tile _ejectWantedTile;

    [HarmonyPatch(typeof(Menace.Tactical.Skills.Effects.EjectEntityHandler), "OnUse")]
    [HarmonyPrefix]
    private static void ProbeEjectBefore(Actor _user, ref Tile _targetTile)
    {
        if (!CoopNet.Connected) return;

        // EJECT WHERE THE ENGINE WILL, THEN MOVE IT WHERE THE PARTNER DID.
        //
        // A big creature registers across 9 tiles at rest and collapses to 1
        // WHILE MOVING -- measured on both machines. The host's AI ejects during
        // that window, so its targets are free. The guest replays the move, the
        // move finishes, the footprint is back to 9, and every eject target is
        // then occupied by the creature itself: IsValidEjectTile=False and OnUse
        // places nobody.
        //
        // Things that were tried and did not work, so they are not tried again:
        // SpawnContainedEntity cannot be aimed; Leave|Teleport is refused;
        // detaching by hand DOES free the passenger but nothing will then move
        // it -- these riders are dormant (active=False), SetTile does no tile
        // bookkeeping, and Actor is not a MonoBehaviour so there is nothing to
        // activate; forcing m_IsMoving does not release the tile registration.
        //
        // What is left is to stop fighting the engine and use it. It will
        // happily eject onto a tile it considers valid, and doing so is what
        // creates a live, active, properly registered actor. So substitute a
        // tile it accepts, let OnUse do the hard part, and afterwards teleport
        // the result -- now an ordinary board actor -- onto the tile the partner
        // used. That last step is the one operation already known to work.
        _ejectWantedTile = null;
        if (Replication.ApplyingRemote && _user != null && _targetTile != null)
        {
            bool valid = true;
            try { valid = Menace.Tactical.Skills.Effects.EjectEntityHandler.IsValidEjectTile(_user, _targetTile, false); }
            catch { }
            if (!valid)
            {
                var want = _targetTile;
                var wp = want.GetTilePos();
                Tile alt = null;
                // Nearest first, so the substitute is as close to the partner's
                // tile as the board allows and the corrective hop is small.
                for (int r = 1; r <= 4 && alt == null; r++)
                    for (int dy = -r; dy <= r && alt == null; dy++)
                        for (int dx = -r; dx <= r && alt == null; dx++)
                        {
                            if (System.Math.Abs(dx) != r && System.Math.Abs(dy) != r) continue;
                            var t = Replication.GetTileAt(wp.x + dx, wp.y + dy);
                            if (t == null) continue;
                            bool ok = false;
                            try { ok = Menace.Tactical.Skills.Effects.EjectEntityHandler.IsValidEjectTile(_user, t, false); }
                            catch { }
                            if (ok) alt = t;
                        }
                if (alt != null)
                {
                    _ejectWantedTile = want;
                    _targetTile = alt;
                    var ap2 = alt.GetTilePos();
                    Plugin.Logger.LogInfo(
                        $"[CONTAINER] Eject target ({wp.x},{wp.y}) is blocked here (the transport covers it); "
                        + $"ejecting to ({ap2.x},{ap2.y}) and moving it back.");
                }
                else
                {
                    Plugin.Logger.LogWarning(
                        $"[CONTAINER] Eject target ({wp.x},{wp.y}) is blocked here and no valid tile was found within 4 -- "
                        + "this passenger cannot be landed.");
                }
            }
        }

        try
        {
            string side = CoopNet.Mode == CoopMode.Host ? "H" : "G";
            var p = _targetTile?.GetTilePos();
            string user = DesyncDetector.TemplateNameOf(_user);

            Entity passenger = null;
            try { passenger = _user?.GetContainedEntity(); } catch { }
            string pname = "null";
            try { pname = passenger == null ? "null"
                        : (passenger.IsActor() ? DesyncDetector.TemplateNameOf(passenger.ToActor()) : "non-actor"); }
            catch { }

            // Both spellings of the tile test: the flag decides whether the
            // transport's entrance tiles are required, and which answer differs
            // between the machines is the whole question.
            string vP = "?", vU = "?";
            try { vP = passenger == null ? "n/a"
                     : Menace.Tactical.Skills.Effects.EjectEntityHandler.IsValidEjectTile(passenger, _targetTile, true).ToString()
                       + "/" + Menace.Tactical.Skills.Effects.EjectEntityHandler.IsValidEjectTile(passenger, _targetTile, false).ToString(); }
            catch (Exception e) { vP = "threw:" + e.GetType().Name; }
            try { vU = Menace.Tactical.Skills.Effects.EjectEntityHandler.IsValidEjectTile(_user, _targetTile, true).ToString()
                       + "/" + Menace.Tactical.Skills.Effects.EjectEntityHandler.IsValidEjectTile(_user, _targetTile, false).ToString(); }
            catch (Exception e) { vU = "threw:" + e.GetType().Name; }

            // THE TARGET TILE'S OCCUPANT *BEFORE* THE EJECT.
            //
            // The first probe logged this only AFTER OnUse, which cannot
            // distinguish "the leviathan was standing there and blocked it"
            // from "the rider was placed there". The host may well report its
            // own transport here too -- it is a large multi-tile creature and
            // ejects onto its own footprint -- in which case occupancy is NOT
            // the difference and the entrance-tile cache is the next suspect.
            string before = "?";
            try
            {
                var occ = Replication.ActorOn(_targetTile);
                before = occ == null ? "empty" : DesyncDetector.TemplateNameOf(occ);
            }
            catch { }

            // Entrance tiles are CACHED -- Entity.UpdateEntranceTiles() exists --
            // so the guest's copy can simply be stale or never computed.
            string blocked = "?";
            try { blocked = _user.IsEntranceTileBlocked(passenger, _targetTile).ToString(); }
            catch (Exception e) { blocked = "threw:" + e.GetType().Name; }

            // AP: the engine has HasAPToEnterOrLeaveContainer(), and the two
            // machines' AP for these units differs visibly in TRACE pending.
            string apU = "?", apP = "?";
            try { apU = _user.GetActionPoints().ToString(); } catch { }
            try
            {
                if (passenger != null && passenger.IsActor())
                {
                    var pa = passenger.ToActor();
                    apP = pa.GetActionPoints() + (pa.HasAPToEnterOrLeaveContainer() ? " canLeave" : " NOAP");
                }
            }
            catch (Exception e) { apP = "threw:" + e.GetType().Name; }

            var up = _user?.GetLastTile()?.GetTilePos();

            // FACING, AND THE FOOTPRINT IT DECIDES.
            //
            // Both machines put this leviathan on the SAME tile with the SAME
            // AP, yet the guest reports it as the occupant of (34,11)/(35,11)/
            // (35,10) while the host reports those empty. A large creature's
            // covered tiles depend on which way it is pointing -- Direction is
            // first-class here, with its own turning AP cost -- and the mod has
            // never replicated it: SetDirection is called nowhere, GetDirection
            // only for cover maths. Print the facing and the actual covered
            // tiles so the two logs can be laid side by side.
            string dir = "?";
            try { dir = _user.GetDirection().ToString(); } catch { }
            string footprint = "";
            try
            {
                if (up != null)
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            var t = Replication.GetTileAt(up.Value.x + dx, up.Value.y + dy);
                            if (t == null) continue;
                            // ACTOR *AND* ENTITY, because they need not agree.
                            //
                            // The guest reports the leviathan as the actor on a
                            // 3x3 block while the host reports one tile. Nine
                            // tiles, and the guest is carrying eight riders it
                            // has not ejected plus the transport = nine. If each
                            // contained rider is sitting on one of those tiles
                            // while GetActor() resolves to its CONTAINER, that
                            // is the blockage, and GetEntity() will name the
                            // rider where GetActor() names the leviathan.
                            Actor occ = null; Entity ent2 = null;
                            try { occ = t.GetActor(); } catch { }
                            try { ent2 = t.GetEntity(); } catch { }
                            if (occ == null && ent2 == null) continue;
                            string an = "-", en = "-";
                            try { an = occ == null ? "-" : DesyncDetector.TemplateNameOf(occ); } catch { }
                            try
                            {
                                en = ent2 == null ? "-"
                                   : (ent2.IsActor() ? DesyncDetector.TemplateNameOf(ent2.ToActor()) : "non-actor");
                            }
                            catch { }
                            // Trim the shared prefix so the line stays readable.
                            an = an.Replace("enemy.alien_leviathan_bombardier", "LEV");
                            en = en.Replace("enemy.alien_leviathan_bombardier", "LEV");
                            footprint += $" ({up.Value.x + dx},{up.Value.y + dy})a={an},e={en}";
                        }
            }
            catch { }
            Plugin.Logger.LogInfo($"[EJECT:{side}] facing={dir} covers={(footprint == "" ? "(none)" : footprint)}");

            Plugin.Logger.LogInfo(
                $"[EJECT:{side}] OnUse user='{user}'@{(up == null ? "(?)" : $"({up.Value.x},{up.Value.y})")} "
                + $"target={(p == null ? "(null)" : $"({p.Value.x},{p.Value.y})")} tileHeldBy='{before}' "
                + $"passenger='{pname}' validForPassenger(entrance/no)={vP} validForUser(entrance/no)={vU} "
                + $"entranceBlocked={blocked} userAP={apU} passengerAP={apP} "
                + $"remote={Replication.ApplyingRemote}");
        }
        catch { }
    }

    // And what actually changed. If the head advanced, the eject worked.
    [HarmonyPatch(typeof(Menace.Tactical.Skills.Effects.EjectEntityHandler), "OnUse")]
    [HarmonyPostfix]
    private static void ProbeEjectAfter(Actor _user, Tile _targetTile)
    {
        if (!CoopNet.Connected) return;
        // If the eject was redirected, finish the job: the passenger the engine
        // just put down is a live, registered actor now, so an ordinary forced
        // teleport can take it to the tile the partner used.
        if (_ejectWantedTile != null)
        {
            var want = _ejectWantedTile;
            _ejectWantedTile = null;
            try
            {
                var landed = Replication.ActorOn(_targetTile);
                var wp = want.GetTilePos();
                if (landed == null)
                {
                    Plugin.Logger.LogWarning($"[CONTAINER] Redirected eject put nothing on the substitute tile -- cannot move it to ({wp.x},{wp.y})");
                }
                else
                {
                    MovementAction act2 = default;
                    bool moved = false;
                    try { moved = landed.MoveTo(want, ref act2, (MovementFlags)(1 | 2 | 4 | 8)); } catch { }

                    // The corrective hop usually CANNOT happen yet: the partner
                    // put this rider on a tile its own transport also covers --
                    // legal there, because the eject happened while the
                    // footprint was collapsed mid-move -- and every placement
                    // path here refuses an occupied tile. The transport moves on
                    // in a later round and frees it, so remember the debt and
                    // settle it then rather than leaving the rider a tile out
                    // for the rest of the battle.
                    //
                    // Do NOT judge it by reading the position on the next line:
                    // MoveTo walks asynchronously and returns before arriving.
                    ContainerSync.OwePlacement(landed, want);
                    Plugin.Logger.LogInfo(
                        $"[CONTAINER] '{DesyncDetector.TemplateNameOf(landed)}' ejected; owed ({wp.x},{wp.y}) "
                        + $"(first attempt accepted={moved}) -- will settle when the transport clears it");
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[CONTAINER] post-eject move failed: {e.Message}"); }
        }
        try
        {
            string side = CoopNet.Mode == CoopMode.Host ? "H" : "G";
            string head = "?";
            try
            {
                var h = _user?.GetContainedEntity();
                head = h == null ? "null" : (h.IsActor() ? DesyncDetector.TemplateNameOf(h.ToActor()) : "non-actor");
            }
            catch { }
            bool holds = false;
            try { holds = _user != null && _user.IsContainingAnotherEntity(); } catch { }
            var occupant = "?";
            try
            {
                var a = Replication.ActorOn(_targetTile);
                occupant = a == null ? "empty" : DesyncDetector.TemplateNameOf(a);
            }
            catch { }
            Plugin.Logger.LogInfo($"[EJECT:{side}] after OnUse: stillHolds={holds} head='{head}' targetTileNow='{occupant}'");
        }
        catch { }
    }

    // HOOK REMOVED -- see the note on ContainerSync.BroadcastUnload. Unloading
    // already replicates as an ordinary skill relay, proven by the guest logging
    // remote=True on every eject. Re-adding this would restore a second trigger
    // for an event that already has one.
    //
    // BroadcastUnload and ApplyUnload are left in place, unreferenced, because
    // the reasoning in them is the record of why this was tried. Delete both once
    // a release has shipped without them.
}
