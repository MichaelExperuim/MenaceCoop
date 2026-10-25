using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
#if MELON
using Steamworks = Il2CppSteamworks;
#endif

namespace MenaceCoop;

// STEAM LOBBIES: CONNECT WITHOUT TYPING IDS (v5.1, user 2026-10-11).
//
// Before this, a Steam session needed every player to find their SteamID64,
// send it to the others and type theirs in -- and the two sides had to get the
// Host / Join roles right by hand. Now the host's "Host (Steam)" also opens a
// Steam LOBBY. A friend gets in by any of the ways Steam itself offers:
//
//   - the host presses INVITE FRIENDS (Steam overlay) or invites from the
//     friends list after alt-tabbing, and the friend accepts;
//   - the friend picks "Join Game" on the host in their friends list;
//   - the friend presses the JOIN button the co-op panel shows for a friend
//     who is hosting.
//
// The lobby is only the meeting point: it tells each side the other's SteamID.
// The session itself still runs over the same P2P transport as before
// (CoopNet), so nothing about how a battle is replicated changes, and typing
// an ID by hand still works when a lobby cannot be made.
//
// HOW IT HEARS FROM STEAM (probed on a rig, 2026-10-11):
//
//   Steamworks.NET's generic Callback<T> / CallResult<T> cannot be
//   instantiated through interop, but their NON-GENERIC base classes can be
//   derived from: RawSteamCallback / RawSteamCallResult below are injected
//   classes the game's own CallbackDispatcher calls with the raw struct.
//   Proven: LobbyEnter reached the mod 100 ms after CreateLobby.
//
//   Asking afterwards does NOT work: SteamUtils.GetAPICallResult answers
//   k_ESteamAPICallFailureInvalidHandle, because the game's dispatcher fetches
//   (and so consumes) every call result itself, whether or not anyone is
//   registered for it. So a call's answer must be registered for BEFORE the
//   next RunCallbacks -- which is what Register(call, result) right after the
//   call does.
//
//   Polling is still how STATE is read (who is in the lobby, which friends are
//   hosting): those are plain getters and need no callback.
internal static class SteamLobby
{
    private enum State { Idle, Creating, Hosting, Joining, Joined }
    private static State _state = State.Idle;

    private const int EResultOk = 1;
    private const uint EnterSuccess = 1;

    private static ulong _lobby;
    private static float _callSince;
    private static ulong _joinTarget;
    private static float _nextMembersAt, _nextFriendsAt;
    private static readonly HashSet<ulong> _members = new();
    private const float CallTimeout = 20f;

    internal static bool HostingLobby => _lobby != 0 && _state == State.Hosting;
    internal static bool Busy => _state == State.Creating || _state == State.Joining;

    private static float Now => UnityEngine.Time.realtimeSinceStartup;

    private static ulong Self()
    {
        try { return Steamworks.SteamUser.GetSteamID().m_SteamID; } catch { return 0; }
    }

    // ---- host ----

    // "Host (Steam)": the P2P host is already started by the caller; this adds
    // the lobby. A failure here leaves a working manual-ID host behind.
    internal static void HostCreate()
    {
        Leave("starting a new lobby");
        if (!EnsureCallbacks())
        {
            CoopNet.Status = "Steam lobby unavailable -- type your partner's Steam ID instead";
            return;
        }
        try
        {
            int max = Math.Max(2, Math.Min(Peers.MaxSeats, Plugin.MaxPlayers?.Value ?? 4));
            var call = Steamworks.SteamMatchmaking.CreateLobby(Steamworks.ELobbyType.k_ELobbyTypeFriendsOnly, max);
            ListenFor(call, RawKind.LobbyCreated);
            _callSince = Now;
            _state = State.Creating;
            CoopNet.Status = "Steam: opening a lobby...";
            Plugin.Logger.LogInfo($"[LOBBY] CreateLobby requested (friends only, {max} players)");
        }
        catch (Exception e)
        {
            _state = State.Idle;
            Plugin.Logger.LogError($"[LOBBY] CreateLobby failed: {e.Message}");
            CoopNet.Status = "Steam lobby unavailable -- type your partner's Steam ID instead";
        }
    }

