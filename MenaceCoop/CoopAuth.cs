using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MenaceCoop;

// PASSWORD + ENCRYPTION FOR THE DIRECT-IP LINK (user, 2026-09-27: "random
// people don't join over our connections, especially since some of these
// people will do it over port forwarding").
//
// Before this, the host accepted ANY TCP connection on its port and trusted
// every message: a stranger who found a forwarded port could issue orders and
// read the whole session in clear.
//
// HANDSHAKE (both sides, before anything else crosses the wire):
//   1. each sends {T:"auth1", Seed:role, Data:nonce(32 random bytes)}
//   2. each sends {T:"auth2", Data:HMAC(key, "proof|<its role>|Nhost|Nguest")}
//   3. each verifies the other's proof (constant-time); only then is the link up.
// The password never crosses the wire, a recorded exchange cannot be replayed
// (fresh nonces from both sides), and the proof is mutual, so a guest cannot be
// fooled by a fake host either. key = PBKDF2-SHA256(password, fixed salt, 100k).
//
// AFTER IT every frame is AES-GCM with a per-direction key derived from the
// same secret and both nonces, and a per-direction counter as the nonce: the
// receiver requires the exact next counter, so a frame cannot be read,
// altered, dropped, reordered or replayed without the link failing.
//
// The Steam transport does not go through this: Steam authenticates the
// partner's SteamID itself and CoopNet already drops every other sender.
//
// TWO DELIBERATE CHOICES -- change them knowingly:
// - The strict next-counter rule assumes an ORDERED, RELIABLE stream (TCP here).
//   Put this framing on a lossy or reordering path (UDP, an N-player fan-out
//   that reorders) and the link will die "at random" -- redesign the counter
//   check first.
// - The PBKDF2 salt is FIXED and the derived key cached, so a connection flood
//   costs the host one HMAC per attempt, not 100k iterations (the backoff in
//   CoopNet does the rest). The price is that precomputation works across all
//   players; the panel's generated code (high entropy, not a personal
//   password) is what makes that irrelevant.
internal sealed class CoopCipher
{
    private readonly AesGcm _send, _recv;
    private ulong _sendCtr, _recvCtr;
    private const int TagLen = 16;

    internal CoopCipher(byte[] sendKey, byte[] recvKey)
    {
        _send = new AesGcm(sendKey);
        _recv = new AesGcm(recvKey);
    }

    private static byte[] Nonce(ulong ctr)
    {
        var n = new byte[12];
        BitConverter.GetBytes(ctr).CopyTo(n, 4);
        return n;
    }

    // [8-byte counter][ciphertext][16-byte tag]
    internal byte[] Seal(byte[] plain)
    {
        ulong ctr = _sendCtr++;
        var outBuf = new byte[8 + plain.Length + TagLen];
        BitConverter.GetBytes(ctr).CopyTo(outBuf, 0);
        var cipher = new Span<byte>(outBuf, 8, plain.Length);
        var tag = new Span<byte>(outBuf, 8 + plain.Length, TagLen);
        _send.Encrypt(Nonce(ctr), plain, cipher, tag);
        return outBuf;
    }

    internal byte[] Open(byte[] frame)
    {
        if (frame.Length < 8 + TagLen) throw new CryptographicException("short frame");
        ulong ctr = BitConverter.ToUInt64(frame, 0);
        if (ctr != _recvCtr) throw new CryptographicException($"frame counter {ctr}, expected {_recvCtr} (replayed or dropped)");
        var plain = new byte[frame.Length - 8 - TagLen];
        _recv.Decrypt(Nonce(ctr), new ReadOnlySpan<byte>(frame, 8, plain.Length),
                      new ReadOnlySpan<byte>(frame, 8 + plain.Length, TagLen), plain);
        _recvCtr++;
        return plain;
    }
}

internal sealed class AuthRefusedException : Exception
{
    internal AuthRefusedException(string m) : base(m) { }
}

internal static class CoopAuth
{
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("MenaceCoop/direct-ip/v4");
    private const int Iterations = 100_000;
    private static string _cachedPw;
    private static byte[] _cachedKey;

    internal static string Password => Plugin.SessionPassword?.Value ?? "";

