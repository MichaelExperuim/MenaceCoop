using System;
using System.Collections.Generic;
using HarmonyLib;
#if MELON
using Il2CppMenace.Tactical;
#else
using Menace.Tactical;
#endif

namespace MenaceCoop;

// A FLEEING ENEMY'S ESCAPE IS THE HOST'S DECISION (BUGS.md #37, r76).
//
// A routed squad's flight is run by each machine's engine on its own, from
// that machine's morale state: it walks the squad toward the edge and removes
// it with Actor.Die -- an ESCAPE, not a kill: no element dies, so none of the
// damage/death relays (ehp/edie) ever see it. And the two machines' morale
// states part at the enemy's turn start, which only the host runs: r76 14:15:03
// the host's squad recovered (morale 0 -> 1, Fleeing -> Wavering) and stayed
// put; the guest's stayed Fleeing, fled to (31,29) and was removed a few
// seconds later. One enemy squad fewer on the guest for the rest of the mission
// ("BATTLES HAVE DRIFTED APART"), which the snapshot cannot undo.
//
// So an escape is a CAUSE and it is relayed like one:
//   host  -- when its engine removes a fleeing enemy without killing it, send
//            "flee"; the guest removes the same actor.
//   guest -- never removes a fleeing enemy on its own (held and logged); it
//            waits for the host's "flee".
// Kills are left alone: a removal in the frame an element died is a death,
// and the existing relays own it. Player units are out of scope here -- their
// owner decides their morale -- and are never held.
internal static class FleeSync
{
    // Last frame an element of this actor died: a Die in that frame is a kill.
    internal static readonly Dictionary<IntPtr, int> KillFrame = new();

    internal static bool IsEscape(Actor a)
    {
        if (a == null) return false;
        try
        {
            if (Ownership.Active && Ownership.IsKnownPlayerUnit(a)) return false;
            if (a.GetMoraleState() != MoraleState.Fleeing) return false;
            if (KillFrame.TryGetValue(a.Pointer, out int f) && UnityEngine.Time.frameCount - f <= 1) return false;
            return true;
        }
        catch { return false; }
    }

    internal static void SendEscape(Actor a)
    {
        var tile = a.GetLastTile();
        var p = tile?.GetTilePos();
        var msg = new CoopMsg { T = "flee", AX = p?.x ?? -1, AY = p?.y ?? -1 };
        Replication.StampIds(msg, a, isOrder: false);
        CoopNet.Send(msg);
        Plugin.Logger.LogInfo($"[FLEE] '{DesyncDetector.TemplateNameOf(a)}' escaped at ({msg.AX},{msg.AY}) -> partner (cid={msg.CId})");
    }

    // THE MORALE STATE IS A CAUSE, TOO (r83 16:30:31). The host's round start
    // recovered a fleeing squad (Fleeing -> Wavering) and relayed the new morale
    // VALUE; SetMorale on the guest moved the number but not the STATE, and the
    // guest's own round start -- 4 ms after the relay -- put it back to 0. The
    // guest's copy stayed Fleeing, its engine ran the flight and removed the
    // squad on the guest only. So the host relays every AI morale-STATE change
    // and the guest applies it as the engine does (recorded state + the engine's
    // own state-changed event), and re-asserts it after its own round start.
    // The state is DERIVED from the morale number (r84: state set to Wavering
    // while the guest's morale was still 0 -> "now reads Fleeing"), so the host
    // sends both and the guest sets the number first.
    internal static readonly Dictionary<IntPtr, (MoraleState s, float morale)> HostState = new();

    // ON since 2026-10-03 (user: the host decides morale). Was off in v4.0.0:
    // one partial run (r84) showed the state alone does not stick, so the host
    // sends morale + state and clients set the number first.
    internal const bool StateRelayEnabled = true;

    // A PLAYER UNIT'S MORALE STATE IS ITS OWNER'S (BUGS.md #97, r150 21:07).
    // Player units were left out of this relay ("their owner decides"), but the
    // owner never sent anything: the host's Pike fled on all three machines (each
    // engine derived it from the relayed hits), then RALLIED at its own turn end
    // on the host only (morale 0 -> 5, faction 2 -> 1). The clients kept it
    // fleeing in faction 2, outside their player roster, and the snapshot's
    // morale correction then threw inside the engine's SetFaction. So the owner
    // relays its own units' state changes, with the faction the engine has just
    // put them in (MoveFlags = faction + 1; 0 = not sent, AI relays).
    internal static void SendState(Actor a, MoraleState s, bool withFaction = false)
    {
        var p = a.GetLastTile()?.GetTilePos();
        var msg = new CoopMsg { T = "mstate", Prm = (int)s, Seed = (int)Math.Round(a.GetMorale()), AX = p?.x ?? -1, AY = p?.y ?? -1 };
        if (withFaction) msg.MoveFlags = a.GetFactionID() + 1;
        Replication.StampIds(msg, a, isOrder: false);
        CoopNet.Send(msg);
        if (BugProbes.On || withFaction)
            Plugin.Logger.LogInfo($"[FLEE] Sent '{DesyncDetector.TemplateNameOf(a)}' state {s} morale {msg.Seed}{(withFaction ? $" faction {msg.MoveFlags - 1} (owner)" : "")} (cid={msg.CId})");
    }

