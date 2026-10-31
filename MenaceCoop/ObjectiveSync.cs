using System;
using HarmonyLib;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Objectives;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling

namespace MenaceCoop;

// THE TWO PLAYERS WERE GRADED SEPARATELY.
//
// From a completed mission, same battle, same moment:
//
//   HOST   Primary DefendArea            Ongoing -> Completed
//          RetainForces / LowProfile / Collateral / DefendAreaBonus(40%)  Completed
//   GUEST  Primary DefendArea            Ongoing -> Failed
//          every single secondary        Failed
//
// Objectives are not replicated at all -- each machine scores its own board. So
// any divergence in the fight, however small, is amplified into a completely
// different mission outcome: the guest's copy had enemies the host had already
// killed, those enemies walked into the defended zone, and the guest watched
// itself fail a mission the host had just won.
//
// The campaign result comes from the HOST's save, so the guest's screen was not
// merely different, it was WRONG -- it reported a failure that never happened
// and that its own campaign would not record. Fixing the underlying divergence
// is a separate, ongoing job; this makes the scoreboard honest in the meantime,
// which matters because a player who is told they failed stops trusting the mod
// long before they stop trusting the numbers.
//
// Host authority, as everywhere else here: the host relays each state change and
// the guest applies it.
internal static class ObjectiveSync
{
    internal static bool Applying;

    private static readonly System.Collections.Generic.Dictionary<int, ObjectiveState> _sentState = new();
    private static IntPtr _sentBattle;

    internal static void Broadcast(Objective obj, ObjectiveState state)
    {
        if (Applying) return;
        if (!CoopNet.Hosting) return;   // alone too: journaled (#57)
        try
        {
            int idx = IndexOf(obj);
            if (idx < 0) return;
            // ONCE PER CHANGE. The engine re-applies a finished objective's state
            // every update, and each re-application was relayed: run 15 sent
            // "#0 -> Completed" once a second from the moment the capture landed.
            var tm = TacticalManager.Get();
            if (tm != null && tm.Pointer != _sentBattle) { _sentBattle = tm.Pointer; _sentState.Clear(); }
            if (_sentState.TryGetValue(idx, out var last) && last == state) return;
            _sentState[idx] = state;
            CoopNet.Send(new CoopMsg { T = "obj", Prm = idx, MoveFlags = (int)state });
            Plugin.Logger.LogInfo($"[OBJ] Relayed objective #{idx} -> {state} ('{Title(obj)}')");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[OBJ] Broadcast failed: {e.Message}"); }
    }

    internal static void HandleRemote(CoopMsg m)
    {
        // The host is authoritative -- except when replaying its OWN journal to
        // resume a saved battle (BattleSave).
        if (CoopNet.Mode == CoopMode.Host && !BattleSave.Restoring) return;
        try
        {
            var mgr = TacticalManager.Get()?.GetMission()?.Objectives;
            if (mgr == null) return;
            int count = mgr.GetCount(new Il2CppSystem.Nullable<ObjectiveState>());
            if (m.Prm < 0 || m.Prm >= count)
            {
                Plugin.Logger.LogWarning($"[OBJ] Host reported objective #{m.Prm} but this machine has {count}");
                return;
            }
            var obj = mgr.Get(m.Prm);
            if (obj == null) return;
            var want = (ObjectiveState)m.MoveFlags;
            if (obj.GetState() == want) return;

            var had = obj.GetState();
            Applying = true;
            // Events fire so the HUD and the results screen both update -- the
            // whole point is that the player SEES the host's outcome.
            try { obj.SetState(want, true); }
            finally { Applying = false; }
            Plugin.Logger.LogInfo($"[OBJ] Objective #{m.Prm} '{Title(obj)}' corrected {had} -> {want} (host authority)");
        }
        catch (Exception e)
        {
            Applying = false;
            Plugin.Logger.LogWarning($"[OBJ] Apply failed: {e.Message}");
        }
    }

    // Once per objective and outcome: the engine re-asserts a local verdict every
    // tick, and the log should say it happened, not how often.
    private static readonly System.Collections.Generic.HashSet<long> _blockedNoted = new();

    internal static void NoteBlocked(Objective obj, ObjectiveState state)
    {
        try
        {
            long key = obj.Pointer.ToInt64() * 8 + (int)state;
            if (!_blockedNoted.Add(key)) return;
            Plugin.Logger.LogInfo($"[OBJ] Guest's own verdict {state} on '{Title(obj)}' ignored -- waiting for the host");
        }
        catch { }
    }

    private static int IndexOf(Objective obj)
    {
        try
        {
            var mgr = TacticalManager.Get()?.GetMission()?.Objectives;
            if (mgr == null || obj == null) return -1;
            int count = mgr.GetCount(new Il2CppSystem.Nullable<ObjectiveState>());
            for (int i = 0; i < count; i++)
                if (mgr.Get(i)?.Pointer == obj.Pointer) return i;
        }
        catch { }
        return -1;
    }

    private static string Title(Objective o)
    {
        try { return o?.GetTitle() ?? "?"; } catch { return "?"; }
    }
}

[HarmonyPatch]
internal static class ObjectivePatches
{
    // THE GUEST DOES NOT GRADE ITSELF.
    //
    // Relaying the host's changes was not enough, because the guest still scored
    // its own board in parallel. Caught in a verified run: at the end of round 5
    // the GUEST's primary DefendArea ("too many hostile units inside for too
    // long") went Ongoing -> Failed while the host's stayed Ongoing -- enemies
    // stand in the zone a moment longer on the machine that replays them. The
    // guest's engine then tried to leave the battle every turn (47 blocked
    // "guest-side mission end" lines) and showed a failed mission the host was
    // still playing, with nothing to set it back: the host never changed it.
    //
    // Only a DECISION is blocked -- Completed or Failed. Activation and progress
    // still run locally, so the HUD stays live even for changes the host sent
    // while this machine was still loading.
    [HarmonyPatch(typeof(Objective), nameof(Objective.SetState))]
    [HarmonyPrefix]
    private static bool GuestMayNotDecide(Objective __instance, ObjectiveState _state)
    {
        if (!CoopNet.Connected || CoopNet.Mode == CoopMode.Host || ObjectiveSync.Applying) return true;
        if (_state != ObjectiveState.Completed && _state != ObjectiveState.Failed) return true;
        if (TacticalManager.Get() == null) return true;
        ObjectiveSync.NoteBlocked(__instance, _state);
        return false;
    }

    // REGISTER IN Plugin.Load -- an unregistered patch class is silently inert.
    [HarmonyPatch(typeof(Objective), nameof(Objective.SetState))]
    [HarmonyPostfix]
    private static void AfterSetState(Objective __instance, ObjectiveState _state)
    {
        if (!CoopNet.Connected) return;
        ObjectiveSync.Broadcast(__instance, _state);
    }
}