    // A lobby-style code: 12 characters from 31 unambiguous symbols (~59 bits),
    // grouped for reading aloud. Generated rather than typed so no player's
    // real password ever goes into the mod (a cracked handshake then yields a
    // throwaway code, not a credential that works elsewhere).
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ2345679";
    internal static string NewCode()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 12; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append('-');
            sb.Append(CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]);
        }
        return sb.ToString();
    }

    // The code is read aloud and typed: case, spaces and dashes must not matter
    // ("they typed it in lowercase" would be a refusal with a correct-looking
    // code on both screens). Applied to typed passwords too, for the same reason.
    internal static string Normalize(string pw)
    {
        if (string.IsNullOrEmpty(pw)) return "";
        var sb = new StringBuilder(pw.Length);
        foreach (char c in pw)
            if (!char.IsWhiteSpace(c) && c != '-') sb.Append(char.ToUpperInvariant(c));
        return sb.ToString();
    }

    private static byte[] KeyFor(string password)
    {
        password = Normalize(password);
        if (_cachedKey != null && _cachedPw == password) return _cachedKey;
        _cachedKey = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Salt, Iterations, HashAlgorithmName.SHA256, 32);
        _cachedPw = password;
        return _cachedKey;
    }

    private static byte[] Hmac(byte[] key, string label, byte[] a, byte[] b)
    {
        using var h = new HMACSHA256(key);
        var l = Encoding.UTF8.GetBytes(label);
        var buf = new byte[l.Length + a.Length + b.Length];
        l.CopyTo(buf, 0); a.CopyTo(buf, l.Length); b.CopyTo(buf, l.Length + a.Length);
        return h.ComputeHash(buf);
    }

    // Runs on the connection thread, right after the TCP connect/accept.
    // Returns the session cipher; throws AuthRefusedException on a wrong
    // password (never retried by the guest) and IOException on a dead link.
    internal static CoopCipher Handshake(NetworkStream stream, bool isHost, string password)
    {
        var key = KeyFor(password);
        var mine = RandomNumberGenerator.GetBytes(32);
        int myRole = isHost ? 1 : 2;

        WritePlain(stream, new CoopMsg { T = "auth1", Seed = myRole, Data = Convert.ToBase64String(mine), Ver = Plugin.PluginVersion });
        var a1 = ReadPlain(stream);
        if (a1?.T != "auth1") throw new AuthRefusedException("the other side is not speaking the MenaceCoop v4 handshake (older version, or not MenaceCoop at all)");
        if (a1.Seed == myRole) throw new AuthRefusedException("both sides are set to the same role (two hosts or two guests)");
        byte[] theirs;
        try { theirs = Convert.FromBase64String(a1.Data ?? ""); } catch { throw new AuthRefusedException("malformed handshake"); }
        if (theirs.Length != 32) throw new AuthRefusedException("malformed handshake");

        byte[] nHost = isHost ? mine : theirs, nGuest = isHost ? theirs : mine;
        var myProof = Hmac(key, $"proof|{myRole}|", nHost, nGuest);
        WritePlain(stream, new CoopMsg { T = "auth2", Data = Convert.ToBase64String(myProof) });
        var a2 = ReadPlain(stream);
        byte[] theirProof;
        try { theirProof = Convert.FromBase64String(a2?.Data ?? ""); } catch { theirProof = Array.Empty<byte>(); }
        var expected = Hmac(key, $"proof|{3 - myRole}|", nHost, nGuest);
        if (a2?.T != "auth2" || !CryptographicOperations.FixedTimeEquals(theirProof, expected))
            throw new AuthRefusedException("wrong password");

        var h2g = Hmac(key, "key|h2g|", nHost, nGuest);
        var g2h = Hmac(key, "key|g2h|", nHost, nGuest);
        return isHost ? new CoopCipher(h2g, g2h) : new CoopCipher(g2h, h2g);
    }

    // Handshake frames are plaintext JSON in the normal [len][payload] framing,
    // capped small: nothing large is accepted before the peer is authenticated.
    private static void WritePlain(NetworkStream s, CoopMsg m)
    {
        var p = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(m));
        s.Write(BitConverter.GetBytes(p.Length), 0, 4);
        s.Write(p, 0, p.Length);
    }

    private static CoopMsg ReadPlain(NetworkStream s)
    {
        var lenBuf = new byte[4];
        CoopNet.ReadExactPublic(s, lenBuf, 4);
        int len = BitConverter.ToInt32(lenBuf, 0);
        if (len <= 0 || len > 4096) throw new AuthRefusedException($"handshake frame of {len} bytes");
        var buf = new byte[len];
        CoopNet.ReadExactPublic(s, buf, len);
        try { return JsonSerializer.Deserialize<CoopMsg>(Encoding.UTF8.GetString(buf)); }
        catch { throw new AuthRefusedException("malformed handshake"); }
    }
}
