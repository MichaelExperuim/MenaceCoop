using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
// MelonLoader prefixes the Steamworks.NET assembly too
// (Il2Cppcom.rlabrecque.steamworks.net.dll), so every Steamworks.X reference
// below keeps compiling through this alias.
using Steamworks = Il2CppSteamworks;

namespace MenaceCoop;

internal enum CoopMode { Off, Host, Guest }

internal enum TransportKind { Tcp, Steam }

// One message type for everything; T discriminates. Kept flat so System.Text.Json
// needs no polymorphism support.
internal class CoopMsg
{
    public string T { get; set; }      // hello | seed | skill | move | skip | done | fmeta | fchunk | fdone
    public string Ver { get; set; }    // hello: plugin version
    public string GVer { get; set; }   // hello: game build fingerprint
    // hello: "name@version;..." of every melon and content pack installed.
    // Null from a partner running a build older than this check -- treated as
    // "cannot tell", never as "matches".
    public string Mods { get; set; }
    public int Seed { get; set; }      // seed / per-order RNG seed
    public int AX { get; set; }        // actor tile x
    public int AY { get; set; }        // actor tile y
    public int TX { get; set; }        // target tile x
    public int TY { get; set; }        // target tile y
    public string SkillId { get; set; }
    public int Prm { get; set; }       // skill: UsageParameter / done: flag
    public int MoveAction { get; set; }
    public int MoveFlags { get; set; }
    public string FName { get; set; }  // file transfer: file name
    public long FSize { get; set; }    // fmeta: total bytes
    public long FOff { get; set; }     // fchunk: offset
    public string FHash { get; set; }  // fmeta: SHA256 hex
    public string Data { get; set; }   // fchunk: base64 payload
    public float CX { get; set; }      // cursor: normalized x (0..1, left origin)
    public float CY { get; set; }      // cursor: normalized y (0..1, top origin)
    public int MIdx { get; set; }      // startmission: mission index

    // v5 (Peers.cs): sender's seat (host 0, clients 1..7) and the seat it is for
    // (-1 = every other player). Stamped by CoopNet.Send; -1/-1 from older builds.
    public int From { get; set; } = -1;
    public int To { get; set; } = -1;
    public string Key { get; set; }    // hello: sender's install id (Peers.LocalKey)
    public string Name { get; set; }   // hello: sender's player name
    public string Seats { get; set; }  // own: seat per deploy slot, "0,1,0,1"
    // Host only, never serialised (a field): the link this message was read from.
    internal int ViaLink;

    // GuestReload journals and re-sends messages; a queued message must never be
    // mutated after it is handed to Send.
    internal CoopMsg Clone() => (CoopMsg)MemberwiseClone();

    // Stable identity of the actor an order refers to -- tile addressing alone
    // breaks the moment positions drift, so orders carry identity and the tile
    // is only a fallback. RId: hired-roster index (player units). CId: spawn-tile
    // derived coop id (AI units, see DesyncDetector). -1 = not applicable.
    public int RId { get; set; } = -1;
    public int CId { get; set; } = -1;
    // HOST-DECIDED FOG for an AI actor's order: the host's visibility of it when
    // it acted (0 = not stamped, 1 = hidden, 2 = visible). The engine moves a
    // hidden unit instantly (0.05 s vs 4.9 s, r131), so a client that disagrees
    // about fog runs the same order on a different clock. See FogSync.ApplyHostVis.
    public int HVis { get; set; }

    // STABLE IDENTITY OF A SQUAD MEMBER for damage packets (ehp/edie).
    //
    // Those used to address a member purely by its index in GetElements(), but
    // that list holds only LIVING members, so it shrinks as the squad takes
    // casualties and every later index shifts down. Once the two machines had
    // lost a different number of members, the same index meant a different
    // person on each side -- "ehp: cannot resolve el4 -- actor=found,3 els" was
    // only the visible half; where the index still existed, damage was applied
    // silently to the WRONG squaddie. SqId is the engine's own per-squaddie id
    // and does not move when someone dies. -1 = not applicable (old build).
    public int SqId { get; set; } = -1;

    // Move orders: the tile of the CONTAINER (building) the unit will enter
    // when the move finishes, or -1 for none. Buildings are static, so
    // addressing them by tile is stable -- unlike units.
    public int CtX { get; set; } = -1;
    public int CtY { get; set; } = -1;

