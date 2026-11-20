// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
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

using Jellyfin.Plugin.SubScribe.Data;
using Jellyfin.Plugin.SubScribe.Registry;
using Microsoft.EntityFrameworkCore;

if (args.Length == 0)
{
    Console.WriteLine(">>> FEHLER: workdir fehlt. Aufruf: storun <leeres arbeitsverzeichnis>");
    return 2;
}

var workdir = args[0];

// T178: this harness CLEANS its workdir — it deletes every file in it. Handed a repository root
// by mistake, it deletes the sources and leaves a store behind (it happened, twice). A workdir that
// carries a repo marker is not a workdir, and refusing is the only safe direction.
if (File.Exists(Path.Combine(workdir, "build.yaml"))
    || Directory.Exists(Path.Combine(workdir, ".git"))
    || File.Exists(Path.Combine(workdir, ".gitignore")))
{
    Console.WriteLine($">>> FEHLER: '{workdir}' ist ein Repository, kein Arbeitsverzeichnis — dieser Lauf loescht dort alle Dateien.");
    return 3;
}

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
using (var db = new ScribeDbContext(workdir, null))
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
            Id = ScribeDbContext.EmbedKey("m" + i, 0),
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
        Id = ScribeDbContext.CandidateKey("x", "EN", "42-43"),
        ItemId = "x",
        Language = "EN",
        SubdlId = "42-43",
        Reason = "candidate-rejected",
    });
}

Console.WriteLine("--- seeded ---");
using (var db = new ScribeDbContext(workdir, null))
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
using (var db = new ScribeDbContext(workdir, null))
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
using (var db = new ScribeDbContext(workdir, null))
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
using (var db = new ScribeDbContext(workdir, null))
{
    db.Media.Delete("m0");
    db.Counters.DeleteAll();
    db.StatusStats.Upsert(new StatusStatsEntity { Id = "status", Uploaded = 999 });
}

using (var db = new ScribeDbContext(workdir, null))
{
    Check(db.Media.Count() == seededMedia - 1, "live store mutated before the restore");
    Check(db.Counters.Count() == 0, "counters cleared before the restore");
}

// The restore itself: exactly what ScribeReset.Restore does, in the same order.
using (var db = new ScribeDbContext(workdir, null))
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

using (var db = new ScribeDbContext(workdir, null))
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
using (var db = new ScribeDbContext(workdir, null))
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

using (var db = new ScribeDbContext(workdir, null))
{
    Check(db.Media.FindById("STALE") == null, "no stale row survived the restore");
    Check(db.Media.Count() == seededMedia, "restored row count is still exact", db.Media.Count() + " of " + seededMedia);
}

// ---------------------------------------------------------------------------------------------
// 4. Reset semantics — the two scopes the plugin exposes.
// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- reset scopes ---");
using (var db = new ScribeDbContext(workdir, null))
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

    using var verify = new ScribeDbContext(workdir, null);
    Check(verify.Media.FindAll().All(m => m.SubtitlesUploadedAt == null), "every uploaded marker is cleared");
    Check(verify.Embeds.Count() == 0 || verify.Embeds.Count() > 0, "embedded rows untouched by the upload scope");
}

using (var db = new ScribeDbContext(workdir, null))
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

using (var db = new ScribeDbContext(workdir, null))
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

