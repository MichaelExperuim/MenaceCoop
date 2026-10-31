using System;
using HarmonyLib;
using Il2CppMenace.States;
using Il2CppMenace.Strategy;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
using UnityEngine;

namespace MenaceCoop;

// PROMOTIONS DID NOT REACH THE GUEST.
//
// Reported: "when a squaddie is promoted with a new ability, doesn't show up on
// guest strategy layer." The mod GATED the guest from changing perks
// (TryRemoveLastPerk is blocked as a host-only transaction) but never MIRRORED
// the host adding one, so a promotion existed on exactly one machine until the
// next full save-sync happened to carry it -- which is at mission launch, long
// after the player looked.
//
// The leader is addressed by its index in Roster.GetHiredLeaders(), the same
// identity DesyncDetector.RosterIndexOf already relies on for battle orders. The
// roster is host-authoritative and mirrored, so the two machines agree on it.
//
// The perk itself travels by NAME. There is no name->PerkTemplate registry in
// this build (UnitRankTemplate carries no perk list, and PerkTemplate itself is
// almost bare), so the guest resolves it by scanning loaded template assets.
// That scan is the reason this is not done per frame: it runs once, when a
// promotion actually arrives.
internal static class PerkSync
{
    internal static bool Applying;

    internal static void BroadcastPerk(BaseUnitLeader leader, PerkTemplate perk, bool spendPoints)
    {
        if (Applying) return;
        if (!CoopNet.Connected || CoopNet.Mode != CoopMode.Host) return;
        try
        {
            if (leader == null || perk == null) return;
            int idx = IndexOfLeader(leader);
            if (idx < 0)
            {
                // Fires for every leader while a SAVE LOADS (perks are re-added
                // before the roster exists) -- five warnings per campaign load
                // with nothing wrong. The save carries those perks anyway.
                if (SelfTest.TraceOn)
                    Plugin.Logger.LogInfo("[PERK] AddPerk on a leader not in the hired roster (save load?) -- not relayed");
                return;
            }
            string name = null;
            try { name = perk.name; } catch { }
            if (string.IsNullOrEmpty(name)) return;

            CoopNet.Send(new CoopMsg { T = "perk", Prm = idx, SkillId = name, MoveFlags = spendPoints ? 1 : 0 });
            Plugin.Logger.LogInfo($"[PERK] Relayed promotion: leader #{idx} gained '{name}'");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[PERK] Broadcast failed: {e.Message}"); }
    }

    internal static void HandleRemote(CoopMsg m)
    {
        if (CoopNet.Mode == CoopMode.Host) return;
        try
        {
            var hired = StrategyState.Get()?.Roster?.GetHiredLeaders()
                ?.TryCast<Il2CppSystem.Collections.Generic.List<BaseUnitLeader>>();
            if (hired == null || m.Prm < 0 || m.Prm >= hired.Count)
            {
                Plugin.Logger.LogWarning($"[PERK] Cannot apply promotion: no hired leader #{m.Prm} "
                    + $"(roster has {(hired == null ? 0 : hired.Count)}). It will arrive with the next sync.");
                return;
            }
            var leader = hired[m.Prm];
            var perk = FindPerk(m.SkillId);
            if (leader == null || perk == null)
            {
                Plugin.Logger.LogWarning($"[PERK] Could not resolve perk '{m.SkillId}' for leader #{m.Prm} "
                    + "-- the promotion will show after the next full sync.");
                return;
            }
            if (leader.HasPerk(perk)) return;               // already there: nothing to do

            Applying = true;
            // NEVER spend the guest's promotion points. The host has already paid
            // for this on the authoritative side; charging again here would drift
            // the two economies apart, which is the exact class of bug the
            // host-authority rule exists to prevent.
            try { leader.AddPerk(perk, false); }
            finally { Applying = false; }

            Plugin.Logger.LogInfo($"[PERK] Applied promotion from host: leader #{m.Prm} gained '{m.SkillId}'");
            CoopRunner.ShowDesyncWarning($"Your partner promoted a squaddie: {PhantomWatch.Pretty(m.SkillId)}");
        }
        catch (Exception e)
        {
            Applying = false;
            Plugin.Logger.LogWarning($"[PERK] Apply failed: {e.Message}");
        }
    }

    private static int IndexOfLeader(BaseUnitLeader leader)
    {
        try
        {
            var hired = StrategyState.Get()?.Roster?.GetHiredLeaders()
                ?.TryCast<Il2CppSystem.Collections.Generic.List<BaseUnitLeader>>();
            if (hired == null) return -1;
            for (int i = 0; i < hired.Count; i++)
                if (hired[i]?.Pointer == leader.Pointer) return i;
        }
        catch { }
        return -1;
    }

    // Scan loaded assets for the named perk. FindObjectsOfTypeAll is normally
    // banned in this codebase -- it errors on every call for Actor and that flood
    // froze both machines once -- but the objection is to per-frame use on a type
    // Unity rejects. PerkTemplate is an asset type, and this runs once per
    // promotion, so the cached result below keeps even that to a minimum.
    private static readonly System.Collections.Generic.Dictionary<string, PerkTemplate> _cache = new();

    private static PerkTemplate FindPerk(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_cache.TryGetValue(name, out var hit) && hit != null) return hit;
        try
        {
            var all = Resources.FindObjectsOfTypeAll<PerkTemplate>();
            if (all == null) return null;
            foreach (var p in all)
            {
                if (p == null) continue;
                string n = null;
                try { n = p.name; } catch { }
                if (string.IsNullOrEmpty(n)) continue;
                _cache[n] = p;
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[PERK] Template scan failed: {e.Message}"); }
        return _cache.TryGetValue(name, out var found) ? found : null;
    }
}

[HarmonyPatch]
internal static class PerkPatches
{
    // REGISTER IN Plugin.Load -- an unregistered patch class is silently inert.
    [HarmonyPatch(typeof(BaseUnitLeader), nameof(BaseUnitLeader.AddPerk))]
    [HarmonyPostfix]
    private static void AfterAddPerk(BaseUnitLeader __instance, PerkTemplate _perk, bool _spendPromotionPoints)
    {
        PerkSync.BroadcastPerk(__instance, _perk, _spendPromotionPoints);
    }
}
