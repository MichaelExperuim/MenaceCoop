using System;
using HarmonyLib;
using Il2CppMenace.Tactical;

namespace MenaceCoop;

// SUPPRESSION WAS NEVER RELAYED, SO THE GUEST'S SUPPRESSIVE FIRE DID NOTHING.
//
// Damage follows the shooter (ehp/edie from the machine that rolled the hit),
// but suppression rode along inside the replayed hit, and the replaying machine
// swallows that hit. Seen 2026-09-25 every other round: the guest pinned an
// enemy team (suppression 30), the host -- which runs the enemy AI and so
// decides what a suppressed unit does -- had 0, and each resync then wiped the
// guest's value ("SUPPRESSION drift 'enemy.pirate_chaingun_team': mine 30 ->
// host 0"). The guest's covering fire was cosmetic.
//
// Same rule as damage: the machine that owns the SUPPRESSOR relays the target's
// resulting suppression, as an absolute value (idempotent, so a machine that did
// also apply it locally just agrees). A player unit's shots belong to its owner;
// AI, hazards and anything unattributed belong to the host.
internal static class SuppressionSync
{
    internal static void HandleStance(CoopMsg m)
    {
        var actor = Replication.ResolveOrderActor(m);
        if (actor == null) return;
        try
        {
            if ((int)actor.GetStance() == m.Prm) return;
            bool prev = Replication.ApplyingRemote;
            Replication.ApplyingRemote = true;
            try { actor.SetStance((ActorStance)m.Prm, true); }
            finally { Replication.ApplyingRemote = prev; }
            Plugin.Logger.LogInfo($"[SUPPRESS] '{DesyncDetector.TemplateNameOf(actor)}' stance -> {(ActorStance)m.Prm} (partner)");
        }
        catch { }
    }

    internal static bool Authoritative(Entity suppressor)
    {
        Actor s = null;
        try { s = suppressor?.TryCast<Actor>(); } catch { }
        if (s != null && Ownership.Active && Ownership.IsKnownPlayerUnit(s)) return Ownership.IsLocal(s);
        return CoopNet.Mode == CoopMode.Host;
    }

    internal static void HandleRemote(CoopMsg m)
    {
        var actor = Replication.ResolveOrderActor(m);
        if (actor == null) return;   // dead or gone here: nothing to pin
        float before, moraleBefore;
        try { before = actor.GetSuppression(); moraleBefore = actor.GetMorale(); } catch { return; }
        bool supDiff = Math.Abs(before - m.Prm) >= 1f;
        bool morDiff = m.Seed >= 0 && Math.Abs(moraleBefore - m.Seed) >= 1f;
        // Host's morale state rides along (see Send): apply number + state together.
        if (m.MoveFlags > 0 && m.Seed >= 0 && FleeSync.StateRelayEnabled)
        {
            var hs = (MoraleState)(m.MoveFlags - 1);
            FleeSync.HostState[actor.Pointer] = (hs, m.Seed);
            if (supDiff)
            {
                bool p0 = Replication.ApplyingRemote;
                Replication.ApplyingRemote = true;
                try { actor.SetSuppression(m.Prm); } catch { }
                finally { Replication.ApplyingRemote = p0; }
            }
            FleeSync.Apply(actor, hs, m.Seed, "host esup");
            return;
        }
        if (!supDiff && !morDiff) return;
        bool prev = Replication.ApplyingRemote;
        Replication.ApplyingRemote = true;
        try
        {
            if (supDiff) actor.SetSuppression(m.Prm);
            if (morDiff) actor.SetMorale(m.Seed);
            // Keep FleeSync's copy of the host's morale current (#37).
            // The state follows the number, so store the state the NEW number gives:
            // keeping the old state made a later reassert force "Neutral" onto
            // morale 7, which the engine reads back as Wavering (r118 20:36:39).
            if (morDiff && FleeSync.HostState.ContainsKey(actor.Pointer))
                FleeSync.HostState[actor.Pointer] = (actor.GetMoraleState(), m.Seed);
        }
        catch { return; }
        finally { Replication.ApplyingRemote = prev; }
        // Every machine now holds these values, so they count as the host's last
        // relay for the dedupe -- otherwise a later return to the OLD value is
        // judged "already sent" and never goes out (r121: clients 66, host 70).
        if (CoopNet.Mode == CoopMode.Host)
            SuppressionSyncPatches.NoteRelayed(actor);
        Plugin.Logger.LogInfo($"[SUPPRESS] '{DesyncDetector.TemplateNameOf(actor)}'"
            + (supDiff ? $" suppression {before:F0} -> {m.Prm}" : "")
            + (morDiff ? $" morale {moraleBefore:F0} -> {m.Seed}" : "") + " (partner)");
    }
}

