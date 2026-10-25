using System;
using System.Collections.Generic;
using System.Text;
#if MELON
using Il2CppMenace.Tactical;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.Tactical;
#endif

namespace MenaceCoop;

// AN ENEMY THAT EXISTS FOR ONE PLAYER AND NOT THE OTHER.
//
// Reported from a live battle as "some enemies are straight up invisible". They
// were not a rendering fault: the two machines had genuinely different boards,
// the host holding 46 mid-battle arrivals to the guest's 34, including a
// chaingun truck and a flamethrower truck the guest never received at all. From
// the guest's chair that is fire arriving from an empty tile.
//
// The roster check already NOTICES this, but only at the level of "the armies
// differ" in a log line, and only host-to-guest. What a player needs while it is
// happening is to see WHERE, on their own screen. So both machines publish the
// tiles they believe hold enemies, and each marks the tiles its partner has
// something on and it does not.
//
// Deliberately separate from BattleRoster's manifest rather than making that one
// bidirectional: the roster comparison is built around host-sends/guest-checks
// and carries the strict start-of-battle semantics that caught the generation
// bugs. This is a different question asked continuously during the fight, and
// mixing them would put both at risk.
internal static class PhantomWatch
{
    internal readonly struct Phantom
    {
        internal readonly int X, Y;
        internal readonly string Template;
        internal Phantom(int x, int y, string t) { X = x; Y = y; Template = t; }
    }

    // IS THIS ACTOR ACTUALLY AN ENEMY?
    //
    // "Not in the player faction" is not the same question, and answering the
    // wrong one put red ENEMY brackets over civilians -- reported immediately.
    // Rescue and defence missions put a crowd of them on the board, they are
    // driven independently of the player's units, and they are the very thing a
    // player must NOT be encouraged to shoot at.
    //
    // FactionType names the distinction outright, so use it rather than
    // inferring hostility from faction index.
    internal static bool IsHostile(TacticalManager tm, Actor a)
    {
        try
        {
            int fid = a.GetFactionID();
            var factions = tm.GetFactions();
            if (factions == null) return false;
            for (int i = 0; i < factions.Length; i++)
            {
                var f = factions[i];
                if (f == null) continue;
                int idx;
                try { idx = f.GetIndex(); } catch { continue; }
                if (idx != fid) continue;
                switch (f.GetFactionType())
                {
                    case FactionType.Player:
                    case FactionType.PlayerAI:
                    case FactionType.Civilian:
                    case FactionType.Neutral:
                    case FactionType.AlliedLocalForces:
                        return false;
                    default:
                        return true;     // Pirates, Wildlife, RogueArmy, Constructs, EnemyLocalForces
                }
            }
        }
        catch { }
        return false;    // unknown: never paint a red box on a guess
    }

    // Tiles the PARTNER reports an enemy on that we have nothing on.
    internal static readonly List<Phantom> Phantoms = new();

    private const float PublishEvery = 3f;
    private static float _lastPublishAt = -999f;
    private static IntPtr _lastTm = IntPtr.Zero;

    private static string _partnerPayload;
    private static string _prevPartnerPayload;
    // Coop ids of the rows in those two reports (SkillId, "id,id,..."); null when
    // the sender is an older build that sends none.
    private static HashSet<int> _partnerIds, _prevPartnerIds;
    private static float _partnerAt = -999f;
    private static int _reportsSinceMotion;   // partner reports received since OUR board last moved

    internal static void Reset()
    {
        Phantoms.Clear();
        _seenAt.Clear();
        _prev2 = null;
        _firstPhantomAt = -999f;
        _partnerPayload = null;
        _prevPartnerPayload = null;
        _partnerIds = _prevPartnerIds = null;
        _lastPublishAt = -999f;
    }

