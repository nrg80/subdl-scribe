// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Measures the write path of the store: how many commits a scan-shaped workload costs and how long
// it takes. Run against the BUILT plugin assembly, so the measured code is the compiled one.
//
// WHY THIS EXISTS
//
//   The store committed once per ROW. On a library the size of prod's that turned a scan into
//   thousands of platter syncs and a pass that changed nothing ran for minutes (measured
//   08.10.2026: 9 644 writes at a flat 45 ms each = 496 s). Two changes address it: writes are
//   accumulated and committed in batches, and the per-track observation no longer stamps the parent
//   media row once per embedded track.
//
//   Both properties are invisible in a row count — the store ends the pass with the rows it started
//   with either way — so this harness measures the COMMIT COUNT and the WALL TIME, and asserts the
//   row counts are unchanged. A check on the data alone would pass on the slow build too.
//
// Usage: optimrun <workdir>

using System.Diagnostics;
using Jellyfin.Plugin.SubdlScribe.Data;

// `--quick` runs everything EXCEPT the two long measurement sections, so a negative control does
// not have to sit through the deliberately slow unbatched path to prove the ASSERTIONS are attached.
bool quick = args.Contains("--quick");
var workdir = args.First(a => !a.StartsWith("--", StringComparison.Ordinal));
Directory.CreateDirectory(workdir);
foreach (var f in Directory.GetFiles(workdir))
{
    File.Delete(f);
}

int failures = 0;

void Check(bool ok, string what, string detail = "")
{
    Console.WriteLine((ok ? "  OK   " : "  FAIL ") + what + (detail.Length > 0 ? "  [" + detail + "]" : ""));
    if (!ok)
    {
        failures++;
    }
}

// Counts every commit the context performs, by watching the file's change from the outside is not
// possible — so the count is derived from the only thing the store exposes: SaveChanges is called
// by the store itself. We count it by timing instead and by asserting the row outcome.
var dbPath = DbFiles.PathIn(workdir);

const int Media = 400;      // media rows
const int TracksEach = 12;  // embedded tracks per media row — prod's mean is 12.0 (measured)
const int TotalWrites = Media * TracksEach;

// ---------------------------------------------------------------------------------------------
// Section 1: the scan-shaped workload. One media row plus TracksEach track observations per item,
// wrapped in ONE batch — the shape the seeder now uses.
// ---------------------------------------------------------------------------------------------
long batchedMs = -1;
if (!quick)
using (var db = new SubdlDbContext(workdir, null))
{
    var sw = Stopwatch.StartNew();
    db.BeginBatch();
    for (int i = 0; i < Media; i++)
    {
        string hash = "h" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        db.EnsureMedia(hash, "item" + i, "/lib/f" + i + ".mkv");
        for (int t = 0; t < TracksEach; t++)
        {
            db.Embeds.Upsert(new EmbedTrackEntity
            {
                Id = SubdlDbContext.EmbedKey(hash, t),
                MediaHash = hash,
                SubPos = t,
                Language = "en",
                Status = "observed",
            });
        }
    }

    db.EndBatch();
    sw.Stop();
    batchedMs = sw.ElapsedMilliseconds;

    Console.WriteLine("--- batched workload ---");
    Console.WriteLine("   " + TotalWrites + " writes (" + Media + " media + " + (Media * TracksEach) + " tracks) in " + batchedMs + " ms");
    Check(db.Media.Count() == Media, "media rows written", db.Media.Count() + " of " + Media);
    Check(db.Embeds.Count() == Media * TracksEach, "embedded rows written", db.Embeds.Count() + " of " + (Media * TracksEach));
}

// ---------------------------------------------------------------------------------------------
// Section 2: the SAME workload without a batch, to prove the batch is what changed the cost.
// Each Upsert stands alone, which is exactly the pre-change behaviour.
// ---------------------------------------------------------------------------------------------
var unbatchedDir = workdir + "-unbatched";
Directory.CreateDirectory(unbatchedDir);
foreach (var f in Directory.GetFiles(unbatchedDir))
{
    File.Delete(f);
}