    internal static void HandleState(CoopMsg m)
    {
        var a = Replication.ResolveOrderActor(m);
        if (a == null)
        {
            Plugin.Logger.LogInfo($"[FLEE] Partner's morale state {(MoraleState)m.Prm} for cid={m.CId} at ({m.AX},{m.AY}): no such unit here");
            return;
        }
        var s = (MoraleState)m.Prm;
        HostState[a.Pointer] = (s, m.Seed);
        // The faction FIRST: the r150 exception came from the engine's own
        // faction switch, run from inside SetMorale when the number crossed back
        // over the fleeing threshold. With the unit already home, there is
        // nothing left for that switch to do.
        if (m.MoveFlags > 0) ApplyFaction(a, m.MoveFlags - 1, s == MoraleState.Fleeing);
        Apply(a, s, m.Seed, m.MoveFlags > 0 ? "owner" : "host");
        // AND AGAIN AFTER (r156 08:45:41, round 6 hang). The host broke its Pike
        // (morale 77 -> 0): its engine made it Wavering and kept it in faction 1.
        // On both clients SetMorale(0) ran the engine's flee switch instead --
        // faction 1 -> 2 AFTER the faction step above had found nothing to do.
        // M2 then saw no host unit left in the player phase, ended it and waited
        // in faction 2 for a Pike that the host was still to move in faction 1.
        // The owner's faction is the truth; put it back once the number is in.
        if (m.MoveFlags > 0) ApplyFaction(a, m.MoveFlags - 1, s == MoraleState.Fleeing);
    }

    // Fleeing goes through the engine's own SetFactionFleeing, which is what the
    // owner's engine ran and presumably what lets the unit find its way home.
    private static void ApplyFaction(Actor a, int fac, bool fleeing)
    {
        int before;
        try { before = a.GetFactionID(); } catch { return; }
        if (before == fac) return;
        bool agentNull = false;
        try { agentNull = a.GetAgent() == null; } catch { }
        bool prev = Replication.ApplyingRemote;
        Replication.ApplyingRemote = true;
        string err = null;
        FactionSync.ChangedFired = false;
        try { if (fleeing) a.SetFactionFleeing(fac); else a.SetFaction(fac); }
        catch (Exception e) { err = e.GetType().Name; }
        finally { Replication.ApplyingRemote = prev; }
        int after = -1;
        try { after = a.GetFactionID(); } catch { }
        try { if (after == fac && !FactionSync.ChangedFired) a.OnFactionChanged(); } catch { }
        string line = $"[FLEE] '{DesyncDetector.TemplateNameOf(a)}' faction {before} -> {fac} (owner's): now {after}"
            + $"{(err != null ? $", SetFaction threw {err}" : "")} agent={(agentNull ? "null" : "set")}";
        if (after == fac) Plugin.Logger.LogInfo(line);
        else Plugin.Logger.LogWarning(line);
    }

    // Make this machine's copy hold the host's morale and state, if it does not.
    internal static void Apply(Actor a, MoraleState s, float morale, string why)
    {
        try
        {
            if (a == null || a.IsDying()) return;
            bool moraleOff = Math.Abs(a.GetMorale() - morale) >= 1f;
            if (!moraleOff && a.GetMoraleState() == s && a.m_LastMoraleState == s) return;
            var before = a.GetMoraleState();
            float mBefore = a.GetMorale();
            bool prev = Replication.ApplyingRemote;
            Replication.ApplyingRemote = true;
            try
            {
                if (moraleOff) a.SetMorale(morale);
                if (a.m_LastMoraleState != s || before != s)
                {
                    a.m_LastMoraleState = s;
                    TacticalManager.Get()?.InvokeOnMoraleStateChanged(a, s);
                }
            }
            finally { Replication.ApplyingRemote = prev; }
            Plugin.Logger.LogInfo($"[FLEE] '{DesyncDetector.TemplateNameOf(a)}' morale {mBefore:F0} -> {a.GetMorale():F0}, state {before} -> {s} ({why}); now reads {a.GetMoraleState()}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[FLEE] Applying morale state failed: {e.Message}"); }
    }

