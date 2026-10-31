using System;
using System.Collections.Generic;
using Il2CppMenace.Tactical;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling

namespace MenaceCoop;

// DO THE TWO MACHINES HAVE THE SAME ENEMIES AT ALL?
//
// Until now the mod ASSUMED they did. A battle cannot be saved in MENACE -- the
// save system only captures the strategy layer -- so the guest cannot be handed
// the host's board. It re-launches the mission locally from the host's mission
// seed and trusts the game to generate an identical enemy force. That is a bet,
// and on the 0.7.11 build it stopped paying:
//
//   host  (12,22) enemy.pirate_outcasts          7 members
//   guest (12,22) enemy.pirate_flamethrower_team 3 members
//   host  (28,18) enemy.pirate_flamethrower_team 3 members
//   guest (28,18) enemy.pirate_scavengers        7 members
//
// Two different armies, playing one match. Of six enemy squads exactly ONE had
// the same identity on both machines; the host had units at (10,25) and (7,25)
// the guest did not have at all, and the guest had two the host did not. A
// player reported it as "a basic scavenger to the Client was a flamethrower to
// the Host, resulting in a squad's death" -- they fought the unit they could
// see, and it was not the unit that shot back.
//
// The existing roster repair could never fix this. It matches squad members by
// squaddie id and only corrects HITPOINTS; squads generated separately share no
// ids, so every element missed and the whole match logged the same three
// ROSTER DIFFERS lines while reporting "0 position(s), 0 HP/AP, 0 REFUSED". It
// had been describing the problem accurately for the entire session and was
// structurally incapable of acting on it.
//
// This class does not repair anything either -- rebuilding one machine's enemy
// force to match the other's is not something the engine exposes. What it does
// is NOTICE, at the start of the battle, before anybody has died for it, and say
// which units disagree.
internal static class BattleRoster
{
    // The host's manifest, kept so a late-arriving guest board can still be
    // compared once it exists.
    private static string _pending;
    private static bool _checkedThisBattle;
    private static IntPtr _lastTm;

    internal static bool Mismatched { get; private set; }

    internal static void Reset()
    {
        _pending = null;
        _checkedThisBattle = false;
        Mismatched = false;
    }