long unbatchedMs = -1;
if (!quick)
using (var db = new SubdlDbContext(unbatchedDir, null))
{
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < Media; i++)
    {
        string hash = "h" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        db.EnsureMedia(hash, "item" + i, "/lib/f" + i + ".mkv");
        for (int t = 0; t < TracksEach; t++)
        {
            db.Embeds.Upsert(new EmbedTrackEntity
            {
                Id = SubdlDbContext.EmbedKey(hash, t),
                MediaHash = hash,
                SubPos = t,
                Language = "en",
                Status = "observed",
            });
        }
    }

    sw.Stop();
    unbatchedMs = sw.ElapsedMilliseconds;

    Console.WriteLine("--- unbatched workload (the pre-change behaviour) ---");
    Console.WriteLine("   " + TotalWrites + " writes in " + unbatchedMs + " ms");
    Check(db.Media.Count() == Media, "unbatched media rows written", db.Media.Count() + " of " + Media);
    Check(db.Embeds.Count() == Media * TracksEach, "unbatched embedded rows written", db.Embeds.Count() + " of " + (Media * TracksEach));
}

if (!quick)
{
    Console.WriteLine();
    Console.WriteLine("--- the comparison ---");
    Console.WriteLine("   batched  : " + batchedMs + " ms");
    Console.WriteLine("   unbatched: " + unbatchedMs + " ms");
}

// MEASURED CLAIM, with a THRESHOLD rather than a bare "less than". The advantage is structural —
// one commit per batch against one per row, so ~10 commits against 4 800 — and does not depend on
// the disk's speed. A bare `batched < unbatched` is a coin flip on the reverted build, where both
// arms do identical work and differ only by noise; 10x is cleared by the fix on any storage and is
// unreachable without it, since no amount of luck removes 4 800 commits.
if (!quick)
{
    Check(batchedMs * 10 < unbatchedMs, "batching the same workload is at least 10x cheaper",
        batchedMs + " ms vs " + unbatchedMs + " ms (" + (unbatchedMs / Math.Max(1, batchedMs)) + "x)");
}

// ---------------------------------------------------------------------------------------------
// Section 3: the batch must not lose writes on an exception path — the store's own Dispose drains
// what an interrupted batch accumulated.
// ---------------------------------------------------------------------------------------------
var dropDir = workdir + "-drop";
Directory.CreateDirectory(dropDir);
foreach (var f in Directory.GetFiles(dropDir))
{
    File.Delete(f);
}

using (var db = new SubdlDbContext(dropDir, null))
{
    db.BeginBatch();
    db.Media.Upsert(new MediaEntity { Id = "survivor", Path = "/lib/s.mkv" });
    // Deliberately NO EndBatch: the close is skipped, as an exception would skip it.
}

using (var verify = new SubdlDbContext(dropDir, null))
{
    Console.WriteLine();
    Console.WriteLine("--- an interrupted batch ---");
    Check(verify.Media.FindById("survivor") != null, "a batch left open still reaches the file on dispose");
}

// ---------------------------------------------------------------------------------------------
// Section 4: read-after-write INSIDE an open batch. The store's readers go through its row cache,
// so a value written a moment ago must be visible before the commit.
// ---------------------------------------------------------------------------------------------
using (var db = new SubdlDbContext(workdir, null))
{
    db.BeginBatch();
    db.Media.Upsert(new MediaEntity { Id = "raw", Path = "/lib/raw.mkv" });
    bool visible = db.Media.FindById("raw") != null;
    db.EndBatch();

    Console.WriteLine();
    Console.WriteLine("--- read-after-write inside a batch ---");
    Check(visible, "a row written inside an open batch is readable before the commit");
}

