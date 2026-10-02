// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LiteDB;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

namespace Jellyfin.Plugin.SubdlScribe.Data;

/// <summary>
/// F-M236: what a compaction attempt left behind. Three states, because two of them need different
/// log lines and only one of them means the file actually got smaller.
/// </summary>
/// F-M214: compacted / recovered / broken, so a failed rebuild is visible instead of silent.
public enum CompactOutcome
{
    /// <summary>The rebuild finished; the file was released and shrunk.</summary>
    Compacted,

    /// <summary>
    /// The rebuild failed, but the file was rebuilt from its own rows into a fresh one (F-M237) —
    /// usable AND smaller. Distinct from <see cref="Compacted"/>: the rows were rewritten, not the
    /// pages merely released.
    /// </summary>
    Repaired,

    /// <summary>The rebuild failed, but the database was reopened — usable, NOT shrunk.</summary>
    Recovered,

    /// <summary>The rebuild failed and the database could not be reopened — restart required.</summary>
    Broken
}

/// <summary>
/// Central LiteDB context for SubDL Scribe. One data file, five clearly separated areas:
/// <list type="number">
/// <item><description>0 — file compatibility (schema/plugin/Jellyfin version)</description></item>
/// <item><description>1 — OSHash cache (path → file hash, disposable)</description></item>
/// <item><description>2 — video files, each with its embedded subtitle tracks beneath it</description></item>
/// <item><description>3 — sidecar .srt files, keyed by content</description></item>
/// <item><description>4 — burned download candidates</description></item>
/// <item><description>5 — cumulative GUI counters (one row)</description></item>
/// </list>
/// <para>
/// Every area is keyed by a business key carried in the document's own Id field, never by the
/// database-assigned auto id. That is the correction of F-M194: with an auto id, an upsert of a
/// "new" row can never find the row it means to update, which is what silently produced
/// duplicates. It cannot happen now because the key is computed, not assigned.
/// </para>
/// </summary>
public sealed class SubdlDbContext : IDisposable
{
    /// <summary>
    /// Structure version of the data file. Increase whenever the stored shape changes in a way an
    /// older build could not survive.
    /// </summary>
    // F-M195: the schema marker the compatibility row carries.
    // F-M282/F-M283/F-M284 (02.10.2026): raised to 2. The stored shape changed — a subtitle row now
    // carries `Forced` beside `HearingImpaired`, and the media record no longer carries the download
    // mark (`SubtitlesDownloadedAt` / `SubtitlesDownloadedLanguages`). Per F-M195b there is no
    // migration: the marker makes the change visible, the older records are ignored rather than
    // rewritten, and a reset (F-M90) is the intended path.
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// The open LiteDB engine. Deliberately NOT readonly: LiteDB's <c>Rebuild()</c> closes the
    /// engine and only reopens it on success, so a failed rebuild leaves it dead — see
    /// <see cref="VerifyAndRepair"/>, which replaces this instance to bring the store back (F-M236).
    /// </summary>
    private LiteDatabase _db;
    private readonly string _dbPath;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubdlDbContext"/> class.
    /// </summary>
    /// <param name="dataDir">Plugin data directory.</param>
    /// <param name="logger">Optional logger.</param>
    public SubdlDbContext(string dataDir, ILogger? logger = null)
    {
        _logger = logger;
        Directory.CreateDirectory(dataDir);
        var path = DbFiles.PathIn(dataDir);
        _dbPath = path;
        _db = new LiteDatabase(path);
        EnsureIndexes();
        CheckSchemaVersion();
    }

    /// <summary>Area 0: the single compatibility record describing this data file.</summary>
    public ILiteCollection<MetaEntity> Meta => _db.GetCollection<MetaEntity>("meta");

    /// <summary>Area 1: OSHash cache, keyed by media path.</summary>
    public ILiteCollection<OshashEntity> Oshashes => _db.GetCollection<OshashEntity>("oshashes");

    /// <summary>Area 2: one record per video file, keyed by media hash.</summary>
    public ILiteCollection<MediaEntity> Media => _db.GetCollection<MediaEntity>("media");