    // AUTHORITATIVE ACTION POINTS after the order completed on the sender.
    //
    // Replaying an order re-derives its AP cost locally, and any small
    // difference (a path one tile longer, a skill resolving slightly
    // differently) leaves the two machines disagreeing about that unit's AP for
    // the rest of the battle. Every mission logged a stream of
    // "AP drift 'enemy.x': mine 12 -> host 100" as a result, all of it healed
    // after the fact by the desync fixer. Carrying the sender's AP on the order
    // itself keeps the two sides equal at the moment of the action instead --
    // prevention rather than correction. -1 = not supplied.
    public int Ap { get; set; } = -1;

    // ehp/edie: WHO landed the hit and how it looked, so the receiving machine can
    // kill the element through the engine's own Element.OnDeath (death animation,
    // body) instead of a bare Die() (BUGS.md #28). -1 = not supplied; SkillId then
    // carries the killing skill's id.
    public int ARId { get; set; } = -1;
    public int ACId { get; set; } = -1;
    public int Vis { get; set; } = -1;

    // skill: the shooter's remaining uses BEFORE the use, for limited-use skills
    // (ammo, grenades); the receiver aligns before replaying. -1 = unlimited.
    public int SkUses { get; set; } = -1;
}

// Length-prefixed JSON over a single TCP connection. Reader/writer run on
// background threads; received messages are drained on the Unity main thread
// by CoopRunner because Il2Cpp objects must not be touched off-thread.
// Start/Stop are callable at any time (driven by the in-game UI); a generation
// counter invalidates stale session threads.
internal static class CoopNet
{
    internal static CoopMode Mode = CoopMode.Off;
    internal static TransportKind Transport = TransportKind.Tcp;
    internal static readonly ConcurrentQueue<CoopMsg> Incoming = new();
    private static readonly ConcurrentQueue<CoopMsg> Outgoing = new();

    private static TcpListener _listener;
    private static TcpClient _client;
    private static volatile bool _connected;
    private static int _generation;

    internal static volatile string Status = "Offline";
    // Host: at least one client linked. Client: linked to the host.
    internal static bool Connected => _connected || (Mode == CoopMode.Host && HostLinks.Count > 0);
    internal static int ClientCount => Mode == CoopMode.Host ? HostLinks.Count : (_connected ? 1 : 0);
    // Host, with or without clients (#57). Host-authority state that the reload
    // journal keeps is sent under this, so a battle played alone for a while
    // still rebuilds for a player who joins later (Send journals with no links).
    internal static bool Hosting => Mode == CoopMode.Host;

    internal static void SetListener(TcpListener l) => _listener = l;
    internal static void NoteLinksChanged()
    {
        int n = HostLinks.Count;
        Status = n == 0 ? "Hosting, waiting for players..." : $"Hosting: {n} player{(n == 1 ? "" : "s")} connected";
    }

    internal static void Start(CoopMode mode, string hostAddress, int port)
    {
        Stop();
        if (mode == CoopMode.Off) return;
        // A direct-IP session is password-only (CoopAuth.cs): with no password a
        // forwarded port would be open to anyone who finds it.
        if (string.IsNullOrEmpty(CoopAuth.Normalize(CoopAuth.Password)))
        {
            Status = "Set a session PASSWORD first (both players, the same one)";
            Plugin.Logger.LogWarning("[NET] Direct-IP session not started: no password set");
            return;
        }
        Mode = mode;
        Transport = TransportKind.Tcp;
        int gen = _generation;
        var thread = new Thread(() =>
        {
            if (mode == CoopMode.Host) HostLinks.AcceptLoop(gen, port, Stale);
            else ConnectionLoop(gen, mode, hostAddress, port);
        })
        {
            IsBackground = true,
            Name = "MenaceCoop.Net",
        };
        thread.Start();
    }

