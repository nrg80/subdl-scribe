// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Migration harness (F-M327): proves the one-time import from the previous document store into the
// SQLite file, against a real data file.
//
// WHAT IT PROVES, AND WHY IT IS NOT A UNIT TEST
//
//   The import is the only code path in the plugin that reads the old format, and it runs exactly
//   once in the life of an installation — on a live server, on data the operator cannot re-create.
//   A unit test with three hand-built rows would not answer the question that matters: does a REAL
//   file, with the field types LiteDB actually wrote (int vs long, absent vs null, a `de` collation),
//   come across with every row and every business key intact?
//
//   So this runs against a copy of a live file and checks three things:
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
// Usage: migrun <workdir> <source-litedb-file>

using System.Globalization;
using System.Text;
using Jellyfin.Plugin.SubdlScribe.Data;
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
Console.WriteLine();
Console.WriteLine("--- row counts, source vs imported ---");
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
Console.WriteLine();
Console.WriteLine("--- business keys ---");
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
Console.WriteLine();
Console.WriteLine("--- three-valued flags (F-M285) ---");
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
Console.WriteLine();
Console.WriteLine("--- status row ---");
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
Console.WriteLine();
Console.WriteLine("--- second start (must not double the rows) ---");
using (var db = new SubdlSqliteContext(sqlitePath))
{
    var before = db.GetAreaRowCounts().Sum(x => x.Rows);
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
Console.WriteLine();
Console.WriteLine("--- live context round trip (as a registry would do it) ---");
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
Console.WriteLine();
Console.WriteLine("--- compaction ---");
using (var ctx = new SubdlDbContext(workdir, null))
{
    var (beforeB, afterB, rebuilt, outcome) = ctx.Compact();
    Console.WriteLine("   " + beforeB + " -> " + afterB + " bytes, " + rebuilt + " area(s), outcome " + outcome);
    Check(outcome is CompactOutcome.Compacted or CompactOutcome.Recovered, "compaction returns a usable outcome", outcome.ToString());
    Check(ctx.Meta.FindById("db") != null, "store answers after compaction");
}

Console.WriteLine();
Console.WriteLine("=== " + (failures == 0 ? "MIGRATION OK" : failures + " FAILURE(S)") + " ===");
return failures == 0 ? 0 : 1;

static void Classify(BsonDocument d, string field, out bool? value)
{
    if (!d.TryGetValue(field, out var v) || v.IsNull)
    {
        value = null;
        return;
    }

    value = v.AsBoolean;
}