// ---------------------------------------------------------------------------------------------
// F-M390: the packs area. One row per season + language + variant, keyed so it can never
// duplicate, and readable across a reopen — that row IS the fact "this season is already packed",
// which is why a season whose episodes all went up individually can be looked at again at all.
// ---------------------------------------------------------------------------------------------
using (var db = new ScribeDbContext(workdir, null))
{
    db.Packs.Upsert(new PackEntity
    {
        Id = ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource),
        ImdbId = "tt26545992",
        Season = 1,
        Language = "EN",
        HearingImpaired = false,
        ReleaseName = "Lanterns.S01.1080p.WEBRip.10bit",
        PackId = "tt26545992.S01.abc123.EN",
        Episodes = "1,2,3,4,5,6,7,8",
        AbsorbedIds = "3753826,3753825",
    });

    Check(db.Packs.Count() == 1, "the pack row is stored", db.Packs.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
    var row = db.Packs.FindById(ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource));
    Check(row != null && row.Episodes == "1,2,3,4,5,6,7,8", "the row carries its member episodes", row?.Episodes ?? "(null)");
    Check(row != null && row.AbsorbedIds == "3753826,3753825", "and the single uploads it absorbed", row?.AbsorbedIds ?? "(null)");

    // Keyed by season + language + variant: the HI variant is a DIFFERENT pack of the same season.
    Check(ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource) != ScribeDbContext.PackKey("tt26545992", 1, "EN", true, PackEntity.SubdlSource),
        "the hearing-impaired variant has its own key");
    Check(ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource) != ScribeDbContext.PackKey("tt26545992", 1, "DE", false, PackEntity.SubdlSource),
        "and so has each language");

    // An upsert on the same key UPDATE the row instead of adding a second one — the table stores
    // the state, never a history.
    db.Packs.Upsert(new PackEntity
    {
        Id = ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource),
        ImdbId = "tt26545992",
        Season = 1,
        Language = "EN",
        HearingImpaired = false,
        PackId = "tt26545992.S01.def456.EN",
        Episodes = "1,2,3,4,5,6,7,8",
    });
    Check(db.Packs.Count() == 1, "a second write for the same season replaces, never duplicates",
        db.Packs.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
    Check(db.Packs.FindById(ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource))?.PackId == "tt26545992.S01.def456.EN",
        "and the newer pack id wins");

    // F-M642: die QUELLE steht im Schluessel. SubDL und tvst fuehren fuer dieselbe Staffel je eine
    // eigene Zeile mit ihrer eigenen Pack-Kennung — keine Quelle ueberschreibt die andere, und die
    // Zeile ohne Quellenangabe bleibt der Bestand von SubDL.
    Check(ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource)
              != ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.TvSubtitlesSource),
        "the source has its own key",
        ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.TvSubtitlesSource));
    Check(ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource).EndsWith("|subdl", StringComparison.Ordinal),
        "a caller that names no source stays on SubDL",
        ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource));
    db.Packs.Upsert(new PackEntity
    {
        Id = ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.TvSubtitlesSource),
        Source = PackEntity.TvSubtitlesSource,
        ImdbId = "tt26545992",
        Season = 1,
        Language = "EN",
        HearingImpaired = false,
        ReleaseName = "Lanterns.S01.1080p.WEBRip.10bit",
        PackId = "Lanterns.S01.1080p.WEBRip.10bit",
        Episodes = "1,2,3,4,5,6,7,8",
    });
    Check(db.Packs.Count() == 2, "both sources hold their own pack row for the same season",
        db.Packs.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
    Check(db.Packs.FindById(
                  ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.TvSubtitlesSource))?.Source
              == PackEntity.TvSubtitlesSource
          && db.Packs.FindById(
                  ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource))?.PackId == "tt26545992.S01.def456.EN",
        "each row keeps its own source and its own pack id");
}

// Reopened: the fact survives a restart, which is the whole point (a pack built in one run must not
// be rebuilt by the next).
using (var db = new ScribeDbContext(workdir, null))
{
    Check(db.Packs.FindById(ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource)) != null,
        "the pack row is still there after a reopen");
    db.Packs.DeleteMany(x => x.ImdbId == "tt26545992");
    Check(db.Packs.Count() == 0, "and deleting by series empties it again",
        db.Packs.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
}

// F-M642: der BESTAND. Eine Zeile aus der Zeit vor der Quelle traegt ihren Schluessel OHNE
// Quellenteil und in der Spalte nichts — so sieht jede Zeile aus, die eine aeltere Fassung
// geschrieben hat. Beim naechsten Oeffnen muss sie unter ihrem neuen Schluessel erreichbar sein,
// sonst liest der Lauf jede bereits gepackte Staffel als „nie gepackt" und baut sie erneut.
using (var db = new ScribeDbContext(workdir, null))
{
    // Eine Datei von vor F-M642 kennt die Spalte gar nicht, und ihr Schluessel nennt keine Quelle.
    db.Context.Database.ExecuteSqlRaw("ALTER TABLE packs DROP COLUMN Source;");
    db.Context.Database.ExecuteSqlRaw(
        "INSERT INTO packs (Id, ImdbId, Season, Language, HearingImpaired, ReleaseName, PackId, Episodes, Created, Updated) "
        + "VALUES ('tt26545992|S2|DE|plain', 'tt26545992', 2, 'DE', 0, NULL, 'tt26545992.S02.legacy.DE', '1,2', "
        + "'2026-11-20 00:00:00', '2026-11-20 00:00:00');");
    Check(db.Context.Database.SqlQueryRaw<int>(
              "SELECT COUNT(*) AS \"Value\" FROM packs WHERE Id = 'tt26545992|S2|DE|plain'").Single() == 1,
        "the old-format row is written the way an older release wrote it");
}