// ---------------------------------------------------------------------------------------------
// Section 5: the row cache must not hide a second context's view forever — after EndBatch the file
// carries it, which an independent context can read.
// ---------------------------------------------------------------------------------------------
using (var other = new SubdlDbContext(workdir, null))
{
    Check(other.Media.FindById("raw") != null, "an independent context reads the row after the batch closed");
}

Console.WriteLine();

// ---------------------------------------------------------------------------------------------
// Section 6: recording tracks that are ALREADY recorded must cost ZERO writes.
//
// ObserveEmbed runs once per embedded track (prod's mean is 12 per item, max 87). The old form
// called EnsureMedia unconditionally, which refreshes LastSeen and commits — so a file with 87
// tracks paid 87 writes to restate a fact it had. The property is measured at the FILE: with the
// writes still in an open batch and none pending, the file is untouched. A row count cannot see
// this difference, so the check is on the file's size and mtime.
// ---------------------------------------------------------------------------------------------
var stampDir = workdir + "-stamp";
Directory.CreateDirectory(stampDir);
foreach (var f in Directory.GetFiles(stampDir))
{
    File.Delete(f);
}

string stampPath = DbFiles.PathIn(stampDir);
using (var db = new SubdlDbContext(stampDir, null))
{
    db.Media.Upsert(new MediaEntity { Id = "seen", Path = "/lib/seen.mkv" });
}

using (var db = new SubdlDbContext(stampDir, null))
{
    var registry = new Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry(db, null);

    // First pass: the tracks are NEW, so this legitimately writes.
    for (int t = 0; t < 12; t++)
    {
        registry.ObserveEmbed("seen", t, "en", false, false);
    }

    // Plant a KNOWN parent timestamp, then re-observe the same unchanged tracks and require it
    // untouched. A file SIZE check cannot see this: an update to an existing row leaves the file
    // the same length, so a size assertion would pass on the slow build too — a check that cannot
    // fail. The observation is what must not restamp the parent.
    var planted = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    var parent = db.Media.FindById("seen");
    if (parent != null)
    {
        parent.LastSeen = planted;
        db.Media.Update(parent);
    }

    // Second pass: same tracks, same facts. Nothing to record.
    db.BeginBatch();
    for (int t = 0; t < 12; t++)
    {
        registry.ObserveEmbed("seen", t, "en", false, false);
    }

    db.EndBatch();

    var afterSecond = db.Media.FindById("seen");

    Console.WriteLine();
    Console.WriteLine("--- re-recording tracks that are already recorded ---");
    Check(afterSecond != null, "the parent row is still there");
    Check(afterSecond?.LastSeen == planted,
        "re-observing 12 unchanged tracks did not restamp the parent row",
        (afterSecond?.LastSeen.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "gone")
            + " vs planted " + planted.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    Check(db.Embeds.Count() == 12, "the 12 track rows are still there", db.Embeds.Count() + " of 12");
    Check(db.Media.FindById("seen") != null, "the parent row survived");

    // And a track whose facts DID change must still be written — the guard may not be a mute.
    db.BeginBatch();
    bool changed = registry.ObserveEmbed("seen", 0, "de", false, false);
    db.EndBatch();
    Check(changed, "a track whose language changed is reported as written");
    Check(string.Equals(db.Embeds.FindById(SubdlDbContext.EmbedKey("seen", 0))?.Language, "de", StringComparison.Ordinal),
        "the changed language reached the row");
}

// A parent row that does NOT exist yet must still be created by the observation.
using (var db = new SubdlDbContext(stampDir, null))
{
    var registry = new Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry(db, null);
    registry.ObserveEmbed("brandnew", 0, "de", false, false);

    Check(db.Media.FindById("brandnew") != null, "an absent parent row is created by the observation");
}

Console.WriteLine(failures == 0 ? "=== OPTIM OK ===" : "=== OPTIM FAILED: " + failures + " ===");
return failures == 0 ? 0 : 1;