    internal static void Tick()
    {
        if (!CoopNet.Connected) return;
        var tm = TacticalManager.Get();
        if (tm == null)
        {
            if (_lastTm != IntPtr.Zero) { Reset(); _lastTm = IntPtr.Zero; }
            return;
        }
        if (tm.Pointer != _lastTm) { Reset(); _lastTm = tm.Pointer; }

        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now - _lastPublishAt >= PublishEvery)
        {
            _lastPublishAt = now;
            if (CoopNet.Mode == CoopMode.Host) FogInputProbe.Sample(tm);   // clients sample on receipt: same moment
            try
            {
                var ids = new StringBuilder();
                string rows = Build(tm, ids);
                CoopNet.Send(new CoopMsg { T = "eseen", Data = rows, SkillId = ids.ToString() });
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[PHANTOM] Publish failed: {e.Message}"); }
        }

        // NEVER JUDGE A MOVING BOARD.
        //
        // Reported: "the red box appears when enemies are moving, works good when
        // they're standing still." Of course it did -- the partner's snapshot and
        // ours are taken at different instants, so mid-walk every unit is
        // somewhere slightly different on the two machines and every one of them
        // looks missing. Freeze the verdict while anything is animating and let
        // the previous result stand; a real phantom is not going anywhere.
        bool moving = false;
        try { moving = TacticalManager.IsAnyActorMoving(); } catch { }
        if (moving) { _reportsSinceMotion = 0; return; }
        if (_adoptPending && CoopNet.Mode == CoopMode.Guest) { _adoptPending = false; AdoptHostView(tm); }

        // A UNIT THAT HAS JUST SPAWNED IS NOT A PHANTOM EITHER.
        //
        // Reported: "it looks like it appeared when enemies just spawned on the
        // map." Confirmed in the log -- every false marker sat on a map EDGE
        // tile ((37,24), (33,5), (20,1)), i.e. a reinforcement spawn point, and
        // each cleared itself within seconds with "Boards agree again". The two
        // machines simply register an arrival a moment apart, and for that
        // moment one of them honestly has nothing there.
        //
        // Reinforcement waves are exactly when this feature matters most, so the
        // answer is to wait out the churn rather than to exclude spawn areas.
        if (UnityEngine.Time.realtimeSinceStartup - DesyncDetector.LastArrivalAt < ArrivalGrace) return;

        // A REPORT SENT BEFORE THE PARTNER SAW OUR LAST MOVE IS HISTORY, NOT EVIDENCE.
        //
        // A heavy truck drove (32,29) -> (26,26) and BOTH machines flagged it:
        // each had applied the move, then received a partner report taken before
        // the partner had applied it -- nine tiles off, past MoveTolerance. The
        // freeze above only covers the animation itself. Two reports that
        // arrived after our board last settled are guaranteed to post-date it.
        if (_reportsSinceMotion < 2) return;

        Recompute(tm);
    }

    // template|x|y per enemy, semicolon separated. Same shape as the roster
    // manifest minus the member count, which does not matter for "is anything
    // there at all".
    private static string Build(TacticalManager tm, StringBuilder ids)
    {
        var sb = new StringBuilder();
        try
        {
            int playerFac = -1;
            try { playerFac = tm.GetPlayerFaction()?.GetIndex() ?? -1; } catch { }
            bool fogOn = true;
            try { fogOn = tm.GetMap()?.IsUsingFogOfWar() ?? true; } catch { }
            var actors = DesyncDetector.AllActors(tm);
            for (int i = 0; i < actors.Length; i++)
            {
                var a = actors[i];
                if (a == null) continue;
                try
                {
                    if (a.GetFactionID() == playerFac) continue;
                    if (Ownership.IsKnownPlayerUnit(a)) continue;
                    if (a.IsDying()) continue;
                    // NON-HOSTILES (civilians, allied militia) ride along with a 4th
                    // field: the phantom/fog checks parse exactly 3 fields and skip
                    // them, as before; AdoptHostView reads them (r136: clients kept
                    // workers and militia visible after they left all sight; the host
                    // hid them -- the host is right).
                    bool hostile = IsHostile(tm, a);

                    // PUBLISH ONLY WHAT THE PLAYER CAN SEE, BY THE GAME'S OWN FOG.
                    //
                    // This used to publish every enemy and let the receiver guess
                    // with "within 14 tiles of a player unit". Reported: a marker
                    // appeared on an enemy neither player could see, only the
                    // garrison -- distance is not sight, and it ignored every
                    // friendly that is not a player unit. The engine already
                    // knows: VisibilityToPlayer is the fog of war itself, garrison
                    // vision and all. Both players' squads are in the one player
                    // faction on both machines, so what is visible here is what
                    // the partner would see on an agreeing board, and nothing the
                    // fog hides can leak into a marker.
                    if (fogOn && a.VisibilityToPlayer != Menace.Tactical.Visibility.Visible) continue;
                    var t = a.GetLastTile();
                    if (t == null) continue;                 // aboard something: no tile to mark
                    var p = t.GetTilePos();
                    if (sb.Length > 0) sb.Append(';');
                    sb.Append(DesyncDetector.TemplateNameOf(a)).Append('|').Append(p.x).Append('|').Append(p.y);
                    if (!hostile) sb.Append("|n");
                    if (ids.Length > 0) ids.Append(',');
                    ids.Append(DesyncDetector.CoopIdOf(a));
                }
                catch { }
            }
        }
        catch { }
        return sb.ToString();
    }