    // INVITE FRIENDS: Steam's own friend picker, over the game.
    internal static void Invite()
    {
        if (!HostingLobby)
        {
            CoopNet.Status = Busy ? "Steam: the lobby is still opening..." : "Press Host (Steam) first";
            return;
        }
        try
        {
            bool overlay = false;
            try { overlay = Steamworks.SteamUtils.IsOverlayEnabled(); } catch { }
            Steamworks.SteamFriends.ActivateGameOverlayInviteDialog(new Steamworks.CSteamID(_lobby));
            Plugin.Logger.LogInfo($"[LOBBY] Invite dialog requested (overlay enabled: {overlay})");
            CoopNet.Status = overlay
                ? "Steam: pick the friends to invite"
                : "Steam overlay is off -- invite from your Steam friends list instead (right-click a friend)";
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[LOBBY] Invite dialog failed: {e.Message}");
            CoopNet.Status = "Could not open the invite dialog -- invite from your Steam friends list";
        }
    }

    // ---- guest ----

    internal static void Join(ulong lobby, string why)
    {
        if (lobby == 0) return;
        if ((_lobby == lobby || _joinTarget == lobby) && _state != State.Idle) return;   // already there / on the way
        if (CoopNet.Connected)
        {
            // Never tear a running session down because an invite arrived.
            Plugin.Logger.LogWarning($"[LOBBY] Not joining lobby {lobby} ({why}): a co-op session is already running");
            CoopNet.Status = "Invite received -- press Disconnect first, then accept it again";
            return;
        }
        // Hosting with nobody in yet, or still trying to reach someone: the
        // invite wins.
        if (CoopNet.Mode != CoopMode.Off) CoopNet.Stop();
        Leave("joining another lobby");
        if (!EnsureCallbacks())
        {
            CoopNet.Status = "Steam lobbies unavailable -- type the host's Steam ID instead";
            return;
        }
        try
        {
            var call = Steamworks.SteamMatchmaking.JoinLobby(new Steamworks.CSteamID(lobby));
            ListenFor(call, RawKind.LobbyEnter);
            _callSince = Now;
            _joinTarget = lobby;
            _state = State.Joining;
            CoopNet.Status = "Steam: joining the lobby...";
            Plugin.Logger.LogInfo($"[LOBBY] JoinLobby {lobby} requested ({why})");
        }
        catch (Exception e)
        {
            _state = State.Idle;
            _joinTarget = 0;
            Plugin.Logger.LogError($"[LOBBY] JoinLobby failed: {e.Message}");
            CoopNet.Status = "Could not join the Steam lobby (see log)";
        }
    }

    // Friends who are in a lobby of this game right now. Polled, so it needs no
    // callback: this is how a player already in the game finds a host without
    // an invite.
    internal sealed class FriendLobby { internal ulong Friend, Lobby; internal string Name; }
    private static readonly List<FriendLobby> _friendLobbies = new();
    internal static int FriendLobbiesVersion;
    internal static IReadOnlyList<FriendLobby> FriendLobbies => _friendLobbies;
    private static bool _friendErrorLogged;

