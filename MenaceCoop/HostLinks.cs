using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MenaceCoop;

// THE HOST'S END OF A STAR -- v5 Phase 2 (direct IP / TCP).
//
// Before v5 the host accepted ONE connection and stopped listening until it
// dropped. Now it keeps listening: every client that passes the password check
// gets its own Link -- own socket, own cipher (the AES-GCM counters are per
// connection), own send queue and writer thread. A client still has exactly one
// link, to the host, so the client side of CoopNet is unchanged.
//
// What a client sends reaches only the host. Whatever the other clients must
// also see (orders, hits, cursors...) the host FORWARDS -- see Relay.Forward --
// keeping the original sender's seat in From, so each receiver knows whose it is.
//
// Seats are given out on the main thread when the client's hello is handled
// (Peers.Admit); until then a link has Seat -1, and messages read from it carry
// only its LinkId.
internal sealed class Link
{
    internal int Id;
    internal volatile int Seat = -1;
    internal TcpClient Client;
    internal CoopCipher Cipher;
    internal readonly ConcurrentQueue<CoopMsg> Out = new();
    internal volatile bool Up;
    internal string Remote = "?";
    internal ulong SteamId;             // Steam P2P link (no socket); 0 for TCP
}

internal static class HostLinks
{
    private static readonly object _lock = new();
    private static readonly List<Link> _links = new();
    private static int _nextId = 1;
    private static int _authFailures;

    internal static int Count { get { lock (_lock) { int n = 0; foreach (var l in _links) if (l.Up) n++; return n; } } }

    internal static List<Link> Snapshot() { lock (_lock) return new List<Link>(_links); }

    internal static Link ById(int id) { lock (_lock) { foreach (var l in _links) if (l.Id == id) return l; } return null; }
    internal static Link BySteamId(ulong id) { lock (_lock) { foreach (var l in _links) if (l.Up && l.SteamId == id) return l; } return null; }

    // Steam P2P: a player we heard from becomes a link. Its queue is drained on
    // the main thread by CoopNet.PumpSteam (Steam API affinity), not by a thread.
    internal static Link AddSteam(ulong id)
    {
        var link = new Link { SteamId = id, Up = true, Remote = $"steam:{id}" };
        lock (_lock) { link.Id = _nextId++; _links.Add(link); }
        Plugin.Logger.LogInfo($"[NET] Steam P2P link {link.Id} to {id} -- {Count} client(s)");
        CoopNet.NoteLinksChanged();
        link.Out.Enqueue(CoopNet.HelloFor(link));
        return link;
    }

    internal static void DropSteam(Link link) => Drop(link, false);

    // The player on this link reconnected over the SAME Steam session: retire
    // the link (they count as having left, as a closed socket would) but leave
    // the session itself open -- the new link uses it.
    internal static void ReplaceSteam(Link link)
    {
        link.SteamId = 0;
        Drop(link, false);
    }

    internal static Link BySeat(int seat) { lock (_lock) { foreach (var l in _links) if (l.Up && l.Seat == seat) return l; } return null; }

    // Seats of every admitted, live client.
    internal static List<int> Seats()
    {
        var s = new List<int>();
        lock (_lock) foreach (var l in _links) if (l.Up && l.Seat > 0 && !s.Contains(l.Seat)) s.Add(l.Seat);
        s.Sort();
        return s;
    }

    internal static void CloseAll()
    {
        List<Link> all;
        lock (_lock) { all = new List<Link>(_links); _links.Clear(); }
        foreach (var l in all) { l.Up = false; try { l.Client?.Close(); } catch { } }
    }

    // ---- sending (any thread) ----
    internal static void Enqueue(CoopMsg msg)
    {
        lock (_lock)
        {
            foreach (var l in _links)
            {
                if (!l.Up) continue;
                if (msg.To >= 0 && l.Seat != msg.To) continue;
                // A forwarded message never goes back to the seat it came from.
                if (msg.From >= 0 && msg.From != Peers.HostSeat && l.Seat == msg.From) continue;
                l.Out.Enqueue(msg);
            }
        }
    }

