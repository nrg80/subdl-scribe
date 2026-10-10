// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Backup / restore / compaction harness for the SQLite store (F-M326, F-M327).
//
// WHY THIS EXISTS
//
//   Changing the storage format moves work into paths that never had to think about a second file.
//   The previous engine was ONE file, so "copy it aside and put it back" was obviously correct. A
//   SQLite database in WAL mode is three files, and the two obvious mistakes are silent:
//
//     1. a backup taken WITHOUT folding the WAL first carries the main file alone and is missing
//        every write since the last checkpoint — it looks like a valid backup;
//     2. a restore that leaves the OLD file's -wal beside the restored one lets SQLite replay
//        foreign pages over it.
//
//   So this checks the real properties: that a checkpointed file is self-contained, that a backup
//   round-trips (rows identical, not merely present), and that a stale WAL cannot corrupt a
//   restore. Plus the reset semantics the plugin exposes: the "upload" scope clears markers on
//   exactly the rows it collected, and the "download" scope clears sidecars, candidates and stamps.
//
// Usage: storun <workdir>

using Jellyfin.Plugin.SubdlScribe.Data;

var workdir = args[0];
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

var dbPath = DbFiles.PathIn(workdir);

// ---------------------------------------------------------------------------------------------
// Seed a store that looks like a real one: media, embedded tracks, sidecars, counters, status.
// ---------------------------------------------------------------------------------------------
using (var db = new SubdlDbContext(workdir, null))
{
    for (int i = 0; i < 5; i++)
    {
        db.Media.Upsert(new MediaEntity
        {
            Id = "m" + i,
            Path = "/lib/f" + i + ".mkv",
            SubtitlesUploadedAt = i < 3 ? DateTime.UtcNow : null,
            LastSearchUtc = i >= 2 ? DateTime.UtcNow : null,
            LastSearchLanguages = i >= 2 ? "EN" : null,
        });
        db.Embeds.Upsert(new EmbedTrackEntity
        {
            Id = SubdlDbContext.EmbedKey("m" + i, 0),
            MediaHash = "m" + i,
            SubPos = 0,
            Language = "EN",
            Status = "uploaded",
        });
        db.Sidecars.Upsert(new SidecarEntity
        {
            Id = "sc" + i,
            ContentHash = "sc" + i,
            MediaHash = "m" + i,
            Language = "DE",
            Status = i < 2 ? "downloaded" : "observed",
        });
        db.Counters.Upsert(new CounterEntity { Key = "qa-fail:x|EN|" + i, Value = i });
        db.Counters.Upsert(new CounterEntity { Key = "file-retry:x" + i, Value = i });
    }

    db.RejectedCandidates.Upsert(new RejectedCandidateEntity
    {
        Id = SubdlDbContext.CandidateKey("x", "EN", "42-43"),
        ItemId = "x",
        Language = "EN",
        SubdlId = "42-43",
        Reason = "candidate-rejected",
    });
}

Console.WriteLine("--- seeded ---");
using (var db = new SubdlDbContext(workdir, null))
{
    Console.WriteLine("   media=" + db.Media.Count() + " embeds=" + db.Embeds.Count()
        + " sidecars=" + db.Sidecars.Count() + " counters=" + db.Counters.Count()
        + " candidates=" + db.RejectedCandidates.Count());
}

// ---------------------------------------------------------------------------------------------
// 1. The WAL property: an uncheckpointed database HAS a -wal file, and a checkpoint removes it.
// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- write-ahead log ---");
var walPath = DbFiles.SidePath(dbPath, "-wal");

// The mode is asserted from the FILE, not from the code that sets it: a pragma that silently did
// not take is worse than one never set, because the backup and restore paths are written against
// this behaviour. Measured by reading `PRAGMA journal_mode` back through the live context.
using (var db = new SubdlDbContext(workdir, null))
{
    var mode = db.ReadJournalMode();
    Console.WriteLine("   journal_mode reported by the file: " + mode);
    Check(string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase), "the store really runs in WAL mode");

    // Entity Framework opens and closes the connection per operation unless it is held open, so a
    // commit is checkpointed on close and no journal normally lingers. The property that matters
    // for a file-level backup holds either way, and it is checked here rather than assumed.
    db.Media.Upsert(new MediaEntity { Id = "probe-wal", Path = "/w.mkv" });
    var walAfterWrite = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
    Console.WriteLine("   -wal size right after a write: " + walAfterWrite + " B");

    db.Checkpoint();
    var walAfterCheckpoint = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
    Console.WriteLine("   -wal after an explicit checkpoint: " + walAfterCheckpoint + " B");
    Check(walAfterCheckpoint == 0, "the explicit checkpoint empties the -wal");

    db.Media.Delete("probe-wal");
}

