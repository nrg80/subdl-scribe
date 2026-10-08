// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Migration harness (F-M327, T139): proves the one-time import from the previous document store into
// the SQLite file, against a real data file — and then keeps going through the three paths the
// operator drives once the import has landed: DATABASE REFRESH, RESET and RESTORE.
//
// WHAT IT PROVES, AND WHY IT IS NOT A UNIT TEST
//
//   The import is the only code path in the plugin that reads the old format, and it runs exactly
//   once in the life of an installation — on a live server, on data the operator cannot re-create.
//   A unit test with three hand-built rows would not answer the question that matters: does a REAL
//   file, with the field types LiteDB actually wrote (int vs long, absent vs null, a `de` collation),
//   come across with every row and every business key intact?
//
//   So this runs against a copy of a live file and checks:
//     1. ROW COUNTS per area, old vs new — a row that quietly fails to import is the failure mode
//        that hurts, and nothing in the plugin would report it.
//     2. THE BUSINESS KEYS survive, including the counters (whose numeric id is NOT the new key)
//        and the rejected candidates (the verdicts that must not be lost).
//     3. THE THREE-VALUED FLAGS stay three-valued (F-M285): an absent `HearingImpaired` must import
//        as null, never as false. A gap turned into a statement is a false claim about a subtitle.
//   Then it re-runs the import on the already-imported directory and requires it to be a NO-OP, so
//   a second start cannot double the rows.
//
//   Finally it opens the imported file through the plugin's REAL context (SubdlDbContext) and reads
//   the counters back, which is what the registries will do at runtime.
//
// THE THREE PATHS AFTER THE IMPORT (added 08.10.2026)
//
//   The import lands the operator in a store whose housekeeping he then drives by hand: refresh the
//   state against reality, reset a direction, restore a backup. Those three were only covered by
//   the STORE harness against a SEEDED store — a store this harness never touches, on paths that
//   behave differently once the rows carry real shapes (verdicts with paths, counters with budgets,
//   media with item ids). So they are driven HERE, on the imported file:
//
//     4. REFRESH  — the prune machinery, without a Jellyfin host. Every tracker's PruneDeadItems and
//        the oshash/sidecar file-side sweeps are driven with a predicate and a directory the harness
//        owns, so the two things that matter can be measured:
//          a. a dead item is pruned and a LIVE item is NOT (the difference between housekeeping and
//             data loss);
//          b. the FAIL-SAFE rule holds (F-M234): an unusable root deletes NOTHING. That is asserted
//             by pointing the sweep at a directory that cannot be listed and requiring a zero.
//     5. RESET    — all three scopes on the imported data: `upload`, `download`, and `all`. The `all`
//        scope is the one that REPLACES the file (checkpoint, copy aside, delete, side files, fresh
//        context) and it is driven in the same order <see cref="Api.SubdlReset"/> drives it, then
//        the store is required to be empty AND usable, with the backup present and complete.
//     6. RESTORE  — round trip through the plugin's own backup format: the reset's `all` scope
//        produces the `.bak-<stamp>` file, the store is mutated, and the restore (clear side files,
//        copy back, reopen) must bring every row and value back — asserted on VALUES, not on
//        presence, because a restore that keeps the old rows is the failure the pool caused.
//
// Usage: migrun <workdir> <source-litedb-file>

using System.Globalization;
using System.Text;
using Jellyfin.Plugin.SubdlScribe.Data;
using Jellyfin.Plugin.SubdlScribe.Registry;
using LiteDB;
using Microsoft.EntityFrameworkCore;

var workdir = args[0];
var source = args[1];

int failures = 0;

void Check(bool ok, string what, string detail = "")
{
    Console.WriteLine((ok ? "  OK   " : "  FAIL ") + what + (detail.Length > 0 ? "  [" + detail + "]" : ""));
    if (!ok)
    {
        failures++;
    }
}

void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine("--- " + title + " ---");
}

Console.WriteLine("=== F-M327 import harness ===");
Console.WriteLine("workdir: " + workdir);
Console.WriteLine("source : " + source);
Console.WriteLine();

// ---------------------------------------------------------------------------------------------
// 0. The source must be readable BEFORE anything else, so a failure here is not confused with an
//    import failure. Read-only, and with the same access path the importer uses.
// ---------------------------------------------------------------------------------------------
var expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
using (var src = new LiteDatabase(new ConnectionString { Filename = source, ReadOnly = true }))
{
    foreach (var area in src.GetCollectionNames().OrderBy(n => n, StringComparer.Ordinal))
    {
        expected[area] = src.GetCollection(area).FindAll().Count();
    }
}

Console.WriteLine("--- source areas (" + expected.Count + ") ---");
foreach (var kv in expected)
{
    Console.WriteLine("   " + kv.Key.PadRight(22) + kv.Value + " row(s)");
}

var totalExpected = expected.Values.Sum();
Console.WriteLine("   " + "TOTAL".PadRight(22) + totalExpected + " row(s)");
Console.WriteLine();

// ---------------------------------------------------------------------------------------------
// 1. The import itself, through the real entry point: constructing SubdlDbContext runs
//    LiteDbImport.RunOnce before the SQLite store is opened.
// ---------------------------------------------------------------------------------------------
var sqlitePath = DbFiles.PathIn(workdir);
var legacyPath = DbFiles.LegacyPathIn(workdir);

Check(File.Exists(legacyPath), "source file sits at the legacy path the importer looks for", legacyPath);
Check(!File.Exists(sqlitePath), "no SQLite file before the first start");

using (var ctx = new SubdlDbContext(workdir, null))
{
    // Reading one row proves the store opened and the schema exists.
    var meta = ctx.Meta.FindById("db");
    Check(meta != null, "SQLite store opened, meta row present");
}

Check(File.Exists(sqlitePath), "SQLite file created");
Check(!File.Exists(legacyPath), "previous file renamed aside");
Check(File.Exists(legacyPath + LiteDbImport.ImportedSuffix), "previous file kept as .imported");

// ---------------------------------------------------------------------------------------------
// 2. Row counts, old vs new, per area.
// ---------------------------------------------------------------------------------------------
Section("row counts, source vs imported");
using (var db = new SubdlSqliteContext(sqlitePath))
{
    var counts = db.GetAreaRowCounts().ToDictionary(x => x.Area, x => (int)x.Rows, StringComparer.OrdinalIgnoreCase);
    foreach (var kv in expected)
    {
        counts.TryGetValue(kv.Key, out var got);
        Check(got == kv.Value, kv.Key.PadRight(22) + " " + got + " of " + kv.Value + " row(s)");
    }

    var totalGot = counts.Values.Sum();
    Check(totalGot == totalExpected, "TOTAL".PadRight(22) + " " + totalGot + " of " + totalExpected + " row(s)");
}

