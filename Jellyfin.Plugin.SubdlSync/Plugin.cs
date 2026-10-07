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
using System.Xml.Linq;
using Jellyfin.Plugin.SubdlScribe.Data;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

namespace Jellyfin.Plugin.SubdlScribe;

/// <summary>
/// Main plugin entry point for SubDL Scribe (upload + download pipelines).
/// </summary>
public class Plugin : BasePlugin<Configuration.PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILoggerFactory loggerFactory)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _loggerFactory = loggerFactory;
        DiceSchedulerConfig();

        // (rework 25.09.2026): clear any leftover run-lock block file. After a restart
        // no previous run can still be alive, so a file found here is always a corpse —
        // without this, the stale-detection path has to discover that the hard way.
        Pipeline.PipelineRunLock.ClearOnStartup(
            _loggerFactory?.CreateLogger("SubDL-Lock")
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        // Stats period start defaults to plugin install/first start —
        // the GUI "since" always shows a real date until "Reset statistics" re-sets it.
        // Migrated to the database (25.09.2026): counters and the period start now live in the
        // data file, so a database reset resets them too. The old XML values are adopted once.
        MigrateStatusCounters();
    }

    /// <summary>
    /// One-time adoption of the cumulative counters from the configuration XML into the database.
    /// <para>
    /// Runs only while the database has no stats row yet. The XML values are the only copy of the
    /// pre-migration totals, so they are taken over verbatim; afterwards the XML fields are ignored
    /// (and left in place, so a downgrade still finds them).
    /// </para>
    /// </summary>
    private void MigrateStatusCounters()
    {
        try
        {
            if (Configuration.StatusCountersMigratedToDb)
            {
                return; // adopted once already — never re-import after a database reset
            }

            var db = SharedDbContext;
            if (db.StatusStats.FindById("status") == null)
            {
                var seeded = new Data.StatusStatsEntity
                {
                    Id = "status",
                    Uploaded = Configuration.StatusUploaded,
                    Downloaded = Configuration.StatusDownloaded,
                    SinceUtc = Configuration.StatusStatsSinceUtc ?? DateTime.UtcNow,
                    Updated = DateTime.UtcNow
                };
                db.StatusStats.Upsert(seeded);
                LogUtil.Detail(_loggerFactory?.CreateLogger<Plugin>(), 
                    "[SubDL] status counters migrated to the database: {Up} uploaded, {Down} downloaded since {Since:yyyy-MM-dd}.",
                    seeded.Uploaded, seeded.Downloaded, seeded.SinceUtc);
            }

            // Flag regardless of whether a row had to be created: after this point the XML values are
            // a frozen historical snapshot and must never seed the data file again.
            Configuration.StatusCountersMigratedToDb = true;
            SavePluginState();
        }
        catch (Exception ex)
        {
            _loggerFactory?.CreateLogger<Plugin>().LogError(ex,
                "[SubDL] failed to migrate status counters into the database.");
        }
    }

    /// <summary>
    /// Reads the cumulative GUI counters from the database. Creates the row on first use so the
    /// caller always gets a valid object instead of a null check.
    /// </summary>
    /// <returns>The stored counters (never null).</returns>
    public Data.StatusStatsEntity GetStatusStats()
    {
        var db = SharedDbContext;
        var row = db.StatusStats.FindById("status");
        if (row == null)
        {
            row = new Data.StatusStatsEntity { Id = "status", SinceUtc = DateTime.UtcNow, Updated = DateTime.UtcNow };
            db.StatusStats.Upsert(row);
        }

        return row;
    }

    /// <summary>
    /// Adds finished work to the cumulative counters. Called once per run with the run's own summary
    /// numbers, so the counters describe what actually happened rather than what was attempted.
    /// </summary>
    /// <param name="uploaded">Subtitles uploaded in this run.</param>
    /// <param name="downloaded">Subtitles downloaded in this run.</param>
    /// <param name="typeCorrected">F-M218: items typed by the file name instead of Jellyfin.</param>
    /// <param name="tmdbYearMisses">F-M218: TMDb searches that needed the year filter dropped.</param>
    /// <param name="rejectedDownload">F-M286: download candidates fetched and thrown away.</param>
    /// <param name="rejectedUpload">F-M286: upload candidates discarded from the upload.</param>
    /// <param name="fittedToAudio">F-M308: downloaded subtitles fitted to their audio track.</param>
    public void AddStatusCounters(
        long uploaded,
        long downloaded,
        long typeCorrected = 0,
        long tmdbYearMisses = 0,
        long rejectedDownload = 0,
        long rejectedUpload = 0,
        long fittedToAudio = 0)
    {
        if (uploaded == 0 && downloaded == 0 && typeCorrected == 0 && tmdbYearMisses == 0
            && rejectedDownload == 0 && rejectedUpload == 0 && fittedToAudio == 0)
        {
            return; // nothing happened — do not touch the row (keeps Updated meaningful)
        }

        var db = SharedDbContext;
        var row = db.StatusStats.FindById("status")
            ?? new Data.StatusStatsEntity { Id = "status", SinceUtc = DateTime.UtcNow };
        row.Uploaded += uploaded;
        row.Downloaded += downloaded;
        row.TypeCorrectedByFileName += typeCorrected;
        row.TmdbYearFilterMisses += tmdbYearMisses;
        row.RejectedDownload += rejectedDownload;
        row.RejectedUpload += rejectedUpload;
        row.FittedToAudio += fittedToAudio; // F-M308
        row.Updated = DateTime.UtcNow;
        db.StatusStats.Upsert(row);
    }

    /// <summary>
    /// Zeroes the cumulative counters and restarts the period ("Reset statistics").
    /// </summary>
    /// <returns>The fresh, zeroed counters.</returns>
    public Data.StatusStatsEntity ResetStatusStats()
    {
        var db = SharedDbContext;
        var row = db.StatusStats.FindById("status") ?? new Data.StatusStatsEntity { Id = "status" };
        row.Uploaded = 0;
        row.Downloaded = 0;
        row.TypeCorrectedByFileName = 0; // F-M218
        row.TmdbYearFilterMisses = 0;
        row.RejectedDownload = 0;
        row.RejectedUpload = 0;
        row.FittedToAudio = 0; // F-M308
        row.SinceUtc = DateTime.UtcNow;
        row.Updated = DateTime.UtcNow;
        db.StatusStats.Upsert(row);
        return row;
    }

    /// <summary>
    /// F-M48 (user decision 08.09.2026): dice the per-installation random schedule
    /// anchors ONCE — only when the stored value is empty. Persisted in the plugin
    /// config; survives restarts. New install = new dice; config edits never re-roll.
    /// </summary>
    private void DiceSchedulerConfig()
    {
        var c = Configuration;
        bool changed = false;

        if (string.IsNullOrWhiteSpace(c.RandomDailyTime))
        {
            c.RandomDailyTime = $"{Random.Shared.Next(0, 24):D2}:{Random.Shared.Next(0, 60):D2}";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(c.RandomWeeklyTime))
        {
            c.RandomWeeklyTime = $"{Random.Shared.Next(1, 8)} {Random.Shared.Next(0, 24):D2}:{Random.Shared.Next(0, 60):D2}";
            changed = true;
        }

        if (c.RandomMonthlyDay <= 0 || string.IsNullOrWhiteSpace(c.RandomMonthlyTime))
        {
            c.RandomMonthlyDay = Random.Shared.Next(1, 29);
            c.RandomMonthlyTime = $"{Random.Shared.Next(0, 24):D2}:{Random.Shared.Next(0, 60):D2}";
            changed = true;
        }

        // Prune + oshash get their own anchors (breaking point fix —
        // shared weekly anchor fired refetch + prune + rehash together).
        if (string.IsNullOrWhiteSpace(c.RandomPruneTime))
        {
            c.RandomPruneTime = $"{Random.Shared.Next(1, 8)} {Random.Shared.Next(0, 24):D2}:{Random.Shared.Next(0, 60):D2}";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(c.RandomOshashTime))
        {
            c.RandomOshashTime = $"{Random.Shared.Next(1, 8)} {Random.Shared.Next(0, 24):D2}:{Random.Shared.Next(0, 60):D2}";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(c.RandomPostprocessTime))
        {
            c.RandomPostprocessTime = $"{Random.Shared.Next(1, 8)} {Random.Shared.Next(0, 24):D2}:{Random.Shared.Next(0, 60):D2}";
            changed = true;
        }

        if (changed)
        {
            SavePluginState();
        }
    }

    /// <summary>
    /// Fields this plugin maintains itself (scheduler anchors, statistics). Everything else in
    /// the configuration belongs to the user.
    /// </summary>
    // F-M201: the plugin persists ONLY these fields, patched into the file on disk; the rest of the
    // document is preserved byte for byte.
    private static readonly string[] PluginOwnedFields =
    [
        "RandomDailyTime", "RandomWeeklyTime", "RandomMonthlyTime", "RandomMonthlyDay",
        "RandomPruneTime", "RandomOshashTime", "RandomPostprocessTime",
        "StatusUploaded", "StatusDownloaded", "StatusStatsSinceUtc", "StatusCountersMigratedToDb"
    ];

    /// <summary>
    /// Persists the fields this plugin owns WITHOUT touching user settings.
    /// <para>
    /// Why not <c>SaveConfiguration</c>: Jellyfin has no partial write — <c>SaveConfiguration</c>
    /// serialises the WHOLE in-memory configuration object over the file. Measured 25.09.2026 on
    /// the test instance: after an unclean restart the in-memory state was empty, the constructor
    /// saw the emptiness as "fresh install", re-diced the anchors and called SaveConfiguration —
    /// which wrote that emptiness to disk and destroyed Username, Password, ApiKey and
    /// SelectedLibraries. Silent, unrecoverable from the log (no warning, no error).
    /// <para>
    /// Here the file on disk is the source of truth for user settings: it is re-read, the
    /// plugin-owned fields are replaced in that document only, and the result is written back.
    /// An empty in-memory state can therefore no longer erase credentials, by construction.
    /// </para>
    /// <para>
    /// The write itself is a temp file plus rename, so an interrupted save can never replace a
    /// valid file with a truncated one.
    /// </para>
    /// </summary>
    public void SavePluginState()
    {
        try
        {
            string path = ConfigurationFilePath;
            var mine = new Dictionary<string, string?>(StringComparer.Ordinal);

            var cfg = Configuration;
            mine["RandomDailyTime"] = cfg.RandomDailyTime;
            mine["RandomWeeklyTime"] = cfg.RandomWeeklyTime;
            mine["RandomMonthlyTime"] = cfg.RandomMonthlyTime;
            mine["RandomMonthlyDay"] = cfg.RandomMonthlyDay.ToString(CultureInfo.InvariantCulture);
            mine["RandomPruneTime"] = cfg.RandomPruneTime;
            mine["RandomOshashTime"] = cfg.RandomOshashTime;
            mine["RandomPostprocessTime"] = cfg.RandomPostprocessTime;
            mine["StatusUploaded"] = cfg.StatusUploaded.ToString(CultureInfo.InvariantCulture);
            mine["StatusDownloaded"] = cfg.StatusDownloaded.ToString(CultureInfo.InvariantCulture);
            mine["StatusStatsSinceUtc"] = cfg.StatusStatsSinceUtc?.ToUniversalTime()
                .ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
            mine["StatusCountersMigratedToDb"] = cfg.StatusCountersMigratedToDb ? "true" : "false";

            PatchPluginStateFile(path, mine);
        }
        catch (Exception ex)
        {
            _loggerFactory?.CreateLogger<Plugin>().LogError(ex,
                "[SubDL] failed to persist plugin state (user settings left untouched).");
        }
    }

    /// <summary>
    /// Replaces the given fields inside the configuration file, leaving every other element
    /// exactly as it is on disk. Writes to a temp file and renames, so an interrupted write
    /// cannot replace a valid file with a partial one.
    /// </summary>
    /// <param name="path">Path of the plugin configuration file.</param>
    /// <param name="fields">Field name to value. Null values are skipped, not written as empty.</param>
    /// <returns>True when the file was written.</returns>
    public static bool PatchPluginStateFile(string path, IReadOnlyDictionary<string, string?> fields)
    {
        // Missing file: write only the plugin's own fields. Seeding a full document from the
        // in-memory object would be the very overwrite this class exists to prevent.
        var doc = File.Exists(path)
            ? XDocument.Load(path, LoadOptions.PreserveWhitespace)
            : new XDocument(new XElement("PluginConfiguration"));

        var root = doc.Root;
        if (root == null)
        {
            return false;
        }

        foreach (var kv in fields)
        {
            if (kv.Value == null)
            {
                continue;
            }

            var el = root.Element(kv.Key);
            if (el == null)
            {
                root.Add(new XElement(kv.Key, kv.Value));
            }
            else
            {
                el.Value = kv.Value;
            }
        }

        string tmp = path + ".tmp";
        doc.Save(tmp);
        File.Move(tmp, path, overwrite: true);
        return true;
    }

    /// <summary>
    /// Recreate the shared LiteDB context after a reset/restore.
    /// </summary>
    public void RecreateDbContext()
    {
        _sharedDbContext?.Dispose();
        _sharedDbContext = null;
        _sharedOshashCache = null;
        _sharedRegistry = null;
        _sharedWorkerRuns = null;
        var logger = _loggerFactory?.CreateLogger<SubdlDbContext>();
        _sharedDbContext = new SubdlDbContext(DataFolderPath, logger);
    }

    /// <inheritdoc />
    public override string Name => "SubDL Scribe";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("7d1c5a2e-3f4b-4d6e-9a8b-5c2f8e1d4a9b"); // matches build.yaml — stable plugin identity

    /// <summary>Gets the plugin description (shown in Dashboard → Plugins).</summary>
    /// <remarks>
    /// This string is compiled into the DLL and is what the plugin card renders — NOT
    /// build.yaml, which only feeds the catalog manifest. Keep it to two sentences: the
    /// card is a one-glance summary, not a feature list or an explanation of the id
    /// resolution design (that lives in F-M203/F-M219 and the README).
    /// </remarks>
    /// F-M220/F-M221: this string and build.yaml must hold the same text, under 260 characters, and
    /// must name both required keys. The card reads the DLL, not build.yaml.
    public override string Description => "SubDL Scribe brings SubDL.com to Jellyfin: it downloads missing subtitles for the languages and libraries you pick and uploads your own. Download is on by default; upload is off — enable at your choice. Requires a SubDL login and API Key plus a TMDb API Key.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    private static SubdlDbContext? _sharedDbContext;
    private static ILoggerFactory? _loggerFactory;

    /// <summary>Guards the lazy creation of the shared context.</summary>
    /// <para>
    /// Without this, two scheduled tasks starting in the same tick both see
    /// <c>_sharedDbContext == null</c> and each construct a SubdlDbContext over the same data
    /// file. LiteDB's BsonMapper is not thread-safe, so the concurrent EnsureIndexes() calls
    /// raced and one task died with
    /// <c>System.NotSupportedException: Member Id not found on BsonMapper for type
    /// EmbedTrackEntity</c> (measured 26.09.2026 at 08:21:09.081/.082, prune and OSHash firing
    /// in the same scheduler tick — the first occurrence in any log). The task itself is fine:
    /// started alone it completes normally.
    /// </para>
    // F-M211: the shared database context is created under a lock; two contexts over one data file
    // race the object mapper and one task is lost.
    private static readonly object SharedDbContextLock = new();

    /// <summary>
    /// Shared LiteDB context. Lazy-created on first use; all registries
    /// and scheduled tasks share this one instance.
    /// </summary>
    public SubdlDbContext SharedDbContext
    {
        get
        {
            if (_sharedDbContext == null)
            {
                lock (SharedDbContextLock)
                {
                    if (_sharedDbContext == null)
                    {
                        var logger = _loggerFactory?.CreateLogger<SubdlDbContext>();
                        _sharedDbContext = new SubdlDbContext(DataFolderPath, logger);

                        // Area 0: record which plugin and Jellyfin version last wrote this data file.
                        // Done once per context creation, not per run — an audit record that rewrites
                        // itself every cycle is a write amplifier and tells nobody anything.
                        _sharedDbContext.RecordVersions(Version?.ToString(), JellyfinVersionProbe());
                    }
                }
            }

            return _sharedDbContext;
        }
    }

    /// <summary>
    /// Jellyfin's own version, read from the loaded server assembly. Null when unavailable (unit
    /// test host, stripped build) — the caller stores null rather than inventing a value.
    /// <para>
    /// (25.09.2026, measured): the server assembly is named <c>jellyfin</c> (lower case, from
    /// jellyfin.dll) — <c>Jellyfin.Server</c> is only a NAMESPACE inside it, and there is no
    /// assembly by that name. The earlier probe therefore never matched and the recorded field
    /// was always null while the server was running normally. Verified against the live
    /// instance: <c>jellyfin.dll</c> reports Name=jellyfin, Version=12.1.0.0.
    /// </para>
    /// </summary>
    private static string? JellyfinVersionProbe()
    {
        // Names in order of preference; the entry assembly wins when Jellyfin is hosted differently.
        string[] candidates = ["jellyfin", "Jellyfin.Server", "Jellyfin.Server.Implementations"];

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var name in candidates)
        {
            var asm = assemblies.FirstOrDefault(a =>
                string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
            var version = asm?.GetName().Version?.ToString();
            if (!string.IsNullOrEmpty(version))
            {
                return version;
            }
        }

        // Last resort: the entry assembly, if it looks like the server.
        try
        {
            var entry = System.Reflection.Assembly.GetEntryAssembly();
            var entryName = entry?.GetName().Name;
            if (entryName != null && entryName.Contains("jellyfin", StringComparison.OrdinalIgnoreCase))
            {
                return entry?.GetName().Version?.ToString();
            }
        }
        catch
        {
            // fall through to null
        }

        return null;
    }

    private static Registry.OshashCache? _sharedOshashCache;

    /// <summary>
    /// F-M119 (user decision 14.09.2026): ONE shared OshashCache instance for
    /// the upload pipeline AND the scheduler-driven refresh task — backed by
    /// SharedDbContext so no JSON flush collisions can happen.
    /// </summary>
    public Registry.OshashCache SharedOshashCache => _sharedOshashCache ??= new Registry.OshashCache(SharedDbContext, _loggerFactory?.CreateLogger<Registry.OshashCache>());

    private static Registry.ContentHashRegistry? _sharedRegistry;

    /// <summary>
    /// F-M17y: shared content-hash registry used by the upload pipeline and the
    /// duplicate cleanup (postprocessing job, own schedule — F-M184).
    /// </summary>
    public Registry.ContentHashRegistry Registry => _sharedRegistry ??= new Registry.ContentHashRegistry(SharedDbContext, SharedOshashCache, _loggerFactory?.CreateLogger<Registry.ContentHashRegistry>());

    private static Registry.WorkerRunRegistry? _sharedWorkerRuns;

    /// <summary>
    /// The last run of each worker, stored in the data file (one row per worker).
    /// <para>
    /// Shared like the other registries, so the scheduled tasks and the configuration endpoint read
    /// the same rows. Recreated by <see cref="RecreateDbContext"/> after a reset/restore, because the
    /// area it reads is inside the data file that was just replaced.
    /// </para>
    /// </summary>
    public Registry.WorkerRunRegistry WorkerRuns => _sharedWorkerRuns ??= new Registry.WorkerRunRegistry(SharedDbContext, _loggerFactory?.CreateLogger<Registry.WorkerRunRegistry>());

    /// <summary>F-M17y: logger factory for cleanup tasks that run outside DI.</summary>
    public ILoggerFactory? LoggerFactory => _loggerFactory;

    /// <inheritdoc />
    public System.Collections.Generic.IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = "Jellyfin.Plugin.SubdlScribe.Configuration.configPage.html"
            },
            new PluginPageInfo
            {
                Name = Name + ".js",
                EmbeddedResourcePath = "Jellyfin.Plugin.SubdlScribe.Configuration.configPage.js"
            }
        ];
    }
}