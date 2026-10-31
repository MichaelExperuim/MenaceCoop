using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MenaceCoop;

// ARE WE BOTH RUNNING THE SAME MODS?
//
// The game-version and mod-version checks in the "hello" handler catch two
// players on different builds. Neither catches the case that actually bites in
// this community: same game, same MenaceCoop, DIFFERENT CONTENT MODS.
//
// Content mods add and patch unit templates. Two machines with different
// templates generate different armies from the same seed -- and unlike a version
// mismatch, nothing announces it. The board hash covers position, hitpoints and
// action points, so an enemy that exists for one player and not the other hashes
// as identical; the first sign is a squad dying to something that was never on
// the other screen. That is the same blind spot the terrain check was added for
// in v3.13.0, and it is why this is a check and not a note in the readme.
//
// WHAT IS COMPARED, AND WHY BOTH HALVES
//   melons          every loaded MelonLoader mod, name + version. A code mod can
//                   change anything, including generation.
//   content packs   Mods\<folder>\modpack.json and jiangyu.json, name + version.
//                   These are the ones that add units, and a player can easily
//                   have a different SET of them without a single DLL differing.
//
// Version is part of the key on purpose: "Expanded Army Lists 1.1.7" and
// "1.2.0" are not the same army.
//
// THIS ONLY WARNS. It cannot repair a mismatch -- the engine has no way to
// rebuild one side's content -- and refusing to connect over it would lock out
// people whose mods differ only cosmetically. Players get told, plainly, before
// anyone dies for it.
internal static class ModList
{
    private static string _cached;

    // "name@version" per entry, sorted, semicolon separated. Sorted so the two
    // machines' strings are comparable directly rather than only as sets.
    internal static string Local
    {
        get
        {
            if (_cached != null) return _cached;
            var entries = new List<string>();
            try { CollectMelons(entries); } catch { }
            try { CollectContentPacks(entries); } catch { }
            entries.Sort(StringComparer.OrdinalIgnoreCase);
            _cached = string.Join(";", entries);
            return _cached;
        }
    }

    private static void CollectMelons(List<string> into)
    {
        foreach (var melon in MelonLoader.MelonBase.RegisteredMelons)
        {
            string name = melon?.Info?.Name;
            if (string.IsNullOrEmpty(name)) continue;
            // Our own version is already compared on its own, as CoopMsg.Ver.
            // Listing it again would report one disagreement as two.
            if (name.IndexOf("MenaceCoop", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            string file = null;
            try { file = melon.MelonAssembly?.Location; } catch { }
            into.Add($"{name}@{melon.Info.Version ?? "?"}#{FileFingerprint(file)}");
        }
    }

    // Menace_Data sits beside the exe, so its parent is the game root -- a
    // fixed anchor, independent of where the mod DLL itself was loaded from.
    private static string ModsDir()
    {
        try
        {
            string root = Path.GetDirectoryName(UnityEngine.Application.dataPath);
            if (root == null) return null;
            string mods = Path.Combine(root, "Mods");
            return Directory.Exists(mods) ? mods : null;
        }
        catch { return null; }
    }

    private static void CollectContentPacks(List<string> into)
    {
        string dir = ModsDir();
        if (dir == null) return;

        foreach (var folder in Directory.GetDirectories(dir))
        {
            string name = null, version = null, found = null;

            // modpack.json is the Menace Modpack Loader's manifest; jiangyu.json
            // is the Jiangyu loader's. Both are content loaders and both change
            // templates, so both count.
            foreach (var manifest in new[] { "modpack.json", "jiangyu.json" })
            {
                string path = Path.Combine(folder, manifest);
                if (!File.Exists(path)) continue;
                found = manifest;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty("name", out var n)) name = n.GetString();
                    if (doc.RootElement.TryGetProperty("version", out var v)) version = v.GetString();
                }
                catch { /* unreadable manifest: fall back to the folder name */ }
                break;   // one manifest per folder is enough
            }

            // A FOLDER WITH NEITHER MANIFEST STILL COUNTS.
            //
            // Not every content mod announces itself. Three of the 28 folders on
            // the development machine have no manifest at all (customleaders,
            // a squad-leader pack, tools), because their own loader reads a
            // different layout. Skipping those would let two players differ by
            // exactly one of them and still be told their lists MATCH -- the one
            // outcome this check exists to prevent. Named by folder, and
            // "versioned" by how many files it contains, which is enough to
            // notice a different build of the same pack.
            if (found == null)
            {
                int files;
                try { files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length; }
                catch { files = -1; }
                into.Add($"{Path.GetFileName(folder)}@{files}f#{FolderFingerprint(folder)}");
                continue;
            }

            into.Add($"{name ?? Path.GetFileName(folder)}@{version ?? "?"}#{FolderFingerprint(folder)}");
        }
    }

