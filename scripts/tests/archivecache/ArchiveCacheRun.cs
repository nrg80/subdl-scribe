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

        // ── 7. Wiring on the source ──────────────────────────────────────────────
        Console.WriteLine("7. Source contract");
        var api = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/SubdlApiClient.cs"));
        Check(api.Contains("private readonly RunArchiveCache _archiveCache = new();", StringComparison.Ordinal),
            "the client owns one cache");
        Check(api.Contains("public RunArchiveCache ArchiveCache => _archiveCache;", StringComparison.Ordinal),
            "the cache is reachable for the pipeline's reset");
        Check(api.Contains("_archiveCache.TryGet(fullUrl, out byte[]? cachedArchive)", StringComparison.Ordinal),
            "the download path asks the cache first");
        Check(api.Contains("_archiveCache.Put(fullUrl, bytes)", StringComparison.Ordinal),
            "and stores what it fetched");
        Check(api.Contains("private async Task<byte[]?> FetchArchiveAsync(string fullUrl, CancellationToken ct)", StringComparison.Ordinal),
            "the retry ladder moved into its own method");
        Check(!api.Contains("bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);\n                break;", StringComparison.Ordinal),
            "no second GET loop left in the cached path");
        Check(api.Contains("Trace(\"GET\", fullUrl, 200, \"archive cache hit (run)\")", StringComparison.Ordinal),
            "a hit is visible in the trace without being counted as a download");

        var pipe = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs"));
        Check((pipe.Split("_api.ArchiveCache.Reset();").Length - 1) == 2,
            "the pipeline empties the cache at run start AND in the finally block");
        Check(pipe.Contains("_api.ArchiveCache.Hits", StringComparison.Ordinal),
            "the run summary reports the hits");
        var resetIdx = pipe.IndexOf("_api.ArchiveCache.Reset();", StringComparison.Ordinal);
        var lockIdx = pipe.IndexOf("_dailyLimitWaits = 0;", StringComparison.Ordinal);
        Check(lockIdx >= 0 && resetIdx > lockIdx, "the first reset sits with the per-run counters");

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? $"archivecache: {_checks} checks, all green."
            : $"archivecache: {_checks} checks, {Failures.Count} FAILED.");
        return Failures.Count == 0 ? 0 : 1;
    }
}