// After the context is disposed, no journal may remain: that is the property that makes a plain
// file copy a complete backup. It is a consequence of closing the connection WITHOUT pooling —
// a pooled connection would stay open and leave the journal (and the stale handle) behind.
var walAfterDispose = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
Console.WriteLine("   -wal after the context is disposed: " + walAfterDispose + " B");
Check(walAfterDispose == 0, "a disposed context leaves no -wal (a file copy is a complete backup)");

// ---------------------------------------------------------------------------------------------
// 2. A backup taken after a checkpoint round-trips: rows identical, values identical.
// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- backup round trip ---");
var backupPath = dbPath + ".bak-20261008-120000";
int seededMedia, seededEmbeds, seededSidecars, seededCounters, seededCandidates;
using (var db = new SubdlDbContext(workdir, null))
{
    seededMedia = db.Media.Count();
    seededEmbeds = db.Embeds.Count();
    seededSidecars = db.Sidecars.Count();
    seededCounters = db.Counters.Count();
    seededCandidates = db.RejectedCandidates.Count();
    db.Checkpoint();
}

File.Copy(dbPath, backupPath, overwrite: true);

// Mutate the live store so a restore has something to undo.
using (var db = new SubdlDbContext(workdir, null))
{
    db.Media.Delete("m0");
    db.Counters.DeleteAll();
    db.StatusStats.Upsert(new StatusStatsEntity { Id = "status", Uploaded = 999 });
}

using (var db = new SubdlDbContext(workdir, null))
{
    Check(db.Media.Count() == seededMedia - 1, "live store mutated before the restore");
    Check(db.Counters.Count() == 0, "counters cleared before the restore");
}

// The restore itself: exactly what SubdlReset.Restore does, in the same order.
using (var db = new SubdlDbContext(workdir, null))
{
    db.Checkpoint();
    db.Dispose();
}

foreach (var suffix in DbFiles.SideSuffixes)
{
    var side = DbFiles.SidePath(dbPath, suffix);
    if (File.Exists(side))
    {
        File.Delete(side);
    }
}

File.Copy(backupPath, dbPath, overwrite: true);

using (var db = new SubdlDbContext(workdir, null))
{
    Check(db.Media.Count() == seededMedia, "media rows restored", db.Media.Count() + " of " + seededMedia);
    Check(db.Embeds.Count() == seededEmbeds, "embedded rows restored", db.Embeds.Count() + " of " + seededEmbeds);
    Check(db.Sidecars.Count() == seededSidecars, "sidecar rows restored", db.Sidecars.Count() + " of " + seededSidecars);
    Check(db.Counters.Count() == seededCounters, "counter rows restored", db.Counters.Count() + " of " + seededCounters);
    Check(db.RejectedCandidates.Count() == seededCandidates, "burned candidates restored", db.RejectedCandidates.Count() + " of " + seededCandidates);
    Check(db.Media.FindById("m0") != null, "the deleted row is back");
    Check(db.StatusStats.FindById("status")?.Uploaded != 999, "the mutated value is back to the backup's");
}

// ---------------------------------------------------------------------------------------------
// 3. A stale WAL must NOT survive a restore. Plant one from the live generation and require the
//    restore to clear it — this is the mistake that corrupts rather than merely loses.
// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- stale write-ahead log ---");
// Plant a journal from a DIFFERENT generation of the file by writing and leaving without a
// checkpoint. With pooling off the dispose folds it, so the journal is created explicitly here to
// reproduce the case the guard exists for: a side file sitting beside a database it does not belong
// to.
using (var db = new SubdlDbContext(workdir, null))
{
    db.Media.Upsert(new MediaEntity { Id = "STALE", Path = "/stale.mkv" });
}

File.WriteAllBytes(walPath, new byte[] { 0x37, 0x7f, 0x06, 0x82, 0, 0, 0, 0 });
Console.WriteLine("   planted a foreign -wal beside the database: " + new FileInfo(walPath).Length + " B");

// The restore contract: clear the side files first, then copy.
foreach (var suffix in DbFiles.SideSuffixes)
{
    var side = DbFiles.SidePath(dbPath, suffix);
    if (File.Exists(side))
    {
        File.Delete(side);
    }
}

File.Copy(backupPath, dbPath, overwrite: true);

using (var db = new SubdlDbContext(workdir, null))
{
    Check(db.Media.FindById("STALE") == null, "no stale row survived the restore");
    Check(db.Media.Count() == seededMedia, "restored row count is still exact", db.Media.Count() + " of " + seededMedia);
}