[HarmonyPatch]
internal static class SuppressionSyncPatches
{
    // DECAY AND RECOVERY ARE THE HOST'S TOO, FOR AI UNITS.
    // Shots were only half of it: after the "esup" relay the guest still drifted
    // one way only -- "mine 24 -> host 0" -- because an enemy's suppression wears
    // off at the start of the enemy turn, which only the host simulates. Any
    // other write the host's engine makes to an AI unit's suppression is relayed
    // as the resulting value. Player units' recovery runs on both machines.
    [HarmonyPatch(typeof(Actor), nameof(Actor.SetSuppression))]
    [HarmonyPostfix]
    private static void AfterSet(Actor __instance) => RelayHostAi(__instance);

    [HarmonyPatch(typeof(Actor), nameof(Actor.ChangeSuppressionAndUpdateAP))]
    [HarmonyPostfix]
    private static void AfterChange(Actor __instance) => RelayHostAi(__instance);

    // THE DECAY ITSELF GOES THROUGH NEITHER SETTER. After the two hooks above the
    // guest still drifted "mine 28 -> host 0" (suppression) and "mine 60 -> host
    // 65" (morale): the engine writes m_Suppression / m_Morale directly in its
    // turn and round start. So relay the RESULT after those, and after morale
    // changes. Deduped per actor, so a round start costs only what changed.
    [HarmonyPatch(typeof(Actor), nameof(Actor.OnTurnStart))]
    [HarmonyPostfix]
    private static void AfterTurnStart(Actor __instance) => RelayHostAi(__instance);

    [HarmonyPatch(typeof(Actor), nameof(Actor.OnRoundStart))]
    [HarmonyPostfix]
    private static void AfterRoundStart(Actor __instance) => RelayHostAi(__instance);

    [HarmonyPatch(typeof(Actor), nameof(Actor.SetMorale))]
    [HarmonyPostfix]
    private static void AfterSetMorale(Actor __instance) => RelayHostAi(__instance);