    // SAME VERSION, DIFFERENT FILES (2026-10-02, v5 prep). Name + version alone
    // passed two copies of a mod that report the same version but differ -- a
    // re-uploaded build, a local edit, a half-finished update. A short content
    // fingerprint rides after '#': the DLL's bytes for a melon, every file's
    // relative path + size for a content folder (sizes, not bytes: some packs are
    // hundreds of MB, and an edit that keeps every size identical is rare).
    // Older partners send no '#'; then only versions are compared.
    private static string FileFingerprint(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "-";
            using var sha = System.Security.Cryptography.SHA1.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs)).Substring(0, 10);
        }
        catch { return "-"; }
    }

    private static string FolderFingerprint(string folder)
    {
        try
        {
            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            foreach (var f in files)
            {
                sb.Append(Path.GetRelativePath(folder, f).Replace('\\', '/').ToLowerInvariant())
                  .Append(':').Append(new FileInfo(f).Length).Append('\n');
            }
            using var sha = System.Security.Cryptography.SHA1.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Substring(0, 10);
        }
        catch { return "-"; }
    }

    // Human-readable difference, or null when the two sides agree.
    //
    // Reports WHICH mods differ rather than just that they do: "your partner is
    // missing Expanded Army Lists" is something a player can act on, and
    // "your mod lists differ" is not.
    internal static string Diff(string remote) => Diff(remote, "you", "your partner", "partner");

    internal static string Diff(string remote, string me, string them, string themShort)
    {
        if (remote == null) return null;                 // partner predates this check
        if (remote == Local) return null;

        var mine = Parse(Local);
        var theirs = Parse(remote);

        var onlyMine = new List<string>();
        var onlyTheirs = new List<string>();
        var versions = new List<string>();
        var files = new List<string>();

        foreach (var kv in mine)
        {
            if (!theirs.TryGetValue(kv.Key, out var tv)) { onlyMine.Add(kv.Key); continue; }
            SplitPrint(kv.Value, out var myVer, out var myPrint);
            SplitPrint(tv, out var theirVer, out var theirPrint);
            if (myVer != theirVer) versions.Add($"{kv.Key} ({me} {myVer}, {themShort} {theirVer})");
            // Fingerprints only count when both sides sent one ("-" = could not read).
            else if (myPrint != null && theirPrint != null && myPrint != "-" && theirPrint != "-" && myPrint != theirPrint)
                files.Add(kv.Key);
        }
        foreach (var kv in theirs)
            if (!mine.ContainsKey(kv.Key)) onlyTheirs.Add(kv.Key);

        if (onlyMine.Count == 0 && onlyTheirs.Count == 0 && versions.Count == 0 && files.Count == 0) return null;

        var sb = new StringBuilder();
        if (onlyMine.Count > 0) sb.Append($"only {me} have: {string.Join(", ", onlyMine)}. ");
        if (onlyTheirs.Count > 0) sb.Append($"only {them} has: {string.Join(", ", onlyTheirs)}. ");
        if (versions.Count > 0) sb.Append($"different versions: {string.Join(", ", versions)}. ");
        if (files.Count > 0) sb.Append($"same version but different files: {string.Join(", ", files)}.");
        return sb.ToString().Trim();
    }

    // "1.1.7#ab12cd34ef" -> version "1.1.7", print "ab12cd34ef" (print null if absent).
    private static void SplitPrint(string value, out string version, out string print)
    {
        int h = value?.IndexOf('#') ?? -1;
        version = h >= 0 ? value.Substring(0, h) : value;
        print = h >= 0 ? value.Substring(h + 1) : null;
    }

    private static Dictionary<string, string> Parse(string list)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(list)) return d;
        foreach (var entry in list.Split(';'))
        {
            if (entry.Length == 0) continue;
            int at = entry.LastIndexOf('@');
            string name = at > 0 ? entry.Substring(0, at) : entry;
            string ver = at > 0 ? entry.Substring(at + 1) : "?";
            d[name] = ver;
        }
        return d;
    }

    // Short, for the log line that is printed whether or not anything differs --
    // "both on the same 31 mods" is worth having in a support log too.
    internal static int Count => string.IsNullOrEmpty(Local) ? 0 : Local.Split(';').Length;
}
