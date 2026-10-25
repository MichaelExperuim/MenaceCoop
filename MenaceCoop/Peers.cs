using System;
using System.Collections.Generic;

namespace MenaceCoop;

// WHO IS IN THE SESSION -- v5 Phase 0 (V5-PLAN.md, ARCHITECTURE-NPLAYER.md §4).
//
// Every player has a SEAT: the host is seat 0, clients are 1..MaxSeats-1. Seats,
// not "host"/"guest", are what ownership, messages and (later) colours key on, so
// nothing below has to change shape when a third player arrives.
//
// A seat belongs to an INSTALL KEY, not to join order: a player who drops and
// comes back must get their own seat -- and so their own squads -- back, and join
// order would hand them to whoever reconnected first. The key is a GUID generated
// once per install and kept in MelonPreferences. NOT the Steam ID: the direct-IP
// path has none, and every install on the test rig runs under one Steam account,
// so Steam IDs would collide exactly where the testing happens.
//
// Phase 0 runs this at N=2 over the existing single link: there is one client,
// it is seat 1, and every message is still "to the partner". From/To are stamped
// and checked anyway so the invariant is live long before it is load-bearing.
internal static class Peers
{
    internal const int HostSeat = 0;
    internal const int Everyone = -1;   // CoopMsg.To: all other players
    internal const int MaxSeats = 8;    // built for 8 (user 2026-10-02); MaxPlayers caps it lower

    // This install's identity. Generated on first launch (MelonEntry).
    internal static string LocalKey = "";
    // DISPLAY NAMES (user 2026-10-02): set in the co-op panel under Join, shown
    // to everyone -- unit tags, cursor markers, the player list, the squad picker
    // and the turn banner. A player who has set none keeps the old labels
    // (HOST / GUEST / Player N), so nothing changes for anyone who ignores it.
    internal const int MaxNameLength = 16;
    internal static string LocalCustomName => Clean(Plugin.PlayerName?.Value);
    internal static string LocalName => LocalCustomName ?? $"Player {LocalSeat + 1}";

    // Trimmed, length-capped, free of the roster's separators; null when empty.
    internal static string Clean(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new System.Text.StringBuilder();
        foreach (char c in raw.Trim())
        {
            if (c == ';' || c == ':' || char.IsControl(c)) continue;
            sb.Append(c);
            if (sb.Length >= MaxNameLength) break;
        }
        string n = sb.ToString().Trim();
        return n.Length == 0 ? null : n;
    }

    private static string RawName(int seat)
    {
        if (seat == LocalSeat) return LocalCustomName;
        return _nameBySeat.TryGetValue(seat, out var n) ? Clean(n) : null;
    }

    // The name a player chose, or null. TWO PLAYERS WITH ONE NAME (user
    // 2026-10-02): identity is the seat, so nothing breaks, but "POG IS ACTING"
    // would not say which. The later seat gets " (2)", " (3)" ... -- computed
    // from the shared roster, so every machine shows the same suffixes.
    internal static string CustomName(int seat)
    {
        string n = RawName(seat);
        if (n == null) return null;
        int same = 0;
        foreach (int s in SessionSeats())
            if (s < seat && string.Equals(RawName(s), n, StringComparison.OrdinalIgnoreCase)) same++;
        return same == 0 ? n : $"{n} ({same + 1})";
    }

    // Short label over units and on buttons: the chosen name, else HOST / GUEST / Pn.
    internal static string Label(int seat) => CustomName(seat) ?? SeatColors.Tag(seat);

    // This player changed their name in the panel.
    internal static void SetLocalName(string raw)
    {
        string n = Clean(raw);
        Plugin.PlayerName.Value = n ?? "";
        try { MelonLoader.MelonPreferences.Save(); } catch { }
        Plugin.Logger.LogInfo($"[PEER] Display name set to '{n ?? "(none)"}'");
        RosterVersion++;
        if (!CoopNet.Connected) return;
        if (CoopNet.Mode == CoopMode.Host) BroadcastRoster();
        else CoopNet.Send(new CoopMsg { T = "pname", Name = n ?? "" });
    }