    private static HashSet<int> _prev2;
    private static HashSet<int> _hiddenLastEval = new(), _hiddenBeforeReport = new();

    private static float _lastHiddenLogAt = -999f;
    private static bool _adoptPending;

    // HOST-DECIDED FOG BETWEEN ORDERS (#64, r132: a scavenger stayed visible on a
    // client 20 s after the host's fog hid it -- it was not acting, so no order
    // carried the host's view). The host's eseen lists exactly the hostile enemies
    // it can see. A client adopts it: an enemy both of the last two host reports
    // agree on (seen in both / absent from both) gets the host's visibility. Two
    // reports, like the checker, so a unit walking between snapshots never flickers.
    private static void AdoptHostView(TacticalManager tm)
    {
        // null = no report yet; "" = a report of NO visible enemies, which is real
        // information (hide everything) and must not read as "no report".
        if (_partnerPayload == null || _prevPartnerPayload == null) return;
        var now = ParseAllTiles(_partnerPayload);
        var prev = ParseAllTiles(_prevPartnerPayload);
        var disagreeNow = new HashSet<IntPtr>();
        bool fogOn = true;
        try { fogOn = tm.GetMap()?.IsUsingFogOfWar() ?? true; } catch { }
        if (!fogOn) return;
        int playerFac = -1;
        try { playerFac = tm.GetPlayerFaction()?.GetIndex() ?? -1; } catch { }
        var want = new Dictionary<IntPtr, bool>();
        var actors = DesyncDetector.AllActors(tm);
        for (int i = 0; i < actors.Length; i++)
        {
            var a = actors[i];
            if (a == null) continue;
            try
            {
                if (a.GetFactionID() == playerFac || Ownership.IsKnownPlayerUnit(a)) continue;
                if (a.IsDying()) continue;   // civilians and allies too (r136)
                var t = a.GetLastTile();
                if (t == null) continue;
                var p = t.GetTilePos();
                int key = p.x * 1000 + p.y;
                // BY IDENTITY WHEN THE HOST SENDS IT (player report 2026-10-24, 3
                // players: "for guests the enemy is invisible until he moves").
                // Matching by tile alone failed for an enemy standing on a
                // different tile here than on the host: the host's row named a
                // tile where we have nothing, so this read "host does not see it"
                // and kept it hidden. Only its own move (HVis on the order) ever
                // showed it. Id OR tile: an id the two machines assigned
                // differently (#71 garrisons) still matches by tile as before.
                int cid = DesyncDetector.CoopIdOf(a);
                bool hostSees = now.Contains(key) || (cid >= 0 && _partnerIds != null && _partnerIds.Contains(cid));
                bool hostSaw = prev.Contains(key) || (cid >= 0 && _prevPartnerIds != null && _prevPartnerIds.Contains(cid));
                if (hostSees != hostSaw) continue;                   // reports disagree: wait
                bool iSee = a.VisibilityToPlayer == Menace.Tactical.Visibility.Visible;
                // Keep an override the host still confirms even when it currently
                // agrees -- it agrees BECAUSE of the override (r133: dropping it let
                // the engine re-reveal the chaingun every 3 s, 8 times running).
                if (iSee == hostSees) { if (FogSync.HasOverride(a.Pointer)) want[a.Pointer] = hostSees; continue; }
                // THE DISAGREEMENT MUST PREDATE THIS REPORT (r136 08:13:30: a scavenger
                // came into view on BOTH machines between two host reports; the
                // client saw it first and hid it on two reports that were simply
                // older than the reveal). Act only if we already disagreed last time.
                disagreeNow.Add(a.Pointer);
                if (!_prevDisagree.Contains(a.Pointer) && !FogSync.HasOverride(a.Pointer)) continue;
                want[a.Pointer] = hostSees;
                // r133: an enemy that revealed itself by SHOOTING keeps being re-shown
                // by the client's engine; the host clears that at the end of the AI
                // activation, which clients skip (the r109 pattern). Clear the
                // reveal/detection state with the visibility, and log both sides.
                string flags = "";
                if (!hostSees)
                {
                    string before = DetectFlags(a, playerFac);
                    try { a.Revealed = false; } catch { }
                    try { a.ResetDetectedByFaction(); } catch { }
                    flags = $" [revealed/detected {before} -> {DetectFlags(a, playerFac)}]";
                }
                a.SetVisibility(hostSees ? Menace.Tactical.Visibility.Visible : Menace.Tactical.Visibility.Hidden);
                bool took = (a.VisibilityToPlayer == Menace.Tactical.Visibility.Visible) == hostSees;
                Plugin.Logger.LogInfo($"[VIS] Host's view adopted: '{DesyncDetector.TemplateNameOf(a)}' at ({p.x},{p.y}) "
                    + $"{(iSee ? "visible" : "hidden")} here -> {(hostSees ? "visible" : "hidden")}{flags}{(took ? "" : " -- DID NOT TAKE")}");
            }
            catch { }
        }
        _prevDisagree = disagreeNow;
        FogSync.SetOverrides(want, actors);
    }

