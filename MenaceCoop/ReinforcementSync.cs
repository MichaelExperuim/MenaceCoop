using System;
using HarmonyLib;
#if MELON
using Il2CppMenace.States;
using Il2CppMenace.Strategy;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.States;
using Menace.Strategy;
using Menace.Tactical;
using Menace.Tactical.AI;
#endif

namespace MenaceCoop;

// MID-BATTLE REINFORCEMENTS.
//
// Reported against v3.13.0, without logs: "immediately after the first pirate
// reinforcements appear on the transport, problems begin. For the guest, half of
// the reinforcement vehicles do not exist; the other half arrives but does not
// unload reinforcements, unlike for the host."
//
// That is two faults with one shape, and both follow from the same architectural
// fact that drives everything else in this mod: A BATTLE CANNOT BE SAVED, so the
// guest never receives the host's board -- it runs its own copy of the same
// simulation. Reinforcements are not relayed; each machine generates its own
// wave locally in ReinforcementGroup.HandleReinforcementGroup, drawing an army
// from PossibleArmies. Two machines drawing independently get two different
// waves, which is "half the vehicles do not exist".
//
// The ones that DO exist then fail to unload because the unload is an AI
// activation -- a relayed order -- and until v3.14.0 an AI unit's identity was
// hashed from its spawn tile. Worse, mid-battle arrivals were only captured on
// ONE spawn path (a skill effect); a mission-driven wave went through none of it
// and got no identity at all until a later sweep guessed one from its tile. An
// order addressed to a vehicle the other machine cannot resolve simply vanishes,
// so the transport sits there full.
//
// Two fixes here, plus the identity work in DesyncDetector:
//   1. Pin the draw, so both machines roll the same wave.
//   2. Capture every arrival at the one door they all come through.
internal static class ReinforcementSync
{
    // Pin the strategy RNG immediately before a reinforcement group is resolved,
    // keyed on values BOTH machines already agree about: the mission seed, the
    // round, and the group's own index. Round-keyed deliberately -- the host can
    // be a round or two ahead of the guest, and a pin that ignored the round
    // would make the two draw the same wave at DIFFERENT times instead of the
    // same wave for the same round.
    internal static void PinDraw(Mission mission, int round, int index)
    {
        if (!Plugin.PinMissionRng.Value || !CoopNet.Connected || mission == null) return;
        try
        {
            var op = StrategyState.Get()?.Operations?.GetCurrentOperation();
            var rng = op?.m_Random;
            if (rng == null) return;
            int seed;
            unchecked { seed = mission.GetSeed() + round * 7919 + index * 104729; }
            rng.Init(seed);

            // PIN THE COMBAT STREAM HERE TOO.
            //
            // With the budget now aligned, the two machines draw waves of the
            // SAME SIZE and still differ in composition -- host 2 chaingun + 3
            // outcast trucks against guest 1 + 4, five units either way. So
            // something in the army SELECTION is reading a stream that
            // Operation.m_Random is not.
            //
            // TacticalManager.s_Random is the obvious suspect: this mod reseeds
            // it on EVERY order, so its position depends on how many orders each
            // machine has processed by now -- which is guaranteed to differ, and
            // differs in exactly the way that would leave counts equal while
            // changing which army came out. Pinning it to the same derived value
            // costs nothing, because the next relayed order reseeds it again
            // immediately afterwards.
            try { Replication.Reseed(seed); }
            catch (Exception re) { Plugin.Logger.LogWarning($"[REINF] Combat-stream pin failed: {re.Message}"); }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[REINF] RNG pin failed: {e.Message}"); }
    }

    // Deliberately loud, because this is being shipped without a log to confirm
    // it against. The next paired run answers the open question outright: if the
    // two sides print the same round/index/strength and still produce different
    // waves, the draw does not use Operation.m_Random and the pin above is
    // aimed at the wrong stream.
    // THE WAVE DRAW MUST NOT READ LOCAL BOARD STATE.
    //
    // Pinning the RNG was necessary and not sufficient, because the draw takes an
    // INPUT that each machine computes for itself: _concurrentEnemyStrength, the
    // strength currently on its own board at its own round transition. The guest
    // replays the host's orders, so its board is always slightly behind -- and
    // therefore its strength reading is always slightly stale. Same seed, same
    // round, same group index, different budget, different army.
    //
    // Measured across every run, identical shape each time:
    //
    //     round 1-2   host 1105   guest 1105     (nothing has died yet)
    //     round 3     host 1025   guest 1105     (host has killed 80 points)
    //     round 4     host  800   guest 1270
    //
    // Chasing this through faster death replication was the wrong track: it is
    // not that the guest MISSES kills, it is that the two machines sample the
    // board at different instants and always will. Three separate attempts at
    // the symptom (composition pin, mapgen pin, death relay) all left this
    // untouched.
    //
    // The host's number is the authoritative one, so the guest uses it. Keyed on
    // (round, group) so a late message cannot be applied to the wrong draw.
    private static readonly System.Collections.Generic.Dictionary<long, int> _hostStrength = new();

    private static long Key(int round, int index) => ((long)round << 8) | (uint)(index & 0xFF);

    internal static void AlignStrength(int round, int index, ref int strength)
    {
        if (!CoopNet.Connected && !CoopNet.Hosting) return;   // host alone still journals (#57)
        try
        {
            if (CoopNet.Mode == CoopMode.Host && BattleSave.Restoring
                && _hostStrength.TryGetValue(Key(round, index), out int saved))
            {
                Plugin.Logger.LogInfo($"[REINF] Resumed battle: r{round} group #{index} budget {strength} -> saved {saved}");
                strength = saved;
            }
            if (CoopNet.Mode == CoopMode.Host)
            {
                // Publish what WE drew with, before drawing with it.
                CoopNet.Send(new CoopMsg { T = "reinfs", MoveFlags = round, Prm = index, Seed = strength });
                return;
            }

            if (_hostStrength.TryGetValue(Key(round, index), out int hostVal))
            {
                if (hostVal != strength)
                {
                    Plugin.Logger.LogInfo($"[REINF] Wave budget aligned to host for r{round} group #{index}: "
                        + $"{strength} -> {hostVal}");
                    strength = hostVal;
                }
                return;
            }

            // The host's figure has not arrived. Drawing on a local number here
            // is what produced two different armies, so say so plainly rather
            // than letting it pass as normal.
            Plugin.Logger.LogWarning($"[REINF] No host budget for r{round} group #{index} yet -- "
                + $"drawing on the local figure ({strength}); this wave may differ from the host's");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[REINF] Strength align failed: {e.Message}"); }
    }

    internal static void HandleRemoteStrength(CoopMsg m)
    {
        // The host normally draws with its own board; a resumed battle (BattleSave)
        // replays the budgets it journaled, so the same waves come back.
        if (CoopNet.Mode == CoopMode.Host && !BattleSave.Restoring) return;
        try { _hostStrength[Key(m.MoveFlags, m.Prm)] = m.Seed; } catch { }
    }

    internal static void ResetStrength() { _hostStrength.Clear(); }

    internal static void Report(int round, int index, int strength, float mult, int result)
    {
        if (!CoopNet.Connected) return;
        Plugin.Logger.LogInfo($"[REINF] group #{index} round={round} concurrentStrength={strength} "
            + $"mult={mult:0.##} -> {result}  ({(CoopNet.Mode == CoopMode.Host ? "H" : "G")})");
    }
}

[HarmonyPatch]
internal static class ReinforcementPatches
{
    // REGISTER IN Plugin.Load -- an unregistered patch class is silently inert.
    [HarmonyPatch(typeof(Menace.Strategy.Missions.ReinforcementGroup),
                  nameof(Menace.Strategy.Missions.ReinforcementGroup.HandleReinforcementGroup))]
    [HarmonyPrefix]
    private static void BeforeGroup(int _index, Mission _mission, int _round,
                                    ref int _concurrentEnemyStrength)
    {
        ReinforcementSync.PinDraw(_mission, _round, _index);
        ReinforcementSync.AlignStrength(_round, _index, ref _concurrentEnemyStrength);
    }

    [HarmonyPatch(typeof(Menace.Strategy.Missions.ReinforcementGroup),
                  nameof(Menace.Strategy.Missions.ReinforcementGroup.HandleReinforcementGroup))]
    [HarmonyPostfix]
    private static void AfterGroup(int _index, int _round, int _concurrentEnemyStrength,
                                   float _maxConcurrentEnemyStrengthMult, int __result)
    {
        ReinforcementSync.Report(_round, _index, _concurrentEnemyStrength,
                                 _maxConcurrentEnemyStrengthMult, __result);
    }

    // EVERY unit that joins a faction, from any spawn path. This is the capture
    // the old skill-effect-only hook was missing, and it is why mission-driven
    // reinforcement waves had no cross-machine identity at all.
    [HarmonyPatch(typeof(BaseFaction), nameof(BaseFaction.AddActor))]
    [HarmonyPostfix]
    private static void ActorJoinedFaction(Actor _actor)
    {
        if (!CoopNet.Connected) return;
        DesyncDetector.CaptureArrival(_actor);
    }

    // IDENTIFY A UNIT BEFORE IT DISAPPEARS INSIDE SOMETHING.
    //
    // A garrisoned or embarked actor leaves its faction's actor list and has no
    // tile, so it is reachable by neither of the two ways orders are addressed.
    // Once inside, it cannot be given an id at all -- which is why an enemy
    // entering a structure took every subsequent order for it down with it.
    //
    // The entry event is the last moment the unit is still an ordinary actor on
    // the board, so that is where identity has to be nailed down. Cheap, exact,
    // and far better than sweeping the map hunting for occupied structures.
    [HarmonyPatch(typeof(Actor), nameof(Actor.OnEnteredContainer))]
    [HarmonyPrefix]
    private static void BeforeEnteringContainer(Actor __instance)
    {
        if (!CoopNet.Connected) return;
        try
        {
            var tm = TacticalManager.Get();
            if (tm == null || __instance == null) return;
            if (DesyncDetector.CoopIdOf(__instance) >= 0) return;   // already known
            if (DesyncDetector.RosterIndexOf(__instance) >= 0) return; // players use roster index
            DesyncDetector.EnsureCoopIds(tm);
            if (DesyncDetector.CoopIdOf(__instance) < 0)
                DesyncDetector.CaptureArrival(__instance);
            Plugin.Logger.LogInfo($"[SYNC] Identified '{DesyncDetector.TemplateNameOf(__instance)}' "
                + $"before it entered a container -> coop id {DesyncDetector.CoopIdOf(__instance)}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[SYNC] Pre-container capture failed: {e.Message}"); }
    }
}
