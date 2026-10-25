using System.Collections.Generic;

namespace MenaceCoop;

// WHAT THE HOST PASSES ON -- v5 Phase 2 (star topology).
//
// A client is linked to the host only, so anything another client must also see
// has to be forwarded. The rule is "forward everything a client sends, except
// what is addressed to the host alone": requests the host answers, handshakes,
// checks the host compares, file transfers. Forwarding keeps From = the original
// sender, so a receiver applies it exactly as it applied the partner's message
// before v5 -- a move is a move whoever made it.
//
// At two players there are no other clients and this sends nothing.
internal static class Relay
{
    private static readonly HashSet<string> HostOnly = new()
    {
        "hello", "seat", "players", "pname", "ping", "auth1", "auth2",
        "passreq",                                  // the host decides the turn
        "catchreq", "reqreload",                    // guest reload protocol
        "reqsync", "reqhard", "hash", "hashrows", "hashok", "spdone",   // desync checks are host <-> each client
        "ready", "prepready", "synced", "trust",    // per-client readiness
        "roster", "terrain",                        // per-client manifest checks against the host's
        "dbgreq", "sdiereq", "ireq", "dposreq",                // asks the host to do something
        "fmeta", "fchunk", "fdone",                 // file transfers are point to point
    };

    private static int _forwarded;
    internal static int Forwarded => _forwarded;

    internal static void MaybeForward(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Host || m == null || m.T == null) return;
        if (m.From <= 0) return;                    // ours, or an unadmitted link
        if (m.To == Peers.HostSeat) return;         // addressed to us
        if (HostOnly.Contains(m.T)) return;
        if (CoopNet.ClientCount < 2 && m.To < 0) return;   // nobody else to tell
        CoopNet.Forward(m);
        _forwarded++;
    }
}