    // Run 20: with the hit owner relaying the victim's morale, the other machine
    // still applied its own morale events while replaying that hit's damage and
    // deaths (under ApplyingRemote / inside the swallowed copy) -- 63 relayed,
    // then 59 locally, every exchange. The relayed value is it, for every unit.
    [HarmonyPatch(typeof(Actor), nameof(Actor.ApplyMorale))]
    [HarmonyPrefix]
    private static bool SkipReplayedMorale(Actor __instance)
    {
        if (!CoopNet.Connected || !Ownership.Active) return true;
        // AI MORALE IS THE HOST'S (user, 2026-10-03), except a client's OWN hit,
        // whose morale it decides and relays (the host swallows that damage, so
        // it never sees those morale events). Any other AI morale effect on a
        // client is skipped -- r120: a client's own -1 outside its hit left a
        // squad on 6 vs the host's 7.
        if (FleeSync.StateRelayEnabled && CoopNet.Mode != CoopMode.Host
            && !Ownership.IsKnownPlayerUnit(__instance) && !Replication.InOwnHit)
        {
            try { if (TacticalManager.Get() == null) return true; } catch { return true; }
            return false;
        }
        if (!Replication.ApplyingRemote && !Replication.InPartnersHit) return true;
        // Any unit (run 21, #19): the decider of the hit relays the result.
        try { if (TacticalManager.Get() == null) return true; } catch { return true; }
        return false;
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.ApplyMorale))]
    [HarmonyPostfix]
    private static void AfterApplyMorale(Actor __instance) => RelayHostAi(__instance);

    private static readonly System.Collections.Generic.Dictionary<IntPtr, long> _lastSent = new();

    private static long RelayKey(Actor a)
        => ((long)(int)Math.Round(a.GetSuppression()) << 32) | (uint)(int)Math.Round(a.GetMorale());

    internal static void NoteRelayed(Actor a)
    {
        try { _lastSent[a.Pointer] = RelayKey(a); } catch { }
    }

    // STANCE: the last combat-state correction left after suppression and morale
    // ("STANCE drift 'player_squad.lim': mine 1 -> host 0", 2026-09-25). The
    // owner's engine changes it (e.g. standing up to move); relay the result from
    // the unit's authority -- its owner for player units, the host for the rest.
    [HarmonyPatch(typeof(Actor), nameof(Actor.SetStance))]
    [HarmonyPostfix]
    private static void AfterStance(Actor __instance)
    {
        if (!CoopNet.Connected || Replication.ApplyingRemote) return;
        try
        {
            if (TacticalManager.Get() == null || __instance == null) return;
            bool mine = Ownership.Active && Ownership.IsKnownPlayerUnit(__instance)
                ? Ownership.IsLocal(__instance)
                : CoopNet.Mode == CoopMode.Host;
            if (!mine) return;
            var p = __instance.GetLastTile()?.GetTilePos();
            var msg = new CoopMsg { T = "estance", AX = p?.x ?? -1, AY = p?.y ?? -1, Prm = (int)__instance.GetStance() };
            Replication.StampIds(msg, __instance, isOrder: false);
            CoopNet.Send(msg);
        }
        catch { }
    }

    private static void RelayHostAi(Actor a)
    {
        if (!CoopNet.Connected || Replication.ApplyingRemote) return;
        try
        {
            if (TacticalManager.Get() == null || a == null) return;
            // A player unit's morale/suppression hit by a shot belongs to whoever
            // decided the shot (run 17: the queen's stab cost 'lim' 20 morale on
            // the host only; the guest swallows its copy of the hit, morale and all).
            // Run 21 (#19): the same held for AI units hit by the GUEST -- the guest
            // took the morale loss, the host replayed the hit with none and relayed
            // its untouched 65 back over it. So: inside a hit, its decider relays
            // (any unit); outside one, the host relays AI units (decay, turn start).
            if (Replication.InOwnHit) { }
            else if (Replication.InPartnersHit) return;
            else if (Ownership.Active && Ownership.IsKnownPlayerUnit(a)) return;
            else if (CoopNet.Mode != CoopMode.Host) return;
            int sup = (int)Math.Round(a.GetSuppression()), mor = (int)Math.Round(a.GetMorale());
            long key = ((long)sup << 32) | (uint)mor;
            if (_lastSent.TryGetValue(a.Pointer, out long prev) && prev == key) return;
            _lastSent[a.Pointer] = key;
            Send(a, mor);
        }
        catch { }
    }

    // morale < 0: not sent (a shot's relay carries suppression only -- morale
    // for AI units is the host's alone, and the shooter's view would regress it).
    private static void Send(Actor a, int morale = -1)
    {
        var p = a.GetLastTile()?.GetTilePos();
        var msg = new CoopMsg { T = "esup", AX = p?.x ?? -1, AY = p?.y ?? -1,
                                Prm = (int)Math.Round(a.GetSuppression()), Seed = morale };
        // THE HOST DECIDES AI MORALE: send its STATE with the number (MoveFlags =
        // state + 1). SetMorale alone leaves a client's state where it was -- r119:
        // the host recovered two squads Fleeing -> Wavering at round start, the
        // clients got morale 0 -> 2 but stayed Fleeing, and ran the flight locally.
        if (morale >= 0 && CoopNet.Mode == CoopMode.Host
            && !(Ownership.Active && Ownership.IsKnownPlayerUnit(a)))
        {
            try { msg.MoveFlags = (int)a.GetMoraleState() + 1; } catch { }
        }
        Replication.StampIds(msg, a, isOrder: false);
        CoopNet.Send(msg);
    }

    // THE SAME RULE FOR SUPPRESSION AS FOR MORALE (BUGS.md #14). Only the hit's
    // decider applies it; the other machine takes the relayed "esup" (SetSuppression,
    // which does not come through here). Run 30: the guest's local copy of a host
    // scrap bomb put 'lim' on 32 suppression while the host, whose projectile had
    // not landed yet, had 0 -- one resync per exchange like that.
    [HarmonyPatch(typeof(Actor), nameof(Actor.ApplySuppression))]
    [HarmonyPrefix]
    private static bool SkipNonAuthoritativeSuppression(Actor __instance, Entity _suppressor)
    {
        // No suppressor = not a hit (decay, turn start): every machine runs its own.
        if (_suppressor == null || !CoopNet.Connected || !Ownership.Active) return true;
        try { if (TacticalManager.Get() == null) return true; } catch { return true; }
        bool mine = SuppressionSync.Authoritative(_suppressor);
        if (!mine && BugProbes.On)
            Plugin.Logger.LogInfo($"[SUPPRESS] Skipped local suppression on '{DesyncDetector.TemplateNameOf(__instance)}' from the partner's hit -- its relay decides");
        return mine;
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.ApplySuppression))]
    [HarmonyPostfix]
    private static void After(Actor __instance, Entity _suppressor)
    {
        if (!CoopNet.Connected || Replication.ApplyingRemote) return;
        try
        {
            if (TacticalManager.Get() == null || __instance == null) return;
            if (!SuppressionSync.Authoritative(_suppressor)) return;
            Send(__instance);
        }
        catch { }
    }
}