    // Host: a client renamed itself.
    internal static void HandleName(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Host || m.From <= 0) return;
        _nameBySeat[m.From] = Clean(m.Name);
        Plugin.Logger.LogInfo($"[PEER] Player {m.From + 1} is now '{Clean(m.Name) ?? "(none)"}'");
        BroadcastRoster();
    }

    // Client: assigned by the host's "seat" message. 1 until then -- at N=2 that is
    // also the only seat a client can get, so an older host that never sends one
    // still yields the right answer.
    private static int _clientSeat = 1;
    internal static int LocalSeat => CoopNet.Mode == CoopMode.Host ? HostSeat : _clientSeat;

    // Host: SOME client's seat (the lowest live one), for the places that still
    // mean "the partner" -- every use of it is a two-player assumption; grep for it.
    // Client: the host, its only link.
    internal static int LinkSeat
    {
        get
        {
            if (CoopNet.Mode != CoopMode.Host) return HostSeat;
            var s = HostLinks.Seats();
            return s.Count > 0 ? s[0] : 1;
        }
    }

    // Every seat in the session, host first, ascending -- the turn order.
    // Host: itself plus every admitted live client. Client: it does not decide
    // turns, so this only has to be plausible (host + itself).
    internal static List<int> SessionSeats()
    {
        var seats = new List<int> { HostSeat };
        if (CoopNet.Mode == CoopMode.Host) seats.AddRange(HostLinks.Seats());
        else if (CoopNet.Connected)
        {
            if (_roster.Count > 0) { foreach (var s in _roster) if (s != HostSeat) seats.Add(s); }
            else seats.Add(LocalSeat);
            if (!seats.Contains(LocalSeat)) seats.Add(LocalSeat);
            seats.Sort();
        }
        return seats;
    }

    // Is that seat's player connected right now? (A client cannot see the other
    // clients' links, so it says yes; it never decides anything on this.)
    internal static bool IsConnected(int seat)
    {
        if (seat == LocalSeat) return true;
        if (CoopNet.Mode == CoopMode.Host) return HostLinks.BySeat(seat) != null;
        if (!CoopNet.Connected) return false;
        return seat == HostSeat || _roster.Count == 0 || _roster.Contains(seat);
    }

    // Host: install key -> seat for the whole game session, so a rejoin keeps its
    // seat. Not yet persisted with a battle save (Phase 3 rejoin).
    private static readonly Dictionary<string, int> _seatByKey = new();
    private static readonly Dictionary<int, string> _nameBySeat = new();

    internal static string NameOf(int seat) => CustomName(seat) ?? $"Player {seat + 1}";

    // Host: the seat most recently admitted (the hello being handled right now).
    internal static int LastAdmitted = -1;

    // Host, on a client's hello. Returns the seat it now holds, or -1.
    internal static int Admit(CoopMsg hello)
    {
        var link = HostLinks.ById(hello.ViaLink);
        if (link == null)
        {
            Plugin.Logger.LogWarning($"[PEER] hello from an unknown link {hello.ViaLink}; ignored");
            return -1;
        }
        string key = string.IsNullOrEmpty(hello.Key) ? "" : hello.Key;
        if (key.Length > 0 && key == LocalKey)
        {
            Plugin.Logger.LogWarning("[PEER] A player's install id equals ours (MelonPreferences.cfg copied between installs?). "
                + "Delete InstallId from one of them to give it a new one.");
            key = "";
        }
        if (link.Seat > 0) return link.Seat;   // a repeated hello on the same link

        // A known install gets its own seat back, unless someone live holds it.
        // Anyone else gets the lowest seat no live client holds -- which, for a
        // stranger arriving after a player left, is that player's seat and squads:
        // at two players exactly the pre-v5 behaviour (a new partner inherits).
        int seat = -1;
        bool known = key.Length > 0 && _seatByKey.TryGetValue(key, out seat) && HostLinks.BySeat(seat) == null;
        if (!known)
        {
            seat = 1;
            while (seat < MaxSeats && HostLinks.BySeat(seat) != null) seat++;
            if (seat >= MaxSeats) { Plugin.Logger.LogWarning("[PEER] No free seat"); return -1; }
            var stale = new List<string>();
            foreach (var kv in _seatByKey) if (kv.Value == seat) stale.Add(kv.Key);
            foreach (var k in stale) _seatByKey.Remove(k);
            if (key.Length > 0) _seatByKey[key] = seat;
        }
        link.Seat = seat;
        LastAdmitted = seat;
        _nameBySeat[seat] = hello.Name;
        hello.From = seat;
        Plugin.Logger.LogInfo($"[PEER] {hello.Name ?? "player"} (link {link.Id}) {(known ? "rejoined" : "admitted")} -> seat {seat}"
            + (key.Length > 0 ? "" : " (no install id: older build or duplicate)")
            + $"; seats now [{string.Join(",", SessionSeats())}]");
        // The SAME player back (known install id): their units come back to them.
        if (known) Ownership.ReturnLent(seat);
        return seat;
    }

    // Host: a client's link dropped.
    internal static void NoteLost(int seat)
    {
        _ready.Remove(seat);
        Replication.ReleaseLockOf(seat, "left the session");
        Ownership.LendToHost(seat);
        Plugin.Logger.LogInfo($"[PEER] {NameOf(seat)} (seat {seat}) left; seats now [{string.Join(",", SessionSeats())}]");
        BroadcastRoster();
    }

    // ---- who is in the session, as every client sees it ----
    // A client is linked to the host only, so it learns of the OTHER clients
    // from this list: "seat:name;seat:name". The host sends it whenever someone
    // joins or leaves. Drives the panel's player list and the client's idea of
    // the seat order (it still never decides turns).
    private static readonly List<int> _roster = new();
    internal static int RosterVersion;

    internal static void BroadcastRoster()
    {
        if (CoopNet.Mode != CoopMode.Host) return;
        var parts = new List<string>();
        foreach (var s in SessionSeats()) parts.Add($"{s}:{CustomName(s) ?? ""}");
        CoopNet.Send(new CoopMsg { T = "players", Data = string.Join(";", parts) });
        RosterVersion++;
    }

    internal static void HandleRoster(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest || m.Data == null) return;
        _roster.Clear();
        foreach (var part in m.Data.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int c = part.IndexOf(':');
            if (c <= 0 || !int.TryParse(part.Substring(0, c), out int seat)) continue;
            _roster.Add(seat);
            if (seat != LocalSeat) _nameBySeat[seat] = part.Substring(c + 1);
        }
        RosterVersion++;
        Replication.ReleaseLocksNotIn(_roster);
        GuestReload.OnRoster(_roster);
        Plugin.Logger.LogInfo($"[PEER] Players: {m.Data}");
    }

    internal static void ClearRoster() { _roster.Clear(); RosterVersion++; }

    // ---- who is in the battle (host) ----
    // Each client says "ready" once its battle has loaded; the host holds fire
    // until ALL connected clients have.
    private static readonly HashSet<int> _ready = new();
    internal static void SetReady(int seat) { if (seat > 0) _ready.Add(seat); }
    internal static void ClearReady() => _ready.Clear();
    internal static void Unready(int seat) => _ready.Remove(seat);
    internal static bool IsReady(int seat) => seat == LocalSeat || CoopNet.Mode != CoopMode.Host || _ready.Contains(seat);
    internal static bool AllClientsReady
    {
        get
        {
            var s = HostLinks.Seats();
            if (s.Count == 0) return false;
            foreach (var x in s) if (!_ready.Contains(x)) return false;
            return true;
        }
    }

    // Client: the host told us our seat.
    internal static void HandleSeat(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        if (m.Prm < 1 || m.Prm >= MaxSeats)
        {
            Plugin.Logger.LogWarning($"[PEER] Host assigned an impossible seat {m.Prm}; keeping {_clientSeat}");
            return;
        }
        if (m.Prm != _clientSeat) Plugin.Logger.LogInfo($"[PEER] Host assigned us seat {m.Prm} (was {_clientSeat})");
        else Plugin.Logger.LogInfo($"[PEER] Seat {m.Prm} confirmed by the host");
        _clientSeat = m.Prm;
    }

    // Client, on the host's hello.
    internal static void NoteHost(CoopMsg hello) => _nameBySeat[HostSeat] = hello.Name;

    // Every incoming message passes through here (CoopRunner). The host trusts
    // the LINK for the sender (HostLinks stamps From), forwards what the other
    // clients must see (Relay), and applies what is for it. A client receives
    // from the host only -- From is the host or a client the host forwarded.
    private static int _fromWarnings;
    internal static bool Accept(CoopMsg m)
    {
        if (CoopNet.Mode == CoopMode.Host)
        {
            if (m.T != "hello" && m.ViaLink > 0)
            {
                var link = HostLinks.ById(m.ViaLink);
                if (link == null || link.Seat <= 0)
                {
                    // Before its hello was handled: hold nothing back, but say so.
                    if (_fromWarnings++ < 20)
                        Plugin.Logger.LogWarning($"[PEER] '{m.T}' from link {m.ViaLink} before its seat was assigned");
                }
                else m.From = link.Seat;
            }
            Relay.MaybeForward(m);
            if (m.To > 0) return false;           // client-to-client: forwarded, not ours
            return true;
        }
        // "seat" is the one message addressed to a seat we do not know we hold yet.
        if (m.To >= 0 && m.To != LocalSeat && m.T != "seat")
        {
            if (_fromWarnings++ < 20)
                Plugin.Logger.LogWarning($"[PEER] Dropped '{m.T}' addressed to seat {m.To} (we are seat {LocalSeat})");
            return false;
        }
        if (m.From == LocalSeat && m.From > 0 && _fromWarnings++ < 20)
            Plugin.Logger.LogWarning($"[PEER] '{m.T}' came back to us (From = our seat {LocalSeat})");
        return true;
    }
}

