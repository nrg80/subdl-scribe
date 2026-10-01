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
// Queue seeder (F-M111, user decision 12.09.2026): scans the library and fills
// the upload/download queues as plain candidates. Knows NOTHING about runs —
// the dispatcher owns run sequencing; this file only scans and merges.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Api;
using Jellyfin.Plugin.SubdlScribe.Pipeline;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

using Jellyfin.Plugin.SubdlScribe.Language;
using Jellyfin.Plugin.SubdlScribe.Data;

namespace Jellyfin.Plugin.SubdlScribe.ScheduledTasks;

/// <summary>Result of one seeder pass: fresh candidate lists per direction.</summary>
public sealed class SeedSnapshot
{
    /// <summary>Library candidates for the upload queue.</summary>
    public List<QueueItem> Upload { get; } = new();

    /// <summary>Library candidates for the download queue.</summary>
    public List<QueueItem> Download { get; } = new();
}

/// <summary>One queue entry (shared by seeder and dispatcher).</summary>
public sealed class QueueItem
{
    /// <summary>Queue states: waiting, processed, or dropped after retry limit.</summary>
    public enum ItemState
    {
        /// <summary>Waiting to be processed by the pipeline.</summary>
        Queued,

        /// <summary>Processed successfully — tombstone, skipped by future seeds.</summary>
        Done,

        /// <summary>Purged (retry limit / manual) — tombstone, skipped by future seeds.</summary>
        Purged,
    }

    /// <summary>Jellyfin item id.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Media file path (existence check).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Display name (log only).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Consecutive REAL-failure counter (max 3 → purge).</summary>
    public int Retries { get; set; }

    /// <summary>Queue state (done/purged act as tombstones the seeder skips).</summary>
    public ItemState State { get; set; } = ItemState.Queued;

    /// <summary>Item creation time (UTC) — LIFO sort key for seeding (F-M111).</summary>
    public DateTime SortCreatedUtc { get; set; }

    /// <summary>Last state change (UTC, for tombstone pruning).</summary>
    public DateTime ChangedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Library seeder: one pass over the selected libraries, every Movie/Episode with
/// an existing media file becomes a queue candidate. Pure producer — the dispatcher
/// merges the snapshot into the persisted queues (retry counters and tombstones
/// are preserved there).
/// </summary>
/// F-M38: persistent state is split by the kind of thing described (section 3.18).
public sealed class SubdlSeeder
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILogger _logger;
    private readonly TmdbImdbResolver _tmdb;