    internal static void Stop()
    {
        Interlocked.Increment(ref _generation);
        Mode = CoopMode.Off;
        _connected = false;
        Status = "Offline";
        while (Outgoing.TryDequeue(out _)) { }
        while (Incoming.TryDequeue(out _)) { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        try { _client?.Close(); } catch { }
        _client = null;
        HostLinks.CloseAll();
        StopSteam();
    }

    // ---- Steam P2P transport ----
    // Callback registration isn't available through interop (the generic
    // callback instantiations weren't AOT-compiled into the game), so the
    // session is established by MUTUAL SEND: both players enter the other's
    // SteamID and ping until the first packet arrives. Blind
    // AcceptP2PSessionWithUser calls during the handshake cover the rest.
    private static bool _steamActive;
    private static ulong _partnerSteamId;
    // v5 host: every player allowed to join over Steam (PartnerSteamId may hold a
    // list). A client still has exactly one partner: the host.
    private static readonly System.Collections.Generic.HashSet<ulong> _steamAllowed = new();
    private static readonly System.Collections.Generic.Dictionary<ulong, int> _steamPingAt = new();

    // "id, id; id" -> ids. Anything that is not a SteamID64 is skipped.
    internal static System.Collections.Generic.List<ulong> ParseSteamIds(string raw)
    {
        var ids = new System.Collections.Generic.List<ulong>();
        if (string.IsNullOrWhiteSpace(raw)) return ids;
        foreach (var part in raw.Split(new[] { ',', ';', ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            if (ulong.TryParse(part.Trim(), out ulong id) && id != 0 && !ids.Contains(id)) ids.Add(id);
        return ids;
    }

    internal static void CloseSteamSession(ulong id)
    {
        if (SteamSelfTest) return;      // the "other player" is this same account: closing would cut both ends
        try { Steamworks.SteamNetworking.CloseP2PSessionWithUser(new Steamworks.CSteamID(id)); } catch { }
    }

    // DEV: TWO INSTANCES ON ONE STEAM ACCOUNT ([MenaceCoop_Dev] SteamSelfTest).
    //
    // Every rig on the dev machine is logged in as the same Steam user, so a
    // real Steam session between two of them is "a player connecting to
    // themselves": same SteamID at both ends. With this on, both instances
    // address that one ID and are told apart by CHANNEL instead -- whatever is
    // for the host travels on channel 10, whatever is for the client on 11 --
    // so each reads only what the other sent. Session-error checks are skipped
    // (there is no second user to have a session with). Everything above the
    // transport, and the lobby in front of it, runs exactly as for two players.
    // Compiled out of release builds, like the other dev tools.
#if COOP_DEVTOOLS
    internal static bool SteamSelfTest => Plugin.SteamSelfTest != null && Plugin.SteamSelfTest.Value;
#else
    internal static bool SteamSelfTest => false;
#endif
    private static int SteamSendChannel => SteamSelfTest ? (Mode == CoopMode.Host ? 11 : 10) : 0;
    private static int SteamReadChannel => SteamSelfTest ? (Mode == CoopMode.Host ? 10 : 11) : 0;
    private static int _lastPingAt;
    private static int _lastStateCheckAt;
    private static bool _steamErrorLogged;

    internal static void StartSteam(CoopMode mode, ulong partnerSteamId)
        => StartSteam(mode, new System.Collections.Generic.List<ulong> { partnerSteamId });

    internal static void StartSteam(CoopMode mode, System.Collections.Generic.List<ulong> ids)
    {
        Stop();
        // A HOST may start with nobody listed: its lobby (SteamLobby) adds each
        // player as they enter. A client always has exactly one partner, the host.
        if (mode == CoopMode.Off || ids == null || (ids.Count == 0 && mode != CoopMode.Host)) return;
        Mode = mode;
        Transport = TransportKind.Steam;
        _partnerSteamId = ids.Count > 0 ? ids[0] : 0;
        _steamAllowed.Clear();
        _steamPingAt.Clear();
        if (mode == CoopMode.Host) foreach (var id in ids) _steamAllowed.Add(id);
        _steamActive = true;
        _steamErrorLogged = false;
        _steamSendFailures = 0;
        _lastPingAt = 0;
        try { Steamworks.SteamNetworking.AllowP2PPacketRelay(true); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[NET] AllowP2PPacketRelay failed: {e.Message}"); }
        Status = mode == CoopMode.Host
            ? (ids.Count == 0 ? "Steam: hosting, waiting for players..."
                : ids.Count > 1 ? $"Steam: waiting for {ids.Count} players (each must connect too)..."
                : "Steam: waiting for partner (they must connect too)...")
            : "Steam: connecting to the host...";
        Plugin.Logger.LogInfo(ids.Count == 0
            ? "[NET] Steam P2P host started; players are added as they enter the lobby"
            : $"[NET] Steam P2P handshake with {string.Join(", ", ids)} started");
    }

    // HOST: one more player may connect (they entered the Steam lobby). The
    // handshake pings to them start on the next PumpSteamHost.
    internal static void AllowSteamPeer(ulong id)
    {
        if (!_steamActive || Mode != CoopMode.Host || id == 0) return;
        if (_steamAllowed.Add(id)) Plugin.Logger.LogInfo($"[NET] Steam P2P handshake with {id} started (lobby member)");
    }

    private static void StopSteam()
    {
        if (!_steamActive) return;
        _steamActive = false;
        if (_partnerSteamId != 0) CloseSteamSession(_partnerSteamId);
        foreach (var id in _steamAllowed) CloseSteamSession(id);
        _steamAllowed.Clear();
        _partnerSteamId = 0;
    }

    // Called every frame from CoopRunner (main thread -- Steam API affinity).
    internal static void PumpSteam()
    {
        if (!_steamActive) return;
        if (Mode == CoopMode.Host) { PumpSteamHost(); return; }
        try
        {
            var partner = new Steamworks.CSteamID(_partnerSteamId);
            int now = Environment.TickCount;

            if (!_connected && now - _lastPingAt > 2000)
            {
                _lastPingAt = now;
                Steamworks.SteamNetworking.AcceptP2PSessionWithUser(partner);
                SteamSend(new CoopMsg { T = "ping", Ver = Plugin.PluginVersion });
            }

            while (Steamworks.SteamNetworking.IsP2PPacketAvailable(out uint size, SteamReadChannel))
            {
                var buf = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(size);
                if (!Steamworks.SteamNetworking.ReadP2PPacket(buf, size, out uint msgSize, out Steamworks.CSteamID remote, SteamReadChannel))
                    break;
                if (remote.m_SteamID != _partnerSteamId) continue; // strangers are ignored

                if (!_connected)
                {
                    _connected = true;
                    Status = "Steam: connected to partner";
                    Plugin.Logger.LogInfo("[NET] Steam P2P session established");
                    Send(Hello());
                }

                var managed = new byte[msgSize];
                for (int i = 0; i < msgSize; i++) managed[i] = buf[i];
                var msg = JsonSerializer.Deserialize<CoopMsg>(Encoding.UTF8.GetString(managed));
                if (msg != null && msg.T != "ping") Incoming.Enqueue(msg);
            }

            if (_connected)
            {
                while (Outgoing.TryDequeue(out var m)) SteamSend(m);

                if (!SteamSelfTest && now - _lastStateCheckAt > 3000)
                {
                    _lastStateCheckAt = now;
                    if (Steamworks.SteamNetworking.GetP2PSessionState(partner, out var st) && st.m_eP2PSessionError != 0)
                    {
                        Plugin.Logger.LogWarning($"[NET] Steam P2P session error {st.m_eP2PSessionError}; disconnected");
                        _connected = false;
                        Status = "Steam: connection lost, retrying...";
                        _lastPingAt = 0; // resume handshake pings
                    }
                }
            }
        }
        catch (Exception e)
        {
            if (!_steamErrorLogged)
            {
                _steamErrorLogged = true;
                Plugin.Logger.LogError($"[NET] Steam P2P failure (is Steam running?): {e}");
                Status = "Steam networking unavailable (see log)";
            }
        }
    }

    private static void SteamSend(CoopMsg msg) => SteamSend(msg, _partnerSteamId);

    // A REFUSED SEND MUST NOT BE SILENT. Steam returns false when it will not
    // take the packet (too large, or its send buffer is full), and the message
    // is then simply gone -- which over a save transfer looks like "connected,
    // but nothing happens". Handshake pings are expected to be refused until
    // the other side is there, so they are not counted.
    private static int _steamSendFailures;

    private static void SteamSend(CoopMsg msg, ulong to)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));
        bool ok = Steamworks.SteamNetworking.SendP2PPacket(
            new Steamworks.CSteamID(to), payload, (uint)payload.Length,
            Steamworks.EP2PSend.k_EP2PSendReliable, SteamSendChannel);
        if (ok || msg.T == "ping") return;
        _steamSendFailures++;
        if (_steamSendFailures <= 10 || _steamSendFailures % 100 == 0)
            Plugin.Logger.LogError($"[NET] Steam REFUSED a packet to {to}: '{msg.T}', {payload.Length} bytes (failure {_steamSendFailures}) -- that message is lost");
    }

    // HOST over Steam (v5): one link per allowed player. Same mutual-send
    // handshake as before, per player; their packets carry the link, and each
    // link's queue is drained here.
    private static void PumpSteamHost()
    {
        try
        {
            int now = Environment.TickCount;
            foreach (var id in _steamAllowed)
            {
                if (HostLinks.BySteamId(id) != null) continue;
                if (_steamPingAt.TryGetValue(id, out int at) && now - at < 2000) continue;
                _steamPingAt[id] = now;
                var cid = new Steamworks.CSteamID(id);
                Steamworks.SteamNetworking.AcceptP2PSessionWithUser(cid);
                SteamSend(new CoopMsg { T = "ping", Ver = Plugin.PluginVersion }, id);
            }

            while (Steamworks.SteamNetworking.IsP2PPacketAvailable(out uint size, SteamReadChannel))
            {
                var buf = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(size);
                if (!Steamworks.SteamNetworking.ReadP2PPacket(buf, size, out uint msgSize, out Steamworks.CSteamID remote, SteamReadChannel))
                    break;
                ulong rid = remote.m_SteamID;
                if (!_steamAllowed.Contains(rid)) continue;          // strangers are ignored
                var link = HostLinks.BySteamId(rid);
                if (link == null)
                {
                    int max = Math.Max(2, Math.Min(Peers.MaxSeats, Plugin.MaxPlayers?.Value ?? 4));
                    if (HostLinks.Count >= max - 1) continue;          // full
                    link = HostLinks.AddSteam(rid);
                    Plugin.Logger.LogInfo($"[NET] Steam P2P session with {rid} established");
                }
                var managed = new byte[msgSize];
                for (int i = 0; i < msgSize; i++) managed[i] = buf[i];
                var msg = JsonSerializer.Deserialize<CoopMsg>(Encoding.UTF8.GetString(managed));
                if (msg == null) continue;
                if (msg.T == "ping")
                {
                    // A client only pings while it is NOT connected. A ping on a
                    // link whose player was already admitted therefore means
                    // they started over (restarted the game, or Disconnect then
                    // Join) while Steam kept the session alive under us -- no
                    // socket closes to say so. Treat it as TCP would: that link
                    // is gone, and a fresh one greets them again. (Their pings
                    // from before the first hello arrive before it, on a link
                    // with no seat yet, so this cannot fire on a first connect.)
                    if (link.Seat > 0)
                    {
                        Plugin.Logger.LogInfo($"[NET] {rid} is handshaking again on a live link -- they reconnected; replacing link {link.Id}");
                        HostLinks.ReplaceSteam(link);
                        HostLinks.AddSteam(rid);
                    }
                    continue;
                }
                msg.ViaLink = link.Id;
                if (link.Seat > 0) msg.From = link.Seat;
                Incoming.Enqueue(msg);
            }

            foreach (var link in HostLinks.Snapshot())
            {
                if (link.SteamId == 0 || !link.Up) continue;
                while (link.Out.TryDequeue(out var m)) SteamSend(m, link.SteamId);
            }

            if (!SteamSelfTest && now - _lastStateCheckAt > 3000)
            {
                _lastStateCheckAt = now;
                foreach (var link in HostLinks.Snapshot())
                {
                    if (link.SteamId == 0 || !link.Up) continue;
                    if (Steamworks.SteamNetworking.GetP2PSessionState(new Steamworks.CSteamID(link.SteamId), out var st)
                        && st.m_eP2PSessionError != 0)
                    {
                        Plugin.Logger.LogWarning($"[NET] Steam P2P session error {st.m_eP2PSessionError} with {link.SteamId}; dropped");
                        HostLinks.DropSteam(link);
                    }
                }
            }
        }
        catch (Exception e)
        {
            if (!_steamErrorLogged)
            {
                _steamErrorLogged = true;
                Plugin.Logger.LogError($"[NET] Steam P2P failure (is Steam running?): {e}");
                Status = "Steam networking unavailable (see log)";
            }
        }
    }
    // ---- end Steam P2P ----

    private static bool Stale(int gen) => gen != Volatile.Read(ref _generation);

    private static void ConnectionLoop(int gen, CoopMode mode, string hostAddress, int port)
    {
        while (!Stale(gen))
        {
            TcpClient client = null;
            try
            {
                if (mode == CoopMode.Host)
                {
                    var listener = new TcpListener(IPAddress.Any, port);
                    _listener = listener;
                    listener.Start();
                    Status = $"Hosting on port {port}, waiting for partner...";
                    Plugin.Logger.LogInfo($"[NET] {Status}");
                    client = listener.AcceptTcpClient();
                }
                else
                {
                    Status = $"Connecting to {hostAddress}:{port}...";
                    Plugin.Logger.LogInfo($"[NET] {Status}");
                    client = new TcpClient();
                    client.Connect(hostAddress, port);
                }

                if (Stale(gen)) { client.Close(); return; }

                _client = client;
                client.NoDelay = true;
                // PASSWORD FIRST: nothing but the handshake is read from this
                // peer until it has proved it knows the session password.
                client.ReceiveTimeout = 10000;
                try { _cipher = CoopAuth.Handshake(client.GetStream(), mode == CoopMode.Host, CoopAuth.Password); }
                catch (AuthRefusedException ae)
                {
                    string who = "?";
                    try { who = client.Client.RemoteEndPoint?.ToString(); } catch { }
                    if (mode == CoopMode.Host)
                    {
                        // Slow down anyone guessing: 2 s, doubling to 30 s.
                        _authFailures++;
                        int wait = Math.Min(30000, 2000 << Math.Min(4, _authFailures - 1));
                        Plugin.Logger.LogWarning($"[NET] REFUSED a connection from {who}: {ae.Message} (failure {_authFailures}; next accept in {wait / 1000}s)");
                        Status = $"Refused a connection ({ae.Message}) -- waiting for partner...";
                        client.Close();
                        Thread.Sleep(wait);
                        continue;
                    }
                    Plugin.Logger.LogError($"[NET] The host REFUSED us: {ae.Message}. Not retrying -- check the password and press Join again.");
                    Status = $"Refused by host: {ae.Message} -- check the password";
                    client.Close();
                    return;
                }
                client.ReceiveTimeout = 0;
                _authFailures = 0;
                Plugin.Logger.LogInfo("[NET] Password accepted both ways; link encrypted (AES-GCM)");
                _connected = true;
                Status = $"Connected ({client.Client.RemoteEndPoint})";
                Plugin.Logger.LogInfo($"[NET] {Status}");
                Send(Hello());

                var writer = new Thread(() => WriteLoop(gen, client)) { IsBackground = true };
                writer.Start();
                ReadLoop(gen, client); // blocks until disconnect
            }
            catch (Exception e)
            {
                if (!Stale(gen))
                {
                    Plugin.Logger.LogWarning($"[NET] Connection error: {e.Message}");

                    // "A SOCKET IN A WAY FORBIDDEN BY ITS ACCESS PERMISSIONS"
                    // MEANS THE PORT IS RESERVED, NOT THAT CO-OP IS BROKEN.
                    //
                    // Windows hands whole TCP blocks to Hyper-V, WSL and Docker,
                    // and those reservations move around between reboots. When
                    // one of them swallows the co-op port the host cannot bind
                    // it, the guest is refused, and the only thing either player
                    // sees is this message repeating every five seconds forever.
                    //
                    // It cost a test session to work out, with the mod itself
                    // entirely innocent -- so it says so, and says what to do,
                    // rather than leaving a player to conclude the mod is broken.
                    if (e is SocketException se && se.SocketErrorCode == SocketError.AccessDenied)
                    {
                        Status = $"Port {port} is reserved by Windows -- pick another port in the panel";
                        Plugin.Logger.LogError(
                            $"[NET] Port {port} cannot be opened: Windows has reserved it (Hyper-V / WSL / Docker take " +
                            $"whole ranges, and they change between reboots). This is NOT a mod fault and retrying will " +
                            $"never succeed.");
                        Plugin.Logger.LogError(
                            "[NET] Fix: set a different Port in the co-op panel (both players must use the same one). " +
                            "To see what Windows has taken, run:  netsh int ipv4 show excludedportrange protocol=tcp");
                    }
                }
            }
            finally
            {
                _connected = false;
                try { client?.Close(); } catch { }
                try { _listener?.Stop(); } catch { }
                _listener = null;
                _client = null;
            }

            if (Stale(gen)) return;
            Status = "Disconnected, retrying in 5s...";
            Plugin.Logger.LogInfo($"[NET] {Status}");
            Thread.Sleep(5000);
        }
    }

    private static void ReadLoop(int gen, TcpClient client)
    {
        var stream = client.GetStream();
        var lenBuf = new byte[4];
        while (!Stale(gen) && client.Connected)
        {
            ReadExact(stream, lenBuf, 4);
            int len = BitConverter.ToInt32(lenBuf, 0);
            if (len <= 0 || len > 1_000_000) throw new IOException($"bad frame length {len}");
            var buf = new byte[len];
            ReadExact(stream, buf, len);
            // A frame that fails authentication ends the link (the throw lands in
            // ConnectionLoop, which reconnects -- and re-runs the password check).
            var plain = _cipher.Open(buf);
            var msg = JsonSerializer.Deserialize<CoopMsg>(Encoding.UTF8.GetString(plain));
            if (msg != null) Incoming.Enqueue(msg);
        }
    }

    private static CoopCipher _cipher;
    private static int _authFailures;

    internal static void ReadExactPublic(NetworkStream stream, byte[] buf, int count) => ReadExact(stream, buf, count);

    private static void ReadExact(NetworkStream stream, byte[] buf, int count)
    {
        int off = 0;
        while (off < count)
        {
            int n = stream.Read(buf, off, count - off);
            if (n <= 0) throw new IOException("connection closed");
            off += n;
        }
    }

    private static void WriteLoop(int gen, TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            while (!Stale(gen) && client.Connected)
            {
                if (Outgoing.TryDequeue(out var msg))
                {
                    var payload = _cipher.Seal(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg)));
                    stream.Write(BitConverter.GetBytes(payload.Length), 0, 4);
                    stream.Write(payload, 0, payload.Length);
                }
                else
                {
                    Thread.Sleep(5);
                }
            }
        }
        catch (Exception e)
        {
            if (!Stale(gen)) Plugin.Logger.LogWarning($"[NET] Write loop ended: {e.Message}");
        }
    }

    // Host: our hello for one new link.
    internal static CoopMsg HelloFor(Link link)
    {
        var m = Hello();
        m.From = Peers.HostSeat;
        m.To = link.Seat;
        return m;
    }

    // Host: pass a client's message on to the other clients, sender kept.
    internal static void Forward(CoopMsg msg)
    {
        if (Mode != CoopMode.Host) return;
        HostLinks.Enqueue(msg);
    }

    private static CoopMsg Hello() => new CoopMsg
    {
        T = "hello", Ver = Plugin.PluginVersion, GVer = Plugin.GameFingerprint, Mods = ModList.Local,
        Key = Peers.LocalKey, Name = Peers.LocalCustomName,
    };

    // To one seat. At N=2 there is one link, so this differs from Send only in
    // what the receiver can assert (Peers.Accept).
    internal static void SendTo(CoopMsg msg, int seat)
    {
        if (Mode == CoopMode.Off) return;
        msg.To = seat;
        Send(msg);
    }

    // To every other player.
    internal static void Broadcast(CoopMsg msg) => Send(msg);

    internal static void Send(CoopMsg msg)
    {
        if (Mode == CoopMode.Off) return;
        msg.From = Peers.LocalSeat;
        GuestReload.Note(msg);   // host: permanent-state messages feed the reload journal
        // The host's fog refreshes on the same events a client's does (see FogSync.Tick).
        if (Mode == CoopMode.Host) FogSync.MarkDirty(msg.T);
        if (Mode == CoopMode.Host) HostLinks.Enqueue(msg);   // TCP and Steam alike
        else Outgoing.Enqueue(msg);
    }

    internal static void Shutdown() => Stop();
}
