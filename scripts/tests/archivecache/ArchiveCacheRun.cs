// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. SubDL Scribe is distributed WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR
// PURPOSE. See the GNU General Public License for more details.
//
// T161 / F-M369, F-M386: an archive is fetched once — for the whole run AND for the next run.
// There is ONE cache since the operator order of 02.11.2026 ("nur noch den Platten-Cache"): the
// disk cache. The run-scoped layer that used to sit in front of it is gone, so these checks
// prove the remaining single layer covers both cases:
//
//   inside a run   the second pass of the same archive is a hit, no second quota unit
//   across runs    the same hit survives a new cache instance pointed at the same folder
//   when off       days = 0 or megabytes = 0 caches nothing at all, not even within a run
//
// Usage: dotnet build Jellyfin.Plugin.SubdlSync -c Release
//        dotnet run --project scripts/tests/archivecache/archivecache.csproj -- [repo-root]

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.SubdlScribe.Api;

namespace ArchiveCacheRun;

internal static class Program
{
    private static int _checks;
    private static readonly System.Collections.Generic.List<string> Failures = new();

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (!ok)
        {
            Failures.Add(what);
            Console.WriteLine("  FAIL  " + what);
        }
    }

    private static byte[] Bytes(int n, byte fill = 0x41) => Enumerable.Repeat(fill, n).ToArray();

    private static string Url(string id) => "https://dl.subdl.com/subtitle/" + id + ".zip";

    /// <summary>A body that passes the ZIP signature check the cache applies.</summary>
    private static byte[] Zip(int n) => new byte[] { 0x50, 0x4B, 3, 4 }.Concat(Bytes(n)).ToArray();

    private static int Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";
        var dir = Path.Combine(Path.GetTempPath(), "archivecache-" + Guid.NewGuid().ToString("N")[..8]);
        string InCache(string d) => Path.Combine(d, "archive-cache"); // Configure legt diesen Unterordner an

        var cache = new ArchiveDiskCache();
        cache.Configure(dir, 14, 512);
        Check(cache.Enabled, "14 days and 512 MB switch the cache on");

        // ── 1. Same archive, second pass — inside one run ─────────────────────────
        Console.WriteLine("1. Same archive, second pass (inside a run)");
        Check(!cache.TryGet("3738370-8660123", out var first), "first pass misses (nothing fetched yet)");
        Check(first == null, "a miss returns null, not a stale buffer");
        cache.Put("3738370-8660123", Zip(4096));
        Check(cache.TryGet("3738370-8660123", out var second), "second pass HITS — no second quota unit");
        Check(second != null && second.Length == 4100, "the hit carries the same bytes");
        Check(cache.Hits == 1 && cache.Misses == 1, "one hit, one miss");
        Check(cache.Stores == 1, "one archive written");
        Check(!cache.TryGet("3738372-8660125", out _), "a different archive is still a miss");

        // ── 2. Across runs: the point of keeping it on disk ──────────────────────
        Console.WriteLine("2. Across runs");
        var nextRun = new ArchiveDiskCache();
        nextRun.Configure(dir, 14, 512);
        Check(nextRun.TryGet("3738370-8660123", out var fromDisk) && fromDisk!.Length == 4100,
            "a fresh cache instance on the same folder serves the pack without a fetch");
        Check(nextRun.Hits == 1, "and counts it as a hit");

        // ── 3. Refusals: nothing but a ZIP becomes an entry ──────────────────────
        Console.WriteLine("3. Refusals");
        cache.Put(string.Empty, Zip(64));
        cache.Put("empty", Array.Empty<byte>());
        cache.Put("null", null!);
        cache.Put("plain", Bytes(512));
        Check(!cache.TryGet(string.Empty, out _), "an empty key never hits");
        Check(!cache.TryGet("empty", out _) && !cache.TryGet("null", out _), "no body, no entry");
        Check(!cache.TryGet("plain", out _),
            "a body that is not a ZIP is never stored — a single file must not be served for another");

        // ── 4. The per-archive ceiling ───────────────────────────────────────────
        // It used to be borrowed from the removed run cache; it is the disk cache's own constant
        // now, and the Lastenheft names the number, so it is read off the field rather than trusted.
        Console.WriteLine("4. Per-archive ceiling");
        var ceiling = (int)(typeof(ArchiveDiskCache)
            .GetField("MaxEntryBytes", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!);
        Check(ceiling == 16 * 1024 * 1024, "16 MB per archive");
        var nearCeiling = new ArchiveDiskCache();
        nearCeiling.Configure(dir + "-big", 14, 512);
        nearCeiling.Put("nine-mb", Zip(9 * 1024 * 1024));
        Check(nearCeiling.TryGet("nine-mb", out _), "a 9 MB pack IS cached");
        nearCeiling.Put("seventeen-mb", Zip(17 * 1024 * 1024));
        Check(!nearCeiling.TryGet("seventeen-mb", out _), "a 17 MB pack is refused");

        // ── 5. The key: an archive, not an entry ────────────────────────────────
        // Measured 01.11.2026 (run 04:07-04:24Z, 44 files): 51 archive fetches, ZERO cache hits,
        // although three packs served many entries —
        //   Xhfe8mD1w5/  ten entries,  0p9xSnSU4g/  nine,  BlwZSYtLaU/  seven.
        // The key was the full URL, so the same pack was downloaded once per entry. Keying on the
        // archive segment turns 23 of those 51 fetches into hits.
        Console.WriteLine("5. Archive key");
        Check(ArchiveKey.FromUrl("https://dl.subdl.com/subtitle/Xhfe8mD1w5/5Ibw144l42") == "Xhfe8mD1w5",
            "an entry URL keys on the archive segment");
        Check(ArchiveKey.FromUrl("https://dl.subdl.com/subtitle/Xhfe8mD1w5/SUK5YnspZX?api_key=secret") == "Xhfe8mD1w5",
            "the query string does not change the key");
        Check(ArchiveKey.FromUrl("https://dl.subdl.com/subtitle/Xhfe8mD1w5/5Ibw144l42")
              == ArchiveKey.FromUrl("https://dl.subdl.com/subtitle/Xhfe8mD1w5/SUK5YnspZX"),
            "two entries of one pack share the key — that is the whole point");
        Check(ArchiveKey.FromUrl("https://dl.subdl.com/subtitle/0p9xSnSU4g/Rc8foE2T9m") != "Xhfe8mD1w5",
            "a different pack gets a different key");
        Check(ArchiveKey.FromUrl("https://dl.subdl.com/subtitle/1778460-1115627.zip") == "1778460-1115627",
            "a bare zip name is its own archive");
        Check(ArchiveKey.FromUrl("https://dl.subdl.com/subtitle/abc") == null, "too short to be a key");
        Check(ArchiveKey.FromUrl("https://dl.subdl.com/subtitle/../../etc/passwd") == null,
            "path traversal never becomes a key");
        Check(ArchiveKey.FromUrl("") == null && ArchiveKey.FromUrl(null) == null, "no URL, no key");
        Check(!ArchiveKey.IsCacheable("https://dl.subdl.com/other/thing.srt"), "a non-archive URL is not cacheable");

        // ── 6. Off means off — also inside a run ─────────────────────────────────
        Console.WriteLine("6. Switched off");
        var off = new ArchiveDiskCache();
        off.Configure(dir + "-off", 0, 512);
        Check(!off.Enabled, "days = 0 switches the cache off");
        off.Put("Xhfe8mD1w5", Zip(64));
        Check(!off.TryGet("Xhfe8mD1w5", out _), "and nothing is read back");
        Check(!Directory.Exists(InCache(dir + "-off")) || Directory.GetFiles(InCache(dir + "-off")).Length == 0,
            "and nothing was written at all");

        var off2 = new ArchiveDiskCache();
        off2.Configure(dir + "-off2", 14, 0);
        Check(!off2.Enabled, "megabytes = 0 switches it off as well");

        // ── 7. Ceilings, expiry, the touch that keeps a hot pack alive ──────────
        Console.WriteLine("7. Retention and ceiling");
        var narrow = new ArchiveDiskCache();
        var dir2 = dir + "-narrow";
        narrow.Configure(dir2, 14, 4);
        for (int i = 0; i < 5; i++)
        {
            narrow.Put("k" + i, Zip(1024 * 1024));
            File.SetLastWriteTimeUtc(Path.Combine(InCache(dir2), "k" + i + ".zip"), DateTime.UtcNow.AddMinutes(-(30 - i)));
        }

        Check(narrow.SizeBytes() > 4L * 1024 * 1024, "five megabytes in a four megabyte cache");
        narrow.Trim();
        Check(narrow.SizeBytes() <= 4L * 1024 * 1024, "the trim brings it back under the ceiling");
        Check(!File.Exists(Path.Combine(InCache(dir2), "k0.zip")) && File.Exists(Path.Combine(InCache(dir2), "k4.zip")),
            "the oldest went first, the newest stayed");
        Check(narrow.Removed >= 1, "and the removal is counted");

        var exp = new ArchiveDiskCache();
        var dir3 = dir + "-exp";
        exp.Configure(dir3, 14, 512);
        exp.Put("old", Zip(256));
        File.SetLastWriteTimeUtc(Path.Combine(InCache(dir3), "old.zip"), DateTime.UtcNow.AddDays(-20));
        Check(!exp.TryGet("old", out _), "an archive older than the retention is a miss");
        Check(!File.Exists(Path.Combine(InCache(dir3), "old.zip")) && exp.Removed >= 1, "and it is deleted, not kept");

        exp.Put("hot", Zip(256));
        File.SetLastWriteTimeUtc(Path.Combine(InCache(dir3), "hot.zip"), DateTime.UtcNow.AddDays(-13));
        Check(exp.TryGet("hot", out _), "an archive inside the retention is a hit");
        var touched = File.GetLastWriteTimeUtc(Path.Combine(InCache(dir3), "hot.zip"));
        Check((DateTime.UtcNow - touched).TotalMinutes < 5,
            "a hit restarts the clock, so a pack that keeps serving episodes does not expire mid-season");
        exp.Trim();
        Check(File.Exists(Path.Combine(InCache(dir3), "hot.zip")), "and the trim leaves it alone");
        Check(exp.Count() == 1 && exp.SizeBytes() > 0, "size and count are readable for diagnostics");

        try
        {
            foreach (var d in Directory.GetDirectories(Path.GetTempPath(), Path.GetFileName(dir) + "*"))
            {
                Directory.Delete(d, recursive: true);
            }
        }
        catch (Exception)
        {
            // A leftover temp directory must not decide the suite.
        }

        // ── 8. Wiring on the source: ONE layer ───────────────────────────────────
        Console.WriteLine("8. Source contract");
        var api = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/SubdlApiClient.cs"));
        Check(api.Contains("private readonly ArchiveDiskCache _archiveDiskCache = new();", StringComparison.Ordinal),
            "the client owns the disk cache");
        Check(!api.Contains("RunArchiveCache", StringComparison.Ordinal)
              && !api.Contains("_archiveCache", StringComparison.Ordinal),
            "and no second, run-scoped layer");
        Check(!File.Exists(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/RunArchiveCache.cs")),
            "the run cache file is gone, not merely unused");
        Check(api.Contains("string? key = ArchiveKey.FromUrl(fullUrl);", StringComparison.Ordinal),
            "the download path derives the key before it fetches");
        Check(api.Contains("_archiveDiskCache.TryGet(key, out byte[]? diskBytes)", StringComparison.Ordinal),
            "the ONE lookup goes to disk");
        Check(api.Contains("Trace(\"GET\", fullUrl, 200, \"archive cache hit (disk)\")", StringComparison.Ordinal),
            "a hit is visible without being counted as a download");
        // F-M387 moved the fetch into GetArchiveBodyAsync, which every reader shares; the store
        // happens there, so both the entry download and the sibling retry fill the one cache.
        Check(api.Contains("StoreArchive(key, fetched)", StringComparison.Ordinal),
            "a fetch fills the cache (in the shared body reader)");
        Check(api.Split("GetArchiveBodyAsync(").Length - 1 >= 3,
            "the entry download and the member retry go through that one reader");
        Check((api.Split("if (bytes.Length <= 4 || bytes[0] != 'P' || bytes[1] != 'K')").Length - 1) >= 2,
            "packs only — the store and the unpacker agree on what a ZIP is");
        Check(api.Contains("archive cache does not hold this entry — refetching", StringComparison.Ordinal),
            "a cached pack without the wanted entry is refetched instead of reported as rejected");
        Check(api.Contains("private async Task<byte[]?> FetchArchiveAsync(string fullUrl, CancellationToken ct)", StringComparison.Ordinal),
            "the retry ladder still lives in its own method");

        var pipe = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs"));
        Check(!pipe.Contains("ArchiveCache.Reset", StringComparison.Ordinal),
            "the pipeline no longer empties a run cache — there is none");
        Check(pipe.Contains("_api.ArchiveDiskCache.Configure(", StringComparison.Ordinal)
              && pipe.Contains("_api.ArchiveDiskCache.Trim();", StringComparison.Ordinal),
            "it configures the cache and trims it before the first request");
        Check(pipe.Contains("_config.DownloadArchiveCacheDays", StringComparison.Ordinal)
              && pipe.Contains("_config.DownloadArchiveCacheMaxMb", StringComparison.Ordinal),
            "both switches come from the configuration");
        Check(pipe.Contains("_api.ArchiveDiskCache.Hits", StringComparison.Ordinal),
            "the summary counts the hits");

        var cfg = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Configuration/PluginConfiguration.cs"));
        Check(cfg.Contains("public int DownloadArchiveCacheDays { get; set; } = 7;", StringComparison.Ordinal),
            "the default retention is 7 days (F-M428)");
        Check(cfg.Contains("public int DownloadArchiveCacheMaxMb { get; set; } = 256;", StringComparison.Ordinal),
            "the default ceiling is 256 MB (F-M428)");

        var page = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Configuration/configPage.html"));
        Check(page.Contains("id=\"DownloadArchiveCacheDays\"", StringComparison.Ordinal)
              && page.Contains("id=\"DownloadArchiveCacheMaxMb\"", StringComparison.Ordinal),
            "both switches are fields in the Download tab");
        Check(page.Contains("config.DownloadArchiveCacheDays =", StringComparison.Ordinal)
              && page.Contains("config.DownloadArchiveCacheMaxMb =", StringComparison.Ordinal),
            "the config page writes them back, clamped at 0");
        Check(page.Contains("config.DownloadArchiveCacheDays ?? 7", StringComparison.Ordinal)
              && page.Contains("config.DownloadArchiveCacheMaxMb ?? 256", StringComparison.Ordinal),
            "the page falls back to the new defaults, 7 days and 256 MB (F-M428)");

        // ── F-M427: der DB-Reset purgt die Ablage mit ──────────────────────────
        Console.WriteLine("Purge with the database reset");
        var purgeDir = Path.Combine(Path.GetTempPath(), "archivecache-purge-" + Guid.NewGuid().ToString("N")[..8]);
        var purgeCache = new ArchiveDiskCache();
        purgeCache.Configure(purgeDir, 14, 512);
        purgeCache.Put("4711-815", Zip(2048));
        int before = Directory.GetFiles(InCache(purgeDir), "*.zip").Length;
        int purged = ArchiveDiskCache.PurgeDirectory(purgeDir);
        int after = Directory.GetFiles(InCache(purgeDir), "*.zip").Length;
        Check(before == 1, "one archive sits in the cache");
        Check(purged == before, "the purge reports what it removed");
        Check(after == 0, "and the folder is empty afterwards");
        Check(ArchiveDiskCache.PurgeDirectory(purgeDir) == 0, "a second purge removes nothing and throws nothing");

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? $"archivecache: {_checks} checks, all green."
            : $"archivecache: {_checks} checks, {Failures.Count} FAILED.");
        return Failures.Count == 0 ? 0 : 1;
    }
}