    /// <summary>Initializes the seeder.</summary>
    public SubdlSeeder(ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager, ILoggerFactory loggerFactory, TmdbImdbResolver tmdb)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _tmdb = tmdb;
        _logger = loggerFactory.CreateLogger("Jellyfin.Plugin.SubdlScribe.ScheduledTasks.SubdlSeeder");
    }

    /// <summary>
    /// Scans the selected libraries and returns candidates per direction.
    /// <paramref name="onlyLibraries"/> (event path, user decision 12.09.2026):
    /// when non-empty, ONLY items of these library names are scanned — the
    /// dispatcher passes the libraries of the changed items (grows per event,
    /// caps back to "all" above the targeted-set limit). Null/empty = all
    /// selected libraries.
    /// Order (user decision 12.09.2026): LIFO (DateCreated descending); items
    /// WITHOUT imdb AND tmdb id go artificially to the queue END (same partition
    /// Rule as the pipeline's ladder).
    /// </summary>
    /// <summary>
    /// (15.09.2026, user request): cheap pre-check for seed rounds 2..n —
    /// the newest library-side change timestamp (JF housekeeping: item
    /// DateModified/DateCreated from the server DB, no file walk). Compared
    /// against the previous real scan's start time; unchanged libraries let the
    /// dispatcher skip the expensive scan entirely. Pure-DB: fast even for
    /// large libraries.
    /// </summary>
    public DateTime MaxChangedUtc(Configuration.PluginConfiguration config, System.Collections.Generic.ISet<string>? onlyLibraries = null)
    {
        DateTime max = DateTime.MinValue;
        try
        {
            // F-M188 (24.09.2026): same hard scope as Scan() — otherwise the
            // pre-check sees changes in NON-selected libraries, reports
            // "changed" and forces a full scan that then filters everything
            // out. Correct result, wasted work.
            // F-M189: the scope is PATH-based (see LibraryScope) so nested
            // library selections resolve correctly.
            var scope = LibraryScope.Create(_libraryManager, config.SelectedLibraries, _logger);
            if (scope.IsEmpty)
            {
                return DateTime.MinValue;
            }

            // Event path: only the named libraries (resolved the same way, so a
            // nested selection is filtered by its subtree, not by a name that
            // never matches).
            LibraryScope? eventScope = null;
            if (onlyLibraries is { Count: > 0 })
            {
                eventScope = LibraryScope.Create(_libraryManager, onlyLibraries, _logger);
            }

            var query = new InternalItemsQuery(null)
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Episode },
                Recursive = true,
                IsVirtualItem = false
            };

            foreach (var item in _libraryManager.GetItemList(query))
            {
                if (item is not (Movie or Episode))
                {
                    continue;
                }

                if (!scope.Contains(item))
                {
                    continue;
                }

                if (eventScope != null && !eventScope.Contains(item))
                {
                    continue;
                }

                var mod = item.DateModified.ToUniversalTime();
                var created = item.DateCreated.ToUniversalTime();
                if (mod > max) { max = mod; }
                if (created > max) { max = created; }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[SubDL-Seed] Change pre-check failed ({Msg}) — forcing full scan.", ex.Message);
            return DateTime.MaxValue; // safe direction: pre-check never skips
        }

        return max;
    }

    /// <summary>
    /// F-M233: the arrival-scope decision, extracted so it is testable without the
    /// Jellyfin host. Null set = no narrowing (schedule/manual: full coverage).
    /// </summary>
    /// <param name="itemId">Candidate item id.</param>
    /// <param name="onlyItemIds">The arrived ids, or null for a full run.</param>
    /// <returns>True when the item must be skipped for this run.</returns>
    public static bool SkipForArrivalScope(string itemId, System.Collections.Generic.ISet<string>? onlyItemIds)
        => onlyItemIds != null && !onlyItemIds.Contains(itemId);

    public SeedSnapshot Scan(Configuration.PluginConfiguration config, System.Collections.Generic.ISet<string>? onlyLibraries = null, SubdlEventDispatcher.CycleDirection dir = SubdlEventDispatcher.CycleDirection.Both, System.Collections.Generic.ISet<string>? onlyItemIds = null)
    {
        var snapshot = new SeedSnapshot();
        string dirLabel = dir switch
        {
            SubdlEventDispatcher.CycleDirection.UploadOnly => "upload",
            SubdlEventDispatcher.CycleDirection.DownloadOnly => "download",
            _ => "both"
        };
        LogUtil.Normal(_logger, "[SubDL-Seed] scan started ({Direction})", dirLabel);

        // F-M4a: requirement marker added for traceability.
        // F-M189: selected libraries resolve to their media PATHS, so a
        // selection also covers a library nested inside another one.
        var scope = LibraryScope.Create(_libraryManager, config.SelectedLibraries, _logger);

        // F-M188 (24.09.2026): the selected-library scope is a HARD gate for
        // every trigger. Before this, only the event path filtered by library
        // (via onlyLibraries); a scheduled/manual run passed null and scanned
        // the whole server. The pipeline's own CollectItems() masked the
        // damage locally, but the queue filled with items no pipeline would
        // ever work (measured: 913 queued for a 26-item test library).
        if (scope.IsEmpty)
        {
            LogUtil.Normal(_logger, "[SubDL-Seed] No libraries selected — nothing to queue.");
            return snapshot;
        }

        // Event path: filter to the changed libraries' subtrees.
        LibraryScope? eventScope = null;
        if (onlyLibraries is { Count: > 0 })
        {
            eventScope = LibraryScope.Create(_libraryManager, onlyLibraries, _logger);
        }

        try
        {
            var query = new InternalItemsQuery(null)
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Episode },
                Recursive = true,
                IsVirtualItem = false
            };

            foreach (var item in _libraryManager.GetItemList(query))
            {
                if (item is not (Movie or Episode))
                {
                    continue;
                }

                // F-M233: an on-arrival run works ONLY the items that actually
                // arrived. The set is passed by the dispatcher for the event and
                // follow-up triggers — a scheduled or manual run passes null and
                // keeps scanning the whole selection (the user's full-coverage path).
                if (SkipForArrivalScope(item.Id.ToString(), onlyItemIds))
                {
                    continue;
                }

                // F-M188: hard scope — an item outside the selected libraries
                // never enters a queue, whatever triggered this scan.
                // F-M189: matched by PATH, so a nested selection works.
                if (!scope.Contains(item))
                {
                    continue;
                }

                string? mediaPath = item.Path;
                if (string.IsNullOrEmpty(mediaPath) || !File.Exists(mediaPath))
                {
                    continue;
                }

                var qi = new QueueItem
                {
                    ItemId = item.Id.ToString(),
                    Path = mediaPath,
                    Name = item.Name ?? System.IO.Path.GetFileName(mediaPath)
                };

                // F-M257: record what the container CARRIES before deciding anything about it.
                // The embedded area's only writer was the upload pipeline, so with the shipped
                // default (UploadEnabled=false) no row existed and the HI reader (F-M254) answered
                // "absent" forever for tracks that were right there. The scan already walks every
                // item and the streams are cheap here; the write is idempotent after the first pass.
                // F-M259: the same for the loose .srt files beside the media.
                ObserveEmbeddedFacts(item, mediaPath);
                ObserveSidecarFacts(mediaPath);

                // F-M261 (user decision 30.09.2026): allocate missing language codes BEFORE the
                // queue decision. This is a seeder job and not a pipeline job, and the order is the
                // reason: `IsDownloadTodo` below asks `MissingLanguagesOf`, which reads the language
                // of a track through `LanguageMapper` — for an untagged or `und` track that answer
                // is "not present", so the item is queued and searched for a language its container
                // demonstrably carries. Only a resolution that has already happened can change that
                // answer. The pipeline's own gate stays for the upload direction and for a directed
                // download fire, where the seeder did not run.
                AllocateMissingLanguageCodes(mediaPath, item);

                // Event path: only the changed libraries (user decision 12.09.2026,
                // extended 12.09.2026: targeted growth — each changed library JOINS
                // the set instead of widening to "all"). F-M189: subtree match,
                // so a nested selection filters by its own path.
                if (eventScope != null && !eventScope.Contains(item))
                {
                    continue;
                }

                // F-M111: carry DateCreated for the LIFO ordering (separate field —
                // ChangedUtc stays the tombstone clock).
                qi.SortCreatedUtc = item.DateCreated.ToUniversalTime();

                // 12.09.2026 (user decision): the seeder only queues ACTUAL todo —
                // items the pipelines would skip locally (settled uploads, refetch
                // gap, nothing-missing downloads, QA-exhausted) never enter the
                // queue. Defense in depth stays: the pipelines keep their checks.
                // 13.09.2026 (user decision): a directed cycle seeds ONLY its
                // direction — an upload-button fire must not fill the download
                // queue (and vice versa).
                if (dir != SubdlEventDispatcher.CycleDirection.UploadOnly
                    && config.DownloadEnabled && IsDownloadTodo(item, mediaPath, config))
                {
                    snapshot.Download.Add(Clone(qi));
                }

                if (dir != SubdlEventDispatcher.CycleDirection.DownloadOnly
                    && config.UploadEnabled && IsUploadTodo(item, mediaPath, config))
                {
                    snapshot.Upload.Add(Clone(qi));
                }
            }

            // F-M111: LIFO + id-less partition (user decision 12.09.2026).
            foreach (var list in new[] { snapshot.Download, snapshot.Upload })
            {
                var (withIds, withoutIds) = PartitionByIds(list, config);
                list.Clear();
                list.AddRange(withIds);
                list.AddRange(withoutIds);
            }

            LogUtil.Normal(_logger, "[SubDL-Seed] scan finished ({Direction}) — {Upload} upload, {Download} download queued", dirLabel, snapshot.Upload.Count, snapshot.Download.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[SubDL-Seed] Library scan failed: {Msg}", ex.Message);
        }

        return snapshot;
    }

    private (List<QueueItem> With, List<QueueItem> Without) PartitionByIds(List<QueueItem> items, Configuration.PluginConfiguration config)
    {
        var with = new List<QueueItem>();
        var without = new List<QueueItem>();
        foreach (var qi in items)
        {
            // Parity: no imdb AND no tmdb id → back of the queue (the pipeline
            // would spend its 15-min metadata ladder on them). Episoden: Series-Ids
            // zählen.
            var item = GetItem(qi.ItemId);
            var (imdb, tmdb, _, _, _) = ResolveIdsOf(item);
            if (string.IsNullOrWhiteSpace(imdb) && string.IsNullOrWhiteSpace(tmdb))
            {
                without.Add(qi);
            }
            else
            {
                with.Add(qi);
            }
        }

        // LIFO within both partitions (newest first).
        with.Sort((a, b) => b.SortCreatedUtc.CompareTo(a.SortCreatedUtc));
        without.Sort((a, b) => b.SortCreatedUtc.CompareTo(a.SortCreatedUtc));
        return (with, without);
    }

    private BaseItem? GetItem(string itemId)
    {
        try
        {
            return _libraryManager.GetItemById(Guid.Parse(itemId));
        }
        catch
        {
            return null;
        }
    }

    private (string?, string?, int, int, bool) ResolveIdsOf(BaseItem? item)
    {
        return ResolveIdsOfAsync(item).GetAwaiter().GetResult();
    }

    private async Task<(string?, string?, int, int, bool)> ResolveIdsOfAsync(BaseItem? item)
    {
        var (imdb, tmdb, season, episode, isSeries) = ResolveIdsOfCore(item);
        if (!string.IsNullOrWhiteSpace(imdb) || !string.IsNullOrWhiteSpace(tmdb) || item == null)
        {
            return (imdb, tmdb, season, episode, isSeries);
        }

        // TMDb fallback: when no provider id is present, try a title search.
        if (!_tmdb.IsConfigured)
        {
            return (null, null, season, episode, isSeries);
        }

        string? title = null;
        int? year = null;
        string? mediaPath = null;
        switch (item)
        {
            case Episode ep:
                title = ep.Series?.Name;
                mediaPath = ep.Path;
                if (ep.Series?.ProductionYear is > 0)
                {
                    year = ep.Series.ProductionYear;
                }

                break;
            case Movie movie:
                title = movie.Name;
                mediaPath = movie.Path;
                if (movie.ProductionYear is > 0)
                {
                    year = movie.ProductionYear;
                }

                break;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return (null, null, season, episode, isSeries);
        }

        try
        {
            // F-M219 (27.09.2026): the FILE NAME decides type and title before TMDB is
            // asked, exactly as F-M217 established for the pipeline. ResolveIdsOfCore reads
            // `movie.Name` for a Movie, and for an episode that Jellyfin imported as a Movie
            // that name IS the raw file name ("Reacher.S03E01") — the typed search below
            // then asks search/movie for a file name and finds nothing, while the type it
            // assumed came from the library that F-M190/F-M217 stopped trusting.
            bool nameSaysSeries = isSeries;
            if (mediaPath is { Length: > 0 })
            {
                var parsed = MediaNameParser.Parse(mediaPath);
                if (parsed.IsSeries)
                {
                    nameSaysSeries = true;
                    if (parsed.Title is { Length: >= 2 } parsedTitle)
                    {
                        title = parsedTitle;
                    }

                    if (parsed.Year is int py && py > 0)
                    {
                        year = py;
                    }

                    if (!isSeries)
                    {
                        isSeries = true; // the name knows the type, Jellyfin did not
                        season = parsed.Season ?? season;
                        episode = parsed.Episode ?? episode;
                    }
                }
            }

            // Type-neutral first (TMDB decides the type), typed search as the second rung
            // where a known type plus the year is the sharper query.
            var multi = await _tmdb.ResolveByMultiSearchAsync(title, year, nameSaysSeries, default).ConfigureAwait(false);
            if (multi.Found)
            {
                return (multi.Imdb, multi.Tmdb, season, episode, multi.IsSeries);
            }

            var (resolvedImdb, resolvedTmdb) = await _tmdb.ResolveImdbByTitleAsync(title, year, nameSaysSeries, default).ConfigureAwait(false);
            return (resolvedImdb, resolvedTmdb, season, episode, nameSaysSeries);
        }
        catch (Exception ex)
        {
            LogUtil.Detail(_logger, ex, "[SubDL-Seeder] TMDb fallback failed for {Title}", title);
        }

        return (null, null, season, episode, isSeries);
    }

    private (string?, string?, int, int, bool) ResolveIdsOfCore(BaseItem? item)
    {
        switch (item)
        {
            case Episode ep:
                string? seriesImdb = null;
                string? seriesTmdb = null;
                ep.Series?.ProviderIds?.TryGetValue("Imdb", out seriesImdb);
                ep.Series?.ProviderIds?.TryGetValue("Tmdb", out seriesTmdb);
                return (seriesImdb, seriesTmdb, ep.ParentIndexNumber ?? 0, ep.IndexNumber ?? 0, true);
            case Movie movie:
                string? imdb = null;
                string? tmdb = null;
                movie.ProviderIds?.TryGetValue("Imdb", out imdb);
                movie.ProviderIds?.TryGetValue("Tmdb", out tmdb);
                return (imdb, tmdb, 0, 0, false);
            default:
                return (null, null, 0, 0, false);
        }
    }

    // F-M189 (24.09.2026): the old parent-walk / name-only resolvers
    // (IsInSelectedLibrary / IsInLibrary) are GONE — replaced by LibraryScope,
    // which matches the item PATH against the selected libraries' locations and
    // therefore also works when libraries are nested. Do not re-add a
    // name-only scope check: it silently selects nothing for an inner library.

    // ── Seeder prefilter (12.09.2026, user decision "only actual todo in queue") ──

    /// <summary>
    /// True when the download pipeline would actually work on this item: id
    /// resolvable (including TMDb fallback), refetch gap elapsed, at least one
    /// target language missing that is not QA-exhausted.
    /// </summary>
    private bool IsDownloadTodo(BaseItem item, string mediaPath, Configuration.PluginConfiguration config)
    {
        try
        {
            var db = Plugin.Instance!.SharedDbContext;

            // F-M217 (27.09.2026, user decision "die id-Aufloesung aus dem Prefilter
            // nehmen"): the id gate is GONE. It asked ResolveIdsOf, which for an item
            // Jellyfin typed as a Movie looks up the raw item name ("Reacher.S03E01")
            // and finds nothing — with DownloadRequireImdb on, such an item never
            // reached the queue, so the pipeline's own ladder (file-name parse plus
            // TYPE-NEUTRAL TMDb search/multi) could never run. Measured 27.09.2026 on
            // the test library: 44 items, 23 with ids, 21 without — 0 of the 21 were
            // queued. The pipeline keeps the fail-closed decision: an item that stays
            // id-less is skipped there (SkippedNoId, no-id requeue), not here.

            // F-M47: refetch gate.
            var searchTracker = new Registry.DownloadSearchTracker(db);
            if (!searchTracker.IsDue(item.Id.ToString(), SubdlDownloadTask.IntervalToGap(config.RefetchInterval), TargetLanguagesOf(config)))
            {
                return false;
            }

            // Parity: QA-exhausted languages drop out of the missing list.
            var missing = MissingLanguagesOf(item, mediaPath, TargetLanguagesOf(config), config.DownloadOnlyMissing);
            var qaFails = new Registry.QaFailTracker(db);
            missing.RemoveAll(lang => qaFails.IsExhausted(item.Id.ToString(), lang, config.DownloadQaRetryLimit));
            return missing.Count > 0;
        }
        catch
        {
            return true; // prefilter failure → treat as todo (pipeline decides)
        }
    }

    /// <summary>
    /// True when the upload pipeline would actually work on this media file.
    /// Mirrors the file-level fast paths (): fully settled embedded
    /// streams or a complete known-pairs set without loose sidecars → skip.
    /// Loose sidecars keep the item as todo — the pipeline owns their checks.
    /// </summary>
    private bool IsUploadTodo(BaseItem item, string mediaPath, Configuration.PluginConfiguration config)
    {
        try
        {
            var db = Plugin.Instance!.SharedDbContext;
            var registry = new Registry.ContentHashRegistry(db);

            var allSubs = _mediaSourceManager.GetMediaStreams(item.Id)?
                .Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal)
                .ToList() ?? new List<MediaStream>();
            // F-M246: a forced track is not upload work — same predicate as the pipeline, so the
            // prefilter and the run agree on what there is to do.
            var textStreams = allSubs
                .Where(s => s.IsTextSubtitleStream && !Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming.IsForcedStream(s))
                .ToList();

            if (textStreams.Count == 0)
            {
                var looseOnly = Pipeline.UploadPipeline.FindLooseSrts(mediaPath);
                if (looseOnly.Count == 0)
                {
                    return false;
                }

                string looseHash = registry.GetMediaHash(mediaPath);
                if (looseHash == null)
                {
                    return true;
                }

                return SidecarsNeedWork(looseOnly, looseHash);
            }

            string? mediaHash = registry.GetMediaHash(mediaPath);
            if (mediaHash == null)
            {
                return true;
            }

            // Parity: settled positions skip the embedded walk. Loose sidecars
            // DO keep their own checks — but those are pure registry lookups
            // (IsUploaded / RejectedReason per language, zero I/O), so the seeder
            // runs them here too (user request 13.09.2026: seeder decides the
            // sidecars directly). Only a sidecar that is neither uploaded nor
            // rejected keeps the item as todo.
            if (registry.ArePositionsTerminal(mediaHash, textStreams.Select(s => allSubs.FindIndex(x => x == s))))
            {
                var loose = Pipeline.UploadPipeline.FindLooseSrts(mediaPath);
                if (!SidecarsNeedWork(loose, mediaHash))
                {
                    return false; // embedded settled + every sidecar uploaded/rejected/hash-known
                }

                return true;
            }

            // Parity: distinct (language, HI) pairs fully known + no loose
            // sidecars → nothing to do.
            if (Pipeline.UploadPipeline.FindLooseSrts(mediaPath).Count == 0)
            {
                var needed = new HashSet<string>(StringComparer.Ordinal);
                int untagged = 0;
                foreach (var s in textStreams)
                {
                    string? mapped = s.Language is null ? null : LanguageMapper.MapToSubdl(s.Language);
                    if (mapped is null)
                    {
                        untagged++;
                    }
                    else
                    {
                        needed.Add(mapped + "|" + (Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming.IsHearingImpairedStream(s) ? "1" : "0"));
                    }
                }

                if (registry.CountKnownPairs(mediaHash) >= needed.Count + untagged)
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return true; // prefilter failure → treat as todo (pipeline decides)
        }
    }

    /// <summary>
    /// True when any loose sidecar still needs the upload pipeline: neither
    /// uploaded, nor rejected, nor content-hash already known. The hash check
    /// mirrors the uploader's F-M17c final net (user request 13.09.2026:
    /// seeder decides sidecars via content hashes). File reads only happen
    /// for sidecars that pass the registry lookups — settled items cost two
    /// dictionary hits and no I/O.
    /// </summary>
    private bool SidecarsNeedWork(List<(string Path, string Lang, bool HearingImpaired)> loose, string mediaHash)
    {
        var db = Plugin.Instance!.SharedDbContext;
        var registry = new Registry.ContentHashRegistry(db);

        foreach (var (loosePath, looseLang, looseHi) in loose)
        {
            if (registry.IsUploaded(mediaHash, looseLang, looseHi))
            {
                continue;
            }

            // Sidecars are keyed by content, so the rejection check needs the file's content —
            // read it once, then ask both questions against the same hash.
            string looseContent;
            try
            {
                looseContent = File.ReadAllText(loosePath);
            }
            catch
            {
                return true; // unreadable sidecar → let the pipeline decide
            }

            string looseHash = Registry.ContentHashRegistry.ComputeHash(looseContent);
            if (registry.IsSidecarUploaded(looseHash) || registry.SidecarRejectedReason(looseHash) != null)
            {
                continue;
            }

            try
            {
                string content = File.ReadAllText(loosePath);
                if (registry.IsContentKnown(Registry.ContentHashRegistry.ComputeHash(content)))
                {
                    continue;
                }
            }
            catch
            {
                return true; // unreadable sidecar → let the pipeline decide
            }

            return true;
        }

        return false;
    }

    private List<string> TargetLanguagesOf(Configuration.PluginConfiguration config)
        => config.DownloadLanguages
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.ToUpperInvariant())
            .Distinct()
            .ToList();

    /// <summary>Parity with DownloadPipeline.MissingLanguages — now via the one name reader (F-M239).</summary>
    /// <param name="item">Jellyfin item.</param>
    /// <param name="mediaPath">Media file path.</param>
    /// <param name="targets">Configured target languages.</param>
    /// <param name="onlyMissing">True when embedded streams count as present.</param>
    /// <returns>Target languages without file evidence.</returns>
    private List<string> MissingLanguagesOf(BaseItem item, string mediaPath, List<string> targets, bool onlyMissing)
    {
        // F-M239: the FOURTH copy of the name parse used to live here, and this one matters most —
        // it decides whether an item enters the download queue at all. It had the same defect as
        // the other copies (no "sdh" marker, so "sdh" became the phantom language "SD"), which
        // means a file whose only subtitle was Movie.de.sdh.srt looked like it was missing DE and
        // was queued for a search on every scan.
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, lang, _) in Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming.List(mediaPath))
        {
            present.Add(lang);
        }

        // F-M240: while the hearing-impaired switch is on, a language whose HI variant is absent is
        // "missing" for queueing too — otherwise the pipeline's own disk check could never fire,
        // because the seeder would not have queued the item in the first place.
        if (onlyMissing)
        {
            // F-M246: one rule with the pipeline — a forced track does not make its language present.
            foreach (var lang in Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming
                         .EmbeddedPresentLanguages(_mediaSourceManager.GetMediaStreams(item.Id)))
            {
                present.Add(lang);
            }
        }

        var missing = targets.Where(t => !present.Contains(t)).ToList();

        // F-M240: with the hearing-impaired switch on, a language whose HI variant is absent is
        // missing for QUEUEING as well. Without this the pipeline never sees the item — the
        // download mark would be invalidated there, but nothing would ever queue it, so the HI
        // variant still would not arrive.
        if (Plugin.Instance?.Configuration.DownloadHearingImpaired == true)
        {
            // F-M254: answered from the registry — the uploader stored which embedded tracks are
            // hearing-impaired, the downloader stored which fetched variants are. Asking the streams
            // again would ignore those verdicts: a track whose title Jellyfin does not surface read
            // as "HI absent" here while its own row said otherwise, so the item was queued for a
            // variant the container already carried.
            foreach (var l in Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming
                         .MissingHearingImpaired(targets, Plugin.Instance?.Registry.HearingImpairedLanguages(mediaPath)))
            {
                if (!missing.Contains(l, StringComparer.OrdinalIgnoreCase))
                {
                    missing.Add(l);
                }
            }
        }

        return missing;
    }

    /// <summary>
    /// F-M261 (user decision 30.09.2026): gives an untagged or <c>und</c> subtitle track its real
    /// language code, in the container and in the registry.
    /// <para>
    /// This runs in the SCAN, before <see cref="IsDownloadTodo"/> decides whether the item is
    /// queued. That order is the entire point: the queue gate reads a track's language through
    /// <see cref="LanguageMapper"/>, which answers "absent" for an untagged or <c>und</c> track —
    /// so the item was queued and searched for a language it was carrying all along. A resolution
    /// that has not happened yet cannot change that answer.
    /// </para>
    /// <para>
    /// The sequence is forced rather than chosen:
    /// <list type="number">
    /// <item>find the tracks that carry no usable tag (one ffmpeg pass, F-M5);</item>
    /// <item>detect their language offline, with the 2 KB floor (F-M74);</item>
    /// <item>write the found codes into the container (<c>-c copy</c>, verified before the original
    /// is replaced) — the file changes identity here;</item>
    /// <item>move every registry row of the file from the old hash to the new one, so the file keeps
    /// ONE identity and its marks are not orphaned;</item>
    /// <item>record the tracks under the new hash, so the queue gate below reads the corrected
    /// language.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Cost, measured on a 525 MB episode with 44 text tracks: 0.8 s extraction, 13.7 s detection,
    /// 29.9 s write. It hits a file once — after that its tracks carry tags and the gate has nothing
    /// to do. It is off by default for exactly that reason: a first pass over an untagged library is
    /// a long task, not a scan detail.
    /// </para>
    /// <para>
    /// Failure is swallowed per file: an unreadable container, an absent ffmpeg or a refused write
    /// leaves the file as it was and the scan continues. The item then stays queued exactly as
    /// before this change — the previous behaviour, never a worse one.
    /// </para>
    /// </summary>
    /// <param name="mediaPath">Media file to inspect and, when needed, to rewrite.</param>
    /// <param name="item">Jellyfin item the file belongs to.</param>
    private void AllocateMissingLanguageCodes(string mediaPath, BaseItem item)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.AllocateMissingLanguageCodes != true)
            {
                return;
            }

            var db = Plugin.Instance?.SharedDbContext;
            if (db == null)
            {
                return;
            }

            var registry = new Registry.ContentHashRegistry(db);
            var gate = new Pipeline.LanguageTagGate(_logger, config, registry);
            var streams = _mediaSourceManager.GetMediaStreams(item.Id);

            // A file whose every track already carries a usable tag has no work here, and this
            // answers that for the price of the stream list — no ffmpeg call at all.
            bool anyUntagged = streams != null && streams.Any(s =>
                s.Type == MediaStreamType.Subtitle && !s.IsExternal
                && !Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming.IsForcedStream(s)
                && s.IsTextSubtitleStream
                && Pipeline.LanguageTagGate.IsUntagged(s.Language));

            if (!anyUntagged)
            {
                return;
            }

            var resolved = gate.RunAsync(
                    mediaPath, streams, Pipeline.FfmpegTools.ResolvePath(config, _logger), System.Threading.CancellationToken.None)
                .GetAwaiter().GetResult();

            if (resolved.Written == 0)
            {
                return;
            }

            // The file was rewritten: it has a new identity. Move everything it owned across, or it
            // keeps two rows and the marks stay on the dead one (F-M261).
            if (resolved.OldHash != null && resolved.NewHash != null)
            {
                int moved = registry.ReplaceMediaIdentity(resolved.OldHash, resolved.NewHash);

                // F-M262: the write itself is reported by the gate at NORMAL — file, count, languages
                // and the identity pair. This line therefore carries only what the seeder adds and the
                // gate cannot know: how many registry rows the rename actually carried across.
                LogUtil.PerItem(config.LogMode, _logger,
                    "[SubDL-Seed] {File} — identity moved, {Moved} registry row(s) carried across",
                    System.IO.Path.GetFileName(mediaPath), moved);
            }

            // Record the tracks under the CURRENT hash — the queue gate below reads them from here.
            // NOT through ObserveEmbeddedFacts: that one reads Jellyfin's stream list, and Jellyfin
            // CACHES it, so the very tags this gate just wrote are still reported as "none" for the
            // rest of this scan. Writing the gate's own verdict is the only way the queue decision
            // below sees the corrected language (the same reason DownloadPipeline keeps
            // _resolvedByPosition alongside its own observation pass).
            string? newHash = resolved.NewHash ?? registry.GetMediaHash(mediaPath);
            if (!string.IsNullOrEmpty(newHash))
            {
                registry.EnsureMedia(newHash, item.Id.ToString("D"), mediaPath);
                foreach (var (subPos, lang, hi) in resolved.Tracks)
                {
                    registry.ObserveEmbed(newHash, subPos, lang, hi);
                }
            }

            // The remaining tracks of the file (the ones whose tag was already readable) are still
            // recorded by the normal pass.
            ObserveEmbeddedFacts(item, mediaPath);
        }
        catch (Exception ex)
        {
            // Never a precondition: the item is queued by the rules that applied before this gate.
            LogUtil.Detail(_logger, "[SubDL-Seed] language allocation skipped for {File}: {Msg}",
                System.IO.Path.GetFileName(mediaPath), ex.Message);
        }
    }

    /// <summary>
    /// F-M257: writes the embedded tracks of this file into the registry as OBSERVATIONS.
    /// <para>
    /// This is the write path the embedded area never had. It runs for every scanned item in both
    /// directions and regardless of <c>UploadEnabled</c>, because the facts are the file's, not a
    /// work decision: which language sits at which position, and whether that track is
    /// hearing-impaired. Readers (the HI question, F-M254) can then answer from the registry on an
    /// install where the uploader never runs.
    /// </para>
    /// <para>
    /// Positions are computed against the UNFILTERED subtitle list, exactly like the upload
    /// pipeline does: ffmpeg's <c>0:s:N</c> counts bitmap and forced tracks too, so an index taken
    /// from a filtered list would point at a different stream. Forced tracks are skipped for the
    /// same reason the pipeline skips them (F-M246) — a forced track carries a handful of cues for
    /// foreign-language scenes and is not a subtitle of the film.
    /// </para>
    /// <para>
    /// Failure is swallowed on purpose: an observation is an improvement, never a precondition. A
    /// scan that cannot read the streams must still queue its items normally.
    /// </para>
    /// </summary>
    /// <param name="item">Jellyfin item.</param>
    /// <param name="mediaPath">Media file path.</param>
    /// <returns>Number of rows created or changed.</returns>
    private int ObserveEmbeddedFacts(BaseItem item, string mediaPath)
    {
        try
        {
            var db = Plugin.Instance?.SharedDbContext;
            if (db == null)
            {
                return 0;
            }

            var registry = new Registry.ContentHashRegistry(db);
            string? mediaHash = registry.GetMediaHash(mediaPath);
            if (string.IsNullOrEmpty(mediaHash))
            {
                return 0;
            }

            // F-M257: one shared enumeration (SidecarNaming.EmbeddedTracks) — position against the
            // unfiltered list, forced tracks out, bitmap tracks out, unmappable tags out. A second
            // copy here would drift from the uploader's, which is how the four name parsers went wrong.
            var tracks = Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming
                .EmbeddedTracks(_mediaSourceManager.GetMediaStreams(item.Id));

            int written = 0;
            foreach (var (subPos, lang, hi) in tracks)
            {
                if (registry.ObserveEmbed(mediaHash, subPos, lang, hi))
                {
                    written++;
                }
            }

            return written;
        }
        catch (Exception ex)
        {
            // An observation is never a precondition — a file whose streams cannot be read is still
            // scanned and queued like any other.
            LogUtil.Detail(_logger, "[SubDL-Seed] embedded facts not recorded for {File}: {Msg}",
                System.IO.Path.GetFileName(mediaPath), ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// F-M259: writes the loose .srt files beside this media into the registry as OBSERVATIONS.
    /// <para>
    /// The sidecar area's only writers were the two pipelines, both behind their own switch — so a
    /// subtitle file that no plugin run ever touched (another tool wrote it, it predates the
    /// registry, or upload is off) had no row. The download side reads the DISK for "is the language
    /// there?" but asks the REGISTRY for the HI question (F-M254): for such a file the language
    /// looked present and its hearing-impaired variant looked absent, on every run.
    /// </para>
    /// <para>
    /// The read is the sidecar's content, because content is the sidecar's identity — the same rule
    /// the uploader follows, so a row observed here is the row the uploader would find. Failure is
    /// swallowed: an observation is an improvement, never a precondition.
    /// </para>
    /// </summary>
    /// <param name="mediaPath">Media file path.</param>
    /// <returns>Number of rows created or changed.</returns>
    private int ObserveSidecarFacts(string mediaPath)
    {
        try
        {
            var db = Plugin.Instance?.SharedDbContext;
            if (db == null)
            {
                return 0;
            }

            var registry = new Registry.ContentHashRegistry(db);
            string? mediaHash = registry.GetMediaHash(mediaPath);
            if (string.IsNullOrEmpty(mediaHash))
            {
                return 0;
            }

            int written = 0;
            // F-M239/F-M259: the shared name reader (SidecarNaming.List), NOT the seeder's own
            // FindLooseSrts below — that copy returns bare paths and carries no language or HI
            // marker, so it cannot answer the question this method records. Reading a name here with
            // a fifth parser is exactly how the earlier four drifted apart.
            foreach (var (loosePath, looseLang, looseHi) in
                     Pipeline.UploadPipeline.FindLooseSrts(mediaPath))
            {
                string content;
                try
                {
                    content = File.ReadAllText(loosePath);
                }
                catch
                {
                    continue; // unreadable — the pipeline's business, not an observation
                }

                string contentHash = Registry.ContentHashRegistry.ComputeHash(content);
                if (registry.ObserveSidecar(contentHash, mediaHash, looseLang, looseHi,
                        System.IO.Path.GetFileName(loosePath), loosePath))
                {
                    written++;
                }
            }

            return written;
        }
        catch (Exception ex)
        {
            LogUtil.Detail(_logger, "[SubDL-Seed] sidecar facts not recorded for {File}: {Msg}",
                System.IO.Path.GetFileName(mediaPath), ex.Message);
            return 0;
        }
    }

    /// <summary>Loose sidecar SRTs next to the media file.</summary>
    private List<string> FindLooseSrts(string mediaPath)
    {
        var result = new List<string>();
        try
        {
            string dir = System.IO.Path.GetDirectoryName(mediaPath) ?? ".";
            string baseName = System.IO.Path.GetFileNameWithoutExtension(mediaPath);
            foreach (var f in Directory.EnumerateFiles(dir, baseName + "*.srt"))
            {
                if (f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string name = System.IO.Path.GetFileNameWithoutExtension(f);
                if (!name.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                {
                    // labelled sidecar (<base>.<lang>.srt) or unlabeled — both count
                    result.Add(f);
                }
            }
        }
        catch
        {
            // listing failed — no loose SRTs assumed
        }

        return result;
    }


    private static QueueItem Clone(QueueItem qi) => new()
    {
        ItemId = qi.ItemId,
        Path = qi.Path,
        Name = qi.Name
    };
}