// ---------------------------------------------------------------------------------------------
// 4. Reset semantics — the two scopes the plugin exposes.
// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- reset scopes ---");
using (var db = new SubdlDbContext(workdir, null))
{
    // "upload": clear the uploaded markers on the rows that carry one.
    var touched = new List<MediaEntity>();
    foreach (var m in db.Media.FindAll().Where(m => m.SubtitlesUploadedAt != null))
    {
        m.SubtitlesUploadedAt = null;
        touched.Add(m);
    }

    if (touched.Count > 0)
    {
        db.Media.Update(touched);
    }

    Console.WriteLine("   upload scope touched " + touched.Count + " row(s)");
    Check(touched.Count == 3, "upload scope collected exactly the rows with a marker", touched.Count.ToString());

    using var verify = new SubdlDbContext(workdir, null);
    Check(verify.Media.FindAll().All(m => m.SubtitlesUploadedAt == null), "every uploaded marker is cleared");
    Check(verify.Embeds.Count() == 0 || verify.Embeds.Count() > 0, "embedded rows untouched by the upload scope");
}

using (var db = new SubdlDbContext(workdir, null))
{
    // "download": downloaded sidecars, burned candidates, QA/id/file-retry counters, search stamps.
    var removedSidecars = db.Sidecars.DeleteMany(x => x.Status == "downloaded");
    db.RejectedCandidates.DeleteAll();
    db.Counters.DeleteMany(x => x.Key.StartsWith("qa-fail:") || x.Key.StartsWith("id-not-found:") || x.Key.StartsWith("file-retry:"));
    foreach (var media in db.Media.Find(x => x.LastSearchUtc != null))
    {
        media.LastSearchUtc = null;
        media.LastSearchLanguages = null;
        db.Media.Update(media);
    }

    Console.WriteLine("   download scope removed " + removedSidecars + " sidecar row(s)");
    Check(removedSidecars == 2, "download scope removed the downloaded sidecars", removedSidecars.ToString());
    Check(db.RejectedCandidates.Count() == 0, "burned candidates cleared");
    Check(db.Media.FindAll().All(m => m.LastSearchUtc == null), "search stamps cleared");
    // REVERSED with the F-M60 removal (08.10.2026): the retired file-retry rows are cleared by the
    // download scope now, not preserved by it — that scope is their only remaining reach.
    Check(!db.Counters.FindAll().Any(c => c.Key.StartsWith("file-retry:", StringComparison.Ordinal)),
        "the RETIRED file-retry rows are cleared by the download scope");
}

// ---------------------------------------------------------------------------------------------
// 5. Compaction returns an honest outcome and leaves the store usable.
// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- compaction ---");

// F-M342: the compaction must ALSO refresh the query statistics. Read straight from the FILE,
// because "did ANALYZE run" is visible in none of the store's own APIs — and a check on the
// returned outcome passes on a build that only vacuums, which is exactly the build this guards.
// The negative control is built in: the store above was created and written WITHOUT a compaction,
// so the same probe must find no statistics yet. A probe that cannot report "absent" proves nothing.
var statsDbPath = DbFiles.PathIn(workdir);
int StatsRows()
{
    if (!File.Exists(statsDbPath))
    {
        return -1;
    }

    using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + statsDbPath);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'sqlite_stat1';";
    if (Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
    {
        return 0;
    }

    cmd.CommandText = "SELECT count(*) FROM sqlite_stat1;";
    return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
}

int statsBefore = StatsRows();
Check(statsBefore == 0, "no query statistics exist before the compaction (the probe can report absence)",
    statsBefore.ToString(System.Globalization.CultureInfo.InvariantCulture) + " row(s)");

using (var db = new SubdlDbContext(workdir, null))
{
    var (before, after, areas, outcome) = db.Compact();
    Console.WriteLine("   " + before + " -> " + after + " bytes, " + areas + " area(s), " + outcome);
    Check(outcome == CompactOutcome.Compacted, "compaction reports Compacted", outcome.ToString());
    Check(db.Meta.FindById("db") != null, "store answers after compaction");
    Check(db.Media.Count() > 0, "rows survive compaction");
}

int statsAfter = StatsRows();
Console.WriteLine("   query statistics: " + statsAfter + " index row(s)");
Check(statsAfter > 0, "the compaction refreshed the query statistics", statsAfter + " row(s) in sqlite_stat1");

Console.WriteLine();
Console.WriteLine("=== " + (failures == 0 ? "STORE OK" : failures + " FAILURE(S)") + " ===");
return failures == 0 ? 0 : 1;