    // One line per AI unit: template|x|y|livingMembers, sorted so the two
    // machines produce identical text for identical boards. Sorting matters --
    // actor enumeration order is not guaranteed to match, and comparing unsorted
    // lists would report a difference for two identical armies.
    internal static string Build(TacticalManager tm)
    {
        var rows = new List<string>();
        try
        {
            int playerFac = -1;
            try { playerFac = tm.GetPlayerFaction()?.GetIndex() ?? -1; } catch { }

            var actors = DesyncDetector.AllActors(tm);
            for (int i = 0; i < actors.Length; i++)
            {
                var a = actors[i];
                if (a == null) continue;
                try
                {
                    // Player squads are deployed from the host's captured
                    // deployment and are already replicated; only the AI force is
                    // generated independently on each machine.
                    //
                    // FACTION ID IS NOT A RELIABLE TEST FOR "IS THIS ONE OF OURS".
                    //
                    // The engine moves a DOWNED unit out of the player faction
                    // into an orphan faction, so a squad that goes down stops
                    // looking like a player unit and starts counting as an enemy.
                    // Squads go down a moment apart on the two machines, so the
                    // counts then disagree and the check reports "you are not
                    // fighting the same army" over one of your own casualties --
                    // observed live as "player_squad.darby: host 1, you 0".
                    //
                    // Ownership tracks player units by identity and survives the
                    // faction move, which is exactly what this needs.
                    if (a.GetFactionID() == playerFac) continue;
                    if (Ownership.IsKnownPlayerUnit(a)) continue;

                    // Death timing legitimately differs by a moment between
                    // machines. A corpse is not a roster difference.
                    if (a.IsDying()) continue;
                    // ...and IsDying() is false again once a death has COMPLETED,
                    // while the actor can linger in the list with nobody left.
                    // Run 50 flagged "only HERE: chaingun_team|20|27|0" -- a squad
                    // of zero, dead on both machines. Nobody living, not on the board.
                    try
                    {
                        var els = a.GetElements();
                        bool anyAlive = false;
                        for (int e = 0; els != null && e < els.Count; e++)
                            if (els[e] != null && els[e].IsAlive()) { anyAlive = true; break; }
                        if (!anyAlive) continue;
                    }
                    catch { }

                    // FRIENDLIES ARE NOT PART OF THE ENEMY ARMY.
                    //
                    // Manual testing raised "SAME ARMY, DIFFERENT PLACEMENT --
                    // abandon this mission" three times at round 1 of a mission
                    // that then played cleanly to the end. Enemy manifests and
                    // terrain both matched; the only rows that differed were a
                    // six-man allied local-forces garrison -- at (31,19) on one
                    // machine and (10,41) on the other -- i.e. two garrison squads
                    // with a different one inside a building on each side at the
                    // moment of the check. Garrisons and civilians are driven by
                    // the host and moved independently of the enemy force this
                    // check exists to compare, so they are left out of it.
                    if (!PhantomWatch.IsHostile(Menace.Tactical.TacticalManager.Get(), a)) continue;

                    // A UNIT IN A VEHICLE IS STILL A UNIT.
                    //
                    // Passengers have no tile of their own, and skipping them
                    // meant an enemy transport's crew simply vanished from the
                    // manifest -- so a squad riding on one machine and standing
                    // on the other compared as equal, and a whole truckload could
                    // differ unnoticed. The reported mission had exactly that:
                    // enemy.pirate_outcasts_on_heavy_truck.
                    //
                    // Recording them at a sentinel position keeps them counted and
                    // makes a boarding difference visible: same units aboard on
                    // both machines still matches, one aboard and one afoot does
                    // not -- which is a real divergence, not a false alarm.
                    var tile = a.GetLastTile();
                    string px = "-1", py = "-1";
                    if (tile != null) { var p = tile.GetTilePos(); px = p.x.ToString(); py = p.y.ToString(); }
                    else
                    {
                        // Any container counts as aboard. Restricting this to
                        // towers dropped every VEHICLE passenger from the
                        // manifest, so a truck full of pirates compared as an
                        // empty tile on both sides -- a divergence the roster
                        // check could never see.
                        bool aboard = ContainerSync.IsContained(a);
                        if (!aboard) continue;      // no tile and not aboard: not on the board at all
                        // WHICH container (BUGS #75, user 2026-10-08): enemies
                        // in watchtowers/bunkers stood in different places per
                        // machine once revealed, and a bare "aboard" sentinel
                        // compared tower A on one side and tower B on the other
                        // as a MATCH. Record the container's tile: "in21|25".
                        try
                        {
                            var ct = a.GetContainerEntity()?.GetTile()?.GetTilePos();
                            if (ct != null) { px = "in" + ct.Value.x; py = ct.Value.y.ToString(); }
                        }
                        catch { }
                    }

                    int members = 0;
                    try { members = a.GetElements()?.Count ?? 0; } catch { }

                    rows.Add(DesyncDetector.TemplateNameOf(a) + "|" + px + "|" + py + "|" + members);
                }
                catch { }
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[ROSTER] Build failed: " + e.Message); }

        rows.Sort(StringComparer.Ordinal);
        if (BugProbes.On) ProbeVanished(tm, rows);
        return BattleId(tm) + "#" + string.Join(";", rows);
    }

    // WHICH BATTLE IS THIS? THE COMPARISON IS MEANINGLESS WITHOUT IT.
    //
    // The first time these checks caught something, the report read "34.5% of
    // tiles differ" and "the two machines are NOT fighting the same army" -- and
    // both were true but neither was the fault. The guest had never loaded into
    // the second battle at all, so a host board was being compared against a
    // guest board from the PREVIOUS mission.
    //
    // A player told their maps differ when the real problem is that their
    // partner never joined has been sent chasing the wrong thing entirely. The
    // mission identity travels with the manifest so that case names itself.
    internal static string BattleId(TacticalManager tm)
    {
        try
        {
            var m = tm?.GetMission();
            if (m == null) return "?";
            return m.GetIdx() + "/" + m.GetSeed();
        }
        catch { return "?"; }
    }

    // Returns true when the two payloads describe different battles, in which
    // case comparing their contents tells you nothing worth knowing.
    internal static bool DifferentBattle(string a, string b, out string idA, out string idB)
    {
        idA = IdOf(a); idB = IdOf(b);
        return idA != "?" && idB != "?" && idA != idB;
    }

    private static string IdOf(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return "?";
        int h = payload.IndexOf('#');
        return h < 0 ? "?" : payload.Substring(0, h);
    }

    // Host side: publish once the guest has confirmed it is in the battle. Sent
    // then rather than at the host's own battle start because the guest's board
    // does not exist yet at that point -- a manifest that arrives before there is
    // anything to compare against is dropped for nothing.
    internal static void Publish(TacticalManager tm)
    {
        if (!Plugin.CheckEnemyRoster.Value) return;
        if (tm == null) return;
        try
        {
            string manifest = Build(tm);
            CoopNet.Send(new CoopMsg { T = "roster", Data = manifest });
            Plugin.Logger.LogInfo("[ROSTER] Sent enemy manifest: " + CountOf(manifest) + " unit(s)");
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[ROSTER] Publish failed: " + e.Message); }
    }

    internal static void OnRemote(string manifest)
    {
        if (!Plugin.CheckEnemyRoster.Value) return;
        _pending = manifest ?? "";
        _checkedThisBattle = false;
        TryCompare();
    }

    // Runs from the per-frame tick as well as on arrival: the manifest can beat
    // the guest's board into existence by a frame or two.
    internal static void Tick()
    {
        PublishPerBattle();
        if (_pending == null || _checkedThisBattle) return;
        TryCompare();
    }

    // EVERY BATTLE, NOT JUST THE FIRST ONE.
    //
    // Publishing hung off the guest's "ready" handshake, which fires once when
    // the partner joins the session -- so the check ran on the first battle of a
    // session and never again. A pair playing a second mission got no roster
    // check at all, which is precisely the situation the reporting players were
    // in: theirs was mission idx=2, well into a session.
    //
    // Driving it from the battle itself is what makes it per-battle: a new
    // TacticalManager IS a new battle.
    private static IntPtr _publishedFor;

    // ONCE AT THE START IS NOT ENOUGH WHEN THE ENEMY BRINGS FRIENDS.
    //
    // The check ran exactly once per battle, on the force that was standing there
    // at the beginning. Missions that send REINFORCEMENTS mid-battle -- the
    // reported pirate mission had a truckload arrive around round four -- could
    // therefore diverge on every unit that showed up after the first activation
    // and nothing would ever look.
    //
    // The full manifest can only be compared before anything moves, but the
    // template comparison (see LateCheck) is movement-proof, so republishing on
    // an interval costs about a kilobyte a minute and covers the rest of the
    // battle.
    private const float RepublishSeconds = 20f;
    private static float _lastPublishAt;

    private static void PublishPerBattle()
    {
        if (CoopNet.Mode != CoopMode.Host || !CoopNet.Connected) return;
        if (!SessionSync.PartnerBattleConfirmed) return;
        var tm = TacticalManager.Get();
        if (tm == null) return;

        float now = UnityEngine.Time.realtimeSinceStartup;

        if (tm.Pointer != _publishedFor)
        {
            // First publish of a battle still waits for the board to settle, so
            // the strict start-of-battle comparison has a clean pair to work on.
            if (!BoardSettled(tm)) return;
            _publishedFor = tm.Pointer;
            _lastPublishAt = now;
            Publish(tm);
            return;
        }

        if (now - _lastPublishAt < RepublishSeconds) return;
        _lastPublishAt = now;
        Publish(tm);
    }

    // WAIT FOR THE BOARD TO FINISH SPAWNING.
    //
    // Units arrive over several frames, so a manifest taken the instant the
    // TacticalManager exists describes a board that is still filling up. The
    // first attempt at this guarded on the payload being non-empty, which
    // stopped working the moment the battle id was prefixed to it -- the string
    // was then never empty, the host published on frame one, and it shipped a
    // manifest of ONE unit against the guest's ten. The report blamed the
    // rosters; the rosters were fine.
    //
    // Counting rows and requiring the count to hold still is the honest test:
    // it is the thing that is actually changing while the board loads.
    private static int _lastCount = -1;
    private static float _steadySince;

    internal static bool BoardSettled(TacticalManager tm)
    {
        // BOTH SIDES MUST LOOK AT THE SAME MOMENT.
        //
        // The manifest records where every unit IS, so comparing a host snapshot
        // against a guest snapshot taken later compares two different instants
        // and calls the difference a desync. That is exactly what happened on the
        // first real catch: the guest's half listed squads with x0 living members
        // -- units that had already been killed -- so its snapshot was clearly
        // taken well after the host's, and every position had legitimately
        // changed in between.
        //
        // Before the first activation nothing has moved yet, so that is the one
        // instant the two machines can both describe without coordinating.
        if (!AtBattleStart(tm)) return false;

        int n = RowCount(Build(tm));
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (n != _lastCount) { _lastCount = n; _steadySince = now; return false; }
        return n > 0 && now - _steadySince >= 2f;
    }

    internal static bool AtBattleStart(TacticalManager tm)
    {
        try { return tm.GetRound() <= 1; } catch { return false; }
    }

    internal static int RowCount(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return 0;
        int h = payload.IndexOf('#');
        string rows = h < 0 ? payload : payload.Substring(h + 1);
        return rows.Length == 0 ? 0 : rows.Split(';').Length;
    }

    // THE WEAKER CHECK THAT STILL WORKS MID-BATTLE.
    //
    // Compares the multiset of unit TEMPLATES only -- not positions, not member
    // counts, both of which drift honestly once units start moving and dying.
    // "The host has a flamethrower team and you do not" stays true regardless of
    // when it is asked, and that is the failure that killed a player's squad.
    //
    // Confirmed over two samples several seconds apart before it says anything: a
    // unit that dies on one machine a moment before the other is a difference
    // that resolves itself, and reporting it would be the false alarm this whole
    // class of check exists to avoid.
    private static string _lateSuspect;
    private static float _lateSuspectAt;
    private static IntPtr _allClearFor;
    private static IntPtr _reportedFor;
    // Set once the two manifests have been proven identical this battle, so a
    // later disagreement can be reported as drift rather than as bad generation.
    private static IntPtr _generationConfirmedFor;

    // PROBE 6: WHY DID A UNIT LEAVE THE MANIFEST WITHOUT DYING?
    // An emptied pirate truck drops out of the host's roster with no Die() call
    // at all (probe 5 was silent). Remember every hostile actor once counted and,
    // the first time one stops being counted, say which filter it now fails.
    private static readonly Dictionary<IntPtr, Actor> _counted = new();
    private static readonly HashSet<IntPtr> _vanishReported = new();
    private static IntPtr _probeBattle;

    private static void ProbeVanished(TacticalManager tm, List<string> rows)
    {
        try
        {
            if (_probeBattle != tm.Pointer) { _probeBattle = tm.Pointer; _counted.Clear(); _vanishReported.Clear(); }
            var inList = new HashSet<IntPtr>();
            foreach (var a in DesyncDetector.AllActors(tm)) if (a != null) inList.Add(a.Pointer);

            var nowCounted = new HashSet<IntPtr>();
            foreach (var a in DesyncDetector.AllActors(tm))
            {
                if (a == null) continue;
                try
                {
                    if (Ownership.IsKnownPlayerUnit(a) || !PhantomWatch.IsHostile(tm, a) || a.IsDying()) continue;
                    if (a.GetLastTile() == null && !ContainerSync.IsContained(a)) continue;
                    nowCounted.Add(a.Pointer);
                    _counted[a.Pointer] = a;
                }
                catch { }
            }
            foreach (var kv in _counted)
            {
                if (nowCounted.Contains(kv.Key) || !_vanishReported.Add(kv.Key)) continue;
                var a = kv.Value;
                string why;
                try
                {
                    var t = a.GetLastTile()?.GetTilePos();
                    why = $"inActorLists={inList.Contains(kv.Key)} dying={a.IsDying()} hostile={PhantomWatch.IsHostile(tm, a)} "
                        + $"fac={a.GetFactionID()} tile={(t == null ? "null" : $"({t.Value.x},{t.Value.y})")} "
                        + $"contained={ContainerSync.IsContained(a)} els={a.GetElements()?.Count ?? -1} "
                        // HUD visibility candidates (BUGS.md #13: the emptied truck keeps
                        // its health bar on the guest only).
                        + $"detectedByPlayer={a.IsDetectedByFaction(tm.GetPlayerFaction().GetIndex())} discoveredByPlayer={a.IsDiscovered(tm.GetPlayerFaction().GetIndex())}";
                }
                catch (Exception e) { why = "unreadable: " + e.GetType().Name; }
                Plugin.Logger.LogWarning($"[PROBE:{(CoopNet.Mode == CoopMode.Host ? "H" : "G")}] VANISHED from roster '{DesyncDetector.TemplateNameOf(a)}' {why}");
            }
        }
        catch { }
    }

    private static void LogRowDiff(string host, string mine)
    {
        try
        {
            static List<string> Rows(string p)
            {
                int h = p?.IndexOf('#') ?? -1;
                var r = new List<string>();
                if (h < 0) return r;
                foreach (var x in p.Substring(h + 1).Split(';')) if (x.Length > 0) r.Add(x);
                return r;
            }
            var hr = Rows(host); var mr = Rows(mine);
            var onlyHost = new List<string>(hr); foreach (var x in mr) onlyHost.Remove(x);
            var onlyMine = new List<string>(mr); foreach (var x in hr) onlyMine.Remove(x);
            foreach (var x in onlyHost) Plugin.Logger.LogError("[ROSTER]   only on HOST: " + x);
            foreach (var x in onlyMine) Plugin.Logger.LogError("[ROSTER]   only HERE:    " + x);
        }
        catch { }
    }

    private static void LateCheck(TacticalManager tm)
    {
        string theirs = _pending;
        if (string.IsNullOrEmpty(theirs)) return;

        var mineSet = TemplateCounts(Build(tm));
        var theirSet = TemplateCounts(theirs);
        if (mineSet.Count == 0) return;

        var diffs = new List<string>();
        foreach (var kv in theirSet)
        {
            mineSet.TryGetValue(kv.Key, out int n);
            if (n != kv.Value) diffs.Add($"{kv.Key}: host {kv.Value}, you {n}");
        }
        foreach (var kv in mineSet)
            if (!theirSet.ContainsKey(kv.Key)) diffs.Add($"{kv.Key}: host 0, you {kv.Value}");

        float now = UnityEngine.Time.realtimeSinceStartup;

        if (diffs.Count == 0)
        {
            _pending = null;
            _checkedThisBattle = true;
            _lateSuspect = null;
            // Said once. This now runs every republish, and a clean board
            // announcing itself every twenty seconds is noise that buries the
            // one line that matters.
            if (_allClearFor != tm.Pointer)
            {
                _allClearFor = tm.Pointer;
                Plugin.Logger.LogWarning("[ROSTER] Compared after the fighting started: both machines have the same unit types -- no army mismatch");
            }
            return;
        }

        diffs.Sort(StringComparer.Ordinal);
        string sig = string.Join(";", diffs);

        if (sig != _lateSuspect) { _lateSuspect = sig; _lateSuspectAt = now; return; }
        // Raised from 6s. Six seconds was not enough to outlast a squad dying a
        // beat later on one machine, and the whole point of this check is that a
        // false "you are not fighting the same army" is worse than a late one.
        if (now - _lateSuspectAt < 20f) return;

        _pending = null;
        _checkedThisBattle = true;
        Mismatched = true;
        if (_reportedFor == tm.Pointer) return;   // said once per battle, not every republish
        _reportedFor = tm.Pointer;

        // DRIFT IS NOT A GENERATION FAULT, and must not be reported as one.
        //
        // Seen live in v3.14.0: map fingerprints matched, enemy manifests matched
        // five times running (10 units, then 9 as casualties came in), and only
        // then did this check report "pirate_outcasts: host 2, you 1". Generation
        // was demonstrably fine; the two simulations drifted apart mid-fight,
        // which is a damage/death replication problem and a completely different
        // investigation. The old wording ("you are not fighting the same army")
        // would have pointed the next person straight back at the generator.
        bool drifted = _generationConfirmedFor == tm.Pointer;

        Plugin.Logger.LogError(drifted
            ? "[ROSTER] ======= BATTLES HAVE DRIFTED APART (late check) ======="
            : "[ROSTER] ========= ENEMY FORCES DIFFER (late check) =========");
        if (drifted)
        {
            Plugin.Logger.LogError("[ROSTER] The two machines DID generate the same force (manifests matched earlier");
            Plugin.Logger.LogError("[ROSTER] this battle), but their boards no longer agree after " + (int)(now - _lateSuspectAt) + "s:");
        }
        else
        {
            Plugin.Logger.LogError("[ROSTER] Joined too late to compare positions, but the two machines do not even");
            Plugin.Logger.LogError("[ROSTER] have the same unit types on the board, and have not for 20 seconds:");
        }
        foreach (var d in diffs) Plugin.Logger.LogError("[ROSTER]   " + d);
        // WHICH UNIT, WHERE. A count says a truck is missing; the rows say which
        // one and on which tile ("-1,-1" = aboard a transport), which is what
        // tells an unreplicated departure from a death from a boarding split.
        LogRowDiff(theirs, Build(tm));
        Plugin.Logger.LogError(drifted
            ? "[ROSTER] A unit died on one machine and not the other -- damage or death replication,"
            : "[ROSTER] You are not fighting the same army. Abandon and relaunch this mission.");
        if (drifted)
            Plugin.Logger.LogError("[ROSTER] NOT map or army generation. Try FIX DESYNC before abandoning.");
        Plugin.Logger.LogError("[ROSTER] ====================================================");

        CoopNet.Status = drifted
            ? "Boards have drifted apart -- try FIX DESYNC"
            : "ENEMY FORCES DIFFER between the two machines";
        // Drift is ORANGE: the resync machinery is already correcting it, so
        // this is a notification, not an instruction (see the banner rules in
        // CoopRunner). A genuinely different army stays RED -- only the player
        // can fix that, by relaunching.
        if (drifted)
            CoopRunner.ShowDesyncWarning(
                "The two boards have drifted apart -- a unit died on one machine and not the other. Resyncing; use FIX DESYNC if it persists.");
        else
            CoopRunner.ShowVersionWarning(
                "ENEMY FORCES DIFFER BETWEEN PLAYERS -- you are not fighting the same army.");
    }

    private static Dictionary<string, int> TemplateCounts(string payload)
    {
        var d = new Dictionary<string, int>();
        if (string.IsNullOrEmpty(payload)) return d;
        int h = payload.IndexOf('#');
        string rows = h < 0 ? payload : payload.Substring(h + 1);
        if (rows.Length == 0) return d;
        foreach (var row in rows.Split(';'))
        {
            var f = row.Split('|');
            if (f.Length != 4) continue;
            d.TryGetValue(f[0], out int n);
            d[f[0]] = n + 1;
        }
        return d;
    }

    private static void TryCompare()
    {
        var tm = TacticalManager.Get();
        if (tm == null) return;
        if (tm.Pointer != _lastTm) { _lastTm = tm.Pointer; Mismatched = false; }

        // The receiver waits too: comparing a settled host manifest against a
        // board still spawning reports a difference that is pure timing.
        if (!BoardSettled(tm))
        {
            // Past the first activation the boards have legitimately diverged --
            // units have moved and died -- so the full manifest cannot be
            // compared. That is not a reason to check NOTHING: a guest that
            // joined late is exactly the case the reporting players were in
            // (their battle was mission idx=2), and giving up there means the
            // one check that would have caught their bug never runs.
            //
            // What survives movement is WHICH UNITS EXIST. Positions and member
            // counts drift legitimately once the fighting starts; a squad type
            // present on one machine and absent on the other does not.
            if (!AtBattleStart(tm)) LateCheck(tm);
            return;
        }
        string mine = Build(tm);

        _checkedThisBattle = true;
        string theirs = _pending;
        _pending = null;

        if (mine == theirs)
        {
            Plugin.Logger.LogInfo("[ROSTER] Enemy manifests MATCH (" + CountOf(mine) + " unit(s)) -- both machines generated the same force");
            // Remember that generation was PROVEN good for this battle. If the
            // boards drift apart later, that is a different fault with a
            // different fix, and saying "you are not fighting the same army"
            // would send the next investigation at the generator we just cleared.
            _generationConfirmedFor = TacticalManager.Get()?.Pointer ?? IntPtr.Zero;
            return;
        }

        Mismatched = true;
        if (DifferentBattle(theirs, mine, out string ba, out string bb))
        {
            NotSameBattle(ba, bb);
            return;
        }
        Report(theirs, mine);
    }

    // Not a desync: the two machines are in different missions entirely.
    internal static void NotSameBattle(string theirs, string mine)
    {
        Plugin.Logger.LogError("[ROSTER] ============ NOT IN THE SAME BATTLE ============");
        Plugin.Logger.LogError("[ROSTER] Partner is in mission " + theirs + ", you are in mission " + mine + " (idx/seed).");
        Plugin.Logger.LogError("[ROSTER] Nothing can be compared across two different missions. The usual cause");
        Plugin.Logger.LogError("[ROSTER] is one player never finishing the load into the new battle.");
        Plugin.Logger.LogError("[ROSTER] ===============================================");
        CoopNet.Status = "NOT IN THE SAME MISSION as your partner -- both return to the strategy screen";
        CoopRunner.ShowVersionWarning("YOU AND YOUR PARTNER ARE IN DIFFERENT MISSIONS -- one of you never finished loading in.");
    }

    // Say exactly which units disagree. A bare "rosters differ" sends whoever
    // reads the log hunting through two 500-line files to reconstruct what this
    // already knows.
    private static void Report(string hostText, string mineText)
    {
        var host = Parse(hostText);
        var mine = Parse(mineText);

        // SAME ARMY, DIFFERENT BOARD is a distinct fault from a different army,
        // and calling it the wrong one sends the next investigation at the wrong
        // subsystem. The v3.13.0 Zeta report was exactly this: every battle
        // matched on unit COUNT and composition (18 groups vs 18, and the same
        // group sizes on both sides) while every coordinate differed -- because
        // the map generator was unsynced, not the army generator. This check
        // keys entries on tile, so identical squads on different tiles read as
        // "host has NOTHING here" and the old wording blamed the roster.
        bool sameComposition = SameMultiset(host, mine);

        // TRANSPORTS THAT UNLOADED ON ONLY ONE MACHINE have a signature of their
        // own, and it is worth naming because the fix is nothing like the fix for
        // a bad army roll. Seen live: host holding
        // "pirate_outcasts_on_heavy_truck x1 + pirate_outcasts x3" on a tile
        // where the guest had "..._on_heavy_truck x1 + pirate_outcasts x1" --
        // same transport, fewer passengers out of it. The tell is a tile where
        // one side has strictly MORE bodies of a template whose mount-agnostic
        // name matches a transport standing on that same tile.
        bool unloadSkew = UnloadSkew(host, mine);

        Plugin.Logger.LogError(unloadSkew
            ? "[ROSTER] ========= TRANSPORTS UNLOADED ON ONE SIDE ONLY ========="
            : sameComposition
            ? "[ROSTER] ============= SAME ARMY, DIFFERENT PLACEMENT ============="
            : "[ROSTER] ================= ENEMY FORCES DIFFER =================");
        Plugin.Logger.LogError("[ROSTER] Host generated " + host.Count + " enemy position(s), this machine generated " + mine.Count + ".");
        if (unloadSkew)
        {
            Plugin.Logger.LogError("[ROSTER] A transport has dropped its passengers on one machine and not the other.");
            Plugin.Logger.LogError("[ROSTER] The armies were generated the same -- this is the container/unload relay,");
            Plugin.Logger.LogError("[ROSTER] not map or army generation. Look for [CONTAINER] lines in both logs.");
        }
        else if (sameComposition)
        {
            Plugin.Logger.LogError("[ROSTER] Both machines generated the SAME enemy force -- same templates, same");
            Plugin.Logger.LogError("[ROSTER] group sizes -- but standing on different tiles. That is a MAP");
            Plugin.Logger.LogError("[ROSTER] divergence, not an army one: check the [TERRAIN] report above.");
            Plugin.Logger.LogError("[ROSTER] Line of sight and every position-addressed order will disagree.");
        }
        else
        {
            Plugin.Logger.LogError("[ROSTER] The two machines are NOT fighting the same army. Damage, deaths and");
            Plugin.Logger.LogError("[ROSTER] line of sight will disagree for the rest of this battle.");
        }

        int shown = 0;
        const int Max = 24;

        // Same tile, different occupant -- the case that gets squads killed,
        // because both players see a unit there and it is not the same unit.
        foreach (var kv in host)
        {
            if (shown >= Max) break;
            if (!mine.TryGetValue(kv.Key, out var m)) continue;
            if (m == kv.Value) continue;
            shown++;
            Plugin.Logger.LogError("[ROSTER]   at (" + kv.Key + "): host has " + kv.Value + ", you have " + m);
        }

        foreach (var kv in host)
        {
            if (shown >= Max) break;
            if (mine.ContainsKey(kv.Key)) continue;
            shown++;
            Plugin.Logger.LogError("[ROSTER]   at (" + kv.Key + "): host has " + kv.Value + ", you have NOTHING");
        }

        foreach (var kv in mine)
        {
            if (shown >= Max) break;
            if (host.ContainsKey(kv.Key)) continue;
            shown++;
            Plugin.Logger.LogError("[ROSTER]   at (" + kv.Key + "): you have " + kv.Value + ", host has NOTHING");
        }

        if (shown >= Max) Plugin.Logger.LogError("[ROSTER]   ... listing capped at " + Max);
        Plugin.Logger.LogError("[ROSTER] Abandon this mission and relaunch it -- it cannot be played fairly.");
        Plugin.Logger.LogError("[ROSTER] =======================================================");

        CoopNet.Status = unloadSkew
            ? "A transport unloaded on one machine only -- try FIX DESYNC"
            : sameComposition
            ? "MAPS DIFFER -- same army, different board"
            : "ENEMY FORCES DIFFER between the two machines";
        // Unload skew is ORANGE for the same reason: the container relay retries
        // and the resync corrects the bodies. Maps or armies actually differing
        // is RED -- the mission has to be relaunched.
        if (unloadSkew)
            CoopRunner.ShowDesyncWarning(
                "A transport unloaded on one machine only -- one of you is facing passengers the other cannot see yet.");
        else
            CoopRunner.ShowVersionWarning(sameComposition
                ? "THE TWO MAPS DIFFER -- you both have the same enemies, but they are standing in different places."
                : "ENEMY FORCES DIFFER BETWEEN PLAYERS -- you are not fighting the same army.");
    }

    // Does any tile hold a transport on BOTH sides while one side has more
    // bodies of that transport's own troop type standing with it? That is a
    // transport that unloaded once and not twice, and it is a container-relay
    // fault rather than anything to do with generation.
    private static bool UnloadSkew(Dictionary<string, string> host, Dictionary<string, string> mine)
    {
        foreach (var kv in host)
        {
            if (!mine.TryGetValue(kv.Key, out var m)) continue;
            if (m == kv.Value) continue;
            string a = kv.Value, b = m;
            // A mounted template present in BOTH descriptions of the same tile.
            if (a.IndexOf("_on_", StringComparison.Ordinal) < 0) continue;
            if (b.IndexOf("_on_", StringComparison.Ordinal) < 0) continue;
            // ...and one side listing more entries than the other, i.e. extra
            // passengers standing about. Entry count is a good enough proxy:
            // the payload lists each squad separately.
            int ca = CountChar(a, '+'), cb = CountChar(b, '+');
            if (ca != cb) return true;
        }
        return false;
    }

    private static int CountChar(string s, char c)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++) if (s[i] == c) n++;
        return n;
    }

    // Do both sides hold the same bag of squads, ignoring WHERE each one stands?
    // Compared as a multiset, so two identical squads at swapped tiles (which the
    // Zeta logs showed for civilian.worker) still count as the same force.
    private static bool SameMultiset(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        var counts = new Dictionary<string, int>();
        foreach (var v in a.Values)
        {
            counts.TryGetValue(v, out int n);
            counts[v] = n + 1;
        }
        foreach (var v in b.Values)
        {
            if (!counts.TryGetValue(v, out int n) || n == 0) return false;
            counts[v] = n - 1;
        }
        foreach (var n in counts.Values) if (n != 0) return false;
        return true;
    }

    // tile -> "template xN"
    private static Dictionary<string, string> Parse(string text)
    {
        var d = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(text)) return d;
        int __h = text.IndexOf('#');
        if (__h >= 0) text = text.Substring(__h + 1);
        foreach (var row in text.Split(';'))
        {
            var f = row.Split('|');
            if (f.Length != 4) continue;
            string key = f[1] + "," + f[2];
            string val = f[0] + " x" + f[3];
            // Several units can share a tile (garrisons, passengers); keep them
            // all rather than letting one silently overwrite another.
            d[key] = d.TryGetValue(key, out var prev) ? prev + " + " + val : val;
        }
        return d;
    }

    private static int CountOf(string manifest) => RowCount(manifest);
}