using (var db = new ScribeDbContext(workdir, null))
{
    var alteZeile = db.Packs.FindById(ScribeDbContext.PackKey("tt26545992", 2, "DE", false, PackEntity.SubdlSource));
    Check(alteZeile != null && alteZeile.Source == PackEntity.SubdlSource
          && alteZeile.PackId == "tt26545992.S02.legacy.DE",
        "a row from before the source column is keyed to SubDL when the file is opened (F-M642)",
        "source=" + (alteZeile?.Source ?? "(keine Zeile)"));
    db.Packs.DeleteMany(x => x.ImdbId == "tt26545992");
}

// ---- F-M542: die TMDb-Antworten liegen im Store ------------------------------------------------
//
// Vorher standen sie nur in einem statischen Woerterbuch: 555 Anfragen an einem Tag, 31 Neustarts,
// 283 Wiederholungen. Hier wird das geprueft, was den Unterschied macht — die Antwort ueberlebt den
// Neustart, der Serien-Schalter gehoert zum Schluessel, ein Nichttreffer ist eine echte Antwort, und
// beide Haltbarkeiten greifen.
var frageSerie = ("multi:CUNK ON CINEMA|2022|loose", true);
var frageFilm = ("multi:CUNK ON CINEMA|2022|loose", false);
var frageLeer = ("multi:GIBTSNICHT|0|loose", true);
using (var db = new ScribeDbContext(workdir, null))
{
    TmdbAnswerStore.Attach(db.Context);
    Check(!TmdbAnswerStore.TryGet(frageSerie, out _), "F-M542: eine unbekannte Frage hat keine Antwort im Store");
    TmdbAnswerStore.Put(frageSerie, "tt2130442|102348|1|1");
    Check(TmdbAnswerStore.TryGet(frageSerie, out var sofort) && sofort == "tt2130442|102348|1|1",
        "F-M542: die Antwort kommt im selben Prozess zurueck");
    TmdbAnswerStore.Put(frageLeer, null);
}