    private static HashSet<IntPtr> _prevDisagree = new();

    // The coop ids that ride along with a report, one per row. Null (fall back to
    // tiles) when there are none or the count does not match the rows. Rows
    // whose sender had no id for that unit carry -1 and simply never match.
    private static HashSet<int> ParseIds(string ids, string payload)
    {
        if (ids == null) return null;
        int rows = string.IsNullOrEmpty(payload) ? 0 : payload.Split(';').Length;
        var parts = ids.Length == 0 ? System.Array.Empty<string>() : ids.Split(',');
        if (parts.Length != rows) return null;
        var set = new HashSet<int>();
        foreach (var s in parts) if (int.TryParse(s, out int id) && id >= 0) set.Add(id);
        return set;
    }

    // Every row of a host report, hostile (3 fields) or not (4th field "n").
    private static HashSet<int> ParseAllTiles(string payload)
    {
        var set = new HashSet<int>();
        if (string.IsNullOrEmpty(payload)) return set;
        foreach (var row in payload.Split(';'))
        {
            var f = row.Split('|');
            if (f.Length < 3) continue;
            if (int.TryParse(f[1], out int x) && int.TryParse(f[2], out int y)) set.Add(x * 1000 + y);
        }
        return set;
    }

    private static string DetectFlags(Actor a, int playerFac)
    {
        string r = "?", d = "?";
        try { r = a.Revealed ? "R" : "-"; } catch { }
        try { d = a.IsDetectedByFaction(playerFac) ? "D" : "-"; } catch { }
        return r + d;
    }

    internal static void HandleRemote(CoopMsg m)
    {
        // ONE PARTNER VIEW (v5): with 3+ players every client's "eseen" reaches
        // everyone and overwrote this single slot in turn. Clients compare with
        // the HOST's view only; the host with the lowest client seat.
        if (CoopNet.Mode == CoopMode.Guest && m.From > 0 && m.From != Peers.HostSeat) return;
        if (CoopNet.Mode == CoopMode.Host)
        {
            int first = -1;
            foreach (int s in Peers.SessionSeats()) if (s != Peers.HostSeat && (first < 0 || s < first)) first = s;
            if (first >= 0 && m.From != first) return;
        }
        // What was fogged here while the partner saw it, as of the report before
        // this one: only those can be confirmed by this report.
        _hiddenBeforeReport = _hiddenLastEval;
        _prev2 = ParseTiles(_prevPartnerPayload);
        _prevPartnerPayload = _partnerPayload;
        _partnerPayload = m.Data ?? "";
        _prevPartnerIds = _partnerIds;
        _partnerIds = ParseIds(m.SkillId, _partnerPayload);
        _partnerAt = UnityEngine.Time.realtimeSinceStartup;
        _adoptPending = true;
        if (CoopNet.Mode == CoopMode.Guest) FogInputProbe.Sample(TacticalManager.Get());
        _reportsSinceMotion++;
    }

