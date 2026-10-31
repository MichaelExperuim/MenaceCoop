using System;
using System.Reflection;
using Il2CppMenace.Tactical;

namespace MenaceCoop;

// WHEN THE AUTO COMBAT MOD IS INSTALLED, ITS BRAIN DRIVES AND OURS STANDS DOWN.
//
// Co-op compiles MenaceAutoCombat's brain into itself, bound to our real
// Ownership / TurnControl / Replication. That copy keeps working for anyone
// without the Auto Combat mod. But a player running both (the user's own set:
// MenaceCoop + MenaceAutoCombat + MenaceIntroSkip) used to get Auto Combat
// switching itself off, and the brain being refined in that mod never ran in a
// co-op game -- nor under our harness, which is where most of its hours are.
//
// So at startup we look for MenaceAutoCombat's CoopBridge and hand it our
// answers. If it accepts, exactly one brain runs (theirs) and SelfTest stops
// ticking ours; the player toggles it with Auto Combat's own F10 / panel. If
// the mod is absent, older than the bridge, or refuses the version, nothing
// changes: Auto Combat still stands itself down and ours drives as before.
//
// Keys are strings so the two mods can ship out of step; see CoopBridge.cs in
// _AutoCombatModDev for the other half. An unknown key throws here, and the
// bridge treats a throw as "use the single-player answer".
internal static class AutoCombatBridge
{
    private const int BridgeVersion = 1;

    internal static bool Delegated { get; private set; }
    // The Auto Combat assembly is loaded, whether or not the bridge connected.
    internal static bool ModInstalled { get; private set; }

    // The Auto Combat mod's own switch and commentary, which co-op's panel shows
    // in place of its own when delegated (Auto Combat then builds no panel).
    private static PropertyInfo _theirEnabled, _theirCommentary;

    internal static bool TheirEnabled
    {
        get { try { return _theirEnabled != null && (bool)_theirEnabled.GetValue(null); } catch { return false; } }
        set { try { _theirEnabled?.SetValue(null, value); } catch { } }
    }

    internal static string TheirCommentary
    {
        get { try { return _theirCommentary?.GetValue(null) as string ?? ""; } catch { return ""; } }
    }

    internal static void TryInstall()
    {
        Assembly asm = null;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            if (a.GetName().Name == "MenaceAutoCombat") { asm = a; break; }
        if (asm == null)
        {
            Plugin.Logger.LogInfo("[AUTO] Auto Combat mod not installed -- no auto combat in co-op (install MenaceAutoCombat to get it)");
            return;
        }
        // Present but maybe not connected: the panel must not tell this player to
        // install a mod they already have (a version mismatch says "update both").
        ModInstalled = true;

        var bridge = asm.GetType("MenaceAutoCombat.CoopBridge");
        var install = bridge?.GetMethod("Install", BindingFlags.Public | BindingFlags.Static);
        _theirEnabled = bridge?.GetProperty("PlayerAutoCombat", BindingFlags.Public | BindingFlags.Static);
        _theirCommentary = bridge?.GetProperty("Commentary", BindingFlags.Public | BindingFlags.Static);
        if (install == null)
        {
            Plugin.Logger.LogWarning($"[AUTO] Auto Combat mod v{asm.GetName().Version} has no co-op bridge -- it will switch itself off, and there is no auto combat in co-op. Update MenaceAutoCombat.");
            return;
        }

        try
        {
            object ok = install.Invoke(null, new object[]
            {
                Plugin.PluginName, BridgeVersion,
                (Func<string, bool>)Flag,
                (Func<string, Actor, bool>)ActorFlag,
                (Func<int>)(() => (int)CoopNet.Mode),
                (Action<float>)(t => CoopRunner.LastUserInputAt = t),
            });
            Delegated = ok is bool b && b;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[AUTO] Auto Combat bridge failed ({e.GetBaseException().Message}) -- no auto combat in co-op. Update both MenaceCoop and MenaceAutoCombat.");
            return;
        }

        Plugin.Logger.LogInfo(Delegated
            ? "[AUTO] Auto Combat mod found -- ITS brain drives auto combat (and the harness)"
            : $"[AUTO] Auto Combat mod refused bridge v{BridgeVersion} -- no auto combat in co-op. Update both MenaceCoop and MenaceAutoCombat.");
    }

    private static bool Flag(string key) => key switch
    {
        "Connected"              => CoopNet.Connected,
        "OwnershipActive"        => Ownership.Active,
        "TurnEnabled"            => TurnControl.Enabled,
        "TurnActive"             => TurnControl.Active,
        "IsMyTurn"               => TurnControl.IsMyTurn,
        "SyncPause"              => Replication.SyncPauseActive,
        "LocallyBlocked"         => Replication.LocallyBlocked(),
        "PartnerBattleConfirmed" => SessionSync.PartnerBattleConfirmed,
        "HarnessMayAct"          => SessionSync.HarnessMayAct(),
        "HostInPlayerPhase"      => DesyncDetector.HostIsInPlayerPhase,
        "DevAutoPlay"            => SelfTest.DevAutoPlay,
        "ResultScreenOpen"       => SessionSync.ResultScreenOpen,   // battle over (also when co-op pulled the guest out early)
        _ => throw new ArgumentException(key),
    };

    private static bool ActorFlag(string key, Actor a) => key switch
    {
        "KnownPlayerUnit" => a != null && Ownership.IsKnownPlayerUnit(a),
        "Local"           => a != null && Ownership.IsLocal(a),
        // A dropped player's unit the host is holding (user, 2026-10-03): auto
        // combat plays it whether or not the host has auto combat switched on.
        "ForceAuto"       => a != null && Ownership.IsLent(a) && Ownership.IsLocal(a),
        "BlockedByLock"   => Replication.ActionBlockedByLock(),
        _ => throw new ArgumentException(key),
    };
}