    /// <summary>Area 2, beneath the file: embedded subtitle tracks, keyed by "&lt;media hash&gt;|&lt;position&gt;".</summary>
    public ILiteCollection<EmbedTrackEntity> Embeds => _db.GetCollection<EmbedTrackEntity>("embeds");

    /// <summary>Area 3: sidecar subtitle files, keyed by content hash.</summary>
    public ILiteCollection<SidecarEntity> Sidecars => _db.GetCollection<SidecarEntity>("sidecars");

    /// <summary>Area 4: download candidates that were fetched and discarded.</summary>
    public ILiteCollection<RejectedCandidateEntity> RejectedCandidates => _db.GetCollection<RejectedCandidateEntity>("rejected_candidates");

    /// <summary>Retry and fail-budget counters.</summary>
    public ILiteCollection<CounterEntity> Counters => _db.GetCollection<CounterEntity>("counters");

    /// <summary>Pipeline run bookkeeping.</summary>
    public ILiteCollection<PipelineRunEntity> Runs => _db.GetCollection<PipelineRunEntity>("runs");

    /// <summary>
    /// Cumulative subtitle counters for the GUI (single row, key "status"). Stored here rather than
    /// in the configuration XML so a database reset resets them too — see <see cref="StatusStatsEntity"/>.
    /// </summary>
    public ILiteCollection<StatusStatsEntity> StatusStats => _db.GetCollection<StatusStatsEntity>("status_stats");

    /// <summary>
    /// Area 6: the LAST run of each worker, keyed by the worker's task key (one row per worker, never
    /// a history). Read by the configuration page's "Workers" section — see <see cref="WorkerRunEntity"/>.
    /// </summary>
    public ILiteCollection<WorkerRunEntity> WorkerRuns => _db.GetCollection<WorkerRunEntity>("worker_runs");

    /// <summary>
    /// True when the data file was written by a NEWER plugin build than this one. Callers must not
    /// write in that case: the newer build may store fields this one would silently drop.
    /// </summary>
    public bool IsWrittenByNewerVersion { get; private set; }

    private void EnsureIndexes()
    {
        Meta.EnsureIndex(x => x.Id, true);

        Oshashes.EnsureIndex(x => x.Id, true);

        Media.EnsureIndex(x => x.Id, true);
        Media.EnsureIndex(x => x.ImdbId);
        Media.EnsureIndex(x => x.TmdbId);
        Media.EnsureIndex(x => x.Path);
        Media.EnsureIndex(x => x.JellyfinItemId);

        Embeds.EnsureIndex(x => x.Id, true);
        Embeds.EnsureIndex(x => x.MediaHash);
        Embeds.EnsureIndex(x => x.ContentHash);
        Embeds.EnsureIndex(x => x.Status);

        Sidecars.EnsureIndex(x => x.Id, true);
        Sidecars.EnsureIndex(x => x.ContentHash);
        Sidecars.EnsureIndex(x => x.MediaHash);
        Sidecars.EnsureIndex(x => x.Status);
        Sidecars.EnsureIndex(x => x.Language);

        RejectedCandidates.EnsureIndex(x => x.Id, true);
        RejectedCandidates.EnsureIndex(x => x.ItemId);
        RejectedCandidates.EnsureIndex(x => x.SubdlId);

        Counters.EnsureIndex(x => x.Key, true);
        Counters.EnsureIndex(x => x.Expires);

        Runs.EnsureIndex(x => x.Started);
        StatusStats.EnsureIndex(x => x.Id, true);
        WorkerRuns.EnsureIndex(x => x.Id, true);
    }