// ONE COLOUR PER SEAT (user 2026-10-02): host blue, player 2 orange -- today's two,
// unchanged -- then red, then green. Seats 4-7 are a PROPOSAL the user has not
// confirmed (purple, yellow, cyan, pink); MaxPlayers defaults to 4, so they only
// appear if someone raises it. Spectator grey.
internal static class SeatColors
{
    private static readonly UnityEngine.Color[] _cols =
    {
        new(0.45f, 0.85f, 1f, 0.95f),   // 0 host: blue
        new(1f, 0.72f, 0.32f, 0.95f),   // 1: orange
        new(1f, 0.38f, 0.38f, 0.95f),   // 2: red
        new(0.45f, 0.95f, 0.45f, 0.95f),// 3: green
        new(0.78f, 0.5f, 1f, 0.95f),    // 4: purple  (unconfirmed)
        new(1f, 0.95f, 0.4f, 0.95f),    // 5: yellow  (unconfirmed)
        new(0.4f, 1f, 0.95f, 0.95f),    // 6: cyan    (unconfirmed)
        new(1f, 0.55f, 0.85f, 0.95f),   // 7: pink    (unconfirmed)
    };
    internal static readonly UnityEngine.Color Spectator = new(0.6f, 0.6f, 0.6f, 0.95f);

    internal static UnityEngine.Color Of(int seat) => seat >= 0 && seat < _cols.Length ? _cols[seat] : Spectator;

    // Unit tag text. Two players keep today's HOST / GUEST.
    internal static string Tag(int seat) => seat == Peers.HostSeat ? "HOST" : seat == 1 ? "GUEST" : $"P{seat + 1}";
}
