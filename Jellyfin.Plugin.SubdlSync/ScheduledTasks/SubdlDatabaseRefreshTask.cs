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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SubdlScribe.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

namespace Jellyfin.Plugin.SubdlScribe.ScheduledTasks;

/// <summary>
/// The database refresh: reconciles the stored state with reality.
/// <para>
/// F-M94 (user decision 11.09.2026) started this as a prune: drop tracker state whose Jellyfin item
/// no longer exists, because a deleted movie left its retry/quota memories behind forever.
/// </para>
/// <para>
/// F-M234 (user decision 28.09.2026) widened it into a refresh. Deleting an item is only the
/// crudest way reality drifts from the database: a subtitle file can be deleted while its media
/// item stays, a download mark can claim "complete" for a language whose file is gone, and a
/// sidecar can vanish without anything else changing. Those cases were invisible — the stored
/// verdicts stayed, so the item was reported complete forever and only a file-system probe in the
/// running pipeline could ever notice. The refresh now verifies the FILE side of each verdict, not
/// only the item side.
/// </para>
/// FAIL-SAFE SEMANTICS (user requirement: "what happens when no access exists right now?"):
/// - "Unknown ≠ deleted": when the library manager or the filesystem is not reliably queryable,
///   the ENTIRE run deletes NOTHING. A missed refresh costs nothing; a false one would erase valid
///   retry/quota memories and live subtitle verdicts.
/// - Existence checks: LibraryManager.GetItemById → null counts as dead ONLY after a positive
///   probe proved the library responds (a live item resolves).
/// - File paths (oshash cache, subtitles) are only dropped when the file is gone AND its parent
///   directory still exists and lists cleanly — an offline/unmounted volume reports everything as
///   missing and must not nuke valid state (two-tier rule, now shared in SubtitlePresence).
/// - No TTL/age-based pruning here by design (decided 11.09.2026): age proves nothing about item
///   existence; the existence checks above are the truth source.
/// </summary>
public class SubdlDatabaseRefreshTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILogger<SubdlDatabaseRefreshTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubdlDatabaseRefreshTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSourceManager">Media source manager (for the embedded-track check, F-M258).</param>
    /// <param name="logger">Logger.</param>
    public SubdlDatabaseRefreshTask(ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager, ILogger<SubdlDatabaseRefreshTask> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "SubDL Scribe — Database Refresh";

    /// <inheritdoc />
    public string Key => "SubdlSyncDatabaseRefreshTask";

    /// <inheritdoc />
    public string Description => "Checks stored state against reality: removed media are cleaned up, a deleted subtitle loses its verdict, a vanished file drops its download mark. Compacts the database.";

    /// <inheritdoc />
    // F-M227: every task this plugin schedules reports ONE category, so the dashboard shows one
    // group instead of splitting the plugin's work.
    public string Category => "SubDL Scribe";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // (user decision 14.09.2026): the coordinator fires the weekly refresh as a scheduler job
        // on the diced weekly anchor (QueueScheduledTask keeps it visible + manually triggerable in
        // the dashboard). No default JF trigger anymore — a fixed JF timer here would double-fire.
        return [];
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            _logger.LogError("[SubDL-Refresh] Plugin instance missing — cannot run.");
            return;
        }

        plugin.WorkerRuns.Start("SubdlSyncDatabaseRefreshTask", Name);

        string dataDir;
        try
        {
            dataDir = plugin.DataFolderPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[SubDL-Refresh] DataFolderPath unavailable ({Message}) — refresh skipped.", ex.Message);
            return;
        }

        // F-M94h (user decision 11.09.2026): the refresh mutates the SAME registry files
        // (search/retry/idnotfound/qa/oshash JSON) that pipeline runs mutate — last-flush-wins
        // across instances could resurrect dropped entries or drop live updates. Take the single
        // global pipeline run lock and hold it exclusively; on overlap reschedule by
        // JobSpacingMinutes instead of waiting (rework 25.09.2026).
        if (!await PipelineRunLock.AcquireAsync(_logger, "refresh", cancellationToken).ConfigureAwait(false))
        {
            var spacing = JobSpacingMinutes();
            SubdlSchedulerCoordinator.Instance?.ScheduleRefreshFire(DateTime.UtcNow.AddMinutes(spacing));
            _logger.LogWarning("[SubDL-Refresh] Deferred — global run lock busy; re-fire scheduled in {Spacing} min.", spacing);
            // Lock busy is a DEFERRAL, not a switch-off: the work is pending and will retry, so the
            // light is yellow. Grey stays for "disabled" and "never run".
            plugin.WorkerRuns.Finish("SubdlSyncDatabaseRefreshTask", Name, Registry.WorkerRunRegistry.Outcome.Deferred, "lock busy — rescheduled");
            return;
        }

        try
        {
            // ---- Phase 0: fail-safe probes (unknown ≠ deleted) ----------------------
            // Library probe: resolve the FULL id set. If the library fails or returns nothing at
            // all, it is not queryable right now (DB locked, starting up) → skip everything.
            var allItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode },
                    Recursive = true,
                    Limit = 100_000 // Limit=0 returned 0 rows (live 21:58) — explicit cap instead
                };

                // GetItemIds is SYNCHRONOUS in JF 10.11 and returns IReadOnlyList<Guid> directly.
                foreach (var id in _libraryManager.GetItemIds(query))
                {
                    allItems.Add(id.ToString("D"));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[SubDL-Refresh] Library not queryable ({Message}) — refresh skipped.", ex.Message);
                return;
            }

            if (allItems.Count == 0)
            {
                _logger.LogWarning("[SubDL-Refresh] Library query returned 0 items — refresh skipped.");
                return;
            }

            progress.Report(20);

            int removedSearch = 0, removedRetry = 0, removedQa = 0, removedOshash = 0;
            int forgottenSidecars = 0, openFiles = 0, forgottenEmbeds = 0, backfilledForced = 0;
            int removedPrunedSubtitles = 0, removedPrunedMedia = 0;
            Exception? refreshException = null;
            // Filled by the compaction step below; stays empty when it compacted cleanly. Names the
            // rebuild fallback in the worker row, which otherwise reads as a clean run.
            string compactNote = string.Empty;
            try
            {
                bool ItemExists(string id) => allItems.Contains(id);
                var db = plugin.SharedDbContext;
                var registry = plugin.Registry;

                // ---- Phase 1: dead media/subtitle rows (item removed from Jellyfin) ----
                if (registry != null)
                {
                    // Counted, not discarded (user decision 01.10.2026): the tuple was thrown away
                    // with `_ =`, so Phase 1 could remove rows and the run summary would not
                    // mention it — a refresh that cleaned house looked like a refresh that found
                    // nothing.
                    (removedPrunedSubtitles, removedPrunedMedia) = registry.PruneDeadMediaAndSubtitles(allItems);
                }

                // ---- Phase 2: guid-keyed trackers ----
                var searchTracker = new Registry.DownloadSearchTracker(db);
                removedSearch = searchTracker.PruneDeadItems(ItemExists);

                var fileRetries = new Registry.FileRetryTracker(db);
                removedRetry = fileRetries.PruneDeadItems(ItemExists);

                var qaFails = new Registry.QaFailTracker(db);
                removedQa = qaFails.PruneDeadItems(ItemExists);

                progress.Report(50);

                // ---- Phase 3: oshash cache paths ----
                var oshash = new Registry.OshashCache(db);
                removedOshash = oshash.PruneStalePaths(collectRoots(oshash));

                progress.Report(65);

                // ---- Phase 4 (F-M234 A+C): vanished subtitle files ----
                // The item still exists, only the subtitle is gone. Until now nothing noticed: the
                // verdict row stayed "uploaded"/"rejected" and the download mark kept claiming the
                // language was settled, so the item was reported complete forever.
                //
                // Fail-safe FIRST, exactly like the oshash cache: every root the stored sidecar
                // paths live under must exist AND list cleanly. An unmounted volume reports every
                // path as missing, and forgetting live verdicts would be far worse than keeping a
                // stale one.
                if (registry != null)
                {
                    var roots = registry.GetSidecarRoots();
                    if (roots.Count == 0)
                    {
                        LogUtil.Normal(_logger, "[SubDL-Refresh] no stored subtitle paths to verify — file side skipped.");
                    }
                    else if (!Registry.SubtitlePresence.RootsUsable(roots))
                    {
                        _logger.LogWarning("[SubDL-Refresh] {N} subtitle root(s) not readable — file side skipped (no verdict forgotten).", roots.Count);
                    }
                    else
                    {
                        var vanished = registry.GetSidecarsMissingFromDisk();
                        forgottenSidecars = registry.ForgetSidecars(vanished);
                    }

                    // F-M283 (user decision 02.10.2026): there is no download mark to drop any more.
                    // What the refresh still does here is ASK the one question — which required files
                    // are open on this disk — through the same reader the pipeline and the seeder
                    // use, so all three components can never disagree. It reports and writes nothing,
                    // which is why the run cannot take work away from the pipeline.
                    openFiles = CountItemsWithOpenFiles(db, registry, allItems);
                }

                progress.Report(75);

                // ---- Phase 4b (F-M258): embedded rows that no longer match their file ----
                // A sidecar has a FILE, so phase 4 can probe it with File.Exists. An embedded track
                // has no file — its evidence is the stream list, and until now nothing checked it:
                // a row for a position that no longer exists (file replaced in place, streams
                // removed, language re-tagged) stayed forever, and the HI question (F-M254) read its
                // answer from a track that was gone. Nothing here deletes on suspicion: a file whose
                // streams cannot be read, or an item Jellyfin no longer resolves, is skipped.
                if (registry != null)
                {
                    // F-M284: rows written before the forced property existed read back as
                    // `false`, which would let a forced track pass as the film's dialogue and
                    // mark its language settled. Bring them up to date from their own names.
                    backfilledForced = BackfillForcedFlags(db);

                    forgottenEmbeds = ForgetStaleEmbeds(registry, allItems);
                }

                progress.Report(85);

                // ---- Phase 5: compact the file ----
                // Deleting rows frees pages inside the file but never releases them, and
                // the journal keeps every delete separately. Compact in the SAME run so the refresh
                // actually leaves a smaller store behind; housekeeping later would be forgotten.
                try
                {
                    var (before, after, _, outcome) = db.Compact();
                    switch (outcome)
                    {
                        case Data.CompactOutcome.Compacted:
                            LogUtil.Normal(_logger, "[SubDL-Refresh] data file compacted: {Before} -> {After} bytes.", before, after);
                            break;

                        case Data.CompactOutcome.Repaired:
                            // F-M237: the release failed, so the file was rewritten from its own
                            // rows. Reporting this as "compacted" would hide that the release
                            // broke; staying silent would hide that housekeeping worked again.
                            // The accepted path, not a defect (user decision 30.09.2026: "Kompaktierung
                            // werden wir nicht mehr lösen, dafür gibt es den rebuild") — but the worker
                            // row must say WHICH path ran, otherwise a fallback reads as a clean run.
                            compactNote = $"compaction failed — rebuilt from own rows ({before} -> {after} bytes)";
                            LogUtil.Normal(_logger, "[SubDL-Refresh] rebuild failed — the data file was rebuilt from its own rows: {Before} -> {After} bytes (previous state kept as one .bak).", before, after);
                            break;

                        case Data.CompactOutcome.Recovered:
                            // F-M236: the rebuild failed but the store was brought back. Reporting
                            // "compacted: X -> X" here would be a lie about the file, and staying
                            // silent would hide a broken rebuild.
                            compactNote = $"compaction failed — file not shrunk ({before} bytes stay)";
                            _logger.LogWarning(
                                "[SubDL-Refresh] compaction failed — the data file was NOT shrunk ({Before} bytes stay). The database was reopened and is usable; the next refresh retries.",
                                before);
                            break;

                        default:
                            compactNote = "compaction failed — data file could not be reopened";
                            _logger.LogWarning("[SubDL-Refresh] compaction failed and the data file could not be reopened — restart Jellyfin to recover the database.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // Already caught inside Compact(); belt-and-braces so a compaction problem cannot
                    // mark the refresh as failed after its real work succeeded.
                    compactNote = $"compaction skipped ({ex.Message})";
                    _logger.LogWarning("[SubDL-Refresh] compaction skipped ({Msg}).", ex.Message);
                }
            }
            catch (Exception ex)
            {
                refreshException = ex;
                _logger.LogError(ex, "[SubDL-Refresh] Refresh aborted mid-run — already-flushed parts stay, remainder untouched.");
            }

            int removedTotal = removedSearch + removedRetry + removedQa + removedOshash
                               + removedPrunedSubtitles + removedPrunedMedia;
            int changedTotal = removedTotal + forgottenSidecars + openFiles + forgottenEmbeds + backfilledForced;

            if (changedTotal > 0)
            {
                LogUtil.Normal(
                    _logger,
                    "[SubDL-Refresh] Removed dead state: {Search} search / {Retry} file-retry / {Qa} qa-fail / {Oshash} oshash / {PrunedSubs} subtitle / {PrunedMedia} media (dead items). Forgot {Sidecars} vanished subtitle verdict(s); {OpenFiles} item(s) with an open required file; forgot {Embeds} stale embedded row(s).",
                    removedSearch, removedRetry, removedQa, removedOshash,
                    removedPrunedSubtitles, removedPrunedMedia,
                    forgottenSidecars, openFiles, forgottenEmbeds);
            }
            else if (refreshException != null)
            {
                _logger.LogWarning("[SubDL-Refresh] No counters updated because an exception occurred earlier; see error log for details.");
            }
            else
            {
                LogUtil.Normal(_logger, "[SubDL-Refresh] Database matches reality — nothing changed.");
            }

            // The refresh's own numbers are locals and vanish with the method — persist the outcome so
            // the configuration page can show when each worker last ran and what it found.
            plugin.WorkerRuns.Finish(
                "SubdlSyncDatabaseRefreshTask",
                Name,
                refreshException != null ? Registry.WorkerRunRegistry.Outcome.Failed : Registry.WorkerRunRegistry.Outcome.Ok,
                BuildRefreshDetail(refreshException, changedTotal, removedTotal, forgottenSidecars, forgottenEmbeds, openFiles, compactNote));

            progress.Report(100);
        }
        finally
        {
            PipelineRunLock.Release(_logger, "refresh");
        }
    }

    /// <summary>
    /// Builds the detail line the configuration page shows for a database refresh.
    /// <para>
    /// Static and pure so the wording is testable without a Jellyfin host.
    /// </para>
    /// <para>
    /// A compaction that fell back to the rebuild is appended to the line (user decision 30.09.2026:
    /// the compaction failure itself will NOT be solved, the rebuild is the accepted answer — but
    /// the row said "ok" with a line that read like a clean run, so the fallback was invisible).
    /// This is a NOTE, not a red light: the refresh did its work and the store came back usable.
    /// </para>
    /// </summary>
    /// <param name="refreshException">The exception that aborted the refresh, or null.</param>
    /// <param name="changedTotal">How many pieces of dead state were removed in total.</param>
    /// <param name="removedTotal">How many dead-state rows were removed.</param>
    /// <param name="forgottenSidecars">How many vanished subtitle verdicts were forgotten.</param>
    /// <param name="forgottenEmbeds">How many stale embedded rows were forgotten.</param>
    /// <param name="openFiles">How many items still have a required file missing.</param>
    /// <param name="compactNote">A note about the compaction step, or empty when it compacted cleanly.</param>
    /// <returns>The detail text for the worker row.</returns>
    public static string BuildRefreshDetail(
        Exception? refreshException,
        int changedTotal,
        int removedTotal,
        int forgottenSidecars,
        int forgottenEmbeds,
        int openFiles,
        string? compactNote)
    {
        if (refreshException != null)
        {
            string message = refreshException.Message;
            return string.IsNullOrWhiteSpace(compactNote) ? message : message + " | " + compactNote;
        }

        string body = changedTotal > 0
            ? string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "removed {0} dead state, forgot {1} stale row(s), {2} item(s) with open files",
                removedTotal,
                forgottenSidecars + forgottenEmbeds,
                openFiles)
            : "nothing to change";

        return string.IsNullOrWhiteSpace(compactNote) ? body : body + " | " + compactNote;
    }

    /// <summary>
    /// F-M284 (user decision 02.10.2026): backfills the FORCED property on rows written before it
    /// existed.
    /// <para>
    /// A document store has no schema, so a row written by an older build simply has no
    /// <c>Forced</c> field and reads back as <c>false</c>. That is not a harmless default here: a
    /// forced track would then be indistinguishable from the film's dialogue, so it would COUNT AS
    /// COVERAGE — the language would look settled although its only track carries foreign-language
    /// scenes, and the regular subtitle would never be fetched. The stored rows are the state, so
    /// they are brought up to date rather than left to look settled.
    /// </para>
    /// <para>
    /// The flag is read back off the row's own name, which is where it came from in the first place:
    /// a sidecar states its markers in its file name (<c>Movie.de.forced.srt</c>), so the backfill
    /// derives nothing new — it re-reads a fact the name always carried.
    /// </para>
    /// <para>
    /// An EMBEDDED row cannot be backfilled this way and is deliberately left alone: its flag lives
    /// in the container's stream list, not in a name, and the next scan or download pass observes
    /// the track again and writes the current value. Guessing <c>false</c> there would be the very
    /// mistake this method exists to undo.
    /// </para>
    /// </summary>
    /// <param name="registry">Content registry (accepts the sidecar rows directly).</param>
    /// <returns>Number of sidecar rows whose forced flag was set.</returns>
    private int BackfillForcedFlags(Data.SubdlDbContext db)
    {
        int updated = 0;
        var changed = new List<Data.SidecarEntity>();

        foreach (var row in db.Sidecars.FindAll())
        {
            // F-M285: an explicit `true` OR `false` is a statement and is left alone. Only a
            // `null` — a row that does not say — is filled. The distinction is the point: without
            // it a legacy gap and a deliberate "not forced" look identical.
            if (row.Forced.HasValue)
            {
                continue; // the row already states its value
            }

            // The stored file name is the same string the marker reader works on. A row without one
            // carries no evidence either way, so it is skipped rather than guessed at.
            string? name = !string.IsNullOrEmpty(row.FileName)
                ? Path.GetFileNameWithoutExtension(row.FileName!)
                : (!string.IsNullOrEmpty(row.Path) ? Path.GetFileNameWithoutExtension(row.Path!) : null);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var (_, forced) = Registry.SidecarNaming.ReadFlags(name);

            // F-M285: the gap is filled with the value that was READ — including `false`. Returning
            // early on a name without the marker left a plain `.de.srt` row at `null` for ever, so it
            // never reached the explicit statement the three-valued rule requires: the row would keep
            // saying "I do not know" about a fact its own name states, and the datum would read as
            // open on every single run. A gap-filler that only writes the affirmative case does not
            // converge.
            row.Forced = forced;
            changed.Add(row);
            if (forced)
            {
                updated++;
            }
        }

        if (changed.Count > 0)
        {
            db.Sidecars.Update(changed);
            LogUtil.Normal(_logger, "[SubDL-Refresh] forced flag stated on {Count} sidecar row(s) written before the field existed ({ForcedCount} forced, {PlainCount} not).", changed.Count, updated, changed.Count - updated);
        }

        return updated;
    }

    /// <summary>
    /// F-M258: drops embedded rows that no longer match the file's own tracks.
    /// <para>
    /// The sidecar half of this lives in <see cref="Registry.ContentHashRegistry.GetSidecarsMissingFromDisk"/>
    /// and needs only <c>File.Exists</c>. An embedded track has no file, so its evidence is the
    /// stream list — this is the only caller that can read it, which is why the check lives here.
    /// </para>
    /// <para>
    /// Fail-safe, the same rule the rest of the refresh follows ("unknown ≠ deleted"): an item
    /// Jellyfin no longer resolves, a file that is gone, or a stream list that comes back empty is
    /// skipped rather than judged. An empty list is the dangerous case — an item Jellyfin cannot
    /// probe returns no streams, and treating that as "the file has no tracks" would delete every
    /// row of a library that is merely offline. Only a NON-empty list that disagrees with a stored
    /// row is evidence.
    /// </para>
    /// </summary>
    /// <param name="registry">Content registry.</param>
    /// <param name="aliveItemIds">Item ids still present in Jellyfin.</param>
    /// <returns>Number of removed embedded rows.</returns>
    private int ForgetStaleEmbeds(Registry.ContentHashRegistry registry, HashSet<string> aliveItemIds)
    {
        int removed = 0;
        var mediaItems = Plugin.Instance?.SharedDbContext.Media;
        if (mediaItems == null)
        {
            return 0;
        }

        foreach (var media in mediaItems.FindAll()
                     .Where(m => !string.IsNullOrEmpty(m.JellyfinItemId)
                                 && aliveItemIds.Contains(m.JellyfinItemId!))
                     .ToList())
        {
            if (registry.GetEmbeds(media.Id).Count == 0)
            {
                continue; // nothing stored for this file — no work, no query
            }

            if (!Guid.TryParse(media.JellyfinItemId, out var itemGuid))
            {
                continue;
            }

            List<MediaBrowser.Model.Entities.MediaStream>? streams;
            try
            {
                streams = _mediaSourceManager.GetMediaStreams(itemGuid)?.ToList();
            }
            catch (Exception ex)
            {
                LogUtil.Detail(_logger, "[SubDL-Refresh] embedded check skipped for {Id}: {Msg}", media.Id, ex.Message);
                continue; // unreadable streams — no verdict this run
            }

            if (streams == null || streams.Count == 0)
            {
                continue; // unknown ≠ deleted: an empty list may mean "cannot probe right now"
            }

            // F-M258 (user decision 01.10.2026): a stored row is only provably stale when the
            // POSITION is gone. The previous call compared against EmbeddedTracks, which lists a
            // track only once its language resolves — and Jellyfin caches its stream list, so a
            // container the gate corrected earlier in the cycle still reports Language=null. Every
            // row of such a file therefore looked orphaned and was deleted, although the track was
            // right there (FM264 Probe: the container carries eng/ger, ffprobe reads them, Jellyfin
            // reported none, and two valid rows were dropped).
            //
            // A position that still exists keeps its row: the stored language came from the gate's
            // own detection or from an observed stream, and it is at least as good as a tag the
            // cache has not caught up with. A position that no longer exists goes, and so does a
            // row whose position is out of range of the item's subtitle streams.
            var positions = Registry.SidecarNaming.SubtitlePositions(streams);
            var current = Registry.SidecarNaming.EmbeddedTracks(streams)
                .Where(t => positions.Contains(t.SubPos))
                .ToList();
            removed += registry.ForgetStaleEmbeds(media.Id, current, positions);
        }

        return removed;
    }

    /// <summary>
    /// F-M283 (user decision 02.10.2026): how many items have an OPEN required file.
    /// <para>
    /// This used to be <c>InvalidateStaleDownloadMarks</c>: it walked every stored download mark,
    /// re-derived its "open tokens" and DROPPED the mark when a subtitle had vanished — a repair of
    /// a second truth that should never have existed. Measured on the live library, that machinery
    /// dropped 450 valid marks in one run on its first attempt (disk-only evidence against files
    /// whose subtitles sat inside the container) and, once narrowed, kept contradicting a mark the
    /// pipeline could not write at all: 35 marks withheld in a single run.
    /// </para>
    /// <para>
    /// With the mark gone there is nothing to drop and nothing to repair. What remains is the
    /// question itself — "which required files are missing on this disk?" — asked through the SAME
    /// reader the pipeline and the seeder use (<see cref="Registry.SubtitleCoverage"/>), so a
    /// deletion here and a deletion there cannot be judged differently. A vanished
    /// <c>.sdh.srt</c> is simply an open pair, and the item is due on the next run without any
    /// bookkeeping at all.
    /// </para>
    /// <para>
    /// The method only REPORTS, deliberately: this is the refresh, and its job is to make the state
    /// visible. It writes nothing, so a run can never take work away from the pipeline.
    /// </para>
    /// </summary>
    /// <param name="db">Shared database.</param>
    /// <param name="registry">Content registry.</param>
    /// <param name="aliveItemIds">Item ids still present in Jellyfin.</param>
    /// <returns>Number of items with at least one open required file.</returns>
    private int CountItemsWithOpenFiles(Data.SubdlDbContext db, Registry.ContentHashRegistry registry, HashSet<string> aliveItemIds)
    {
        var targets = (Plugin.Instance?.Configuration.DownloadLanguages ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.ToUpperInvariant())
            .Distinct()
            .ToList();
        if (targets.Count == 0)
        {
            return 0;
        }

        var required = Registry.SubtitleRef.Required(
            targets, Plugin.Instance?.Configuration.DownloadHearingImpaired == true);

        int open = 0;
        foreach (var media in db.Media.FindAll()
                     .Where(m => !string.IsNullOrEmpty(m.JellyfinItemId)
                                 && aliveItemIds.Contains(m.JellyfinItemId!))
                     .ToList())
        {
            if (string.IsNullOrEmpty(media.Path) || !File.Exists(media.Path!))
            {
                continue; // media file itself gone — the pipeline's file-retry path owns this
            }

            string dir = Path.GetDirectoryName(media.Path!) ?? ".";
            if (!Registry.SubtitlePresence.RootsUsable(new[] { dir }))
            {
                continue; // directory unreadable — no verdict this run (unknown ≠ deleted)
            }

            // The SAME reader the pipeline and the seeder use, so all three answer alike. The
            // embedded rows the registry holds are part of the evidence, which is what makes a
            // subtitle inside the container count — and a VARIANT inside the container count as the
            // variant, which the old language-keyed check could not express (F-M234, F-M282).
            var openPairs = registry.OpenPairs(media.Path!, required);
            if (openPairs.Count == 0)
            {
                continue;
            }

            // F-M320 (operator order 08.10.2026): the QA retry limit is the FIT's budget and no longer
            // makes a pair actionless. The filter that used to sit here called an item "not missing
            // work" once its candidates had been gate-rejected enough times — a give-up the operator
            // removed, and the reason a file with only badly ripped candidates was never searched
            // again. Every open pair is actionable now.
            var actionable = openPairs;
            if (actionable.Count == 0)
            {
                continue;
            }

            open++;
            LogUtil.Normal(
                _logger,
                "[SubDL-Refresh] {File} has {Count} open required file(s): {Pairs}",
                Path.GetFileName(media.Path!),
                actionable.Count,
                string.Join(", ", actionable.Select(p => p.ToString())));
        }

        return open;
    }

    /// <summary>
    /// F-M234: the embedded languages that count as coverage for a stored download mark.
    /// <para>
    /// Same sources the download pipeline consults, in the same order of authority: the REGISTRY
    /// first, because Jellyfin caches its stream list and still reports the OLD tag of a container
    /// corrected earlier in the cycle, then Jellyfin's own list as the second opinion.
    /// </para>
    /// <para>
    /// Empty when the configuration does not count embedded tracks at all, and empty while the item
    /// cannot be probed. The caller then decides on the files alone, which is the pre-01.10.2026
    /// behaviour — narrow, but never worse than the truth.
    /// </para>
    /// </summary>
    /// <param name="db">Shared database.</param>
    /// <param name="registry">Content registry.</param>
    /// <param name="media">The media row whose mark is being judged.</param>
    /// <returns>Languages carried by the file's own tracks.</returns>
    private HashSet<string> EmbeddedLanguagesForCoverage(Data.SubdlDbContext db, Registry.ContentHashRegistry registry, Data.MediaEntity media)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Plugin.Instance?.Configuration.DownloadOnlyMissing != true)
        {
            return found;
        }

        if (!string.IsNullOrEmpty(media.Path))
        {
            foreach (var lang in registry.EmbeddedLanguages(media.Path))
            {
                found.Add(lang);
            }
        }

        if (string.IsNullOrEmpty(media.JellyfinItemId) || !Guid.TryParse(media.JellyfinItemId, out var itemGuid))
        {
            return found;
        }

        try
        {
            foreach (var lang in Registry.SidecarNaming.EmbeddedPresentLanguages(_mediaSourceManager.GetMediaStreams(itemGuid)))
            {
                found.Add(lang);
            }
        }
        catch (Exception ex)
        {
            // unknown ≠ missing: the registry half already answered, and a probe that fails adds
            // nothing rather than turning a language into "absent".
            LogUtil.Detail(_logger, "[SubDL-Refresh] embedded coverage probe skipped for {Id}: {Msg}", media.Id, ex.Message);
        }

        return found;
    }

    /// <summary>
    /// Derive the distinct top-level folders (library roots) from the cached
    /// media paths — e.g. all paths under /data/movies2 → "/data/movies2". The refresh
    /// verifies these exist AND list cleanly before removing anything (an offline
    /// mount would otherwise report every file as missing and nuke the cache).
    /// </summary>
    private static List<string> collectRoots(Registry.OshashCache oshash)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in oshash.GetAllPaths())
        {
            // two levels below filesystem root: "/data/movies2/..." → "/data/movies2"
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                roots.Add("/" + string.Join('/', parts[0], parts[1]));
            }
            else if (parts.Length == 1)
            {
                roots.Add("/" + parts[0]);
            }
        }

        return roots.ToList();
    }

    private static int JobSpacingMinutes()
    {
        try
        {
            return Math.Clamp(Plugin.Instance?.Configuration.JobSpacingMinutes ?? 15, 5, 120);
        }
        catch
        {
            return 15;
        }
    }
}