    internal static void HandleRemote(CoopMsg m)
    {
        var a = Replication.ResolveOrderActor(m);
        if (a == null)
        {
            Plugin.Logger.LogInfo($"[FLEE] Host's escape at ({m.AX},{m.AY}) cid={m.CId}: not on this board (already gone)");
            return;
        }
        bool prev = Replication.ApplyingRemote;
        Replication.ApplyingRemote = true;
        try
        {
            if (!a.IsDying()) a.Die(false);
            // A COLD Die() LEAVES A ZOMBIE (r146 15:23:09: a fleeing warrior
            // escaped on the host; on both clients Die ran three times and the
            // actor ended not alive, 0 members, but NOT dying -- the host's engine
            // flight sets that flag, the bare call does not). Hash and snapshot
            // both count it as standing at 0 HP, a mismatch no correction can
            // remove. Same fallback as Replication.MarkDeadIfNoLivingElements.
            bool flagged = false;
            try { if (!a.IsDying()) { a.m_IsDying = true; flagged = true; } } catch { }
            Plugin.Logger.LogInfo($"[FLEE] '{DesyncDetector.TemplateNameOf(a)}' escaped (host's decision) -- removed here{(flagged ? " (dying flag set by hand)" : "")}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[FLEE] Applying the host's escape failed: {e.Message}"); }
        finally { Replication.ApplyingRemote = prev; }
    }
}

[HarmonyPatch]
internal static class FleeSyncPatches
{
    // Host: every AI morale-state change the host's engine makes goes to the guest.
    [HarmonyPatch(typeof(TacticalManager), nameof(TacticalManager.InvokeOnMoraleStateChanged))]
    [HarmonyPostfix]
    private static void RelayState(Actor _actor, MoraleState _moraleState)
    {
        if (!FleeSync.StateRelayEnabled) return;
        // NOT skipped under ApplyingRemote: a client's hit applied here (its esup)
        // changes the host's morale state too, and the host decides AI morale --
        // clients never send mstate, so this cannot echo (r118).
        if (!CoopNet.Connected || _actor == null) return;
        try
        {
            // #97: a player unit's state goes out from its OWNER, host or client.
            // Receivers never send for a unit they do not own, so no echo.
            if (Ownership.Active && Ownership.IsKnownPlayerUnit(_actor))
            {
                if (Ownership.IsLocal(_actor)) FleeSync.SendState(_actor, _moraleState, withFaction: true);
                return;
            }
            if (CoopNet.Mode != CoopMode.Host) return;
            FleeSync.SendState(_actor, _moraleState);
        }
        catch { }
    }

    // Guest: after its own round/turn start (which re-derives morale locally and
    // raced the host's relay in r83), put the host's last state back.
    [HarmonyPatch(typeof(Actor), nameof(Actor.OnRoundStart))]
    [HarmonyPostfix]
    private static void ReassertAfterRoundStart(Actor __instance) => Reassert(__instance, "after round start");

    [HarmonyPatch(typeof(Actor), nameof(Actor.OnTurnStart))]
    [HarmonyPostfix]
    private static void ReassertAfterTurnStart(Actor __instance) => Reassert(__instance, "after turn start");

    private static void Reassert(Actor a, string why)
    {
        if (!FleeSync.StateRelayEnabled) return;
        if (!CoopNet.Connected || CoopNet.Mode != CoopMode.Guest || a == null) return;
        try
        {
            // Only when the STATE disagrees: the stored morale is the host's latest
            // (SuppressionSync refreshes it), but a plain value drift is the
            // snapshot's business, not this.
            if (FleeSync.HostState.TryGetValue(a.Pointer, out var hs)
                && (a.GetMoraleState() != hs.s || a.m_LastMoraleState != hs.s))
                FleeSync.Apply(a, hs.s, hs.morale, why);
        }
        catch { }
    }

    [HarmonyPatch(typeof(Element), nameof(Element.OnDeath))]
    [HarmonyPrefix]
    private static void NoteKill(Element __instance)
    {
        try
        {
            var a = __instance?.GetEntity()?.TryCast<Actor>();
            if (a != null) FleeSync.KillFrame[a.Pointer] = UnityEngine.Time.frameCount;
        }
        catch { }
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.Die))]
    [HarmonyPrefix]
    private static bool EscapeIsHosts(Actor __instance)
    {
        if (!CoopNet.Connected || Replication.ApplyingRemote) return true;
        if (!FleeSync.IsEscape(__instance)) return true;
        if (CoopNet.Mode == CoopMode.Host)
        {
            try { FleeSync.SendEscape(__instance); } catch { }
            return true;
        }
        // THE GUEST MUST NOT BLOCK THIS (r82). The engine has already taken the
        // squad half off the board by the time it calls Die; holding the call
        // left a zombie that the guest's enemy phase waited on forever -- the
        // guest sat in round 9 while the host was in round 10, 40+ minutes. A
        // one-sided escape is a roster drift that FIX DESYNC repairs; a stall is
        // not. So the guest only reports it.
        Plugin.Logger.LogWarning($"[FLEE] '{DesyncDetector.TemplateNameOf(__instance)}' escaped on THIS machine without the host's decision -- expect a roster drift (FIX DESYNC repairs it)");
        return true;
    }
}