// ---------------------------------------------------------------------------------------------
// 3. Business keys survive — the counters and the burned-candidate verdicts above all.
// ---------------------------------------------------------------------------------------------
Section("business keys");
using (var db = new SubdlSqliteContext(sqlitePath))
{
    var counterKeys = db.CounterRows.Select(c => c.Key).ToList();
    foreach (var c in db.CounterRows.ToList())
    {
        Console.WriteLine("   counter: " + c.Key + " = " + c.Value);
    }

    Check(counterKeys.All(k => !string.IsNullOrEmpty(k)), "every counter carries a non-empty key");
    Check(counterKeys.Count == expected.GetValueOrDefault("counters"), "every counter row arrived");

    // The specific counters the import exists for: a retry budget and a QA fail budget.
    Check(counterKeys.Any(k => k.StartsWith("id-not-found:", StringComparison.Ordinal)), "an id-not-found retry budget survived");
    Check(counterKeys.Any(k => k.StartsWith("qa-fail:", StringComparison.Ordinal)), "a qa-fail budget survived");

    var rejected = db.RejectedRows.Select(r => r.Id).ToList();
    Check(rejected.Count == expected.GetValueOrDefault("rejected_candidates"), "every burned candidate arrived");
    Check(rejected.All(id => id.Split('|').Length == 3), "burned candidate keys keep the item|language|subdl shape");
}

// ---------------------------------------------------------------------------------------------
// 4. The three-valued flags must stay three-valued (F-M285).
// ---------------------------------------------------------------------------------------------
Section("three-valued flags (F-M285)");
int srcNullHi = 0, srcTrueHi = 0, srcFalseHi = 0, srcNullForced = 0;
using (var src = new LiteDatabase(new ConnectionString { Filename = legacyPath + LiteDbImport.ImportedSuffix, ReadOnly = true }))
{
    foreach (var doc in src.GetCollection("embeds").FindAll())
    {
        Classify(doc, "HearingImpaired", out var hi);
        Classify(doc, "Forced", out var forced);
        if (hi == null) srcNullHi++; else if (hi.Value) srcTrueHi++; else srcFalseHi++;
        if (forced == null) srcNullForced++;
    }
}

using (var db = new SubdlSqliteContext(sqlitePath))
{
    var rows = db.EmbedRows.ToList();
    var newNullHi = rows.Count(r => r.HearingImpaired == null);
    var newTrueHi = rows.Count(r => r.HearingImpaired == true);
    var newFalseHi = rows.Count(r => r.HearingImpaired == false);
    var newNullForced = rows.Count(r => r.Forced == null);

    Check(newNullHi == srcNullHi, "HearingImpaired null count preserved", "source " + srcNullHi + " -> imported " + newNullHi);
    Check(newTrueHi == srcTrueHi, "HearingImpaired true count preserved", "source " + srcTrueHi + " -> imported " + newTrueHi);
    Check(newFalseHi == srcFalseHi, "HearingImpaired false count preserved", "source " + srcFalseHi + " -> imported " + newFalseHi);
    Check(newNullForced == srcNullForced, "Forced null count preserved", "source " + srcNullForced + " -> imported " + newNullForced);
    Console.WriteLine("   (source: " + srcNullHi + " null / " + srcTrueHi + " true / " + srcFalseHi + " false for HearingImpaired)");
}

// ---------------------------------------------------------------------------------------------
// 5. The status row — the cumulative counters must come across, values included.
// ---------------------------------------------------------------------------------------------
Section("status row");
using (var db = new SubdlSqliteContext(sqlitePath))
{
    var row = db.StatusRows.FirstOrDefault();
    Check(row != null, "status row present");
    if (row != null)
    {
        Console.WriteLine("   Uploaded=" + row.Uploaded + " Downloaded=" + row.Downloaded
            + " RejectedDownload=" + row.RejectedDownload + " RejectedUpload=" + row.RejectedUpload
            + " FittedToAudio=" + row.FittedToAudio);
    }
}

// ---------------------------------------------------------------------------------------------
// 6. A SECOND start must be a no-op: the import runs once, not on every boot.
// ---------------------------------------------------------------------------------------------
Section("second start (must not double the rows)");
using (var db = new SubdlSqliteContext(sqlitePath))
{
    var before = AreaTotalSqlite(db);
    using (var again = new SubdlDbContext(workdir, null))
    {
        _ = again.Meta.FindById("db");
    }

    using var after = new SubdlSqliteContext(sqlitePath);
    var afterCount = after.GetAreaRowCounts().Sum(x => x.Rows);
    Check(before == afterCount, "row total unchanged after a second start", before + " -> " + afterCount);
}

// ---------------------------------------------------------------------------------------------
// 7. The counters must be reachable through the real context the registries use, and writable.
// ---------------------------------------------------------------------------------------------
Section("live context round trip (as a registry would do it)");
using (var ctx = new SubdlDbContext(workdir, null))
{
    var all = ctx.Counters.FindAll().ToList();
    var first = all.FirstOrDefault();
    Check(first != null, "counters readable through SubdlDbContext");

    if (first != null)
    {
        var key = first.Key;
        var original = first.Value;
        var probe = ctx.Counters.FindOne(c => c.Key == key);
        Check(probe != null, "FindOne by business key works");
        probe!.Value = original + 7;
        ctx.Counters.Upsert(probe);

        var reread = ctx.Counters.FindById(key);
        Check(reread != null && reread.Value == original + 7, "upsert by business key persists", "value " + original + " -> " + (reread?.Value));

        reread!.Value = original;
        ctx.Counters.Upsert(reread);
        Check(ctx.Counters.FindById(key)!.Value == original, "value restored");
    }

    // The reset path deletes by key prefix, exactly as SubdlReset does.
    var before = ctx.Counters.Count();
    var doomed = ctx.Counters.FindAll().Where(c => c.Key.StartsWith("file-retry:", StringComparison.Ordinal)).ToList();
    Check(doomed.Count == 0 || ctx.Counters.DeleteMany(c => c.Key.StartsWith("file-retry:", StringComparison.Ordinal)) == doomed.Count,
        "DeleteMany by key prefix works");
}

// ---------------------------------------------------------------------------------------------
// 8. Compaction on the imported file.
// ---------------------------------------------------------------------------------------------
Section("compaction");
using (var ctx = new SubdlDbContext(workdir, null))
{
    var (beforeB, afterB, rebuilt, outcome) = ctx.Compact();
    Console.WriteLine("   " + beforeB + " -> " + afterB + " bytes, " + rebuilt + " area(s), outcome " + outcome);
    Check(outcome is CompactOutcome.Compacted or CompactOutcome.Recovered, "compaction returns a usable outcome", outcome.ToString());
    Check(ctx.Meta.FindById("db") != null, "store answers after compaction");
}

// =============================================================================================
// PART TWO — the three paths the operator drives after an import lands: refresh, reset, restore.
//
// ISOLATION IS THE POINT. Every section below works on its OWN COPY of the imported store. The
// refresh prunes rows and the reset deletes them, so a shared directory would let one section's
// housekeeping decide whether the next section has anything to assert — and a suite whose green
// depends on the order of its own sections proves nothing. Each section therefore copies the
// imported file first, and the copy is what it mutates.
// =============================================================================================

var importedPath = DbFiles.PathIn(workdir);

string FreshCopy(string name)
{
    var dir = Path.Combine(workdir, name);
    if (Directory.Exists(dir))
    {
        Directory.Delete(dir, recursive: true);
    }

    Directory.CreateDirectory(dir);
    File.Copy(importedPath, DbFiles.PathIn(dir), overwrite: true);
    return dir;
}

