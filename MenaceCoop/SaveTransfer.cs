using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
using Il2CppMenace.Strategy;

namespace MenaceCoop;

// Sends the newest save (plus its .jpg thumbnail) to the co-op partner over
// the existing message channel: fmeta announces name/size/hash, fchunk carries
// 256 KB base64 pieces, fdone verifies and writes. Existing files are backed
// up to <name>.coopbak before overwrite; identical content is skipped (which
// also makes the button a harmless no-op in same-machine testing).
internal static class SaveTransfer
{
    private const int ChunkSize = 256 * 1024;
    private const long MaxFileSize = 512L * 1024 * 1024;

    private class IncomingFile
    {
        public byte[] Buf;
        public string Hash;
    }

    private static readonly Dictionary<string, IncomingFile> _incoming = new();

    // Files that finished receiving successfully (hash verified or
    // identical-skip). SessionSync gates auto-load on membership here.
    internal static readonly HashSet<string> CompletedFiles = new(StringComparer.OrdinalIgnoreCase);

    // ASK THE GAME WHERE ITS SAVES ARE; DO NOT ASSUME.
    //
    // This was Path.Combine(persistentDataPath, "Saves") -- a second, hardcoded
    // answer to a question SaveSystem already answers. The two only agreed by
    // coincidence, and the moment the dev benches redirect SaveSystem away from
    // the user's folder (SaveIsolation.cs) they would have split: the guest would
    // write coop_sync.save into the USER'S folder while the game looked for it in
    // the bench's. Every save write the mod makes goes through PathFor, so this
    // one property is the single source of truth for all of them -- the sync
    // save, the battle save and the battle checkpoint.
    private static string SavesDir
    {
        get
        {
            try
            {
                var p = SaveSystem.GetAndCreateSaveFileFolderPath();
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch { }
            return Path.Combine(Application.persistentDataPath, "Saves");
        }
    }

    internal static string PathFor(string fileName) => Path.Combine(SavesDir, fileName);

    internal static void SendLatestSave()
    {
        try
        {
            var dir = new DirectoryInfo(SavesDir);
            if (!dir.Exists) { CoopNet.Status = "No Saves folder found"; return; }
            FileInfo latest = null;
            foreach (var f in dir.GetFiles("*.save"))
                if (latest == null || f.LastWriteTimeUtc > latest.LastWriteTimeUtc) latest = f;
            if (latest == null) { CoopNet.Status = "No save files found"; return; }

            SendFile(latest.FullName);
            string jpg = Path.ChangeExtension(latest.FullName, ".jpg");
            if (File.Exists(jpg)) SendFile(jpg);

            CoopNet.Status = $"Sent save '{latest.Name}'";
            Plugin.Logger.LogInfo($"[FILE] Sent {latest.Name} ({latest.Length} bytes)");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[FILE] Send failed: {e}");
            CoopNet.Status = "Save send failed (see log)";
        }
    }

    private static void Out(int to, CoopMsg m) { if (to > 0) CoopNet.SendTo(m, to); else CoopNet.Send(m); }

    // to: one seat, or -1 for every other player.
    internal static void SendFile(string path, int to = -1)
    {
        var bytes = File.ReadAllBytes(path);
        string name = Path.GetFileName(path);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        Out(to, new CoopMsg { T = "fmeta", FName = name, FSize = bytes.LongLength, FHash = hash });
        for (long off = 0; off < bytes.LongLength; off += ChunkSize)
        {
            int len = (int)Math.Min(ChunkSize, bytes.LongLength - off);
            var chunk = new byte[len];
            Array.Copy(bytes, off, chunk, 0, len);
            Out(to, new CoopMsg { T = "fchunk", FName = name, FOff = off, Data = Convert.ToBase64String(chunk) });
        }
        Out(to, new CoopMsg { T = "fdone", FName = name });
    }

    internal static void HandleMeta(CoopMsg m)
    {
        // Saves flow host -> guest only. The host never accepts a file, so a peer
        // cannot overwrite the host's saves (user, 2026-09-27: connection security).
        if (CoopNet.Mode != CoopMode.Guest)
        {
            Plugin.Logger.LogWarning($"[FILE] Ignored incoming file '{m.FName}': only the guest receives saves");
            return;
        }
        string name = Sanitize(m.FName);
        if (name == null) return;
        if (m.FSize < 0 || m.FSize > MaxFileSize)
        {
            Plugin.Logger.LogError($"[FILE] Refusing incoming '{name}' of size {m.FSize}");
            return;
        }
        _incoming[name] = new IncomingFile { Buf = new byte[m.FSize], Hash = m.FHash };
        CompletedFiles.Remove(name); // a fresh transfer invalidates the old completion
        CoopNet.Status = $"Receiving '{name}'...";
        Plugin.Logger.LogInfo($"[FILE] Incoming {name} ({m.FSize} bytes)");
    }

    internal static void HandleChunk(CoopMsg m)
    {
        string name = Sanitize(m.FName);
        if (name == null || !_incoming.TryGetValue(name, out var f)) return;
        var data = Convert.FromBase64String(m.Data);
        if (m.FOff < 0 || m.FOff + data.Length > f.Buf.LongLength)
        {
            Plugin.Logger.LogError($"[FILE] Bad chunk offset for {name}, aborting transfer");
            _incoming.Remove(name);
            return;
        }
        Array.Copy(data, 0, f.Buf, m.FOff, data.Length);
    }

    internal static void HandleDone(CoopMsg m)
    {
        string name = Sanitize(m.FName);
        if (name == null || !_incoming.TryGetValue(name, out var f)) return;
        _incoming.Remove(name);

        string hash = Convert.ToHexString(SHA256.HashData(f.Buf));
        if (!string.Equals(hash, f.Hash, StringComparison.OrdinalIgnoreCase))
        {
            Plugin.Logger.LogError($"[FILE] Hash mismatch for {name}, discarding");
            CoopNet.Status = $"Transfer of '{name}' corrupted, ask for a resend";
            return;
        }

        Directory.CreateDirectory(SavesDir);
        string dest = Path.Combine(SavesDir, name);
        if (File.Exists(dest))
        {
            var existing = File.ReadAllBytes(dest);
            if (Convert.ToHexString(SHA256.HashData(existing)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Logger.LogInfo($"[FILE] {name} already identical, skipping write");
                CoopNet.Status = $"Save '{name}' already in sync";
                CompletedFiles.Add(name);
                return;
            }
            File.Copy(dest, dest + ".coopbak", true);
            Plugin.Logger.LogInfo($"[FILE] Backed up existing {name} to {name}.coopbak");
        }
        File.WriteAllBytes(dest, f.Buf);
        CompletedFiles.Add(name);
        CoopNet.Status = $"Received '{name}'";
        Plugin.Logger.LogInfo($"[FILE] Wrote {dest} ({f.Buf.LongLength} bytes)");
    }

    // Strip any path component and allow only save/thumbnail extensions, so a
    // remote peer can never write outside the Saves folder.
    private static string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        name = Path.GetFileName(name);
        string ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext != ".save" && ext != ".jpg")
        {
            Plugin.Logger.LogError($"[FILE] Refusing file type: '{name}'");
            return null;
        }
        return name;
    }
}