    private static void RefreshFriendLobbies()
    {
        var found = new List<FriendLobby>();
        try
        {
            uint app = Steamworks.SteamUtils.GetAppID().m_AppId;
            var flags = Steamworks.EFriendFlags.k_EFriendFlagImmediate;
            int n = Steamworks.SteamFriends.GetFriendCount(flags);
            for (int i = 0; i < n; i++)
            {
                var f = Steamworks.SteamFriends.GetFriendByIndex(i, flags);
                if (!Steamworks.SteamFriends.GetFriendGamePlayed(f, out var info)) continue;
                if ((uint)(info.m_gameID.m_GameID & 0xFFFFFF) != app) continue;
                ulong lobby = info.m_steamIDLobby.m_SteamID;
                if (lobby == 0) continue;
                string name = null;
                try { name = Steamworks.SteamFriends.GetFriendPersonaName(f); } catch { }
                found.Add(new FriendLobby { Friend = f.m_SteamID, Lobby = lobby, Name = string.IsNullOrEmpty(name) ? "a friend" : name });
            }
        }
        catch (Exception e)
        {
            if (!_friendErrorLogged) { _friendErrorLogged = true; Plugin.Logger.LogWarning($"[LOBBY] Friend list unavailable: {e.Message}"); }
        }
        if (CoopNet.SteamSelfTest)
        {
            ulong own = SelfTestLobby();
            if (own != 0) found.Add(new FriendLobby { Friend = Self(), Lobby = own, Name = "SELF TEST (this account's other window)" });
        }
        bool same = found.Count == _friendLobbies.Count;
        for (int i = 0; same && i < found.Count; i++)
            same = found[i].Lobby == _friendLobbies[i].Lobby && found[i].Name == _friendLobbies[i].Name;
        if (same) return;
        _friendLobbies.Clear();
        _friendLobbies.AddRange(found);
        FriendLobbiesVersion++;
        Plugin.Logger.LogInfo($"[LOBBY] Friends hosting right now: {(found.Count == 0 ? "none" : string.Join(", ", found.ConvertAll(x => x.Name)))}");
    }

    // "Join (Steam)" with no ID typed: the one friend who is hosting.
    internal static void JoinFriend()
    {
        RefreshFriendLobbies();
        if (_friendLobbies.Count == 0)
        {
            CoopNet.Status = "No friend is hosting -- ask the host to invite you (or type their Steam ID)";
            return;
        }
        if (_friendLobbies.Count > 1)
        {
            CoopNet.Status = "Several friends are hosting -- press the JOIN button for the one you want";
            return;
        }
        Join(_friendLobbies[0].Lobby, $"Join (Steam), friend '{_friendLobbies[0].Name}'");
    }

    // ---- shared ----

    internal static void Leave(string why)
    {
        ulong lobby = _lobby;
        if (_state == State.Idle && lobby == 0) return;
        bool wasGuest = _state == State.Joined;
        bool wasHost = _state == State.Hosting;
        _state = State.Idle;
        _joinTarget = 0;
        _lobby = 0;
        _members.Clear();
        if (lobby == 0) return;
        if (CoopNet.SteamSelfTest)
        {
            // One account: the client leaving would take the host out of its
            // own lobby too. Only the host's window closes it.
            if (wasGuest) { Plugin.Logger.LogInfo($"[LOBBY] SteamSelfTest: forgot lobby {lobby} without leaving it ({why})"); return; }
            if (wasHost) { try { System.IO.File.Delete(SelfTestFile); } catch { } }
        }
        try { Steamworks.SteamMatchmaking.LeaveLobby(new Steamworks.CSteamID(lobby)); } catch { }
        Plugin.Logger.LogInfo($"[LOBBY] Left lobby {lobby} ({why})");
    }