    private const float ArrivalGrace = 10f;

    // How far a reported enemy may be from one of ours before we stop assuming
    // it is the same unit having moved. Roughly one generous activation.
    private const int MoveTolerance = 6;
    private const float PhantomLinger = 8f;
    private static readonly Dictionary<int, (float at, string tmpl)> _seenAt = new();

    // Is any unit of this KIND (ignoring what it is riding) within r tiles?
    private static bool KindNear(Dictionary<string, List<int>> byKind, string kind, int x, int y, int r)
    {
        if (string.IsNullOrEmpty(kind)) return false;
        if (!byKind.TryGetValue(kind, out var tiles)) return false;
        for (int i = 0; i < tiles.Count; i++)
        {
            int tx = tiles[i] / 1000, ty = tiles[i] % 1000;
            if (System.Math.Abs(tx - x) + System.Math.Abs(ty - y) <= r) return true;
        }
        return false;
    }

    private static bool EnemyNear(HashSet<int> mine, int x, int y, int r)
    {
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                if (System.Math.Abs(dx) + System.Math.Abs(dy) > r) continue;
                if (mine.Contains((x + dx) * 1000 + (y + dy))) return true;
            }
        return false;
    }

    private static float _lastReportAt = -999f;
    private static int _lastReported;
    private static float _firstPhantomAt = -999f;
    private static float _lastBannerAt = -999f;
    private const float PersistentPhantom = 25f;

    private static void Recompute(TacticalManager tm)
    {
        Phantoms.Clear();
        if (string.IsNullOrEmpty(_partnerPayload)) return;
        // Stale partner data describes a board that has moved on; marking from it
        // would paint ghosts that were simply units walking.
        if (UnityEngine.Time.realtimeSinceStartup - _partnerAt > PublishEvery * 3f) return;

        try
        {
            // Where WE have enemies.
            var mineTiles = new HashSet<int>();
            var mineHidden = new Dictionary<int, string>();   // our enemies the fog hides here
            var hiddenHere = new List<string>();
            var hiddenNow = new HashSet<int>();
            bool fogOn = true;
            try { fogOn = tm.GetMap()?.IsUsingFogOfWar() ?? true; } catch { }
            var mineByKind = new Dictionary<string, List<int>>();
            int playerFac = -1;
            try { playerFac = tm.GetPlayerFaction()?.GetIndex() ?? -1; } catch { }
            var actors = DesyncDetector.AllActors(tm);
            for (int i = 0; i < actors.Length; i++)
            {
                var a = actors[i];
                if (a == null) continue;
                try
                {
                    // "IS ANYTHING THERE?" IS A WIDER QUESTION THAN "IS AN ENEMY
                    // THERE?" -- deliberately no IsHostile filter on this side.
                    //
                    // Reported: a red square on an enemy heavy truck that BOTH
                    // players could plainly see. The cause is what happens to a
                    // transport when it unloads: it stops being an active hostile
                    // and the strict hostile-only scan stopped counting it, so
                    // the machine that had unloaded saw an empty tile where its
                    // partner still reported a pirate truck. Every phantom in the
                    // run was an _on_heavy_truck, and they were all on the host --
                    // the side that had done the unloading.
                    //
                    // For deciding whether a tile is EMPTY, any actor standing
                    // there is enough. IsHostile still governs what we PUBLISH,
                    // which is what keeps civilians out of it.
                    if (a.GetFactionID() == playerFac) continue;
                    if (Ownership.IsKnownPlayerUnit(a)) continue;
                    var t = a.GetLastTile();
                    if (t == null) continue;
                    var p = t.GetTilePos();
                    mineTiles.Add(p.x * 1000 + p.y);
                    try { if (fogOn && a.VisibilityToPlayer != Menace.Tactical.Visibility.Visible) mineHidden[p.x * 1000 + p.y] = DesyncDetector.TemplateNameOf(a); } catch { }

                    // ...and the same tiles indexed by mount-agnostic template,
                    // so a squad can be recognised whether or not it is aboard.
                    string kind = DesyncDetector.MountAgnostic(DesyncDetector.TemplateNameOf(a));
                    if (!mineByKind.TryGetValue(kind, out var lst))
                    { lst = new List<int>(); mineByKind[kind] = lst; }
                    lst.Add(p.x * 1000 + p.y);
                }
                catch { }
            }

            // CONFIRM ACROSS TWO REPORTS BEFORE BELIEVING IT.
            //
            // A unit that moved between the partner's snapshot and ours is not a
            // phantom, and flagging it would put markers all over the screen
            // during ordinary play. Requiring the same tile in the PREVIOUS
            // partner report as well means a genuine phantom -- which does not
            // move, because it is not there to move -- survives while a walking
            // unit does not.
            var prev = ParseTiles(_prevPartnerPayload);

            foreach (var row in _partnerPayload.Split(';'))
            {
                if (row.Length == 0) continue;
                var f = row.Split('|');
                if (f.Length != 3) continue;
                if (!int.TryParse(f[1], out int x) || !int.TryParse(f[2], out int y)) continue;
                int key = x * 1000 + y;
                if (mineTiles.Contains(key))                 // we have something there too...
                {
                    // ...but is it VISIBLE here? (hand test 2026-10-02: "some
                    // enemies looked invisible" -- present on every board, so no
                    // phantom; the fog hid it on one machine only.)
                    // AND it was already hidden here before this report arrived
                    // (r111: mirror-image [VIS] lines on host and all clients at
                    // the same instant -- the enemy had just gone into fog on
                    // every machine, each comparing its fresh fog against the
                    // other's older report).
                    if (mineHidden.TryGetValue(key, out var hn) && prev != null && prev.Contains(key))
                    {
                        hiddenNow.Add(key);
                        if (_hiddenBeforeReport.Contains(key)) hiddenHere.Add($"{hn}@({x},{y})");
                    }
                    continue;
                }

                // A UNIT THAT MOVED IS NOT A PHANTOM.
                //
                // Exact-tile comparison flagged every enemy that had taken a step
                // between the two snapshots. What separates the two cases is
                // distance: a walker is still CLOSE to where the partner last saw
                // it, while something the board never received has nothing of its
                // kind anywhere near. Anything within a normal move of the
                // reported tile is treated as that same unit, moved.
                if (EnemyNear(mineTiles, x, y, MoveTolerance)) continue;

                // THE SAME SQUAD MID-DISMOUNT IS NOT A PHANTOM.
                //
                // Every marker that survived the other guards was a transport:
                // "'enemy.pirate_outcasts_on_heavy_truck' at (32,29)", appearing
                // and clearing again seconds later. A squad changes template when
                // it leaves its truck, so for a moment one machine holds
                // 'X_on_heavy_truck' where the other already holds plain 'X' --
                // same men, same place, different name, and a tile comparison
                // cannot see that they are the same thing.
                //
                // Comparing on the mount-agnostic name closes it without loosening
                // anything real: a genuinely absent squad has nothing of its kind
                // anywhere near, whatever it is riding.
                if (KindNear(mineByKind, DesyncDetector.MountAgnostic(f[0]), x, y, MoveTolerance)) continue;

                // Confirmed across THREE consecutive partner reports (~9s), not
                // two. Two was not enough to outlast a spawn landing on one
                // machine before the other.
                if (prev != null && !prev.Contains(key)) continue;
                if (_prev2 != null && !_prev2.Contains(key)) continue;

                // DO NOT SPOIL FOG OF WAR -- enforced at the SOURCE now.
                //
                // Reported: "we need to remove the red box in fog of war, it
                // spoils the enemy locations." The partner only publishes enemies
                // its fog shows the player (see Build), so there is no distance
                // guess here any more; the old 14-tile radius both leaked unseen
                // enemies near a squad and hid ones a garrison could see.

                Phantoms.Add(new Phantom(x, y, f[0]));
            }

            // PERSISTENCE. Asked for directly, and it is also the honest reading:
            // a phantom is a board difference, not an event, so it should stay up
            // until it is actually resolved rather than blinking out on one clean
            // sample. A tile keeps its marker for a few seconds after it stops
            // being reported, which spans the gap between partner updates.
            float tnow = UnityEngine.Time.realtimeSinceStartup;
            foreach (var ph in Phantoms) _seenAt[ph.X * 1000 + ph.Y] = (tnow, ph.Template);
            foreach (var kv in _seenAt)
            {
                if (tnow - kv.Value.at > PhantomLinger) continue;
                int kx = kv.Key / 1000, ky = kv.Key % 1000;
                if (mineTiles.Contains(kv.Key)) continue;           // resolved: we have it now
                if (EnemyNear(mineTiles, kx, ky, MoveTolerance)) continue;
                bool already = false;
                foreach (var ph in Phantoms) if (ph.X == kx && ph.Y == ky) { already = true; break; }
                if (!already) Phantoms.Add(new Phantom(kx, ky, kv.Value.tmpl));
            }
            if (_seenAt.Count > 64)
            {
                var stale = new List<int>();
                foreach (var kv in _seenAt) if (tnow - kv.Value.at > PhantomLinger) stale.Add(kv.Key);
                foreach (var k in stale) _seenAt.Remove(k);
            }

            // Tell the player, once, when this starts -- and say how many and
            // what, because "an enemy you cannot see is shooting you" is exactly
            // the class of thing that must not stay buried in a log file.
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (Phantoms.Count > 0 && (Phantoms.Count != _lastReported || now - _lastReportAt > 30f))
            {
                _lastReported = Phantoms.Count;
                _lastReportAt = now;
                var first = Phantoms[0];
                Plugin.Logger.LogWarning($"[PHANTOM] {Phantoms.Count} enemy position(s) your partner can see are EMPTY here "
                    + $"-- e.g. '{first.Template}' at ({first.X},{first.Y}). Marked on screen.");
                // NO BANNER FOR THE ORDINARY CASE. Asked for directly: "I don't
                // think an orange banner needs to appear every time that
                // happens." The on-screen marker already says it, in the one
                // place it matters -- over the tile. A banner is reserved for a
                // divergence that has REFUSED TO CLEAR, which is no longer spawn
                // lag and is worth interrupting someone for.
                if (now - _firstPhantomAt > PersistentPhantom && now - _lastBannerAt > 60f)
                {
                    _lastBannerAt = now;
                    CoopRunner.ShowDesyncWarning(Phantoms.Count == 1
                        ? $"An enemy your partner can see is still missing from your board after {(int)(now - _firstPhantomAt)}s: {Pretty(first.Template)} at ({first.X},{first.Y})."
                        : $"{Phantoms.Count} enemies your partner can see have been missing from your board for {(int)(now - _firstPhantomAt)}s.");
                }
            }
            if (Phantoms.Count > 0 && _firstPhantomAt < 0f) _firstPhantomAt = UnityEngine.Time.realtimeSinceStartup;
            if (Phantoms.Count == 0) _firstPhantomAt = -999f;

            if (Phantoms.Count == 0 && _lastReported != 0)
            {
                _lastReported = 0;
                Plugin.Logger.LogInfo("[PHANTOM] Boards agree again -- no unseen enemies.");
            }

            _hiddenLastEval = hiddenNow;
            // Present here but fogged, while the partner sees it: log what and where.
            if (hiddenHere.Count > 0 && now - _lastHiddenLogAt > 10f)
            {
                _lastHiddenLogAt = now;
                Plugin.Logger.LogWarning($"[VIS] {hiddenHere.Count} enemy(ies) {(CoopNet.Mode == CoopMode.Guest ? "the host" : "a client")} can see are HIDDEN by fog here: {string.Join(", ", hiddenHere)}");
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[PHANTOM] Recompute failed: {e.Message}"); }
    }

    private static HashSet<int> ParseTiles(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return null;
        var set = new HashSet<int>();
        foreach (var row in payload.Split(';'))
        {
            var f = row.Split('|');
            if (f.Length != 3) continue;
            if (int.TryParse(f[1], out int x) && int.TryParse(f[2], out int y)) set.Add(x * 1000 + y);
        }
        return set;
    }

    internal static string Pretty(string template)
    {
        if (string.IsNullOrEmpty(template)) return "enemy";
        int dot = template.LastIndexOf('.');
        var s = dot >= 0 ? template.Substring(dot + 1) : template;
        return s.Replace('_', ' ');
    }
}
