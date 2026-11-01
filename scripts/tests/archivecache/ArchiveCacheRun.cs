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
// T161 / F-M369: an archive is fetched once per download run, not once per extraction.
//
// THE MEASURED CASE (31.10.2026, prod, run 03:46:22-04:04:58):
//
//   quota read    downloads 50/50, 46 files saved — four units unaccounted for.
//   the URLs      three archives fetched TWICE inside fifteen minutes
//                 (3738370-8660123, 3738372-8660125, 3738377-8660131): one variant per
//                 pass, and the second pass downloaded the same ZIP again.
//   the fourth    1696155-1518421.zip answered 200 and yielded no file at all.
//
// The first three are pure waste: same URL, same bytes, same quota unit. The cache below is
// run-scoped on purpose — it must not outlive the run, so it can never serve an archive that
// SubDL replaced upstream, and it releases its memory on the Pi when the run ends.
//
// Usage: dotnet build Jellyfin.Plugin.SubdlSync -c Release
//        dotnet run --project scripts/tests/archivecache/archivecache.csproj -- [repo-root]

using System;
using System.IO;
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

    /// <summary>A body that passes the ZIP signature check the caches apply.</summary>
    private static byte[] Zip(int n) => new byte[] { 0x50, 0x4B, 3, 4 }.Concat(Bytes(n)).ToArray();

    private static int Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";

        // ── 1. The measured waste: the same archive twice ───────────────────────
        Console.WriteLine("1. Same archive, second pass");
        var cache = new RunArchiveCache();
        Check(!cache.TryGet(Url("3738370-8660123"), out var first), "first pass misses (a fresh run holds nothing)");
        Check(first == null, "a miss returns null, not a stale buffer");
        cache.Put(Url("3738370-8660123"), Bytes(4096));
        Check(cache.TryGet(Url("3738370-8660123"), out var second), "second pass HITS — no second quota unit");
        Check(second != null && second.Length == 4096, "the hit carries the same bytes");
        Check(cache.Hits == 1 && cache.Misses == 1, "one hit, one miss");
        Check(cache.Stores == 1, "one archive held");
        Check(cache.TotalBytes == 4096, "the byte budget counts what is held");

        // A different URL must not be served from the first one's entry.
        Check(!cache.TryGet(Url("3738372-8660125"), out _), "a different archive is still a miss");

        // ── 2. Ceilings: per entry, per count, per total ─────────────────────────
        Console.WriteLine("2. Ceilings");
        var big = new RunArchiveCache(maxEntries: 8, maxEntryBytes: 1024, maxTotalBytes: 8192);
        big.Put(Url("too-big"), Bytes(2048));
        Check(!big.TryGet(Url("too-big"), out _), "an archive above the per-entry ceiling is not cached");
        Check(big.Refused == 1, "and it is counted as refused");
        Check(big.Stores == 0 && big.TotalBytes == 0, "nothing was held");

        var fifo = new RunArchiveCache(maxEntries: 2, maxEntryBytes: 4096, maxTotalBytes: 1 << 20);
        fifo.Put(Url("a"), Bytes(100));
        fifo.Put(Url("b"), Bytes(100));
        fifo.Put(Url("c"), Bytes(100));
        Check(fifo.Count == 2, "the entry ceiling holds (2 kept)");
        Check(fifo.Evictions == 1, "the oldest one was dropped");
        Check(!fifo.TryGet(Url("a"), out _), "a is gone");
        Check(fifo.TryGet(Url("b"), out _) && fifo.TryGet(Url("c"), out _), "b and c are still there");

        var tight = new RunArchiveCache(maxEntries: 16, maxEntryBytes: 4096, maxTotalBytes: 250);
        tight.Put(Url("x"), Bytes(100));
        tight.Put(Url("y"), Bytes(100));
        tight.Put(Url("z"), Bytes(100));
        Check(tight.TotalBytes <= 250, "the total budget is never exceeded");
        Check(tight.Count == 2, "so the oldest archive made room");
        Check(!tight.TryGet(Url("x"), out _), "x was the one dropped");

        // ── 3. Re-storing the same URL ──────────────────────────────────────────
        Console.WriteLine("3. Re-store");
        var replace = new RunArchiveCache();
        replace.Put(Url("same"), Bytes(500, 0x41));
        replace.Put(Url("same"), Bytes(700, 0x42));
        Check(replace.Count == 1, "the key stays single");
        Check(replace.TotalBytes == 700, "the byte budget follows the replacement, not the sum");
        Check(replace.TryGet(Url("same"), out var replaced) && replaced != null && replaced[0] == 0x42,
            "the newer bytes win");

        // ── 4. Nonsense input ───────────────────────────────────────────────────
        Console.WriteLine("4. Refusals");
        var guard = new RunArchiveCache();
        guard.Put(string.Empty, Bytes(10));
        guard.Put(Url("empty"), Array.Empty<byte>());
        guard.Put(Url("null"), null!);
        Check(guard.Count == 0 && guard.Stores == 0, "an empty key or empty body stores nothing");
        Check(!guard.TryGet(string.Empty, out _), "an empty key never hits");

        // ── 5. Reset: nothing survives the run ───────────────────────────────────
        Console.WriteLine("5. Reset");
        var run = new RunArchiveCache();
        run.Put(Url("p"), Bytes(300));
        run.TryGet(Url("p"), out _);
        run.TryGet(Url("q"), out _);
        run.Reset();
        Check(run.Count == 0 && run.TotalBytes == 0, "every archive is gone");
        Check(run.Hits == 0 && run.Misses == 0 && run.Stores == 0, "and the counters are fresh again");
        Check(!run.TryGet(Url("p"), out _), "the next run misses, as it must — no cross-run reuse");

        // ── 6. The published limits ──────────────────────────────────────────────
        // The Lastenheft (F-M369) names these numbers; asserting them here is what keeps the
        // document from drifting away from the code without a single test going red.
        Console.WriteLine("6. Published limits");
        Check(RunArchiveCache.DefaultMaxEntries == 32, "32 archives at once");
        Check(RunArchiveCache.DefaultMaxEntryBytes == 16 * 1024 * 1024, "16 MB per archive");
        Check(RunArchiveCache.DefaultMaxTotalBytes == 64L * 1024 * 1024, "64 MB in total");
        var published = new RunArchiveCache();
        published.Put(Url("nine-mb"), Bytes(9 * 1024 * 1024));
        Check(published.TryGet(Url("nine-mb"), out _), "a 9 MB pack IS cached now (it was refused at 8 MB)");
        published.Put(Url("seventeen-mb"), Bytes(17 * 1024 * 1024));
        Check(!published.TryGet(Url("seventeen-mb"), out _), "a 17 MB pack is still refused");
        Check(published.Refused == 1, "and only that one was refused");
        published.Reset();

                // ── 7. The key: an archive, not an entry ────────────────────────────────
        // Measured 01.11.2026 (run 04:07-04:24Z, 44 files): 51 archive fetches, ZERO cache hits,
        // although three packs served many entries —
        //   Xhfe8mD1w5/  ten entries,  0p9xSnSU4g/  nine,  BlwZSYtLaU/  seven.
        // The key was the full URL, so the same pack was downloaded once per entry. Keying on the
        // archive segment turns 23 of those 51 fetches into hits.
        Console.WriteLine("7. Archive key");
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

        // ── 8. The disk cache: days and megabytes, each 0 = off ─────────────────
        Console.WriteLine("8. Disk cache");
        var dir = Path.Combine(Path.GetTempPath(), "archivecache-" + Guid.NewGuid().ToString("N")[..8]);
        string InCache(string d) => Path.Combine(d, "archive-cache"); // Configure legt diesen Unterordner an

        var off = new ArchiveDiskCache();
        off.Configure(dir, 0, 512);
        Check(!off.Enabled, "days = 0 switches the cache off");
        off.Put("Xhfe8mD1w5", Zip(64));
        Check(!off.TryGet("Xhfe8mD1w5", out _), "and nothing is read back");
        Check(!Directory.Exists(InCache(dir)) || Directory.GetFiles(InCache(dir)).Length == 0, "and nothing was written at all");

        var off2 = new ArchiveDiskCache();
        off2.Configure(dir, 14, 0);
        Check(!off2.Enabled, "megabytes = 0 switches it off as well");

        var disk = new ArchiveDiskCache();
        disk.Configure(dir, 14, 512);
        Check(disk.Enabled, "14 days and 512 MB switch it on");
        var pack = Zip(2048);
        disk.Put("Xhfe8mD1w5", pack);
        Check(File.Exists(Path.Combine(InCache(dir), "Xhfe8mD1w5.zip")), "the archive is a file named after its key");
        Check(disk.TryGet("Xhfe8mD1w5", out var fromDisk) && fromDisk!.Length == pack.Length && disk.Hits == 1,
            "a second run gets the same bytes without a fetch");
        Check(disk.SizeBytes() > 0 && disk.Count() == 1, "size and count are readable for diagnostics");

        disk.Put("plain", Bytes(512));
        Check(!disk.TryGet("plain", out _), "a body that is not a ZIP is never stored — a single file must not be served for another");

        // Ceiling: oldest goes first.
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

        // Expiry and the touch that keeps a hot pack alive.
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

        try
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(dir2, recursive: true);
            Directory.Delete(dir3, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp directory must not decide the suite.
        }

        // ── 9. Wiring on the source ─────────────────────────────────────────────
        Console.WriteLine("9. Source contract");
        var api = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/SubdlApiClient.cs"));
        Check(api.Contains("private readonly RunArchiveCache _archiveCache = new();", StringComparison.Ordinal)
              && api.Contains("private readonly ArchiveDiskCache _archiveDiskCache = new();", StringComparison.Ordinal),
            "the client owns both layers");
        Check(api.Contains("public RunArchiveCache ArchiveCache => _archiveCache;", StringComparison.Ordinal)
              && api.Contains("public ArchiveDiskCache ArchiveDiskCache => _archiveDiskCache;", StringComparison.Ordinal),
            "both are reachable for the pipeline");
        Check(api.Contains("string? key = ArchiveKey.FromUrl(fullUrl);", StringComparison.Ordinal),
            "the download path derives the key before it fetches");
        Check(api.Contains("_archiveCache.TryGet(key, out byte[]? runBytes)", StringComparison.Ordinal)
              && api.Contains("_archiveDiskCache.TryGet(key, out byte[]? diskBytes)", StringComparison.Ordinal),
            "run layer first, disk layer second");
        Check(api.Contains("StoreArchive(key, bytes)", StringComparison.Ordinal), "a fetch fills both layers");
        Check((api.Split("if (bytes.Length <= 4 || bytes[0] != 'P' || bytes[1] != 'K')").Length - 1) >= 2,
            "packs only — the store and the unpacker agree on what a ZIP is");
        Check(api.Contains("archive cache does not hold this entry — refetching", StringComparison.Ordinal),
            "a cached pack without the wanted entry is refetched instead of reported as rejected");
        Check(api.Contains("Trace(\"GET\", fullUrl, 200, \"archive cache hit (disk)\")", StringComparison.Ordinal),
            "a disk hit is visible without being counted as a download");
        Check(api.Contains("private async Task<byte[]?> FetchArchiveAsync(string fullUrl, CancellationToken ct)", StringComparison.Ordinal),
            "the retry ladder still lives in its own method");

        var pipe = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs"));
        Check((pipe.Split("_api.ArchiveCache.Reset();").Length - 1) == 2,
            "the pipeline empties the run cache at start AND in the finally block");
        Check(pipe.Contains("_api.ArchiveDiskCache.Configure(", StringComparison.Ordinal)
              && pipe.Contains("_api.ArchiveDiskCache.Trim();", StringComparison.Ordinal),
            "it configures the disk cache and trims it before the first request");
        Check(pipe.Contains("_config.DownloadArchiveCacheDays", StringComparison.Ordinal)
              && pipe.Contains("_config.DownloadArchiveCacheMaxMb", StringComparison.Ordinal),
            "both switches come from the configuration");
        Check(pipe.Contains("_api.ArchiveDiskCache.Hits", StringComparison.Ordinal), "the summary counts the disk hits");
        var resetIdx = pipe.IndexOf("_api.ArchiveCache.Reset();", StringComparison.Ordinal);
        var limitIdx = pipe.IndexOf("_dailyLimitWaits = 0;", StringComparison.Ordinal);
        Check(limitIdx >= 0 && resetIdx > limitIdx, "the first reset sits with the per-run counters");

        var cfg = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Configuration/PluginConfiguration.cs"));
        Check(cfg.Contains("public int DownloadArchiveCacheDays { get; set; } = 14;", StringComparison.Ordinal),
            "the default retention is 14 days");
        Check(cfg.Contains("public int DownloadArchiveCacheMaxMb { get; set; } = 512;", StringComparison.Ordinal),
            "the default ceiling is 512 MB");

        var page = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Configuration/configPage.html"));
        Check(page.Contains("id=\"DownloadArchiveCacheDays\"", StringComparison.Ordinal)
              && page.Contains("id=\"DownloadArchiveCacheMaxMb\"", StringComparison.Ordinal),
            "both switches are fields in the Download tab");
        Check(page.Contains("config.DownloadArchiveCacheDays =", StringComparison.Ordinal)
              && page.Contains("config.DownloadArchiveCacheMaxMb =", StringComparison.Ordinal),
            "the config page writes them back, clamped at 0");

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? $"archivecache: {_checks} checks, all green."
            : $"archivecache: {_checks} checks, {Failures.Count} FAILED.");
        return Failures.Count == 0 ? 0 : 1;
    }
}