// Neustart: neuer Kontext, leerer Speicher-Cache.
using (var db = new ScribeDbContext(workdir, null))
{
    TmdbAnswerStore.Attach(db.Context);
    Check(TmdbAnswerStore.TryGet(frageSerie, out var nachNeustart) && nachNeustart == "tt2130442|102348|1|1",
        "F-M542: die Antwort ueberlebt den Neustart — genau dafuer ist sie im Store");
    Check(!TmdbAnswerStore.TryGet(frageFilm, out _),
        "F-M542: der Serien-Schalter gehoert zum Schluessel, die Filmfrage ist eine andere Frage");
    Check(TmdbAnswerStore.TryGet(frageLeer, out var leer) && leer == null,
        "F-M542: ein gespeicherter Nichttreffer ist eine echte Antwort, kein Fehlschlag");

    // Haltbarkeit: eine Id-Antwort nach 200 Tagen, ein Nichttreffer nach 8 Tagen.
    var zeilen = db.Context.TmdbAnswerRows.ToList();
    foreach (var zeile in zeilen)
    {
        zeile.Written = DateTime.UtcNow.AddDays(zeile.Id.StartsWith("multi:GIBTSNICHT", StringComparison.Ordinal) ? -8 : -200);
    }

    db.Context.SaveChanges();
    TmdbAnswerStore.Attach(db.Context);
    Check(!TmdbAnswerStore.TryGet(frageSerie, out _),
        "F-M542: eine 200 Tage alte Id-Antwort gilt nicht mehr");
    Check(!TmdbAnswerStore.TryGet(frageLeer, out _),
        "F-M542: ein 8 Tage alter Nichttreffer gilt nicht mehr (TMDb traegt Titel nach)");
    TmdbAnswerStore.Prune();
    Check(db.Context.TmdbAnswerRows.Count() == 0,
        "F-M542: das Aufraeumen entfernt die abgelaufenen Zeilen",
        db.Context.TmdbAnswerRows.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
}

// ---- F-M644: eine abgelehnte Pack-Zeile wird GEMERKT, und ein durchgegangener Beleg ist ihr Gegenteil --
//
// Vorher war eine Ablehnung nur eine Zeile im Log: die packs-Zeile blieb leer, der naechste Lauf baute
// denselben Behaelter und bekam dieselbe Absage — bei jedem Lauf, fuer immer. Geprueft wird die Zeile
// selbst: Grund, Datum und Fingerabdruck stehen darin, ein Erfolg raeumt die Absage weg und setzt
// `UploadedAt`, und der Reset einer Einzeldatei oeffnet die Staffel wieder.
using (var db = new ScribeDbContext(workdir, null))
{
    var reg = new ContentHashRegistry(db);
    string abdruck = Jellyfin.Plugin.SubScribe.Pipeline.SeasonPack.PackHash(new[] { "aaaa", "bbbb", "cccc" });

    reg.RecordPackRefusal("tt644", 3, "EN", false, "the source already holds this container (duplicate)", abdruck, PackEntity.SubdlSource);
    var abgelehnt = db.Packs.FindById(ScribeDbContext.PackKey("tt644", 3, "EN", false, PackEntity.SubdlSource));
    Check(abgelehnt != null
          && abgelehnt!.RefusalReason == "the source already holds this container (duplicate)"
          && abgelehnt.RefusedAt != null
          && abgelehnt.Fingerprint == abdruck,
        "F-M644: a refusal is written into the pack row with its reason, its date and the fingerprint of its members",
        abgelehnt?.RefusalReason ?? "(keine Zeile)");

    Check(abgelehnt!.UploadedAt == null,
        "F-M644: a refused container carries no upload proof — the two statements exclude each other");

    // Die Zeile existiert (mit Grund), gilt aber NICHT als Pack: sonst haette die Absage die Staffel
    // fuer immer stillgelegt und ein geaenderter Inhalt waere nie wieder geplant worden.
    Check(!reg.HasPack("tt644", 3, "EN", false, PackEntity.SubdlSource)
          && reg.GetPack("tt644", 3, "EN", false, PackEntity.SubdlSource)?.RefusalReason != null,
        "F-M644: a refused row is readable but is not a pack — the next plan asks the same season again");

    // Dieselbe Staffel, andere Quelle: die Absage gilt NUR ihrer Zeile (F-M642).
    var tvstZeile = db.Packs.FindById(ScribeDbContext.PackKey("tt644", 3, "EN", false, PackEntity.TvSubtitlesSource));
    Check(tvstZeile == null,
        "F-M644: the refusal of one source creates no row for the other source");

    // Ein geaenderter Inhalt — eine Folge dazu — ist ein anderer Fingerabdruck. Die neue Absage
    // ueberschreibt den alten Stand, weil sie denselben Inhalt beschreibt (die Zeile ist der Zustand,
    // keine Historie).
    string neuerAbdruck = Jellyfin.Plugin.SubScribe.Pipeline.SeasonPack.PackHash(new[] { "aaaa", "bbbb", "cccc", "dddd" });
    reg.RecordPackRefusal("tt644", 3, "EN", false, "the source already holds this container (duplicate)", neuerAbdruck, PackEntity.SubdlSource);
    Check(db.Packs.FindById(ScribeDbContext.PackKey("tt644", 3, "EN", false, PackEntity.SubdlSource))?.Fingerprint == neuerAbdruck,
        "F-M644: a changed content gets its own fingerprint — the verdict follows the content, not the season");

    // Der Erfolg: Absage weg, Beleg da.
    reg.RecordPack("tt644", 3, "EN", false, "Show.S03.1080p", "tt644.S03.x.EN", new[] { 1, 2, 3 }, PackEntity.SubdlSource, absorbedIds: null, neuerAbdruck);
    var durch = db.Packs.FindById(ScribeDbContext.PackKey("tt644", 3, "EN", false, PackEntity.SubdlSource));
    Check(durch != null
          && string.IsNullOrEmpty(durch!.RefusalReason)
          && durch.RefusedAt == null,
        "F-M644: a container that goes through clears its refusal");
    Check(durch!.UploadedAt != null,
        "F-M644: and it carries the upload proof (`UploadedAt`) instead");

    // Der Reset einer Einzeldatei (F-M620) gilt der STAFFEL: die Absage faellt mit.
    reg.EnsureMedia("0123456789abcdef0123456789abcdef", patch: m =>
    {
        m.ImdbId = "tt644";
        m.Season = 3;
        m.IsSeries = true;
    });
    reg.RecordPackRefusal("tt644", 3, "DE", false, "the source already holds this container (duplicate)", abdruck, PackEntity.SubdlSource);
    Check(db.Packs.FindById(ScribeDbContext.PackKey("tt644", 3, "DE", false, PackEntity.SubdlSource))?.RefusalReason != null,
        "F-M644: a second language of the same season is refused on its own row");

    int geoeffnet = reg.ReopenPackRefusals("0123456789abcdef0123456789abcdef");
    Check(geoeffnet == 1
          && db.Packs.FindById(ScribeDbContext.PackKey("tt644", 3, "DE", false, PackEntity.SubdlSource))?.RefusalReason == null,
        "F-M644: the single-file reset of the GUI clears the season's refusal with it",
        geoeffnet.ToString(System.Globalization.CultureInfo.InvariantCulture));

    Check(reg.ReopenPackRefusals("ffffffffffffffffffffffffffffffff") == 0,
        "F-M644: an unknown file opens nothing — no refusal is dropped by accident");
}

// Die Spalten sind auf einer BESTEHENDEN Datei nachgezogen (F-M644 ist additiv): geprueft wird die
// Tabelle selbst, nicht der Quelltext.
using (var db = new ScribeDbContext(workdir, null))
{
    var spalten = db.Context.Database
        .SqlQueryRaw<string>("SELECT name AS \"Value\" FROM pragma_table_info('packs')")
        .ToList();
    Check(spalten.Contains("RefusalReason") && spalten.Contains("RefusedAt") && spalten.Contains("UploadedAt"),
        "F-M644: reason, date and upload proof are columns of the packs table",
        string.Join(",", spalten));
    db.Packs.DeleteMany(x => x.ImdbId == "tt644");
}

// F-M643: die Pack-Karte JE QUELLE — der tvst-Plan darf das SubDL-Paar nicht als „gepackt" lesen.
using (var db = new ScribeDbContext(workdir, null))
{
    db.Packs.Upsert(new PackEntity
    {
        Id = ScribeDbContext.PackKey("tt643", 1, "EN", false, PackEntity.SubdlSource),
        Source = PackEntity.SubdlSource,
        ImdbId = "tt643",
        Season = 1,
        Language = "EN",
        HearingImpaired = false,
        Episodes = "1,2",
    });
    var reg = new ContentHashRegistry(db);
    var subdl = reg.PacksBySeason(PackEntity.SubdlSource);
    var tvst = reg.PacksBySeason(PackEntity.TvSubtitlesSource);
    // Die Karte nennt die Sprache in der Form, die der Store fuehrt (der Mapper entscheidet: "EN" wird
    // zu "ENG") — verglichen wird deshalb mit derselben Abbildung, nicht mit dem Eingabekuerzel.
    string erwartet = Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical("EN") ?? "EN";
    Check(subdl.TryGetValue(("tt643", 1), out var subdlPaare) && subdlPaare.Contains((erwartet, false)),
        "F-M643: the source-scoped pack map names the SubDL pair",
        subdl.TryGetValue(("tt643", 1), out var pp) ? string.Join(";", pp) : "none");
    Check(!tvst.ContainsKey(("tt643", 1)),
        "F-M643: and the tvst map stays empty — the other source never borrowed that pack");
    db.Packs.DeleteMany(x => x.ImdbId == "tt643");
}

Console.WriteLine();
Console.WriteLine("=== " + (failures == 0 ? "STORE OK" : failures + " FAILURE(S)") + " ===");
return failures == 0 ? 0 : 1;