// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubScribe.Pipeline;
using Jellyfin.Plugin.SubScribe.Registry;

namespace Jellyfin.Plugin.SubScribe.Data;

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
/// Central data context for Sub Scribe. One SQLite file, five clearly separated areas:
/// <list type="number">
/// <item><description>0 — file compatibility (schema/plugin/Jellyfin version)</description></item>
/// <item><description>1 — OSHash cache (path → file hash, disposable)</description></item>
/// <item><description>2 — video files, each with its embedded subtitle tracks beneath it</description></item>
/// <item><description>3 — sidecar .srt files, keyed by content</description></item>
/// <item><description>4 — burned download candidates</description></item>
/// <item><description>5 — cumulative GUI counters (one row)</description></item>
/// </list>
/// <para>
/// Every area is keyed by a business key carried in the document's own Id field, never by an
/// auto-assigned id. That is the correction of F-M194: with an auto id, an upsert of a "new" row
/// can never find the row it means to update, which is what silently produced duplicates. It cannot
/// happen now because the key is computed, not assigned.
/// </para>
/// <para>
/// Why SQLite through Entity Framework Core rather than the document store this replaced: Jellyfin
/// already ships <c>Microsoft.EntityFrameworkCore.Sqlite</c> and its native <c>libe_sqlite3</c>, and
/// its <c>PluginLoadContext</c> resolves an assembly the plugin does not carry from the host. With
/// <c>ExcludeAssets=runtime</c> on that package NOTHING rides along in the ZIP, where the previous
/// engine had to ship its whole 510 KB engine. The provider's own service set (connection pooling,
/// change tracking, migrations surface, <c>VACUUM</c>) is part of what the host already pays for.
/// </para>
/// <para>
/// The store is a file of this plugin's own, never Jellyfin's <c>jellyfin.db</c>. Jellyfin offers
/// plugins no database interface at all, and its own schema is rebuilt by its migrations; a plugin
/// writing there would lose data on a server update.
/// </para>
/// </summary>
public sealed class ScribeDbContext : IDisposable
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
    // F-M286 (02.10.2026): raised to 3. The status row's two quality counters were renamed
    // (`QaRejectedDownload`/`QaRejectedUpload` -> `RejectedDownload`/`RejectedUpload`) because they
    // now count every fetched-and-discarded candidate, not the QA gates alone. An older build reading
    // this file finds neither field and reports 0, which is why the marker moves with the rename.
    // F-M308 (07.10.2026): raised to 4. The status row gained `FittedToAudio` — the count of
    // downloaded subtitles the run fitted to their audio track. An older build reading this file
    // finds no such field and reports 0, which is why the marker moves with the addition.
    // F-M325 (08.10.2026): raised to 5. The FILE CHANGED FORMAT — a SQLite database replacing the
    // document store, named subdl-scribe.sqlite (F-M326). An older build opening this file finds no
    // document header and reports a broken store; the marker is what makes that loud instead of
    // silent. The old file is imported once on first start and kept as a .bak (F-M327).
    // F-M501 (15.11.2026): raised to 13. The MEDIA record gained `TvstShowId` — die Nummer der Serie
    // bei tvsubtitles.net, auf derselben Ebene wie TmdbId und ImdbId. Die Spalte wird beim Start
    // nachgezogen (EnsureMediaColumns), ein Bestand bleibt also gueltig und lesbar; der Marker wandert
    // mit, weil ein aelterer Build das Feld nicht kennt und es beim Schreiben stillschweigend
    // verlieren wuerde.
    // F-M507 (15.11.2026): raised to 14. Die dritte Quelle bekommt ihre eigenen Spalten — je Quelle und
    // Richtung eine (MoviesubtitlesDownloadId, MoviesubtitlesUploadId) plus den Upload-Zeitpunkt
    // (MoviesubtitlesUploadedAt) an `sidecars` UND `embeds`, und das MEDIA-Record die Film-Nummer der
    // Gegenseite (`MsubMovieId`, dieselbe Ebene wie TvstShowId). Nachgezogen beim Start
    // (EnsureReportColumns/EnsureMediaColumns), Bestandszeilen bleiben unangetastet und lesen die neuen
    // Felder als null.
    public const int CurrentSchemaVersion = 17;

    private readonly string _dbPath;
    private readonly ILogger? _logger;
    private readonly ScribeSqliteContext _db;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScribeDbContext"/> class.
    /// </summary>
    /// <param name="dataDir">Plugin data directory.</param>
    /// <param name="logger">Optional logger.</param>
    public ScribeDbContext(string dataDir, ILogger? logger = null)
    {
        _logger = logger;
        Directory.CreateDirectory(dataDir);
        _dbPath = DbFiles.PathIn(dataDir);

        // F-M327: the one-time import runs BEFORE the store is opened. It is the only path that ever
        // reads the old format, it is skipped when there is nothing to import, and it never deletes

        _db = new ScribeSqliteContext(_dbPath);

        // F-M514: the context itself warns now (a rejected row, a rebuilt tracker), so it needs the
        // same logger the registries log to — one line, one place.
        _db.Logger = logger;
        _db.Database.EnsureCreated();
        EnsurePacksTable();
        EnsureTmdbAnswersTable();
        EnsureAiredSeasonsTable();
        EnsurePackSeasonsTable();
        EnsureSourceRefusalsTable();
        EnsureTitleFailuresTable();
        EnsureReportColumns();
        EnsureStatusColumns();
        EnsurePackColumns();
        EnsureMediaColumns();
        NormaliseLanguages();
        _db.ConfigurePragmas();

        Meta = new ScribeSet<MetaEntity>(_db, e => e.Id);
        Oshashes = new ScribeSet<OshashEntity>(_db, e => e.Id);
        Media = new ScribeSet<MediaEntity>(_db, e => e.Id);
        Embeds = new ScribeSet<EmbedTrackEntity>(_db, e => e.Id);
        Sidecars = new ScribeSet<SidecarEntity>(_db, e => e.Id);
        RejectedCandidates = new ScribeSet<RejectedCandidateEntity>(_db, e => e.Id);
        Counters = new ScribeSet<CounterEntity>(_db, e => e.Key);
        Runs = new ScribeSet<PipelineRunEntity>(_db, e => e.Id.ToString(CultureInfo.InvariantCulture));
        StatusStats = new ScribeSet<StatusStatsEntity>(_db, e => e.Id);
        WorkerRuns = new ScribeSet<WorkerRunEntity>(_db, e => e.Id);
        Packs = new ScribeSet<PackEntity>(_db, e => e.Id);

        CheckSchemaVersion();
    }

    /// <summary>Area 0: the single compatibility record describing this data file.</summary>
    public ScribeSet<MetaEntity> Meta { get; }

    /// <summary>Area 1: OSHash cache, keyed by media path.</summary>
    public ScribeSet<OshashEntity> Oshashes { get; }

    /// <summary>Area 2: one record per video file, keyed by media hash.</summary>
    public ScribeSet<MediaEntity> Media { get; }

    /// <summary>Area 2, beneath the file: embedded subtitle tracks, keyed by "&lt;media hash&gt;|&lt;position&gt;".</summary>
    public ScribeSet<EmbedTrackEntity> Embeds { get; }

    /// <summary>Area 3: sidecar subtitle files, keyed by content hash.</summary>
    public ScribeSet<SidecarEntity> Sidecars { get; }

    /// <summary>Area 4: download candidates that were fetched and discarded.</summary>
    public ScribeSet<RejectedCandidateEntity> RejectedCandidates { get; }

    /// <summary>Retry and fail-budget counters, keyed by the namespaced counter key.</summary>
    public ScribeSet<CounterEntity> Counters { get; }

    /// <summary>Pipeline run bookkeeping.</summary>
    public ScribeSet<PipelineRunEntity> Runs { get; }

    /// <summary>
    /// Cumulative subtitle counters for the GUI (single row, key "status"). Stored here rather than
    /// in the configuration XML so a database reset resets them too — see <see cref="StatusStatsEntity"/>.
    /// </summary>
    public ScribeSet<StatusStatsEntity> StatusStats { get; }

    /// <summary>
    /// Area 6: the LAST run of each worker, keyed by the worker's task key (one row per worker, never
    /// a history). Read by the configuration page's "Workers" section — see <see cref="WorkerRunEntity"/>.
    /// </summary>
    public ScribeSet<WorkerRunEntity> WorkerRuns { get; }

    /// <summary>
    /// Area 7: the season packs this plugin published, keyed by "&lt;imdb&gt;|S&lt;season&gt;|&lt;lang&gt;|&lt;hi|plain&gt;".
    /// One row per pack, never a history — it answers "was this season already packed in this
    /// language, and with which members" — see <see cref="PackEntity"/>.
    /// </summary>
    public ScribeSet<PackEntity> Packs { get; }

    /// <summary>
    /// True when the data file was written by a NEWER plugin build than this one. Callers must not
    /// write in that case: the newer build may store fields this one would silently drop.
    /// </summary>
    public bool IsWrittenByNewerVersion { get; private set; }

    /// <summary>
    /// F-M542: the EF context itself, for a store that owns its own table (the TMDb answers). The
    /// wrapper's other members stay the normal way in; this is the one door for a new table that has no
    /// wrapper API yet.
    /// </summary>
    public ScribeSqliteContext Context => _db;

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
                    "[Sub Scribe] data file initialised, schema version {Version}.", CurrentSchemaVersion);
                return;
            }

            if (meta.SchemaVersion > CurrentSchemaVersion)
            {
                IsWrittenByNewerVersion = true;
                _logger?.LogError(
                    "[Sub Scribe] data file schema version {Found} is NEWER than this plugin understands ({Current}). "
                    + "Writing is disabled for this session so the newer data is not damaged. Update the plugin.",
                    meta.SchemaVersion, CurrentSchemaVersion);
                return;
            }

            if (meta.SchemaVersion < CurrentSchemaVersion)
            {
                _logger?.LogWarning(
                    "[Sub Scribe] data file schema version {Found} is older than {Current}; upgrading the marker. "
                    + "Records from other schema versions are ignored, not rewritten.",
                    meta.SchemaVersion, CurrentSchemaVersion);
                meta.SchemaVersion = CurrentSchemaVersion;
                Meta.Upsert(meta);
            }
        }
        catch (Exception ex)
        {
            // A broken compatibility check must never stop the plugin from starting.
            _logger?.LogWarning("[Sub Scribe] schema version check failed (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// F-M390: creates the <c>packs</c> table on a data file that predates it.
    /// <para>
    /// <c>EnsureCreated</c> only builds a schema when the FILE is new, so a store written by an
    /// earlier build keeps its old tables and every query against the new one would throw. The
    /// statement is <c>IF NOT EXISTS</c>, so a fresh file (where EnsureCreated already built the
    /// table) and a second start are both no-ops; a failure is logged and swallowed, because a
    /// missing pack table must not stop the plugin from starting.
    /// </para>
    /// </summary>
    private void EnsurePacksTable()
    {
        try
        {
            _db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS packs ("
                + "Id TEXT NOT NULL PRIMARY KEY, "
                + "ImdbId TEXT NOT NULL DEFAULT '', "
                + "Season INTEGER NOT NULL DEFAULT 0, "
                + "Language TEXT NOT NULL DEFAULT '', "
                + "HearingImpaired INTEGER NULL, "
                + "ReleaseName TEXT NULL, "
                + "PackId TEXT NULL, "
                + "Episodes TEXT NOT NULL DEFAULT '', "
                + "AbsorbedIds TEXT NULL, "
                + "Fingerprint TEXT NULL, "
                + "Created TEXT NOT NULL DEFAULT '', "
                + "Updated TEXT NOT NULL DEFAULT '');");
            _db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_packs_ImdbId ON packs (ImdbId);");
            _db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_packs_Season ON packs (Season);");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] could not create the packs table (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// F-M542: creates the <c>tmdb_answers</c> table on a data file that predates it. Same reason and
    /// same shape as <see cref="EnsurePacksTable"/>: <c>EnsureCreated</c> builds a schema only when the
    /// FILE is new, and a missing table would throw on the first TMDb answer instead of at startup.
    /// </summary>
    private void EnsureTmdbAnswersTable()
    {
        try
        {
            _db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS tmdb_answers ("
                + "Id TEXT NOT NULL PRIMARY KEY, "
                + "Value TEXT NOT NULL DEFAULT '', "
                + "Written TEXT NOT NULL DEFAULT '');");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] could not create the tmdb_answers table (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// F-M601: creates the <c>aired_seasons</c> table on a data file that predates it. Same shape and
    /// same reason as <see cref="EnsureTmdbAnswersTable"/>: <c>EnsureCreated</c> builds a schema only
    /// when the FILE is new, and a missing table would throw on the first stored answer instead of at
    /// startup. Name and mapping are identical on purpose (<c>aired_seasons</c> both here and in
    /// <c>OnModelCreating</c>) — a table that is created under one name and read under another is
    /// dead weight that looks like a working cache.
    /// </summary>
    /// <summary>
    /// F-M604: the stored pack verdicts. Same reasoning as <see cref="EnsureAiredSeasonsTable"/> — the
    /// table is created here and read under the very same name in <c>OnModelCreating</c>.
    /// </summary>
    /// <summary>
    /// F-M605: the remembered source refusals. Same reasoning as <see cref="EnsurePackSeasonsTable"/>.
    /// </summary>
    /// <summary>
    /// F-M608: how often creating and finding a title failed, and whether it is closed for good.
    /// </summary>
    private void EnsureTitleFailuresTable()
    {
        try
        {
            _db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS title_failures ("
                + "Id TEXT NOT NULL PRIMARY KEY, "
                + "Attempts INTEGER NOT NULL DEFAULT 0, "
                + "Closed INTEGER NOT NULL DEFAULT 0, "
                + "Reason TEXT NOT NULL DEFAULT '', "
                + "Written TEXT NOT NULL DEFAULT '');");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] could not create the title_failures table (continuing): {Msg}", ex.Message);
        }
    }

    private void EnsureSourceRefusalsTable()
    {
        try
        {
            _db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS source_refusals ("
                + "Id TEXT NOT NULL PRIMARY KEY, "
                + "Signature TEXT NOT NULL DEFAULT '', "
                + "Reason TEXT NOT NULL DEFAULT '', "
                + "Written TEXT NOT NULL DEFAULT '');");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] could not create the source_refusals table (continuing): {Msg}", ex.Message);
        }
    }

    private void EnsurePackSeasonsTable()
    {
        try
        {
            _db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS pack_seasons ("
                + "Id TEXT NOT NULL PRIMARY KEY, "
                + "Signature TEXT NOT NULL DEFAULT '', "
                + "Verdict TEXT NOT NULL DEFAULT '', "
                + "Reason TEXT NOT NULL DEFAULT '', "
                + "Written TEXT NOT NULL DEFAULT '');");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] could not create the pack_seasons table (continuing): {Msg}", ex.Message);
        }
    }

    private void EnsureAiredSeasonsTable()
    {
        try
        {
            _db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS aired_seasons ("
                + "Id TEXT NOT NULL PRIMARY KEY, "
                + "Value TEXT NOT NULL DEFAULT '', "
                + "Written TEXT NOT NULL DEFAULT '');");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] could not create the aired_seasons table (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// F-M450: adds the report columns to <c>rejected_candidates</c> on a data file that predates
    /// them.
    /// <para>
    /// <c>EnsureCreated</c> builds a schema only when the FILE is new and never alters an existing
    /// table, so an upgraded installation would otherwise keep five columns while every write names
    /// ten — and the failure would land in the middle of a download run. Each <c>ALTER</c> is
    /// guarded by the table's own column list, because SQLite has no "ADD COLUMN IF NOT EXISTS".
    /// A failure is logged and swallowed: reporting must never stop the plugin from starting.
    /// </para>
    /// </summary>
    private void EnsureReportColumns()
    {
        try
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = _db.Database.GetDbConnection().CreateCommand())
            {
                if (cmd.Connection!.State != System.Data.ConnectionState.Open)
                {
                    cmd.Connection.Open();
                }

                cmd.CommandText = "PRAGMA table_info(rejected_candidates);";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    existing.Add(reader.GetString(1));
                }
            }

            foreach (var (name, ddl) in new[]
                     {
                         ("NId", "ALTER TABLE rejected_candidates ADD COLUMN NId TEXT NOT NULL DEFAULT '';"),
                         ("ReleaseName", "ALTER TABLE rejected_candidates ADD COLUMN ReleaseName TEXT NOT NULL DEFAULT '';"),
                         ("Finding", "ALTER TABLE rejected_candidates ADD COLUMN Finding TEXT NOT NULL DEFAULT '';"),
                         ("Kind", "ALTER TABLE rejected_candidates ADD COLUMN Kind TEXT NOT NULL DEFAULT 'qa';"),
                         ("ReportedUtc", "ALTER TABLE rejected_candidates ADD COLUMN ReportedUtc TEXT NULL;")
                     })
            {
                if (!existing.Contains(name))
                {
                    _db.Database.ExecuteSqlRaw(ddl);
                    LogUtil.Normal(_logger, "[Sub Scribe] data file: column {Column} added to rejected_candidates.", name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] could not add the report columns (continuing): {Msg}", ex.Message);
        }

        // F-M486 (schema 10): Herkunft je Untertitel. Ohne sie kann der Upload nicht wissen, ob er
        // einen tvsubtitles-Fund gerade zu SubDL schickt (F-M487) — die Spalte ist die einzige Stelle,
        // an der diese Herkunft überlebt.
        foreach (var (table, column, ddl) in new[]
                 {
                     ("sidecars", "Origin", "ALTER TABLE sidecars ADD COLUMN Origin TEXT NULL;"),
                     ("sidecars", "OriginId", "ALTER TABLE sidecars ADD COLUMN OriginId TEXT NULL;"),
                     ("embeds", "Origin", "ALTER TABLE embeds ADD COLUMN Origin TEXT NULL;"),
                     ("embeds", "OriginId", "ALTER TABLE embeds ADD COLUMN OriginId TEXT NULL;"),

                     // F-M488 (schema 11): vier eigene Kennzeichen, je Quelle und Richtung eines.
                     ("sidecars", "SubdlDownloadId", "ALTER TABLE sidecars ADD COLUMN SubdlDownloadId TEXT NULL;"),
                     ("sidecars", "SubdlUploadId", "ALTER TABLE sidecars ADD COLUMN SubdlUploadId TEXT NULL;"),
                     ("sidecars", "TvSubtitlesDownloadId", "ALTER TABLE sidecars ADD COLUMN TvSubtitlesDownloadId TEXT NULL;"),
                     ("sidecars", "TvSubtitlesUploadId", "ALTER TABLE sidecars ADD COLUMN TvSubtitlesUploadId TEXT NULL;"),
                     ("embeds", "SubdlDownloadId", "ALTER TABLE embeds ADD COLUMN SubdlDownloadId TEXT NULL;"),
                     ("embeds", "SubdlUploadId", "ALTER TABLE embeds ADD COLUMN SubdlUploadId TEXT NULL;"),
                     ("embeds", "TvSubtitlesDownloadId", "ALTER TABLE embeds ADD COLUMN TvSubtitlesDownloadId TEXT NULL;"),
                     ("embeds", "TvSubtitlesUploadId", "ALTER TABLE embeds ADD COLUMN TvSubtitlesUploadId TEXT NULL;"),

                     // F-M490 (schema 12): "hochgeladen" je Quelle, mit Zeitpunkt.
                     ("sidecars", "SubdlUploadedAt", "ALTER TABLE sidecars ADD COLUMN SubdlUploadedAt TEXT NULL;"),
                     ("sidecars", "TvSubtitlesUploadedAt", "ALTER TABLE sidecars ADD COLUMN TvSubtitlesUploadedAt TEXT NULL;"),
                     ("embeds", "SubdlUploadedAt", "ALTER TABLE embeds ADD COLUMN SubdlUploadedAt TEXT NULL;"),
                     ("embeds", "TvSubtitlesUploadedAt", "ALTER TABLE embeds ADD COLUMN TvSubtitlesUploadedAt TEXT NULL;"),

                     // F-M507 (schema 14): die dritte Quelle, je Richtung ihr eigenes Kennzeichen und
                     // ihr eigener Upload-Zeitpunkt — dieselben drei Felder in beiden Untertitel-Tabellen.
                     ("sidecars", "MoviesubtitlesDownloadId", "ALTER TABLE sidecars ADD COLUMN MoviesubtitlesDownloadId TEXT NULL;"),
                     ("sidecars", "MoviesubtitlesUploadId", "ALTER TABLE sidecars ADD COLUMN MoviesubtitlesUploadId TEXT NULL;"),
                     ("sidecars", "MoviesubtitlesUploadedAt", "ALTER TABLE sidecars ADD COLUMN MoviesubtitlesUploadedAt TEXT NULL;"),
                     ("embeds", "MoviesubtitlesDownloadId", "ALTER TABLE embeds ADD COLUMN MoviesubtitlesDownloadId TEXT NULL;"),
                     ("embeds", "MoviesubtitlesUploadId", "ALTER TABLE embeds ADD COLUMN MoviesubtitlesUploadId TEXT NULL;"),
                     ("embeds", "MoviesubtitlesUploadedAt", "ALTER TABLE embeds ADD COLUMN MoviesubtitlesUploadedAt TEXT NULL;")
                 })
        {
            try
            {
                var exists = false;
                using var cmd = _db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "PRAGMA table_info(" + table + ");";
                _db.Database.OpenConnection();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                    }
                }

                _db.Database.CloseConnection();
                if (!exists)
                {
                    _db.Database.ExecuteSqlRaw(ddl);
                    LogUtil.Normal(_logger, "[Sub Scribe] data file: column {Column} added to {Table}.", column, table);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("[Sub Scribe] could not add {Column} to {Table} (continuing): {Msg}", column, table, ex.Message);
            }
        }
    }

    /// <summary>
    /// F-M466: adds the pack fingerprint column. A table that already carries it is left alone, so
    /// the pass runs on every start and costs one PRAGMA.
    /// </summary>
    private void EnsurePackColumns()
    {
        try
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = _db.Database.GetDbConnection().CreateCommand())
            {
                if (cmd.Connection!.State != System.Data.ConnectionState.Open)
                {
                    cmd.Connection.Open();
                }

                cmd.CommandText = "PRAGMA table_info(packs);";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    existing.Add(reader.GetString(1));
                }
            }

            const string Name = "Fingerprint";
            if (!existing.Contains(Name))
            {
                _db.Database.ExecuteSqlRaw("ALTER TABLE packs ADD COLUMN Fingerprint TEXT NULL;");
                LogUtil.Normal(_logger, "[Sub Scribe] data file: column {Column} added to packs.", Name);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] could not add the pack columns (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// F-M501 (Schema 13) / F-M507 (Schema 14): zieht die Spalten des MEDIUM-Records auf einem Bestand
    /// nach, der sie noch nicht kennt — heute <c>media.TvstShowId</c> und <c>media.MsubMovieId</c>.
    /// <para>
    /// <c>EnsureCreated</c> baut ein Schema nur bei einer NEUEN Datei und ändert eine bestehende Tabelle
    /// nie — ohne diesen Schritt liefe jede Abfrage auf einem aufgerüsteten Datenbestand gegen eine
    /// Tabelle ohne die Spalte, und der Fehler landete mitten im Upload-Lauf. SQLite kennt kein
    /// „ADD COLUMN IF NOT EXISTS", deshalb ist jedes <c>ALTER</c> an die Spaltenliste der Tabelle
    /// gebunden. Ein Fehlschlag wird protokolliert und geschluckt: eine fehlende Spalte darf den
    /// Plugin-Start nicht verhindern.
    /// </para>
    /// <para>
    /// Die Liste ist eine Liste, kein Einzelfall: die nächste Spalte am Medium kostet eine Zeile hier
    /// und keine zweite Methode.
    /// </para>
    /// </summary>
    private void EnsureMediaColumns()
    {
        foreach (var (column, ddl) in new[]
                 {
                     ("TvstShowId", "ALTER TABLE media ADD COLUMN TvstShowId TEXT NULL;"),
                     ("MsubMovieId", "ALTER TABLE media ADD COLUMN MsubMovieId TEXT NULL;"),

                     // F-M557/F-M562 (schema 15): der Zugeh-Stempel JE QUELLE plus die Spursignatur, zu
                     // der er gehoert. Eine globale Marke gibt es weiter, sie entscheidet aber nichts mehr.
                     ("CompleteSubdl", "ALTER TABLE media ADD COLUMN CompleteSubdl TEXT NULL;"),
                     ("CompleteTvSubtitles", "ALTER TABLE media ADD COLUMN CompleteTvSubtitles TEXT NULL;"),
                     ("CompleteMsub", "ALTER TABLE media ADD COLUMN CompleteMsub TEXT NULL;"),
                     ("CompleteSignature", "ALTER TABLE media ADD COLUMN CompleteSignature TEXT NULL;"),
                 })
        {
            try
            {
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = _db.Database.GetDbConnection().CreateCommand())
                {
                    if (cmd.Connection!.State != System.Data.ConnectionState.Open)
                    {
                        cmd.Connection.Open();
                    }

                    cmd.CommandText = "PRAGMA table_info(media);";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        existing.Add(reader.GetString(1));
                    }
                }

                if (!existing.Contains(column))
                {
                    _db.Database.ExecuteSqlRaw(ddl);
                    LogUtil.Normal(_logger, "[Sub Scribe] data file: column {Column} added to media.", column);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("[Sub Scribe] could not add {Column} to media (continuing): {Msg}", column, ex.Message);
            }
        }
    }

    /// <summary>
    /// F-M557 (Operator-Anweisung 17.11.2026: „Ich würde iso nehmen und immer auf die jeweiligen Quellen
    /// umsetzen. Per Software-Routine"): Schema 16 schreibt die Sprache EINMAL in ISO 639-2
    /// (<c>ENG</c>, <c>DEU</c>) in den Store; jede Quelle bekommt ihre Schreibweise erst an der Kante.
    /// <para>
    /// Diese Wanderung zieht den Bestand nach: jede Zeile in <c>embeds</c> und <c>sidecars</c> bekommt ihr
    /// ISO-Kennzeichen, und die Zugeh-Stempel werden geleert — eine Signatur aus der alten Schreibweise
    /// darf keine Datei als fertig behaupten.
    /// </para>
    /// </summary>
    private void NormaliseLanguages()
    {
        try
        {
            int geaendert = 0;
            foreach (var row in _db.EmbedRows.ToList())
            {
                string? iso = Language.LanguageMapper.Canonical(row.Language);
                if (iso != null && !string.Equals(iso, row.Language, StringComparison.Ordinal))
                {
                    row.Language = iso;
                    _db.EmbedRows.Update(row);
                    geaendert++;
                }
            }

            foreach (var row in _db.SidecarRows.ToList())
            {
                string? iso = Language.LanguageMapper.Canonical(row.Language);
                if (iso != null && !string.Equals(iso, row.Language, StringComparison.Ordinal))
                {
                    row.Language = iso;
                    _db.SidecarRows.Update(row);
                    geaendert++;
                }
            }

            foreach (var media in _db.MediaRows.ToList())
            {
                if (media.CompleteSubdl != null || media.CompleteTvSubtitles != null
                    || media.CompleteMsub != null || media.CompleteSignature != null)
                {
                    media.CompleteSubdl = null;
                    media.CompleteTvSubtitles = null;
                    media.CompleteMsub = null;
                    media.CompleteSignature = null;
                    _db.MediaRows.Update(media);
                }
            }

            int n = _db.SaveChanges();
            if (geaendert > 0)
            {
                LogUtil.Normal(_logger, "[Sub Scribe] data file: {Count} language tag(s) rewritten to ISO 639-2 (schema 16).", geaendert);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] could not rewrite the language tags (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// F-M455: adds the status row's own late column — the count of broken subtitles reported to
    /// SubDL. A table that already carries it is left alone, so the pass runs on every start and
    /// costs one PRAGMA.
    /// </summary>
    private void EnsureStatusColumns()
    {
        try
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = _db.Database.GetDbConnection().CreateCommand())
            {
                if (cmd.Connection!.State != System.Data.ConnectionState.Open)
                {
                    cmd.Connection.Open();
                }

                cmd.CommandText = "PRAGMA table_info(status_stats);";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    existing.Add(reader.GetString(1));
                }
            }

            if (!existing.Contains("BrokenReported"))
            {
                _db.Database.ExecuteSqlRaw("ALTER TABLE status_stats ADD COLUMN BrokenReported INTEGER NOT NULL DEFAULT 0;");
                LogUtil.Normal(_logger, "[Sub Scribe] data file: column BrokenReported added to status_stats.");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] could not add the status column (continuing): {Msg}", ex.Message);
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
                "[Sub Scribe] data file last written by plugin {Plugin} on Jellyfin {Jellyfin}",
                pluginVersion ?? "unknown", jellyfinVersion ?? "unknown");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] recording versions failed (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// Reads the journal mode the data file is actually in, through the live context.
    /// </summary>
    /// <returns>The mode string, e.g. <c>wal</c> or <c>delete</c>.</returns>
    public string ReadJournalMode()
    {
        FlushPending();
        return _db.ReadJournalMode();
    }

    /// <summary>
    /// Drops every idle pooled connection for this database, so a file-level REPLACEMENT of the
    /// database is actually seen by the next open.
    /// <para>
    /// Called by the reset and restore paths immediately before they copy a file over the database.
    /// Pooling is off in this context, but SQLite keeps a process-wide pool registry and the plugin
    /// shares one path across several contexts over its lifetime — so the call is cheap insurance
    /// for the one operation that cannot tolerate a stale handle.
    /// </para>
    /// </summary>
    /// <param name="path">Path of the database file.</param>
    public static void ClearPoolFor(string path)
    {
        using var cn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString());
        SqliteConnection.ClearPool(cn);
        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Folds the write-ahead log into the main file, so the file on disk is a complete,
    /// self-contained snapshot.
    /// <para>
    /// Required before any file-level copy of the database. In WAL mode every committed write sits
    /// in <c>&lt;name&gt;-wal</c> until a checkpoint; a copy taken without this one carries the main
    /// file alone and silently loses everything written since the last checkpoint — and a RESTORE
    /// from such a copy leaves a stale <c>-wal</c> beside the restored file, which SQLite then tries
    /// to apply as if it belonged to it. The previous engine was a single file and needed none of
    /// this, which is exactly why the copy sites are worth checking when the format changes.
    /// </para>
    /// </summary>
    public void Checkpoint()
    {
        FlushPending();
        _db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");
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
        LogUtil.Detail(_logger, "[Sub Scribe] store pruned: {ExpiredCounters} expired counters, {OldRuns} old runs", expiredCounters, oldRuns);
    }

    /// <summary>
    /// Physically compacts the data file. SQLite's <c>VACUUM</c> does in one statement what the
    /// previous engine needed a rebuild for: it rewrites the database into a dense file, which
    /// releases the pages that deletes freed.
    /// <para>
    /// Why this exists: deleting rows in SQLite does not shrink the file. The freed pages go on the
    /// freelist and the file stays as large as before the prune, so a state prune would leave the
    /// store physically untouched.
    /// </para>
    /// <para>
    /// <c>VACUUM</c> cannot run inside a transaction, and this runs on a database other writers hold
    /// open — so the outcome is reported rather than assumed. The returned flag is true only when the
    /// database answers afterwards, so a failed compaction costs the size reduction and nothing else.
    /// </para>
    /// </summary>
    /// <returns>Compaction result: sizes before/after, collections rebuilt, and what became of the
    /// rebuild.</returns>
    /// F-M214: a database refresh compacts in the same run; measured over the database file plus its
    /// write-ahead log, because reporting the main file alone understates the starting size.
    public (long Before, long After, long Rebuilt, CompactOutcome Outcome) Compact()
    {
        // The measured footprint is what the FILE holds, so a batch still in the change tracker
        // would be missing from it and the "before" number would understate the store.
        FlushPending();
        var (mainBefore, walBefore) = MeasureFootprint();
        long beforeTotal = mainBefore + walBefore;

        try
        {
            // Fold the WAL into the main file first. Without this, the bytes a prune just freed are
            // still counted against the data directory in the side file, and VACUUM would report a
            // "before" that no reader ever sees.
            _db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");

            _db.Database.ExecuteSqlRaw("VACUUM;");

            // F-M342 (operator order 10.10.2026): refresh the query statistics in the SAME pass.
            // VACUUM shrinks the file; ANALYZE is what fills sqlite_stat1 — the row count and the
            // rows-per-key average of every index — and sqlite_stat1 is what the query planner reads
            // to pick an index. Without it the planner only guesses, and the guess is measurably
            // wrong: on "MediaHash = ? AND Status = ?" against this schema it chose IX_embeds_Status
            // before the statistics existed and IX_embeds_MediaHash after, and MediaHash is the
            // selective one (13 rows per key against 29).
            //
            // Its OWN try, deliberately: a statistics failure must NOT be reported as a failed
            // compaction. The file WAS shrunk at this point, and the outcomes below ("recovered",
            // "the data file was NOT shrunk") would be a lie about a file that is smaller than it was.
            // The statistics are the cheaper half of the pair and lose nothing but their own freshness.
            bool statisticsRefreshed = false;
            try
            {
                _db.Database.ExecuteSqlRaw("ANALYZE;");
                statisticsRefreshed = true;
            }
            catch (Exception statEx)
            {
                _logger?.LogWarning(
                    "[Sub Scribe-DB] ANALYZE skipped ({Msg}) — the file was compacted, the query statistics stay stale.",
                    statEx.Message);
            }

            // Fold the rewrite in before measuring. VACUUM and ANALYZE BOTH write into the journal
            // in WAL mode, so without this second checkpoint the "after" number describes a journal
            // holding the whole rebuilt database rather than the settled file — measured in the
            // store harness: main 159 744 B plus wal 164 856 B gave the 324 600 B the line reported,
            // against a 155 648 B "before", which reads as a file that doubled while it shrank.
            // F-M214 wants the footprint of the settled store, and the file is meant to be
            // self-contained afterwards (a plain copy is a backup) — one fold serves both.
            _db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");

            // A VACUUM rewrites every table; SQLite reports nothing, so the count that the previous
            // engine got from its rebuild is taken from the collections that actually hold rows.
            long rebuilt = _db.GetAreaRowCounts().Count(x => x.Rows > 0);

            var (mainAfter, walAfter) = MeasureFootprint();
            long afterTotal = mainAfter + walAfter;

            LogUtil.Detail(_logger,
                "[Sub Scribe-DB] compacted: {BeforeTotal} -> {AfterTotal} bytes total "
                + "(main {MainBefore} -> {MainAfter}, wal {WalBefore} -> {WalAfter}); "
                + "{Rebuilt} area(s) rewritten, {Released} bytes released; statistics {Stats}.",
                beforeTotal, afterTotal, mainBefore, mainAfter, walBefore, walAfter,
                rebuilt, beforeTotal - afterTotal,
                statisticsRefreshed ? "refreshed" : "stale (ANALYZE did not run)");
            return (beforeTotal, afterTotal, rebuilt, CompactOutcome.Compacted);
        }
        catch (Exception ex)
        {
            // Compaction is housekeeping. A failure must never abort the task that called it — but it
            // must not leave a dead connection behind either. The outcome stays distinguishable:
            // "recovered" means the store works but the FILE WAS NOT SHRUNK, which is not the same
            // message as "compacted".
            _logger?.LogWarning("[Sub Scribe-DB] compaction failed: {Msg}", ex.Message);
            var recovered = VerifyAndRepair(ex);
            return (beforeTotal, beforeTotal, 0,
                recovered ? CompactOutcome.Recovered : CompactOutcome.Broken);
        }
    }

    /// <summary>
    /// Proves the database still answers after a failed compaction.
    /// <para>
    /// The probe is a real query — reading one row — because a flag would only restate what we
    /// already know about the exception. A SQLite connection that survives a failed VACUUM needs no
    /// repair; the check exists so the caller can tell "usable but not shrunk" from "broken".
    /// </para>
    /// </summary>
    /// <param name="cause">The exception the compaction threw, for the log line.</param>
    /// <returns>True when the database answers again.</returns>
    private bool VerifyAndRepair(Exception cause)
    {
        try
        {
            _ = Meta.FindById("db");
            _logger?.LogWarning("[Sub Scribe-DB] data file still answers after the failed compaction ({Cause}).", cause.Message);
            return true;
        }
        catch (Exception probeEx)
        {
            _logger?.LogError(probeEx, "[Sub Scribe-DB] the data file stopped answering after the failed compaction — restart Jellyfin to recover the database.");
            return false;
        }
    }

    /// <summary>
    /// Returns the byte sizes of the database file and its write-ahead log. SQLite in WAL mode keeps
    /// every delete and update in the side <c>-wal</c> file until a checkpoint folds it in.
    /// </summary>
    /// <returns>Main file size and WAL size in bytes; 0 for a file that does not exist.</returns>
    private (long Main, long Wal) MeasureFootprint()
    {
        FlushPending();
        long main = File.Exists(_dbPath) ? new FileInfo(_dbPath).Length : 0;
        var walPath = DbFiles.SidePath(_dbPath, "-wal");
        long wal = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
        return (main, wal);
    }

    /// <summary>
    /// Opens a batch on the underlying context — see <see cref="ScribeSqliteContext.BeginBatch"/>.
    /// <para>
    /// The wrapper exposes it because this is the type the callers hold; the batch state itself
    /// lives beside <c>SaveChanges</c>, which only the context can call.
    /// </para>
    /// </summary>
    public void BeginBatch() => _db.BeginBatch();

    /// <summary>Closes a batch and commits what it accumulated.</summary>
    public void EndBatch() => _db.EndBatch();

    /// <summary>Commits accumulated writes now.</summary>
    public void FlushPending() => _db.FlushPending();

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
    public MediaEntity EnsureMedia(string? mediaHash, string? jellyfinItemId, string? path, Action<MediaEntity>? patch = null)
    {
        // F-M514 (Vorfall 16.11.2026, jf-test 02:51): OHNE SCHLUESSEL GIBT ES KEINE ZEILE.
        // Ein leerer Medien-Hash heisst "diese Datei hat noch keine Identitaet" — nicht "eine Zeile
        // ohne Id". Genau das wurde hier aber gebaut: `new MediaEntity { Id = null }`, und Entity
        // Framework verweigert das Anhaengen ("Unable to track an entity of type 'MediaEntity'
        // because its primary key property 'Id' is null"). Seit EF Core 10 bleibt die abgewiesene
        // Instanz danach als Detached im Change Tracker stehen, und JEDES spaetere SaveChanges
        // dieses Kontexts wirft "Unexpected entry.EntityState: Detached" — der Store war damit fuer
        // den Rest des Prozesses schreibunfaehig, und der Upload-Zyklus endete ohne Lauf. Gemessen
        // im Log: 02:51:48.410 die WRITE-FAILED-Zeile, 02:51:48.428 die erste Detached-Warnung.
        // Der Aufruf kommt aus `MarkSidecar` (Download-Pfad), wenn `GetMediaHash` fuer die eben
        // geschriebene Datei noch keinen OSHash kennt. Also: nichts anlegen, nichts anhaengen, und
        // die Zusage "kein Medium bekannt" durchreichen — der Schreiber bekommt eine leere, nicht
        // gespeicherte Instanz (seine Kopien der Kennungen bleiben dann leer, wie ohne Zeile).
        if (string.IsNullOrEmpty(mediaHash))
        {
            LogUtil.Detail(_logger, "[Sub Scribe-DB] media row NOT written: no media hash known (F-M514)");
            return new MediaEntity();
        }

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
        LogUtil.Detail(_logger, "[Sub Scribe-DB] media upsert {Hash}", mediaHash);
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
    /// F-M390: builds the primary key of a season pack — one pack per season, language and variant.
    /// <para>
    /// The variant is part of the key because a plain pack and an SDH pack of the same season are two
    /// different containers on SubDL, and SubDL carries the HI flag as a single field. Without it the
    /// second pack would overwrite the first one's row and the plugin would believe it had published
    /// something it never sent.
    /// </para>
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="language">SubDL language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant flag.</param>
    /// <returns>The composite key.</returns>
    public static string PackKey(string imdbId, int season, string language, bool hearingImpaired)
        => imdbId + "|S" + season.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + "|" + language + "|" + (hearingImpaired ? "hi" : "plain");

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
        // A batch left open by an exception still owns writes the unbatched store would have
        // committed, so the close drains them before the file handle goes away.
        try
        {
            FlushPending();
        }
        catch (Exception ex)
        {
            LogUtil.Detail(_logger, "[Sub Scribe-DB] pending writes could not be flushed on dispose: {Msg}", ex.Message);
        }

        _db.Dispose();
    }
}

/// <summary>
/// The Entity Framework Core model over the plugin's own SQLite file.
/// <para>
/// One <see cref="DbSet{TEntity}"/> per area, with the business key as the primary key — never an
/// auto-assigned id (F-M194). The indexes are declared here so they exist in the schema rather than
/// being created at runtime on every open.
/// </para>
/// </summary>
public sealed class ScribeSqliteContext : DbContext
{
    private readonly string _path;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScribeSqliteContext"/> class.
    /// </summary>
    /// <param name="path">Path of the SQLite file, this plugin's own.</param>
    public ScribeSqliteContext(string path)
    {
        _path = path;
    }

    /// <summary>Path of the database file this context owns.</summary>
    public string Path => _path;

    /// <summary>
    /// The one gate every read and write goes through.
    /// <para>
    /// NOT optional, and not a performance choice. A <see cref="DbContext"/> is single-threaded by
    /// contract: a second operation started while another is in flight throws
    /// <c>InvalidOperationException</c> ("A second operation was started on this context instance
    /// before a previous operation completed"). The plugin shares ONE context across all registries,
    /// the scheduled tasks and the API endpoints, and the configuration page polls
    /// <c>/Plugins/SubdlSync/Stats</c> every 10 seconds while a worker writes — so without this gate
    /// the failure is not hypothetical, it is a matter of timing.
    /// </para>
    /// <para>
    /// The previous engine held thread-safe collection handles and needed no such gate. This is the
    /// one behavioural difference the format change introduced that the callers cannot see, which is
    /// why it lives here rather than at every call site.
    /// </para>
    /// </summary>
    public object Gate { get; } = new();

    /// <summary>
    /// F-M514: the plugin's logger, handed down by <see cref="ScribeDbContext"/> so a store-side guard
    /// can warn without a second wiring path. Null in a host-free probe, so every use is optional.
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// F-M514: a rejected attach must not poison this context for the rest of the process.
    /// <para>
    /// Entity Framework Core 10 leaves the instance behind as a <c>Detached</c> entry when it refuses
    /// to track it (measured 16.11.2026 on 10.0.11: "Unable to track an entity … primary key property
    /// 'Id' is null"). From then on EVERY <c>SaveChanges</c> of the same context throws
    /// <c>ArgumentOutOfRangeException: Unexpected entry.EntityState: Detached</c> from the command
    /// batch preparer, and nothing can be written again — that is how one unkeyed media row ended a
    /// whole upload cycle ("cycle finished without an upload run", jf-test 02:51–03:07).
    /// </para>
    /// <para>
    /// The repair drops the ghosts and re-attaches everything else from the same instances, so the
    /// identity map and every pending change survive. It is safe to retry because the exception is
    /// thrown while the commands are PREPARED, before any of them is executed: a failed preparation
    /// writes nothing at all. A second failure is NOT swallowed — the retry is the last word, and a
    /// store that still refuses stays visible.
    /// </para>
    /// </summary>
    /// <inheritdoc />
    public override int SaveChanges()
    {
        try
        {
            return base.SaveChanges();
        }
        catch (ArgumentOutOfRangeException ex) when (IsDetachedGhost(ex) && RestoreTracker())
        {
            Logger?.LogWarning(
                "[Sub Scribe-DB] a rejected row left a Detached entry in the change tracker; the tracker was rebuilt and the write retried ({Msg})",
                ex.Message);
            return base.SaveChanges();
        }
    }

    /// <summary>F-M514: the one exception shape the tracker repair answers to.</summary>
    /// <param name="ex">The thrown exception.</param>
    /// <returns>True when this is the Detached-ghost failure of the command batch preparer.</returns>
    private static bool IsDetachedGhost(Exception ex)
        => ex.Message.Contains("Unexpected entry.EntityState", StringComparison.Ordinal);

    /// <summary>
    /// F-M514: rebuilds the change tracker without the entries Entity Framework left as
    /// <c>Detached</c>, keeping every other row tracked with the state it had.
    /// <para>
    /// Setting <c>Entry.State = Detached</c> on the ghost does NOT remove it — measured against
    /// 10.0.11: the entry stays in the tracker and the next save throws the same exception. Only
    /// <see cref="ChangeTracker.Clear"/> drops it, which is why every tracked row is captured first
    /// and re-attached afterwards (as <c>Added</c>/<c>Modified</c>/<c>Deleted</c>/<c>Unchanged</c>).
    /// The row cache of the areas points at these same instances, so re-attaching them is also what
    /// keeps "read a row, mutate it, save it" working — an untracked cached row would make a later
    /// save silently write nothing.
    /// </para>
    /// </summary>
    /// <returns>True when the tracker was rebuilt, false when even that failed.</returns>
    private bool RestoreTracker()
    {
        try
        {
            // Detect first: a row mutated by its caller still reads "Unchanged" until change
            // detection runs, and re-attaching it as Unchanged would discard that change.
            ChangeTracker.DetectChanges();

            var snapshot = ChangeTracker.Entries()
                .Where(e => e.State != EntityState.Detached)
                .Select(e => (e.Entity, e.State))
                .ToList();

            ChangeTracker.Clear();

            foreach (var (entity, state) in snapshot)
            {
                try
                {
                    Entry(entity).State = state;
                }
                catch (Exception)
                {
                    // One row that cannot be attached again must not abort the repair; it is lost,
                    // and the warning above names the cause.
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger?.LogWarning("[Sub Scribe-DB] the change tracker could not be rebuilt ({Msg})", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// How many writes may accumulate before a batch commits on its own.
    /// <para>
    /// The floor exists so the change tracker never walks an unbounded set: Entity Framework runs
    /// change detection over every tracked row on each <c>SaveChanges</c>, and the row cache holds
    /// whole areas. On the largest install measured (11 153 embedded rows, 1 201 media rows) 500
    /// keeps that walk cheap while collapsing a 14 000-write scan into roughly 28 commits.
    /// </para>
    /// </summary>
    public const int BatchFloor = 500;

    private int _batchDepth;
    private int _pendingWrites;

    /// <summary>
    /// Opens a batch: writes made until <see cref="EndBatch"/> accumulate and commit together
    /// instead of one transaction per row.
    /// <para>
    /// WHY this exists: a single row used to cost one commit, and a commit is a platter sync.
    /// Measured live (08.10.2026): a library scan issued 9 644 writes at a flat 45 ms each — 496 s
    /// of wall time for a pass that changed nothing. The upload and download pipelines write through
    /// the same path, so the cost was never the seeder's alone.
    /// </para>
    /// <para>
    /// READ-AFTER-WRITE IS PRESERVED WITHOUT THE COMMIT: readers go through the per-area row cache,
    /// which the areas maintain in place, so a value written a moment ago is visible to this context
    /// before it is durable. What changes is when the FILE receives it — an independent context (the
    /// config page holds one) sees it at the next flush, up to <see cref="BatchFloor"/> rows later.
    /// </para>
    /// <para>
    /// Callers pair this with <see cref="EndBatch"/> in a <c>finally</c>: an exception that skips the
    /// close would drop the accumulated writes, where the unbatched store had already persisted each
    /// one.
    /// </para>
    /// </summary>
    public void BeginBatch() => _batchDepth++;

    /// <summary>
    /// Closes a batch and commits what it accumulated. Nested batches commit at the outermost close,
    /// so a caller opening one inside another cannot flush a half-finished unit of work.
    /// </summary>
    public void EndBatch()
    {
        if (_batchDepth > 0)
        {
            _batchDepth--;
        }

        if (_batchDepth == 0)
        {
            FlushPending();
        }
    }

    /// <summary>
    /// Persists one write, or accumulates it while a batch is open. The areas call this instead of
    /// <c>SaveChanges</c> directly, so the batching decision lives in one place.
    /// </summary>
    public void RequestSave()
    {
        _pendingWrites++;
        if (_batchDepth == 0 || _pendingWrites >= BatchFloor)
        {
            FlushPending();
        }
    }

    /// <summary>
    /// Commits accumulated writes now. Called at the end of a batch and at every point whose answer
    /// comes from the FILE rather than the row cache: area row counts, footprint, checkpoint,
    /// compaction and disposal.
    /// </summary>
    public void FlushPending()
    {
        if (_pendingWrites == 0)
        {
            return;
        }

        _pendingWrites = 0;
        SaveChanges();
    }

    /// <summary>Area 0: the single compatibility row.</summary>
    public DbSet<MetaEntity> MetaRows => Set<MetaEntity>();

    /// <summary>Area 1: OSHash cache.</summary>
    public DbSet<OshashEntity> OshashRows => Set<OshashEntity>();

    /// <summary>Area 2: video files.</summary>
    public DbSet<MediaEntity> MediaRows => Set<MediaEntity>();

    /// <summary>Area 2: embedded subtitle tracks.</summary>
    public DbSet<EmbedTrackEntity> EmbedRows => Set<EmbedTrackEntity>();

    /// <summary>Area 3: sidecar subtitle files.</summary>
    public DbSet<SidecarEntity> SidecarRows => Set<SidecarEntity>();

    /// <summary>Area 4: burned download candidates.</summary>
    public DbSet<RejectedCandidateEntity> RejectedRows => Set<RejectedCandidateEntity>();

    /// <summary>Retry and fail-budget counters.</summary>
    public DbSet<CounterEntity> CounterRows => Set<CounterEntity>();

    /// <summary>Pipeline run bookkeeping.</summary>
    public DbSet<PipelineRunEntity> RunRows => Set<PipelineRunEntity>();

    /// <summary>F-M542: TMDb answers, so a restart does not re-ask the same questions.</summary>
    public DbSet<TmdbAnswerEntity> TmdbAnswerRows => Set<TmdbAnswerEntity>();

    /// <summary>Area 5: cumulative GUI counters.</summary>
    public DbSet<StatusStatsEntity> StatusRows => Set<StatusStatsEntity>();

    /// <summary>Area 6: the last run of each worker.</summary>
    public DbSet<WorkerRunEntity> WorkerRunRows => Set<WorkerRunEntity>();

    /// <summary>The published season packs — one row per season, language and variant (F-M390).</summary>
    public DbSet<PackEntity> PackRows => Set<PackEntity>();

    /// <summary>F-M601: the aired episode numbers per season, so a restart does not re-ask IMDb/TMDb.</summary>
    public DbSet<AiredSeasonEntity> AiredSeasonRows => Set<AiredSeasonEntity>();

    /// <summary>F-M604: the stored pack verdicts with the signature of the members they were made for.</summary>
    public DbSet<PackSeasonEntity> PackSeasonRows => Set<PackSeasonEntity>();

    /// <summary>F-M605: the remembered refusals of the sources, per track and content hash.</summary>
    public DbSet<SourceRefusalEntity> SourceRefusalRows => Set<SourceRefusalEntity>();

    /// <summary>F-M608: the titles whose creation keeps failing, and the ones closed for good.</summary>
    public DbSet<TitleFailureEntity> TitleFailureRows => Set<TitleFailureEntity>();

    /// <summary>
    /// Sets the pragmas this store runs with. Called once per context, right after the schema exists.
    /// <para>
    /// <c>journal_mode=WAL</c> is set EXPLICITLY, because it is not the default: SQLite and Entity
    /// Framework both start in the rollback-journal mode (<c>delete</c>) — verified by reading
    /// <c>PRAGMA journal_mode</c> back on a fresh EF-created file, which answered <c>delete</c>. WAL
    /// is what this store wants: the plugin has one writer (a scheduled task, serialised by the
    /// global run lock) and several readers (the configuration page polls every 10 seconds, the
    /// diagnostics endpoint walks four areas), and in WAL a reader never blocks the writer or sees a
    /// half-written state.
    /// </para>
    /// <para>
    /// The mode is stored IN the file, so this is a one-time change per database. It is also the
    /// reason the reset and restore paths must checkpoint before they copy the file: in WAL the
    /// committed writes sit in the side journal until a checkpoint, and a copy that ignores it is a
    /// backup missing everything since the last one.
    /// </para>
    /// <para>
    /// <c>busy_timeout</c> covers the one case the in-process gate cannot: two CONTEXTS, or a
    /// second process, over the same file. SQLite then answers "database is locked" immediately;
    /// the timeout makes it wait instead of failing a scheduled run.
    /// </para>
    /// </summary>
    public void ConfigurePragmas()
    {
        Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
        Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
    }

    /// <summary>
    /// Reads the journal mode the FILE is actually in.
    /// </summary>
    /// <returns>The mode string, e.g. <c>wal</c> or <c>delete</c>.</returns>
    public string ReadJournalMode()
    {
        using var cn = (SqliteConnection)Database.GetDbConnection();
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
    }

    /// <summary>
    /// Row counts per area, for the diagnostics endpoint and the compaction line.
    /// </summary>
    /// <returns>One entry per area with its row count.</returns>
    public List<(string Area, long Rows)> GetAreaRowCounts()
        => new()
        {
            ("meta", MetaRows.LongCount()),
            ("oshashes", OshashRows.LongCount()),
            ("media", MediaRows.LongCount()),
            ("embeds", EmbedRows.LongCount()),
            ("sidecars", SidecarRows.LongCount()),
            ("rejected_candidates", RejectedRows.LongCount()),
            ("counters", CounterRows.LongCount()),
            ("runs", RunRows.LongCount()),
            ("status_stats", StatusRows.LongCount()),
            ("worker_runs", WorkerRunRows.LongCount()),
        };

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // WAL so a reader never blocks a writer; the plugin has one writer and several readers
        // (config page, diagnostics, scheduled tasks) on the same file.
        // Pooling OFF, deliberately. The plugin's reset and restore routes are file-level
        // operations: they dispose the context, copy a file over the database and open a fresh
        // context. With pooling ON the physical connection is not closed on Dispose — it returns
        // to the pool holding the file handle and its WAL index — so the copy does not take
        // effect and the reopened context reads the OLD file. Measured, in a probe that mirrors
        // both paths: pooling ON → 2 of 3 rows restored and a mutated value still reads 999;
        // pooling OFF → 3 of 3 and the backed-up value. The cost is one connection open per
        // context, and the plugin creates one context per process.
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        optionsBuilder.UseSqlite(cs, sqlite =>
        {
            // The plugin schema is created by EnsureCreated and owned by the plugin alone, so no
            // migration assembly is involved.
            sqlite.CommandTimeout(30);
        });
        optionsBuilder.EnableSensitiveDataLogging(false);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MetaEntity>(e =>
        {
            e.ToTable("meta");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<OshashEntity>(e =>
        {
            e.ToTable("oshashes");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<MediaEntity>(e =>
        {
            e.ToTable("media");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ImdbId);
            e.HasIndex(x => x.TmdbId);
            e.HasIndex(x => x.Path);
            e.HasIndex(x => x.JellyfinItemId);
        });

        modelBuilder.Entity<EmbedTrackEntity>(e =>
        {
            e.ToTable("embeds");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.MediaHash);
            e.HasIndex(x => x.ContentHash);
            e.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<SidecarEntity>(e =>
        {
            e.ToTable("sidecars");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ContentHash);
            e.HasIndex(x => x.MediaHash);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.Language);
        });

        modelBuilder.Entity<RejectedCandidateEntity>(e =>
        {
            e.ToTable("rejected_candidates");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ItemId);
            e.HasIndex(x => x.SubdlId);
        });

        modelBuilder.Entity<CounterEntity>(e =>
        {
            e.ToTable("counters");
            // Key, not the numeric Id: the previous engine's auto id is what made an upsert unable
            // to find the row it meant to update (F-M194). The business key is the identity.
            e.HasKey(x => x.Key);
            e.HasIndex(x => x.Expires);
        });

        modelBuilder.Entity<PipelineRunEntity>(e =>
        {
            e.ToTable("runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => x.Started);
        });

        modelBuilder.Entity<StatusStatsEntity>(e =>
        {
            e.ToTable("status_stats");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<WorkerRunEntity>(e =>
        {
            e.ToTable("worker_runs");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<PackEntity>(e =>
        {
            e.ToTable("packs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ImdbId);
            e.HasIndex(x => x.Season);
        });

        // F-M601: explicit name, so the table created by EnsureAiredSeasonsTable is the one EF reads.
        modelBuilder.Entity<AiredSeasonEntity>(e =>
        {
            e.ToTable("aired_seasons");
            e.HasKey(x => x.Id);
        });

        // F-M608: and for the title ladder.
        modelBuilder.Entity<TitleFailureEntity>(e =>
        {
            e.ToTable("title_failures");
            e.HasKey(x => x.Id);
        });

        // F-M605: and for the remembered source refusals.
        modelBuilder.Entity<SourceRefusalEntity>(e =>
        {
            e.ToTable("source_refusals");
            e.HasKey(x => x.Id);
        });

        // F-M604: same contract for the pack verdicts.
        modelBuilder.Entity<PackSeasonEntity>(e =>
        {
            e.ToTable("pack_seasons");
            e.HasKey(x => x.Id);
        });
    }
}

/// <summary>
/// One area of the store, reached by the same verbs the previous engine offered —
/// <c>FindById</c>, <c>FindAll</c>, <c>Find</c>, <c>Upsert</c>, <c>DeleteMany</c>, <c>Exists</c>,
/// <c>Count</c>.
/// <para>
/// Why the predicates are evaluated in memory rather than handed to Entity Framework as SQL: the
/// registries depend on that behaviour and say so. <c>QaFailTracker</c> compares languages with
/// <c>String.Equals(..., StringComparison.OrdinalIgnoreCase)</c>, which no provider can translate,
/// and the stored values are the plugin's own strings — a provider that translates the same
/// comparison into SQL <c>LIKE</c> changes which rows match. A silent difference in matching is
/// exactly the class of bug the registry code was written to avoid, so the comparison stays where it
/// was: in LINQ-to-objects.
/// </para>
/// <para>
/// That is affordable because these areas are small — measured on a live install: 117 rows across
/// all ten areas, the largest being 86 embedded tracks. Rows are read once per area, kept for the
/// lifetime of the context, and invalidated on write, so a read costs a memory walk and a write one
/// round trip.
/// </para>
/// </summary>
/// <typeparam name="TEntity">The area's row type.</typeparam>
public sealed class ScribeSet<TEntity>
    where TEntity : class
{
    private readonly ScribeSqliteContext _db;
    private readonly Func<TEntity, string> _keyText;
    private bool _dirty = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScribeSet{TEntity}"/> class.
    /// </summary>
    /// <param name="db">The owning context.</param>
    /// <param name="keyText">Reads the business key as text, for in-memory lookup.</param>
    public ScribeSet(ScribeSqliteContext db, Func<TEntity, string> keyText)
    {
        _db = db;
        _keyText = keyText;
    }

    /// <summary>Every row of the area.</summary>
    /// <returns>The rows.</returns>
    public IEnumerable<TEntity> FindAll()
    {
        lock (_db.Gate)
        {
            return Rows().ToList();
        }
    }

    /// <summary>Rows matching a predicate, evaluated in memory.</summary>
    /// <param name="predicate">The condition.</param>
    /// <returns>The matching rows.</returns>
    public IEnumerable<TEntity> Find(Expression<Func<TEntity, bool>> predicate)
    {
        var compiled = predicate.Compile();
        lock (_db.Gate)
        {
            return Rows().Where(compiled).ToList();
        }
    }

    /// <summary>One row by its business key, or null.</summary>
    /// <param name="id">Business key.</param>
    /// <returns>The row, or null.</returns>
    public TEntity? FindById(string id)
    {
        lock (_db.Gate)
        {
            return Rows().FirstOrDefault(r => string.Equals(_keyText(r), id, StringComparison.Ordinal));
        }
    }

    /// <summary>The first row matching a predicate, or null.</summary>
    /// <param name="predicate">The condition.</param>
    /// <returns>The row, or null.</returns>
    public TEntity? FindOne(Expression<Func<TEntity, bool>> predicate)
    {
        var compiled = predicate.Compile();
        lock (_db.Gate)
        {
            return Rows().FirstOrDefault(compiled);
        }
    }

    /// <summary>Whether any row matches.</summary>
    /// <param name="predicate">The condition.</param>
    /// <returns>True when at least one row matches.</returns>
    public bool Exists(Expression<Func<TEntity, bool>> predicate)
    {
        var compiled = predicate.Compile();
        lock (_db.Gate)
        {
            return Rows().Any(compiled);
        }
    }

    /// <summary>Number of rows in the area.</summary>
    /// <returns>The row count.</returns>
    public int Count()
    {
        lock (_db.Gate)
        {
            return Rows().Count;
        }
    }

    /// <summary>Number of rows matching a predicate.</summary>
    /// <param name="predicate">The condition.</param>
    /// <returns>The matching row count.</returns>
    public int Count(Expression<Func<TEntity, bool>> predicate)
    {
        var compiled = predicate.Compile();
        lock (_db.Gate)
        {
            return Rows().Count(compiled);
        }
    }

    /// <summary>Insert or update one row, matched by its business key.</summary>
    /// <param name="entity">The row.</param>
    public void Upsert(TEntity entity)
    {
        lock (_db.Gate)
        {
            UpsertCore(entity);
        }
    }

    private void UpsertCore(TEntity entity)
    {
        var key = _keyText(entity);

        // F-M514: OHNE SCHLUESSEL GIBT ES KEINE ZEILE — und ein Schreibversuch darf den Store nicht
        // vergiften. Eine Zeile ohne Geschäftsschlüssel kann Entity Framework nicht verfolgen; seit
        // EF Core 10 bleibt sie danach als Detached im Change Tracker stehen, und JEDES spätere
        // SaveChanges dieses Kontexts wirft „Unexpected entry.EntityState: Detached". Deshalb wird
        // sie hier gar nicht erst angehängt: warnen, nicht schreiben. Die Regel gilt für JEDE Area
        // und damit für jeden Worker, weil sie im gemeinsamen Set steht, nicht am Aufrufer.
        if (string.IsNullOrEmpty(key))
        {
            _db.Logger?.LogWarning(
                "[Sub Scribe-DB] {Area}: a row without a business key was not written (F-M514)",
                typeof(TEntity).Name);
            return;
        }

        var existing = Rows().FirstOrDefault(r => string.Equals(_keyText(r), key, StringComparison.Ordinal));
        if (existing == null)
        {
            // Attach FIRST, cache second: a rejected attach that lands in the cache would leave a
            // row behind that no table holds and no key can address.
            _db.Add(entity);
            _rowCache.Add(entity);
        }
        else if (!ReferenceEquals(existing, entity))
        {
            _db.Entry(existing).CurrentValues.SetValues(entity);

            // Keep the cache pointing at the instance that now holds the new values, so a read
            // straight after the write sees them without a reload from disk.
            var at = _rowCache.IndexOf(existing);
            if (at >= 0 && !ReferenceEquals(entity, existing))
            {
                _rowCache[at] = existing;
            }
        }

        _db.RequestSave();

        // The cache is maintained IN PLACE rather than dropped. Marking it dirty here would make a
        // write cost a full table read, which turns a run's writes into quadratic work: a refresh
        // that writes 3000 embedded rows would re-read the whole area 3000 times. Measured on the
        // 117-row live store the difference is invisible; on a large library it is the difference
        // between one pass and thousands.
    }

    /// <summary>Update one row, matched by its business key. Alias of <see cref="Upsert"/>.</summary>
    /// <param name="entity">The row.</param>
    public void Update(TEntity entity) => Upsert(entity);

    /// <summary>
    /// Update many rows, each matched by its business key.
    /// <para>
    /// The previous engine accepted a list here and callers rely on it — the reset path collects the
    /// rows it cleared and persists exactly those, and the refresh path does the same for sidecar
    /// rows whose forced flag it just stated. Without this overload the batch would have to be
    /// written row by row at each call site, which is how one of them silently stops persisting.
    /// </para>
    /// </summary>
    /// <param name="entities">The rows.</param>
    public void Update(IEnumerable<TEntity> entities)
    {
        lock (_db.Gate)
        {
            // ONE SaveChanges for the batch. UpsertCore saves per row, which turns a reset that
            // clears 3000 markers into 3000 round trips; the batch is what the callers actually mean,
            // and the reset path persists a list precisely so it is a single write.
            var any = false;
            foreach (var e in entities)
            {
                var key = _keyText(e);
                if (string.IsNullOrEmpty(key))
                {
                    // F-M514: see UpsertCore — a keyless row is not a row, and attaching it would
                    // leave a Detached entry that breaks every later write of this context.
                    _db.Logger?.LogWarning(
                        "[Sub Scribe-DB] {Area}: a row without a business key was not written (F-M514)",
                        typeof(TEntity).Name);
                    continue;
                }

                any = true;
                var existing = Rows().FirstOrDefault(r => string.Equals(_keyText(r), key, StringComparison.Ordinal));
                if (existing == null)
                {
                    _db.Add(e);
                    _rowCache.Add(e);
                }
                else if (!ReferenceEquals(existing, e))
                {
                    _db.Entry(existing).CurrentValues.SetValues(e);
                }

                // When the caller passed the SAME instance the cache handed out, it mutated that
                // tracked instance in place and there is nothing to copy — the change is visible to
                // Entity Framework's change tracker, and SaveChanges is what persists it. Skipping
                // the save in that case is exactly the F-M192 defect: the reset reported success
                // while the rows kept their markers, because the marked list was never written.
            }

            if (any)
            {
                _db.RequestSave();
            }
        }
    }

    /// <summary>Insert many rows in one transaction, for the import path.</summary>
    /// <param name="entities">The rows.</param>
    public void InsertBulk(IEnumerable<TEntity> entities)
    {
        lock (_db.Gate)
        {
            foreach (var e in entities)
            {
                // F-M514: a keyless row is not a row — see UpsertCore.
                if (string.IsNullOrEmpty(_keyText(e)))
                {
                    _db.Logger?.LogWarning(
                        "[Sub Scribe-DB] {Area}: a row without a business key was not written (F-M514)",
                        typeof(TEntity).Name);
                    continue;
                }

                _db.Add(e);
                _rowCache.Add(e);
            }

            _db.RequestSave();
        }
    }

    /// <summary>Delete the row with this business key.</summary>
    /// <param name="id">Business key.</param>
    /// <returns>True when a row was deleted.</returns>
    public bool Delete(string id)
    {
        lock (_db.Gate)
        {
            var existing = Rows().FirstOrDefault(r => string.Equals(_keyText(r), id, StringComparison.Ordinal));
            if (existing == null)
            {
                return false;
            }

            _db.Remove(existing);
            _db.RequestSave();
            _rowCache.Remove(existing);
            return true;
        }
    }

    /// <summary>Delete every row matching a predicate.</summary>
    /// <param name="predicate">The condition.</param>
    /// <returns>The number of rows deleted.</returns>
    public int DeleteMany(Expression<Func<TEntity, bool>> predicate)
    {
        var compiled = predicate.Compile();
        lock (_db.Gate)
        {
            var doomed = Rows().Where(compiled).ToList();
            if (doomed.Count == 0)
            {
                return 0;
            }

            _db.RemoveRange(doomed);
            _db.RequestSave();
            foreach (var d in doomed)
            {
                _rowCache.Remove(d);
            }

            return doomed.Count;
        }
    }

    /// <summary>Delete every row of the area.</summary>
    public void DeleteAll()
    {
        lock (_db.Gate)
        {
            _db.RemoveRange(Rows().ToList());
            _db.RequestSave();
            _rowCache.Clear();
        }
    }

    private List<TEntity> Rows()
    {
        if (_dirty)
        {
            // TRACKED, deliberately — the identity map IS the write path here. An entity handed out
            // by FindById and then mutated by its caller is the instance SaveChanges will write, and
            // a second read returns that same instance rather than a detached copy that would be
            // re-attached and throw. These areas are small by construction (117 rows measured), so
            // holding them for the context's lifetime is the cheap side of the trade.
            _rowCache = _db.Set<TEntity>().ToList();
            _dirty = false;
        }

        return _rowCache;
    }

    private List<TEntity> _rowCache = new();
}