    /// <summary>
    /// Reads area 0 and decides whether this build may write to the file.
    /// </summary>
    private void CheckSchemaVersion()
    {
        try
        {
            var meta = Meta.FindById("db");
            if (meta == null)
            {
                meta = new MetaEntity { Id = "db", SchemaVersion = CurrentSchemaVersion };
                Meta.Upsert(meta);
                LogUtil.Normal(_logger, 
                    "[SubDL] data file initialised, schema version {Version}.", CurrentSchemaVersion);
                return;
            }

            if (meta.SchemaVersion > CurrentSchemaVersion)
            {
                IsWrittenByNewerVersion = true;
                _logger?.LogError(
                    "[SubDL] data file schema version {Found} is NEWER than this plugin understands ({Current}). "
                    + "Writing is disabled for this session so the newer data is not damaged. Update the plugin.",
                    meta.SchemaVersion, CurrentSchemaVersion);
                return;
            }

            if (meta.SchemaVersion < CurrentSchemaVersion)
            {
                _logger?.LogWarning(
                    "[SubDL] data file schema version {Found} is older than {Current}; upgrading the marker. "
                    + "Records from other schema versions are ignored, not rewritten.",
                    meta.SchemaVersion, CurrentSchemaVersion);
                meta.SchemaVersion = CurrentSchemaVersion;
                Meta.Upsert(meta);
            }
        }
        catch (Exception ex)
        {
            // A broken compatibility check must never stop the plugin from starting.
            _logger?.LogWarning("[SubDL] schema version check failed (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// Records the running plugin and Jellyfin versions in area 0.
    /// <para>
    /// Called once at startup. Deliberately NOT called per run: writing the plugin's own version on
    /// every cycle would turn the audit record into a write amplifier.
    /// </para>
    /// </summary>
    /// <param name="pluginVersion">Version of this plugin build.</param>
    /// <param name="jellyfinVersion">Version of the hosting Jellyfin server.</param>
    public void RecordVersions(string? pluginVersion, string? jellyfinVersion)
    {
        try
        {
            var meta = Meta.FindById("db") ?? new MetaEntity { Id = "db", SchemaVersion = CurrentSchemaVersion };
            meta.SchemaVersion = CurrentSchemaVersion;
            meta.PluginVersion = pluginVersion;
            meta.JellyfinVersion = jellyfinVersion;
            meta.Updated = DateTime.UtcNow;
            Meta.Upsert(meta);
            LogUtil.Normal(_logger, 
                "[SubDL] data file last written by plugin {Plugin} on Jellyfin {Jellyfin}",
                pluginVersion ?? "unknown", jellyfinVersion ?? "unknown");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[SubDL] recording versions failed (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// Delete all expired counters and very old runs.
    /// </summary>
    /// <param name="now">Reference time, defaults to now.</param>
    public void Prune(DateTime? now = null)
    {
        var cutoff = now ?? DateTime.UtcNow;
        var expiredCounters = Counters.DeleteMany(x => x.Expires != null && x.Expires < cutoff);
        var oldRuns = Runs.DeleteMany(x => x.Ended != null && x.Ended < cutoff.AddDays(-30));
        LogUtil.Detail(_logger, "[SubDL] LiteDB pruned: {ExpiredCounters} expired counters, {OldRuns} old runs", expiredCounters, oldRuns);
    }

    /// <summary>
    /// Physically compacts the data file: folds the rollback journal (<c>&lt;name&gt;-log.db</c>) into
    /// the main file, then rebuilds it so free pages left behind by deletes are released.
    /// <para>
    /// Why this exists: deleting rows in LiteDB does not shrink anything by itself. The freed space
    /// stays in the file, and every delete/update also sits in the side journal until a checkpoint.
    /// A state prune therefore leaves the store physically as large as before the prune — measured on
    /// a live file: 8 KB main + 496 KB journal before, 280 KB + 0 journal after.
    /// </para>
    /// <para>
    /// Runs on the open database rather than by copying the file aside and rebuilding a replacement,
    /// because the database is open and other writers hold references to it — a file-level swap
    /// would race with them.
    /// </para>
    /// <para>
    /// F-M236: this is only half true, and the half that was wrong took the API down. LiteDB's
    /// <c>Rebuild</c> DOES swap the file: <c>LiteEngine.Rebuild()</c> runs <c>Close()</c> →
    /// <c>RebuildService.Rebuild()</c> (which moves the file aside and renames a temp copy in) →
    /// <c>Open()</c>. When any step of that throws, the <c>Open()</c> never runs and the engine —
    /// and with it every collection handle on this shared instance — stays closed. Measured on prod
    /// (28.09.2026): after one manually triggered refresh, <c>/Plugins/SubdlSync/Stats</c> and
    /// <c>/Diagnostics</c> answered HTTP 500 with <c>ObjectDisposedException</c> on LiteDB's
    /// <c>ReaderWriterLockSlim</c> for the rest of the server's life, while <c>/Status</c> (no
    /// database) kept working.
    /// </para>
    /// <para>
    /// So the caller must be able to tell success from failure, and this method must repair the
    /// engine when the rebuild died: the returned flag is true only when the database answers
    /// afterwards, so a failed compaction costs the size reduction and nothing else.
    /// </para>
    /// </summary>
    /// <returns>Compaction result: sizes before/after, collections rebuilt, and what became of the
    /// rebuild.</returns>
    /// F-M214: a database refresh compacts in the same run; measured over data file plus journal,
    /// because reporting the main file alone understates the starting size.
    public (long Before, long After, long Rebuilt, CompactOutcome Outcome) Compact()
    {
        long beforeTotal = 0;
        try
        {
            // Measure the whole footprint, not just the main file: before the checkpoint the freed
            // bytes are held in the journal, so a main-file-only "before" understates it — measured
            // live as 8192 -> 286720 with a negative "released", which read like growth.
            var (mainBefore, journalBefore) = MeasureFootprint();
            beforeTotal = mainBefore + journalBefore;

            // Fold the journal into the main file first. Without this the bytes a prune just freed
            // are still counted against the data directory in the side file.
            _db.Checkpoint();

            // Then release the freed pages. LiteDB reports how many collections it rewrote; 0 means
            // the file was already dense and nothing needed moving.
            var rebuilt = _db.Rebuild(new LiteDB.Engine.RebuildOptions());

            var (mainAfter, journalAfter) = MeasureFootprint();
            long afterTotal = mainAfter + journalAfter;

            LogUtil.Detail(_logger,
                "[SubDL-DB] compacted: {BeforeTotal} -> {AfterTotal} bytes total "
                + "(main {MainBefore} -> {MainAfter}, journal {JournalBefore} -> {JournalAfter}); "
                + "{Rebuilt} collection(s) rebuilt, {Released} bytes released.",
                beforeTotal, afterTotal, mainBefore, mainAfter, journalBefore, journalAfter,
                rebuilt, beforeTotal - afterTotal);
            return (beforeTotal, afterTotal, rebuilt, CompactOutcome.Compacted);
        }
        catch (Exception ex)
        {
            // Compaction is housekeeping. A failure must never abort the task that called it — but
            // it must not leave a dead engine behind either (F-M236). Verify and repair. The outcome
            // stays distinguishable: "recovered" means the store works but the FILE WAS NOT SHRUNK,
            // which is not the same message as "compacted".
            _logger?.LogWarning("[SubDL-DB] compaction failed: {Msg}", ex.Message);
            var recovered = VerifyAndRepair(ex);
            if (recovered && TryRebuildIntoFreshFile(out long repairedTo))
            {
                LogUtil.Normal(_logger, "[SubDL-DB] file rebuilt from its own rows: {Before} -> {After} bytes.", beforeTotal, repairedTo);
                return (beforeTotal, repairedTo, 0, CompactOutcome.Repaired);
            }

            return (beforeTotal, beforeTotal, 0,
                recovered ? CompactOutcome.Recovered : CompactOutcome.Broken);
        }
    }

    /// <summary>
    /// F-M237: rebuilds the data file from its own rows when <c>Rebuild()</c> cannot release the
    /// pages. Reproduces and fixes prod's <c>Detected loop in FindAll({0})</c> — measured on a
    /// harness with real data: <c>Rebuild()</c> throws from ~3000 rows upward in <c>subtitles</c>,
    /// and export/import into a fresh file compacts normally afterwards (6578176 -> 6569984 bytes,
    /// rows and schema intact).
    /// <para>
    /// Why this works where <c>Rebuild()</c> fails: the rebuild walks the linked list of every
    /// index and throws when that walk exceeds a budget derived from the file size, so the failure
    /// is about index-chain length, not damaged data. Reading the rows out never walks those chains.
    /// </para>
    /// <para>
    /// Reads as <see cref="BsonDocument"/>, never as mapped entities: field types in the file
    /// (int vs long) disagree with the entity classes, and a mapped read dies with
    /// <c>InvalidCastException</c> on an unrelated field.
    /// </para>
    /// <para>
    /// Safety order: back up, write the new file, verify its row counts, and only then swap it in.
    /// Every failure path leaves the previous file in place — this runs on a live database, so a
    /// half-finished rebuild must never become the file of record.
    /// </para>
    /// </summary>
    /// <param name="afterBytes">Main-file size after a successful rebuild, in bytes.</param>
    /// <returns>True when the file was rebuilt, verified and swapped in.</returns>
    private bool TryRebuildIntoFreshFile(out long afterBytes)
    {
        afterBytes = 0;
        string freshPath = _dbPath + ".rebuild-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string backupPath = _dbPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        try
        {
            // 1. Read every area out as raw documents. This is the step that bypasses the
            //    damaged index chain — FindAll on a live engine works, only Rebuild's
            //    full-index walk does not. Reading through _db rather than a second connection:
            //    VerifyAndRepair has just proved this engine answers, and a second engine on the
            //    same path would only add lock contention.
            var data = new Dictionary<string, List<BsonDocument>>();
            foreach (var name in _db.GetCollectionNames().OrderBy(n => n))
            {
                data[name] = _db.GetCollection(name).FindAll().ToList();
            }

            // 2. Write them into a fresh file. InsertBulk keeps this linear; per-row Insert on
            //    thousands of rows spends the whole time rebalancing indexes.
            using (var target = new LiteDatabase(freshPath))
            {
                foreach (var area in data)
                {
                    if (area.Value.Count > 0)
                    {
                        target.GetCollection(area.Key).InsertBulk(area.Value);
                    }
                }
                target.Checkpoint();
            }

            // 3. VERIFY before touching the file of record: every area must come back with the
            //    same row count. This is what makes the swap safe rather than hopeful.
            using (var check = new LiteDatabase(freshPath))
            {
                foreach (var area in data)
                {
                    long actual = check.GetCollection(area.Key).Count();
                    if (actual != area.Value.Count)
                    {
                        _logger?.LogError(
                            "[SubDL-DB] rebuild verification failed for {Area}: {Expected} row(s) read, {Actual} written — keeping the original file.",
                            area.Key, area.Value.Count, actual);
                        TryDelete(freshPath);
                        return false;
                    }
                }
            }

            // 4. One restorable backup, named exactly as SubdlReset's Restore expects
            //    (subdl-scribe.db.bak-yyyyMMdd-HHmmss), so the pre-repair state is one click away.
            //    Keep only this one: the point is a single safety copy, not a pile of them.
            foreach (var old in Directory.GetFiles(Path.GetDirectoryName(_dbPath) ?? ".", DbFiles.BackupPrefix + "*"))
            {
                if (!string.Equals(old, backupPath, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(old);
                }
            }

            // 5. Swap. The engine must be closed first — LiteDB holds the file handle. Disposing
            //    also folds the journal into the main file, so the copy taken here is a
            //    self-contained, checkpointed snapshot rather than a main file whose missing
            //    journal would have to travel with it.
            _db.Dispose();
            try
            {
                File.Copy(_dbPath, backupPath, overwrite: true);

                // The old journal belongs to the OLD file generation and must not survive next to
                // the fresh one: a stale <name>-log.db beside a new main file is journal recovery
                // waiting to apply foreign pages. The failed Rebuild also leaves its own temp pair
                // behind — same treatment, they are leftovers, not state.
                DeleteSibling(_dbPath, "-log.db");
                DeleteSibling(_dbPath, "-temp.db");
                DeleteSibling(_dbPath, "-temp-log.db");

                // RENAME, not File.Copy. Measured on prod (v12.1.12.114, first live run): the copy
                // was refused with "The process cannot access the file ... because it is being used
                // by another process" — the plugin opens the same path from more than one place over
                // its lifetime (ctor, VerifyAndRepair, RecreateDbContext, the reset endpoint) and a
                // stale handle can outlive its owner. A rename replaces the directory entry and does
                // not need write access to the file contents, so it succeeds where the copy does not
                // (reproduced in a harness with a second holder open: Copy=FAIL, Move=OK). Both paths
                // are in the same directory, so this never crosses a filesystem.
                File.Move(freshPath, _dbPath, overwrite: true);
            }
            finally
            {
                _db = new LiteDatabase(_dbPath);
                EnsureIndexes();

                // Settle the size before reporting it: until this folds, the number is the main
                // file alone and the next reader would see a different one.
                _db.Checkpoint();
            }

            var (mainAfter, journalAfter) = MeasureFootprint();
            afterBytes = mainAfter + journalAfter;
            return true;
        }
        catch (Exception ex)
        {
            // The original file is still in place on every failure path above, so report and move on.
            _logger?.LogWarning("[SubDL-DB] rebuilding the data file from its own rows failed ({Msg}) — the previous file stays in use.", ex.Message);
            TryDelete(freshPath);
            return false;
        }
    }

    /// <summary>Deletes a file, ignoring a failure — cleanup must never mask the real error.</summary>
    /// <param name="path">File to delete.</param>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Nothing to do: the file is a leftover, not state.
        }
    }

    /// <summary>
    /// Deletes LiteDB's side file for a data file: the rollback journal and the rebuild
    /// temporaries. These are named from the data file's STEM (<c>subdl-scribe-log.db</c>), never by
    /// appending to the full name — that variant (<c>subdl-scribe.db-log.db</c>) matches nothing on
    /// disk, which the migration harness caught by leaving a stale journal in place.
    /// </summary>
    /// <param name="dbPath">Main data file path.</param>
    /// <param name="suffix">Suffix including the leading dash, e.g. "-log.db".</param>
    private static void DeleteSibling(string dbPath, string suffix)
    {
        TryDelete(DbFiles.SidePath(dbPath, suffix));
    }

    /// <summary>
    /// F-M236: proves the database still answers after a failed rebuild and repairs it when it does
    /// not.
    /// <para>
    /// LiteDB's <c>Rebuild()</c> closes the engine before it swaps the file and only reopens it on
    /// the success path (<c>Close()</c> → <c>RebuildService.Rebuild()</c> → <c>Open()</c>), so a
    /// throw anywhere in between leaves every collection handle on this instance dead. That is not
    /// a hypothetical: it is what produced the persistent <c>ObjectDisposedException</c> on prod.
    /// </para>
    /// <para>
    /// The probe is a real query — reading one row — because a flag would only restate what we
    /// already know about the exception. Only when it fails is the instance recreated; the new
    /// instance re-runs the schema check and the index setup through the constructor.
    /// </para>
    /// </summary>
    /// <param name="cause">The exception the rebuild threw, for the log line.</param>
    /// <returns>True when the database answers again (repaired or never broken).</returns>
    private bool VerifyAndRepair(Exception cause)
    {
        try
        {
            _ = Meta.FindById("db");
            return true; // still alive — the rebuild failed before it closed anything
        }
        catch (Exception probeEx)
        {
            _logger?.LogWarning(
                "[SubDL-DB] the engine stayed closed after the failed rebuild ({Probe}) — reopening the data file.",
                probeEx.Message);
        }

        // The engine cannot be reopened in place: LiteDB's LiteEngine leaves _state.Disposed set, so
        // Open() on the same instance is not the path Rebuild() takes. A fresh LiteDatabase over the
        // same path is, and the collections are property getters over _db — they resolve against the
        // new instance on their next access, so no handle has to be re-created by hand.
        try
        {
            var fresh = new LiteDatabase(_dbPath);
            var previous = _db;
            _db = fresh;
            EnsureIndexes();
            try
            {
                previous.Dispose();
            }
            catch (Exception disposeEx)
            {
                LogUtil.Detail(_logger, "[SubDL-DB] old engine dispose after repair: {Msg}", disposeEx.Message);
            }

            _logger?.LogWarning("[SubDL-DB] data file reopened after the failed compaction ({Cause}) — the store is usable again.", cause.Message);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[SubDL-DB] reopening the data file failed — restart Jellyfin to recover the database.");
            return false;
        }
    }

    /// <summary>
    /// Returns the byte sizes of the data file and its rollback journal. The journal is named
    /// <c>&lt;name&gt;-log.db</c> beside the main file (LiteDB, not SQLite — there is no -wal/-shm pair),
    /// and it holds every delete/update until a checkpoint folds it in.
    /// </summary>
    /// <returns>Main file size and journal size in bytes; 0 for a file that does not exist.</returns>
    private (long Main, long Journal) MeasureFootprint()
    {
        long main = File.Exists(_dbPath) ? new FileInfo(_dbPath).Length : 0;
        var journalPath = Path.Combine(
            Path.GetDirectoryName(_dbPath) ?? ".",
            Path.GetFileNameWithoutExtension(_dbPath) + "-log.db");
        long journal = File.Exists(journalPath) ? new FileInfo(journalPath).Length : 0;
        return (main, journal);
    }

    /// <summary>
    /// Replace aliases for a media record by hash. Creates the record if absent.
    /// </summary>
    /// <param name="mediaHash">Media hash.</param>
    /// <param name="patch">Optional mutation.</param>
    /// <returns>The stored record.</returns>
    public MediaEntity EnsureMedia(string mediaHash, Action<MediaEntity>? patch = null)
    {
        return EnsureMedia(mediaHash, null, null, patch);
    }

    /// <summary>
    /// Replace aliases for a media record by hash, including Jellyfin item id and path.
    /// Creates the record if absent.
    /// </summary>
    /// <param name="mediaHash">Media hash.</param>
    /// <param name="jellyfinItemId">Optional Jellyfin item id.</param>
    /// <param name="path">Optional file path.</param>
    /// <param name="patch">Optional mutation.</param>
    /// <returns>The stored record.</returns>
    public MediaEntity EnsureMedia(string mediaHash, string? jellyfinItemId, string? path, Action<MediaEntity>? patch = null)
    {
        var entity = Media.FindById(mediaHash);
        if (entity == null)
        {
            entity = new MediaEntity { Id = mediaHash, LastSeen = DateTime.UtcNow };
        }
        else
        {
            entity.LastSeen = DateTime.UtcNow;
        }

        if (!string.IsNullOrEmpty(jellyfinItemId))
        {
            entity.JellyfinItemId = jellyfinItemId;
        }

        if (!string.IsNullOrEmpty(path))
        {
            entity.Path = path;
        }

        patch?.Invoke(entity);
        Media.Upsert(entity);
        LogUtil.Detail(_logger, "[SubDL-DB] media upsert {Hash}", mediaHash);
        return entity;
    }

    /// <summary>
    /// Builds the primary key of an embedded track. Kept here beside the collection so writers and
    /// readers cannot drift apart on the key shape.
    /// </summary>
    /// <param name="mediaHash">Parent media hash.</param>
    /// <param name="subPos">ffmpeg stream position.</param>
    /// <returns>The composite key.</returns>
    public static string EmbedKey(string mediaHash, int subPos) => mediaHash + "|" + subPos;

    /// <summary>
    /// Builds the primary key of a rejected download candidate.
    /// </summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="language">Language code.</param>
    /// <param name="subdlId">SubDL release id.</param>
    /// <returns>The composite key.</returns>
    public static string CandidateKey(string itemId, string language, string subdlId)
        => itemId + "|" + language.ToUpperInvariant() + "|" + subdlId;

    /// <inheritdoc />
    public void Dispose()
    {
        _db.Dispose();
    }
}