// ---------------------------------------------------------------------------------------------
// 9. DATABASE REFRESH — the prune machinery, driven without a Jellyfin host.
//
//    The refresh task itself needs ILibraryManager and IMediaSourceManager, so it cannot be
//    instantiated here. What CAN be driven is every decision it makes: each tracker's
//    PruneDeadItems takes a predicate, and the file-side sweeps take a root list. Three properties
//    are asserted, and the order matters:
//
//      a. a LIVE item is never pruned — the difference between housekeeping and data loss;
//      b. a DEAD item IS pruned — otherwise the path is merely untested, not proven;
//      c. the FAIL-SAFE rule (F-M234, "unknown != deleted"): an unusable root prunes NOTHING, and a
//         usable root with a live file prunes NOTHING either. The dangerous outcome is a false
//         prune on an unmounted volume, so the rule is asserted from both sides.
// ---------------------------------------------------------------------------------------------
Section("database refresh — prune, driven with a predicate (F-M234)");
{
    var refreshDir = FreshCopy("refresh-probe");
    using var db = new SubdlDbContext(refreshDir, null);

    // A probe directory the harness owns, standing in for a library root.
    var probeRoot = Path.Combine(workdir, "probe-library");
    Directory.CreateDirectory(probeRoot);

    // A media file that EXISTS, under a root the harness can list: the positive case.
    var liveMedia = Path.Combine(probeRoot, "live.mkv");
    File.WriteAllText(liveMedia, "media");

    // A sidecar whose file EXISTS — a verdict the refresh must NOT forget.
    var liveSidecar = Path.Combine(probeRoot, "live.de.srt");
    File.WriteAllText(liveSidecar, "1\n00:00:00,000 --> 00:00:01,000\nx\n");

    // A sidecar whose file is GONE — a verdict the refresh MUST forget.
    var goneSidecar = Path.Combine(probeRoot, "gone.de.srt");

    var itemIds = db.Media.FindAll()
        .Select(m => m.JellyfinItemId)
        .Where(id => !string.IsNullOrEmpty(id))
        .Select(id => id!)
        .Distinct()
        .ToList();

    // The imported store carries ONE item id, which is not enough to tell "pruned the dead one"
    // from "pruned everything". So the fixture is made explicit rather than hoped for: two media
    // rows with ids the harness owns, one declared dead, one alive, each with a search stamp and a
    // retry budget to lose.
    const string aliveItem = "11111111-1111-1111-1111-111111111111";
    const string deadItem = "22222222-2222-2222-2222-222222222222";

    db.Media.Upsert(new MediaEntity
    {
        Id = "probe-alive",
        Path = liveMedia,
        JellyfinItemId = aliveItem,
        LastSearchUtc = DateTime.UtcNow,
        LastSearchLanguages = "EN",
        SubtitlesUploadedAt = DateTime.UtcNow,
    });
    db.Media.Upsert(new MediaEntity
    {
        Id = "probe-dead",
        Path = Path.Combine(probeRoot, "dead.mkv"),
        JellyfinItemId = deadItem,
        LastSearchUtc = DateTime.UtcNow,
        LastSearchLanguages = "EN",
        SubtitlesUploadedAt = DateTime.UtcNow,
    });

    db.Counters.Upsert(new CounterEntity { Key = "file-retry:" + aliveItem, Value = 2 });
    db.Counters.Upsert(new CounterEntity { Key = "file-retry:" + deadItem, Value = 5 });
    db.Counters.Upsert(new CounterEntity { Key = "id-not-found:" + deadItem, Value = 3 });

    db.RejectedCandidates.Upsert(new RejectedCandidateEntity
    {
        Id = SubdlDbContext.CandidateKey(deadItem, "EN", "900-901"),
        ItemId = deadItem,
        Language = "EN",
        SubdlId = "900-901",
        Reason = "candidate-rejected",
    });
    db.RejectedCandidates.Upsert(new RejectedCandidateEntity
    {
        Id = SubdlDbContext.CandidateKey(aliveItem, "EN", "902-903"),
        ItemId = aliveItem,
        Language = "EN",
        SubdlId = "902-903",
        Reason = "candidate-rejected",
    });

    // Two sidecar verdicts: one whose file is there, one whose file is not.
    db.Sidecars.Upsert(new SidecarEntity
    {
        Id = "probe-live-sidecar",
        ContentHash = "probe-live-sidecar",
        MediaHash = "probe-alive",
        Language = "DE",
        Status = SubtitleStatus.Uploaded,
        Path = liveSidecar,
        FileName = "live.de.srt",
    });
    db.Sidecars.Upsert(new SidecarEntity
    {
        Id = "probe-gone-sidecar",
        ContentHash = "probe-gone-sidecar",
        MediaHash = "probe-alive",
        Language = "DE",
        Status = SubtitleStatus.Uploaded,
        Path = goneSidecar,
        FileName = "gone.de.srt",
    });

    // An oshash entry whose file exists (must survive) and one whose file is gone (may go).
    db.Oshashes.Upsert(new OshashEntity { Id = liveMedia, Hash = "h-live", Size = 5 });
    db.Oshashes.Upsert(new OshashEntity { Id = Path.Combine(probeRoot, "vanished.mkv"), Hash = "h-gone", Size = 5 });

    Console.WriteLine("   fixture: item ids in the store = " + itemIds.Count
        + ", plus one alive and one dead id owned by the harness");

    bool ItemExists(string id) => !string.Equals(id, deadItem, StringComparison.OrdinalIgnoreCase);

    // --- 9a. Trackers keyed by item id: the dead item goes, the live one stays. ---
    var searchPruned = new DownloadSearchTracker(db).PruneDeadItems(ItemExists);
    var deadKeepsStamp = db.Media.FindById("probe-dead")!.LastSearchUtc != null;
    var aliveKeepsStamp = db.Media.FindById("probe-alive")!.LastSearchUtc != null;
    Console.WriteLine("   search stamps pruned: " + searchPruned + " | dead keeps stamp: " + deadKeepsStamp + " | live keeps stamp: " + aliveKeepsStamp);
    Check(searchPruned >= 1, "the dead item's search stamp was pruned", searchPruned.ToString());
    Check(!deadKeepsStamp, "the DEAD item lost its search stamp");
    Check(aliveKeepsStamp, "the LIVE item KEPT its search stamp");

    // --- 9b. Counters keyed by a prefixed item id, on the retry budgets. ---
    var fileRetryPruned = new FileRetryTracker(db).PruneDeadItems(ItemExists);
    var idNotFoundPruned = new IdNotFoundTracker(db).PruneDeadItems(ItemExists);
    var aliveRetry = db.Counters.FindById("file-retry:" + aliveItem);
    var deadRetry = db.Counters.FindById("file-retry:" + deadItem);
    Console.WriteLine("   counters pruned: file-retry " + fileRetryPruned + ", id-not-found " + idNotFoundPruned);
    Check(deadRetry == null, "the DEAD item's retry budget was pruned");
    Check(aliveRetry != null && aliveRetry.Value == 2, "the LIVE item's retry budget SURVIVED with its value",
        aliveRetry?.Value.ToString() ?? "(missing)");

    // --- 9c. Burned candidates, keyed by item id. ---
    var candidatesPruned = new QaFailTracker(db).PruneDeadItems(ItemExists);
    var deadCandidates = db.RejectedCandidates.FindAll().Count(c => c.ItemId == deadItem);
    var aliveCandidates = db.RejectedCandidates.FindAll().Count(c => c.ItemId == aliveItem);
    Console.WriteLine("   candidates pruned: " + candidatesPruned + " | dead left: " + deadCandidates + " | live left: " + aliveCandidates);
    Check(deadCandidates == 0, "no burned candidate of the DEAD item is left");
    Check(aliveCandidates == 1, "the LIVE item's burned candidate SURVIVED", aliveCandidates.ToString());

    // --- 9d. The file side: a usable root with a live file prunes NOTHING. ---
    Check(SubtitlePresence.RootsUsable(new[] { probeRoot }), "the probe root is usable (it exists and lists)");

    var oshash = new OshashCache(db);
    var oshashPrunedUsable = oshash.PruneStalePaths(new[] { probeRoot });
    var liveOshashKept = oshash.GetAllPaths().Any(p => string.Equals(p, liveMedia, StringComparison.Ordinal));
    var goneOshashKept = oshash.GetAllPaths().Any(p => p.Contains("vanished.mkv", StringComparison.Ordinal));
    Console.WriteLine("   oshash pruned with a USABLE root: " + oshashPrunedUsable
        + " | live entry kept: " + liveOshashKept + " | vanished entry kept: " + goneOshashKept);
    Check(liveOshashKept, "the LIVE file keeps its oshash entry");
    Check(!goneOshashKept, "the vanished file LOST its oshash entry");

    var registry = new ContentHashRegistry(db);
    var vanishedSidecars = registry.GetSidecarsMissingFromDisk();
    var forgotten = registry.ForgetSidecars(vanishedSidecars);
    var liveSidecarKept = db.Sidecars.FindById("probe-live-sidecar") != null;
    var goneSidecarKept = db.Sidecars.FindById("probe-gone-sidecar") != null;
    Console.WriteLine("   sidecar verdicts forgotten: " + forgotten
        + " | the live file's verdict kept: " + liveSidecarKept + " | the vanished file's verdict kept: " + goneSidecarKept);
    Check(liveSidecarKept, "the LIVE subtitle keeps its verdict");
    Check(!goneSidecarKept, "the VANISHED subtitle lost its verdict");

    // --- 9e. The FAIL-SAFE rule, from the other side: an unusable root prunes NOTHING. ---
    var missingRoot = Path.Combine(workdir, "no-such-library-root");
    Check(!SubtitlePresence.RootsUsable(new[] { missingRoot }), "a missing root is reported unusable");
    Check(!SubtitlePresence.RootsUsable(Array.Empty<string>()), "an empty root list is reported unusable");

    // A FRESH dead entry, because the usable-root sweep above already removed the first one — a
    // fail-safe test with nothing left to lose cannot fail and therefore proves nothing.
    var freshDead = Path.Combine(probeRoot, "vanished-2.mkv");
    db.Oshashes.Upsert(new OshashEntity { Id = freshDead, Hash = "h-gone-2", Size = 5 });

    var oshashBefore = oshash.GetAllPaths().Count;
    Check(!File.Exists(freshDead) && oshash.GetAllPaths().Contains(freshDead),
        "a dead oshash entry is planted before the fail-safe test");
    var prunedMissingRoot = oshash.PruneStalePaths(new[] { missingRoot });
    var prunedEmptyList = oshash.PruneStalePaths(Array.Empty<string>());
    Console.WriteLine("   oshash with a missing root: " + prunedMissingRoot + ", with an empty list: " + prunedEmptyList
        + ", rows before: " + oshashBefore);
    Check(prunedMissingRoot == 0, "a MISSING root removes no oshash row (fail-safe)", prunedMissingRoot.ToString());
    Check(prunedEmptyList == 0, "an EMPTY root list removes no oshash row (fail-safe)", prunedEmptyList.ToString());
    Check(oshash.GetAllPaths().Count == oshashBefore, "the oshash cache is untouched by an unusable root");
    Check(oshash.GetAllPaths().Contains(freshDead), "the PLANTED dead entry survived the unusable root — the sweep ran and refused");

    // An unreadable directory, when the OS enforces the mode. Reported either way rather than
    // asserted, because a run as root can read mode 000 and would fail the platform, not the rule —
    // and the two other unusable cases above carry the assertion.
    var unreadable = Path.Combine(workdir, "unreadable-root");
    Directory.CreateDirectory(unreadable);
    File.WriteAllText(Path.Combine(unreadable, "x"), "x");
    bool couldNotRead = false;
    try
    {
        File.SetUnixFileMode(unreadable, UnixFileMode.None);
        couldNotRead = !SubtitlePresence.RootsUsable(new[] { unreadable });
    }
    catch (Exception ex)
    {
        Console.WriteLine("   (could not set mode 000: " + ex.Message + ")");
    }
    finally
    {
        File.SetUnixFileMode(unreadable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    Console.WriteLine("   a directory with mode 000 is reported unusable: " + couldNotRead
        + " (only measurable when the OS enforces the mode)");

    // --- 9f. The dead-media sweep, both directions. ---
    var (prunedSubs, prunedMedia) = registry.PruneDeadMediaAndSubtitles(new[] { aliveItem });
    Console.WriteLine("   dead-media sweep with ONLY the live id alive: " + prunedSubs + " subtitle row(s), " + prunedMedia + " media row(s)");
    Check(db.Media.FindById("probe-dead") == null, "the DEAD item's media row was pruned");
    Check(db.Media.FindById("probe-alive") != null, "the LIVE item's media row SURVIVED");

    var beforeAllAlive = db.Media.Count();
    var (subsAllAlive, mediaAllAlive) = registry.PruneDeadMediaAndSubtitles(new[] { aliveItem, deadItem });
    Console.WriteLine("   dead-media sweep with EVERY id alive: " + subsAllAlive + " subtitle row(s), " + mediaAllAlive + " media row(s)");
    Check(mediaAllAlive == 0 && subsAllAlive == 0, "nothing is pruned while every item id is alive");
    Check(db.Media.Count() == beforeAllAlive, "the row count is unchanged when every id is alive");

    // --- 9g. The refresh's own compaction (its Phase 5) leaves a usable store. ---
    var (rBefore, rAfter, rAreas, rOutcome) = db.Compact();
    Console.WriteLine("   refresh compaction: " + rBefore + " -> " + rAfter + " bytes, " + rAreas + " area(s), " + rOutcome);
    Check(rOutcome is CompactOutcome.Compacted or CompactOutcome.Recovered, "refresh compaction is usable", rOutcome.ToString());
    Check(db.Meta.FindById("db") != null, "store answers after the refresh's compaction");
}

// ---------------------------------------------------------------------------------------------
// 10. RESET — all three scopes on the imported data.
//
//     `upload` and `download` are driven through the same statements SubdlReset runs, in the same
//     order. `all` is driven through the file-replacing sequence: checkpoint, dispose, drop pooled
//     handles, copy to the timestamped backup, delete, remove the side files, reopen.
// ---------------------------------------------------------------------------------------------
Section("reset scopes");
{
    var resetDir = FreshCopy("reset-probe");
    using var db = new SubdlDbContext(resetDir, null);

    // The imported store carries no upload marker and no retry budget of its own, so the fixture is
    // completed here — a reset tested against a store with nothing to clear proves nothing.
    db.Media.Upsert(new MediaEntity { Id = "reset-marked-1", Path = "/lib/rm1.mkv", SubtitlesUploadedAt = DateTime.UtcNow });
    db.Media.Upsert(new MediaEntity { Id = "reset-marked-2", Path = "/lib/rm2.mkv", SubtitlesUploadedAt = DateTime.UtcNow });
    db.Media.Upsert(new MediaEntity { Id = "reset-plain", Path = "/lib/rm3.mkv", SubtitlesUploadedAt = null, LastSearchUtc = DateTime.UtcNow, LastSearchLanguages = "EN" });
    db.Counters.Upsert(new CounterEntity { Key = "file-retry:keepme", Value = 4 });
    db.Counters.Upsert(new CounterEntity { Key = "qa-fail:dropme|EN", Value = 1 });
    db.Sidecars.Upsert(new SidecarEntity { Id = "reset-dl", ContentHash = "reset-dl", MediaHash = "reset-plain", Language = "DE", Status = SubtitleStatus.Downloaded });
    db.Sidecars.Upsert(new SidecarEntity { Id = "reset-obs", ContentHash = "reset-obs", MediaHash = "reset-plain", Language = "DE", Status = SubtitleStatus.Observed });

    // --- 10a. "upload": clears the uploaded markers on exactly the rows that carry one. ---
    var carrying = db.Media.FindAll().Count(m => m.SubtitlesUploadedAt != null);
    var embedsBeforeUpload = db.Embeds.Count();
    var touched = new List<MediaEntity>();
    foreach (var media in db.Media.FindAll().Where(m => m.SubtitlesUploadedAt != null))
    {
        media.SubtitlesUploadedAt = null;
        touched.Add(media);
    }

    if (touched.Count > 0)
    {
        db.Media.Update(touched);
    }

    Console.WriteLine("   upload scope: " + carrying + " row(s) carried a marker, " + touched.Count + " collected");
    Check(carrying >= 2, "the fixture carries markers to clear", carrying.ToString());
    Check(touched.Count == carrying, "the upload scope collects EVERY marked row", touched.Count + " of " + carrying);
    Check(db.Media.FindAll().All(m => m.SubtitlesUploadedAt == null), "no uploaded marker survives the upload scope");
    Check(db.Embeds.Count() == embedsBeforeUpload, "the upload scope does not touch embedded rows",
        db.Embeds.Count() + " of " + embedsBeforeUpload);

    // --- 10b. "download": downloaded sidecars, burned candidates, QA/id counters, search stamps. ---
    var downloadedBefore = db.Sidecars.Count(x => x.Status == SubtitleStatus.Downloaded);
    var observedBefore = db.Sidecars.Count(x => x.Status == SubtitleStatus.Observed);
    var candidatesBefore = db.RejectedCandidates.Count();
    var qaCountersBefore = db.Counters.Count(x => x.Key.StartsWith("qa-fail:") || x.Key.StartsWith("id-not-found:"));
    var fileRetryBefore = db.Counters.Count(x => x.Key.StartsWith("file-retry:"));
    var stampsBefore = db.Media.Count(m => m.LastSearchUtc != null);

    // The same statements SubdlReset runs for this scope.
    var removedSidecars = db.Sidecars.DeleteMany(x => x.Status == SubtitleStatus.Downloaded);
    db.RejectedCandidates.DeleteAll();
    db.Counters.DeleteMany(x => x.Key.StartsWith("qa-fail:") || x.Key.StartsWith("id-not-found:"));
    foreach (var media in db.Media.Find(x => x.LastSearchUtc != null))
    {
        media.LastSearchUtc = null;
        media.LastSearchLanguages = null;
        db.Media.Update(media);
    }

    var fileRetryAfter = db.Counters.Count(x => x.Key.StartsWith("file-retry:"));
    Console.WriteLine("   download scope: sidecars " + downloadedBefore + " -> " + db.Sidecars.Count(x => x.Status == SubtitleStatus.Downloaded)
        + ", candidates " + candidatesBefore + " -> " + db.RejectedCandidates.Count()
        + ", qa/id counters " + qaCountersBefore + " -> " + db.Counters.Count(x => x.Key.StartsWith("qa-fail:") || x.Key.StartsWith("id-not-found:"))
        + ", search stamps " + stampsBefore + " -> " + db.Media.Count(m => m.LastSearchUtc != null));

    Check(downloadedBefore >= 1, "the fixture holds a downloaded sidecar to remove", downloadedBefore.ToString());
    Check(removedSidecars == downloadedBefore, "EVERY downloaded sidecar was removed", removedSidecars + " of " + downloadedBefore);
    Check(db.Sidecars.Count(x => x.Status == SubtitleStatus.Observed) == observedBefore,
        "observed sidecars survive the download scope", db.Sidecars.Count(x => x.Status == SubtitleStatus.Observed) + " of " + observedBefore);
    Check(db.RejectedCandidates.Count() == 0, "burned candidates cleared");
    Check(stampsBefore >= 1, "the fixture carries a search stamp to clear", stampsBefore.ToString());
    Check(db.Media.FindAll().All(m => m.LastSearchUtc == null), "EVERY search stamp cleared");
    Check(fileRetryBefore >= 1, "the fixture holds a file-retry budget", fileRetryBefore.ToString());
    Check(fileRetryAfter == fileRetryBefore, "file-retry counters SURVIVE the download scope (not that scope's state)",
        fileRetryAfter + " of " + fileRetryBefore);
}

// --- 10c. "all": the file-replacing sequence, then an empty but usable store, plus a complete backup. ---
{
    var allDir = FreshCopy("reset-all-probe");
    var allDbPath = DbFiles.PathIn(allDir);
    var allStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    var allBackupPath = allDbPath + ".bak-" + allStamp;

    int rowsBeforeReset;
    using (var db = new SubdlDbContext(allDir, null))
    {
        rowsBeforeReset = (int)AreaTotal(db);
        Console.WriteLine("   all scope: store holds " + rowsBeforeReset + " row(s) before the reset");

        // The plugin's own order (Api.SubdlReset): fold the WAL, dispose, drop pooled handles, copy,
        // delete, side files, reopen.
        db.Checkpoint();
        db.Dispose();
        SubdlDbContext.ClearPoolFor(allDbPath);

        if (File.Exists(allDbPath))
        {
            File.Copy(allDbPath, allBackupPath, overwrite: true);
            File.Delete(allDbPath);
        }

        foreach (var suffix in DbFiles.SideSuffixes)
        {
            var side = DbFiles.SidePath(allDbPath, suffix);
            if (File.Exists(side))
            {
                File.Delete(side);
            }
        }
    }

    Check(File.Exists(allBackupPath), "the reset wrote a timestamped backup", allStamp);
    var backupBytes = File.Exists(allBackupPath) ? new FileInfo(allBackupPath).Length : 0;
    Console.WriteLine("   backup: " + Path.GetFileName(allBackupPath) + " at " + backupBytes + " B");

    // The old file must be gone and the side files with it — a -wal beside a deleted database would
    // be applied to whatever file comes next.
    var leftovers = DbFiles.SideSuffixes.Where(s => File.Exists(DbFiles.SidePath(allDbPath, s))).ToList();
    Check(leftovers.Count == 0, "no side file survives the all-scope reset", string.Join(",", leftovers));

    // The backup must be a COMPLETE snapshot: a backup taken without folding the WAL first carries
    // the main file alone. So it is opened ON ITS OWN and compared with what the store held before.
    {
        var probeDir = Path.Combine(workdir, "backup-probe");
        if (Directory.Exists(probeDir))
        {
            Directory.Delete(probeDir, recursive: true);
        }

        Directory.CreateDirectory(probeDir);
        File.Copy(allBackupPath, DbFiles.PathIn(probeDir), overwrite: true);
        using var probe = new SubdlDbContext(probeDir, null);
        var backupRows = (int)AreaTotal(probe);
        Console.WriteLine("   the backup opens on its own and reports " + backupRows + " of " + rowsBeforeReset + " row(s)");
        Check(backupRows == rowsBeforeReset, "the backup is a COMPLETE snapshot (not the main file alone)",
            backupRows + " of " + rowsBeforeReset);
        Check(probe.Meta.FindById("db") != null, "the backup carries the meta row (it is a real store)");
    }

    using (var db = new SubdlDbContext(allDir, null))
    {
        var afterReset = (int)AreaTotal(db);
        Console.WriteLine("   all scope: store holds " + afterReset + " row(s) after the reset");
        Check(db.Meta.FindById("db") != null, "the store is usable after the all-scope reset");
        Check(db.Media.Count() == 0, "no imported media row survives the all-scope reset", db.Media.Count().ToString());
        Check(db.Embeds.Count() == 0, "no imported embedded row survives either", db.Embeds.Count().ToString());
        Check(db.Oshashes.Count() == 0, "no imported oshash row survives either", db.Oshashes.Count().ToString());
    }
}

// ---------------------------------------------------------------------------------------------
// 11. RESTORE — round trip through the plugin's own backup format.
//
//     The dangerous failure here is silent: a restore that leaves a pooled handle open reads the
//     OLD rows and looks like a success. So this mutates the store first, restores, and asserts
//     VALUES (not presence) against the pre-mutation state.
// ---------------------------------------------------------------------------------------------
Section("restore — round trip through the plugin's own backup format");
{
    var restoreDir = Path.Combine(workdir, "restore-probe");
    if (Directory.Exists(restoreDir))
    {
        Directory.Delete(restoreDir, recursive: true);
    }

    Directory.CreateDirectory(restoreDir);
    var restoreDbPath = DbFiles.PathIn(restoreDir);

    long uploadedBefore, downloadedBefore, rejectedDownloadBefore;
    int countersBefore, candidatesBefore, sidecarsBefore, mediaBefore;

    using (var db = new SubdlDbContext(restoreDir, null))
    {
        db.Meta.Upsert(new MetaEntity { Id = "db", SchemaVersion = SubdlDbContext.CurrentSchemaVersion, Updated = DateTime.UtcNow });
        for (int i = 0; i < 7; i++)
        {
            db.Media.Upsert(new MediaEntity { Id = "rm" + i, Path = "/lib/rm" + i + ".mkv", JellyfinItemId = Guid.NewGuid().ToString("D") });
            db.Sidecars.Upsert(new SidecarEntity
            {
                Id = "rsc" + i,
                ContentHash = "rsc" + i,
                MediaHash = "rm" + i,
                Language = "DE",
                Status = i < 4 ? SubtitleStatus.Downloaded : SubtitleStatus.Observed,
            });
            db.Counters.Upsert(new CounterEntity { Key = "file-retry:rm" + i, Value = 10 + i });
        }

        db.RejectedCandidates.Upsert(new RejectedCandidateEntity
        {
            Id = SubdlDbContext.CandidateKey("rm0", "EN", "7-8"),
            ItemId = "rm0",
            Language = "EN",
            SubdlId = "7-8",
            Reason = "candidate-rejected",
        });

        db.StatusStats.Upsert(new StatusStatsEntity
        {
            Id = "status",
            Uploaded = 71,
            Downloaded = 16,
            RejectedDownload = 44,
            RejectedUpload = 17,
            FittedToAudio = 3,
            SinceUtc = DateTime.UtcNow,
        });

        db.Checkpoint();

        mediaBefore = db.Media.Count();
        uploadedBefore = db.StatusStats.FindById("status")!.Uploaded;
        downloadedBefore = db.StatusStats.FindById("status")!.Downloaded;
        rejectedDownloadBefore = db.StatusStats.FindById("status")!.RejectedDownload;
        countersBefore = db.Counters.Count();
        candidatesBefore = db.RejectedCandidates.Count();
        sidecarsBefore = db.Sidecars.Count();
        Console.WriteLine("   built a store: " + mediaBefore + " media, " + sidecarsBefore + " sidecars, "
            + countersBefore + " counters, " + candidatesBefore + " candidates, Uploaded=" + uploadedBefore
            + " Downloaded=" + downloadedBefore + " RejectedDownload=" + rejectedDownloadBefore);
        db.Dispose();
        SubdlDbContext.ClearPoolFor(restoreDbPath);
    }

    var restoreStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    var restoreBackupPath = restoreDbPath + ".bak-" + restoreStamp;
    File.Copy(restoreDbPath, restoreBackupPath, overwrite: true);
    Console.WriteLine("   backup taken: " + Path.GetFileName(restoreBackupPath) + " at " + new FileInfo(restoreBackupPath).Length + " B");

    // Mutate the live store so the restore has something to undo — rows deleted, values changed.
    using (var db = new SubdlDbContext(restoreDir, null))
    {
        db.Media.Delete("rm0");
        db.Media.Delete("rm1");
        db.Media.Upsert(new MediaEntity { Id = "intruder", Path = "/lib/intruder.mkv" });
        db.Counters.DeleteAll();
        db.RejectedCandidates.DeleteAll();
        db.StatusStats.Upsert(new StatusStatsEntity { Id = "status", Uploaded = 999, Downloaded = 999, RejectedDownload = 999 });

        using var verify = new SubdlDbContext(restoreDir, null);
        Check(verify.Media.Count() == mediaBefore - 1, "the live store is mutated before the restore",
            verify.Media.Count() + " of " + (mediaBefore - 1));
        Check(verify.Media.FindById("intruder") != null, "an extra row is planted before the restore");
        Check(verify.Counters.Count() == 0, "counters cleared before the restore");
        Check(verify.StatusStats.FindById("status")!.Uploaded == 999, "a wrong value is planted before the restore");
    }

    // The restore, in the plugin's own order (Api.SubdlReset.Restore): checkpoint, dispose, drop
    // pooled handles, keep a pre-restore copy, CLEAR THE SIDE FILES FIRST, copy back, reopen.
    using (var db = new SubdlDbContext(restoreDir, null))
    {
        db.Checkpoint();
        db.Dispose();
    }

    SubdlDbContext.ClearPoolFor(restoreDbPath);

    var preRestoreCopy = restoreDbPath + ".pre-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    if (File.Exists(restoreDbPath))
    {
        File.Copy(restoreDbPath, preRestoreCopy, overwrite: true);
    }

    foreach (var suffix in DbFiles.SideSuffixes)
    {
        var side = DbFiles.SidePath(restoreDbPath, suffix);
        if (File.Exists(side))
        {
            File.Delete(side);
        }
    }

    File.Copy(restoreBackupPath, restoreDbPath, overwrite: true);

    using (var db = new SubdlDbContext(restoreDir, null))
    {
        // Values, not merely presence: a restore that keeps the OLD rows is exactly the silent
        // failure the connection pool caused, and a count alone would not catch it.
        Check(db.Media.Count() == mediaBefore, "media rows restored", db.Media.Count() + " of " + mediaBefore);
        Check(db.Media.FindById("rm0") != null && db.Media.FindById("rm1") != null, "the deleted rows are back");
        Check(db.Media.FindById("intruder") == null, "the planted row is GONE after the restore");
        Check(db.Counters.Count() == countersBefore, "counter rows restored", db.Counters.Count() + " of " + countersBefore);
        Check(db.RejectedCandidates.Count() == candidatesBefore, "burned candidates restored", db.RejectedCandidates.Count() + " of " + candidatesBefore);
        Check(db.Sidecars.Count() == sidecarsBefore, "sidecar rows restored", db.Sidecars.Count() + " of " + sidecarsBefore);

        var status = db.StatusStats.FindById("status");
        Check(status != null && status.Uploaded == uploadedBefore, "the mutated Uploaded value is back to the backup's", "999 -> " + status?.Uploaded);
        Check(status != null && status.Downloaded == downloadedBefore, "the mutated Downloaded value is back to the backup's", "999 -> " + status?.Downloaded);
        Check(status != null && status.RejectedDownload == rejectedDownloadBefore, "the mutated RejectedDownload value is back to the backup's",
            "999 -> " + status?.RejectedDownload);
        Check(db.Counters.FindById("file-retry:rm3")?.Value == 13, "a counter VALUE is exact after the restore",
            db.Counters.FindById("file-retry:rm3")?.Value.ToString() ?? "(missing)");
    }

    // -----------------------------------------------------------------------------------------
    // 12. A STALE write-ahead log must not survive a restore. This is the mistake that corrupts
    //     rather than merely loses: SQLite would replay foreign pages over the restored file.
    // -----------------------------------------------------------------------------------------
    Section("restore against a stale write-ahead log");
    {
        // Leave a row behind in the live store, then plant a foreign journal beside it.
        using (var staleDb = new SubdlDbContext(restoreDir, null))
        {
            staleDb.Media.Upsert(new MediaEntity { Id = "STALE", Path = "/stale.mkv", JellyfinItemId = Guid.NewGuid().ToString("D") });
        }

        var walPath = DbFiles.SidePath(restoreDbPath, "-wal");
        File.WriteAllBytes(walPath, new byte[] { 0x37, 0x7f, 0x06, 0x82, 0, 0, 0, 0 });
        Console.WriteLine("   planted a foreign -wal beside the database: " + new FileInfo(walPath).Length + " B");

        foreach (var suffix in DbFiles.SideSuffixes)
        {
            var side = DbFiles.SidePath(restoreDbPath, suffix);
            if (File.Exists(side))
            {
                File.Delete(side);
            }
        }

        File.Copy(restoreBackupPath, restoreDbPath, overwrite: true);

        using var db = new SubdlDbContext(restoreDir, null);
        Check(db.Media.FindById("STALE") == null, "no stale row survived the restore");
        Check(db.Media.Count() == mediaBefore, "the restored row count is still exact", db.Media.Count() + " of " + mediaBefore);
        Check(db.ReadJournalMode().Equals("wal", StringComparison.OrdinalIgnoreCase), "the restored store is in WAL mode again",
            db.ReadJournalMode());
    }

    // -----------------------------------------------------------------------------------------
    // 13. The negative control: an OPEN context is NOT affected by swapping the file underneath it.
    //     That is precisely why the plugin's restore disposes the context and drops the pooled
    //     handles BEFORE the copy — without those two calls the running plugin keeps reading the
    //     generation it opened, and the restore looks like it succeeded while nothing changed.
    // -----------------------------------------------------------------------------------------
    Section("restore — negative control (the handle is held)");
    {
        var negDir = Path.Combine(workdir, "pool-negative");
        if (Directory.Exists(negDir))
        {
            Directory.Delete(negDir, recursive: true);
        }

        Directory.CreateDirectory(negDir);
        var negPath = DbFiles.PathIn(negDir);

        using (var db = new SubdlDbContext(negDir, null))
        {
            db.Meta.Upsert(new MetaEntity { Id = "db", SchemaVersion = SubdlDbContext.CurrentSchemaVersion, Updated = DateTime.UtcNow });
            for (int i = 0; i < 3; i++)
            {
                db.Media.Upsert(new MediaEntity { Id = "nm" + i, Path = "/lib/nm" + i + ".mkv" });
            }

            db.Checkpoint();
            db.Dispose();
            SubdlDbContext.ClearPoolFor(negPath);
        }

        var negBackup = negPath + ".bak-20261008-120000";
        File.Copy(negPath, negBackup, overwrite: true);

        // The context is opened and HELD across the swap — the mistake the plugin's Dispose +
        // ClearPoolFor exist to prevent.
        var held = new SubdlDbContext(negDir, null);
        held.Media.Delete("nm0");
        held.Media.Upsert(new MediaEntity { Id = "nm9", Path = "/lib/nm9.mkv" });

        foreach (var suffix in DbFiles.SideSuffixes)
        {
            var side = DbFiles.SidePath(negPath, suffix);
            if (File.Exists(side))
            {
                File.Delete(side);
            }
        }

        File.Copy(negBackup, negPath, overwrite: true);

        var stillHeld = held.Media.FindById("nm9");
        var heldCount = held.Media.Count();
        Console.WriteLine("   with the handle held across the swap, the context still reports: "
            + heldCount + " media, nm9 " + (stillHeld != null ? "visible" : "gone"));
        Check(stillHeld != null, "an OPEN context keeps reading its own generation across a file swap");
        held.Dispose();
        SubdlDbContext.ClearPoolFor(negPath);

        // And the same directory read freshly DOES see the backup's generation — which is what the
        // plugin's restore gets because it disposes first.
        using var fresh = new SubdlDbContext(negDir, null);
        var freshCount = fresh.Media.Count();
        Check(fresh.Media.FindById("nm9") == null, "a FRESH context sees the restored generation instead", freshCount + " media");
        Check(freshCount == 3, "the fresh context reads the backup's three rows", freshCount.ToString());
    }
}

// ---------------------------------------------------------------------------------------------
// 14. THE ORDER IN THE PLUGIN'S OWN SOURCE. Sections 10-12 drive the sequence the plugin runs, but
//     they drive it as the HARNESS writes it — so they would stay green if the plugin's own order
//     changed. These assertions read Api/SubdlReset.cs and require the order the format needs.
// ---------------------------------------------------------------------------------------------
Section("the plugin's own order (read from Api/SubdlReset.cs)");
{
    // The source is found by walking up from the working directory, looking for the plugin project.
    // A fixed relative path was wrong twice over: it silently pointed nowhere, and the section then
    // reported a SKIP that read like a pass — the defect this harness exists to catch.
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    string? resetSource = null;
    while (dir != null)
    {
        var candidate = Path.Combine(dir.FullName, "Jellyfin.Plugin.SubdlSync", "Api", "SubdlReset.cs");
        if (File.Exists(candidate))
        {
            resetSource = candidate;
            break;
        }

        dir = dir.Parent;
    }

    Check(resetSource != null, "Api/SubdlReset.cs is reachable from the harness (else the order is unproven)");
    if (resetSource == null)
    {
        Console.WriteLine("   searched upward from " + Directory.GetCurrentDirectory());
    }
    else
    {
        var src = File.ReadAllText(resetSource!);
        Console.WriteLine("   read " + new FileInfo(resetSource!).Length + " B of " + resetSource);

        // In Restore: the side files must be cleared BEFORE the backup is copied over, otherwise
        // SQLite replays the old generation's journal onto the restored pages.
        var restoreBody = src[src.IndexOf("public IActionResult Restore", StringComparison.Ordinal)..];
        var atDeleteSide = restoreBody.IndexOf("DeleteSideFiles(dbPath)", StringComparison.Ordinal);
        var atCopy = restoreBody.IndexOf("File.Copy(backupPath, dbPath", StringComparison.Ordinal);
        Check(atDeleteSide > 0 && atCopy > 0, "both calls are present in Restore", "DeleteSideFiles=" + atDeleteSide + ", Copy=" + atCopy);
        Check(atDeleteSide < atCopy, "Restore clears the side files BEFORE it copies the backup over");

        // The pool handle must be dropped before the file is touched, or the copy is not what the
        // next open reads.
        var atClearPool = restoreBody.IndexOf("ClearPoolFor(dbPath)", StringComparison.Ordinal);
        Check(atClearPool > 0, "Restore drops the pooled handles");
        Check(atClearPool < atCopy, "Restore drops the pooled handles BEFORE the copy");

        // In Reset (scope "all"): the WAL is folded before the backup copy, and the old file is
        // deleted before the side files go.
        var resetBody = src[src.IndexOf("public IActionResult Reset", StringComparison.Ordinal)..];
        var atCheckpoint = resetBody.IndexOf("db.Checkpoint()", StringComparison.Ordinal);
        var atBackupCopy = resetBody.IndexOf("File.Copy(dbPath, backupPath", StringComparison.Ordinal);
        var atDbDelete = resetBody.IndexOf("File.Delete(dbPath)", StringComparison.Ordinal);
        Check(atCheckpoint > 0 && atBackupCopy > 0 && atDbDelete > 0,
            "the all-scope reset checkpoints, copies and deletes",
            "checkpoint=" + atCheckpoint + ", copy=" + atBackupCopy + ", delete=" + atDbDelete);
        Check(atCheckpoint < atBackupCopy, "the all-scope reset checkpoints BEFORE it takes the backup");
        Check(atBackupCopy < atDbDelete, "the all-scope reset takes the backup BEFORE it deletes the file");
        Check(resetBody.IndexOf("ClearPoolFor(dbPath)", StringComparison.Ordinal) < atDbDelete,
            "the all-scope reset drops the pooled handles BEFORE it touches the file");
    }
}

// ---------------------------------------------------------------------------------------------
// 15. THE CASE THE ONE-TIME GUARD EXISTS FOR: an old file AND a populated SQLite file side by
//     side. Section 6 re-runs the import after the source was renamed aside — where the guard is
//     never reached, because the source is gone. This is the other case, and the one a botched
//     upgrade leaves behind: the SQLite file is present and the old file is back (restored from a
//     backup, copied in by hand). The import must NOT run over a populated store.
// ---------------------------------------------------------------------------------------------
Section("an old file beside a populated store (the guard, not the rename)");
{
    var clashDir = FreshCopy("clash-probe");

    // Put the old document store back beside the imported SQLite file.
    File.Copy(legacyPath + LiteDbImport.ImportedSuffix, DbFiles.LegacyPathIn(clashDir), overwrite: true);

    int rowsBeforeClash;
    using (var db = new SubdlDbContext(clashDir, null))
    {
        rowsBeforeClash = (int)AreaTotal(db);
    }

    // Read the size WITHOUT assuming the file is still there: with the guard removed the import runs
    // and RENAMES this file aside, and a bare Length read would then crash the harness instead of
    // reporting the defect — an exit code nobody can read is not a test result.
    var clashSource = DbFiles.LegacyPathIn(clashDir);
    var sourceBytes = File.Exists(clashSource) ? new FileInfo(clashSource).Length : -1;
    Console.WriteLine("   old file beside the store: " + (sourceBytes >= 0 ? sourceBytes + " B" : "GONE (renamed aside)")
        + " | the store holds " + rowsBeforeClash + " row(s)");
    Check(sourceBytes > 0, "the old file is still in place after a start with both present (the import did not consume it)");

    using (var db = new SubdlDbContext(clashDir, null))
    {
        var rowsAfterClash = (int)AreaTotal(db);
        Console.WriteLine("   after a start with BOTH files present: " + rowsAfterClash + " row(s)");
        Check(rowsAfterClash == rowsBeforeClash, "the import did NOT run over a populated store",
            rowsAfterClash + " of " + rowsBeforeClash);
        Check(File.Exists(DbFiles.LegacyPathIn(clashDir)), "the old file was NOT renamed aside (nothing imported)");
        Check(db.Meta.FindById("db") != null, "the store is the same one, still readable");
    }

    // The store must also be intact per area, not merely the same total.
    using var check = new SubdlSqliteContext(DbFiles.PathIn(clashDir));
    var areas = check.GetAreaRowCounts().ToDictionary(x => x.Area, x => (int)x.Rows, StringComparer.OrdinalIgnoreCase);
    foreach (var kv in expected)
    {
        areas.TryGetValue(kv.Key, out var got);
        Check(got == kv.Value, "area intact after the clash: " + kv.Key.PadRight(20), got + " of " + kv.Value);
    }
}

Section("the imported file is still the source of truth");
{
    using var finalCheck = new SubdlDbContext(workdir, null);
    var stillThere = AreaTotal(finalCheck);
    Check(stillThere > 0, "the imported store still carries its rows", stillThere + " row(s)");
    Check(File.Exists(legacyPath + LiteDbImport.ImportedSuffix), "the previous data file is still kept as .imported");
}

Console.WriteLine();
Console.WriteLine("=== " + (failures == 0 ? "MIGRATION OK" : failures + " FAILURE(S)") + " ===");
return failures == 0 ? 0 : 1;

static long AreaTotalSqlite(SubdlSqliteContext db)
    => db.GetAreaRowCounts().Sum(x => x.Rows);

static long AreaTotal(SubdlDbContext db)
    => db.Meta.Count() + db.Oshashes.Count() + db.Media.Count() + db.Embeds.Count()
     + db.Sidecars.Count() + db.RejectedCandidates.Count() + db.Counters.Count()
     + db.Runs.Count() + db.StatusStats.Count() + db.WorkerRuns.Count();

static void Classify(BsonDocument d, string field, out bool? value)
{
    if (!d.TryGetValue(field, out var v) || v.IsNull)
    {
        value = null;
        return;
    }

    value = v.AsBoolean;
}