    // ---- accept loop (network thread) ----
    internal static void AcceptLoop(int gen, int port, Func<int, bool> stale)
    {
        TcpListener listener = null;
        while (!stale(gen))
        {
            try
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                CoopNet.SetListener(listener);
                CoopNet.Status = $"Hosting on port {port}, waiting for players...";
                Plugin.Logger.LogInfo($"[NET] {CoopNet.Status}");
                while (!stale(gen))
                {
                    var client = listener.AcceptTcpClient();
                    if (stale(gen)) { client.Close(); return; }
                    Admit(gen, client, stale);
                }
            }
            catch (Exception e)
            {
                if (stale(gen)) return;
                Plugin.Logger.LogWarning($"[NET] Listener error: {e.Message}");
                if (e is SocketException se && se.SocketErrorCode == SocketError.AccessDenied)
                {
                    CoopNet.Status = $"Port {port} is reserved by Windows -- pick another port in the panel";
                    Plugin.Logger.LogError($"[NET] Port {port} cannot be opened: Windows has reserved it (Hyper-V / WSL / Docker). "
                        + "Set a different Port in the co-op panel (all players the same). See: netsh int ipv4 show excludedportrange protocol=tcp");
                }
            }
            finally { try { listener?.Stop(); } catch { } }
            if (stale(gen)) return;
            Thread.Sleep(5000);
        }
    }

    // Password check, then a link with its own reader and writer. Done on the
    // accept thread so a guesser is slowed down before the next accept.
    private static void Admit(int gen, TcpClient client, Func<int, bool> stale)
    {
        string who = "?";
        try { who = client.Client.RemoteEndPoint?.ToString(); } catch { }

        // MaxPlayers counts the host.
        int max = Math.Max(2, Math.Min(Peers.MaxSeats, Plugin.MaxPlayers?.Value ?? 4));
        if (Count >= max - 1)
        {
            Plugin.Logger.LogWarning($"[NET] Refused {who}: the session is full ({max} players, MaxPlayers)");
            try { client.Close(); } catch { }
            return;
        }

        CoopCipher cipher;
        try
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 10000;
            cipher = CoopAuth.Handshake(client.GetStream(), true, CoopAuth.Password);
            client.ReceiveTimeout = 0;
        }
        catch (AuthRefusedException ae)
        {
            _authFailures++;
            int wait = Math.Min(30000, 2000 << Math.Min(4, _authFailures - 1));
            Plugin.Logger.LogWarning($"[NET] REFUSED a connection from {who}: {ae.Message} (failure {_authFailures}; next accept in {wait / 1000}s)");
            CoopNet.Status = $"Refused a connection ({ae.Message}) -- waiting for players...";
            try { client.Close(); } catch { }
            Thread.Sleep(wait);
            return;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[NET] Handshake with {who} failed: {e.Message}");
            try { client.Close(); } catch { }
            return;
        }
        _authFailures = 0;

        var link = new Link { Client = client, Cipher = cipher, Up = true, Remote = who };
        lock (_lock) { link.Id = _nextId++; _links.Add(link); }
        Plugin.Logger.LogInfo($"[NET] Password accepted both ways; link {link.Id} to {who} encrypted (AES-GCM) -- {Count} client(s)");
        CoopNet.NoteLinksChanged();
        // Our hello goes to this link only; it must not reach the others again.
        link.Out.Enqueue(CoopNet.HelloFor(link));

        new Thread(() => WriteLoop(gen, link, stale)) { IsBackground = true, Name = $"MenaceCoop.Out{link.Id}" }.Start();
        new Thread(() => ReadLoop(gen, link, stale)) { IsBackground = true, Name = $"MenaceCoop.In{link.Id}" }.Start();
    }

    private static void ReadLoop(int gen, Link link, Func<int, bool> stale)
    {
        try
        {
            var stream = link.Client.GetStream();
            var lenBuf = new byte[4];
            while (!stale(gen) && link.Up)
            {
                CoopNet.ReadExactPublic(stream, lenBuf, 4);
                int len = BitConverter.ToInt32(lenBuf, 0);
                if (len <= 0 || len > 1_000_000) throw new IOException($"bad frame length {len}");
                var buf = new byte[len];
                CoopNet.ReadExactPublic(stream, buf, len);
                var msg = JsonSerializer.Deserialize<CoopMsg>(Encoding.UTF8.GetString(link.Cipher.Open(buf)));
                if (msg == null) continue;
                // The link, not the message, says who sent it.
                msg.ViaLink = link.Id;
                if (link.Seat > 0) msg.From = link.Seat;
                CoopNet.Incoming.Enqueue(msg);
            }
        }
        catch (Exception e)
        {
            if (!stale(gen)) Plugin.Logger.LogWarning($"[NET] Link {link.Id} (seat {link.Seat}) ended: {e.Message}");
        }
        Drop(link, stale(gen));
    }

    private static void WriteLoop(int gen, Link link, Func<int, bool> stale)
    {
        try
        {
            var stream = link.Client.GetStream();
            while (!stale(gen) && link.Up)
            {
                if (link.Out.TryDequeue(out var msg))
                {
                    var payload = link.Cipher.Seal(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg)));
                    stream.Write(BitConverter.GetBytes(payload.Length), 0, 4);
                    stream.Write(payload, 0, payload.Length);
                }
                else Thread.Sleep(5);
            }
        }
        catch (Exception e)
        {
            if (!stale(gen)) Plugin.Logger.LogWarning($"[NET] Link {link.Id} write ended: {e.Message}");
        }
        Drop(link, stale(gen));
    }

    private static void Drop(Link link, bool quiet)
    {
        bool was;
        lock (_lock) { was = link.Up; link.Up = false; _links.Remove(link); }
        try { link.Client?.Close(); } catch { }
        if (link.SteamId != 0) CoopNet.CloseSteamSession(link.SteamId);
        if (!was || quiet) return;
        Plugin.Logger.LogInfo($"[NET] Player {link.Seat + 1} (link {link.Id}) disconnected -- {Count} client(s) left");
        CoopNet.NoteLinksChanged();
        if (link.Seat > 0) Peers.NoteLost(link.Seat);
    }
}