    // Launched BY an invite (the game was not running): Steam starts it with
    // "+connect_lobby <id>" on the command line.
    private static bool _launchChecked;
    private static ulong _launchLobby;
    private static void CheckLaunchArgs()
    {
        if (_launchChecked) return;
        _launchChecked = true;
        try
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "+connect_lobby", StringComparison.OrdinalIgnoreCase)
                    && ulong.TryParse(args[i + 1], out ulong id) && id != 0)
                    _launchLobby = id;
        }
        catch { }
        if (_launchLobby != 0) Plugin.Logger.LogInfo($"[LOBBY] Launched by a Steam invite to lobby {_launchLobby} -- joining once the title screen is up");
    }

    // Every frame, main thread (Steam API affinity), from CoopRunner.
    internal static void Tick()
    {
        try
        {
            CheckLaunchArgs();
            ProbeTick();
            if (_launchLobby != 0 && Self() != 0 && CoopRunner.TitleScreenUp())
            {
                ulong id = _launchLobby;
                _launchLobby = 0;
                Join(id, "launched by an invite");
            }

            // The lobby follows the session: Disconnect (or a direct-IP start)
            // ends it without every caller of CoopNet.Stop having to know.
            if ((_state == State.Creating || _state == State.Hosting)
                && !(CoopNet.Mode == CoopMode.Host && CoopNet.Transport == TransportKind.Steam))
                Leave("the Steam host session ended");
            if (_state == State.Joined
                && !(CoopNet.Mode == CoopMode.Guest && CoopNet.Transport == TransportKind.Steam))
                Leave("the Steam session ended");

            DrainEvents();

            if (Busy && Now - _callSince > CallTimeout)
            {
                Plugin.Logger.LogError($"[LOBBY] Steam did not answer within {CallTimeout:0}s ({_state})");
                Fail("no answer from Steam");
            }
            if (_state == State.Hosting && Now >= _nextMembersAt) { _nextMembersAt = Now + 1f; PollMembers(); }
            if (CoopNet.Mode == CoopMode.Off && _state == State.Idle && Now >= _nextFriendsAt && Self() != 0)
            {
                _nextFriendsAt = Now + 3f;
                EnsureCallbacks();       // so an accepted invite is heard from the title screen on
                RefreshFriendLobbies();
            }
        }
        catch (Exception e)
        {
            if (!_tickErrorLogged) { _tickErrorLogged = true; Plugin.Logger.LogError($"[LOBBY] Tick failed: {e}"); }
        }
    }
    private static bool _tickErrorLogged;

    private static void Fail(string why)
    {
        bool creating = _state == State.Creating;
        Plugin.Logger.LogError($"[LOBBY] {(creating ? "Create" : "Join")} failed: {why}");
        CoopNet.Status = creating
            ? "Steam lobby unavailable -- type your partner's Steam ID instead"
            : "Could not join the Steam lobby -- ask for a new invite";
        _state = State.Idle;
        _joinTarget = 0;
    }

    private static void OnCreated(ulong lobby)
    {
        if (_state == State.Hosting && _lobby == lobby) return;      // told twice (callback + call result)
        _lobby = lobby;
        _state = State.Hosting;
        _members.Clear();
        _nextMembersAt = 0f;
        try { Steamworks.SteamMatchmaking.SetLobbyData(new Steamworks.CSteamID(lobby), "menacecoop", Plugin.PluginVersion); }
        catch { }
        Plugin.Logger.LogInfo($"[LOBBY] Lobby {lobby} is open -- friends can join from the friends list or by invite");
        CoopNet.Status = "Steam lobby open -- press INVITE FRIENDS, or have friends Join Game from their friends list";
        if (CoopNet.SteamSelfTest)
        {
            // The second instance is this same account: it never shows up as
            // "another member", so allow it now and leave the lobby id where
            // that instance's JOIN button can find it.
            CoopNet.AllowSteamPeer(Self());
            try { System.IO.File.WriteAllText(SelfTestFile, lobby.ToString()); } catch { }
            Plugin.Logger.LogWarning($"[LOBBY] SteamSelfTest: a second instance on this account may join lobby {lobby}");
        }
    }

    // ---- dev: two instances on one Steam account (CoopNet.SteamSelfTest) ----
    private static string SelfTestFile => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MenaceCoop-selftest-lobby.txt");

    private static ulong SelfTestLobby()
    {
        try
        {
            var f = new System.IO.FileInfo(SelfTestFile);
            if (!f.Exists || (DateTime.UtcNow - f.LastWriteTimeUtc).TotalHours > 6) return 0;
            return ulong.TryParse(System.IO.File.ReadAllText(SelfTestFile).Trim(), out ulong id) ? id : 0;
        }
        catch { return 0; }
    }

    private static void OnEntered(ulong lobby)
    {
        if (_state == State.Joined && _lobby == lobby) return;       // told twice
        ulong owner = 0;
        try { owner = Steamworks.SteamMatchmaking.GetLobbyOwner(new Steamworks.CSteamID(lobby)).m_SteamID; } catch { }
        ulong me = Self();
        if (owner == 0 || (owner == me && !CoopNet.SteamSelfTest))
        {
            // Our own lobby from another run, or an abandoned one we now own.
            try { Steamworks.SteamMatchmaking.LeaveLobby(new Steamworks.CSteamID(lobby)); } catch { }
            _state = State.Idle;
            _joinTarget = 0;
            Plugin.Logger.LogWarning($"[LOBBY] Lobby {lobby} has no other host (owner {owner}, me {me}); left it");
            CoopNet.Status = "That lobby has no host any more -- ask for a new invite";
            return;
        }
        // Order matters: StartSteam stops any old session first, and Tick leaves
        // the lobby whenever the session is not a Steam guest one.
        CoopNet.StartSteam(CoopMode.Guest, owner);
        _lobby = lobby;
        _joinTarget = 0;
        _state = State.Joined;
        Plugin.Logger.LogInfo($"[LOBBY] Entered lobby {lobby}; host is {owner} -- connecting");
    }

    // HOST: everyone in the lobby may connect. Checked once a second, which is
    // also what covers a player who came in without an invite (Join Game).
    private static void PollMembers()
    {
        var id = new Steamworks.CSteamID(_lobby);
        ulong me = Self();
        int n = Steamworks.SteamMatchmaking.GetNumLobbyMembers(id);
        var now = new HashSet<ulong>();
        for (int i = 0; i < n; i++)
        {
            ulong m = Steamworks.SteamMatchmaking.GetLobbyMemberByIndex(id, i).m_SteamID;
            if (m == 0 || m == me) continue;
            now.Add(m);
            if (_members.Add(m))
            {
                string name = "?";
                try { name = Steamworks.SteamFriends.GetFriendPersonaName(new Steamworks.CSteamID(m)); } catch { }
                Plugin.Logger.LogInfo($"[LOBBY] '{name}' ({m}) entered the lobby -- connecting to them");
                CoopNet.AllowSteamPeer(m);
            }
        }
        // Gone from the lobby: forget them here, so coming back counts as new.
        // Their P2P link (if any) is CoopNet's to drop, on its own evidence.
        _members.RemoveWhere(m => !now.Contains(m));
    }

    // ---- hearing from Steam ----

    internal enum RawKind { JoinRequested, LobbyEnter, LobbyCreated }

    private static bool _callbacksTried, _callbacksLive;
    // Held so the injected objects are never collected under the dispatcher.
    private static readonly List<object> _keepAlive = new();
    internal static bool CallbacksLive => _callbacksLive;

    private static bool EnsureCallbacks()
    {
        if (_callbacksTried) return _callbacksLive;
        _callbacksTried = true;
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<RawSteamCallback>();
            ClassInjector.RegisterTypeInIl2Cpp<RawSteamCallResult>();
            foreach (var kind in new[] { RawKind.JoinRequested, RawKind.LobbyEnter })
            {
                var cb = new RawSteamCallback { Kind = kind };
                Steamworks.CallbackDispatcher.Register(cb);
                _keepAlive.Add(cb);
            }
            _callbacksLive = true;
            Plugin.Logger.LogInfo("[LOBBY] Listening to Steam: accepted invites and lobby entry");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[LOBBY] Cannot listen to Steam ({e.Message}) -- lobbies are off; connect by typing Steam IDs.");
        }
        return _callbacksLive;
    }

    // The answer to ONE call. Registered straight after the call, before the
    // game's next RunCallbacks -- the dispatcher drops an answer nobody asked for.
    private static void ListenFor(Steamworks.SteamAPICall_t call, RawKind kind)
    {
        try
        {
            var cr = new RawSteamCallResult { Kind = kind };
            Steamworks.CallbackDispatcher.Register(call, cr);
            _keepAlive.Add(cr);
            if (_keepAlive.Count > 64) _keepAlive.RemoveRange(2, _keepAlive.Count - 34);   // keep the 2 callbacks + recent results
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[LOBBY] Could not register for the call's answer ({e.Message}); relying on the lobby-entry callback"); }
    }

    // Steam's dispatcher calls in from inside the game's RunCallbacks. Only copy
    // the struct out here; everything is acted on from Tick.
    private struct Ev { internal RawKind Kind; internal ulong A, B; internal uint Code; internal bool Failed; internal string Via; }
    private static readonly List<Ev> _events = new();

    internal static void OnRaw(RawKind kind, IntPtr p, bool failed, string via)
    {
        try
        {
            var ev = new Ev { Kind = kind, Failed = failed, Via = via };
            if (p != IntPtr.Zero)
            {
                switch (kind)
                {
                    case RawKind.JoinRequested:     // GameLobbyJoinRequested_t { CSteamID lobby; CSteamID friend }
                        ev.A = (ulong)Marshal.ReadInt64(p, 0); ev.B = (ulong)Marshal.ReadInt64(p, 8); break;
                    case RawKind.LobbyEnter:        // LobbyEnter_t { ulong lobby; uint perms; bool locked; uint response }
                        ev.A = (ulong)Marshal.ReadInt64(p, 0); ev.Code = (uint)Marshal.ReadInt32(p, 16); break;
                    case RawKind.LobbyCreated:      // LobbyCreated_t { EResult; ulong lobby }
                        ev.Code = (uint)Marshal.ReadInt32(p, 0); ev.A = (ulong)Marshal.ReadInt64(p, 8); break;
                }
            }
            lock (_events) _events.Add(ev);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[LOBBY] Could not read Steam's {kind}: {e.Message}"); }
    }

    private static void DrainEvents()
    {
        Ev[] evs;
        lock (_events)
        {
            if (_events.Count == 0) return;
            evs = _events.ToArray();
            _events.Clear();
        }
        foreach (var ev in evs)
        {
            // Rare (a handful per session) and the only record of what Steam
            // actually answered, so always logged.
            Plugin.Logger.LogInfo($"[LOBBY] Steam: {ev.Kind} via {ev.Via} -- lobby {ev.A}, code {ev.Code}, failed {ev.Failed} (we are {_state})");
            switch (ev.Kind)
            {
                case RawKind.JoinRequested:
                    Join(ev.A, "invite accepted in Steam");
                    break;

                case RawKind.LobbyCreated:
                    if (_state != State.Creating) break;
                    if (ev.Failed || ev.Code != EResultOk || ev.A == 0) Fail($"Steam refused the lobby (EResult {ev.Code}, failed {ev.Failed})");
                    else OnCreated(ev.A);
                    break;

                case RawKind.LobbyEnter:
                    if (_state == State.Creating)
                    {
                        // The creator enters its own lobby: this is also how the
                        // lobby id arrives if the create answer itself is missed.
                        if (!ev.Failed && ev.Code == EnterSuccess && ev.A != 0) OnCreated(ev.A);
                    }
                    else if (_state == State.Joining && (ev.A == _joinTarget || ev.A == 0))
                    {
                        if (ev.Failed || ev.Code != EnterSuccess || ev.A == 0)
                            Fail($"Steam refused the join (response {ev.Code}: full, gone, or not allowed)");
                        else OnEntered(ev.A);
                    }
                    break;
            }
        }
    }

    // ---- dev probe ([MenaceCoop_Dev] SteamLobbyProbe) ----
    // Steam behaviour cannot be read off the API; it has to be watched. At the
    // title screen: host over Steam with nobody listed, open the lobby, log
    // what comes back, hold it open for a minute (so a second instance can be
    // started with "+connect_lobby <id>"), then close it.
    private static int _probeStep;
    private static float _probeAt;

    private static void ProbeTick()
    {
#if !COOP_DEVTOOLS
        return;         // compiled out of release builds, like the other dev tools
#else
        ProbeTickDev();
#endif
    }

    private static void ProbeTickDev()
    {
        if (Plugin.SteamLobbyProbe == null || !Plugin.SteamLobbyProbe.Value || _probeStep >= 3) return;
        if (_probeStep == 0)
        {
            if (Self() == 0 || !CoopRunner.TitleScreenUp()) return;
            _probeStep = 1;
            _probeAt = Now;
            string persona = "?"; uint app = 0; bool overlay = false; int friends = -1; bool disp = false;
            try { persona = Steamworks.SteamFriends.GetPersonaName(); } catch { }
            try { app = Steamworks.SteamUtils.GetAppID().m_AppId; } catch { }
            try { overlay = Steamworks.SteamUtils.IsOverlayEnabled(); } catch { }
            try { friends = Steamworks.SteamFriends.GetFriendCount(Steamworks.EFriendFlags.k_EFriendFlagImmediate); } catch { }
            try { disp = Steamworks.CallbackDispatcher.IsInitialized; } catch { }
            Plugin.Logger.LogWarning($"[LOBBYPROBE] start: persona='{persona}' app={app} overlay={overlay} friends={friends} dispatcherInitialized={disp}");
            CoopNet.StartSteam(CoopMode.Host, new List<ulong>());
            HostCreate();
            Plugin.Logger.LogWarning($"[LOBBYPROBE] callbacks live: {CallbacksLive}");
            return;
        }
        if (_probeStep == 1)
        {
            if (Now - _probeAt < 10f) return;
            _probeStep = 2;
            LogProbeState("after 10s");
            return;
        }
        if (Now - _probeAt < 75f) return;
        _probeStep = 3;
        LogProbeState("after 75s, closing");
        CoopNet.Stop();          // Tick then leaves the lobby
    }

    private static void LogProbeState(string when)
    {
        int n = -1; ulong owner = 0; string data = "?";
        try
        {
            var id = new Steamworks.CSteamID(_lobby);
            n = Steamworks.SteamMatchmaking.GetNumLobbyMembers(id);
            owner = Steamworks.SteamMatchmaking.GetLobbyOwner(id).m_SteamID;
            data = Steamworks.SteamMatchmaking.GetLobbyData(id, "menacecoop");
        }
        catch (Exception e) { data = "threw: " + e.Message; }
        Plugin.Logger.LogWarning($"[LOBBYPROBE] {when}: state={_state} lobby={_lobby} members={n} ownerIsMe={(owner != 0 && owner == Self())} data='{data}' "
            + $"links={CoopNet.ClientCount} netStatus='{CoopNet.Status}'");
    }
}

// Steamworks.NET's non-generic callback base, implemented here because the
// generic Callback<T> cannot be instantiated through interop. The game's
// CallbackDispatcher asks it which Steam struct it wants and then hands over
// the raw struct pointer.
public class RawSteamCallback : Steamworks.Callback
{
    public RawSteamCallback(IntPtr ptr) : base(ptr) { }
    public RawSteamCallback() : base(ClassInjector.DerivedConstructorPointer<RawSteamCallback>())
        => ClassInjector.DerivedConstructorBody(this);

    internal SteamLobby.RawKind Kind;

    public override Il2CppSystem.Type GetCallbackType() => Kind == SteamLobby.RawKind.LobbyEnter
        ? Il2CppType.Of<Steamworks.LobbyEnter_t>()
        : Il2CppType.Of<Steamworks.GameLobbyJoinRequested_t>();
    public override void OnRunCallback(IntPtr pvParam) => SteamLobby.OnRaw(Kind, pvParam, false, "callback");
    public override void SetUnregistered() { }
    public override bool IsGameServer => false;
}

// The same for the answer to one call (CreateLobby, JoinLobby).
public class RawSteamCallResult : Steamworks.CallResult
{
    public RawSteamCallResult(IntPtr ptr) : base(ptr) { }
    public RawSteamCallResult() : base(ClassInjector.DerivedConstructorPointer<RawSteamCallResult>())
        => ClassInjector.DerivedConstructorBody(this);

    internal SteamLobby.RawKind Kind;

    public override Il2CppSystem.Type GetCallbackType() => Kind == SteamLobby.RawKind.LobbyCreated
        ? Il2CppType.Of<Steamworks.LobbyCreated_t>()
        : Il2CppType.Of<Steamworks.LobbyEnter_t>();
    public override void OnRunCallResult(IntPtr pvParam, bool bFailed, ulong hSteamAPICall)
        => SteamLobby.OnRaw(Kind, pvParam, bFailed, "call result");
    public override void SetUnregistered() { }
}
