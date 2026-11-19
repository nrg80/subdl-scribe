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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Api;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubScribe.Configuration;
using Jellyfin.Plugin.SubScribe.Filters;
using Jellyfin.Plugin.SubScribe.Language;
using Jellyfin.Plugin.SubScribe.Registry;
using Jellyfin.Plugin.SubScribe.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;
using Movie = MediaBrowser.Controller.Entities.Movies.Movie;
using Jellyfin.Plugin.SubScribe.Data;

namespace Jellyfin.Plugin.SubScribe.Pipeline;

/// <summary>
/// Summary of one pipeline run (F-M23 basis).
/// </summary>
public class RunSummary
{
    /// <summary>F-M247: true when this run was a dry run — it did the work but wrote nothing.
    /// The statistics counters ignore everything a dry run produced; the run-end log line
    /// labels its numbers as hypothetical instead of claiming uploads that never happened.</summary>
    public bool IsDryRun { get; set; }

    /// <summary>Gets or sets the number of uploaded subtitles.</summary>
    public int Uploaded { get; set; }

    /// <summary>Gets or sets the number of failed uploads.</summary>
    public int Failed { get; set; }

    /// <summary>
    /// F-M609: Versuche, die die Quelle (oder eine unserer Vorpruefungen) mit NEIN beantwortet hat.
    /// Das Paar ist damit geschlossen.
    /// </summary>
    public int Refused { get; set; }

    /// <summary>
    /// F-M609: Versuche ohne Urteil — Timeout, Verbindungsfehler, 429, 5xx, Stundendeckel, Abbruch.
    /// Das Paar bleibt offen und wird beim naechsten Lauf neu gefragt.
    /// </summary>
    public int NoVerdicts { get; set; }

    /// <summary>F-M81z-2 (user decision 10.09.2026): run ended by hitting the daily
    /// quota (429) — a clean rescheduled stop, NOT an upload failure. Own counter
    /// in the Task finished line, symmetric to the download direction.</summary>
    public int QuotaStopped { get; set; }

    /// <summary>F-M94h: run was not started because another run holds the global lock.</summary>
    public bool SkippedByLock { get; set; }

    /// <summary>Gets or sets the number of skipped subtitle streams.</summary>
    public int SkippedStreams { get; set; }

    /// <summary>Gets or sets the count of items skipped by dir/file patterns (F-M24d aggregate).</summary>
    public int SkippedByFilter { get; set; }

    /// <summary>Gets or sets the number of skipped items (filters, no-imdb).</summary>
    public int SkippedItems { get; set; }

    /// <summary>Gets or sets the count of items skipped for missing IMDB/season/episode.</summary>
    public int SkippedNoImdb { get; set; }

    /// <summary>
    /// F-M286: candidates DISCARDED from the upload this run — every reject path, not the QA gates
    /// alone. F-M218 said "counted AT the gate, not at the skip counter"; the code implemented that at
    /// exactly one gate while 16 other paths recorded a Rejected verdict and counted nothing, so the
    /// GUI's upload column reported a fraction of the truth. This counter is the whole set:
    /// QA gates, und-off, unmapped language, self-echo, duplicate-remote, forced (F-M284).
    /// </summary>
    public int RejectedCandidates { get; set; }

    /// <summary>
    /// F-M595 (Operator-Anweisung 18.11.2026: „Stopped before sending bitte nur noch für qa fails in
    /// die Statistik. Wording: Uploads qa fails not uploaded"): die Zahl, die als Statistik geht.
    /// Nur Urteile der QUALITAETS-Gates (Groessenboden, Cue-Zahl, SRT-Struktur, Sync) zaehlen hier —
    /// Duplikate, fehlende Kennungen, Extraktionsfehler und gesperrte Quellen sind Umstaende des
    /// Laufs, kein Urteil ueber den Untertitel. `RejectedCandidates` bleibt als Laufzahl stehen.
    /// </summary>
    public int RejectedQaFail { get; set; }

    /// <summary>
    /// F-M539: Eintraege, die vor dem ffmpeg-Durchgang als erledigt erkannt wurden — jede Textspur hat
    /// ihren Endstand im Store und keine erlaubte Quelle will noch ein Paar dieser Datei.
    /// </summary>
    public int SettledItems { get; set; }

    /// <summary>Gets or sets the number of rate-limit hits this run.</summary>
    public int RateLimitHits { get; set; }

    /// <summary>Gets or sets the number of media files processed.</summary>
    public int FilesSeen { get; set; }

    /// <summary>
    /// Distinct media files with at least one uploaded subtitle this run.
    /// Upload counters are per subtitle STREAM (one file can upload 19 languages),
    /// so the status card reports "X subtitles in Y files" — Y comes from here.
    /// </summary>
    public int FilesUploaded { get; set; }

    /// <summary>
    /// Distinct media files with candidate streams but zero uploads this run
    /// (all streams skipped/failed). Reported as "N files skipped".
    /// </summary>
    public int FilesSkipped { get; set; }

    /// <summary>
    /// User pressed the stop button during this run.
    /// </summary>
    public bool StopRequested { get; set; }

}

/// <summary>
/// Core upload pipeline (B1): discovery → skip filters → text-sub extraction →
/// dedup → IMDB resolution → upload. QA gates are B2 (F-M13–M17), not in this version.
/// <para>
/// F-M389: it is also the pack filler — a season whose missing episode is already uploaded is
/// completed from the library instead of being dissolved into single uploads.
/// </para>
/// </summary>
public sealed class UploadPipeline : IPackFiller
{
    /// <summary>
    /// F-M613: meldet der Pumpe die naechsten bis zu drei Dateien, die wirklich Arbeit brauchen
    /// (in der Warteschlange und mit Textspuren). Wird an JEDEM Gegenstand aufgerufen, auch wenn er
    /// uebersprungen wird — sonst laeuft die Pumpe leer und jede Extraktion faengt kalt an.
    /// </summary>
    /// <param name="vorlauf">Die Pumpe.</param>
    /// <param name="items">Die Gegenstaende des Laufs.</param>
    /// <param name="itemIndex">Wo der Lauf steht.</param>
    /// <param name="runCt">Abbruch des Laufs.</param>
    private void MeldeVorlauf(
        ExtractionLookahead vorlauf,
        IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem> items,
        int itemIndex,
        CancellationToken runCt)
    {
        var kandidaten = new List<(string Path, List<int> Positions)>();
        for (int i = itemIndex + 1; i < items.Count && kandidaten.Count < 3; i++)
        {
            var naechste = items[i];
            if (string.IsNullOrWhiteSpace(naechste.Path))
            {
                continue;
            }

            if (QueueFilter != null && !QueueFilter.Contains(naechste.Id.ToString()))
            {
                continue;
            }

            var positionen = TextSubtitlePositions(naechste.Id);
            if (positionen.Count == 0)
            {
                continue;
            }

            kandidaten.Add((naechste.Path!, positionen));
        }

        if (kandidaten.Count > 0)
        {
            vorlauf.Plan(kandidaten, _ffmpegPath, _logger, _config, runCt);
        }
    }
        /// <summary>
        /// Current postprocessing state for monitoring /Plugins/SubdlSync/PostprocessStatus.
        /// </summary>
        private static bool _postprocessingRunning;
    private static DateTime _postprocessingStartUtc = DateTime.MinValue;
        private static DateTime _postprocessingStartedUtc;
        private static int _postprocessingPendingTotal;
        private static int _postprocessingPendingRemaining;
        private static int _postprocessingResolved;
        private static int _postprocessingAccepted;
        private static int _postprocessingRejected;
        private static int _postprocessingDeleted;
        private static string _postprocessingLastResult = "never run";

        /// <summary>True while an upload postprocessing run is active.</summary>
        public static bool IsPostprocessingRunning => _postprocessingRunning;

        /// <summary>UTC start time of the current/last postprocessing run.</summary>
        public static DateTime PostprocessingStartedUtc => _postprocessingStartedUtc;

        /// <summary>Pending entries at the start of the current/last run.</summary>
        public static int PostprocessingPendingTotal => _postprocessingPendingTotal;

        /// <summary>Pending entries still unresolved after the current/last run.</summary>
        public static int PostprocessingPendingRemaining => _postprocessingPendingRemaining;

        /// <summary>Pending entries resolved in the current/last run.</summary>
        public static int PostprocessingResolved => _postprocessingResolved;

        /// <summary>Accepted uploads found in the current/last run.</summary>
        public static int PostprocessingAccepted => _postprocessingAccepted;

        /// <summary>Rejected/duplicate uploads found in the current/last run.</summary>
        public static int PostprocessingRejected => _postprocessingRejected;

        /// <summary>Dashboard uploads deleted in the current/last run.</summary>
        public static int PostprocessingDeleted => _postprocessingDeleted;

        /// <summary>Human-readable result of the current/last run.</summary>
        public static string PostprocessingLastResult => _postprocessingLastResult;

    private readonly ILogger<UploadPipeline> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ScribeApiClient _api;
    /// <summary>Always use the current Plugin.Instance registry so a DB reset is respected.</summary>
    private ContentHashRegistry Registry => Plugin.Instance?.Registry ?? throw new InvalidOperationException("Plugin not initialized");
    private readonly TmdbImdbResolver _tmdb;
    private readonly PluginConfiguration _config;
    private readonly GlobalRateLimiter _limiter;

    /// <summary>
    /// F-M501: der Client der zweiten Quelle und die Auflösung ihrer Seriennummer. Beide leben so lange
    /// wie die Pipeline, weil die Anlege-Sperre EINEN LAUF lang gilt (<see cref="Api.Endpoints.TvstShowResolver.BeginRun"/>):
    /// ein je Aufruf neuer Auflöser würde dieselbe Serie bei jeder Folge erneut anzulegen versuchen.
    /// </summary>
    private readonly Api.TvSubtitlesClient _tvstClient;

    /// <summary>F-M501: die Seriennummer-Auflösung der zweiten Quelle (gespeichert → eng gesucht → angelegt).</summary>
    private readonly Api.Endpoints.TvstShowResolver _tvstShows;

    /// <summary>
    /// F-M508: der Client der Film-Adresse derselben Familie und ihre Filmmummer-Auflösung. Dieselbe
    /// Lebensdauer wie die der Serien-Adresse — die Merkliste der Auflösung gilt je Lauf
    /// (<see cref="Api.Endpoints.MsubMovieResolver.BeginRun"/>), damit ein Film mit vier fehlenden
    /// Sprachen nicht viermal bei der Seite sucht.
    /// </summary>
    private readonly Api.MovieSubtitlesClient _msubClient;

    /// <summary>F-M508: die Filmmummer-Auflösung der Film-Adresse (gespeichert → eng gesucht).</summary>
    private readonly Api.Endpoints.MsubMovieResolver _msubMovies;

    /// <summary>
    /// F-M389: one item per (series IMDb id, season) that this run worked — the way into a season
    /// whose episodes are NOT all in the queue. Jellyfin stores the SERIES id on the series, never
    /// on the episode, so the season is found through an episode rather than by a provider query.
    /// </summary>
    private readonly Dictionary<(string ImdbId, int Season), BaseItem> _seasonAnchors = new();

    /// <summary>
    /// F-M389: the episode children of a season, per (series IMDb id, season), read once for all
    /// fills of that season. A filler call costs a library walk; eight of them must not cost eight.
    /// </summary>
    private readonly Dictionary<(string ImdbId, int Season), List<BaseItem>> _seasonEpisodes = new();

    /// <summary>
    /// F-M389: the episode items of every COMPLETE season found at run start. The fill-only plan needs
    /// them (a season the queue no longer touches has no anchor at all) and the season index already
    /// walked the library once — a second walk for the same answer would be waste.
    /// </summary>
    private readonly Dictionary<(string ImdbId, int Season), List<BaseItem>> _completeSeasonItems = new();

    /// <summary>
    /// F-M588 (Operator-Anweisung 17.11.2026: „Ok für mysubtitels"): ein zurueckgestellter Upload,
    /// dessen SubDL-Id noch fehlt. Statt nach JEDEM Upload die vollstaendige eigene Liste zu ziehen
    /// (gemessen: 340 kB / 798 Eintraege je Upload, 2.790-mal am Tag), wird die Id am Laufende
    /// einmal fuer alle offenen Eintraege geholt und hier eingetragen.
    /// </summary>
    /// <param name="ImdbId">IMDb id of the title, when known.</param>
    /// <param name="Season">Season, 0 for a film.</param>
    /// <param name="Episode">Episode, 0 for a film.</param>
    /// <param name="Lang">Language code as we store it (TR, not "Turkish").</param>
    /// <param name="Hi">Hearing-impaired flag of this track.</param>
    /// <param name="MediaHash">Media hash the mark belongs to.</param>
    /// <param name="SubPos">Stream position of the embedded track.</param>
    /// <param name="Loose">True when the upload was a sidecar file rather than an embedded stream.</param>
    /// <param name="FileName">Sidecar file name, when loose.</param>
    /// <param name="At">When the upload happened — the order of the claims.</param>
    private sealed record PendingSubdlId(
        string? ImdbId, int Season, int Episode, string Lang, bool Hi,
        string? MediaHash, int SubPos, bool Loose, string? FileName, string? Release, DateTime At);

    /// <summary>F-M588: the uploads of this run whose SubDL id still has to be read back.</summary>
    private readonly List<PendingSubdlId> _offeneSubdlIds = new();

    /// <summary>
    /// F-M590 (Operator-Anweisung 17.11.2026: „notieren muss pro quelle direkt nach dem upload
    /// funktionieren"): die Marke einer Quelle wird geschrieben, sobald DIESE Quelle geantwortet hat —
    /// nicht erst, wenn die ganze Kette durch ist. Vorher ging eine bereits angenommene Einreichung
    /// verloren, wenn die zweite Quelle danach in den Timeout lief und der Lauf abbrach (gemessen
    /// 17.11.2026, `A.Talent.for.Murder.2026`: SubDL hatte angenommen, moviesubtitles antwortete nie).
    /// Seit F-M587 laufen die Quellen gleichzeitig — deshalb schreibt dieser Pfad unter einem Schloss.
    /// </summary>
    private readonly object _sofortMarkeLock = new();

    /// <summary>
    /// F-M590: wie viele Spuren hintereinander OHNE Antwort irgendeiner Quelle geblieben sind. Erst wenn
    /// alle Quellen weg sind, bricht der Lauf ab — eine einzelne tote Seite ist ein Fehlschlag dieser
    /// Quelle, kein Abbruch des Laufs.
    /// </summary>
    private int _quellenAusfaelleInFolge;

    /// <summary>
    /// F-M571: die Pack-Paare je Staffel einmal je Lauf. Der Planer fragt dieselbe Staffel mehrfach
    /// (Sammler, Ankunftszweig, Plan) — die Antwort aendert sich innerhalb eines Laufs nicht.
    /// </summary>
    private readonly Dictionary<(string ImdbId, int Season), HashSet<(string Language, bool HearingImpaired)>> _packsInSeason = new();

    /// <summary>
    /// F-M575: die beiden Sammel-Karten — EINMAL je Lauf gefuellt, danach nur noch befragt. Gemessen:
    /// je Staffel kostete die Einzelfrage einen vollen Durchlauf durch alle Untertitel-Zeilen (9.385), bei
    /// 125 Staffeln also 125 Durchlaeufe und rund 6 Sekunden je Staffel.
    /// </summary>
    private Dictionary<(string ImdbId, int Season), HashSet<(string Language, bool HearingImpaired)>>? _allePacks;

    /// <summary>F-M575: Sprachen aller Staffeln, einmal je Lauf gelesen.</summary>
    private Dictionary<(string ImdbId, int Season), HashSet<(string Language, bool HearingImpaired)>>? _alleSprachen;

    /// <summary>
    /// F-M429 (operator order 03.11.2026: "Bei packbildung identifizierte einzeluploads für später den
    /// einzelupload merken"): the (series, season, language, variant) pairs a pack of THIS run actually
    /// carried. A single position covered by such a pair is skipped later in the run — the pack is the
    /// only carrier. Positions whose pack failed are absent, so their single goes up as usual.
    /// </summary>
    private readonly HashSet<(string ImdbId, int Season, string Lang, bool Hi)> _packAbsorbed = new();

    /// <summary>
    /// F-M577 (Operator-Entscheidung 17.11.2026, Variante B): wie lange die Ablehnung
    /// „Extraktion gescheitert" gilt, bevor die Spur ihren naechsten Versuch bekommt.
    /// </summary>
    private static readonly TimeSpan ExtractRetryAfter = TimeSpan.FromDays(7);

    /// <summary>F-M88c/d: central OSHash cache (oshash-cache.json in the plugin data dir).</summary>
    /// Always use the current Plugin.Instance cache so a DB reset is respected.<
    private Registry.OshashCache OshashCache => Plugin.Instance?.SharedOshashCache ?? throw new InvalidOperationException("Plugin not initialized");

    /// <summary>Resolved ffmpeg path used to extract embedded text subtitles.</summary>
    private readonly string _ffmpegPath;

    /// <summary>
    /// F-M405 (operator order 02.11.2026): the pack filler extracts a file ONCE per run, not once per
    /// language. Filling a season for 37 languages meant 37 full passes over the same container — for 8
    /// episodes that is 296 re-reads where 8 do. The map is keyed by media path and lives exactly as
    /// long as the run.
    /// </summary>
    // F-M441: the extracted texts of this run live on disk, one file per episode/language/track.
    private readonly Api.RunWorkFiles _workFiles = new();

    /// <summary>Process-static cache so ffmpeg is resolved once per plugin lifetime.</summary>
    private static string? _cachedFfmpegPath;

    /// <summary>Cache key — the FfmpegPath override that produced the cached path.</summary>
    private static string _cachedFfmpegConfig = "\x11NITIAL\x11"; // sentinel unlikely to match any config value

    /// <summary>F-M17y: serialized by the single global PipelineRunLock — no separate semaphore.</summary>
    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            return proc.HasExited == false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception)
        {
            return true; // cannot tell — assume alive (fail-safe)
        }
    }

    /// <summary>F-M17y: throttle upload postprocessing scheduling (at most once per minute).</summary>
    private static DateTime _lastPostprocessingScheduled = DateTime.MinValue;

    /// <summary>F-M49: daily-limit waits used this run (max 1 — second hit stops the run).</summary>
    private int _dailyLimitWaits;

    /// <summary>
    /// (user decision 09.09.2026, option B): arrival runs NEVER wait out the
    /// Daily limit — they stop clean and leave the quota wait (, 2-4h random
    /// post-reset offset) to the scheduled runs. Rationale: the arrival cycle runs
    /// download THEN upload sequentially; a multi-hour download wait would block the
    /// upload direction (verified live: The Runner arrival 06:25, download waited
    /// 23.5h and the upload of 38 subtitle streams never started although the API
    /// request quota was fine). Scheduled tasks (IsArrivalRun=false) keep the wait.
    /// </summary>
    public bool IsArrivalRun { get; set; }

    // F-M425 (operator order 02.11.2026): there is no per-trigger difference left. Every run — arrival,
    // refetch, manual, scheduled — asks the SAME question: are the episodes this run queued the whole
    // season? If yes, a pack is built out of those items and nothing else; if no, the run stays a single
    // uploader. The old fill-only route (a container assembled from what an earlier run had left in the
    // store) is gone: it produced packs out of material no run had touched.

    // ── F-M111: queue-driven operation (user decision 12.09.2026) ───────────
    // Same contract as the download pipeline: QueueFilter restricts the item
    // loop to queued ids; ItemResult reports per-item outcomes for the queue.
    public HashSet<string>? QueueFilter { get; set; }

    /// <summary>
    /// F-M546 (Operator-Frage 16.11.2026: „Wieso machen wir dann nicht eine queue pro quelle?"): die
    /// Queue JE QUELLE. <see cref="QueueFilter"/> bleibt die Vereinigung (welche Items der Lauf
    /// ueberhaupt anfasst); hier steht, welche Quelle welches Item eingereiht hat. Damit fragt der Lauf
    /// eine Datei nur noch gegen die Quellen, die sie wollen — eine Quelle, die das Item nicht
    /// eingereiht hat, haelt es nicht mehr offen, und der Kopf-Skip ist wieder die billige
    /// Eine-Marke-Frage von 12.1.12.378, nur je Quelle.
    /// </summary>
    public Dictionary<Configuration.SubtitleProvider, HashSet<string>>? QueueFilterBySource { get; set; }

    /// <summary>F-M546: die Quellen, die das AKTUELLE Item eingereiht haben (null = unbekannt).</summary>
    private HashSet<Configuration.SubtitleProvider>? _queuedSourcesForItem;

    /// <summary>Per-item outcome report for the queue bookkeeping (fired per item).</summary>
    public event Action<string, ItemOutcome>? ItemResult;

    /// <summary>Per-item result class reported back to the queue bookkeeping.</summary>
    public enum ItemOutcome { Done, RealFailure, NotAvailable }

    /// <summary>Fires ItemResult — no-op when nobody listens.</summary>
    private void ReportOutcome(MediaBrowser.Controller.Entities.BaseItem item, ItemOutcome outcome)
    {
        try
        {
            ItemResult?.Invoke(item.Id.ToString(), outcome);
        }
        catch
        {
            // Queue bookkeeping must never break the pipeline.
        }
    }

    /// <summary>F-M94h: run was not started because another run holds the global lock.</summary>
    public bool SkippedByLock { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="UploadPipeline"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSourceManager">Media source manager (stream lookup in 10.11).</param>
    /// <param name="api">SubDL API client.</param>
    /// <param name="registry">Content hash registry.</param>
    /// <param name="tmdb">TMDB→IMDB resolver (required — gates SERIES items, F-M203).</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="limiter">Global rate limiter shared with the download pipeline (F-M20/F-M26).</param>
    public UploadPipeline(
        ILogger<UploadPipeline> logger,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        ScribeApiClient api,
        TmdbImdbResolver tmdb,
        PluginConfiguration config,
        GlobalRateLimiter limiter)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _api = api;
        _tmdb = tmdb;
        _config = config;
        _limiter = limiter;
        // F-M88c/d (user decision): OSHash cache lives in the plugin data dir via
        // the OshashCache property. Read-only shares keep their cache,
        // library folders stay clean.
        _ffmpegPath = FfmpegTools.ResolvePath(config, logger);

        // F-M501: EIN Client und EIN Auflöser für den ganzen Lauf — siehe die Feldbemerkungen oben.
        _tvstClient = new Api.TvSubtitlesClient(ScribeApiClient.NewHttpClient(TimeSpan.FromSeconds(90)));

        // F-M521 (Operator-Anweisung 17.11.2026: „Und logging für uploads?"): the upload direction of
        // the tvst family is traced too — the same two Debug lines per request as SubDL, so an upload
        // run proves which address it posted to and what came back.
        _tvstClient.DebugTrace += OnApiTrace;
        _tvstShows = new Api.Endpoints.TvstShowResolver(_tvstClient, logger);

        // F-M508: dasselbe für die Film-Adresse der Familie — ein Client, ein Auflöser, ein Lauf.
        _msubClient = new Api.MovieSubtitlesClient(ScribeApiClient.NewHttpClient(TimeSpan.FromSeconds(90)));
        _msubClient.DebugTrace += OnApiTrace;
        _msubMovies = new Api.Endpoints.MsubMovieResolver(_msubClient, logger);
    }

    /// <summary>
    /// Runs a full pipeline pass over the selected libraries.
    /// </summary>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Run summary.</returns>
    public async Task<RunSummary> RunAsync(IProgress<double> progress, CancellationToken ct)
    {
        var summary = new RunSummary();

        // F-M598 (Operator-Anweisung 18.11.2026: „Auch gerne Diagnose Punkte (log level detail) für den
        // ganzen upload vorlauf (pack usw) und den download einführen."): der Lauf selbst wird vermessen,
        // nicht nur die einzelne Spur. Die Zeilen stehen auf `Detail` (also im Debug-LogMode) und nennen
        // je Abschnitt die Dauer: Item-Liste, Pack-Phase, Item-Schleife, Lauf gesamt.
        var laufTakt = System.Diagnostics.Stopwatch.StartNew();

        // F-M445 (operator order 02.11.2026, "bestehende Aufträge bereits beim Upload-Start stornieren"):
        // an order left over from an earlier run is dropped the moment THIS run starts. It would fire
        // against state this run is about to change, and the run's own end sets the new moment — so the
        // order is cancelled here and armed again in the finally below.
        ScheduledTasks.ScribeSchedulerCoordinator.Instance?.CancelUploadPostprocessing("upload run started");

        // F-M501: die Anlege-Sperre der tvst-Serienauflösung gilt je Lauf, nicht je Prozess — hier ist
        // der Anfang des Laufs, also hier wird sie geleert.
        _tvstShows.BeginRun();

        // F-M508: die Merkliste der Film-Adresse ebenso — sie gilt je Lauf, nicht je Prozess.
        _msubMovies.BeginRun();

        // F-M247: record that this run was a dry run. It does the searching and the QA, so its
        // summary fills up with numbers, but it writes nothing to SubDL — the statistics must
        // not receive them. Set here, at the source, so a later counter cannot forget it.
        summary.IsDryRun = _config.DryRun;

        // F-M94h (rework 25.09.2026): ONE global run lock for all six components.
        // No long wait: a brief hand-off grace only, then reschedule by JobSpacingMinutes.
        // The _uploadWorkLock semaphore that used to sit here was removed: it guarded
        // nothing the global lock did not already cover, and its WaitAsync sat OUTSIDE
        // the try block, so a cancellation between the two acquires leaked the run lock.
        if (!await PipelineRunLock.AcquireAsync(_logger, "upload", ct).ConfigureAwait(false))
        {
            summary.SkippedByLock = true;
            var spacingUp = JobSpacingMinutes();
            ScheduledTasks.ScribeSchedulerCoordinator.Instance?.ScheduleRecoveryFireAt(upload: true, DateTime.UtcNow.AddMinutes(spacingUp));
            LogUtil.Normal(_logger, "[Sub Scribe] Upload deferred by {Spacing} min — global run lock busy.", spacingUp);
            return summary;
        }

        Registry.BeginBatch(); // F-M602: ein Commit je Lauf (bzw. je 500 Zeilen) statt zwei je Spur

        try
        {
        // F-M441: a run that was killed leaves its working directory behind — swept here, so a crash
        // costs temp disk until the next start and never longer.
        int sweptDirs = Api.RunWorkFiles.PurgeStale(null, TimeSpan.FromDays(2));
        if (sweptDirs > 0)
        {
            LogUtil.Normal(_logger, "[Sub Scribe] upload: {Count} left-over working directorie(s) of an earlier run removed.", sweptDirs);
        }

        var skipFilter = new SkipFilter(_config.EffectiveSkipDirPatterns, _config.EffectiveSkipFilePatterns);

        // F-M19/F-M203: no partial operation. Without all four credentials the run cannot
        // do its job, and the failure would otherwise surface later at the first call that
        // needs the missing one (the login 404, a search without an API key, an id that
        // cannot be resolved). Refused here, with the fields named.
        var missingUp = _config.MissingCredentials();
        if (missingUp.Count > 0)
        {
            LogUtil.Normal(_logger, "[Sub Scribe] upload: NOT started — missing credential(s): {Missing}. Set them in the plugin configuration.", string.Join(", ", missingUp));
            return summary;
        }

        if (_config.SelectedLibraries.Count == 0)
        {
            LogUtil.Normal(_logger, "[Sub Scribe] No libraries selected — nothing to do.");
            return summary;
        }

        // NF-4: in-run watchdog — fixed 6-min grace, independent of the pacing knobs.
        // ApiActivity fires after every completed API round-trip.
        using var watchdog = new RunWatchdog(
            "SubDL", ct,
            RunWatchdog.Grace,
            msg => _logger.LogError("{Msg}", msg));
        _api.ApiActivity += watchdog.Heartbeat;
        var runCt = watchdog.Token;
        watchdog.Heartbeat();
        watchdog.Start();

        // F-M24a (fixed 09.09.2026): run START is Normal-level (user decision) —
        // critical errors, rate limits, run started/finished.
        // (10.09.2026): moved AFTER CollectItems — FilesSeen was always 0 here,
        // logging "0 items queued" although the scan runs 15 lines later and found 304.
        // F-M403 (operator order 02.11.2026): one spelling for the mode. Inline it is the lowercase
        // word "dry" (as in the workers line), on an event line it is the prefix "DRY-RUN".
        LogUtil.Normal(_logger, _config.DryRun
            ? "[Sub Scribe] upload: started, dry"
            : "[Sub Scribe] upload: started");

        PipelineStopSignal.ClearStaleMarkers(Plugin.Instance?.DataFolderPath ?? string.Empty);

        // F-M24a: forward per-call API traces to LogLevel.Debug
        _api.DebugTrace += OnApiTrace;

        // F-M232: the client's LogInfo channel (login retries, 429 raw bodies, rate
        // headers) was subscribed ONLY by the postprocessing path, so every upload-run
        // login diagnostic was dropped — the 02:05 login failure left no retry line
        // behind, only the raw trace. Subscribe it per run like the trace channel.
        _api.Log += OnApiLog;

        // F-M24a (user decision 09.09.2026): TMDB calls trace at VERBOSE (SubDL API calls are Debug).
        _tmdb.Trace += OnTmdbTrace;

        // F-M54: wrong TMDB key reported ONCE at Normal/Error level.
        _tmdb.AuthError += msg => _logger.LogError("[Sub Scribe] {Msg}", msg);

        // F-M190 (24.09.2026, user decision): a JF-vs-TMDB id disagreement is
        // reported at Normal level (the debug trace alone was invisible), and
        // TMDB wins. Detached on run end with the other TMDB handlers.
        void OnIdMismatch(string msg) => LogUtil.Normal(_logger, "[Sub Scribe] {Msg}", msg);
        _tmdb.IdMismatch += OnIdMismatch;

        // The 1-min metadata grace lives in the ARRIVAL WATCHER (once per
        // event, before the download run which fires first) — not here. Scheduled
        // runs never get one. No-id items still have the per-item 15-min ladder.

        // F-M389: one run, one view of the seasons it works. A fresh pipeline is built per run, but
        // an anchor must never outlive the selection it was read under.
        _seasonAnchors.Clear();
        _seasonEpisodes.Clear();
        _completeSeasonItems.Clear();
        _packsInSeason.Clear();
        _packAbsorbed.Clear(); // F-M429: the memory of this run's packs, not the previous run's

        var items = CollectItems(_config.SelectedLibraries);
        summary.FilesSeen = items.Count; // (not logged — stats-free log)

        // (user decision 08.09.2026): id-less items go to the BACK — their
        // 15-min metadata ladder must not block items that already have ids.
        // Stable partition, no TMDB calls (raw metadata only).
        var withIds = new List<BaseItem>();
        var withoutIds = new List<BaseItem>();
        foreach (var it in items)
        {
            var (im, tm, _, _, _) = ResolveIdsRaw(it);
            (string.IsNullOrWhiteSpace(im) && string.IsNullOrWhiteSpace(tm) ? withoutIds : withIds).Add(it);
        }

        if (withoutIds.Count > 0)
        {
            // No count line — partition stays internal.
            items = withIds.Concat(withoutIds).ToList();
        }

        LogUtil.Detail(_logger, "[Sub Scribe] LAUF upload — {Count} item(s) collected and ordered after {Ms} ms (F-M598)", items.Count, laufTakt.ElapsedMilliseconds);

        // F-M59 (user decision 09.09.2026): LIFO within BOTH partitions — newest items
        // (DateCreated descending) get the daily quota first, old library stock last.
        // LIFO queue order (F-M59): a brand-new arrival must not lose
        // the quota race against 284 legacy items (verified live 09.09.2026: The Runner,
        // arrived 06:14, hit the daily limit after the legacy stock had burned the
        // requests). The id-partition keeps priority (user decision: "id-less items go
        // to the back") — id-less items stay at the back even when newer.
        items = items
            .Select((it, i) => (Item: it, Pos: i, Created: it.DateCreated))
            .OrderByDescending(x => x.Created)
            .ThenBy(x => x.Pos)
            .Select(x => x.Item)
            .ToList();

        // F-M372: can this season become a pack at all? A season is complete when the episodes the
        // library holds are exactly the episodes that aired (IMDb, TMDB as the fallback). A run that
        // sees only part of a season does not make it incomplete — the folder decides, not the queue.
        //
        // F-M420 (operator order 02.11.2026): "refetch und manual: erst einzelupload, dann
        // staffelpack". Nothing is held back for a pack any more. Every position goes up the SINGLE
        // way first, and the pack is assembled at the END of the run out of what is in the store by
        // then — this run's own uploads included — deleting the singles it absorbs (F-M391). An
        // arrival run never builds a LIBRARY pack: it carries only its own arrivals, so a pack can only
        // come from those items and nothing else (F-M424, below).
        SeasonPackCoordinator? packCoordinator = null;
        IReadOnlyDictionary<(string ImdbId, int Season), IReadOnlyList<int>> completeSeasons
            = new Dictionary<(string ImdbId, int Season), IReadOnlyList<int>>();
        // F-M424 (operator order 02.11.2026): "on arrival to investigate first if the queued items can
        // form a pack. If so pls do, but only with the queued items." An arrival run therefore asks the
        // same completeness question — but the answer it needs is narrower: it does not care whether
        // the whole LIBRARY is complete, only whether the episodes that arrived in THIS run are the
        // whole season. If they are, the pack is built out of exactly those items and nothing else is
        // pulled in from the store; if they are not, the run stays what it was, a single uploader.
        if (_config.UploadSeasonPacks)
        {
            completeSeasons = await CompleteSeasonsAsync(runCt).ConfigureAwait(false);
            var queued = QueuedEpisodes(items);
            var arrivalSeasons = new Dictionary<(string ImdbId, int Season), IReadOnlyList<int>>();
            foreach (var entry in completeSeasons)
            {
                if (!queued.TryGetValue(entry.Key, out var arrived) || arrived.Count == 0)
                {
                    continue;
                }

                var aired = entry.Value;
                var have = arrived.Where(n => aired.Contains(n)).Distinct().OrderBy(n => n).ToList();
                if (aired.Count == 0 || have.Count < aired.Count)
                {
                    LogUtil.PerItem(_config.LogMode,
                        _logger,
                        "[Sub Scribe] queue: {Imdb} S{Season} brought {Have} of {Aired} episode(s) — the queued items are not the whole season, so no pack is built from this run.",
                        entry.Key.Item1, entry.Key.Item2, have.Count, aired.Count);
                    continue;
                }

                // F-M426: only investigated when something is still open — a fully packed season is not.
                if (!HasOpenLanguage(entry.Key, have))
                {
                    LogUtil.PerItem(_config.LogMode,
                        _logger,
                        "[Sub Scribe] queue: {Imdb} S{Season} — every language is already inside a pack, nothing is investigated.",
                        entry.Key.Item1, entry.Key.Item2);
                    continue;
                }

                arrivalSeasons[entry.Key] = have;
                LogUtil.PerItem(_config.LogMode,
                    _logger,
                    "[Sub Scribe] queue: {Imdb} S{Season} brought all {Aired} episode(s) — the pack is built from the queued items of this run only.",
                    entry.Key.Item1, entry.Key.Item2, aired.Count);
            }

            completeSeasons = arrivalSeasons;
            if (completeSeasons.Count == 0)
            {
                LogUtil.Normal(
                    _logger,
                    "[Sub Scribe] no season pack this run — no queue of this run was a whole season (F-M424/F-M425).");
            }

            if (completeSeasons.Count > 0)
            {
                // An EMPTY packable set on purpose: ShouldBuffer answers no for every season, so no
                // candidate is parked in a buffer and every one of them is uploaded single right away.
                // The pack exists only as the closing step, built from the store (F-M420).
                var packable = new HashSet<(string ImdbId, int Season)>();

                packCoordinator = new SeasonPackCoordinator(
                    _api,
                    Registry,
                    _logger,
                    _config,
                    completeSeasons,
                    packable,
                    (label, count) => { summary.Uploaded += count; },
                    count => { summary.Failed += count; },
                    itemId => ItemResult?.Invoke(itemId, ItemOutcome.Done),
                    // F-M389: every member is fetched from the library by the filler, which runs the
                    // single path's gates and demands the content be known — the pack invents nothing.
                    this,
                    // F-M406: the pack loop reports progress so the watchdog does not abort it.
                    watchdog.Heartbeat);

                // F-M611: der Koordinator schreibt seine Urteile mit der Datei-Signatur der Staffel,
                // die die Planung gerade berechnet hat (Luftzahl + Groesse + Speicherdatum je Folge).
                packCoordinator.SeasonSignature =
                    (imdb, season) => _saisonSignatur.TryGetValue((imdb, season), out var sig) ? sig : string.Empty;
            }
        }

        int idx = 0;

        // F-M8: login once per run — token needed for uploads (dry-run skips it)
        if (!_config.DryRun)
        {
            // F-M49 (user decision 08.09.2026 "which messages to react to"): a failed
            // login (wrong credentials NOT_FOUND / 403 / daily limit) must abort the
            // run with a clear message — not crash the task with an unhandled exception.
            try
            {
                await _api.LoginAsync(runCt).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // F-M232 (28.09.2026): a transient login failure (5xx/non-JSON after all
                // retries) is a server overload, not a config problem — F-M67 semantics
                // apply: schedule the overload fire and end the run cleanly, exactly like
                // a mid-run service_busy. Only a real verdict (wrong credentials 404,
                // 403, hourly cap) stops without a fire.
                if (_api.TransientOverload)
                {
                    _logger.LogWarning("[Sub Scribe] {Msg}", ex.Message);
                    ScheduledTasks.ScribeSchedulerCoordinator.Instance?.ScheduleOverloadFire(upload: true, DateTime.UtcNow);
                    return summary;
                }

                _logger.LogError("[Sub Scribe] {Msg} — upload run aborted.", ex.Message);
                return summary;
            }
        }

        // F-M17f (own-account mySubtitles fetch) REMOVED 08.09.2026: the strict F-M17a
        // search (imdb+season+episode+language) queries the SAME SubDL database that
        // mySubtitles reads from — own uploads are included in the search results and
        // a release match skips them. No extra 8000-item fetch needed; also F-M17f's
        // blanket (imdb, episode, lang) skip would block desired second releases.

        // F-M429 (operator order 03.11.2026): "Erst packbildung dann Einzeluploads. Bei packbildung
        // identifizierte einzeluploads für später den einzelupload merken."
        //
        // The pack is built and sent FIRST, out of the queued episodes of this run. Only afterwards do
        // the single uploads run — and they skip every position a pack of this run already carries,
        // because a second copy beside the container is exactly the duplicate F-M391 deletes again.
        // A pack that fails leaves its positions OUT of the set, so those singles go up in the normal
        // single phase: nothing is lost, the plan is remembered instead of assumed.
        if (packCoordinator != null && completeSeasons.Count > 0)
        {
            // F-M599: die Füll-Phase (FillOnly) getrennt vermessen — sie spricht mit dem Netz, die
            // Planung nicht. Nur so ist zu sehen, welcher der beiden Teile die Zeit hält.
            var fuellTakt = System.Diagnostics.Stopwatch.StartNew();
            int fuellVersuche = 0;
            // F-M640 (Operator 19.11.2026, nach „Wurde jetzt ein season pack gebaut?" — nein, und im Log
            // stand fuer die Staffel nur die Kandidaten-Zeile): hier wird je Staffel mitgezaehlt, ob die
            // Planung ueberhaupt ein Paar fuer sie hatte. Staffeln ohne Paar bekommen unten ihre eine Zeile.
            var geplantProStaffel = new Dictionary<(string ImdbId, int Season), int>();
            foreach (var (packImdb, packSeason, packLang, packHi) in PlanPacksFromStore(completeSeasons))
            {
                geplantProStaffel[(packImdb, packSeason)] =
                    (geplantProStaffel.TryGetValue((packImdb, packSeason), out int schonGeplant) ? schonGeplant : 0) + 1;
                fuellVersuche++;
                await packCoordinator.FillOnlyAsync(packImdb, packSeason, packLang, packHi, runCt).ConfigureAwait(false);
                if (Registry.HasPack(packImdb, packSeason, packLang, packHi))
                {
                    _packAbsorbed.Add((packImdb, packSeason, packLang, packHi));
                }
                else if (!string.IsNullOrEmpty(packCoordinator.LastRefusal))
                {
                    // F-M611: der Grund steht schon als Urteil im Store (Refuse im Koordinator). Hier
                    // steht nur noch, dass dieser Lauf nichts mehr dafuer tut.
                    LogUtil.Detail(
                        _logger,
                        "[Sub Scribe] PACK — {Imdb} S{Season} [{Lang}] no pack this run ({Reason}); the verdict with the files' signature is stored (F-M611)",
                        packImdb, packSeason, SeasonPack.LangTag(packLang, packHi), packCoordinator.LastRefusal);
                }
                else
                {
                    LogUtil.PerItem(_config.LogMode,
                        _logger,
                        "[Sub Scribe] pack did not go up for {Imdb} S{Season} [{Lang}] — the singles it was meant to carry go up in the single phase (F-M429).",
                        packImdb, packSeason, packLang);
                }
            }

            // F-M640: jede vollstaendige Staffel bekommt GENAU eine Zeile. Vorher fiel eine Staffel, fuer
            // die die Planung kein offenes Paar hatte, lautlos aus der Phase: der Kandidat stand im Log,
            // danach nie wieder — „schon alles oben", „kein Paar offen" und „uebersprungen" sahen gleich aus.
            foreach (var staffel in completeSeasons.Keys)
            {
                if (geplantProStaffel.ContainsKey(staffel))
                {
                    continue;
                }

                LogUtil.PerItem(
                    _config.LogMode,
                    _logger,
                    "[Sub Scribe] season pack — {Imdb} S{Season}: nothing to pack from this run — the plan found no open pair for this season, so every pair is either already up or was refused earlier (F-M640).",
                    staffel.ImdbId, staffel.Season);
            }

            LogUtil.Detail(
                _logger,
                "[Sub Scribe] PACK — fill phase: {N} attempt(s) in {Ms} ms (F-M599)",
                fuellVersuche, fuellTakt.ElapsedMilliseconds);

            LogUtil.Normal(
                _logger,
                "[Sub Scribe] pack phase done — {Count} language(s) are carried by packs of this run; their singles go up beside the pack (F-M585).",
                _packAbsorbed.Count);
        }

        long packFertigMs = laufTakt.ElapsedMilliseconds;
        LogUtil.Detail(_logger, "[Sub Scribe] LAUF upload — pack phase done after {Ms} ms (F-M598)", packFertigMs);

        bool alleQuellenWeg = false; // F-M590: erst wenn jede Quelle streikt, endet der Lauf

        // F-M596 (Operator-Freigabe 18.11.2026): Index statt foreach und EIN Vorlauf — die Extraktion
        // der naechsten Datei laeuft, waehrend diese sendet. `using` raeumt ihn am Laufende ab, auch
        // auf dem Abbruchpfad.
        using var vorlauf = new ExtractionLookahead();
        var itemTakt = System.Diagnostics.Stopwatch.StartNew();

        for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
        {
            var item = items[itemIndex];

            // F-M598: das vorige Item ist so lange gelaufen — die Zahl macht sichtbar, ob die Zeit in
            // einem einzelnen Item steckt (Extraktion, viele Spuren) oder zwischen den Items.
            // F-M614 (Operator-Frage 18.11.2026: „Wieso schreiben wir den store nicht pro file am
            // ende?"): was der VORIGE Gegenstand geschrieben hat, geht jetzt in die Datei. Bis heute
            // hing alles in der F-M602-Klammer bis zum Laufende — und ein Neustart mitten im Lauf
            // (zweimal passiert am 18.11.) rollte die Urteile zurueck: dieselben acht Titel liefen
            // 482, 483 und 484 erneut durch die Titel-Leiter. FlushPending ist ein No-Op, wenn nichts
            // offen ist, kostet also nur bei Gegenstaenden mit Schreibvorgang einen Commit.
            Registry.Flush();

            LogUtil.Detail(_logger, "[Sub Scribe] LAUF upload — item {Index}/{Count} {Name} — previous item took {Ms} ms (F-M598)",
                itemIndex + 1, items.Count, item.Name, itemTakt.ElapsedMilliseconds);
            itemTakt.Restart();
            if (alleQuellenWeg)
            {
                break;
            }

            // User stop marker — check BEFORE doing any work.
            if (PipelineStopSignal.IsStopped(Plugin.Instance?.DataFolderPath ?? string.Empty, upload: true, runCt))
            {
                LogUtil.Normal(_logger, "[Sub Scribe] upload: user stop requested — finishing current run.");
                summary.StopRequested = true;
                break;
            }

            // F-M111: queue-driven — only queued items are worked (seeder owns admission).
            if (QueueFilter != null && !QueueFilter.Contains(item.Id.ToString()))
            {
                continue;
            }

            runCt.ThrowIfCancellationRequested();
            watchdog.Heartbeat();
            idx++;
            // Fine-grained progress: item start + item done (per-stream uploads report in between)
            progress?.Report((idx - 1 + 0.05) / Math.Max(items.Count, 1) * 100);

            string? mediaPath = item.Path;
            if (string.IsNullOrWhiteSpace(mediaPath))
            {
                // F-M255: a skipped item is named, not silently dropped.
                LogUtil.PerItem(_config.LogMode, _logger, "[Sub Scribe] SKIP {Name} — item has no file path", item.Name);
                continue;
            }

            // F-M88c: FIRST check — if this media file is already upload-complete, skip it
            // entirely without touching the OSHash cache, ffprobe, streams, or directory listings.
            // MediaHashFor is deliberately NOT called here for settled files.
            //
            // F-M499 (gemessen 15.11.2026, 16:17 im scharfen Lauf): "upload-complete" war hier
            // QUELLENBLIND gelesen — die Dateimarke stand von den SubDL-Uploads, tvsubtitles hatte auf
            // denselben Dateien noch keine Spur, und die Pipeline warf die 27 vom Seeder quellenweise
            // eingereihten Folgen alle wieder weg ("[Sub Scribe] SKIP {File} — upload file-complete",
            // Laufende "0 uploaded"). Dieselbe Frage, die F-M499 in der Queue je Quelle stellt, gilt
            // im Kopf des Laufs: uebersprungen wird nur, wenn KEINE Quelle mehr offen ist.
            //
            // Die Quellen sind dieselben, die auch die Kette anlaufen laesst (Schalter UND
            // Konfiguration, im Trockenlauf nur der Schalter — F-M496). Ein Lauf ohne eingeschaltete
            // Quelle ueberspringt nichts: dann gibt es keine Marke, die als "fertig" gelten duerfte.
            // F-M509: die Medienart ist HIER noch nicht aufgeloest (sie kommt weiter unten aus TMDb). Sie
            // wird deshalb nicht geraten: ohne sie bleiben beide Adressen der Familie stehen, und eine
            // Datei gilt erst als fertig, wenn BEIDE sie haben. Das kann nur zu einem Item zu viel im Lauf
            // fuehren, nie zu einem weggelassenen — die Kette selbst fragt danach mit der echten Medienart.
            // F-M544 (16.11.2026): die Medienart steht am Item — sie wird hier gelesen, nicht geraten.
            // F-M509 liess ohne Medienart beide Adressen der Familie offen („lieber eine Zeile zu viel"),
            // gemessen am 16.11. kostete das den ganzen Bestand: 80 der 92 Dateien mit offener Quelle
            // standen als „still open: tvsubtitles, moviesubtitles" im Log — Serien, fuer die
            // moviesubtitles.org nie eine Marke bekommt, weil wir Serien dort nicht hochladen. Diese
            // Dateien wurden damit nie „fertig" und kehrten in jeden Zyklus zurueck. Jetzt entscheidet
            // das Item: Episode ⇒ Serie, Movie ⇒ Film; alles andere bleibt unbekannt und behaelt die
            // vorsichtige Lesart von F-M509.
            bool? medienart = item switch
            {
                Episode => true,
                Movie => false,
                _ => (bool?)null,
            };

            // F-M546: welche Quellen haben DIESES Item eingereiht?
            _queuedSourcesForItem = null;
            if (QueueFilterBySource != null && QueueFilterBySource.Count > 0)
            {
                string itemId = item.Id.ToString();
                var gemeldet = new HashSet<Configuration.SubtitleProvider>();
                foreach (var paar in QueueFilterBySource)
                {
                    if (paar.Value.Contains(itemId))
                    {
                        gemeldet.Add(paar.Key);
                    }
                }

                if (gemeldet.Count > 0)
                {
                    _queuedSourcesForItem = gemeldet;
                }
            }

            string? earlyHash = Registry.GetMediaHash(mediaPath);
            var quellen = UploadSources(medienart);

            // ============================================================
            // F-M576 (Operator-Befund 17.11.2026: „Sicherlich nicht gigabyteweise Extraktionen und
            // hunderte db Zugriffe"): der BILLIGE KOPF-AUSSTIEG kehrt zurueck.
            // 12.1.12.203 sprang hier mit EINEM Blick raus (GetMediaHash + IsSubtitlesUploaded,
            // Zeile 481-492, „without touching the OSHash cache, ffprobe, streams, or directory
            // listings"). Der Port ersetzte das durch GetMediaStreams, die Positions-Abrechnung je
            // Quelle und ein Urteil je Textspur: bei 58 Spuren also 58 Store-Lookups — und zwar fuer
            // JEDE Datei der Bibliothek in JEDEM Zyklus. Gemessen am 17.11.2026 (.449): 1.415
            // Dateien, 16.395 Schreibvorgaenge und 139 ffmpeg-Paesse (94 GB gelesen) fuer 155
            // Uploads.
            //
            // Der Zugeh-Stempel je Quelle steht in der Medien-Zeile (F-M557); EIN Blick darauf
            // beantwortet dieselbe Frage, die der Kopf unten je Position beweist. Die Signatur wird
            // hier NICHT mitgegeben: sie aendert sich nur mit den (Sprache, HI)-Paaren der Datei, und
            // die aendern sich nur mit der Datei — und die hat dann einen neuen Hash. Der Stempel ist
            // also nur so lange gueltig, wie er zur selben Datei gehoert.
            //
            // Der Seitenwagen bleibt geprueft — die Luecke von F-M556 darf nicht zurueckkommen. Die
            // Verzeichnisliste selbst entfaellt, wenn das Verzeichnis aelter ist als der juengste
            // Stempel: dann kann seit dem Abschluss nichts dazugekommen sein.
            // ============================================================
            if (quellen.Count > 0 && !string.IsNullOrEmpty(earlyHash)
                && quellen.All(quelle => Registry.IsCompleteFor(earlyHash, quelle, null)))
            {
                bool seitenwagenMoeglich = true;
                if (Registry.NewestCompletionStamp(earlyHash) is { } stempelZeit)
                {
                    try
                    {
                        var verzeichnis = Path.GetDirectoryName(mediaPath);
                        seitenwagenMoeglich = string.IsNullOrEmpty(verzeichnis)
                            || Directory.GetLastWriteTimeUtc(verzeichnis) > stempelZeit;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Ein Verzeichnis, das sich nicht lesen laesst, ist kein Beweis fuer "nichts
                        // dazugekommen" — dann wird gefragt, nicht angenommen.
                        seitenwagenMoeglich = true;
                    }
                }

                bool seitenwagenOffenFrueh = false;
                if (seitenwagenMoeglich)
                {
                    var loseKandidatenFrueh = FindLooseSrts(mediaPath);
                    seitenwagenOffenFrueh = loseKandidatenFrueh.Count > 0
                        && quellen.Any(quelle => QueuedForSource(quelle)
                            && SidecarsNeedWorkFor(loseKandidatenFrueh, earlyHash, quelle, Registry));
                }

                if (!seitenwagenOffenFrueh)
                {
                    summary.SkippedItems++;
                    summary.FilesSkipped++;
                    summary.SettledItems++;
                    LogUtil.PerItem(
                        _config.LogMode,
                        _logger,
                        "[Sub Scribe] SKIP {File} — stamped complete for every source, no stream read (F-M576)",
                        Path.GetFileName(mediaPath));

                    continue;
                }
            }

            // F-M554: die Spuren, die diese Datei wirklich hat — nicht nur die, die der Store kennt.
            // Dieselbe Zaehlweise wie weiter unten (F-M5): Position innerhalb der Untertitelspuren,
            // und ein forcierter Sidecar ist keine Upload-Arbeit (F-M246/F-M284).
            var kopfAlleSubs = _mediaSourceManager.GetMediaStreams(item.Id)?
                .Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal)
                .ToList() ?? new List<MediaStream>();
            var kopfPositionen = kopfAlleSubs
                .Where(Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsDialogueStream)
                .Select(s => kopfAlleSubs.FindIndex(x => x == s))
                .Where(p => p >= 0)
                .Distinct()
                .ToList();
            // F-M557/F-M559/F-M562 (Operator-Korrektur 17.11.2026: „Ist das nicht generell das ziel das
            // verdict zu schreiben? Jetzt schon wieder ne Lücke?"): die Abrechnung JE QUELLE, hier — vor
            // jeder Arbeit, aus dem Store. Gezaehlt wird JE SPUR, nicht je (Sprache, HI)-Paar: die
            // Urteile im Store stehen je Position, und der Kopf beweist weiter unten an denselben
            // Positionen, dass „jede Textspur abgelehnt" ist (F-M541). Die Paar-Fassung fragte mit einem
            // anderen Schluessel und hielt 22 mst-Dateien offen, obwohl jede Spur ein Urteil hatte.
            // Entschieden ist eine Spur, wenn diese Quelle sie hochgeladen hat, ein Ablehnungs-Urteil
            // steht oder die Herkunft bekannt ist (IsEmbedTerminalFor — EINE Regel, drei Aufrufer).
            var kopfPosen = kopfAlleSubs
                .Where(Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsDialogueStream)
                .Select(s => (
                    Position: kopfAlleSubs.FindIndex(x => x == s),
                    Language: Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical(s.Language) ?? string.Empty,
                    Hi: Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsHearingImpairedStream(s)))
                .Where(p => p.Position >= 0)
                .ToList();
            var kopfSignatur = Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry.PositionSignatureOf(kopfPosen);
            foreach (var quelle in quellen)
            {
                var offen = Registry.OpenPositionsFor(earlyHash, quelle, kopfPosen.Select(p => p.Position));
                if (kopfPosen.Count > 0 && offen.Count == 0)
                {
                    Registry.MarkCompleteFor(earlyHash, quelle, kopfSignatur);
                    LogUtil.PerItem(
                        _config.LogMode,
                        _logger,
                        "[Sub Scribe] {File} — {Tracks} track(s), 0 open — stamp for {Source} (F-M559)",
                        Path.GetFileName(mediaPath),
                        kopfPosen.Count,
                        Api.Endpoints.Provenance.Name(quelle));
                }
                else if (offen.Count > 0)
                {
                    LogUtil.PerItem(
                        _config.LogMode,
                        _logger,
                        "[Sub Scribe] {File} — {Open} of {Tracks} track(s) open for {Source}: {List} (no stamp, F-M559)",
                        Path.GetFileName(mediaPath),
                        offen.Count,
                        kopfPosen.Count,
                        Api.Endpoints.Provenance.Name(quelle),
                        string.Join(", ", offen.Take(8).Select(x => "s" + x.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                }
            }

            var offeneQuellen = quellen
                .Where(quelle => !Registry.IsCompleteFor(earlyHash, quelle, kopfSignatur))
                .ToList();
            if (quellen.Count > 0 && offeneQuellen.Count == 0)
            {
                // F-M556 (Befund 16.11.2026): die eingebetteten Spuren koennen fertig sein, waehrend
                // neben der Datei ein LOSER SEITENWAGEN offen ist. Der Einreiher fragt beides und
                // reiht deswegen ein; dieser Kopf kannte nur die eingebetteten Spuren und warf das
                // Item in JEDEM Zyklus weg (gemessen im 21:28-Zyklus: 292 Wegwuerfe, 0 Uploads, und
                // der Seitenwagen wurde nie gesendet). Jetzt gilt hier dieselbe Regel wie dort —
                // EINE Frage, zwei Aufrufer (SidecarsNeedWorkFor), und nur die Quellen, die dieses
                // Item auch eingereiht haben, halten es offen.
                var loseKandidaten = FindLooseSrts(mediaPath);
                bool seitenwagenOffen = loseKandidaten.Count > 0
                    && quellen.Any(quelle => QueuedForSource(quelle)
                        && SidecarsNeedWorkFor(loseKandidaten, earlyHash, quelle, Registry));

                if (!seitenwagenOffen)
                {
                    summary.SkippedItems++;
                    summary.FilesSkipped++;
                    LogUtil.PerItem(
                        _config.LogMode,
                        _logger,
                        "[Sub Scribe] SKIP {File} — upload file-complete for every upload source ({Sources})",
                        Path.GetFileName(mediaPath),
                        string.Join(", ", quellen.Select(Api.Endpoints.Provenance.Name)));

                    continue;
                }

                LogUtil.PerItem(
                    _config.LogMode,
                    _logger,
                    "[Sub Scribe] keeping {File} — embedded tracks are settled, but {Count} loose sidecar(s) still need a source (F-M556)",
                    Path.GetFileName(mediaPath),
                    loseKandidaten.Count);
            }

            if (offeneQuellen.Count > 0)
            {
                // Der Grund gehoert in den Lauf: eine Datei, die fuer eine Quelle fertig ist, ist es
                // fuer die andere nicht, und genau diese Zeile fehlte, als der Lauf "0 uploaded" sagte.
                LogUtil.Detail(
                    _logger,
                    "[Sub Scribe] {File} — still open: {Sources} (F-M499)",
                    Path.GetFileName(mediaPath),
                    string.Join(", ", offeneQuellen.Select(Api.Endpoints.Provenance.Name)));
            }

            // F-M60 REMOVED (operator order 08.10.2026: "Dann bitte file-retry weg in code und gui.
            // Alleinige Aufgabe database refresh."). The file-missing give-up is no longer the
            // pipeline's job: the SEEDER already refuses an item whose file is gone
            // (ScribeSeeder.Scan: `!File.Exists(mediaPath)` -> continue), so a dead catalog entry
            // never reaches this loop, and what the counter used to clean up — the registry rows and
            // the OSHash entry of a vanished file — is now the DATABASE REFRESH's phase 3b
            // (PruneMissingPathMediaAndSubtitles, behind the same RootsUsable fail-safe as its
            // neighbours). Keeping a second, slower owner here is what the order removes.
            //
            // The ONE check that stays is the bare existence read below: it is not a give-up, it is
            // the loop's precondition — the path is used for hashing and probing, and a file that
            // vanishes BETWEEN seeding and this run must be reported as a failure rather than
            // crashing the run in the middle of it.
            if (!File.Exists(mediaPath))
            {
                summary.Failed++;
                ReportOutcome(item, ItemOutcome.RealFailure);
                LogUtil.PerItem(_config.LogMode, _logger, "[Sub Scribe] FILE MISSING {File} — vanished between seeding and this run", Path.GetFileName(mediaPath));

                continue;
            }

            // F-M61: path-independent dedup key — OSHash of the video content, computed ONCE
            // per item per run (~128 KiB read). Survives restarts, moves and renames, unlike
            // the previous mediaPath key that Load() could not rebuild after a restart.
            // F-M88c/d: served from the central oshash-cache.json while the fingerprint
            // (size+mtime) matches and the OshashRefresh cadence (Never/Always/Weekly/
            // Monthly/Yearly) trusts the entry.
            string? mediaHash = MediaHashFor(mediaPath);

            // F-M22 (defect fixed 02.10.2026): this upsert runs per item, ahead of the upload
            // dry-run exit below — so a dry run wrote rows for items it only described. Held back
            // like its counterpart on the download side; the hash is still computed, because the
            // candidate path needs it either way.
            if (!string.IsNullOrEmpty(mediaHash) && !_config.DryRun)
            {
                Registry.MarkAndFlush(() => Registry.EnsureMedia(mediaHash, item.Id.ToString("D"), mediaPath));
            }

            // Per-item outcome flags for the file-level counters on the status card.
            bool itemUploadedThisRun = false;
            bool itemHadCandidates = false;
            bool itemHadFailures = false; // F-M88c: do not mark file complete if any candidate failed

            // F-M34/M35: skip filters
            string? skip = skipFilter.GetSkipReason(mediaPath);
            if (skip != null)
            {
                summary.SkippedItems++;
                summary.SkippedByFilter++;
                summary.FilesSkipped++; // Fully skipped file for the status card
                LogUtil.PerItem(_config.LogMode, _logger,"[Sub Scribe] SKIP {File} — {Reason}", Path.GetFileName(mediaPath), skip);

                continue;
            }

            // F-M66/F-M151: id resolution in ONE pass. Raw JF ids first, then the
            // optional upload ID quality gate (default ON) validates/corrects them
            // against TMDB. An item that stays unresolvable is skipped for this run and
            // retried by the next one: the give-up budget was REMOVED (operator order
            // 08.10.2026, "Id resolution retrys bitte auch löschen"), so the ladder
            // re-runs instead of retiring the item.
            var (imdbId, tmdbIdRaw, season, episode, isSeries) = ResolveIdsRaw(item);


            // F-M190 (24.09.2026, user decision): the FILE NAME is authoritative
            // for the media type and the season/episode numbers. An episode that
            // lives in a library typed "movies" is imported by Jellyfin as a
            // Movie, so class-based detection searches TMDB for a FILM named
            // after the episode ("Norway No How" instead of series "Best
            // Medicine") and finds nothing. Same rule the movies2 watchdog used.
            var parsedName = MediaNameParser.Parse(mediaPath);
            if (parsedName.IsSeries && !isSeries)
            {
                isSeries = true;
                season = parsedName.Season ?? season;
                episode = parsedName.Episode ?? episode;
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[Sub Scribe] {File}: file name marks a series (S{Season}E{Episode}) while Jellyfin reports no series — resolving as series.",
                    Path.GetFileName(mediaPath), parsedName.Season ?? 0, parsedName.Episode ?? 0);
            }

            // Title for the API search: for a series the SERIES name is required,
            // never the episode title. Jellyfin knows it for a real Episode; for a
            // mis-typed one the file name carries it.
            string searchTitle =
                isSeries
                    ? (item as Episode)?.Series?.Name ?? parsedName.Title ?? item.Name
                    : item.Name;
            int? searchYear =
                (item as Movie)?.ProductionYear is int py ? py
                : (item as Episode)?.Series?.ProductionYear is int sy ? sy
                : parsedName.Year;

            // F-M231 (27.09.2026, user decision "Wir testen jfs id immer gegen tmdb"): the
            // FILE NAME's year wins for the id test. Jellyfin takes its year from the same
            // metadata that may be pinned to the wrong title, so asking with Jellyfin's year
            // can only confirm the mistake (Mayday: nfo year 2003 vs. name year 2026 — the
            // test needs 2026 to see the film at all). A name without a year keeps JF's.
            int? idTestYear = parsedName.Year ?? searchYear;

            // F-M203 (25.09.2026, user decision "TMDb-Key wird verpflichtend, nur Serien
            // verweigern"): without a TMDB key the id resolution that guarantees a SHOW id
            // cannot run, and a series uploaded with Jellyfin's own ids would carry an
            // EPISODE id in the series slot (measured on the test library: every
            // mis-typed episode carries an episode id in BOTH provider fields). So a
            // SERIES is refused outright — fail-closed, reported, requeued. A FILM may
            // still upload: Jellyfin's film ids are the film's own ids, nothing is
            // mis-slotted, and the plugin stays usable for film-only libraries.
            // F-M191 (24.09.2026, user decision): a SERIES item needs SHOW-level ids —
            // TMDB and SubDL expect the tvshow id plus season/episode, never an episode
            // id. Jellyfin's provider ids for a mis-typed episode carry EPISODE ids in
            // both fields (measured: Tmdb 6676484 = S1E1, Imdb tt38949652 = S1E10 of the
            // SAME show), and /tv/{id} answers 404 for them — the old code kept them
            // silently, so the upload metadata carried an episode id where the series id
            // belongs, and the conflict report could never fire. Placed AFTER the name
            // detection (so name-detected series are covered too) and BEFORE the gate,
            // which then validates show-level ids instead of episode ids.
            //
            // F-M202 (25.09.2026): season/episode come from the parsed FILE NAME — they are
            // what lets an episode id be PROVEN against a candidate show.
            if (isSeries)
            {
                var (nlImdb, nlTmdb) = await _tmdb.NormalizeSeriesLevelIdsAsync(
                    imdbId, tmdbIdRaw, searchTitle, searchYear, season, episode, runCt).ConfigureAwait(false);
                imdbId = nlImdb;
                tmdbIdRaw = nlTmdb;
            }

            // F-M231 (27.09.2026, user decision "Wir testen jfs id immer gegen tmdb"):
            // ALWAYS test Jellyfin's ids against TMDb by TITLE and YEAR. The pair check
            // inside ResolveAndValidateIdsAsync is tautological (it asks whether the two
            // ids describe the same entity, which a wrongly pinned pair does), so on its
            // own it confirms an item whose metadata points at the wrong title.
            var verified = await _tmdb.VerifyIdsAgainstTmdbAsync(imdbId, tmdbIdRaw, searchTitle, idTestYear, isSeries, runCt).ConfigureAwait(false);
            if (verified.Corrected)
            {
                imdbId = verified.Imdb;
                tmdbIdRaw = verified.Tmdb;
                isSeries = verified.IsSeries;
            }

            string title = searchTitle;
            int? year = searchYear;
            var (correctedImdb, correctedTmdb) = await _tmdb.ResolveAndValidateIdsAsync(imdbId, tmdbIdRaw, title, year, isSeries, runCt).ConfigureAwait(false);
            if (correctedImdb != null || correctedTmdb != null)
            {
                imdbId = correctedImdb ?? imdbId;
                tmdbIdRaw = correctedTmdb ?? tmdbIdRaw;
            }
            else
            {
                imdbId = null;
                tmdbIdRaw = null;
            }

            if (string.IsNullOrWhiteSpace(imdbId))
            {
                // No 60 s wait; go straight to the TMDB ladder when JF ids are empty.
                if (string.IsNullOrWhiteSpace(imdbId) && !string.IsNullOrWhiteSpace(tmdbIdRaw) && _tmdb.IsConfigured)
                {
                    imdbId = await _tmdb.ResolveImdbAsync(tmdbIdRaw, isSeries, runCt).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(imdbId) && _tmdb.IsConfigured)
                {
                    string ladderTitle = searchTitle;
                    int? ladderYear = searchYear;

                    // F-M190: for a name-detected series the type may still be
                    // wrong (Jellyfin said Movie) — ask TMDB WITHOUT a type
                    // assumption so its answer decides, and take the ids from
                    // that same hit. Falls back to the type-specific title
                    // search when the multi-search finds nothing.
                    var multi = await _tmdb.ResolveByMultiSearchAsync(ladderTitle, ladderYear, isSeries, runCt).ConfigureAwait(false);
                    if (multi.Found)
                    {
                        imdbId = multi.Imdb;
                        tmdbIdRaw = multi.Tmdb;
                        if (multi.IsSeries != isSeries)
                        {
                            isSeries = multi.IsSeries;
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[Sub Scribe] {File}: TMDB resolved this as a {Kind} — type corrected.",
                                Path.GetFileName(mediaPath), multi.IsSeries ? "series" : "movie");
                        }
                    }
                    else
                    {
                        (imdbId, tmdbIdRaw) = await _tmdb.ResolveImdbByTitleAsync(ladderTitle, ladderYear, isSeries, runCt).ConfigureAwait(false);
                    }
                }

                if (string.IsNullOrWhiteSpace(imdbId) && string.IsNullOrWhiteSpace(tmdbIdRaw))
                {
                    summary.SkippedNoImdb++;
                    summary.FilesSkipped++;
                    if (_config.LogMode >= LogLevelMode.Verbose)
                    {
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[Sub Scribe] SKIP {File} — no-imdb (TMDB ladder failed) — requeued, not-found +1",
                            Path.GetFileName(mediaPath));
                    }

                    continue;
                }
            }

            bool idsOk = !string.IsNullOrWhiteSpace(imdbId)
                         && (!isSeries || (season >= 0 && episode >= 0));
            if (!idsOk)
            {
                summary.SkippedNoImdb++;
                summary.FilesSkipped++; // Fully skipped file for the status card
                LogUtil.PerItem(_config.LogMode, _logger,"[Sub Scribe] SKIP {File} — no-imdb/no-season-ep (TMDB ladder)", Path.GetFileName(mediaPath));
                continue;
            }

            // (user decision 12.09.2026): IMDb present, TMDB missing → one
            // /find-by-imdb lookup so the upload carries tmdb_id metadata on SubDL.
            // Never blocking: null result = upload proceeds with imdb alone.
            if (string.IsNullOrWhiteSpace(tmdbIdRaw) && !string.IsNullOrWhiteSpace(imdbId) && _tmdb.IsConfigured)
            {
                string? tmdbBackfilled = await _tmdb.ResolveTmdbByImdbAsync(imdbId, runCt).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(tmdbBackfilled))
                {
                    tmdbIdRaw = tmdbBackfilled;
                }
            }

            // ============================================================
            // F-M69 (user decision 09.09.2026 "extract all languages per file
            // first, ONE comma-separated search, find all doubles from it"):
            // three-phase item processing. Phase 1 screens ALL streams + loose
            // SRTs LOCALLY (extraction, QA, dedup, F-M68 reject store) with ZERO
            // API calls. Phase 2 makes exactly ONE batched search call with the
            // comma-separated language list of the survivors. Phase 3 uploads the
            // streams that are not already on SubDL. Before F-M69 every stream
            // cost its own search request (Fauda S05E02: 30 streams = 30 searches);
            // now a whole item costs ONE search (30x fewer requests per item).
            // ============================================================

            // F-M3: only items with embedded TEXT subtitle streams.
            // IsExternal filters out loose .srt files JF mixes into the stream
            // list — ffmpeg extraction only works on container streams.
            // F-M246/F-M284: a FORCED track is not the film's subtitle — it carries only the lines
            // of foreign-language scenes, so there is nothing worth publishing. The question is
            // asked through the ONE predicate (SidecarNaming.IsDialogueStream), which the seeder's
            // prefilter and the language gate ask as well.
            // NOTE: allSubs stays UNFILTERED for the position lookup below — ffmpeg's 0:s:N
            // counts every subtitle stream including the forced and the bitmap ones, so the
            // index must be computed against the whole list.
            var allSubs = _mediaSourceManager.GetMediaStreams(item.Id)?
                .Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal)
                .ToList() ?? new List<MediaStream>();
            var textStreams = allSubs
                .Where(Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsDialogueStream)
                .ToList();

            // ============================================================
            // F-M5: ONE ffmpeg pass for every text stream of this file, before the
            // per-stream loop. The single pass exists because the cost of the
            // per-stream shape is re-reading the whole container: "to avoid re-reading
            // large CIFS files". The C# port lost it and extracted per stream, so
            // ffmpeg reopened a ~1 GB file once per stream. Measured on prod
            // 28.09.2026 (upload run 15:24, 90 items, median item 11 s): the only
            // four items over 10 min were the stream-rich ones — 29 streams 31 min,
            // 33 streams 26 min, 83 streams 21 min, 33 streams 10 min — because
            // extraction re-read the container for each one. 81 files in the library
            // carry 80+ subtitle streams.
            // ============================================================
            var textPositions = textStreams
                .Select(s => allSubs.FindIndex(x => x == s))
                .Where(p => p >= 0)
                .Distinct()
                .OrderBy(p => p)
                .ToList();

            // ============================================================
            // F-M577 (Operator-Entscheidung 17.11.2026, Variante B: „voher prufen ob Datei
            // ueberhaupt vorhanden ist"): eine Spur, deren Extraktion gescheitert ist, wird als
            // Ablehnung im VORHANDENEN Store vermerkt — Grund `extraction-failed`, kein neues Feld,
            // kein Schema-Schritt. Damit ist die Spur erledigt: die Datei kann ihre Vollstaendigkeits-
            // Marke bekommen, und der billige Kopf-Ausstieg ueberspringt sie. Nach
            // `ExtractRetryAfter` (7 Tage) gilt die Ablehnung als veraltet, und die Spur bekommt
            // ihren naechsten Versuch — auch ohne dass jemand etwas loeschen muss.
            // ============================================================
            var gesperrtePositionen = new List<int>();
            if (textPositions.Count > 0)
            {
                var offenePositionen = new List<int>(textPositions.Count);
                foreach (int position in textPositions)
                {
                    if (Registry.EmbedRejectedReason(mediaHash, position) == RejectReason.ExtractionFailed
                        && !Registry.ExtractRejectIsStale(mediaHash, position, ExtractRetryAfter))
                    {
                        gesperrtePositionen.Add(position);
                        continue;
                    }

                    offenePositionen.Add(position);
                }

                if (gesperrtePositionen.Count > 0)
                {
                    LogUtil.PerItem(
                        _config.LogMode,
                        _logger,
                        "[Sub Scribe] {File}: {Parked} of {All} text stream(s) parked after an extraction failure ({List}, F-M577)",
                        Path.GetFileName(mediaPath),
                        gesperrtePositionen.Count,
                        textPositions.Count,
                        string.Join(", ", gesperrtePositionen.Take(8).Select(x => "s" + x.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                    textPositions = offenePositionen;
                }
            }

            // ============================================================
            // F-M539 (Operator-Anweisung 16.11.2026: „Das kann man doch sofort skippen, ohne weitere
            // Pruefung"): ist der Eintrag erledigt, endet er HIER — vor dem einen ffmpeg-Durchgang.
            // Bis dahin wurde fuer JEDE Datei zuerst ueber alle Textspuren extrahiert (F-M5), und erst
            // danach fiel die Entscheidung je Spur: am 16.11. waren das 633 Selbst-Echo-Treffer und
            // 288 "already-uploaded" — also Arbeit an Dateien, an denen nichts zu tun war.
            // Der Medien-Hash deckt den DATEIINHALT, eine neu gerippte Datei hat einen anderen Hash;
            // der fruehe Ausstieg kann deshalb keine zusaetzliche Spur uebersehen. Und ein
            // Schalterwechsel oeffnet den Eintrag wieder, weil die Paar-Frage die Schalter je Quelle
            // liest (F-M538) und nicht nur "irgendwo hochgeladen" (F-M499).
            // ============================================================
            bool settledPairs = !Registry.RecordedPairsByHash(mediaHash)
                .Any(pair => AnyUploadSourceStillWants(mediaHash, pair.Language, pair.HearingImpaired, isSeries));
            if (settledPairs && Registry.ArePositionsTerminal(mediaHash, textPositions))
            {
                // F-M613 (Operator-Entscheidung 18.11.2026: „Ersteres" — die Pumpe laeuft dauerhaft
                // eine Datei voraus): auch hier wird der Vorlauf angemeldet. Genau diese Zeile fehlte:
                // 541 Ueberspringungen am Lauf, und die Pumpe bekam nie Auftrag, weil nur nach einer
                // echten Extraktion angemeldet wurde. Sie lief leer, und die naechste Datei, die
                // wirklich extrahieren musste, fing kalt an (gemessen: 2 von 34 Extraktionen aus dem
                // Vorlauf).
                MeldeVorlauf(vorlauf, items, itemIndex, runCt);
                summary.SettledItems++;
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[Sub Scribe] SKIP {File} — item settled before extraction (every text stream has its end state, no source still wants a pair, F-M539)",
                    Path.GetFileName(mediaPath));
                continue;
            }

            // ============================================================
            // F-M541 (16.11.2026): sind ALLE Textspuren dieser Datei als verworfen vermerkt, endet der
            // Eintrag hier. Die Quellenliste am Dateikopf fragt nur nach HOCHGELADEN (F-M499) — eine
            // verworfene Spur kann aber niemand mehr hochladen. Folge: eine Datei, deren Spuren alle
            // verworfen waren, blieb fuer immer „still open" fuer alle drei Quellen, wurde in jedem
            // vollen Scan erneut entpackt und dann Spur fuer Spur uebersprungen — im Log sichtbar als
            // „SKIP … uploaded earlier in this run (self-echo lock, F-M142)" mit anschliessendem
            // „no stream survived local screening" und ohne Datei-Marke (gemessen 16.11. an
            // The.Assassin S02E02, Zyklus 14:26). Die Entscheidung je Spur bleibt unveraendert (F-M68):
            // dieser Ausstieg tut nur dasselbe eine Ebene hoeher und spart den ffmpeg-Durchgang.
            // ============================================================
            // F-M584: dieselbe Frage eine Ebene hoeher. Der Ausstieg gilt nur, wenn ausserdem KEIN Paar
            // dieser Datei noch eine Quelle verlangt — sonst faellt die Datei wegen einer Ablehnung weg,
            // die nur eine fremde Quelle betrifft.
            bool nochEinPaarOffen = Registry.RecordedPairsByHash(mediaHash)
                .Any(pair => AnyUploadSourceStillWants(mediaHash, pair.Language, pair.HearingImpaired, isSeries));
            if (textPositions.Count > 0 && !nochEinPaarOffen
                && textPositions.All(p => Registry.EmbedRejectedReason(mediaHash, p) != null))
            {
                summary.SettledItems++;
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[Sub Scribe] SKIP {File} — every text stream was rejected earlier, nothing can be uploaded (F-M541)",
                    Path.GetFileName(mediaPath));
                continue;
            }

            if (textPositions.Count == 0 && gesperrtePositionen.Count > 0)
            {
                summary.SettledItems++;
                LogUtil.PerItem(
                    _config.LogMode,
                    _logger,
                    "[Sub Scribe] SKIP {File} — every open text stream is parked after an extraction failure, no ffmpeg pass (F-M577)",
                    Path.GetFileName(mediaPath));
                continue;
            }

            // F-M596: entweder das vorgezogene Ergebnis dieser Datei oder eine frische Extraktion.
            var (extracted, batchExit, ausVorlauf) = await vorlauf
                .TakeOrExtractAsync(mediaPath, textPositions, _ffmpegPath, _logger, _config, runCt)
                .ConfigureAwait(false);

            // When the one-pass call failed as a whole (non-zero exit, spawn error or
            // exception), a null per stream is a SUSPICION, not a verdict — ffmpeg never
            // got to write those files. Then every stream that came back empty is retried
            // individually, because a partial loss would otherwise be recorded as a real
            // extraction failure and the stream would be reported as failed forever.
            // Exit 0 means ffmpeg read every requested stream and some simply carried no
            // text: those nulls are verdicts and must NOT be retried.
            bool retryPerStream = batchExit != 0;
            if (textPositions.Count > 0)
            {
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[Sub Scribe] extraction: {Count} text stream(s) in ONE ffmpeg pass for {File} (exit {Exit}){Note}",
                    textPositions.Count, Path.GetFileName(mediaPath), batchExit,
                    retryPerStream ? " — per-stream retry for empty streams"
                    : ausVorlauf ? " — from the look-ahead (F-M596)" : string.Empty);
            }

            // F-M596: jetzt die naechsten Schritte vorziehen. Die Extraktion liest nur eine Datei und
            // schreibt Textdateien in ein temporaeres Verzeichnis — alles, was zaehlt, sendet oder in
            // den Store schreibt, bleibt hier an seinem Platz.
            //
            // NACHTRAG 18.11.2026 („Unbedingt", nach der Messung 2 von 26 Treffern): nicht mehr nur die
            // naechste Datei anmelden, sondern bis zu drei. Der Kopf-Ausstieg ueberspringt regelmaessig
            // Dateien, deren Spuren im Store erledigt sind; ein Vorlauf fuer genau so eine Datei war
            // umsonst, und die naechste, die wirklich lief, fing kalt bei ~8 s an. Die Pumpe arbeitet
            // die Anmeldungen nacheinander ab (nie zwei ffmpeg gleichzeitig) und wirft weg, was
            // inzwischen aus dem Plan faellt.
            if (!retryPerStream)
            {
                MeldeVorlauf(vorlauf, items, itemIndex, runCt);
            }

            // Phase 1 candidates: (kind, stream/loose-data, lang, srtContent)
            // F-M71: HearingImpaired rides along — SDH streams upload with hi=true.
            var phase1 = new List<(bool IsLoose, MediaStream? Stream, int SubPos, string LoosePath, string Lang, string Srt, bool HearingImpaired, bool Forced)>();
            int looseSeen = 0, embeddedSeen = 0;

            foreach (var stream in textStreams)
            {
                runCt.ThrowIfCancellationRequested();
                watchdog.Heartbeat();
                // ffmpeg's 0:s:N numbering counts ALL subtitle streams (text + bitmap),
                // so the position must be computed against the unfiltered list
                int subPos = allSubs.FindIndex(s => s == stream);
                if (subPos < 0)
                {
                    // F-M255: the stream could not be located in the unfiltered list — without this
                    // line the position simply vanished from the run.
                    LogUtil.PerItem(_config.LogMode, _logger, "[Sub Scribe] SKIP {File} — subtitle stream not in the unfiltered list", Path.GetFileName(mediaPath));
                    continue;
                }

                // F-M10: language mapping — 'und' (undefined) streams resolve via
                // F-M74 detection (tickbox UploadResolveUnd): detected language replaces
                // the tag; without the tickbox every 'und' stream is
                // removed from the upload set.
                string rawLang = stream.Language ?? string.Empty;
                // A NULL/empty tag means exactly what 'und' means (F-M261)
                // (movies2-sub-uploader.py: tags.get('language', 'und')). Before this fix
                // untagged streams (The.Hawk: 40 per file) fell through MapToSubdl → null
                // and were silently skipped without ever learning a pair → permanent walk.
                bool isUnd = string.IsNullOrWhiteSpace(rawLang)
                    || rawLang.Equals("und", StringComparison.OrdinalIgnoreCase)
                    || rawLang.Equals("undefined", StringComparison.OrdinalIgnoreCase);
                string? lang = null;

                if (isUnd)
                {
                    if (!_config.UploadResolveUnd)
                    {
                        summary.SkippedStreams++;
                        summary.RejectedCandidates++; // F-M286
                        LogUtil.PerItem(_config.LogMode, _logger,"[Sub Scribe] SKIP {File}: untagged stream s{Pos} — resolve-und disabled",
                            Path.GetFileName(mediaPath), subPos);
                        // One row, one verdict: the stream is rejected with und-off. There is no
                        // second "settled" row to keep in sync — terminality is derived from this one.
                        Registry.MarkAndFlush(() => Registry.MarkEmbed(
                            mediaHash, subPos, "UN", Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsHearingImpairedStream(stream),
                            SubtitleStatus.Rejected, reason: RejectReason.UndOff));
                        continue;
                    }

                    // Detection needs the extracted text — defer resolution to phase 1;
                    // mark via the placeholder and resolve after extraction below.
                    lang = "UN"; // sentinel — resolved after ExtractSingleStreamAsync
                }
                else
                {
                    lang = LanguageMapper.MapToSubdl(rawLang);
                }

                if (lang == null)
                {
                    // Unmappable tag — terminal for this position.
                    summary.SkippedStreams++;
                    summary.RejectedCandidates++; // F-M286
                    Registry.MarkAndFlush(() => Registry.MarkEmbed(
                        mediaHash, subPos, rawLang, Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsHearingImpairedStream(stream),
                        SubtitleStatus.Rejected, reason: RejectReason.UnmappedLanguage));
                    // F-M255: the registry row above is not visible in the log — name the tag.
                    LogUtil.PerItem(_config.LogMode, _logger, "[Sub Scribe] SKIP {File} s{Pos} — unmappable language tag {Tag}", Path.GetFileName(mediaPath), subPos, rawLang);
                    continue;
                }

                // F-M4/F-M61: dedup — (media content hash, language) pair already uploaded?
                // F-M71: HI variants have their own key space — a normal upload never blocks an SDH variant.
                bool streamHi = Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsHearingImpairedStream(stream);
                if (!AnyUploadSourceStillWants(mediaHash, lang, streamHi, isSeries))
                {
                    if (IrgendeineQuelleHat(mediaHash, lang, streamHi, isSeries))
                    {
                        // Pair already up. This position is NOT uploaded by itself, so it does not get
                        // an uploaded verdict of its own; it is recorded as rejected-by-self-echo, which
                        // is what it factually is: we did not send this stream because we already had
                        // this language. Marking it "uploaded" would claim an upload that never happened.
                        // F-M286: a Rejected verdict and no counter was a silent discard — the row said
                        // "rejected" while the GUI column never learned about it.
                        summary.RejectedCandidates++; // F-M286
                        Registry.MarkAndFlush(() => Registry.MarkEmbed(
                            mediaHash, subPos, lang, streamHi,
                            SubtitleStatus.Rejected, reason: RejectReason.SelfEcho));
                        // F-M255: one line per discarded stream, whatever the reason.
                        LogUtil.PerItem(_config.LogMode, _logger, "[Sub Scribe] SKIP {File} s{Pos} [{Lang}] — duplicate-self-echo (pair already uploaded, HI={Hi})", Path.GetFileName(mediaPath), subPos, lang, streamHi);
                    }
                    else
                    {
                        // F-M581: KEINE Quelle hat dieses Paar — „schon hochgeladen" waere eine Luege, und
                        // weil „abgelehnt" ein Endzustand ist, haette sie das Paar fuer immer geschlossen.
                        // Also kein Urteil, nur eine Zeile: die Spur bleibt offen und bekommt ihren
                        // Versuch, sobald eine Quelle sie wieder fuehrt.
                        summary.SkippedStreams++;
                        LogUtil.PerItem(_config.LogMode, _logger, "[Sub Scribe] SKIP {File} s{Pos} [{Lang}] — no source asks for this pair right now, no verdict written (F-M581)", Path.GetFileName(mediaPath), subPos, lang);
                    }

                    continue;
                }

                // F-M68: persistent reject store — a stream QA-rejected or dup-skipped
                // in a PREVIOUS run never burns a search request again.
                string? rejectReason = Registry.EmbedRejectedReason(mediaHash, subPos);
                if (rejectReason == RejectReason.ExtractionFailed
                    && Registry.ExtractRejectIsStale(mediaHash, subPos, ExtractRetryAfter))
                {
                    // F-M577: die Sperre ist abgelaufen — die Spur bekommt ihren naechsten Versuch.
                    LogUtil.PerItem(
                        _config.LogMode,
                        _logger,
                        "[Sub Scribe] {File} s{Pos} — extraction failure older than {Days} day(s), trying again (F-M577)",
                        Path.GetFileName(mediaPath),
                        subPos,
                        (int)ExtractRetryAfter.TotalDays);
                    rejectReason = null;
                }

                if (rejectReason != null)
                {
                    // F-M298: `duplicate-content` is only a verdict while the content is NOT already up.
                    // The registry decides that on the CONTENT (see RecordSkippedContent) — replaying a
                    // stale reject over content that is demonstrably uploaded is what kept the position
                    // due and made the file re-extract on every run.
                    //
                    // F-M500 (gemessen 15.11.2026, 16:24): "schon hochgeladen" nimmt die QUELLE mit. Lag
                    // der Inhalt bei SubDL, ist die Spur fuer SubDL erledigt und fuer tvsubtitles offen —
                    // dann faellt der Kandidat hier DURCH statt weggeworfen zu werden, und die Kette
                    // fragt je Quelle (ihr Herkunftstor bleibt davor unangetastet, F-M487).
                    string? rejectedContentHash = Registry.GetEmbed(mediaHash, subPos)?.ContentHash;
                    var (settleVerdict, settleOpen) = SettleDuplicateContent(
                        mediaHash, false, subPos, lang, streamHi, false, rejectedContentHash, rejectReason);

                    if (settleVerdict == ContentSettle.Settled)
                    {
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[Sub Scribe] SETTLED {File} [{Lang}] — identical subtitle already uploaded earlier (F-M298)",
                            Path.GetFileName(mediaPath), lang);

                        continue;
                    }

                    if (settleVerdict == ContentSettle.DueForAnotherSource)
                    {
                        // Kein neues Urteil, kein Reject: die Spur wird neu gelesen und geht an die Kette.
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[Sub Scribe] REPLAY {File} s{Pos} [{Lang}] — rejected as duplicate-content earlier, still open for {Sources} (F-M500)",
                            Path.GetFileName(mediaPath), subPos, lang, settleOpen);
                    }
                    else
                    {
                        // F-M580: der Spruch steht schon im Store — er ist keine NEUE Ablehnung. Ihn hier
                        // erneut zu zaehlen trieb „Uploads: gestoppt vor dem Senden" in jedem Lauf hoch,
                        // ohne dass eine einzige neue Entscheidung gefallen waere.
                        summary.SkippedStreams++;
                        // Known reject for THIS position — carry the original reason forward verbatim
                        // instead of replacing it with a generic note, so the cause stays readable.
                        LogUtil.PerItem(_config.LogMode, _logger,"[Sub Scribe] SKIP {File} [{Lang}] — {Reason}", Path.GetFileName(mediaPath), lang, ExplainReject(rejectReason));

                        continue;
                    }
                }

                // F-M5: take the text from the one-pass extraction. Hit ffmpeg again only
                // when that pass failed as a whole and this stream came back empty — the
                // exit code decides, because an empty stream under a clean pass is a real
                // verdict (the stream carries no text) and must not cost another read.
                string? srt = extracted.TryGetValue(subPos, out var fromPass) ? fromPass : null;
                if (srt == null && retryPerStream)
                {
                    LogUtil.PerItem(_config.LogMode, _logger,
                        "[Sub Scribe] per-stream retry after the failed one-pass call: {File} stream {Pos}",
                        Path.GetFileName(mediaPath), subPos);
                    srt = await ExtractSingleStreamAsync(mediaPath, subPos, runCt).ConfigureAwait(false);
                }

                if (string.IsNullOrEmpty(srt))
                {
                    summary.Failed++;
                    ReportOutcome(item, ItemOutcome.RealFailure);
                    // F-M577 (Variante B): ZUERST die Frage, ob die Datei ueberhaupt da ist. Eine
                    // fehlende Datei ist ein Zugriffsfehler (Share weg, Pfad verschoben), kein Urteil
                    // ueber die Spur — sie bekommt keine Ablehnung und wird im naechsten Lauf erneut
                    // versucht. Erst wenn die Datei da ist und ffmpeg trotzdem nichts liefert, wird
                    // die Spur als gescheitert vermerkt.
                    bool dateiDa = File.Exists(mediaPath);
                    if (dateiDa)
                    {
                        Registry.MarkAndFlush(() => Registry.MarkEmbed(
                            mediaHash, subPos, lang ?? "UN", streamHi, SubtitleStatus.Rejected,
                            reason: RejectReason.ExtractionFailed));
                        LogUtil.PerItem(
                            _config.LogMode,
                            _logger,
                            "[Sub Scribe] EXTRACTION FAILED {File} stream {Pos} — parked as rejected, next attempt in {Days} day(s) (F-M577)",
                            Path.GetFileName(mediaPath),
                            subPos,
                            (int)ExtractRetryAfter.TotalDays);
                    }
                    else
                    {
                        LogUtil.PerItem(
                            _config.LogMode,
                            _logger,
                            "[Sub Scribe] EXTRACTION FAILED {File} stream {Pos} — file is gone, no park, retried next run (F-M577)",
                            Path.GetFileName(mediaPath),
                            subPos);
                    }

                    continue;
                }

                // F-M185: canonicalize BEFORE QA, hashing and upload. ffmpeg writes
                // CRLF for some container streams and LF for others (measured: the
                // same episode yielded 293 CR in one run, 0 in the next), so without
                // this the identical text produced two different hashes AND two
                // different payloads — the duplicate pair in the SubDL inventory.
                srt = ContentHashRegistry.NormalizeSrt(srt);

                // F-M74: resolve the 'und' sentinel with real detection on the extracted
                // text (detection runs after extraction).
                // Size order matters: the 2 KB floor (hard, F-M16) runs BEFORE
                // detection — tiny und-streams skip instead of getting a garbage verdict.
                // Success → the detected language replaces the tag; failure → skip.
                if (lang == "UN")
                {
                    if (srt.Length < 2048)
                    {
                        summary.SkippedStreams++;
                        summary.RejectedCandidates++; // F-M286
                        LogUtil.PerItem(_config.LogMode, _logger,"[Sub Scribe] SKIP {File}: untagged stream s{Pos} — too small to detect a language ({Bytes} bytes)",
                            Path.GetFileName(mediaPath), subPos, srt.Length);
                        // Persist the skip so the file can reach file-complete —
                        // otherwise und-skips never learn a pair and the file is re-walked forever.
                        string undContentHashFm87 = ContentHashRegistry.ComputeHash(srt);
                        Registry.MarkAndFlush(() => Registry.MarkEmbed(
                            mediaHash, subPos, "UN", Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsHearingImpairedStream(stream),
                            SubtitleStatus.Rejected, contentHash: undContentHashFm87, reason: RejectReason.UndTooSmall));
                        continue;
                    }

                    string? detected = Qa.QaGates.DetectLanguage(srt);
                    if (detected == null)
                    {
                        summary.SkippedStreams++;
                        summary.RejectedCandidates++; // F-M286
                        LogUtil.PerItem(_config.LogMode, _logger,"[Sub Scribe] SKIP {File}: untagged stream s{Pos} — language could not be detected ({Bytes} bytes)",
                            Path.GetFileName(mediaPath), subPos, srt.Length);
                        // Same — persist detection failures so files can complete.
                        string undContentHashFm87 = ContentHashRegistry.ComputeHash(srt);
                        Registry.MarkAndFlush(() => Registry.MarkEmbed(
                            mediaHash, subPos, "UN", Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsHearingImpairedStream(stream),
                            SubtitleStatus.Rejected, contentHash: undContentHashFm87, reason: RejectReason.UndDetectionFailed));
                        continue;
                    }

                    lang = detected;
                    LogUtil.PerItem(_config.LogMode, _logger,"[Sub Scribe] {File}: untagged stream s{Pos} — detected language {Lang} ({Bytes} bytes)",
                        Path.GetFileName(mediaPath), subPos, lang, srt.Length);
                }

                embeddedSeen++;
                phase1.Add((false, stream, subPos, string.Empty, lang, srt, streamHi, forced: false));
            }

            // F-M48: loose SRT files — same phase-1 treatment (read + QA local)
            foreach (var (loosePath, looseLang, looseHi, looseForced) in FindLooseSrts(mediaPath))
            {
                runCt.ThrowIfCancellationRequested();
                watchdog.Heartbeat();

                // F-M284: a forced subtitle is OBSERVED, never DELIVERED — SubDL has no forced
                // counterpart (no filter, no field, no candidate names it), so there is nothing to
                // publish and nothing to search for. `IsDeliverable` is the predicate that says so;
                // this is a caller that PLANS WORK, which is exactly what must check it. Returning
                // before the read, the hash and the QA keeps the file out of the run entirely rather
                // than spending a search request to have the server reject it.
                //
                // The embedded side needs no counterpart here: its enumeration already runs through
                // IsDialogueStream, which drops a forced track before it can become a candidate.
                if (looseForced)
                {
                    summary.SkippedStreams++;
                    summary.RejectedCandidates++; // F-M286
                    LogUtil.PerItem(_config.LogMode, _logger,
                        "[Sub Scribe] SKIP {File} — forced subtitle, observed but never uploaded (F-M284)",
                        Path.GetFileName(loosePath));
                    continue;
                }

                // F-M315 note: a kept unsynchronized original needs NO branch of its own here. The
                // download wrote a Rejected row for its content with the reason `original-kept`, and the
                // rejection check below already honours every terminal row by content — the same path
                // that keeps a QA-rejected sidecar out of the queue. Adding a second check for this one
                // reason would read the file twice and make the lock look like a special case.

                if (!AnyUploadSourceStillWants(mediaHash, looseLang, looseHi, isSeries))
                {
                    LogUtil.PerItem(_config.LogMode, _logger,"[Sub Scribe] Loose SRT already uploaded — skipping: {File} ({Lang}{Hi})", Path.GetFileName(loosePath), looseLang, looseHi ? ",HI" : "");
                    continue;
                }

                // F-M68: loose file rejected previously — skip without a search
                // Sidecars are keyed by content: hash what is on disk, then ask about that content.
                string looseHashProbe;
                try
                {
                    looseHashProbe = ContentHashRegistry.ComputeHash(await File.ReadAllTextAsync(loosePath, runCt).ConfigureAwait(false));
                }
                catch
                {
                    summary.Failed++;
                    LogUtil.PerItem(_config.LogMode, _logger, "[Sub Scribe] SKIP {Path} — sidecar unreadable", loosePath);
                    continue;
                }

                string? looseReject = Registry.SidecarRejectedReason(looseHashProbe);
                if (looseReject != null)
                {
                    // F-M298: `duplicate-content` is only a verdict while the content is NOT already up.
                    // The registry decides that on the CONTENT (see RecordSkippedContent). A sidecar is
                    // keyed by its content, so the question is asked about exactly the bytes rejected.
                    //
                    // F-M500: und sie fragt JE QUELLE — liegt der Inhalt bei SubDL, ist die Datei fuer
                    // SubDL erledigt und fuer tvsubtitles offen; dann faellt sie hier durch und die Kette
                    // entscheidet je Quelle (Herkunftstor unveraendert, F-M487).
                    var (looseSettle, looseOpen) = SettleDuplicateContent(
                        mediaHash, true, -1, looseLang, looseHi, looseForced, looseHashProbe, looseReject,
                        fileName: Path.GetFileName(loosePath), path: loosePath);

                    if (looseSettle == ContentSettle.Settled)
                    {
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[Sub Scribe] SETTLED {File} [{Lang}] — identical subtitle already uploaded earlier (F-M298)",
                            Path.GetFileName(loosePath), looseLang);

                        continue;
                    }

                    if (looseSettle == ContentSettle.DueForAnotherSource)
                    {
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[Sub Scribe] REPLAY {File} [{Lang}] — rejected as duplicate-content earlier, still open for {Sources} (F-M500)",
                            Path.GetFileName(loosePath), looseLang, looseOpen);
                    }
                    else
                    {
                        // F-M580: gespeichertes Urteil — keine neue Ablehnung, also kein neuer Zaehler.
                        summary.SkippedStreams++;
                        LogUtil.PerItem(_config.LogMode, _logger,"[Sub Scribe] SKIP {File} [{Lang}] — {Reason}", Path.GetFileName(loosePath), looseLang, ExplainReject(looseReject));

                        continue;
                    }
                }

                try
                {
                    var looseSrt = await File.ReadAllTextAsync(loosePath, runCt).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(looseSrt))
                    {
                        continue;
                    }

                    // F-M185: same canonical form for sidecar .srt files — a loose
                    // file written with CRLF must hash and upload like its LF twin.
                    looseSrt = ContentHashRegistry.NormalizeSrt(looseSrt);

                    // F-M74 for SIDECARS: a name without a language token is the sidecar
                    // counterpart of an 'und' stream — the name says nothing, so the text
                    // has to. Same toggle as the embedded path (UploadResolveUnd), same
                    // order, same outcomes:
                    //   switch off + no token in the name → skipped, logged
                    //   text below the 2 KB floor (F-M16)  → skipped as too little text
                    //   detection finds no language         → skipped
                    //   detection finds one                 → it IS the language
                    // The unlabelled "<base>.srt" used to be read as EN by Jellyfin's
                    // convention, which uploaded German text as English; that guess is gone
                    // (user decision 30.09.2026).
                    string? looseLangResolved = looseLang;
                    if (looseLangResolved == null)
                    {
                        if (!_config.UploadResolveUnd)
                        {
                            summary.SkippedStreams++;
                            summary.RejectedCandidates++; // F-M286
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[Sub Scribe] SKIP {File} — sidecar names no language and resolve-und is disabled",
                                Path.GetFileName(loosePath));
                            string offHash = ContentHashRegistry.ComputeHash(looseSrt);
                            Registry.MarkAndFlush(() => Registry.MarkSidecar(
                                offHash, mediaHash, "UN", looseHi, looseForced,
                                SubtitleStatus.Rejected, reason: RejectReason.UndOff,
                                fileName: Path.GetFileName(loosePath), path: loosePath));
                            continue;
                        }

                        if (looseSrt.Length < 2048)
                        {
                            summary.SkippedStreams++;
                            summary.RejectedCandidates++; // F-M286
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[Sub Scribe] SKIP {File} — sidecar names no language and is too small to detect one ({Bytes} bytes)",
                                Path.GetFileName(loosePath), looseSrt.Length);
                            string smallHash = ContentHashRegistry.ComputeHash(looseSrt);
                            Registry.MarkAndFlush(() => Registry.MarkSidecar(
                                smallHash, mediaHash, "UN", looseHi, looseForced,
                                SubtitleStatus.Rejected, reason: RejectReason.UndTooSmall,
                                fileName: Path.GetFileName(loosePath), path: loosePath));
                            continue;
                        }

                        string? looseDetected = Qa.QaGates.DetectLanguage(looseSrt);
                        if (looseDetected == null)
                        {
                            summary.SkippedStreams++;
                            summary.RejectedCandidates++; // F-M286
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[Sub Scribe] SKIP {File} — sidecar names no language and none could be detected ({Bytes} bytes)",
                                Path.GetFileName(loosePath), looseSrt.Length);
                            string failHash = ContentHashRegistry.ComputeHash(looseSrt);
                            Registry.MarkAndFlush(() => Registry.MarkSidecar(
                                failHash, mediaHash, "UN", looseHi, looseForced,
                                SubtitleStatus.Rejected, reason: RejectReason.UndDetectionFailed,
                                fileName: Path.GetFileName(loosePath), path: loosePath));
                            continue;
                        }

                        looseLangResolved = looseDetected;
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[Sub Scribe] {File}: sidecar names no language — detected {Lang} ({Bytes} bytes)",
                            Path.GetFileName(loosePath), looseDetected, looseSrt.Length);
                    }

                    looseSeen++;
                    phase1.Add((true, null, -1, loosePath, looseLangResolved, looseSrt, looseHi, looseForced));
                }
                catch (Exception ex)
                {
                    summary.Failed++;
                    ReportOutcome(item, ItemOutcome.RealFailure);
                    _logger.LogError("[Sub Scribe] READ FAILED {File}: {Msg}", Path.GetFileName(loosePath), ex.Message);
                }
            }

            if (phase1.Count == 0)
            {
                // nothing survived local screening — no API call at all (F-M69)
                // This IS a "fully skipped" file for the status card — it
                // had streams, none survived QA, and nothing will ever be uploaded
                // from it this run. Previously the early continue bypassed the
                // per-file verdict and the card showed 0 files skipped forever.
                summary.FilesSkipped++;
                LogUtil.PerItem(_config.LogMode, _logger, "[Sub Scribe] SKIP {File} — no stream survived local screening (F-M69)", Path.GetFileName(mediaPath));
                progress?.Report((double)idx / Math.Max(items.Count, 1) * 100);
                continue;
            }

            // This file had at least one candidate stream/loose SRT.
            itemHadCandidates = true;

            // Phase 1 QA: run every candidate through the QA gates + content dedup.
            // QA rejects are persisted to the F-M68 reject store so they never
            // come back. QA needs the runtime for the sync gate.
            long runtimeMs = (item.RunTimeTicks ?? 0) / 10_000;
            var qaSurvivors = new List<(bool IsLoose, MediaStream? Stream, int SubPos, string LoosePath, string Lang, string Srt, bool HearingImpaired, bool Forced)>();
            foreach (var cand in phase1)
            {
                runCt.ThrowIfCancellationRequested();
                watchdog.Heartbeat();
                string? qaReject = ScreenContentQa(cand.Srt, cand.Lang, runtimeMs, out string contentHash, out string? correctedLang, out bool qaFail);
                if (qaReject != null)
                {
                    // F-M298: `duplicate-content` says "this text is already known" — and IsContentKnown
                    // counts a REJECTED row as known just as much as an uploaded one. So this gate fires
                    // for content that is genuinely UP, and recording "rejected" over an uploaded row made
                    // a settled file look unsettled: it was re-extracted and re-rejected on every later
                    // run (prod 06.10.2026: Lanterns S01E08, 39 streams, 0 uploaded / 39 rejected after a
                    // container tag write moved the media hash). The registry settles this on the CONTENT
                    // and reports whether it recorded an upload instead of a reject.
                    //
                    // F-M500 (gemessen 15.11.2026, 16:24): das Urteil nimmt die QUELLE mit. Liegt der
                    // Inhalt bei SubDL und tvsubtitles hat ihn noch nicht, ist der Kandidat NICHT
                    // erledigt — er geht mit seiner Sprache an die Kette, und dort entscheidet das
                    // unveraenderte Herkunftstor (F-M487), ob die offene Quelle ihn nehmen darf.
                    var (qaSettle, qaOpen) = SettleDuplicateContent(
                        mediaHash, cand.IsLoose, cand.SubPos, cand.Lang, cand.HearingImpaired, cand.Forced,
                        contentHash, qaReject,
                        fileName: cand.IsLoose ? Path.GetFileName(cand.LoosePath) : null,
                        path: cand.IsLoose ? cand.LoosePath : null);

                    if (qaSettle == ContentSettle.Settled)
                    {
                        summary.SkippedStreams++;
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[Sub Scribe] SETTLED {File} [{Lang}] — identical subtitle already uploaded earlier (F-M298)",
                            Path.GetFileName(cand.IsLoose ? cand.LoosePath : mediaPath), cand.Lang);

                        continue;
                    }

                    if (qaSettle == ContentSettle.DueForAnotherSource)
                    {
                        // Dieselbe Sprache, eine Quelle ohne den Inhalt: der Kandidat laeuft weiter.
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[Sub Scribe] DUE {File} [{Lang}] — content already up elsewhere, still open for {Sources} (F-M500)",
                            Path.GetFileName(cand.IsLoose ? cand.LoosePath : mediaPath), cand.Lang, qaOpen);

                        qaSurvivors.Add(correctedLang != null
                            ? (cand.IsLoose, cand.Stream, cand.SubPos, cand.LoosePath, correctedLang, cand.Srt, cand.HearingImpaired, cand.Forced)
                            : cand);
                        continue;
                    }

                    // F-M286: count the rejection itself. SkippedStreams also carries
                    // non-QA reasons (duplicates, missing ids), so it cannot serve as the
                    // rejected number either.
                    summary.RejectedCandidates++;
                    // F-M595: in die STATISTIK geht nur das Qualitaets-Urteil.
                    if (qaFail)
                    {
                        summary.RejectedQaFail++;
                    }
                    summary.SkippedStreams++;
                    // The reject row itself was already written by RecordSkippedContent above —
                    // it flushes immediately (F-M68 kill-safety: a hard JF kill between reject and
                    // run-end otherwise lost the entry; live, the 12:03 MS-mismatch vanished on the
                    // 12:05 deploy restart).

                    LogUtil.PerItem(_config.LogMode, _logger,"[Sub Scribe] SKIP {File} [{Lang}] — {Reason}", Path.GetFileName(cand.IsLoose ? cand.LoosePath : mediaPath), cand.Lang, ExplainReject(qaReject));

                    continue;
                }

                if (correctedLang != null)
                {
                    LogUtil.PerItem(_config.LogMode, _logger,
                        "[Sub Scribe] LANGUAGE {File} — tagged {Tag}, detected {Lang}; uploaded under {Lang} (F-M375)",
                        Path.GetFileName(cand.IsLoose ? cand.LoosePath : mediaPath), cand.Lang, correctedLang, correctedLang);
                    qaSurvivors.Add((cand.IsLoose, cand.Stream, cand.SubPos, cand.LoosePath, correctedLang, cand.Srt, cand.HearingImpaired, cand.Forced));
                    continue;
                }

                qaSurvivors.Add(cand);
            }

            if (qaSurvivors.Count == 0)
            {
                    progress?.Report((double)idx / Math.Max(items.Count, 1) * 100);
                continue;
            }

            // ---- Phase 2 (F-M69): ONE batched search for the whole item ----
            // Comma-separated languages of the QA survivors — the same strict
            // server-side filters (imdb+season+episode); candidates carry their
            // Language so doubles can be matched per stream.
            // F-M184 (23.09.2026): the dry run no longer probes SubDL for duplicates —
            // it reports what WOULD be uploaded, without spending a search call.
            // F-M373: the frame rate is what decides whether the episodes share one source, so the
            // plan needs it in the dry run too — looked up once per item, as in phase 3.
            string itemFramerate = GetFramerateCode(item);

            // F-M376: what the file itself says about its resolution and codec — the only release
            // facts a pack has when the file names carry no release at all. One lookup per item.
            (int? itemVideoWidth, int? itemVideoHeight, string? itemVideoCodec) = GetVideoFacts(item);

            // F-M389: keep ONE item per (series, season) of this run. It is the way into the season's
            // episode children when a pack has to be filled from subtitles that are already up —
            // Jellyfin carries the series id on the series, not on the episode, so no query for
            // "all episodes of tt…" exists to ask for here.
            if (isSeries && !string.IsNullOrWhiteSpace(imdbId) && season > 0)
            {
                _seasonAnchors.TryAdd((imdbId!, season), item);
            }

            if (_config.DryRun)
            {
            foreach (var cand in qaSurvivors)
            {
                // F-M372/F-M373: a dry run shows the PACK it would send, not eight singles — the
                // plan is part of what the run decides, so it has to be visible without uploading.
                if (packCoordinator != null && !cand.IsLoose && !cand.Forced
                    && packCoordinator.ShouldBuffer(imdbId, season))
                {
                    await packCoordinator.AddAsync(
                        PackCandidate(item, mediaHash!, imdbId!, tmdbIdRaw, season, episode,
                            cand.Lang, cand.HearingImpaired, cand.Forced, cand.SubPos, cand.Srt,
                            mediaPath, itemFramerate, itemVideoWidth, itemVideoHeight, itemVideoCodec),
                        runCt).ConfigureAwait(false);
                    continue;
                }

                string neutralName = BuildNeutralName(
                    isSeries, imdbId!, season, episode, cand.Lang, ContentHashRegistry.ComputeHash(cand.Srt), cand.HearingImpaired);

                // F-M496 (Operator-Anweisung 15.11.2026): der Trockenlauf spielt die KETTE durch, nicht
                // mehr ein einzelnes Ziel. Hier stand bis jetzt EINE Zeile "[Sub Scribe] DRY-RUN would
                // upload …" — sie verschwieg, dass seit F-M495 zwei Endpunkte hintereinander gefragt
                // werden, und damit auch den zweiten Schalter. Jetzt bekommt jede Quelle ihre eigene
                // Zeile, in derselben Reihenfolge, in der die Kette sie fragen würde, und erst NACHDEM
                // ihr Toggle (und — live — ihre Konfiguration) gefragt wurden. Kein API-Aufruf, kein
                // Login: der Lauf endet hier, bevor irgendein Endpunkt angefasst wird.
                string dryHash = ContentHashRegistry.ComputeHash(cand.Srt);
                var dryHerkunft = Registry.GetOrigin(dryHash).Origin;
                var dryGate = Api.Endpoints.UploadGate.Decide(
                    SourceAllowed(Configuration.SubtitleProvider.Subdl),
                    SourceAllowed(Configuration.SubtitleProvider.TvSubtitles),
                    dryHerkunft,
                    Registry.IsUploadedTo(mediaHash!, cand.Lang, cand.HearingImpaired, Configuration.SubtitleProvider.Subdl),
                    Registry.IsUploadedTo(mediaHash!, cand.Lang, cand.HearingImpaired, Configuration.SubtitleProvider.TvSubtitles),
                    SourceAllowed(Configuration.SubtitleProvider.MovieSubtitles),
                    Registry.IsUploadedTo(mediaHash!, cand.Lang, cand.HearingImpaired, Configuration.SubtitleProvider.MovieSubtitles));
                foreach (var quelle in UploadSourceOrder(isSeries))
                {
                    // F-M496: der Toggle JE QUELLE, vor ihrer Zeile — dieselbe Frage, die die Kette
                    // unmittelbar vor ihrem Start stellt (SourceAllowed). Aus heisst: diese Quelle
                    // taucht im Bericht nicht auf, die andere schon.
                    if (!SourceAllowed(quelle))
                    {
                        // F-M508 (Operator-Auflage: „Bevor wir falsche Sprachen hochladen, laden wir nix
                        // hoch."): dass die Film-Adresse der Familie NICHT sendet, ist im Trockenlauf
                        // eine Auskunft und keine Stille. Sie sagt, warum nichts rausgeht.
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[Sub Scribe] DRY-RUN {Name} [{Lang}] — source {Source} not sent: that source is switched off",
                            neutralName, cand.Lang, Api.Endpoints.Provenance.Name(quelle));
                        continue;
                    }

                    // F-M497 (Operator-Anweisung: „ein Trockenlauf braucht keine Zugangsdaten"): eine
                    // Quelle, die NUR die Konfiguration nicht hat, wird im Trockenlauf trotzdem
                    // gezeigt — er macht keinen Aufruf und verlangt deshalb nichts. Live faellt sie
                    // aus der Kette (F-M497); der Trockenlauf sagt, was passieren WUERDE.
                    if (!Api.Endpoints.UploadGate.May(dryGate, quelle))
                    {
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[Sub Scribe] {Name} [{Lang}] — source {Source} skipped: {Reason}",
                            neutralName, cand.Lang, Api.Endpoints.Provenance.Name(quelle),
                            Api.Endpoints.Provenance.MayUploadTo(dryHerkunft, quelle)
                                ? "already-uploaded:" + Api.Endpoints.Provenance.Name(quelle)
                                : Api.Endpoints.Provenance.OriginBlockedReason(dryHerkunft) ?? "no-upload-source");
                        continue;
                    }

                    // F-M496: je Quelle EINE Zeile, vor ihrem Aufruf — der tvst-Zweig hatte sie nicht,
                    // und der Ausstieg weiter unten (UploadSrtContentAsync) deckte die Kette nur als
                    // Ganzes ab und sagte nichts ueber diese Quelle.
                    LogUtil.PerItem(_config.LogMode, _logger, DryRunLine(quelle), neutralName, cand.Lang);
                }
            }

            progress?.Report((double)idx / Math.Max(items.Count, 1) * 100);
                continue;
            }

            // F-M184 (user decision 23.09.2026): the per-item batched duplicate
            // Pre-check (ONE v1 search per item, F-M69) is REMOVED. The duplicate
            // verdict is delegated to the postprocessing job, which reads mySubtitles,
            // deletes rejected entries and marks them remote-duplicate. The pre-check cost
            // one search call per item against the 400/h bucket and the daily search quota
            // just to prevent an upload that postprocessing removes anyway. Accepted
            // trade-off: a duplicate goes up once, appears as Rejected briefly and is
            // cleaned by the next postprocessing run — 1 upload instead of 1 search/item.
            // Consequence for the 429 path: the daily-limit detection that used to come
            // from the pre-check now happens on the real upload (UploadSubtitleAsync →
            // _api.ServerRateLimited, handled in the phase-3 switch), which is the
            // authoritative signal and needs no extra call.

            // ---- Phase 3: upload the survivors that are NOT already on SubDL ----
            // 13.09.2026 (user decision): run-local content dedup REMOVED — the
            // seeder owns all pre-queue checks (registry + content hash). The
            // pipeline uploads what the queue hands it; QA gates stay as the
            // upload action's own quality step.
            // F-M350: the frame rate is a property of the VIDEO, so it is looked up once per item and
            // handed to every candidate below — not once per uploadable track. (Computed above the
            // dry-run gate since F-M373 — the pack decision needs it there as well.)

            // F-M597 (Nachtrag 18.11.2026: die Messung zeigte, dass die Zeit NICHT in der Spur liegt —
            // `send` 0,7–3,0 s, `qa`/`sources`/`slot` je unter 100 ms, der Abstand zwischen zwei Uploads
            // aber 9,5 s. Also wird jetzt auch die Schleife selbst vermessen: wie lange von der letzten
            // Spur bis zum Aufruf der Kette (Urteil, Store, Buchführung) und wie lange die Kette.
            var abstand = System.Diagnostics.Stopwatch.StartNew();
            foreach (var cand in qaSurvivors)
            {
                runCt.ThrowIfCancellationRequested();
                watchdog.Heartbeat();

                // F-M589 (Operator-Regel 17.11.2026, verschaerft): die Transferpause haengt an einer
                // ECHTEN Uebertragung DIESER Spur. Vorher stand dort das Datei-Flag
                // `itemUploadedThisRun`, das beim ersten Upload gesetzt und fuer die Datei nie
                // zurueckgesetzt wurde — jede weitere Spur zahlte 9 s, auch wenn sie nur uebersprungen
                // wurde (gemessen 17.11.: 41 solcher Faelle in 1.114 Dateien, rund 6 Minuten am Tag).
                bool dieseSpurHochgeladen = false;

                // Check stop marker also between streams of the same item.
                if (PipelineStopSignal.IsStopped(Plugin.Instance?.DataFolderPath ?? string.Empty, upload: true, runCt))
                {
                    LogUtil.Normal(_logger, "[Sub Scribe] upload: user stop requested mid-item — finishing current run.");
                    summary.StopRequested = true;
                    break;
                }

                string fileName = cand.IsLoose ? cand.LoosePath : mediaPath;
                string contentHash = ContentHashRegistry.ComputeHash(cand.Srt);

                // Rev.2 (user decision 16.09.2026): persistent self-echo
                // guard instead of the run-local HashSet — after the first
                // upload of a (media, lang, hi) pair the registry KNOWS it
                // (MarkUploaded runs right after the API call), so any further
                // candidate for the same pair — later in this run OR in any
                // future run — skips without a SubDL pre-check (index lag makes
                // the remote check blind to our own fresh uploads). The
                // collector already consults the same registry entry (lines
                // 576/659); this closes the in-run gap between collect and mark.
                // F-M489: nicht mehr "irgendwo hochgeladen", sondern je Quelle gefragt. Sonst blockierte
                // ein tvst-Upload den SubDL-Upload und umgekehrt; und ein Reup auf dieselbe Quelle muss
                // genauso ausbleiben wie ein Quellenwechsel.
                var gateFrueh = Api.Endpoints.UploadGate.Decide(
                    SourceAllowed(Configuration.SubtitleProvider.Subdl),
                    SourceAllowed(Configuration.SubtitleProvider.TvSubtitles),
                    Registry.GetOrigin(contentHash).Origin,
                    Registry.IsUploadedTo(mediaHash, cand.Lang, cand.HearingImpaired, Configuration.SubtitleProvider.Subdl),
                    Registry.IsUploadedTo(mediaHash, cand.Lang, cand.HearingImpaired, Configuration.SubtitleProvider.TvSubtitles));
                if (gateFrueh.SkipReason is not null)
                {
                    // F-M581: das Tor kann aus drei Gruenden schweigen — Schalter aus, Herkunft bekannt,
                    // oder eine Quelle hat das Paar schon. Nur der letzte Fall ist ein Urteil ueber den
                    // INHALT und darf als `duplicate-self-echo` im Store stehen; die anderen beiden sind
                    // Umstaende des Laufs und bekommen keine Zeile, sonst waere das Paar fuer immer zu.
                    if (IrgendeineQuelleHat(mediaHash, cand.Lang, cand.HearingImpaired, isSeries))
                    {
                        summary.RejectedCandidates++; // F-M286
                        if (cand.IsLoose)
                        {
                            Registry.MarkAndFlush(() => Registry.MarkSidecar(
                                contentHash, mediaHash, cand.Lang, cand.HearingImpaired, cand.Forced,
                                SubtitleStatus.Rejected, reason: RejectReason.SelfEcho,
                                fileName: Path.GetFileName(cand.LoosePath), path: cand.LoosePath));
                        }
                        else
                        {
                            Registry.MarkAndFlush(() => Registry.MarkEmbed(
                                mediaHash, cand.SubPos, cand.Lang, cand.HearingImpaired,
                                SubtitleStatus.Rejected, contentHash: contentHash, reason: RejectReason.SelfEcho));
                        }
                    }
                    else
                    {
                        summary.SkippedStreams++;
                    }

                    LogUtil.PerItem(_config.LogMode, _logger,"[Sub Scribe] SKIP {File} [{Lang}] — {Reason} (registry gate)", gateFrueh.SkipReason, Path.GetFileName(fileName), cand.Lang);

                    continue;
                }

                // F-M184: the release-match duplicate check that lived here used the
                // phase-2 batched search result (existingAll) — both are removed; the
                // duplicate verdict now comes from postprocessing.
                // F-M20 (03.10.2026, user decision): the local call budget is removed — the plugin
                // counts no calls of its own and cannot refuse one. SubDL alone decides (daily
                // counters, answered below by the 429/QuotaStopDecision path).
                // The upload→upload transfer pause (F-M26) sits at the end of this loop, unchanged.

                // F-M372: a candidate of a complete season waits for its siblings instead of going
                // up alone. Loose sidecars and forced tracks never join a pack — the first has no
                // place in the season's container, the second is not the season's subtitle.
                if (packCoordinator != null && !cand.IsLoose && !cand.Forced
                    && packCoordinator.ShouldBuffer(imdbId, season))
                {
                    await packCoordinator.AddAsync(
                        PackCandidate(item, mediaHash!, imdbId!, tmdbIdRaw, season, episode,
                            cand.Lang, cand.HearingImpaired, cand.Forced, cand.SubPos, cand.Srt,
                            mediaPath, itemFramerate, itemVideoWidth, itemVideoHeight, itemVideoCodec),
                        runCt).ConfigureAwait(false);
                    continue;
                }

                long bisKette = abstand.ElapsedMilliseconds;
                var ketteTakt = System.Diagnostics.Stopwatch.StartNew();
                var result = await UploadSrtContentAsync(
                    cand.IsLoose ? cand.LoosePath : null,
                    mediaPath,
                    cand.Lang,
                    imdbId!,
                    season,
                    episode,
                    isSeries,
                    mediaHash,
                    runCt,
                    cand.Srt,
                    runtimeMs,
                    skipPrecheck: true,
                    tmdbId: tmdbIdRaw,
                    hearingImpaired: cand.HearingImpaired,
                    subPos: cand.SubPos,
                    forced: cand.Forced,
                    framerate: itemFramerate).ConfigureAwait(false);

                // F-M597: die Schleife selbst. `Abstand` = Ende der letzten Spur bis zum Aufruf dieser
                // Kette (Urteil, Store-Zeilen, Vorbereitung), `Kette` = die vermessene Spur selbst.
                LogUtil.Detail(
                    _logger,
                    "[Sub Scribe] TAKT-SPUR {Lang} — gap since the last track {Abstand} ms, of which the chain {Kette} ms (F-M597)",
                    cand.Lang, bisKette, ketteTakt.ElapsedMilliseconds);
                abstand.Restart();

                    // F-M590: drei Spuren hintereinander ohne Antwort irgendeiner Quelle — dann sind die
                    // Seiten weg und der Lauf hat keinen Sinn mehr. Eine einzelne tote Seite ist KEIN
                    // Abbruch: sie steht als Fehlschlag dieser Quelle im Log, die andere hat geliefert.
                    if (_quellenAusfaelleInFolge >= 3)
                    {
                    alleQuellenWeg = true;
                    _logger.LogWarning(
                        "[Sub Scribe] no upload source answered for {N} track(s) in a row — stopping the run early. Nothing was uploaded to any source; next attempt at the next fire.",
                        _quellenAusfaelleInFolge);
                    break;
                    }

                    switch (result)
                {
                    case { Ok: true }:
                        summary.Uploaded++;
                        ReportOutcome(item, ItemOutcome.Done);
                        itemUploadedThisRun = true;
                        dieseSpurHochgeladen = true; // F-M589: nur DIESE Spur hat uebertragen
                        // A completed upload is the verdict itself — one row, no companion marker.
                        if (cand.IsLoose)
                        {
                            Registry.MarkAndFlush(() => Registry.MarkSidecar(
                                ContentHashRegistry.ComputeHash(cand.Srt), mediaHash, cand.Lang, cand.HearingImpaired, cand.Forced,
                                SubtitleStatus.Uploaded,
                                fileName: Path.GetFileName(cand.LoosePath), path: cand.LoosePath));
                        }
                        else
                        {
                            Registry.MarkAndFlush(() => Registry.MarkEmbed(
                                mediaHash, cand.SubPos, cand.Lang, cand.HearingImpaired,
                                SubtitleStatus.Uploaded, contentHash: ContentHashRegistry.ComputeHash(cand.Srt)));
                        }

                        // F-M550 (Operator-Frage 16.11.2026: „Wieso steht im log dass nach subdl
                        // hochgeladen wurde, lädt er nicht gerade zu tvst?"): die Zeile nannte die
                        // Quelle nicht — nur das alte Präfix [SubDL] stand davor, und das liest sich wie
                        // „nach SubDL geladen". Jetzt steht die Quelle in der Zeile.
                        LogUtil.PerItem(_config.LogMode, _logger, "[Sub Scribe] UPLOADED {File} [{Lang}] → {Source}", Path.GetFileName(fileName), cand.Lang, QuellName(result));

                        break;
                    case { SkippedItem: true }:
                        // F-M298: a duplicate-content skip means "SubDL already holds this text" — which
                        // is what an earlier UPLOAD of the same bytes produces just as much as a remote
                        // duplicate does. The registry settles this on the CONTENT: writing "rejected"
                        // over content that is demonstrably up left the position due forever, and the next
                        // run re-extracted the file to reject it again (prod 06.10.2026: Lanterns S01E08,
                        // 39 streams rejected while the same text sat uploaded under the pre-rewrite
                        // identity).
                        string skippedContentHash = ContentHashRegistry.ComputeHash(cand.Srt);
                        string skipReason = RejectReason.DuplicateRemote == result.Reason
                            ? RejectReason.DuplicateRemote
                            : result.Reason ?? RejectReason.CandidateRejected;
// F-M603 (Operator-Entscheidung 18.11.2026, Weg „A"): ein ANGENOMMENER Versuch ohne
                        // Kennung bekommt seine Marke, BEVOR das Urteil faellt. Sonst rechnet
                        // SettleDuplicateContent die Quelle wieder als offen (ihr fehlt der Beleg), die Spur
                        // bleibt „still open", und der naechste Lauf entpackt dieselbe Datei erneut —
                        // gemessen an „The End Of Oak Street" (18.11.2026): 209 offen-Zeilen, 176 SKIPs,
                        // 0 Uploads, ~20 s je Lauf, seit dem fruehen Morgen.
                        if (result.SourceAccepted && result.Source is { } angenommeneQuelle)
                        {
                            DateTime angenommenAm = DateTime.UtcNow;
                            string angenommeneId = Api.Endpoints.Provenance.OwnUploadId(
                                angenommeneQuelle, skippedContentHash, skipReason, angenommenAm);
                            MarkAcceptedSource(
                                cand.IsLoose, cand.LoosePath, mediaHash, cand.SubPos, cand.Lang,
                                cand.HearingImpaired, cand.Forced, skippedContentHash, angenommeneQuelle,
                                angenommeneId, angenommenAm);
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[Sub Scribe] ACCEPTED {File} [{Lang}] — {Source} took the attempt without naming an id, the pair is settled (F-M603)",
                                Path.GetFileName(fileName), cand.Lang, Api.Endpoints.Provenance.Name(angenommeneQuelle));
                        }

                        // F-M500: auch hier fragt "schon hochgeladen" je Quelle. Die Kette hat alle Quellen
                        // gefragt; hat eine den Inhalt, bekommt SIE ihre Marke, und die andere bleibt offen
                        // statt quellenneutral abgelehnt zu werden.
                        var (skipSettle, skipOpen) = SettleDuplicateContent(
                            mediaHash, cand.IsLoose, cand.SubPos, cand.Lang, cand.HearingImpaired, cand.Forced,
                            skippedContentHash, skipReason,
                            fileName: cand.IsLoose ? Path.GetFileName(cand.LoosePath) : null,
                            path: cand.IsLoose ? cand.LoosePath : null,
                            settleOnAnySkipReason: true);

                        if (skipSettle == ContentSettle.Settled)
                        {
                            summary.SkippedStreams++;
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[Sub Scribe] SETTLED {File} [{Lang}] — identical subtitle already uploaded earlier (F-M298)",
                                Path.GetFileName(fileName), cand.Lang);

                            break;
                        }

                        if (skipSettle == ContentSettle.DueForAnotherSource)
                        {
                            // Keine Ablehnung: der Inhalt liegt bei einer Quelle, die andere hatte in DIESEM
                            // Lauf keinen Versuch (Herkunft, Schalter oder Ausfall). Die Spur bleibt fuer sie
                            // offen, statt quellenneutral verworfen zu werden (F-M500).
                            summary.SkippedStreams++;
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[Sub Scribe] DUE {File} [{Lang}] — content already up elsewhere, still open for {Sources} (F-M500)",
                                Path.GetFileName(fileName), cand.Lang, skipOpen);

                            break;
                        }

                        summary.SkippedStreams++;
                        summary.RejectedCandidates++; // F-M286
                        // A definitive skip verdict settles the position — but
                        // transient reasons (rate caps inside UploadSrtContentAsync return
                        // as failures, not skips) keep the stream due.
                        // The reject row itself was already written by RecordSkippedContent above.

                        LogUtil.PerItem(_config.LogMode, _logger, "[Sub Scribe] SKIP {File} [{Lang}] → {Source} — {Reason}", Path.GetFileName(fileName), cand.Lang, QuellName(result), ExplainReject(result.Reason));

                        break;
                    default:
                        // F-M609: „keine Antwort" ist keine Ablehnung. Zwei Zeilen, zwei Zaehler —
                        // vorher hiess beides „UPLOAD FAILED", und ein Ausfall sah aus wie ein Nein.
                        itemHadFailures = true; // F-M88c: failure blocks file-complete marker
                        if (result.Verdict == Api.UploadVerdict.NoVerdict)
                        {
                            summary.NoVerdicts++;
                            _logger.LogWarning(
                                "[Sub Scribe] NO ANSWER {File} [{Lang}] → {Source}: {Reason} — no verdict, the pair stays open (F-M609)",
                                Path.GetFileName(fileName), cand.Lang, QuellName(result), result.Reason);
                        }
                        else
                        {
                            summary.Refused++;
                            _logger.LogError(
                                "[Sub Scribe] REFUSED {File} [{Lang}] → {Source}: {Reason} (F-M609)",
                                Path.GetFileName(fileName), cand.Lang, QuellName(result), result.Reason);
                        }

                        break;
                }

                if (_api.AuthBroken)
                {
                    _logger.LogError("[Sub Scribe] API key rejected (403) — upload stopped, check credentials.");
                    return summary;
                }

                // F-M72: upload hourly cap (500/h) — HTTP 200 + status:false, NOT a 429.
                // The candidate stays due (skip, not fail); the run stops cleanly and
                // the overload-recovery fire retries within 10–30 min when the next
                // hourly budget is available.
                if (_api.HourlyUploadCapHit)
                {
                    _logger.LogWarning("[Sub Scribe] Upload hourly cap (500/h) — run stopped, next hourly budget.");
                    _api.ResetHourlyCapFlag();
                    ScheduledTasks.ScribeSchedulerCoordinator.Instance?.ScheduleOverloadFire(upload: true, DateTime.UtcNow);
                    return summary;
                }

                if (_api.ServerRateLimited)
                {
                    // Arrival runs never wait out the daily limit
                    if (IsArrivalRun)
                    {
                        _logger.LogWarning("[Sub Scribe] Daily limit (429), arrival run — pass stopped, next scheduled run.");
                        return summary;
                    }

                    if (await WaitOutDailyLimitAsync(watchdog, runCt).ConfigureAwait(false))
                    {
                        _api.ResetRateLimitFlag();
                        watchdog.Heartbeat();
                    }
                    else
                    {
                        var fireInfoUp = ScheduledTasks.ScribeSchedulerCoordinator.Instance?.GetPendingFireUtc(upload: true);
                        _logger.LogWarning("[Sub Scribe] SubDL account daily limit (HTTP 429) — stopping upload run early. Restart: {FireLocal} local time.{Pending}",
                            fireInfoUp?.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "see scheduler log", fireInfoUp.HasValue ? string.Empty : " (no pending fire — will re-fire at next scheduled run)");
                        summary.QuotaStopped++;
                        return summary;
                    }
                }

                // F-M594 (Operator-Anweisung 18.11.2026): Die Pause steht nicht mehr HIER, sondern
                // als Slot VOR dem Versand der naechsten Spur (siehe `WaitForTransferSlotAsync`).
                // Der Grund ist gemessen: blind nach dem Transfer gewartet ergab bei 600/h 8,2 s je
                // Spur statt 6 s, weil Extraktion und Vorbereitung obendrauf kamen. `dieseSpurHochgeladen`
                // bleibt als Zaehler fuer die Zusammenfassung stehen — die Pause haengt nicht mehr daran.
            }

            // F-M88c: when every candidate of this media file has been settled and none
            // failed, mark the whole file complete so future runs skip it entirely.
            if (itemHadCandidates && !itemHadFailures && mediaHash != null)
            {
                Registry.MarkAndFlush(() => Registry.MarkSubtitlesUploaded(mediaHash));
                LogUtil.PerItem(_config.LogMode, _logger, "[Sub Scribe] FILE COMPLETE {File} — all streams settled", Path.GetFileName(mediaPath));
            }

            // Per-file verdict for the status card ("X subtitles in Y files").
            // Counted once per item at the single exit point of the item body.
            if (itemUploadedThisRun)
            {
                summary.FilesUploaded++;
            }
            else if (itemHadCandidates)
            {
                summary.FilesSkipped++;
            }

            // item done — fine-grained progress
            progress?.Report((double)idx / Math.Max(items.Count, 1) * 100);
        }

        _api.DebugTrace -= OnApiTrace;
        _api.Log -= OnApiLog; // F-M232: detach the client's LogInfo channel per run
        _tmdb.Trace -= OnTmdbTrace;
        _tmdb.IdMismatch -= OnIdMismatch; // F-M190: detach per-run handler
        Registry.Flush();
        OshashCache.Flush(); // F-M88c: kill-safe flush of the central OSHash cache
        // (user decision 14.09.2026, REVISED 02.10.2026 — F-M286): the rule used to be "no stats
        // line, no skip aggregates" for the upload, with per-item UPLOADED lines as the only run
        // output. That decision was made when the upload's reject number was a single gate; with the
        // counter now covering every reject path it is the number that explains a run which discarded
        // its whole inventory, and a run that rejected 40 of 40 candidates looked exactly like a run
        // that found nothing (the same blindness F-M183 fixed for the download).
        // What stays: this is ONE line with the two numbers, not a per-reason aggregate — the
        // per-item SKIP lines remain the Verbose detail.
        // F-M247: a dry run uploaded nothing, so its number is labelled hypothetical —
        // "N uploaded" would be a claim the run cannot back.
        // F-M372: every buffer that never filled goes up the single way before the run is closed —
        // an incomplete season must not lose the episodes it did have.
        if (packCoordinator != null)
        {
            // F-M429 (operator order 03.11.2026: "Erst packbildung dann Einzeluploads"): the pack phase
            // now runs BEFORE the item loop — see the block above the loop. What stays here is the
            // flush: nothing is buffered any more (the packable set is empty), so it is the safety net
            // that makes sure no held-back candidate is lost when a run ends.
            await packCoordinator.FlushAsync(runCt).ConfigureAwait(false);
        }

        // F-M588: die SubDL-Ids der in diesem Lauf hochgeladenen Einzel EINMAL nachziehen. Steht
        // bewusst hier und nicht im Postprocessing: dieser Lauf kennt noch jede Spur, ihre Sprache,
        // ihr HI-Kennzeichen und ihre Reihenfolge — die Zuordnung braucht keinen zweiten Verbraucher,
        // der mit dem Lauf um dieselbe Liste rennt.
        await ResolveDeferredUploadIdsAsync(runCt).ConfigureAwait(false);

        if (summary.IsDryRun)
        {
            LogUtil.Detail(_logger, "[Sub Scribe] LAUF upload — done: item loop {Loop} ms, run total {Total} ms, {Items} item(s) (F-M598)",
                laufTakt.ElapsedMilliseconds - packFertigMs, laufTakt.ElapsedMilliseconds, items.Count);

            LogUtil.Normal(_logger, "[Sub Scribe] upload: finished — lock released. DRY-RUN would have uploaded {N} | {Rejected} rejected, {Refused} refused, {NoVerdict} without answer, {Skipped} stream(s) skipped (F-M609)",
                summary.Uploaded, summary.RejectedCandidates, summary.Refused, summary.NoVerdicts, summary.SkippedStreams);
        }
        else
        {
            LogUtil.Normal(_logger, "[Sub Scribe] upload: finished — lock released. {N} uploaded | {Rejected} rejected, {Refused} refused, {NoVerdict} without answer, {Skipped} stream(s) skipped (F-M609)",
                summary.Uploaded, summary.RejectedCandidates, summary.Refused, summary.NoVerdicts, summary.SkippedStreams);
        }

        return summary;
        }
        catch (OperationCanceledException)
        {
            // F-M23: a run cancelled from outside (user stop marker, Jellyfin restart, task
            // cancel) used to propagate out of RunAsync, so the caller never saw a summary
            // and every subtitle already uploaded by this run stayed invisible in the
            // statistics. Return the partial summary instead — the uploads DID reach SubDL.
            // The cancellation is not swallowed: the caller still learns about it through
            // StopRequested/the stop marker and aborts the cycle.
            LogUtil.Normal(_logger, "[Sub Scribe] upload: cancelled — keeping the partial result. {N} uploaded", summary.Uploaded);
            summary.StopRequested = true;
            return summary;
        }
        finally
        {
            Registry.EndBatch(); // F-M602: schreibt, was die Klammer gesammelt hat

            // F-M441: the run's working files are its own business — the run ends, they go.
            int workFiles = _workFiles.Purge();
            if (workFiles > 0)
            {
                _logger.LogDebug("[Sub Scribe] upload: {Count} working file(s) of this run removed.", workFiles);
            }

            // F-M445 (operator decision 02.11.2026): the end of every upload run re-arms the upload
            // postprocessing, and the last order wins — a run that ends later cancels the earlier
            // order. This is what makes the handle remap (upload id -> entry id) land on the same
            // day the pack was built instead of the next morning.
            //
            // A USER STOP cancels the order instead (operator order 02.11.2026: "Nutzerstopp terminiert
            // auch den terminierten run vom Postprocessing") — stop means stop, the scheduled run goes
            // with the run it belonged to. The next regular upload run arms it again.
            if (summary.StopRequested)
            {
                ScheduledTasks.ScribeSchedulerCoordinator.Instance?.CancelUploadPostprocessing(
                    $"upload run stopped by the user ({summary.Uploaded} uploaded)");
            }
            else if (summary.Uploaded > 0)
            {
                ScheduledTasks.ScribeSchedulerCoordinator.Instance?.ArmUploadPostprocessing(
                    $"upload run ended ({summary.Uploaded} uploaded)");
            }
            else
            {
                // F-M465 (operator decision 11.11.2026, "nur bei > 0 uploaded anmahnen"): a run that
                // uploaded nothing has nothing for the postprocessing to remap, so it arms no order.
                // An order someone else placed stays untouched — this run does not own it.
                LogUtil.Normal(
                    _logger,
                    "[Sub Scribe] upload: nothing uploaded — no postprocessing order armed (F-M465).");
            }

            PipelineRunLock.Release(_logger, "upload");
        }
    }

    private async Task<UploadResult> ProcessAndUploadAsync(
        string mediaPath,
        string? mediaHash,
        MediaStream stream,
        int subPos,
        string lang,
        string imdbId,
        int season,
        int episode,
        bool isSeries,
        long runtimeMs,
        CancellationToken ct)
    {
        // NOTE (28.09.2026): this method is UNREFERENCED — dead code kept for reference.
        // It used to carry an "F-M5/F-M6" marker, which was wrong twice over: F-M5 asks for
        // ONE ffmpeg call per FILE, and this path calls the single-stream extractor. A
        // requirement number on the code that violates it is worse than no marker — it makes
        // the requirement look satisfied in every code review. F-M5 is now implemented in the
        // item loop (see ExtractAllStreamsAsync); this is the per-stream path only.
        // F-M6 still applies here: text streams only (bitmap subs are filtered upstream).
        // (subPos = position among ALL subtitle streams — matches ffmpeg's 0:s:N numbering)
        string? srt = await ExtractSingleStreamAsync(mediaPath, subPos, ct).ConfigureAwait(false);
        if (srt == null)
        {
            return UploadResult.Failed("extraction failed");
        }

        // F-M71: forward the SDH detection (embedded stream title) — dead-code path kept consistent.
        return await UploadSrtContentAsync(null, mediaPath, lang, imdbId, season, episode, isSeries, mediaHash, ct, srt, runtimeMs, hearingImpaired: Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsHearingImpairedStream(stream), subPos: subPos).ConfigureAwait(false);
    }

    /// <summary>
    /// F-M69: local QA screening WITHOUT any API call — the gates F-M13–M16,
    /// the size floor and the content-hash dedup. Returns the reject reason
    /// (or null when the content passes) plus the computed content hash.
    /// </summary>

    /// <summary>
    /// F-M596: die Untertitel-Positionen einer Datei fuer den Vorlauf — dieselbe Auswahl wie im Lauf
    /// (<see cref="SidecarNaming.IsDialogueStream"/> gegen die ungefilterte Untertitel-Liste, weil
    /// ffmpeg mit <c>0:s:N</c> ALLE Untertitelspuren zaehlt). Nur lesend, keine Store-Frage: der
    /// Vorlauf soll genau das extrahieren, was der Lauf gleich braucht, aber nichts beurteilen.
    /// </summary>
    /// <param name="itemId">Die Datei, die als naechste dran waere.</param>
    /// <returns>Die Positionen, aufsteigend.</returns>
    private List<int> TextSubtitlePositions(Guid itemId)
    {
        var alle = _mediaSourceManager.GetMediaStreams(itemId)?
            .Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal)
            .ToList() ?? new List<MediaStream>();

        return alle
            .Select((stream, position) => (stream, position))
            .Where(x => Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsDialogueStream(x.stream))
            .Select(x => x.position)
            .ToList();
    }

    private string? ScreenContentQa(string srtContent, string lang, long runtimeMs, out string contentHash, out string? correctedLang, out bool qaFail)
    {
        correctedLang = null;
        contentHash = ContentHashRegistry.ComputeHash(srtContent);
        qaFail = false;

        string? gate = GateReject(srtContent, lang, runtimeMs, out correctedLang);
        if (gate != null)
        {
            // F-M595: ein Urteil der QUALITAETS-Gates — das ist der einzige Fall, den die Statistik zaehlt.
            qaFail = true;
            return gate;
        }

        // F-M17x: server-side duplicate verdict learned from previous runs
        if (Registry.IsRemoteDuplicate(contentHash))
        {
            return "duplicate-remote";
        }

        // F-M17c: content-hash dedup
        if (Registry.IsContentKnown(contentHash))
        {
            return "duplicate-content";
        }

        return null;
    }

    /// <summary>
    /// F-M389: the gates of 3.1 alone — size floor, cue count, SRT structure, sync — without the
    /// duplicate verdicts that <see cref="ScreenContentQa"/> adds on top.
    /// <para>
    /// The pack filler needs exactly this half. It asks the registry itself, and there "known" is the
    /// ANSWER the pack depends on ("this text was uploaded before"), not a refusal — a filler that
    /// went through <see cref="ScreenContentQa"/> would condemn every candidate it is supposed to
    /// find. The gates are shared rather than copied so a filler can never carry a track into a pack
    /// that the single path would have thrown away.
    /// </para>
    /// </summary>
    /// <param name="srtContent">Canonical subtitle text.</param>
    /// <param name="lang">Language the text is offered as.</param>
    /// <param name="runtimeMs">Item runtime in milliseconds (0 when unknown).</param>
    /// <param name="correctedLang">Language the content actually speaks, when a confident verdict differs from the tag.</param>
    /// <returns>The reject reason, or null when the text passes.</returns>
    private string? GateReject(string srtContent, string lang, long runtimeMs, out string? correctedLang)
    {
        correctedLang = null;

        // B1 simple size gate (full QA is B2/F-M13–M16)
        if (srtContent.Length < 2048)
        {
            return "too-small";
        }

        // ---- B2 QA gates (F-M13–M16), each opt-in via config checkbox ----
        var stats = Qa.QaGates.ParseSrt(srtContent);

        // F-M15 + F-M375 (operator order 01.11.2026): language verification — detected content
        // language vs. stream tag. A track whose tag does not match its text is not thrown away any more: the
        // detected language wins and the subtitle goes up under it, which is what the tag
        // should have said in the first place. Only the checkbox decides whether this runs.
        if (_config.QaVerifyLanguage)
        {
            correctedLang = Qa.QaGates.CorrectedLanguage(lang, srtContent);

            // null → tag holds, or no confident verdict → nothing to correct (fail-open)
        }

        // F-M16: minimum cue count (≥30) — beyond the hard 2 KB size floor
        if (_config.QaMinCues && (stats == null || stats.CueCount < 30))
        {
            return $"qa-too-few-cues ({stats?.CueCount ?? 0})";
        }

        // F-M13: SRT parser validation — timings parseable, monotonic starts, plausible durations
        if (_config.QaValidateSrt)
        {
            if (stats == null)
            {
                return "qa-invalid-srt (no cue timings)";
            }

            if (!stats.Monotonic)
            {
                return "qa-invalid-srt (non-monotonic timestamps)";
            }

            if (stats.MinCueMs <= 100 || stats.MaxCueMs >= 600_000)
            {
                return $"qa-invalid-srt (cue duration {stats.MinCueMs}–{stats.MaxCueMs} ms)";
            }
        }

        // F-M14: sync plausibility — cue span vs. item runtime, concentration check
        if (_config.QaCheckSync && stats != null && runtimeMs > 0)
        {
            if (stats.LastEndMs > runtimeMs + 300_000)
            {
                return $"qa-sync (span {stats.LastEndMs / 60000} min > runtime + 5 min)";
            }

            if ((stats.LastEndMs - stats.FirstStartMs) < runtimeMs / 10)
            {
                return "qa-sync (cues concentrated in <10% of runtime)";
            }
        }

        return null;
    }

    /// <summary>
    /// F-M48: finds loose SRT files next to the media file. Returns (path, language, HI, forced) tuples.
    /// <para>
    /// The name is read by <see cref="Registry.SidecarNaming"/>, the one parser — this method used to
    /// carry its own copy and the copies drifted.
    /// </para>
    /// </summary>
    /// <param name="mediaPath">Media file path.</param>
    /// <returns>One entry per loose sidecar.</returns>
    /// <remarks>
    /// Operator order 17.11.2026 ("5xer sidecars bitte generell nicht hochladen"): the reserved
    /// original block (slot 50–99) is filtered out here, at the one place upload candidates enter the
    /// run. Those files are the unsynchronized copies a correction keeps beside its result — publishing
    /// them would put the raw take up next to the corrected one under the same episode.
    /// </remarks>
    /// <summary>
    /// F-M556 (Befund 16.11.2026: „Warum wird nichts gesendet?"): braucht diese Quelle fuer einen der
    /// losen Seitenwagen neben der Datei noch Arbeit?
    /// <para>
    /// DIE REGEL STAND BISHER NUR IM EINREIHER: er reihte ein Item ein, wenn eingebettet alles erledigt
    /// war, aber ein Seitenwagen offen blieb (<c>SidecarsNeedWork</c>). Der Kopf des Laufs kannte nur die
    /// eingebetteten Spuren und warf solche Items darum in JEDEM Zyklus weg — im 21:28-Zyklus **292**
    /// Wegwuerfe bei **0** Uploads, und der Seitenwagen wurde nie gesendet. Jetzt gibt es die Frage
    /// EINMAL, und beide Stellen rufen sie auf.
    /// </para>
    /// <para>
    /// Je Quelle, nicht „irgendwo": ein tvst-Upload haelt das Paar nicht aus dem SubDL-Lauf. Eine
    /// ABLEHNUNG bleibt quellenneutral (Urteil ueber den Untertitel), ein bekannter Fund ohne
    /// Quellenbeleg ebenso — das Herkunftstor sperrt ihn ohnehin. Ein nicht lesbarer Seitenwagen
    /// antwortet „ja" (arbeiten statt ueberspringen).
    /// </para>
    /// </summary>
    /// <param name="loose">Die losen Seitenwagen der Datei.</param>
    /// <param name="mediaHash">Media hash of the file they belong to.</param>
    /// <param name="quelle">The source in question.</param>
    /// <param name="registry">The registry to ask.</param>
    /// <returns>True when at least one sidecar still needs this source.</returns>
    public static bool SidecarsNeedWorkFor(
        List<(string Path, string Lang, bool HearingImpaired, bool Forced)> loose,
        string? mediaHash,
        Configuration.SubtitleProvider quelle,
        Registry.ContentHashRegistry registry)
    {
        if (loose == null || loose.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrEmpty(mediaHash))
        {
            return true; // ohne Medienhash laesst sich keine Marke lesen — arbeiten statt ueberspringen
        }

        foreach (var (loosePath, looseLang, looseHi, _) in loose)
        {
            if (registry.IsUploadedTo(mediaHash, looseLang, looseHi, quelle))
            {
                continue;
            }

            string looseContent;
            try
            {
                looseContent = File.ReadAllText(loosePath);
            }
            catch
            {
                return true; // unreadable sidecar → let the pipeline decide
            }

            string looseHash = Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry.ComputeHash(looseContent);
            if (registry.IsSidecarUploadedTo(looseHash, quelle) || registry.SidecarRejectedReason(looseHash) != null)
            {
                continue;
            }

            if (registry.GetOrigin(looseHash).Origin is not null)
            {
                continue;
            }

            string contentHashProbe = Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry.ComputeHash(looseContent);
            if (registry.IsContentKnown(contentHashProbe)
                && !registry.IsContentUploadedForAnotherSource(contentHashProbe, quelle))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    public static List<(string Path, string Lang, bool HearingImpaired, bool Forced)> FindLooseSrts(string mediaPath)
        => Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.List(mediaPath)
            .Where(x => !Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsOriginalSidecar(x.Path, mediaPath))
            .ToList();

    /// <summary>
    /// F-M71: resolves a loose-file language token (2-letter direct, 3-letter ISO 639-2 via mapper).
    /// </summary>
    /// <summary>
    /// Translates the persisted reject codes into a human-readable log phrase.
    /// Only used for log output — the registry keeps storing the original codes so
    /// lookups stay stable across plugin versions.
    /// </summary>
    private static string ExplainReject(string? reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return "rejected earlier";
        }

        if (reason.StartsWith("qa-lang-mismatch", StringComparison.Ordinal))
        {
            // "qa-lang-mismatch (tag SK, content IT)" → readable form
            // F-M375: no reject of this kind is written any more; rows from older runs stay readable.
            return string.Concat("quality gate: subtitle language did not match its tag (", reason.AsSpan("qa-lang-mismatch ".Length), ")");
        }

        return reason switch
        {
            "too-small" => "quality gate: subtitle too small",
            "duplicate-remote" => "SubDL already has this exact subtitle content (different release name)",
            "duplicate-content" => "identical subtitle already known in this file",
            "duplicate-self-echo" => "uploaded earlier in this run (self-echo lock, F-M142)",
            "duplicate-release" => "SubDL already has this release",
            "und-off (F-M74)" => "untagged stream skipped (resolve-und disabled)",
            "und-too-small (2 KB floor, F-M74)" => "untagged stream: too little text to detect a language",
            "und-detection-failed (F-M74)" => "untagged stream: language could not be detected",
            "no-imdb" => "item has no IMDb id — cannot upload",
            "no-season-ep" => "item has no season/episode numbers — cannot upload",
            "second-source-no-tmdb-id" => "no TMDb id, so there is no leading title for tvsubtitles.net (F-M501)",
            "second-source-no-series-record" => "TMDb answered no series record, so name and year are unknown (F-M501)",
            "second-source-no-language" => "the site does not carry this language, so it can never take it (F-M538)",
            _ => reason
        };
    }

    private static string? ResolveLooseLangToken(string token)
    {
        if (token.Length == 2)
        {
            return token.ToUpperInvariant();
        }

        if (token.Length == 3)
        {
            return LanguageMapper.MapToSubdl(token);
        }

        return null;
    }

    /// <summary>
    /// F-M30 (order 17.10.2026, "die volle md5 macht sinn"): the upload filename carries the content
    /// hash, so two uploads of the same item and language are told apart by what is INSIDE them
    /// rather than by who sent them.
    /// </summary>
    /// <remarks>
    /// Shape: imdb id, season/episode, hash, language, extension — the hash sits directly in front of
    /// the language, so the last two parts before the extension are always "what is inside" and "which
    /// language it is" (operator choice 18.10.2026).
    /// <para>
    /// The hash is the MD5 over the normalized bytes SubDL receives
    /// (<see cref="ContentHashRegistry.ComputeHash"/>), which makes it recomputable from the delivered
    /// file — a check, not a claim. It is deliberately the FULL 32 hex characters: the name is where
    /// a wrong pairing would be read by people, so uniqueness beats brevity.
    /// <para>
    /// It is not an installation, run or user identifier. F-M29 still forbids anything that links
    /// uploads to a user, a library, a path or a server; a content hash links nothing but files that
    /// are byte-identical after normalization, which is a property of the subtitle, not of the sender.
    /// </para>
    /// </remarks>
    /// <summary>
    /// F-M350: the frame rate code this item's video reports. Hands BOTH rates to
    /// <see cref="ScribeFramerate.CodeFor(double?, double?)"/>, which takes the average first and falls
    /// back to the real rate when the average is not a rate at all (measured on the operator's library:
    /// 27 files report 1000 in the average field with a correct real rate). Wrapped because
    /// GetMediaStreams can throw for an item that is being re-scanned, and a missing rate must cost a
    /// metadata field, never the upload.
    /// <para>
    /// Called ONCE per item, before the candidate loop: the answer is a property of the video, not of
    /// the subtitle track, so an item with four uploadable tracks must not look it up four times.
    /// </para>
    /// </summary>
    private string GetFramerateCode(BaseItem item)
    {
        try
        {
            var video = _mediaSourceManager.GetMediaStreams(item.Id)?
                .FirstOrDefault(s => s.Type == MediaStreamType.Video);
            if (video == null)
            {
                return ScribeFramerate.Unknown;
            }

            return ScribeFramerate.CodeFor(video.AverageFrameRate, video.RealFrameRate);
        }
        catch
        {
            return ScribeFramerate.Unknown;
        }
    }

    /// <summary>
    /// F-M376: resolution and codec of the item's video, read from the stream. A missing or throwing
    /// stream costs the release name a token, never the upload.
    /// </summary>
    /// <param name="item">Media item.</param>
    /// <returns>Height in pixels and codec name, both nullable.</returns>
    private (int? Width, int? Height, string? Codec) GetVideoFacts(BaseItem item)
    {
        try
        {
            var video = _mediaSourceManager.GetMediaStreams(item.Id)?
                .FirstOrDefault(s => s.Type == MediaStreamType.Video);
            if (video == null)
            {
                return (null, null, null);
            }

            // F-M380: the width decides the resolution token; the height is cropped on many encodes.
            return (video.Width, video.Height, video.Codec);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static string BuildNeutralName(
        bool isSeries, string imdbId, int season, int episode, string lang, string contentHash, bool hi = false)
        => isSeries
            ? $"{imdbId}.S{season:00}E{episode:00}.{contentHash}.{lang}{SeasonPack.HiMarker(hi)}.srt"
            : $"{imdbId}.{contentHash}.{lang}{SeasonPack.HiMarker(hi)}.srt";

    /// <summary>
    /// F-M71:
    /// detects a hearing-impaired (SDH) embedded subtitle stream. Sources, in order:
    /// the JF stream Title ("SDH"/"Hearing Impaired" — set by MediaElch/ TinyMediaManager
    /// or JF metadata) and the stream's own flag as a fallback.
    /// </summary>
    /// <summary>
    /// F-M88c/d (user decision 10.09.2026): OSHash with central cache — oshash-cache.json
    /// in the plugin data dir, never a sidecar next to the media. Fingerprint (size+mtime)
    /// mismatches always recompute (file replaced/remuxed/repaired detected immediately);
    /// the OshashRefresh cadence bounds trust time. Always = plain recompute every run.
    /// </summary>
    private string? MediaHashFor(string mediaPath)
    {
        // F-M119 (user decision 14.09.2026): the uploader is LAZY-ONLY — it trusts
        // the cache until a size+mtime fingerprint mismatch (the OshashRefresh
        // cadence is enforced by the scheduler-driven ScribeOshashRefreshTask on
        // its own diced WEEKLY anchor, NOT here). Cache miss (file never seen) →
        // compute self-healing so brand-new uploads keep working.
        long size = new System.IO.FileInfo(mediaPath).Length;
        long mtimeUnix = (long)(System.IO.File.GetLastWriteTimeUtc(mediaPath) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

        string? cached = OshashCache.Lookup(mediaPath, size, mtimeUnix, TimeSpan.Zero, out _);
        if (cached != null)
        {
            return cached;
        }

        string? computed = Registry.GetMediaHash(mediaPath);
        if (computed != null)
        {
            OshashCache.Store(mediaPath, computed, size, mtimeUnix);
            // F-M88c: stores are rare (only on fingerprint mismatch or refresh expiry) —
            // flush immediately so a cancelled/killed run keeps every hash it computed.
            OshashCache.Flush();
        }

        return computed;
    }


    /// <summary>
    /// Shared upload path for embedded (extracted) and loose (file) SRT content (F-M48).
    /// srtContent: the subtitle text; loosePath: originating loose file or null for embedded.
    /// </summary>
    /// <summary>
    /// F-M590: schreibt die Marke EINER Quelle sofort nach ihrer Antwort. Nur die Felder dieser Quelle
    /// werden gesetzt — <c>MarkEmbed</c>/<c>MarkSidecar</c> schreiben bekannte Kennzeichen einzeln und
    /// lassen die der anderen Quelle stehen (F-M488), eine zweite Meldung derselben Spur überschreibt
    /// also nichts. Nennt eine Seite keine Id, wird wie bisher eine eigene Marke vergeben (F-M488).
    /// Der Aufruf kommt aus den parallel laufenden Quellen, deshalb ein Schloss.
    /// </summary>
    /// <param name="quelle">Which source answered.</param>
    /// <param name="id">The id it reported, if any.</param>
    /// <param name="mediaHash">Media hash of the row.</param>
    /// <param name="subPos">Stream position (embedded).</param>
    /// <param name="lang">Language code.</param>
    /// <param name="hearingImpaired">HI flag.</param>
    /// <param name="forced">Forced flag.</param>
    /// <param name="loosePath">Sidecar path, when the upload came from a loose file.</param>
    /// <param name="contentHash">Content hash of the text.</param>
    private void MarkSourceUploadNow(
        Configuration.SubtitleProvider quelle, string? id, string? mediaHash, int subPos, string lang,
        bool hearingImpaired, bool forced, string? loosePath, string contentHash)
    {
        string marke = string.IsNullOrWhiteSpace(id) ? "own:" + Guid.NewGuid().ToString("N") : id;
        var jetzt = DateTime.UtcNow;
        string? subdlId = quelle == Configuration.SubtitleProvider.Subdl ? marke : null;
        string? tvstId = quelle == Configuration.SubtitleProvider.TvSubtitles ? marke : null;
        string? msubId = quelle == Configuration.SubtitleProvider.MovieSubtitles ? marke : null;

        lock (_sofortMarkeLock)
        {
            if (loosePath != null)
            {
                Registry.MarkAndFlush(() => Registry.MarkSidecar(
                    contentHash, mediaHash ?? contentHash, lang, hearingImpaired, forced,
                    SubtitleStatus.Uploaded,
                    fileName: Path.GetFileName(loosePath), path: loosePath,
                    subdlUploadId: subdlId,
                    tvSubtitlesUploadId: tvstId,
                    moviesubtitlesUploadId: msubId,
                    subdlUploadedAt: subdlId != null ? jetzt : null,
                    tvSubtitlesUploadedAt: tvstId != null ? jetzt : null,
                    moviesubtitlesUploadedAt: msubId != null ? jetzt : null));
            }
            else
            {
                Registry.MarkAndFlush(() => Registry.MarkEmbed(
                    mediaHash, subPos, lang, hearingImpaired,
                    SubtitleStatus.Uploaded, contentHash: contentHash, forced: forced,
                    subdlUploadId: subdlId,
                    tvSubtitlesUploadId: tvstId,
                    moviesubtitlesUploadId: msubId,
                    subdlUploadedAt: subdlId != null ? jetzt : null,
                    tvSubtitlesUploadedAt: tvstId != null ? jetzt : null,
                    moviesubtitlesUploadedAt: msubId != null ? jetzt : null));
            }
        }

        LogUtil.PerItem(_config.LogMode, _logger,
            "[Sub Scribe] {Source} recorded right away for {File} [{Lang}] — the other source cannot take the record with it any more (F-M590).",
            Api.Endpoints.Provenance.Name(quelle), Path.GetFileName(loosePath ?? "?"), lang);
    }

    private async Task<UploadResult> UploadSrtContentAsync(
        string? loosePath,
        string mediaPath,
        string lang,
        string imdbId,
        int season,
        int episode,
        bool isSeries,
        string? mediaHash,
        CancellationToken ct,
        string? srtContent = null,
        long runtimeMs = 0,
        bool skipPrecheck = false,
        string? tmdbId = null,
        bool hearingImpaired = false,
        int subPos = -1,
        bool forced = false,
        string framerate = ScribeFramerate.Unknown)
    {
        // F-M597 (Operator-Freigabe 18.11.2026: „Ja", nach der Messung „8,4 s ohne eine einzige
        // Logzeile"): die Spur wird vermessen. Gemessen am 18.11.2026 lag der Abstand zwischen zwei
        // Uploads bei 9,6 s (Median, Rate 500/h) — davon 2,0 s Netz und der Rest in einem Block, der
        // nichts protokollierte. Diese Zeile macht ihn sichtbar: QA, Quellen-Tore, Slot, Versand.
        // Sie steht auf `Detail` und ist damit nur im Detail-LogMode zu sehen.
        var takt = System.Diagnostics.Stopwatch.StartNew();
        long qaMs = 0, quellenMs = 0, slotMs = 0, versandMs = 0;

        // F-M429 (operator order 03.11.2026): a position a pack of this run already carries is not
        // uploaded single. The pack is the only carrier — a second copy beside it would be the very
        // duplicate the pack deletes again (F-M391). Positions of a pack that failed are not in the
        // set and reach this line as usual.
        // F-M585 (Operator-Anweisung 17.11.2026: „Subdl erlaubt Einzeluploads die gleichzeitig
        // bestandteil eines staffelpacks sind"): hier stand bis heute der Ausstieg F-M429 — „was der
        // Pack dieses Laufs traegt, geht nicht zusaetzlich als Einzel hoch", als Ablehnung
        // `pack-carries-it` in den Store geschrieben (127 Zeilen). SubDL erlaubt beides, also geht die
        // Position jetzt als Einzel hoch UND steht im Pack. Der Vermerk bleibt als Zeile im Log.
        if (isSeries && season > 0 && _packAbsorbed.Contains((imdbId, season, lang.ToUpperInvariant(), hearingImpaired)))
        {
            LogUtil.PerItem(
                _config.LogMode,
                _logger,
                "[Sub Scribe] {File} [{Lang}] — carried by the pack built in this run and sent as a single beside it (F-M585, the pack row lists this episode: {Covered}).",
                Path.GetFileName(mediaPath), lang,
                CoveredByPack(imdbId, season, lang, hearingImpaired, episode) ? "yes" : "no");
        }

        // F-M48: loose file — read from disk
        if (srtContent == null && loosePath != null)
        {
            try
            {
                srtContent = await File.ReadAllTextAsync(loosePath, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return UploadResult.Failed("read failed: " + ex.Message);
            }
        }

        if (string.IsNullOrEmpty(srtContent))
        {
            return UploadResult.Failed("empty content");
        }

        // F-M185 safety net: the canonical form is established in phase 1, but this
        // method is also reachable directly (retry path, loose-path fallback above).
        // Normalizing again is idempotent and guarantees the bytes handed to SubDL
        // are exactly the bytes ComputeHash saw — the invariant the duplicate
        // detection on both sides depends on.
        srtContent = ContentHashRegistry.NormalizeSrt(srtContent);

        // F-M69: QA + content dedup run in phase 1 (ScreenContentQa) BEFORE the
        // batched search — this path is only reached with skipPrecheck=true from
        // the phase-3 loop. A direct call without the flag (retry path) keeps the
        // full inline QA as a safety net.
        string contentHashEarly = ContentHashRegistry.ComputeHash(srtContent);
        if (!skipPrecheck)
        {
            // F-M218: deliberately NOT counted here. The QA statistics counter is
            // incremented in the phase-1 precheck (the normal path, where the run
            // summary is in scope); counting this safety net as well would report
            // one rejection twice. This branch only runs for a direct/retry call.
            string? qaReject = ScreenContentQa(srtContent, lang, runtimeMs, out _, out string? correctedLang, out _);
            if (qaReject != null)
            {
                return UploadResult.Skipped(qaReject);
            }

            // F-M375: the retry path corrects the same way the phase-1 precheck does.
            lang = correctedLang ?? lang;
        }

        qaMs = takt.ElapsedMilliseconds; // F-M597


        // F-M29/F-M30: neutral filename, no user-identifying data; the content hash keeps two uploads
        // of the same item and language apart without identifying anybody (full MD5, order 17.10.2026)
        string neutralName = BuildNeutralName(isSeries, imdbId, season, episode, lang, contentHashEarly, hearingImpaired);

        string releaseName = Path.GetFileNameWithoutExtension(mediaPath);

        // F-M344: the source quality this upload reports. The subtitle's OWN name decides — a loose
        // sidecar states where that subtitle came from — and the container's name is read only when
        // the own name says nothing. Unrecognized stays "web", which is what shipped before.
        string quality = SourceQuality.Detect(loosePath, mediaPath);

        // F-M350 (operator order 18.10.2026): the frame rate code this upload reports, computed once
        // per item by the caller; anything outside SubDL's table is "0" (default/unknown), which is
        // what shipped before, so an unusual rate cannot become a wrong claim.

        // F-M22: dry run — no API calls. F-M496: the CHAIN's own dry-run line is per source and sits
        // in the batch block above (before this method is even reached); this guard stays as the last
        // door, so a caller that arrives here in dry mode still sends nothing.
        if (_config.DryRun)
        {
            LogUtil.PerItem(_config.LogMode, _logger,"[Sub Scribe] DRY-RUN would upload {Name} [{Lang}]", neutralName, lang);

            return UploadResult.Skipped("dry-run");
        }

        // F-M184 (23.09.2026): the legacy inline duplicate pre-check was removed with
        // the phase-2 batched search. Duplicate handling is delegated to postprocessing.
        // NOTE: skipPrecheck stays — it still guards the QA safety net above.
        // F-M71 (user decision 09.09.2026 "liegt beides vor, beides dem Uploader mitgeben"):
        // tmdb_id is the SubDL-recommended field; imdb_id rides along too. The API-side
        // precedence rule (tmdb empty → imdb only; both → both) lives in the client —
        // here we simply forward what JF metadata provided.
        // F-M485 (operator order 15.11.2026: "beide upload und download bitte individuell schaltbar
        // über die toggles"): the UPLOAD follows its switches. SubDL is called exactly as before when
        // its switch is on — including the response body the caller reads below, which is why this is
        // not folded into a generic call. The second source joins only when its switch is on, and only
        // when SubDL is off or did not take the subtitle.
        // F-M487 (operator order 15.11.2026): no cross-contamination. The subtitle's provenance is read
        // from the store by its content hash; a known provenance may only go back to its OWN source.
        // Unknown provenance (hand copy, embedded track, older row) stays allowed everywhere.
        var herkunft = Registry.GetOrigin(contentHashEarly).Origin;
        // F-M489: die Entscheidung fuer BEIDE Quellen an einer Stelle — Schalter, Herkunft (F-M487) und
        // "diese Quelle hat ihn schon" (F-M488). Ein Reup auf dieselbe Quelle ist genauso unerwuenscht
        // wie eine Kreuzkontamination, und SubDLs alter Status darf tvsubtitles nicht blockieren.
        var gate = Api.Endpoints.UploadGate.Decide(
            SourceAllowed(Configuration.SubtitleProvider.Subdl),
            SourceAllowed(Configuration.SubtitleProvider.TvSubtitles),
            herkunft,
            Registry.IsUploadedTo(mediaHash, lang, hearingImpaired, Configuration.SubtitleProvider.Subdl),
            Registry.IsUploadedTo(mediaHash, lang, hearingImpaired, Configuration.SubtitleProvider.TvSubtitles),
            SourceAllowed(Configuration.SubtitleProvider.MovieSubtitles),
            Registry.IsUploadedTo(mediaHash, lang, hearingImpaired, Configuration.SubtitleProvider.MovieSubtitles));
        if (gate.SkipReason is not null)
        {
            LogUtil.PerItem(_config.LogMode, _logger, "[Sub Scribe] {Name} [{Lang}] — skipped: {Reason}",
                neutralName, lang, gate.SkipReason);
            return UploadResult.Skipped(gate.SkipReason);
        }

        // F-M500 (Operator-Anweisung 15.11.2026): eine bekannte Herkunft sperrt ALLE Quellen. Die Zeile
        // nennt sie, damit im Lauf steht, WOHER der Text kommt und warum er nirgends hochgeht.
        if (Api.Endpoints.Provenance.OriginBlockedReason(herkunft) is { } herkunftGesperrt)
        {
            LogUtil.PerItem(_config.LogMode, _logger,
                "[Sub Scribe] {Name} [{Lang}] — {Reason}", neutralName, lang, herkunftGesperrt);
        }

        // F-M495 (Operator-Anweisung 15.11.2026) / F-M509 (Klärstellung: „Tvst is splitted into tvst and
        // moviesub itself"): EINE Schleife ueber die zustaendigen Quellen — keine Fallunterscheidung
        // mehr, die „alles ausser SubDL" in einen Zweig wirft. Was in der Liste steht, entscheidet die
        // Reihenfolge (Familien-Ebene) und die Medienart: bei einer Serie stehen SubDL und
        // tvsubtitles.net darin, bei einem Film SubDL und moviesubtitles.org.
        //
        // JE QUELLE gilt: eigener Schalter, eigene Konfiguration, eigenes Tor (Herkunft F-M487,
        // „diese Quelle hat ihn schon" F-M488), eigener Versuch, eigenes Ergebnis, eigene Marke — und ein
        // Fehler bei EINER Quelle beendet die Kette nicht (der Versuch der naechsten laeuft weiter).
        // Genau deshalb wird das Ergebnis in einer Ablage JE QUELLE gesammelt und nicht in zwei
        // benannten Variablen.
        var quellenReihenfolge = UploadSourceOrder(isSeries);
        var ergebnisse = new Dictionary<Configuration.SubtitleProvider, UploadResult>();
        var zustaendig = new List<Configuration.SubtitleProvider>();

        // F-M587 (Operator-Anweisung 17.11.2026: „Parallel an subdl und tvst quasi"): die Quellen
        // laufen je Spur GLEICHZEITIG statt nacheinander. Gemessen am 17.11.2026 an einer Spur
        // (Elle S01E08): tvst 0,9 s, SubDL 6,8 s — die alte Kette wartete beides hintereinander ab
        // (17,5 s je Spur, davon 9,8 s Takt). Der Takt bleibt, aber er wird nicht mehr zweimal je
        // Spur bezahlt, und der kurze tvst-Zweig verschwindet im langen SubDL-Zweig.
        //
        // Versatz: die zweite Quelle startet 0-5 % des Transfertakts spaeter (bei 400/h also bis
        // 0,45 s), damit nicht zwei Anfragen in derselben Millisekunde aus demselben Lauf gehen —
        // der Operator wollte „kleinen Versatz plus minus 5 Prozent", keinen Gleichstart.
        //
        // Was UNVERAENDERT bleibt: die Pruefungen je Quelle (Schalter, Zugangsdaten, Tor) laufen
        // weiter VOR dem Versand und in Kettenreihenfolge, damit das Log dieselbe Reihenfolge und
        // dieselben Begruendungen zeigt; gesammelt wird je Quelle, und die Auswahl der Fassung
        // (unten) liest die Kettenreihenfolge, nicht die Antwortzeit.

        foreach (var quelle in quellenReihenfolge)
        {
            // Der Toggle der Quelle, ueber denselben Pruefpunkt wie Gate und Sammelstufe. Aus heisst
            // UEBERSPRINGEN, nicht abbrechen: die andere Quelle kann den Untertitel noch nehmen.
            // F-M508: die Film-Adresse ist standardmaessig aus — DASS sie deshalb nicht sendet, steht
            // als Zeile im Lauf, statt still zu fehlen („Bevor wir falsche Sprachen hochladen, laden wir
            // nix hoch.").
            if (!SourceAllowed(quelle))
            {
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[Sub Scribe] {Name} [{Lang}] — source {Source} skipped: that source is switched off, nothing sent",
                    neutralName, lang, Api.Endpoints.Provenance.Name(quelle));
                continue;
            }

            // F-M497 (Operator-Anweisung 15.11.2026): „Wenn subdl nich konfiguriert -> skip subdl
            // upload". Die zweite Frage neben dem Schalter: HAT diese Quelle, was ihr Upload braucht?
            // Fehlt sie, ist das kein Fehler und kein Blocker mehr (MissingCredentials kennt nur noch
            // den TMDb-Schluessel) — die Quelle faellt aus der Kette, die andere laeuft weiter, und der
            // Grund steht einmal im Log. Im Trockenlauf gilt die Frage NICHT: er macht keinen Aufruf
            // und braucht deshalb keine Zugangsdaten (F-M496) — er zeigt, was ein echter Lauf taete.
            if (!_config.DryRun && !_config.IsConfiguredForUpload(quelle))
            {
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[Sub Scribe] {Name} [{Lang}] — source {Source} skipped — not configured (required for SubDL only)",
                    neutralName, lang, Api.Endpoints.Provenance.Name(quelle));
                continue;
            }

            // F-M489/F-M495: die beiden Tore gelten JE QUELLE — „diese Quelle hat ihn schon" (F-M488)
            // und die Herkunft (F-M487, verschaerft 15.11.2026: bekannt = NIRGENDS hin, unbekannt =
            // ueberallhin). Ein verbotenes Ziel wird uebersprungen und im Log genannt; die Kette laeuft
            // weiter, das Tor wird also nicht abgeschafft, sondern je Quelle gefragt.
            if (!Api.Endpoints.UploadGate.May(gate, quelle))
            {
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[Sub Scribe] {Name} [{Lang}] — source {Source} skipped: {Reason}",
                    neutralName, lang, Api.Endpoints.Provenance.Name(quelle),
                    Api.Endpoints.Provenance.MayUploadTo(herkunft, quelle)
                        ? "already-uploaded:" + Api.Endpoints.Provenance.Name(quelle)
                        : Api.Endpoints.Provenance.OriginBlockedReason(herkunft) ?? "no-upload-source");
                continue;
            }

            // F-M587: was alle Pruefungen bestanden hat, wird nur noch GEMERKT — gestartet wird
            // gesammelt hinter der Schleife, damit die Quellen gleichzeitig laufen.
            // F-M605 (Operator-Frage 18.11.2026: „Erst fragen ob die id bespielbar ist, vor dem hochladen
            // geht nicht?"): die Quelle hat fuer GENAU diesen Inhalt schon ein inhaltliches Nein gegeben —
            // dann wird sie nicht erneut gefragt, solange der Text derselbe ist. Ein voruebergehendes Nein
            // (Rate Limit, Timeout, Abbruch) steht hier nicht, das wird jedes Mal neu gefragt.
            if (Jellyfin.Plugin.SubScribe.Registry.SourceRefusalStore.StillRefused(
                    mediaHash ?? string.Empty, subPos, SeasonPack.LangTag(lang, hearingImpaired), quelle.ToString(),
                    contentHashEarly, out string gemerktesNein))
            {
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[Sub Scribe] {Name} [{Lang}] — source {Source} skipped: remembered refusal ({Reason}) (F-M605)",
                    neutralName, lang, Api.Endpoints.Provenance.Name(quelle), gemerktesNein);
                ergebnisse[quelle] = UploadResult.Skipped(gemerktesNein);
                continue;
            }

            zustaendig.Add(quelle);
        }

        // F-M587: Versatz zwischen den Quellen — 0-5 % des Transfertakts, mindestens 1 ms.
        int versatzMs = Math.Max(1, (int)(_limiter.TransferPauseMs() * 0.05));

        async Task<UploadResult> Versand(Configuration.SubtitleProvider quelle, int index)
        {
            // F-M587: kleiner Versatz statt Gleichstart — die erste Quelle geht sofort raus, jede
            // weitere nach 0-5 % des Takts. Bei einer einzigen Quelle passiert hier nichts.
            if (index > 0)
            {
                try
                {
                    await Task.Delay(Random.Shared.Next(1, versatzMs + 1), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return UploadResult.NoVerdict("run cancelled before the send");
                }
            }

            try
            {
                // Ein Zweig JE QUELLE — aber nicht mehr „SubDL oder alles andere": jede Quelle hat ihre
                // eigene Versandmethode, und welche das ist, sagt der Anbieter selbst. Ein vierter
                // Anbieter braucht damit eine Methode und einen Eintrag in der Ablage, keine neue
                // Fallunterscheidung hier.
                var versand = await SendToSourceAsync(
                        quelle, srtContent, neutralName, lang, releaseName, quality, imdbId, tmdbId,
                        isSeries, season, episode, hearingImpaired, mediaHash, mediaPath, framerate, ct)
                    .ConfigureAwait(false);

                // F-M590: sofort notieren, was DIESE Quelle angenommen hat — bevor die andere Quelle
                // ueberhaupt geantwortet hat. Ein Abbruch danach nimmt die Marke nicht mehr mit.
                if (versand.Ok)
                {
                    MarkSourceUploadNow(quelle, versand.SourceId, mediaHash, subPos, lang,
                        hearingImpaired, forced, loosePath, contentHashEarly);
                }

                return versand;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // F-M590: der HttpClient meldet seinen Timeout als TaskCanceledException — also als
                // OperationCanceledException. Das ist KEIN Abbruch des Laufs: diese eine Quelle ist
                // ausgefallen, die andere macht weiter. Vorher riss so ein Timeout den ganzen Lauf ab
                // (gemessen 17.11.2026: moviesubtitles antwortete 90 s nicht, der Lauf endete mit
                // „cancelled — 0 uploaded", obwohl SubDL den Untertitel schon angenommen hatte).
                _logger.LogWarning(
                    "[Sub Scribe] source {Source} did not answer for {Name} [{Lang}] (client timeout) — the other source keeps going.",
                    Api.Endpoints.Provenance.Name(quelle), neutralName, lang);
                return UploadResult.NoVerdict("timeout");
            }
            catch (OperationCanceledException)
            {
                // Ein echter Abbruch (Nutzer-Stop, Watchdog, Neustart) — der gilt fuer den ganzen Lauf.
                throw;
            }
            catch (Exception ex)
            {
                // F-M495: ein Fehler bei DIESER Quelle darf den Versuch bei der ANDEREN nicht
                // verhindern. Das Ergebnis der Quelle ist ihr eigener Fehlschlag — und weil die
                // Quellen seit F-M587 gleichzeitig laufen, wird der Fehlschlag zurueckgegeben statt
                // geworfen: sonst risse ihn Task.WhenAll mitten in den Versand der anderen Quelle.
                _logger.LogWarning(
                    "[Sub Scribe] upload to {Source} failed for {Name} [{Lang}]: {Msg} — the other source is still tried.",
                    Api.Endpoints.Provenance.Name(quelle), neutralName, lang, ex.Message);
                // F-M609: eine Ausnahme ist KEINE Antwort — kein Urteil, Paar bleibt offen.
                return UploadResult.NoVerdict(ex.Message);
            }
        }

        quellenMs = takt.ElapsedMilliseconds; // F-M597

        if (zustaendig.Count > 0)
        {
            // F-M594: der Slot wird VOR dem Versand dieser Spur abgewartet — gemessen ab dem Ende
            // des letzten echten Transfers, nicht blind nach dem vorigen. Extraktion und
            // Gate-Pruefungen der Spur sind damit Teil der Pause statt ein Zuschlag darauf.
            // Eine Spur bekommt EINEN Slot, auch wenn zwei Quellen bedient werden (F-M587): die
            // Rate zaehlt Spuren, sonst halbierte sie sich bei jeder zweiten Quelle.
            await _limiter.WaitForTransferSlotAsync(ct).ConfigureAwait(false);
            slotMs = takt.ElapsedMilliseconds; // F-M597

            var laeufe = new Task<UploadResult>[zustaendig.Count];
            for (int i = 0; i < zustaendig.Count; i++)
            {
                laeufe[i] = Versand(zustaendig[i], i);
            }

            var jeQuelle = await Task.WhenAll(laeufe).ConfigureAwait(false);
            versandMs = takt.ElapsedMilliseconds; // F-M597

            // F-M597: eine Zeile je Spur, die die Zeit zeigt statt sie zu vermuten.
            LogUtil.Detail(
                _logger,
                "[Sub Scribe] TAKT {Name} [{Lang}] — qa {Qa} ms, sources {Quellen} ms, slot wait {Slot} ms, send {Versand} ms, total {Gesamt} ms (F-M597)",
                neutralName, lang,
                qaMs, quellenMs - qaMs, slotMs - quellenMs, versandMs - slotMs, takt.ElapsedMilliseconds);

            // F-M594: der Anker fuer den naechsten Slot. Gesetzt nur, wenn mindestens eine Quelle
            // die Spur angenommen hat — ein Fehlschlag ohne Uebertragung soll den Takt nicht kosten
            // (F-M547/F-M589).
            if (jeQuelle.Any(r => r.Ok))
            {
                _limiter.MarkTransferDone();
            }

            // F-M590: sind ALLE zustaendigen Quellen in den Timeout gelaufen, ist das ein Ausfall, kein
            // Urteil ueber den Text. Erst wenn das mehrfach hintereinander passiert, ist der Lauf
            // abzubrechen (die Seiten sind dann weg, nicht der Untertitel schlecht).
            bool alleImTimeout = jeQuelle.All(r => !r.Ok && r.Reason == "timeout");
            _quellenAusfaelleInFolge = alleImTimeout ? _quellenAusfaelleInFolge + 1 : 0;

            for (int i = 0; i < zustaendig.Count; i++)
            {
                // F-M550: die antwortende Quelle reist im Ergebnis mit — der Aufrufer weiss sonst nicht,
                // wer genommen hat, und schrieb frueher nur das alte Praefix „[SubDL]" davor.
                jeQuelle[i].Source = zustaendig[i];
                ergebnisse[zustaendig[i]] = jeQuelle[i];
            }
        }

        UploadResult? Ergebnis(Configuration.SubtitleProvider provider) =>
            ergebnisse.TryGetValue(provider, out var e) ? e : null;

        // F-M605 (Operator-Frage 18.11.2026: „Erst fragen ob die id bespielbar ist, vor dem hochladen geht
        // nicht?"): das Nein einer Quelle wird mit dem Inhalts-Hash gemerkt — aber nur ein inhaltliches
        // („die Quelle kennt den Film nicht", „die Seite hat die Serie abgelehnt"). Ein voruebergehendes
        // Nein (Rate Limit, Timeout, Abbruch) wird NICHT gemerkt: das muss der naechste Lauf neu fragen
        // duerfen. Hat eine Quelle den Inhalt genommen, wird ein altes Nein vergessen.
        foreach (var (gemerkteQuelle, gemerkt) in ergebnisse)
        {
            if (gemerkt.Ok || gemerkt.SourceAccepted)
            {
                Jellyfin.Plugin.SubScribe.Registry.SourceRefusalStore.Forget(
                    mediaHash ?? string.Empty, subPos, SeasonPack.LangTag(lang, hearingImpaired),
                    gemerkteQuelle.ToString());
            }
            else if (Jellyfin.Plugin.SubScribe.Registry.SourceRefusalStore.ShouldRemember(gemerkt.Reason, gemerkt.Verdict))
            {
                // F-M609: gemerkt wird jedes NEIN — nicht mehr nur eine handgepflegte Liste von
                // Wortanfaengen. Eine Stoerung (NoVerdict) faellt hier heraus und darf beim naechsten
                // Lauf neu gefragt werden.
                Jellyfin.Plugin.SubScribe.Registry.SourceRefusalStore.Put(
                    mediaHash ?? string.Empty, subPos, SeasonPack.LangTag(lang, hearingImpaired),
                    gemerkteQuelle.ToString(), contentHashEarly, gemerkt.Reason ?? string.Empty,
                    gemerkt.Verdict);
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[Sub Scribe] {Name} [{Lang}] — source {Source} said no ({Reason}), remembered for this content (F-M605/F-M609)",
                    neutralName, lang, Api.Endpoints.Provenance.Name(gemerkteQuelle), gemerkt.Reason);
            }
            else if (gemerkt.Verdict == Api.UploadVerdict.NoVerdict)
            {
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[Sub Scribe] {Name} [{Lang}] — source {Source} gave no verdict ({Reason}); the pair stays open, the next run asks again (F-M609)",
                    neutralName, lang, Api.Endpoints.Provenance.Name(gemerkteQuelle), gemerkt.Reason);
            }
        }

        bool Genommen(Configuration.SubtitleProvider provider) => Ergebnis(provider) is { Ok: true };

        bool subdlGenommen = Genommen(Configuration.SubtitleProvider.Subdl);
        bool zweiteGenommen = Genommen(Configuration.SubtitleProvider.TvSubtitles);

        // F-M508: die Film-Adresse fuehrt ihr eigenes „genommen" — es wird NICHT in zweiteGenommen
        // gemischt, sonst traege die tvst-Marke den Zeitstempel einer anderen Adresse. Und weil die
        // Seite keine Id nennt, steht hier, was gilt: angenommen, Id unbekannt.
        bool dritteGenommen = Genommen(Configuration.SubtitleProvider.MovieSubtitles);
        if (dritteGenommen)
        {
            LogUtil.PerItem(_config.LogMode, _logger,
                "[moviesubtitles] {Name} [{Lang}] — accepted, id unknown: the site names none (own mark minted)",
                neutralName, lang);
        }

        // Was der Rest der Methode liest: die Fassung, die genommen hat — in Kettenreihenfolge, denn dort
        // steht, wer zuerst gefragt wurde. Hat keine genommen, der aussagekraeftigste Fehlschlag (eine
        // „nicht zustaendig"-Meldung einer Quelle erklaert nichts, wenn eine andere es versucht und nicht
        // genommen hat).
        UploadResult result = quellenReihenfolge.Select(Ergebnis).FirstOrDefault(e => e is { Ok: true })
            ?? quellenReihenfolge.Select(Ergebnis).FirstOrDefault(e => e is { SkippedItem: false })
            ?? quellenReihenfolge.Select(Ergebnis).FirstOrDefault(e => e is not null)
            ?? UploadResult.Skipped("no-upload-source");

        if (result.Ok)
        {
            // F-M17y/F-M184: SubDL returns "sent for review" for every upload. The final
            // verdict (approved / rejected / duplicate-remote) is resolved by SubDL at a
            // time we neither control nor know — review latency varies (seconds to hours,
            // depending on backlog and content). There is therefore NO fixed wait here and
            // none anywhere else: the upload is considered live immediately, and the
            // postprocessing job picks the entry up on its own schedule, whenever it runs
            // and whatever the verdict says by then. A verdict that has not arrived yet is
            // simply resolved on a later run — nothing is polled and nothing times out.
            string? sdId = null;
            int? uploadId = null;
            // F-M495: die Antwort mit `sd_id`/`subtitle_id` und die Id-Nachfrage bei mySubtitles gehoeren
            // zu SubDL — hat tvsubtitles allein genommen, gibt es hier nichts zu lesen. Die Marken
            // beider Quellen stehen weiter unten und haengen an `subdlGenommen`/`zweiteGenommen`.
            if (subdlGenommen && result.Response != null)
            {
                var root = result.Response.RootElement;

                // F-M532: die Upload-Antwort wird genauso geprueft. Ist die Gestalt falsch, wird sie
                // gemeldet und die Id NICHT gelesen — der Eintrag bleibt ohne Id statt mit geratener.
                bool antwortInOrdnung = !Jellyfin.Plugin.SubScribe.Api.ScribeFieldSchema.Emit(
                    Jellyfin.Plugin.SubScribe.Api.ScribeFieldSchema.CheckUploadAnswer(root), "upload answer");
                if (antwortInOrdnung
                    && root.TryGetProperty("subtitle", out var subEl)
                    && subEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (subEl.TryGetProperty("sd_id", out var sdEl) && sdEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        sdId = sdEl.GetString();
                    }
                    if (subEl.TryGetProperty("subtitle_id", out var sidEl) && sidEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        if (sidEl.TryGetInt32(out var sid))
                        {
                            uploadId = sid;
                        }
                    }
                }
            }
            // F-M413 (operator order 02.11.2026): "wir brauchen die id zum upload". The upload answer
            // of a single subtitle carries no id — it says "subtitle sent for review" — so the row
            // kept NULL and the pack could never delete the single it absorbed. The id does exist:
            // /user/mySubtitles lists every own upload with its UploadId. It is fetched right here,
            // per upload, and written into the same row.
            if (subdlGenommen && uploadId != null)
            {
                // F-M421 (operator question 02.11.2026: "wird die upload id im log gelistet?"): yes,
                // in every case. The id is the only handle that can delete this single when a pack
                // replaces it, so it is named on the upload — not only when it had to be fetched.
                LogUtil.Normal(
                    _logger,
                    "[Sub Scribe] upload id {Id} from the upload answer for {File} [{Lang}].",
                    uploadId, Path.GetFileName(loosePath ?? mediaPath ?? "?"), lang);
            }
            else if (subdlGenommen)
            {
                // F-M588 (Operator-Anweisung 17.11.2026: „Ok für mysubtitels"): die Id wird NICHT mehr
                // je Upload geholt. Gemessen am 17.11.2026: die Antwort nennt sie nie (0 von 2.790),
                // und die eigene Liste kostet je Abruf 340 kB / 798 Eintraege — bei ~2.790 Uploads am
                // Tag rund 950 MB und eine knappe Stunde Wartezeit. Jetzt wird der Eintrag hier nur
                // VORGEMERKT; am Laufende holt EIN Abruf die Ids fuer alle offenen Uploads
                // (ResolveDeferredUploadIdsAsync) und traegt sie in die Zeilen ein.
                _offeneSubdlIds.Add(new PendingSubdlId(
                    imdbId, season, episode, lang, hearingImpaired,
                    mediaHash, subPos, loosePath != null, Path.GetFileName(loosePath), releaseName, DateTime.UtcNow));
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[Sub Scribe] upload saved for {File} [{Lang}] — the SubDL id is read back once at the end of the run (F-M588).",
                    Path.GetFileName(loosePath ?? mediaPath ?? "?"), lang);
            }

            // Metadata describing the file, and the verdict describing this subtitle, are two
            // different facts and get two rows in two areas.
            Registry.MarkAndFlush(() => Registry.PatchMediaMetadata(mediaHash, imdbId, season, episode, sdId, tmdbId));
            // F-M488/F-M490/F-M509: EINE Marke JE QUELLE, aus derselben Ablage, in der die Kette ihre
            // Ergebnisse gesammelt hat. Der Zeitpunkt wird EINMAL genommen, damit Id und Stempel
            // dieselbe Sekunde tragen; jede Quelle bekommt ihre eigene Zeile, keine erbt die einer
            // anderen.
            var jetzt = DateTime.UtcNow;
            var subdlMarke = MarkOf(ergebnisse, Configuration.SubtitleProvider.Subdl,
                uploadId?.ToString(System.Globalization.CultureInfo.InvariantCulture), contentHashEarly, releaseName, jetzt);
            var tvstMarke = MarkOf(ergebnisse, Configuration.SubtitleProvider.TvSubtitles,
                null, contentHashEarly, releaseName, jetzt);
            var msubMarke = MarkOf(ergebnisse, Configuration.SubtitleProvider.MovieSubtitles,
                null, contentHashEarly, releaseName, jetzt);

            Registry.MarkAndFlush(() =>
            {
                if (loosePath != null)
                {
                    Registry.MarkSidecar(contentHashEarly, mediaHash ?? contentHashEarly, lang, hearingImpaired, forced,
                        SubtitleStatus.Uploaded, subdlId: uploadId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        fileName: Path.GetFileName(loosePath), path: loosePath,
                        // F-M488: eigenes Feld je Quelle und Richtung. Liefert die Quelle keine Id,
                        // vergeben wir eine eigene (Präfix own:) — ein Upload ohne Kennzeichen wäre von
                        // einem, der nie stattfand, nicht zu unterscheiden.
                        // F-M495: JEDE Quelle bekommt ihre Marke, soweit sie genommen hat — nach der
                        // Kette können mehrere Felder gesetzt sein, vorher war es genau eines.
                        subdlUploadId: subdlMarke.Id,
                        tvSubtitlesUploadId: tvstMarke.Id,
                        moviesubtitlesUploadId: msubMarke.Id,
                        // F-M490: "hochgeladen" je Quelle, mit Zeitpunkt — nur fuer die Quelle, die
                        // den Upload wirklich genommen hat.
                        subdlUploadedAt: subdlMarke.At,
                        tvSubtitlesUploadedAt: tvstMarke.At,
                        moviesubtitlesUploadedAt: msubMarke.At);
                }
                else
                {
                    Registry.MarkEmbed(mediaHash, subPos, lang, hearingImpaired,
                        SubtitleStatus.Uploaded, contentHash: contentHashEarly,
                        subdlId: uploadId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        // F-M495/F-M508: je Quelle ihre eigene Marke — mehrere Felder können gesetzt sein.
                        subdlUploadId: subdlMarke.Id,
                        tvSubtitlesUploadId: tvstMarke.Id,
                        moviesubtitlesUploadId: msubMarke.Id,
                        // F-M490: "hochgeladen" je Quelle, mit Zeitpunkt — nur fuer die Quelle, die
                        // den Upload wirklich genommen hat.
                        subdlUploadedAt: subdlMarke.At,
                        tvSubtitlesUploadedAt: tvstMarke.At,
                        moviesubtitlesUploadedAt: msubMarke.At);
                }
            });

        }
        else if (result.Reason == "duplicate-content")
        {
            // Legacy path: if the API ever answers an immediate duplicate verdict, handle
            // it right away. The delayed cleanup job also covers this case via mySubtitles.
            Registry.MarkAndFlush(() => Registry.PatchMediaMetadata(mediaHash, imdbId, season, episode, null, tmdbId));
            Registry.MarkAndFlush(() => Registry.MarkEmbed(
                mediaHash, subPos, lang, hearingImpaired,
                SubtitleStatus.Rejected, contentHash: contentHashEarly, reason: RejectReason.DuplicateRemote));
        }
        else if (_api.ServerRateLimited)
        {
            _logger.LogWarning("[Sub Scribe] SubDL account daily limit (HTTP 429) — upload run should stop early.");
        }

        return result;
    }

    /// <summary>
    /// F-M49 (split 08.09.2026 evening, user decision): wait out the account-wide
    /// SubDL daily API request quota once per run and continue after the
    /// server-reported reset. Uploads draw only from the API quota —
    /// UploadContinueAfterLimit governs this direction.
    /// </summary>
    /// <param name="watchdog">In-run watchdog (heartbeats keep it quiet during the wait).</param>
    /// <param name="ct">Cancellation token (user cancel + watchdog).</param>
    /// <returns>True when the wait completed and the upload can be retried.</returns>
    /// <summary>
    /// F-M24a (user decision 09.09.2026): the TMDb call trace goes through ONE gate,
    /// Verbose. F-M245 (28.09.2026): it also went through Detail, so at Debug mode every
    /// TMDb line appeared twice — Verbose is a SUBSET of Debug, not a separate tier, and
    /// "additionally at Debug" was the misconception behind the second call. Measured on
    /// the 28.09. upload run: 167 duplicated messages, all TMDb.
    /// </summary>
    private void OnTmdbTrace(string msg)
    {
        LogUtil.Trace(_config.LogMode, _logger, "{Tag} {Msg}", "[Sub Scribe]", msg);
    }

    /// <summary>F-M24a: per-API-call trace → LogLevel.Debug (never Normal/Verbose).</summary>
    private void OnApiTrace(string msg) => LogUtil.Detail(_config.LogMode, _logger, "[Sub Scribe] {Msg}", msg);

    /// <summary>
    /// F-M232: routes the API client's LogInfo channel into the run log. Login retries,
    /// raw 429 bodies and rate headers arrive here; Detail keeps them at the plugin's
    /// own Debug mode so Normal stays a lifecycle-only log.
    /// </summary>
    /// <param name="msg">Message from the API client.</param>
    private void OnApiLog(string msg) => LogUtil.Detail(_config.LogMode, _logger, "[Sub Scribe] {Msg}", msg);

    private async Task<bool> WaitOutDailyLimitAsync(RunWatchdog watchdog, CancellationToken ct)
    {
        if (!_config.UploadContinueAfterLimit)
        {
            // F-M152 (rev.10, user decision 17.09.2026): tick OFF = clean stop,
            // NO recovery fire. Items stay due; the next trigger is an arrival
            // event or the weekly anchor. (Old F-M65 fire-on-tick-off removed.)
            LogUtil.Normal(_logger, "[Sub Scribe] Daily limit reached — continue-after-limit is OFF: clean stop, no recovery fire (F-M152).");
            return false;
        }

        // F-M238 (user decision 28.09.2026): ask the counters before treating a 429 as
        // the spent allowance. Uploads draw on the same search allowance, so a trip that
        // left requests free is a rate-limit respacing (one fire after JobSpacingMinutes),
        // not a day-long stop. A spent OR unreadable allowance keeps the day-long path.
        var read = await _api.ReadQuotaAsync(forDownload: false, ct).ConfigureAwait(false);
        if (read == QuotaRead.Available)
        {
            _api.ResetRateLimitFlag();
            ScheduledTasks.ScribeSchedulerCoordinator.Instance?.ScheduleOverloadFire(upload: true, DateTime.UtcNow);
            _logger.LogWarning(
                "[Sub Scribe] 429 is a short-term rate limit — search allowance still has {Remaining}/{Limit} left; "
                + "run respaced by JobSpacingMinutes, items stay due (F-M238).",
                _api.Quota?.SearchRemaining, _api.Quota?.SearchLimit);
            return false;
        }

        var reset = _api.RateLimitResetUtc;

        // F-M49/F-M54 (user decision 08.09.2026 evening): the daily-limit 429 carries
        // NO reset header in practice (verified live) — fall back to the next
        // UTC midnight (the quota resets per UTC day, panel-verified 09.09.2026).
        if (!reset.HasValue)
        {
            var nowUtc = DateTime.UtcNow;
            reset = nowUtc.Date.AddDays(1); // next 00:00 UTC
            LogUtil.Normal(_logger, 
                "[Sub Scribe] Daily limit reached without a server-reported reset time — waiting until {ResetUtc} UTC (next UTC midnight) instead.",
                reset.Value.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (_dailyLimitWaits >= 1)
        {
            _logger.LogWarning("[Sub Scribe] Daily limit again — run stopped, next scheduled run.");
            return false;
        }

        _dailyLimitWaits++;

        // (user decision 09.09.2026 "Neustart nach daily limit von random 2..4h nach
        // offiziellem Reset"): do NOT resume exactly at the official reset time — every
        // API consumer hammers SubDL at 00:00 UTC (verified: the 02:05 post-reset run
        // died on a service_busy 429 five minutes after reset, panel showed 24/2000).
        // Dice a random 5-30 min offset AFTER the reset time (, 11.09.2026:
        // F-M152 (rev.10): anchor = server reset (exact, from v2 headers) or
        // fail-safe next 00:00 UTC. No local dicing - the coordinator adds the
        // 30..300 min jitter per fire (direction-independent; the coordinator's dice,
        // ScribeSchedulerCoordinator. F-M152). Not to be confused with the plain deferral
        // (F-M26b), which offsets by JobSpacingMinutes.
        var effectiveReset = _api.Edge429NoHeaders || !_api.RateLimitResetUtc.HasValue
            ? NextMidnightUtc()
            : _api.RateLimitResetUtc.Value;

        // F-M182 (user report 23.09.2026): this used ScheduleRecoveryFireAt, whose
        // alreadyJittered:true disabled the coordinator's dice — the fire landed exactly
        // on the reset while the log claimed +30..300 min jitter (verified live on the
        // download twin: 21.09. reset 02:00 local → fire 02:00 local, +0 min).
        // ScheduleRecoveryFire lets the coordinator dice 30..300 min per fire (crypto
        // RNG, direction-independent), keeping this installation off the 00:00 UTC herd.
        ScheduledTasks.ScribeSchedulerCoordinator.Instance?.ScheduleRecoveryFire(upload: true, effectiveReset.ToUniversalTime());

        string resetLocal = TimeZoneInfo.ConvertTimeFromUtc(effectiveReset, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var fireInfo = ScheduledTasks.ScribeSchedulerCoordinator.Instance?.GetPendingFireUtc(upload: true);
        _logger.LogWarning(
            "[Sub Scribe] Account daily limit reached - run stopped; reset ~{ResetLocal} local, recovery fire ~{Next} local (F-M152, F-M182).",
            resetLocal,
            fireInfo.HasValue
                ? TimeZoneInfo.ConvertTimeFromUtc(fireInfo.Value, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                : "next scheduled run");
        return false;
    }

    /// <summary>
    /// Resolve the ffmpeg executable path used to extract embedded text subtitles.
    /// Order of preference: explicit config override, Jellyfin's configured encoder path,
    /// "ffmpeg" in PATH, then common system locations. Logs the chosen path once at Info.
    /// </summary>
    /// <summary>
    /// Resolve ffmpeg once per process and reuse it. Re-resolves only when the
    /// explicit FfmpegPath override changes.
    /// </summary>
    private static string GetResolvedFfmpegPath(PluginConfiguration config, ILogger<UploadPipeline> logger)
    {
        string configKey = config.FfmpegPath ?? string.Empty;
        if (_cachedFfmpegPath != null && _cachedFfmpegConfig == configKey)
        {
            LogUtil.Detail(logger, "[Sub Scribe] ffmpeg path from cache: {Path}", _cachedFfmpegPath);
            return _cachedFfmpegPath;
        }

        string resolved = ResolveFfmpegPath(config, logger);
        _cachedFfmpegPath = resolved;
        _cachedFfmpegConfig = configKey;
        return resolved;
    }

    private static string ResolveFfmpegPath(
        PluginConfiguration config,
        ILogger<UploadPipeline> logger)
    {
        // 1) explicit user override
        if (!string.IsNullOrWhiteSpace(config.FfmpegPath))
        {
            var configured = config.FfmpegPath.Trim();
            if (File.Exists(configured))
            {
                LogUtil.Detail(config.LogMode, logger, "[Sub Scribe] ffmpeg path from config override: {Path}", configured);
                return configured;
            }

            logger.LogWarning("[Sub Scribe] configured ffmpeg override not found: {Path}, falling back to auto-detection", configured);
        }

        // 2) PATH lookup via "which ffmpeg" / "where ffmpeg"
        var candidates = new[] { "ffmpeg" };
        foreach (var candidate in candidates)
        {
            var path = FindInPath(candidate);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                LogUtil.Detail(config.LogMode, logger, "[Sub Scribe] ffmpeg path from PATH: {Path}", path);
                return path;
            }
        }

        // 4) common static locations (Linux/Docker first, then macOS, then Windows)
        var staticCandidates = new[]
        {
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            "/usr/share/jellyfin/ffmpeg/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/usr/bin/ffmpeg",
            "/opt/ffmpeg/ffmpeg",
            "/opt/jellyfin-ffmpeg/ffmpeg",
            "/config/ffmpeg",
            "/Applications/Jellyfin.app/Contents/MacOS/ffmpeg",
            "C:\\ProgramData\\Jellyfin\\Server\\ffmpeg.exe",
            "C:\\Program Files\\Jellyfin\\Server\\ffmpeg.exe"
        };

        foreach (var candidate in staticCandidates)
        {
            if (File.Exists(candidate))
            {
                LogUtil.Detail(logger, "[Sub Scribe] ffmpeg path from static fallback: {Path}", candidate);
                return candidate;
            }
        }

        // 5) last resort: keep "ffmpeg" and let the OS report the failure in the extraction log
        logger.LogWarning("[Sub Scribe] ffmpeg not found anywhere; extraction will fail until ffmpeg is installed or FfmpegPath is configured");
        return "ffmpeg";
    }

    /// <summary>Locate an executable in PATH. Uses "where" on Windows, "which" elsewhere.</summary>
    private static string? FindInPath(string name)
    {
        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "where" : "which",
                Arguments = isWindows ? $"\"{name}\"" : name,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return null;
            }

            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                return null;
            }

            // where/which may return multiple lines; take the first non-empty one
            foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed) && File.Exists(trimmed))
                {
                    return trimmed;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>
    /// F-M5: extract EVERY text subtitle stream of one file in a SINGLE ffmpeg call.
    /// </summary>
    /// <remarks>
    /// Why one call and not one per stream: ffmpeg must open and (over CIFS/NAS)
    /// re-read the container for every invocation. On a 1 GB episode with 83
    /// subtitle streams that is 83 reads of the same gigabyte — measured on prod
    /// 28.09.2026 as the only multi-minute stalls in an otherwise 11 s-median run.
    /// One call with repeated <c>-map 0:s:N</c> + <c>-f srt</c> output pairs reads the
    /// file once and writes one file per stream, so the container is read a single
    /// time however many streams it carries.
    /// <para>
    /// The mapping uses the SUBTITLE-relative index (<c>0:s:N</c>), never the container
    /// index: mixing the two lands on video/audio streams (exit 8 / no-stream errors).
    /// Callers pass positions computed against the unfiltered subtitle list.
    /// </para>
    /// </remarks>
    /// <param name="mediaPath">Container to read.</param>
    /// <param name="subIndices">Subtitle-relative stream positions to extract.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The extracted texts plus ffmpeg's exit code. A stream that produced no
    /// text maps to null. The exit code travels with the result because it is
    /// the difference between "this stream has no text" (0) and "the pass went
    /// wrong, retry it per stream" (non-zero) — the caller needs that to decide
    /// whether a null is a verdict or a suspicion.
    /// </returns>
    private async Task<(Dictionary<int, string?> Texts, int ExitCode)> ExtractAllStreamsAsync(
        string mediaPath,
        IReadOnlyList<int> subIndices,
        CancellationToken ct)
    {
        var result = new Dictionary<int, string?>();
        if (subIndices.Count == 0)
        {
            return (result, 0);
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "subdl-scribe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(mediaPath);

            var outputs = new List<(int Index, string Path)>();
            foreach (int idx in subIndices)
            {
                string outFile = Path.Combine(tempDir, $"stream{idx}.srt");
                psi.ArgumentList.Add("-map");
                // NO trailing '?'. It looks like the safe choice — "if this index does not
                // exist, skip it" — but measured 28.09.2026 it is the opposite:
                // `-map 0:s:9?` on a file with 3 streams writes the FIRST subtitle stream
                // (the English one) into the output slot meant for index 9, exits 0 and
                // says nothing. That is a subtitle of the wrong language, silently
                // duplicated, exactly what F-M71/F-M85 exist to prevent. Without '?', an
                // unknown index fails the whole call (exit 234, no output). That is the
                // honest outcome: the caller sees a non-zero exit, retries the empty
                // streams one by one and records a real failure instead of uploading a
                // mislabelled file.
                psi.ArgumentList.Add($"0:s:{idx}");
                psi.ArgumentList.Add("-c:s");
                psi.ArgumentList.Add("srt");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("srt");
                psi.ArgumentList.Add(outFile);
                outputs.Add((idx, outFile));
                result[idx] = null;
            }

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                _logger.LogError("[Sub Scribe] ffmpeg failed to start for {File}", Path.GetFileName(mediaPath));
                return (result, -1);
            }

            string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            // ffmpeg keeps going after a bad stream with -y where it can, so a non-zero
            // exit does NOT mean every output is unusable. Read whatever landed and let
            // the exit code tell the caller whether the nulls are verdicts or suspicions.
            if (proc.ExitCode != 0)
            {
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[Sub Scribe] one-pass extraction of {File} exited {Exit}: {Err}",
                    Path.GetFileName(mediaPath), proc.ExitCode,
                    stderr.Length <= 300 ? stderr : stderr[..300]);
            }

            foreach (var (idx, outFile) in outputs)
            {
                try
                {
                    if (File.Exists(outFile))
                    {
                        string text = await File.ReadAllTextAsync(outFile, ct).ConfigureAwait(false);
                        result[idx] = string.IsNullOrWhiteSpace(text) ? null : text;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("[Sub Scribe] reading extracted stream {Idx} failed for {File}: {Msg}",
                        idx, Path.GetFileName(mediaPath), ex.Message);
                }
            }

            return (result, proc.ExitCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("[Sub Scribe] one-pass extraction error for {File}: {Msg}",
                Path.GetFileName(mediaPath), ex.Message);
            foreach (int idx in subIndices)
            {
                result.TryAdd(idx, null);
            }

            return (result, -1);
        }
        finally
        {
            // F-M7: always clean temp files (success AND failure)
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (Exception)
            {
                // cleanup best effort
            }
        }
    }

    private async Task<string?> ExtractSingleStreamAsync(string mediaPath, int subIndex, CancellationToken ct)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "subdl-scribe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string tempSrt = Path.Combine(tempDir, $"stream{subIndex}.srt");
        try
        {
            var psi = new ProcessStartInfo
            {
                // ffmpeg 7.x: subtitle extraction needs an explicit codec (-c:s srt);
                // -map 0:s:{subIndex} selects the Nth SUBTITLE stream (JF's per-type
                // subtitle index matches ffmpeg's s:N numbering), NOT the container
                // index — mixing them maps onto video/audio (exit 8 / no-stream errors).
                FileName = _ffmpegPath,
                Arguments = $"-y -loglevel error -i \"{mediaPath}\" -map 0:s:{subIndex} -c:s srt \"{tempSrt}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                _logger.LogError("[Sub Scribe] ffmpeg failed to start for {File}", Path.GetFileName(mediaPath));
                return null;
            }

            string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0 || !File.Exists(tempSrt))
            {
                _logger.LogError("[Sub Scribe] ffmpeg extraction failed for {File} stream {Idx}: exit={Exit}, stderr: {Err}",
                    Path.GetFileName(mediaPath), subIndex, proc.ExitCode, stderr.Length <= 300 ? stderr : stderr[..300]);
                return null;
            }

            return await File.ReadAllTextAsync(tempSrt, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError("[Sub Scribe] ffmpeg extraction error for {File}: {Msg}", Path.GetFileName(mediaPath), ex.Message);
            return null;
        }
        finally
        {
            // F-M7: always clean temp files (success AND failure)
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (Exception)
            {
                // cleanup best effort
            }
        }
    }

    private List<MediaStream> GetTextSubtitleStreams(BaseItem item)
    {
        // 10.11: streams come from IMediaSourceManager, not from the Video entity
        var streams = _mediaSourceManager.GetMediaStreams(item.Id);
        if (streams == null)
        {
            return new List<MediaStream>();
        }

        // F-M3/F-M6: text subs only (SRT/ASS/SSA); bitmap (PGS/VobSub) excluded via IsTextSubtitleStream
        return streams
            .Where(s => s.Type == MediaStreamType.Subtitle && s.IsTextSubtitleStream)
            .ToList();
    }

    /// <summary>
    /// F-M9/F-M28 raw ID resolution from JF metadata ONLY (no TMDB calls — the key
    /// is spent only after the metadata wait, user decision 08.09.2026).
    /// </summary>
    /// <summary>
    /// F-M372: the seasons whose episodes on disk are exactly the episodes that aired, with the
    /// aired episode set. Only these are worth buffering into a pack.
    /// <para>
    /// Two questions, two sources: what we hold comes from the library (every Episode item of the
    /// season, deduplicated by episode number — a two-part file carries one number), what aired
    /// comes from <see cref="SeasonEpisodeIndex"/>. A season nobody can answer for is not packed.
    /// </para>
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Complete seasons keyed by (imdb id, season).</returns>
    /// <summary>F-M571: die gepackten Paare einer Staffel, je Lauf einmal aus dem Store.</summary>
    /// <summary>
    /// F-M575: die gepackten Paare einer Staffel — aus der Sammel-Karte, die einmal je Lauf gefuellt wird.
    /// </summary>
    /// <param name="imdbId">Serie.</param>
    /// <param name="season">Staffel.</param>
    /// <returns>Die Paare, leer wenn nichts gepackt ist.</returns>
    private HashSet<(string Language, bool HearingImpaired)> PacksOf(string imdbId, int season)
    {
        var key = (imdbId, season);
        if (!_packsInSeason.TryGetValue(key, out var gepackt))
        {
            _allePacks ??= Registry.PacksBySeason();
            gepackt = _allePacks.TryGetValue(key, out var vorhanden)
                ? vorhanden
                : new HashSet<(string Language, bool HearingImpaired)>();
            _packsInSeason[key] = gepackt;
        }

        return gepackt;
    }

    /// <summary>
    /// F-M575: die Sprachen einer Staffel aus dem Store — ebenfalls aus einer Sammel-Karte statt aus einem
    /// vollen Tabellendurchlauf je Staffel.
    /// </summary>
    /// <param name="imdbId">Serie.</param>
    /// <param name="season">Staffel.</param>
    /// <returns>Sprache und HI-Variante, leer wenn der Store nichts kennt.</returns>
    /// <summary>
    /// F-M604: die Signatur der Packmitglieder. Gelaufene Folgen plus je Folgendatei Nummer, Groesse und
    /// Speicherdatum — ein <c>stat</c> je Datei, kein Lesevorgang. Andert sich ein Mitglied (neu gerippt,
    /// ersetzt, dazugekommen), andert sich die Signatur, und das gespeicherte Urteil gilt nicht mehr.
    /// </summary>
    /// <param name="items">Die Folgen der Staffel, wie die Bibliothek sie liefert.</param>
    /// <param name="aired">Die gelaufenen Folgennummern.</param>
    /// <returns>Die Signatur.</returns>
    private string SeasonSignatureOf(IEnumerable<BaseItem> items, IReadOnlyList<int> aired)
    {
        var dateien = new List<(int Episode, long Size, DateTime Written)>();
        foreach (var item in items)
        {
            var (_, _, _, folge, istSerie) = ResolveIdsRaw(item);
            if (!istSerie || folge < 1 || string.IsNullOrWhiteSpace(item.Path))
            {
                continue;
            }

            try
            {
                var datei = new System.IO.FileInfo(item.Path);
                if (!datei.Exists)
                {
                    continue;
                }

                dateien.Add((folge, datei.Length, datei.LastWriteTimeUtc));
            }
            catch (Exception)
            {
                // Eine Datei, die gerade nicht erreichbar ist, kommt in die Signatur nicht hinein — dann
                // gilt das alte Urteil eben nicht und es wird neu bewertet.
            }
        }

        return Jellyfin.Plugin.SubScribe.Registry.PackSeasonStore.SignatureOf(aired, dateien);
    }

    private HashSet<(string Language, bool HearingImpaired)> LanguagesOf(string imdbId, int season)
    {
        _alleSprachen ??= Registry.LanguagePairsBySeason();
        return _alleSprachen.TryGetValue((imdbId, season), out var paare)
            ? paare
            : new HashSet<(string Language, bool HearingImpaired)>();
    }

    private async Task<Dictionary<(string ImdbId, int Season), IReadOnlyList<int>>> CompleteSeasonsAsync(CancellationToken ct)
    {
        var result = new Dictionary<(string ImdbId, int Season), IReadOnlyList<int>>();

        // F-M599 (Operator: „Ich mein wenn an der packphase was geht, ich würde die gerne immer noch
        // optimieren"): die Pack-Phase wird von innen vermessen, damit die Optimierung an der großen
        // Zahl ansetzt und nicht an der auffälligsten Zeile. Drei Posten: Bibliotheks-Walk,
        // Folgenzahl-Abfragen (IMDb/TMDb) und der Sprachblick in die Dateien. Alles auf `Detail`.
        var paketTakt = System.Diagnostics.Stopwatch.StartNew();
        long walkMs = 0, airedMs = 0, dateiMs = 0;
        int airedCalls = 0, dateiScans = 0, airedAusStore = 0;

        // The same collection the run itself uses (path scope, not library name — F-M189), so the
        // seasons judged here are the seasons this run can actually see.
        List<BaseItem> episodes;
        try
        {
            episodes = CollectItems(_config.SelectedLibraries);
            walkMs = paketTakt.ElapsedMilliseconds;
            LogUtil.Detail(_logger, "[Sub Scribe] PACK — library walk {Count} item(s) in {Ms} ms (F-M599)", episodes.Count, walkMs);
        }
        catch (Exception ex)
        {
            LogUtil.Normal(_logger, "[Sub Scribe] season pack: library query failed ({Msg}) — no season is packed this run.", ex.Message);
            return result;
        }

        var present = new Dictionary<(string ImdbId, int Season), List<int>>();
        var tmdbOf = new Dictionary<(string ImdbId, int Season), string?>();
        // F-M389: the items themselves, one per episode number — a fill-only pack has to reach the
        // files the queue no longer hands it.
        var itemsOf = new Dictionary<(string ImdbId, int Season), List<BaseItem>>();
        foreach (var episode in episodes)
        {
            var (imdb, tmdb, season, number, isSeries) = ResolveIdsRaw(episode);
            if (!isSeries || string.IsNullOrWhiteSpace(imdb) || season < 1 || number < 1)
            {
                continue;
            }

            var key = (imdb!, season);
            if (!present.TryGetValue(key, out var numbers))
            {
                numbers = new List<int>();
                present[key] = numbers;
                itemsOf[key] = new List<BaseItem>();
            }

            if (!numbers.Contains(number))
            {
                numbers.Add(number);
                itemsOf[key].Add(episode);
            }

            if (!tmdbOf.ContainsKey(key) && !string.IsNullOrWhiteSpace(tmdb))
            {
                tmdbOf[key] = tmdb;
            }
        }

        // F-M433: the pack phase runs BEFORE the item loop (F-M429), so the anchors that loop fills are
        // still empty when a pack asks its files. This scan has already walked every episode of every
        // season — hand its items over instead of enumerating the library a second time.
        foreach (var known in itemsOf)
        {
            if (!_seasonEpisodes.ContainsKey(known.Key))
            {
                _seasonEpisodes[known.Key] = known.Value;
            }
        }

        LogUtil.Normal(_logger, "[Sub Scribe] season pack: {Count} season(s) with at least two episodes on disk.", present.Count);
        foreach (var entry in present)
        {
            if (entry.Value.Count < 2)
            {
                continue;
            }

            // F-M573 (Operator-Frage 17.11.2026: „Was ist mit dem exzessiven Staffelpack-Zusammensuchen um
            // dann festzustellen dass schon alles fertig ist?"): die BILLIGSTE Frage zuerst. Sind alle
            // Paare, die der Store fuer diese Staffel kennt, bereits gepackt, ist die Staffel erledigt —
            // dann kostet sie weder eine TMDb-Folgenzahl noch einen Blick in eine Datei noch einen Hash.
            // Vorher lief die teure Kette (TMDb-Fragen + Release-Gruppen + Dateien) fuer JEDE vollstaendige
            // Staffel, nur um am Ende „already up as a pack" zu melden.
            var staffelBekannt = LanguagesOf(entry.Key.Item1, entry.Key.Item2);
            var staffelGepackt = PacksOf(entry.Key.Item1, entry.Key.Item2);
            if (staffelBekannt.Count > 0
                && staffelBekannt.All(p => staffelGepackt.Contains(p)))
            {
                LogUtil.PerItem(_config.LogMode,
                    _logger,
                    "[Sub Scribe] season pack: {Imdb} S{Season} — all {Count} known pair(s) already up as a pack, no TMDb call, no file read.",
                    entry.Key.Item1, entry.Key.Item2, staffelBekannt.Count);
                continue;
            }

            // F-M601 (Operator 18.11.2026: „erst store, wenn der nix hat von tmdb ziehen und dann in
            // den store einspeichern"): die Folgenzahl steht im Store. Gemessen 18.11.2026: 79 Aufrufe
            // = 16.248 ms, gut ein Fuenftel der Pack-Phase — und nach jedem Neustart wieder, weil
            // SeasonEpisodeIndex nur im Arbeitsspeicher haelt.
            IReadOnlyList<int>? aired = null;
            long airedVorher = paketTakt.ElapsedMilliseconds;
            if (AiredSeasonStore.TryGet(entry.Key.Item1, entry.Key.Item2, out var gespeichert)
                && AiredSeasonStore.Covers(gespeichert, entry.Value))
            {
                aired = gespeichert;
                airedAusStore++;
            }
            else
            {
                tmdbOf.TryGetValue(entry.Key, out string? tmdbId);
                aired = await SeasonEpisodeIndex.AiredAsync(entry.Key.Item1, entry.Key.Item2, tmdbId, _config.TmdbApiKey, _logger, ct).ConfigureAwait(false);
                airedMs += paketTakt.ElapsedMilliseconds - airedVorher;
                airedCalls++;
                if (aired is { Count: > 0 })
                {
                    AiredSeasonStore.Put(entry.Key.Item1, entry.Key.Item2, aired);
                }
            }
            if (aired == null)
            {
                LogUtil.PerItem(_config.LogMode,
                    _logger,
                    "[Sub Scribe] season pack skipped: {Imdb} S{Season} — no episode count (IMDb and TMDB both silent), stays single.",
                    entry.Key.Item1, entry.Key.Item2);
                continue;
            }

            // F-M417 (operator order 02.11.2026): the cheap questions come first — the store, not the
            // release names. Is the season already up as a pack for the languages the store knows, and
            // does any of them cover every aired episode? Only then are release groups worth reading,
            // and only then does anything get extracted.
            string saisonSignatur = SeasonSignatureOf(itemsOf[entry.Key], aired);

            // F-M611: die Signatur bleibt fuer den Koordinator liegen — er schreibt damit die Urteile
            // „kein Pack" (Füllversuch gescheitert) und befolgt sie beim naechsten Lauf.
            _saisonSignatur[(entry.Key.Item1, entry.Key.Item2)] = saisonSignatur;

            // F-M604: gilt das gespeicherte Urteil „kein Pack" noch fuer genau diese Mitglieder, ist die
            // Staffel in einem Schritt erledigt — kein Sprachenscan, keine Gruppenwanderung.
            if (Jellyfin.Plugin.SubScribe.Registry.PackSeasonStore.StillImpossible(
                    entry.Key.Item1, entry.Key.Item2, Jellyfin.Plugin.SubScribe.Registry.PackSeasonStore.SeasonTag, saisonSignatur, out string altGrund))
            {
                LogUtil.Detail(
                    _logger,
                    "[Sub Scribe] season pack — {Imdb} S{Season} unchanged since the last judgement ({Reason}), nothing examined (F-M604)",
                    entry.Key.Item1, entry.Key.Item2, altGrund);
                continue;
            }

            var known = LanguagesOf(entry.Key.Item1, entry.Key.Item2);

            // F-M433 (operator order 03.11.2026, "wieso geht Tracker einzeln hoch?"): the store names the
            // languages it has already seen. A season whose texts go up for the FIRST time names none, so
            // the plan refused the pack and the single phase sent 13 episodes one by one. The files are
            // asked as well — a stream list costs no extraction — and the pair is planned from them.
            var pairs = new List<(string Language, bool HearingImpaired)>(known);
            long dateiVorher = paketTakt.ElapsedMilliseconds;
            var dateiSprachen = FileLanguages(entry.Key.Item1, entry.Key.Item2).ToList();
            dateiMs += paketTakt.ElapsedMilliseconds - dateiVorher;
            dateiScans++;
            foreach (var pair in dateiSprachen)
            {
                if (!pairs.Contains(pair))
                {
                    pairs.Add(pair);
                }
            }

            // F-M571 (Operator: „Already up as a pack ist doch etwas das kann man mit dem ersten file
            // prüfen und den rest dann überspringen"): EIN Blick auf die Staffel statt einer Abfrage je
            // Paar, und EINE Zeile statt einer je Paar — gemessen an „The Thundermans" 67 Abfragen und
            // 67 Zeilen in einer Minute, alle mit derselben Antwort.
            var gepackt = PacksOf(entry.Key.Item1, entry.Key.Item2);
            var open = new List<(string Language, bool HearingImpaired)>();
            int schonGepackt = 0;
            foreach (var (language, hi) in pairs)
            {
                if (gepackt.Contains((
                        Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical(language)
                            ?? (language ?? string.Empty).ToUpperInvariant(),
                        hi)))
                {
                    schonGepackt++;
                    continue;
                }

                // F-M433: the store's count decides nothing on its own — the files are the second witness.
                var mitSprache = Registry.EpisodesWithLanguage(entry.Key.Item1, entry.Key.Item2, language, hi);

                // F-M604: was der STORE an Spuren kennt, gehoert in die Signatur — sonst wuerde eine neu
                // eingetroffene Spur eines fehlenden Paares nie bemerkt (die Dateien aendern sich dabei nicht).
                string paarSignatur = saisonSignatur + "|store=" + mitSprache.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string paarTag = SeasonPack.LangTag(language, hi);
                if (Jellyfin.Plugin.SubScribe.Registry.PackSeasonStore.StillImpossible(entry.Key.Item1, entry.Key.Item2, paarTag, paarSignatur, out string paarGrund))
                {
                    LogUtil.Detail(
                        _logger,
                        "[Sub Scribe] season pack — {Imdb} S{Season} [{Lang}] unchanged since the last judgement ({Reason}), nothing examined (F-M604)",
                        entry.Key.Item1, entry.Key.Item2, paarTag, paarGrund);
                    continue;
                }

                bool whole = SeasonPack.IsComplete(aired, mitSprache)
                    || SeasonCarriesInFiles(entry.Key.Item1, entry.Key.Item2, aired, language, hi);
                if (whole)
                {
                    open.Add((language, hi));
                }
                else
                {
                    Jellyfin.Plugin.SubScribe.Registry.PackSeasonStore.Put(
                        entry.Key.Item1, entry.Key.Item2, paarTag, paarSignatur, Jellyfin.Plugin.SubScribe.Registry.PackSeasonStore.NoPack,
                        "neither the store nor the files of every aired episode carry this language");
                    LogUtil.PerItem(_config.LogMode,
                        _logger,
                        "[Sub Scribe] season pack skipped — {Imdb} S{Season} [{Lang}]: neither the store nor the files of every aired episode carry this language — no pack for it, the single uploads of this run are unaffected.",
                        entry.Key.Item1, entry.Key.Item2, paarTag);
                }
            }

            if (schonGepackt > 0)
            {
                LogUtil.PerItem(_config.LogMode,
                    _logger,
                    "[Sub Scribe] season pack: {Imdb} S{Season} — {Packed} of {Pairs} pair(s) already up as a pack, {Open} examined, nothing is extracted for the packed ones.",
                    entry.Key.Item1, entry.Key.Item2, schonGepackt, pairs.Count, open.Count);
            }

            if (pairs.Count > 0 && open.Count == 0)
            {
                continue;
            }

            // F-M408 (operator order 02.11.2026): "die staffel ist vollstaendig wenn alle episoden der
            // gleichen release group da sind". Ein Pack traegt eine Release, eine Qualitaet, eine
            // Bildrate — eine Staffel aus zwei Gruppen ist kein Pack. Es gewinnt die Gruppe, die alle
            // ausgestrahlten Folgen stellt; nur ihre Dateien duerfen mit.
            var groupOf = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in itemsOf[entry.Key])
            {
                var (_, _, _, groupNumber, groupIsSeries) = ResolveIdsRaw(item);
                if (!groupIsSeries || groupNumber < 1)
                {
                    continue;
                }

                string group = SeasonPack.ReleaseGroup(item.Path) ?? SeasonPack.UnknownGroup;
                if (!groupOf.TryGetValue(group, out var groupNumbers))
                {
                    groupNumbers = new List<int>();
                    groupOf[group] = groupNumbers;
                }

                if (!groupNumbers.Contains(groupNumber))
                {
                    groupNumbers.Add(groupNumber);
                }
            }

            // F-M469: the group is binding in BOTH cases. "Every episode is on disk" was enough to hand
            // the whole folder to the pack — and a season of three encodes became one zip of three
            // releases, although the rule says a pack carries one release.
            string? completeGroup = SeasonPack.GroupCoveringSeason(
                aired, groupOf.Select(g => (g.Key, (IEnumerable<int>)g.Value)));

            if (SeasonPack.IsComplete(aired, entry.Value) && completeGroup != null)
            {
                result[entry.Key] = aired;
                _completeSeasonItems[entry.Key] = itemsOf[entry.Key]
                    .Where(x => string.Equals(
                        SeasonPack.ReleaseGroup(x.Path) ?? SeasonPack.UnknownGroup, completeGroup,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                LogUtil.PerItem(_config.LogMode,
                    _logger,
                    "[Sub Scribe] season pack candidate: {Imdb} S{Season} — {Count} of {Aired} episode(s) on disk, season complete, release group {Group}.",
                    entry.Key.Item1, entry.Key.Item2, entry.Value.Count, aired.Count, completeGroup);
            }
            else if (SeasonPack.IsComplete(aired, entry.Value))
            {
                // F-M469: complete on disk, but not one release — no pack. The single uploads of the run
                // are unaffected; this is the pack's own refusal.
                Jellyfin.Plugin.SubScribe.Registry.PackSeasonStore.Put(
                    entry.Key.Item1, entry.Key.Item2, Jellyfin.Plugin.SubScribe.Registry.PackSeasonStore.SeasonTag, saisonSignatur,
                    Jellyfin.Plugin.SubScribe.Registry.PackSeasonStore.NoPack,
                    "all aired episodes are on disk but split over " + groupOf.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " release(s)");
                LogUtil.PerItem(_config.LogMode,
                    _logger,
                    "[Sub Scribe] season pack skipped: {Imdb} S{Season} — all {Aired} episode(s) are on disk but split over {Groups} release(s) ({List}), a pack carries one release, stays single.",
                    entry.Key.Item1, entry.Key.Item2, aired.Count, groupOf.Count,
                    string.Join(", ", groupOf.Select(g => $"{g.Key}: {g.Value.Count}")));
            }
            else if (completeGroup != null)
            {
                result[entry.Key] = aired;
                _completeSeasonItems[entry.Key] = itemsOf[entry.Key]
                    .Where(x => string.Equals(
                        SeasonPack.ReleaseGroup(x.Path) ?? SeasonPack.UnknownGroup, completeGroup,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                LogUtil.PerItem(_config.LogMode,
                    _logger,
                    "[Sub Scribe] season pack candidate: {Imdb} S{Season} — release group {Group} supplies all {Aired} episode(s), season complete.",
                    entry.Key.Item1, entry.Key.Item2, completeGroup, aired.Count);
            }
            else
            {
                Jellyfin.Plugin.SubScribe.Registry.PackSeasonStore.Put(
                    entry.Key.Item1, entry.Key.Item2, Jellyfin.Plugin.SubScribe.Registry.PackSeasonStore.SeasonTag, saisonSignatur,
                    Jellyfin.Plugin.SubScribe.Registry.PackSeasonStore.NoPack, "no release group covers the season");
                LogUtil.PerItem(_config.LogMode,
                    _logger,
                    "[Sub Scribe] season pack skipped: {Imdb} S{Season} — {Have} episode(s) on disk against {Aired} aired, no release group covers the season, stays single.",
                    entry.Key.Item1, entry.Key.Item2, entry.Value.Count, aired.Count);
                    }
                    }

                    // F-M599: die Zahlen der Pack-Planung in einer Zeile — Season-Anzahl, Folgenzahl-Abfragen und
                    // der Sprachblick in die Dateien. Ohne diese Trennung ist „die Pack-Phase dauert 70 s" nur eine
                    // Summe, mit der niemand etwas optimieren kann.
                    LogUtil.Detail(
                    _logger,
                    "[Sub Scribe] PACK — planning done: {Count} season(s), aired lookups {Calls} call(s) {Aired} ms ({FromStore} from the store), file language scans {Scans} {Datei} ms, total {Gesamt} ms (F-M601)",
                    result.Count, airedCalls, airedMs, airedAusStore, dateiScans, dateiMs, paketTakt.ElapsedMilliseconds);

                    return result;
                    }

    /// <summary>
    /// F-M588: holt die SubDL-Ids der in diesem Lauf hochgeladenen Einzel EINMAL — statt nach jedem
    /// Upload die vollstaendige eigene Liste zu ziehen. Zugeordnet wird je Schluessel (IMDb, Staffel,
    /// Folge, Sprache) in der Reihenfolge der Uploads gegen die Eintraege nach Erstellzeit; jeder
    /// Eintrag wird genau einmal verbraucht, damit HI/Non-HI-Paare derselben Folge nicht vertauscht
    /// werden. Was SubDL schon freigegeben hat, steht nicht mehr in der Einreichungsliste — dafuer
    /// wird die veroeffentlichte Liste EINMAL nachgezogen (F-M432).
    /// </summary>
    /// <param name="ct">Cancellation token of the run.</param>
    private async Task ResolveDeferredUploadIdsAsync(CancellationToken ct)
    {
        if (_offeneSubdlIds.Count == 0)
        {
            return;
        }

        int offen = _offeneSubdlIds.Count;
        var benutzt = new HashSet<int>();
        var erledigt = new HashSet<PendingSubdlId>();
        int getroffen = 0;

        List<OwnSubtitleEntry>? einreichungen = null;
        try
        {
            einreichungen = await _api.ListMySubtitlesAsync(ct, maxPages: 1).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Sub Scribe] deferred upload ids: mySubtitles unreadable.");
        }

        getroffen += Zuteilen(einreichungen, "mySubtitles");

        if (getroffen < offen)
        {
            List<OwnSubtitleEntry>? veroeffentlicht = null;
            try
            {
                veroeffentlicht = await _api.ListMyPublishedSubtitlesAsync(ct, maxPages: 2).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Sub Scribe] deferred upload ids: published list unreadable.");
            }

            getroffen += Zuteilen(veroeffentlicht, "myPublishedSubtitles");
        }

        LogUtil.Normal(
            _logger,
            "[Sub Scribe] deferred upload ids: {Hit} of {Open} assigned with one list fetch (F-M588).",
            getroffen, offen);

        if (getroffen < offen)
        {
            _logger.LogWarning(
                "[Sub Scribe] {Left} upload(s) of this run still have no SubDL id — their singles stay beside a pack that replaces them, because there is no handle to delete them (F-M421/F-M588).",
                offen - getroffen);
        }

        int Zuteilen(List<OwnSubtitleEntry>? liste, string quelle)
        {
            if (liste == null || liste.Count == 0)
            {
                return 0;
            }

            int n = 0;
            foreach (var gruppe in _offeneSubdlIds
                         .Where(x => !erledigt.Contains(x))
                         .OrderBy(x => x.At)
                         .GroupBy(x => (x.ImdbId, x.Season, x.Episode, x.Lang)))
            {
                var kandidaten = liste
                    .Where(e => !benutzt.Contains(e.HandleId) && SameLanguage(e.Language, gruppe.Key.Lang))
                    .Where(e => gruppe.Key.ImdbId is null
                                || string.Equals(e.ImdbId, gruppe.Key.ImdbId, StringComparison.OrdinalIgnoreCase))
                    .Where(e => gruppe.Key.Season <= 0 || e.Season == gruppe.Key.Season)
                    .Where(e => gruppe.Key.Episode <= 0 || e.Episode == gruppe.Key.Episode)
                    .OrderBy(e => e.CreatedAt)
                    .ToList();
                if (kandidaten.Count == 0)
                {
                    continue;
                }

                foreach (var p in gruppe.OrderBy(x => x.At))
                {
                    // Erst der Release-Name, dann die Zeit: derselbe Griff wie in ResolveOwnUploadIdAsync,
                    // nur ohne einen Netzaufruf je Spur.
                    var treffer = kandidaten.FirstOrDefault(e => !benutzt.Contains(e.HandleId)
                            && (MatchesRelease(e.Name, p.Release) || MatchesRelease(e.Name, p.FileName)))
                        ?? kandidaten.FirstOrDefault(e => !benutzt.Contains(e.HandleId));
                    if (treffer == null)
                    {
                        break;
                    }

                    benutzt.Add(treffer.HandleId);
                    erledigt.Add(p);
                    string id = treffer.HandleId.ToString(System.Globalization.CultureInfo.InvariantCulture);

                    // F-M413/F-M435: dieselbe Auskunft wie frueher je Upload — nur jetzt fuer alle
                    // Uploads zusammen, deshalb auf der PerItem-Ebene statt auf der Lauf-Ebene.
                    LogUtil.PerItem(_config.LogMode, _logger,
                        quelle == "myPublishedSubtitles"
                            ? "[Sub Scribe] entry id {Id} taken from myPublishedSubtitles for {File} [{Lang}] (F-M432/F-M435/F-M588)."
                            : "[Sub Scribe] upload id {Id} taken from mySubtitles for {File} [{Lang}] (F-M588, assigned at the end of the run).",
                        id, p.FileName ?? p.MediaHash ?? "?", p.Lang);
                    Registry.MarkAndFlush(() => Registry.SetDeferredSubdlId(
                        p.MediaHash, p.SubPos, p.Loose, p.FileName, p.Lang, p.Hi, id));
                    n++;
                }
            }

            return n;
        }
    }

    /// <summary>
    /// The coordinator's view of one candidate — used wherever a pack may take it, so the single
    /// path and the dry run hand over exactly the same thing.
    /// </summary>
    /// <param name="item">Media item.</param>
    /// <param name="mediaHash">Media hash.</param>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="tmdbId">TMDB id as it was read.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="lang">SubDL language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant.</param>
    /// <param name="forced">Forced flag.</param>
    /// <param name="subPos">Stream position of the source track.</param>
    /// <param name="srt">Canonical subtitle text.</param>
    /// <param name="mediaPath">Path of the media file.</param>
    /// <param name="framerate">Frame rate code of the video.</param>
    /// <param name="videoHeight">Video height in pixels, when the stream reports one.</param>
    /// <param name="videoCodec">Video codec name from the stream.</param>
    /// <returns>The candidate as the coordinator expects it.</returns>

    /// <summary>F-M414: does the entry's release name carry ours, extension and case aside?</summary>
    private static bool MatchesRelease(string? entryName, string? ours)
    {
        if (string.IsNullOrWhiteSpace(entryName) || string.IsNullOrWhiteSpace(ours))
        {
            return false;
        }

        string a = Path.GetFileNameWithoutExtension(entryName.Trim());
        string b = Path.GetFileNameWithoutExtension(ours.Trim());
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
               || a.Contains(b, StringComparison.OrdinalIgnoreCase)
               || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>F-M414: "Turkish" and "TR" are the same language; both spellings come over the wire.</summary>
    private static bool SameLanguage(string? entryLanguage, string? ours)
    {
        if (string.IsNullOrWhiteSpace(entryLanguage) || string.IsNullOrWhiteSpace(ours))
        {
            return true;
        }

        if (string.Equals(entryLanguage, ours, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? mapped = LanguageMapper.MapToSubdlName(entryLanguage);
        return string.Equals(mapped, ours, StringComparison.OrdinalIgnoreCase)
               || entryLanguage.StartsWith(ours, StringComparison.OrdinalIgnoreCase);
    }

    private static SeasonPackCoordinator.Candidate PackCandidate(
        BaseItem item, string mediaHash, string imdbId, string? tmdbId, int season, int episode,
        string lang, bool hearingImpaired, bool forced, int subPos, string srt,
        string mediaPath, string framerate, int? videoWidth, int? videoHeight, string? videoCodec,
        string? absorbedSingleId = null)
    => new(
            item,
            item.Id.ToString(),
            mediaHash,
            imdbId,
            tmdbId,
            season,
            episode,
            lang,
            hearingImpaired,
            forced,
            subPos,
            srt,
            Path.GetFileNameWithoutExtension(mediaPath),
            SourceQuality.Detect(null, mediaPath),
            framerate,
            videoWidth,
            videoHeight,
            videoCodec);

    /// <summary>
    /// F-M389 (operator order 02.11.2026): one missing episode of a pack, taken from the subtitle
    /// that is already uploaded for it.
    /// <para>
    /// The order is the guarantee. The episode is resolved from the library, the text is extracted
    /// from its own file, canonicalised, and then put through the SAME gates as a normal upload; only
    /// a text that survives them AND is known to the registry — the proof that it went up before —
    /// becomes a candidate. Everything else returns null, which makes the coordinator send nothing
    /// rather than a pack with a hole in it.
    /// </para>
    /// <para>
    /// It is deliberately not <see cref="ScreenContentQa"/>: that method's duplicate verdict is
    /// exactly what this one has to confirm, so it calls the gate half
    /// (<see cref="GateReject"/>) and asks the registry itself.
    /// </para>
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number the buffer does not cover.</param>
    /// <param name="lang">SubDL language code the pack is built for.</param>
    /// <param name="hearingImpaired">HI/SDH variant the pack is built for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The candidate, or null when the episode cannot join the pack.</returns>
    /// <summary>
    /// F-M433: do the files of a season carry this language in every aired episode? Cheap — stream lists,
    /// no extraction. Used where a pack would otherwise be refused on the store's word alone.
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="aired">Aired episodes.</param>
    /// <param name="lang">SubDL language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant.</param>
    /// <returns>True when every aired episode's file carries the language.</returns>
    private bool SeasonCarriesInFiles(
        string imdbId, int season, IEnumerable<int> aired, string lang, bool hearingImpaired)
    {
        var carried = new HashSet<int>();
        foreach (int episode in SeasonPack.AiredNumbers(aired))
        {
            if (FileCarriesLanguage(imdbId, season, episode, lang, hearingImpaired))
            {
                carried.Add(episode);
            }
        }

        return SeasonPack.IsComplete(aired, carried);
    }

    /// <inheritdoc />
    public bool FileCarriesLanguage(string imdbId, int season, int episode, string lang, bool hearingImpaired)
    {
        // F-M433: matched through ResolveIdsRaw, not through episode.Series — the series object is often
        // not loaded when the pack phase asks (it runs before the item loop), and IsSeasonEpisode's
        // reliance on it made every file answer "no" for a language the files plainly carry.
        BaseItem? item = SeasonItems(imdbId, season).FirstOrDefault(candidate =>
        {
            var (candidateImdb, _, candidateSeason, candidateNumber, isSeries) = ResolveIdsRaw(candidate);
            return isSeries
                   && candidateNumber == episode
                   && candidateSeason == season
                   && string.Equals(candidateImdb, imdbId, StringComparison.OrdinalIgnoreCase);
        });

        return item?.Path != null && FindFillStreams(item, lang, hearingImpaired).Count > 0;
    }

    /// <summary>
    /// F-M600: die billige Vorabfrage fuer die Pack-Phase. Zwei Gruende, die der Store schon kennt:
    /// die Datei hat keine Textspur dieser Sprache (dann kann die Folge nie beitreten), oder jede
    /// Spur dieser Sprache ist im Store bereits gesperrt (dann wird sie nicht mehr entpackt, F-M443).
    /// Beides kostet eine Spurenliste und ein paar Store-Blicke — keine Extraktion.
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="lang">SubDL language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant.</param>
    /// <param name="reason">Why the episode cannot join.</param>
    /// <returns>True when the episode is provably unable to join.</returns>
    public bool EpisodeCannotJoin(string imdbId, int season, int episode, string lang, bool hearingImpaired, out string reason)
    {
        reason = string.Empty;

        BaseItem? item = SeasonItems(imdbId, season).FirstOrDefault(candidate =>
        {
            var (candidateImdb, _, candidateSeason, candidateNumber, isSeries) = ResolveIdsRaw(candidate);
            return isSeries
                   && candidateNumber == episode
                   && candidateSeason == season
                   && string.Equals(candidateImdb, imdbId, StringComparison.OrdinalIgnoreCase);
        });

        if (item?.Path == null)
        {
            reason = "the episode is not in the library";
            return true;
        }

        var positions = FindFillStreams(item, lang, hearingImpaired);
        if (positions.Count == 0)
        {
            reason = "no embedded text track for this language and variant";
            return true;
        }

        string? mediaHash = MediaHashFor(item.Path);
        if (string.IsNullOrWhiteSpace(mediaHash))
        {
            // Ohne Hash kein Urteil aus dem Store — dann entscheidet wie bisher die Extraktion.
            return false;
        }

        var gesperrt = positions
            .Where(position => SpurIstGesperrt(mediaHash, position, lang, hearingImpaired, isSeries: true))
            .ToList();
        if (gesperrt.Count == positions.Count)
        {
            reason = $"every track of this language is already refused in the store ({gesperrt.Count})";
            return true;
        }

        return false;
    }

    /// <summary>
    /// F-M389 (operator order 02.11.2026): one missing episode of a pack, taken from the subtitle
    /// that is already uploaded for it.
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number the buffer does not cover.</param>
    /// <param name="lang">SubDL language code the pack is built for.</param>
    /// <param name="hearingImpaired">HI/SDH variant the pack is built for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The candidate, or null when the episode cannot join the pack.</returns>
    public async Task<SeasonPackCoordinator.Candidate?> FillAsync(
        string imdbId, int season, int episode, string lang, bool hearingImpaired, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // F-M444 (operator 04.11.2026, "Verbesserungsvorschläge 1 bis 4"): an episode can sit in the
        // library more than once — a second release of the same episode. Until now FirstOrDefault
        // decided, so a file that failed a gate killed the pack although another file of the SAME
        // episode carried the language too. Every file is tried now, in library order.
        var files = FindSeasonEpisodeItems(imdbId, season, episode)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Path))
            .ToList();
        if (files.Count == 0)
        {
            LogUtil.Normal(
                _logger,
                "[Sub Scribe] season pack filler: {Imdb} S{Season}E{Episode} [{Lang}] — the episode is not in the library, it does not join the pack.",
                imdbId, season, episode, SeasonPack.LangTag(lang, hearingImpaired));
            return null;
        }

        for (int index = 0; index < files.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var (member, why) = await FillFromFileAsync(
                files[index], imdbId, season, episode, lang, hearingImpaired, ct).ConfigureAwait(false);
            if (member != null)
            {
                if (index > 0)
                {
                    LogUtil.Normal(
                        _logger,
                        "[Sub Scribe] season pack filler: {Imdb} S{Season}E{Episode} [{Lang}] — file {Nth} of {Count} joins the pack, the file(s) before it did not (F-M444).",
                        imdbId, season, episode, SeasonPack.LangTag(lang, hearingImpaired), index + 1, files.Count);
                }

                return member;
            }

            if (index < files.Count - 1)
            {
                LogUtil.Normal(
                    _logger,
                    "[Sub Scribe] season pack filler: {Imdb} S{Season}E{Episode} [{Lang}] — file {Nth} of {Count} does not qualify ({Reason}), trying the next file of this episode (F-M444).",
                    imdbId, season, episode, SeasonPack.LangTag(lang, hearingImpaired), index + 1, files.Count, why);
            }
        }

        return null;
    }

    /// <summary>
    /// F-M444: one file of an episode, put through the whole filler. Split out of
    /// <see cref="FillAsync"/> so that a second file of the same episode gets a full attempt of its own.
    /// </summary>
    /// <param name="item">Episode item (one file).</param>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="lang">SubDL language code.</param>
    /// <param name="hearingImpaired">HI variant.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The member (or null) and the reason the file was refused.</returns>
    private async Task<(SeasonPackCoordinator.Candidate? Member, string Reason)> FillFromFileAsync(
        BaseItem item, string imdbId, int season, int episode, string lang, bool hearingImpaired, CancellationToken ct)
    {
        string mediaPath = item.Path!;

        // F-M431 (operator order 03.11.2026: "wenn eine sprache durchfällt und eine weitere da ist, diese
        // auch versuchen zu verwenden"): a file may carry more than one track of the same language. Until
        // now only the FIRST one was tried — if it failed (extraction, a quality gate, a lying language
        // tag, text that is only known from a download), the whole pack died although a second track of
        // that language sat in the same file. Every track of the language is tried now, in file order,
        // and the first that passes every gate is the member.
        var fillPositions = FindFillStreams(item, lang, hearingImpaired);
        if (fillPositions.Count == 0)
        {
            LogUtil.Normal(
                _logger,
                "[Sub Scribe] season pack filler: {Imdb} S{Season}E{Episode} [{Lang}] — no embedded text track for this language and variant, it does not join the pack.",
                imdbId, season, episode, SeasonPack.LangTag(lang, hearingImpaired));
            return (null, "no embedded text track for this language and variant");
        }

        string? reason = null;

        // F-M443 (operator 04.11.2026, "Verbesserungsvorschläge"): a track whose verdict is already in
        // the store is not extracted again. The single path has asked EmbedRejectedReason before doing
        // any work since F-M68; the fill did not, so every run pulled a stream it had already refused
        // out of the container once more.
        string? fillMediaHash = MediaHashFor(mediaPath);
        if (!string.IsNullOrWhiteSpace(fillMediaHash))
        {
            // F-M584: „abgelehnt" heisst nicht „fuer alle erledigt" — ein Selbst-Echo sperrt nur die Quelle,
            // die das Paar hat. Verlangt eine andere Quelle es noch, wird die Spur entpackt und der Pack
            // kann gebaut werden.
            var alreadyRefused = fillPositions
                .Where(position => SpurIstGesperrt(fillMediaHash, position, lang, hearingImpaired, isSeries: true))
                .ToList();
            if (alreadyRefused.Count > 0)
            {
                fillPositions = fillPositions.Where(position => !alreadyRefused.Contains(position)).ToList();
                LogUtil.Normal(
                    _logger,
                    "[Sub Scribe] season pack filler: {Imdb} S{Season}E{Episode} [{Lang}] — {Count} track(s) are already refused in the store, not extracted again (F-M443).",
                    imdbId, season, episode, SeasonPack.LangTag(lang, hearingImpaired), alreadyRefused.Count);
            }

            if (fillPositions.Count == 0)
            {
                LogUtil.Normal(
                    _logger,
                    "[Sub Scribe] season pack filler: {Imdb} S{Season}E{Episode} [{Lang}] — every track of this language is already refused in the store, nothing is extracted (F-M443).",
                    imdbId, season, episode, SeasonPack.LangTag(lang, hearingImpaired));
                return (null, "every track of this language is already refused in the store");
            }
        }

        // F-M441 (operator 04.11.2026, "kein dauerhafter store"): the member's text comes out of the
        // file, and it is written to the RUN's working directory — not held in memory for the whole
        // run and not kept past it. Reading every subtitle track of every touched file into one
        // dictionary is what made a run grow into the hundreds of megabytes (prod 04.11.2026: 689
        // files, ~21.000 texts, ~900 MB); a second pack that asks for the same file reads its text
        // back off the disk instead of calling ffmpeg again.
        var cachedTexts = new Dictionary<int, string?>();
        var missing = new List<int>();
        foreach (int position in fillPositions)
        {
            if (_workFiles.TryRead(imdbId, season, episode, lang, hearingImpaired, position, out string? onDisk)
                && !string.IsNullOrWhiteSpace(onDisk))
            {
                cachedTexts[position] = onDisk;
            }
            else
            {
                missing.Add(position);
            }
        }

        if (missing.Count > 0)
        {
            var (extracted, _) = await FfmpegTools
                .ExtractAllAsync(_ffmpegPath, mediaPath, missing, _logger, _config, ct)
                .ConfigureAwait(false);
            foreach (var pair in extracted)
            {
                cachedTexts[pair.Key] = pair.Value;
                if (!string.IsNullOrWhiteSpace(pair.Value))
                {
                    _workFiles.Write(imdbId, season, episode, lang, hearingImpaired, pair.Key, pair.Value!);
                }
            }
        }

        for (int attempt = 0; attempt < fillPositions.Count; attempt++)
        {
            int subPos = fillPositions[attempt];
            var member = FillFromPosition(
                item, mediaPath, cachedTexts, subPos, imdbId, season, episode, lang, hearingImpaired, out reason);
            if (member != null)
            {
                if (attempt > 0)
                {
                    LogUtil.Normal(
                        _logger,
                        "[Sub Scribe] season pack filler: {Imdb} S{Season}E{Episode} [{Lang}] — track {Nth} of {Count} joins the pack, the tracks before it did not (F-M431).",
                        imdbId, season, episode, SeasonPack.LangTag(lang, hearingImpaired), attempt + 1, fillPositions.Count);
                }

                return (member, string.Empty);
            }

            if (attempt < fillPositions.Count - 1)
            {
                LogUtil.Normal(
                    _logger,
                    "[Sub Scribe] season pack filler: {Imdb} S{Season}E{Episode} [{Lang}] — track {Nth} of {Count} does not qualify ({Reason}), trying the next track of this language (F-M431).",
                    imdbId, season, episode, SeasonPack.LangTag(lang, hearingImpaired), attempt + 1, fillPositions.Count, reason);
            }
        }

        LogUtil.Normal(
            _logger,
            "[Sub Scribe] season pack filler: {Imdb} S{Season}E{Episode} [{Lang}] — none of the {Count} track(s) of this language qualifies ({Reason}), it does not join the pack.",
            imdbId, season, episode, SeasonPack.LangTag(lang, hearingImpaired), fillPositions.Count, reason);
        return (null, reason);
    }

    /// <summary>
    /// F-M431: one attempt — the text of a single track, put through every gate the pack demands.
    /// </summary>
    /// <param name="item">Episode item.</param>
    /// <param name="mediaPath">Path of the media file.</param>
    /// <param name="cachedTexts">Extracted texts of the file, by stream position.</param>
    /// <param name="subPos">Stream position of this attempt.</param>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="lang">SubDL language code.</param>
    /// <param name="hearingImpaired">HI variant.</param>
    /// <param name="reason">Why this track was refused, for the run log.</param>
    /// <returns>The member, or null with <paramref name="reason"/> set.</returns>
    private SeasonPackCoordinator.Candidate? FillFromPosition(
        BaseItem item, string mediaPath, Dictionary<int, string?> cachedTexts, int subPos,
        string imdbId, int season, int episode, string lang, bool hearingImpaired, out string reason)
    {
        reason = string.Empty;
        cachedTexts.TryGetValue(subPos, out string? extracted);
        if (string.IsNullOrWhiteSpace(extracted))
        {
            reason = "extraction failed";
            return null;
        }

        // F-M185: the same canonical form the single path uploads and hashes — a filler that skipped
        // this would hash its own bytes and prove nothing about the one that went up.
        string srt = ContentHashRegistry.NormalizeSrt(extracted);

        long runtimeMs = (item.RunTimeTicks ?? 0) / 10_000;
        string? gate = GateReject(srt, lang, runtimeMs, out string? correctedLang);
        if (gate != null)
        {
            reason = "quality gate: " + gate;
            return null;
        }

        // F-M375 on the fill path: the single upload corrects a lying tag and sends the text under the
        // detected language. A pack is ONE language, so here a correction is a refusal — the episode
        // would have to go up under a language this container cannot claim.
        if (correctedLang != null && !string.Equals(correctedLang, lang, StringComparison.OrdinalIgnoreCase))
        {
            reason = "the text speaks " + correctedLang;
            return null;
        }

        // F-M395 (operator order 02.11.2026): filler must be text this account UPLOADED — a subtitle
        // that is only known because it was DOWNLOADED must never be packed back up. Packing a
        // download would put onto SubDL what is already there, under our name, as a new container.
        // So the proof is the row of THIS track: uploaded, not merely "content known" (which counts a
        // download and a reject just the same).
        string contentHash = ContentHashRegistry.ComputeHash(srt);
        // F-M410 (operator order 02.11.2026): one hash look per file. The proof and the candidate
        // both asked MediaHashFor and each call wrote its own oshash line — two lines per file for
        // one answer.
        string? proofMediaHash = MediaHashFor(mediaPath);
        var uploadedRow = string.IsNullOrWhiteSpace(proofMediaHash)
            ? null
            : Registry.GetEmbeds(proofMediaHash).FirstOrDefault(r => r.SubPos == subPos && r.Status == SubtitleStatus.Uploaded);

        // F-M429 (operator order 03.11.2026: "Erst packbildung dann Einzeluploads"): the pack runs BEFORE
        // the single uploads, so for a new episode it is the FIRST publication of this text. The old proof
        // ("the row must say uploaded") would forbid exactly that and no arrival could ever pack.
        // The protection F-M395 exists for stays: text that is only known from a DOWNLOAD or a rejection
        // never joins a pack — it would be pushed onto SubDL under our name as a new container.
        if (uploadedRow == null)
        {
            if (Registry.IsContentKnown(contentHash))
            {
                reason = "the text is only known from a download or a rejection";
                return null;
            }

            LogUtil.Normal(
                _logger,
                "[Sub Scribe] season pack filler: {Imdb} S{Season}E{Episode} [{Lang}] — first publication: the pack carries this text up, no single went up before it (F-M429).",
                imdbId, season, episode, SeasonPack.LangTag(lang, hearingImpaired));
        }

        string? mediaHash = proofMediaHash;
        if (string.IsNullOrWhiteSpace(mediaHash))
        {
            reason = "no media hash for the file";
            return null;
        }

        var (width, height, codec) = GetVideoFacts(item);
        // F-M391: the id of the single upload this member replaces travels with the candidate, so the
        // coordinator can delete it BEFORE the pack goes up. Empty means "no single to remove" — the
        // row may be a download, or an upload whose answer carried no id.
        string? absorbed = uploadedRow?.SubdlId;
        return PackCandidate(
            item, mediaHash, imdbId, TmdbOfEpisode(item), season, episode, lang, hearingImpaired,
            forced: false, subPos, srt, mediaPath, GetFramerateCode(item), width, height, codec,
            absorbedSingleId: absorbed);
    }

    /// <summary>
    /// F-M389: the episode item of a season, as the library holds it. The season is entered through
    /// an item this run already worked (its season folder lists the siblings); the run's own
    /// collection is the fallback when no such anchor exists or the folder cannot be read.
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <returns>The episode item, or null.</returns>
    private BaseItem? FindSeasonEpisode(string imdbId, int season, int episode)
        => SeasonItems(imdbId, season).FirstOrDefault(c => IsSeasonEpisode(c, imdbId, season, episode));

    /// <summary>
    /// F-M444: every file of one episode, in library order — an episode may exist twice (a second
    /// release), and the first file is no longer the only one that gets a chance.
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <returns>The items that carry this episode.</returns>
    private List<BaseItem> FindSeasonEpisodeItems(string imdbId, int season, int episode)
        => SeasonItems(imdbId, season)
            .Where(candidate => IsSeasonEpisode(candidate, imdbId, season, episode))
            .ToList();

    /// <summary>
    /// F-M442 (operator 04.11.2026, "Verbesserungsvorschläge"): the source facts of an aired episode,
    /// read WITHOUT extracting anything. Quality comes from the path and the file name, the frame rate
    /// from the stream list — both were checked only after every member had been pulled out of its
    /// container, which cost ten extractions for the mixed-source season measured on 04.11.2026.
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <returns>Quality and frame rate, null where the library cannot answer.</returns>
    public (string? Quality, string? Framerate) SourceFacts(string imdbId, int season, int episode)
    {
        foreach (var item in FindSeasonEpisodeItems(imdbId, season, episode))
        {
            string? path = item.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string loosePath = Path.GetFileNameWithoutExtension(path);
            return (SourceQuality.Detect(loosePath, path), GetFramerateCode(item));
        }

        return (null, null);
    }

    /// <summary>
    /// F-M433: the language pairs the FILES of a season carry, read from the stream lists (no extraction).
    /// <para>
    /// The store can only name languages it has seen; a season that is about to be uploaded for the first
    /// time names none. This is the other witness, and the pack plan is allowed to start from it.
    /// </para>
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <returns>The pairs found in the files of that season.</returns>
    public IReadOnlyCollection<(string Language, bool HearingImpaired)> FileLanguages(string imdbId, int season)
    {
        var pairs = new HashSet<(string Language, bool HearingImpaired)>();
        foreach (var item in SeasonItems(imdbId, season))
        {
            if (item is not Episode episode
                || (episode.ParentIndexNumber ?? -1) != season
                || string.IsNullOrWhiteSpace(episode.Path))
            {
                continue;
            }

            string? seriesImdb = null;
            // F-M433: same reason as FileCarriesLanguage — the ids come from ResolveIdsRaw.
            var (fileImdb, _, fileSeason, fileNumber, fileIsSeries) = ResolveIdsRaw(item);
            if (!fileIsSeries
                || fileNumber < 1
                || fileSeason != season
                || !string.Equals(fileImdb, imdbId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            IReadOnlyList<MediaStream>? streams;
            try
            {
                streams = episode.GetMediaStreams();
            }
            catch (Exception)
            {
                continue;
            }

            if (streams == null)
            {
                continue;
            }

            foreach (var stream in streams)
            {
                if (stream.Type != MediaStreamType.Subtitle
                    || stream.IsExternal
                    || !Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsDialogueStream(stream))
                {
                    continue;
                }

                string? code = LanguageMapper.MapToSubdl(stream.Language);
                if (!string.IsNullOrWhiteSpace(code))
                {
                    pairs.Add((code!, Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsHearingImpairedStream(stream)));
                }
            }
        }

        return pairs;
    }

    /// <summary>
    /// F-M433: the episodes of a season, cached per season folder.
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <returns>The candidate items of that season.</returns>
    private List<BaseItem> SeasonItems(string imdbId, int season)
    {
        var key = (imdbId, season);
        if (!_seasonEpisodes.TryGetValue(key, out var episodes))
        {
            episodes = new List<BaseItem>();
            if (_seasonAnchors.TryGetValue(key, out var anchor) && anchor.GetParent() is Folder seasonFolder)
            {
                try
                {
                    episodes.AddRange(seasonFolder.GetChildren(null, false));
                }
                catch (Exception ex)
                {
                    // A season folder that cannot be read costs the pack, never the run.
                    LogUtil.PerItem(_config.LogMode, _logger,
                        "[Sub Scribe] season pack filler: reading the season folder of {Imdb} S{Season} failed ({Msg}) — falling back to the library.",
                        imdbId, season, ex.Message);
                }
            }

            if (episodes.Count == 0)
            {
                episodes.AddRange(CollectItems(_config.SelectedLibraries));
            }

            _seasonEpisodes[key] = episodes;
        }

        return episodes;
    }

    /// <summary>
    /// F-M389: does this library item stand for the episode the pack needs? Series id, season and
    /// episode number all have to agree — the filler may only ever use the episode it was asked for.
    /// </summary>
    /// <param name="candidate">Library item.</param>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <returns>True when the item is that episode.</returns>
    private static bool IsSeasonEpisode(BaseItem candidate, string imdbId, int season, int episode)
    {
        if (candidate is not Episode ep
            || (ep.IndexNumber ?? -1) != episode
            || (ep.ParentIndexNumber ?? -1) != season)
        {
            return false;
        }

        string? seriesImdb = null;
        ep.Series?.ProviderIds?.TryGetValue("Imdb", out seriesImdb);
        return string.Equals(seriesImdb, imdbId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// F-M389: the ffmpeg position of the embedded track that carries this language and variant.
    /// <para>
    /// The position is taken against the UNFILTERED subtitle list, exactly as the item loop does it —
    /// ffmpeg's <c>0:s:N</c> counts bitmap and forced tracks too, and searching a filtered list maps
    /// onto the wrong stream.
    /// </para>
    /// </summary>
    /// <param name="item">Episode item.</param>
    /// <param name="lang">SubDL language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant.</param>
    /// <returns>The stream position, or -1 when the file carries no such track.</returns>
    private List<int> FindFillStreams(BaseItem item, string lang, bool hearingImpaired)
    {
        var positions = new List<int>();
        List<MediaStream> allSubs;
        try
        {
            // F-M431: enumerated exactly the way the extraction reads the file — every subtitle stream of
            // the item, external ones included — so a position means the same thing in both places.
            // F-M468 (measured 11.11.2026 on VisionQuest tt23112594): asked by ID, not on the object. The
            // pack phase reads a season as folder children, and such an item carries no media sources —
            // its own stream list is empty, so every language answered "the file does not carry it"
            // although the files carry 25. Every other place in this pipeline asks the media source
            // manager by id; the object is only the fallback for a source manager that has nothing.
            allSubs = (_mediaSourceManager.GetMediaStreams(item.Id)
                       ?? item.GetMediaStreams())?
                .Where(s => s.Type == MediaStreamType.Subtitle)
                .ToList() ?? new List<MediaStream>();
        }
        catch (Exception ex)
        {
            LogUtil.PerItem(_config.LogMode, _logger,
                "[Sub Scribe] season pack filler: stream list of {File} unavailable ({Msg}).",
                Path.GetFileName(item.Path ?? item.Name), ex.Message);
            return positions;
        }

        for (int i = 0; i < allSubs.Count; i++)
        {
            var stream = allSubs[i];
            if (stream.IsExternal
                || !Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsDialogueStream(stream)
                || Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsHearingImpairedStream(stream) != hearingImpaired
                || !string.Equals(LanguageMapper.MapToSubdl(stream.Language), lang, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            positions.Add(i);
        }

        return positions;
    }

    /// <summary>
    /// F-M389: the series TMDB id of an episode, read the way the run reads it (F-M9/F-M28) — from the
    /// SERIES, never from the episode.
    /// </summary>
    /// <param name="item">Media item.</param>
    /// <returns>The series TMDB id, or null.</returns>
    private static string? TmdbOfEpisode(BaseItem item)
    {
        string? tmdb = null;
        (item as Episode)?.Series?.ProviderIds?.TryGetValue("Tmdb", out tmdb);
        return string.IsNullOrWhiteSpace(tmdb) ? null : tmdb;
    }

    /// <summary>
    /// F-M389: which episodes of which season the queue hands this run — the input both pack plans
    /// read. A queued item is one this run will work (the queue filter of F-M111 applies).
    /// </summary>
    /// <param name="items">The run's items.</param>
    /// <returns>Episode numbers per (series, season).</returns>
    /// <summary>
    /// F-M426 (operator rule 02.11.2026: "It is only investigated if the sub is not part of a pack already"):
    /// answers whether a season still has a language that is NOT inside a pack.
    /// <para>
    /// Asked BEFORE the queued-versus-aired comparison, so a season that is fully packed is not
    /// investigated at all: no hash look beyond this one, no extraction, no plan line. A season whose
    /// episodes carry no uploaded row yet counts as open — there is nothing packed about it either.
    /// </para>
    /// </summary>
    /// <param name="key">Series and season.</param>
    /// <param name="episodes">The episode numbers this run queued for that season.</param>
    /// <returns>True when at least one language still lacks a pack.</returns>
    private bool HasOpenLanguage((string ImdbId, int Season) key, IReadOnlyCollection<int> episodes)
    {
        if (!_completeSeasonItems.TryGetValue(key, out var items) || items.Count == 0)
        {
            return true;
        }

        bool sawRow = false;
        foreach (var item in items)
        {
            int number = item.IndexNumber ?? 0;
            if (number <= 0 || !episodes.Contains(number))
            {
                continue;
            }

            string? path = item.Path;
            string? mediaHash;
            try
            {
                mediaHash = string.IsNullOrWhiteSpace(path) ? null : MediaHashFor(path);
            }
            catch (Exception)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(mediaHash))
            {
                continue;
            }

            foreach (var row in Registry.GetEmbeds(mediaHash))
            {
                if (row.Status != SubtitleStatus.Uploaded || row.Forced == true)
                {
                    continue;
                }

                sawRow = true;
                if (!Registry.HasPack(key.ImdbId, key.Season, row.Language, row.HearingImpaired ?? false))
                {
                    return true;
                }
            }
        }

        return !sawRow;
    }

    private Dictionary<(string ImdbId, int Season), List<int>> QueuedEpisodes(IReadOnlyList<BaseItem> items)
    {
        var queued = new Dictionary<(string ImdbId, int Season), List<int>>();
        foreach (var item in items)
        {
            if (QueueFilter != null && !QueueFilter.Contains(item.Id.ToString()))
            {
                continue;
            }

            var (imdb, _, season, episode, isSeries) = ResolveIdsRaw(item);
            if (!isSeries || string.IsNullOrWhiteSpace(imdb) || season < 1 || episode < 1)
            {
                continue;
            }

            var key = (imdb!, season);
            if (!queued.TryGetValue(key, out var numbers))
            {
                numbers = new List<int>();
                queued[key] = numbers;
            }

            if (!numbers.Contains(episode))
            {
                numbers.Add(episode);
            }
        }

        return queued;
    }

    /// <summary>
    /// F-M420 (operator order 02.11.2026): the seasons a pack may be closed for at the END of a run —
    /// every episode is up, so a container is what is left to do.
    /// <para>
    /// Asked AFTER the item loop on purpose: at that point this run's own single uploads are in the
    /// store, which is exactly what makes a season packable ("erst einzelupload, dann staffelpack").
    /// Asked before the loop instead, a season whose episodes went up minutes earlier looked unpackable
    /// and waited a whole cycle for its container.
    /// </para>
    /// <para>
    /// The language is not invented: it is the one every episode of the season carries an UPLOADED
    /// row for, read from the registry per file. A season that is already a pack is left alone — its
    /// rows then carry the pack id — because sending the same container again every run is not a
    /// pack, it is a loop.
    /// </para>
    /// </summary>
    /// <param name="complete">Complete seasons with their aired episode set.</param>
    /// <returns>One entry per (season, language, variant) a pack may be attempted for.</returns>
    /// <summary>
    /// F-M429: the language/variant pairs a pack can be built from — the embedded dialogue tracks of the
    /// file. Used because the pack runs before the singles now: a new episode has no store row when the
    /// plan is made, but its file already states every language it can deliver.
    /// </summary>
    /// <param name="episode">Episode item.</param>
    /// <param name="mediaHash">Media hash, for the store fallback.</param>
    /// <returns>The pairs the file offers.</returns>
    private HashSet<(string Lang, bool Hi)> PackPairsOf(BaseItem episode, string? mediaHash)
    {
        var pairs = new HashSet<(string Lang, bool Hi)>();
        try
        {
            var streams = _mediaSourceManager.GetMediaStreams(episode.Id)
                ?.Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal)
                .ToList();
            if (streams != null)
            {
                foreach (var stream in streams)
                {
                    if (!Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsDialogueStream(stream))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(stream.Language))
                    {
                        continue;
                    }

                    string? mapped = LanguageMapper.MapToSubdl(stream.Language);
                    if (string.IsNullOrWhiteSpace(mapped))
                    {
                        continue;
                    }

                    pairs.Add((
                        mapped.ToUpperInvariant(),
                        Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsHearingImpairedStream(stream)));
                }
            }
        }
        catch (Exception ex)
        {
            LogUtil.PerItem(_config.LogMode, _logger,
                "[Sub Scribe] season pack plan: the stream list of {File} is unavailable ({Msg}) — falling back to the store for its languages.",
                Path.GetFileName(episode.Path ?? episode.Name), ex.Message);
        }

        if (pairs.Count > 0)
        {
            return pairs;
        }

        foreach (var row in Registry.GetEmbeds(mediaHash))
        {
            if (row.Status != SubtitleStatus.Uploaded || row.Forced == true)
            {
                continue;
            }

            pairs.Add((row.Language, row.HearingImpaired ?? false));
        }

        return pairs;
    }

    private List<(string ImdbId, int Season, string Lang, bool Hi)> PlanPacksFromStore(
        IReadOnlyDictionary<(string ImdbId, int Season), IReadOnlyList<int>> complete,
        IReadOnlyDictionary<(string ImdbId, int Season), IReadOnlySet<int>>? onlyEpisodes = null)
    {
        var planned = new List<(string ImdbId, int Season, string Lang, bool Hi)>();

        foreach (var entry in complete)
        {
            if (!_completeSeasonItems.TryGetValue(entry.Key, out var allEpisodes) || allEpisodes.Count == 0)
            {
                continue;
            }

            // F-M424 (operator order 02.11.2026): an arrival run plans its pack out of the queued items
            // only. The library holds the whole season, so without this cut the plan would count rows
            // of episodes that did not arrive — and the pack would carry members this run never touched.
            var episodes = onlyEpisodes != null && onlyEpisodes.TryGetValue(entry.Key, out var allowed)
                ? allEpisodes.Where(e => allowed.Contains(e.IndexNumber ?? 0)).ToList()
                : allEpisodes;
            if (episodes.Count == 0)
            {
                continue;
            }

            var seen = new Dictionary<(string Lang, bool Hi), int>();
            // F-M422 (operator doubt 02.11.2026: "es kann eigentlich nicht sein, dass so wenig durch
            // die Zuverlässigkeitsprüfung läuft"): the refusal below names which pair came closest and
            // which episodes it lacks. Without that the line was a verdict with no evidence, and the
            // difference between "one episode missing", "HI flag split" and "hash changed" was invisible.
            var pairEpisodes = new Dictionary<(string Lang, bool Hi), SortedSet<int>>();
            var episodesWithAnyRow = new SortedSet<int>();
            foreach (var episode in episodes)
            {
                string? path = episode.Path;
                string? mediaHash;
                try
                {
                    mediaHash = string.IsNullOrWhiteSpace(path) ? null : MediaHashFor(path);
                }
                catch (Exception ex)
                {
                    // A file that cannot be hashed contributes no language — the season is not planned.
                    LogUtil.PerItem(_config.LogMode, _logger,
                        "[Sub Scribe] season pack plan: {File} could not be hashed ({Msg}) — no fill-only pack for {Imdb} S{Season}.",
                        Path.GetFileName(path ?? episode.Name), ex.Message, entry.Key.Item1, entry.Key.Item2);
                    continue;
                }
                // F-M429 (operator order 03.11.2026): the pack is now planned BEFORE the singles, so the
                // store says nothing about this run's episodes yet. What a pack can carry is what the
                // FILE carries: its embedded dialogue tracks. The store rows stay the fallback for the
                // case where the stream list cannot be read.
                var pairs = PackPairsOf(episode, mediaHash);

                // Counted per EPISODE, not per row: a file may carry two tracks of the same language,
                // and the question is whether every episode has one, not how many.
                foreach (var pair in pairs)
                {
                    seen[pair] = seen.GetValueOrDefault(pair) + 1;

                    int number = episode.IndexNumber ?? 0;
                    if (!pairEpisodes.TryGetValue(pair, out var have))
                    {
                        have = new SortedSet<int>();
                        pairEpisodes[pair] = have;
                    }

                    if (number > 0)
                    {
                        have.Add(number);
                        episodesWithAnyRow.Add(number);
                    }
                }

            }

            foreach (var pair in seen.Where(p => p.Value == episodes.Count))
            {
                // F-M399 (operator doubt 02.11.2026, confirmed): the packs row is the ONLY proof. The
                // second proof this used to accept — "every episode carries a SubdlId" — was wrong: a
                // single upload writes its own id there too, so a season whose episodes all went up
                // alone looked "already packed" and was never packed at all. 98 seasons were reported
                // as inside a pack that never existed.
                // F-M571: dieselbe Staffelfrage wie im Sammler, keine zweite Abfrage je Paar.
                if (PacksOf(entry.Key.Item1, entry.Key.Item2).Contains((
                        Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical(pair.Key.Lang)
                            ?? (pair.Key.Lang ?? string.Empty).ToUpperInvariant(),
                        pair.Key.Hi)))
                {
                    LogUtil.PerItem(
                        _config.LogMode,
                        _logger,
                        "[Sub Scribe] season pack plan: {Imdb} S{Season} [{Lang}] — already inside a pack, nothing is re-sent.",
                        entry.Key.Item1, entry.Key.Item2, SeasonPack.LangTag(pair.Key.Lang, pair.Key.Hi));
                    continue;
                }

                LogUtil.Normal(
                    _logger,
                    "[Sub Scribe] season pack closing: {Imdb} S{Season} [{Lang}] — every episode is uploaded, the container is built from the store.",
                    entry.Key.Item1, entry.Key.Item2, SeasonPack.LangTag(pair.Key.Lang, pair.Key.Hi));
                planned.Add((entry.Key.Item1, entry.Key.Item2, pair.Key.Lang, pair.Key.Hi));
            }

            if (!seen.Any(p => p.Value == episodes.Count))
            {
                // F-M422: the verdict now carries its evidence — which language came closest, how far it
                // got, and which episodes it is missing. A season whose episodes have no row at all is
                // named separately: that is a hash/orphan problem, not a coverage problem.
                var closest = seen
                    .OrderByDescending(p => p.Value)
                    .ThenBy(p => p.Key.Lang, StringComparer.Ordinal)
                    .Take(3)
                    .Select(p =>
                    {
                        var have = pairEpisodes.TryGetValue(p.Key, out var set) ? set : new SortedSet<int>();
                        var missing = episodes
                            .Select(e => e.IndexNumber ?? 0)
                            .Where(n => n > 0 && !have.Contains(n))
                            .OrderBy(n => n)
                            .ToList();
                        return $"[{p.Key.Lang}{(p.Key.Hi ? "/SDH" : string.Empty)}] {p.Value}/{episodes.Count}, missing {string.Join(",", missing)}";
                    })
                    .ToList();

                var withoutRow = episodes
                    .Select(e => e.IndexNumber ?? 0)
                    .Where(n => n > 0 && !episodesWithAnyRow.Contains(n))
                    .OrderBy(n => n)
                    .ToList();

                LogUtil.Normal(
                    _logger,
                    "[Sub Scribe] season pack plan: {Imdb} S{Season} — no language is uploaded for every episode, no pack is built. Closest: {Closest}. Episodes with no uploaded row at all: {WithoutRow}.",
                    entry.Key.Item1, entry.Key.Item2,
                    closest.Count == 0 ? "none" : string.Join(" | ", closest),
                    withoutRow.Count == 0 ? "none" : string.Join(",", withoutRow));
            }
        }

        return planned;
    }

    /// <summary>
    /// F-M373: which complete seasons can still become a pack in THIS run — only those whose queued
    /// episodes are exactly the season that aired. A season that is only partly queued is already
    /// partly up, so it is worked as singles right away rather than buffered to the end of the run.
    /// </summary>
    /// <param name="complete">Complete seasons with their aired episode set.</param>
    /// <param name="items">The run's items, before the loop.</param>
    /// <returns>The seasons that may still form a pack.</returns>
    private ISet<(string ImdbId, int Season)> PlanSeasons(
        IReadOnlyDictionary<(string ImdbId, int Season), IReadOnlyList<int>> complete,
        IReadOnlyList<BaseItem> items)
    {
        var queued = QueuedEpisodes(items);

        var packable = new HashSet<(string ImdbId, int Season)>();
        foreach (var entry in complete)
        {
            queued.TryGetValue(entry.Key, out var numbers);
            var have = numbers ?? new List<int>();

            // F-M394 (operator order 02.11.2026): every season that is complete ON DISK is packable, no
            // matter how much of it this run happens to have queued. The old rule — pack only when ALL
            // episodes are queued, and fall back to singles otherwise — left a dead zone: a season with
            // 7 of 8 queued was neither packed nor filled, and the queue drained into single uploads.
            // What is queued goes into the buffer, what is missing is filled from what is already
            // uploaded; the coordinator decides in one place whether the pack can go up or not.
            packable.Add(entry.Key);
            LogUtil.Normal(
                _logger,
                "[Sub Scribe] season pack plan: {Imdb} S{Season} — {Have} of {Count} episode(s) queued, the rest is filled from what is already uploaded.",
                entry.Key.Item1, entry.Key.Item2, have.Count, entry.Value.Count);
        }

        return packable;
    }

    /// <summary>
    /// F-M373: works the packable seasons first and consecutively, so a season closes in one go and
    /// no buffer outlives it. Everything else keeps the order the queue gave it.
    /// </summary>
    /// <param name="items">The run's items.</param>
    /// <param name="packable">Seasons that may form a pack.</param>
    /// <returns>The items in working order.</returns>
    private List<BaseItem> OrderForClosure(
        List<BaseItem> items, ISet<(string ImdbId, int Season)> packable)
    {
        var run = new List<SeasonPack.RunItem>(items.Count);
        foreach (var item in items)
        {
            var (imdb, _, season, episode, isSeries) = ResolveIdsRaw(item);
            run.Add(new SeasonPack.RunItem(item.Id.ToString(), isSeries ? imdb : null, season, episode));
        }

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = SeasonPack.ClosureOrder(run, packable);
        for (int i = 0; i < order.Count; i++)
        {
            rank[order[i]] = i;
        }

        // Stable: items outside the plan keep their relative order behind the planned seasons.
        return items
            .OrderBy(i => rank.TryGetValue(i.Id.ToString(), out var r) ? r : int.MaxValue)
            .ToList();
    }

    /// <summary>
    /// F-M467 (measured 11.11.2026 on VisionQuest tt23112594): the IDS OF THE SERIES, and the series is
    /// resolved even when the episode does not carry it. <c>Episode.Series</c> is a convenience that is
    /// NULL for items read as children of a season folder — which is exactly how the pack phase reads a
    /// season it did not work this run. Every per-FILE language check then compared against a null id,
    /// answered "no" for every language, and no pack was built although the files carried 25 languages
    /// in all eight episodes. The lookup by <c>SeriesId</c> is the second witness; it is what makes a
    /// pack independent of whether the series object happened to be loaded.
    /// </summary>
    /// <param name="ep">Episode item.</param>
    /// <param name="byId">Resolves an item id, usually the library manager.</param>
    /// <returns>The series' IMDb and TMDB ids, or nulls when the series cannot be reached.</returns>
    internal static (string? ImdbId, string? TmdbId) SeriesIdsOf(Episode ep, Func<Guid, BaseItem?>? byId)
    {
        // The convenience getter throws when the episode cannot resolve its series (measured in the
        // harness: Episode.get_Series() raises instead of answering null), so the access is guarded —
        // a lookup that cannot answer must never cost the pack phase.
        MediaBrowser.Controller.Entities.TV.Series? series = null;
        try
        {
            series = ep.Series;
        }
        catch (Exception)
        {
            series = null;
        }

        if (series == null && ep.SeriesId != Guid.Empty && byId != null)
        {
            series = byId(ep.SeriesId) as MediaBrowser.Controller.Entities.TV.Series;
        }

        string? imdb = null;
        string? tmdb = null;
        series?.ProviderIds?.TryGetValue("Imdb", out imdb);
        series?.ProviderIds?.TryGetValue("Tmdb", out tmdb);
        return (imdb, tmdb);
    }

    private (string? ImdbId, string? TmdbId, int Season, int Episode, bool IsSeries) ResolveIdsRaw(BaseItem item)
    {
        switch (item)
        {
            case Episode ep:
                // F-M9/F-M28: SERIES IMDB, never episode IMDB — and F-M467: the series is resolved by id
                // when the episode does not carry it.
                var (seriesImdb, seriesTmdb) = SeriesIdsOf(ep, id => _libraryManager.GetItemById(id));
                return (seriesImdb, seriesTmdb, ep.ParentIndexNumber ?? -1, ep.IndexNumber ?? -1, true);
            case Movie movie:
                string? imdb = null;
                string? tmdb = null;
                movie.ProviderIds?.TryGetValue("Imdb", out imdb);
                movie.ProviderIds?.TryGetValue("Tmdb", out tmdb);
                return (imdb, tmdb, 0, 0, false);
            default:
                return (null, null, -1, -1, false);
        }
    }

    /// <summary>
    /// Metadata wait (on arrival, user decision 08.09.2026): max 15 min.
    /// Metadata is polled every 5 min (3 checks total); between polls a 30 s
    /// heartbeat loop keeps the in-run watchdog alive (its grace can be as low
    /// as 3× the call period). Returns the fresh item or null on timeout.
    /// </summary>

    private (string? ImdbId, int Season, int Episode, bool IsSeries) ResolveIds(BaseItem item)
    {
        switch (item)
        {
            case Episode ep:
                // F-M9/F-M28: SERIES IMDB, never episode IMDB — F-M467 resolves the series by id.
                var (seriesImdb, _) = SeriesIdsOf(ep, id => _libraryManager.GetItemById(id));
                return (seriesImdb, ep.ParentIndexNumber ?? -1, ep.IndexNumber ?? -1, true);
            case Movie movie:
                string? imdb = null;
                movie.ProviderIds?.TryGetValue("Imdb", out imdb);
                return (imdb, 0, 0, false);
            default:
                return (null, -1, -1, false);
        }
    }

    private List<BaseItem> CollectItems(List<string> libraryNames)
    {
        var result = new List<BaseItem>();
        // F-M189 (24.09.2026): collect via the PATH scope, not by library name —
        // a library nested inside another one owns no items of its own (JF logs
        // "Found duplicate path"), so a name-only match returns an empty list.
        var scope = LibraryScope.Create(_libraryManager, libraryNames, _logger);
        if (scope.IsEmpty)
        {
            return result;
        }

        // Enumerate virtual libraries via the user root folder (10.11-safe, no query types)
        var root = _libraryManager.GetUserRootFolder();
        foreach (var child in root.Children)
        {
            if (child is not CollectionFolder folder)
            {
                continue;
            }

            // Walk a library when the selection touches it — the outer library of
            // a nested selection holds the items, so it must be enumerated too.
            if (!scope.IsLibraryRelevant(folder.Name) && !scope.IsSelectedName(folder.Name))
            {
                continue;
            }

            result.AddRange(folder.GetRecursiveChildren()
                .Where(i => (i is Movie || i is Episode) && !string.IsNullOrWhiteSpace(i.Path))
                .Where(scope.Contains));
        }

        return result;
    }


    /// <summary>
    /// F-M152 (rev.10): the later of the API reset (v2 headers) and the
    /// download reset (v2 sources), default 00:00 UTC each. The recovery fire
    /// anchors on the LATER of the two so both quotas are guaranteed free.
    /// </summary>
    private DateTime LaterOfResets()
    {
        var apiReset = _api.RateLimitResetUtc ?? NextMidnightUtc();
        var dlReset = NextMidnightUtc(); // download quota resets at 00:00 UTC (50/day)
        return apiReset > dlReset ? apiReset : dlReset;
    }

    /// <summary>F-M152 (rev.10): next 00:00 UTC after now (fail-safe anchor).</summary>
    private DateTime NextMidnightUtc()
    {
        var now = DateTime.UtcNow;
        return now.Date.AddDays(now.TimeOfDay == TimeSpan.Zero ? 0 : 1);
    }



    /// <summary>
    /// F-M17y: cleans up rejected entries from /user/mySubtitles. Matched rejected uploads
    /// (by content hash or metadata) are counted separately; all rejected dashboard entries
    /// are deleted. Accepted entries disappear from mySubtitles on their own.
    /// </summary>
    /// <summary>
    /// F-M176: Upload postprocessing runs on its own schedule. It acquires the global
    /// PipelineRunLock (up to 10 min) and is rescheduled by JobSpacingMinutes on lock-busy.
    /// The same reschedule happens on hourly API rate-limit. Stale-lock detection is
    /// handled by PipelineRunLock (24 h / dead PID), so no separate semaphore is needed.
    /// </summary>
    internal static async Task RunUploadPostprocessingAsync()
    {
        var logger = Plugin.Instance?.LoggerFactory?.CreateLogger("SubDL-UploadPostprocessing");

        // F-M176: single global run lock. On overlap reschedule by JobSpacingMinutes
        // instead of waiting (rework 25.09.2026 — no more 10-minute poll loop).
        if (!await Pipeline.PipelineRunLock.AcquireAsync(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "postprocessing", CancellationToken.None).ConfigureAwait(false))
        {
            var spacing = JobSpacingMinutes();
            ScribeSchedulerCoordinator.Instance?.SchedulePostprocessFire(DateTime.UtcNow.AddMinutes(spacing));
            LogUtil.Normal(logger, "[Sub Scribe] Postprocessing deferred by {Spacing} min (global lock busy).", spacing);
            return;
        }

        try
        {
            _postprocessingRunning = true;
            _postprocessingStartUtc = DateTime.UtcNow;
            await RunUploadPostprocessingInternalAsync().ConfigureAwait(false);
        }
        finally
        {
            _postprocessingRunning = false;
            _postprocessingStartUtc = DateTime.MinValue;
            Pipeline.PipelineRunLock.Release(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "postprocessing");
        }
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

    private static async Task RunUploadPostprocessingInternalAsync()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return;
        }

        var cfg = plugin.Configuration;
        var loggerFactory = plugin.LoggerFactory;
        if (loggerFactory == null)
        {
            return;
        }

        var registry = plugin.Registry;
        if (registry == null)
        {
            return;
        }

        var logger = loggerFactory.CreateLogger("SubDL-UploadPostprocessing");
        LogUtil.Detail(logger, "[Sub Scribe-Postprocessing] effective pacing: MinCallPauseSec={Pause} (no local call budget, F-M20).", cfg.MinCallPauseSec);

        try
        {
            _postprocessingStartedUtc = DateTime.UtcNow;
            _postprocessingRunning = true;
            _postprocessingPendingTotal = 0;
            _postprocessingPendingRemaining = 0;
            _postprocessingResolved = 0;
            _postprocessingAccepted = 0;
            _postprocessingRejected = 0;
            _postprocessingDeleted = 0;
            _postprocessingLastResult = "running";

            int maxRuntimeMinutes = Math.Clamp(cfg.PostprocessMaxRuntimeMinutes, 1, 30);
            int maxPages = Math.Clamp(cfg.PostprocessMaxPages, 1, 20000);
            using var runCts = new CancellationTokenSource(TimeSpan.FromMinutes(maxRuntimeMinutes));
            var ct = runCts.Token;

            // F-M343: same client identity as every other SubDL call.
            var http = ScribeApiClient.NewHttpClient(TimeSpan.FromSeconds(30));
            var api = new ScribeApiClient(http)
            {
                Username = cfg.Username,
                Password = cfg.Password,
                ApiKey = cfg.ApiKey
            };
            api.Log += msg => LogUtil.Detail(logger, "[Sub Scribe-Postprocessing] {Msg}", msg);

            var limiter = new GlobalRateLimiter(cfg.UploadsPerHour, cfg.MinCallPauseSec);

            try
            {
                // F-M20 (03.10.2026): no local budget left to exhaust — the pause is the
                // whole throttle and the login always goes ahead.
                await limiter.PauseAsync(ct).ConfigureAwait(false);
                await api.LoginAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning("[Sub Scribe] Upload postprocessing login failed: {Msg}", ex.Message);
                return;
            }

            List<OwnSubtitleEntry>? allMySubtitles = null;
            try
            {
                // F-M20 (03.10.2026): no local budget left to exhaust — the pause is the
                // whole throttle and this fetch always goes ahead.
                await limiter.PauseAsync(ct).ConfigureAwait(false);
                allMySubtitles = await api.ListMySubtitlesAsync(ct, maxPages: maxPages).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("[Sub Scribe] Upload postprocessing: fetching mySubtitles timed out.");
            }
            catch (Exception ex)
            {
                logger.LogWarning("[Sub Scribe] Upload postprocessing could not fetch mySubtitles: {Msg}", ex.Message);
            }

            if (allMySubtitles == null)
            {
                _postprocessingRunning = false;
                _postprocessingLastResult = "failed: could not fetch mySubtitles";
                return;
            }

            var rejectedEntries = allMySubtitles
                .Where(o => string.Equals(o.Status, "rejected", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // F-M435 (operator order 03.11.2026): an entry that was still awaiting review when it was
            // uploaded is stored with its upload id. Once SubDL publishes it, the delete route needs the
            // entry id — and the published list names both numbers for the same entry, so the stored
            // handle is moved over here instead of waiting for the next resolution to guess.
            int remappedHandles = 0;
            int pagesWalked = 0;
            DateTime watermark = cfg.PostprocessWatermarkUtc?.ToUniversalTime() ?? DateTime.MinValue;
            DateTime newestSeen = DateTime.MinValue;
            try
            {
                await limiter.PauseAsync(ct).ConfigureAwait(false);
                // F-M437 (operator decision 04.11.2026): walk down only until the entries are older than
                // the last pass reached. The list is newest first, so this is where the work of the last
                // days sits; a pass after a pause keeps going until the watermark and catches up on its
                // own. maxPages stays as the ceiling of that walk, not as its length.
                var publishedEntries = await api.ListMyPublishedSubtitlesAsync(
                    ct,
                    maxPages: maxPages,
                    shouldStop: e => watermark != DateTime.MinValue
                                     && e.CreatedAt != default
                                     && e.CreatedAt.ToUniversalTime() < watermark,
                    pageRead: n => pagesWalked = n).ConfigureAwait(false);
                foreach (var publishedEntry in publishedEntries ?? new List<OwnSubtitleEntry>())
                {
                    if (publishedEntry.CreatedAt != default
                        && publishedEntry.CreatedAt.ToUniversalTime() > newestSeen)
                    {
                        newestSeen = publishedEntry.CreatedAt.ToUniversalTime();
                    }

                    if (publishedEntry.EntryId <= 0 || publishedEntry.UploadId <= 0
                        || publishedEntry.EntryId == publishedEntry.UploadId)
                    {
                        continue;
                    }

                    remappedHandles += registry.RemapSubdlId(
                        publishedEntry.UploadId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        publishedEntry.EntryId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning("[Sub Scribe] Upload postprocessing could not read myPublishedSubtitles: {Msg}", ex.Message);
            }

            // F-M437: the watermark is the newest createdAt this pass saw — stored through the partial
            // writer, so the next pass starts where this one ended instead of re-reading the whole list.
            if (newestSeen > watermark)
            {
                cfg.PostprocessWatermarkUtc = newestSeen;
                plugin.SavePluginState();
            }

            LogUtil.Normal(
                logger,
                "[Sub Scribe] Upload postprocessing: walked {Pages} page(s) down to the watermark, {Count} stored handle(s) moved to their published entry id (F-M435/F-M437).",
                pagesWalked, remappedHandles);


            LogUtil.Normal(logger, "[Sub Scribe] Upload postprocessing: fetched {Total} mySubtitles entries, {Rejected} rejected to clean up.", allMySubtitles.Count, rejectedEntries.Count);

            int matchedDeleted = 0, unmatchedDeleted = 0, failedToDelete = 0;
            var uploadedCandidates = registry.GetUploadedEmbeds();

            foreach (var rejected in rejectedEntries)
            {
                if (ct.IsCancellationRequested)
                {
                    logger.LogWarning("[Sub Scribe] Upload postprocessing: run time limit reached, stopping early.");
                    break;
                }

                bool matched = false;
                foreach (var up in uploadedCandidates)
                {
                    var upMedia = registry.GetMedia(up.MediaHash);
                    bool byContentHash = rejected.ContentHash != null
                        && !string.IsNullOrEmpty(up.ContentHash)
                        && string.Equals(rejected.ContentHash, up.ContentHash, StringComparison.OrdinalIgnoreCase);
                    bool byId = false;
                    if (upMedia != null)
                    {
                        byId = string.Equals(rejected.ImdbId, upMedia.ImdbId, StringComparison.OrdinalIgnoreCase)
                            || (!string.IsNullOrEmpty(rejected.TmdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                && !string.IsNullOrEmpty(upMedia.TmdbId)
                                && string.Equals(rejected.TmdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture), upMedia.TmdbId, StringComparison.OrdinalIgnoreCase));
                    }
                    bool seasonOk = rejected.Season == 0 || rejected.Season == (upMedia?.Season ?? 0);
                    bool episodeOk = rejected.Episode == 0 || rejected.Episode == (upMedia?.Episode ?? 0);
                    bool byMetadata = byId && seasonOk && episodeOk
                        && string.Equals(LanguageMapper.MapToSubdlName(rejected.Language), up.Language, StringComparison.OrdinalIgnoreCase);

                    if (byContentHash || byMetadata)
                    {
                        matched = true;
                        break;
                    }
                }

                bool deleted = false;
                try
                {
                    // F-M20 (03.10.2026): the local call budget is gone, so this delete always
                    // goes ahead — only the pause remains. No cap can defer it any more.
                    await limiter.PauseAsync(ct).ConfigureAwait(false);
                    var delStart = DateTime.UtcNow;
                    using var deleteCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    deleted = await api.DeleteMySubtitleAsync(rejected.UploadId, deleteCts.Token).ConfigureAwait(false);
                    LogUtil.Detail(logger, "[Sub Scribe-Postprocessing] delete {UploadId} took {ElapsedMs} ms (pause+network).", rejected.UploadId, (DateTime.UtcNow - delStart).TotalMilliseconds);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("[Sub Scribe] Upload postprocessing: delete rejected upload {UploadId} timed out.", rejected.UploadId);
                }
                catch (Exception delEx)
                {
                    logger.LogWarning("[Sub Scribe] Upload postprocessing: delete rejected upload {UploadId} failed: {Msg}", rejected.UploadId, delEx.Message);
                }

                if (deleted)
                {
                    _postprocessingDeleted++;
                    if (matched)
                    {
                        matchedDeleted++;
                        foreach (var up in uploadedCandidates)
                        {
                            var upMedia = registry.GetMedia(up.MediaHash);
                            bool byContentHash = rejected.ContentHash != null
                                && !string.IsNullOrEmpty(up.ContentHash)
                                && string.Equals(rejected.ContentHash, up.ContentHash, StringComparison.OrdinalIgnoreCase);
                            bool byId = false;
                            if (upMedia != null)
                            {
                                byId = string.Equals(rejected.ImdbId, upMedia.ImdbId, StringComparison.OrdinalIgnoreCase)
                                    || (!string.IsNullOrEmpty(rejected.TmdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                        && !string.IsNullOrEmpty(upMedia.TmdbId)
                                        && string.Equals(rejected.TmdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture), upMedia.TmdbId, StringComparison.OrdinalIgnoreCase));
                            }
                            bool seasonOk = rejected.Season == 0 || rejected.Season == (upMedia?.Season ?? 0);
                            bool episodeOk = rejected.Episode == 0 || rejected.Episode == (upMedia?.Episode ?? 0);
                            bool byMetadata = byId && seasonOk && episodeOk
                                && string.Equals(LanguageMapper.MapToSubdlName(rejected.Language), up.Language, StringComparison.OrdinalIgnoreCase);

                            if (byContentHash || byMetadata)
                            {
                                registry.MarkAndFlush(() =>
                                {
                                    registry.PatchMediaMetadata(up.MediaHash, upMedia?.ImdbId,
                                        upMedia?.Season ?? 0, upMedia?.Episode, rejected.NId, upMedia?.TmdbId);
                                    registry.MarkEmbed(up.MediaHash, up.SubPos, up.Language, up.HearingImpaired ?? false,
                                        SubtitleStatus.Rejected, contentHash: up.ContentHash,
                                        reason: RejectReason.DuplicateRemote, subdlId: rejected.NId);
                                });
                                break;
                            }
                        }

                        LogUtil.PerItem(logger, "[Sub Scribe] Upload postprocessing: deleted matched rejected upload {UploadId} ({Name}) and marked remote-duplicate.", rejected.UploadId, rejected.Name);
                    }
                    else
                    {
                        unmatchedDeleted++;
                        LogUtil.PerItem(logger, "[Sub Scribe] Upload postprocessing: deleted unmatched rejected upload {UploadId} ({Name}).", rejected.UploadId, rejected.Name);
                    }
                }
                else
                {
                    failedToDelete++;
                    logger.LogWarning("[Sub Scribe] Upload postprocessing: failed to delete rejected upload {UploadId}.", rejected.UploadId);
                }
            }

            _postprocessingRunning = false;
            var elapsed = DateTime.UtcNow - _postprocessingStartedUtc;
            _postprocessingRejected = rejectedEntries.Count;
            _postprocessingResolved = matchedDeleted;
            _postprocessingLastResult = $"done in {elapsed.TotalSeconds:F0}s: {matchedDeleted} matched rejected deleted+marked, {unmatchedDeleted} unmatched rejected deleted, {failedToDelete} failed, {rejectedEntries.Count} rejected entries";
            LogUtil.Normal(logger, 
                "[Sub Scribe] Upload postprocessing DONE in {Seconds:F0}s: {Matched} matched rejected deleted+remote-duplicate, {Unmatched} unmatched rejected deleted, {Failed} failed, {Total} rejected in fetched pages.",
                elapsed.TotalSeconds, matchedDeleted, unmatchedDeleted, failedToDelete, rejectedEntries.Count);
        }
        catch (Exception ex)
        {
            _postprocessingRunning = false;
            var elapsed = DateTime.UtcNow - _postprocessingStartedUtc;
            _postprocessingLastResult = $"failed after {elapsed.TotalSeconds:F0}s: {ex.Message}";
            logger.LogWarning("[Sub Scribe] Upload postprocessing failed: {Msg}", ex.Message);
        }
    }

    // F-M17y: pending upload attempt model removed; metadata lives in the registry.
    // Kept as a comment for history until the next cleanup.

    /// <summary>
    /// F-M509 (Operator-Klarstellung 15.11.2026: „Tvst is splitted into tvst and moviesub itself"):
    /// EIN Versand je Quelle — die Stelle, an der die Kette ihre Quelle auf ihre Methode abbildet.
    /// <para>
    /// Hier stand bis Etappe 2b die Bauform „wenn SubDL, dann …, sonst die zweite Quelle". Sie war die
    /// Sperre dafuer, dass eine dritte Quelle je ihren eigenen Weg bekommen konnte: alles, was nicht
    /// SubDL war, landete bei tvsubtitles.net — auch ein Film. Jetzt nennt jede Quelle ihren Namen, und
    /// die Medienart ist im Endpunkt selbst die Bedingung (die Film-Adresse lehnt ein Ticket mit Staffel
    /// oder Folge ab, die Serien-Adresse eines ohne).
    /// </para>
    /// <para>
    /// F-M508: die Film-Adresse wird nur erreicht, wenn die Medienart ein Film ist — das entscheidet die
    /// Liste (<see cref="UploadSourceOrder"/>) ueber <c>SourceOrder.AppliesTo</c>, nicht diese Methode.
    /// </para>
    /// </summary>
    /// <param name="quelle">Die Quelle, die senden soll.</param>
    /// <param name="srtContent">Der Untertiteltext.</param>
    /// <param name="neutralName">Der neutrale Dateiname des Uploads.</param>
    /// <param name="lang">Der Sprachcode.</param>
    /// <param name="releaseName">Der Release-Name der Mediendatei.</param>
    /// <param name="quality">Die erkannte Quelle/Qualitaet (Rip-Feld).</param>
    /// <param name="imdbId">IMDb-Kennung, wenn bekannt.</param>
    /// <param name="tmdbId">TMDb-Kennung, wenn bekannt.</param>
    /// <param name="isSeries">Wahr fuer eine Serie, falsch fuer einen Film.</param>
    /// <param name="season">Staffel (Serie) — 0 beim Film.</param>
    /// <param name="episode">Folge (Serie) — 0 beim Film.</param>
    /// <param name="hearingImpaired">HI-Variante.</param>
    /// <param name="mediaHash">Medium (OSHash) fuer die Nummern-Aufloesung im Store.</param>
    /// <param name="mediaPath">F-M517: der Pfad der Mediendatei — die zweite Adresse der Medienzeile, wenn
    /// kein Hash bekannt ist (die Nummer darf dann nicht verloren gehen).</param>
    /// <param name="framerate">Bildrate-Kennung (nur SubDL).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Das Ergebnis DIESER Quelle.</returns>
    /// <summary>
    /// F-M608-Nachtrag (Messung 18.11.2026, 16:07): Titel, die in DIESEM Lauf schon geschlossen wurden.
    /// Ohne diese Liste lief die Leiter fuer jede Spur desselben Titels erneut — vier Zuege mit 2+4+8 s
    /// Pause je Datei, also 14 s pro Spur, und der Store sah die Sperre im selben Lauf nicht (die
    /// Schreibvorgaenge liegen bis zum Ende in der Klammer aus F-M602). Gemessen: „tt14129116" fuhr die
    /// Leiter fuer jede EN-Datei erneut ab.
    /// </summary>
    private readonly System.Collections.Generic.HashSet<string> _titelGeschlossen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// F-M611: die Datei-Signatur je Staffel, wie sie in der Pack-Planung berechnet wurde. Der
    /// Koordinator schreibt damit seine Urteile, ohne die Bibliothek ein zweites Mal zu befragen.
    /// </summary>
    private readonly System.Collections.Generic.Dictionary<(string ImdbId, int Season), string> _saisonSignatur = new();

    /// <summary>
    /// F-M609: ist dieser HTTP-Status eine STOERUNG statt einer Ablehnung? 429 („spaeter nochmal") und
    /// 5xx (Serverfehler) sind keine Entscheidung ueber den Untertitel — sie duerfen das Paar nicht
    /// schliessen. Ohne Status (kein Sendevorgang) gibt es nichts zu entschuldigen: das ist ein Nein.
    /// </summary>
    /// <param name="status">The HTTP status, or null when nothing was sent.</param>
    /// <returns>True when the answer was a disturbance, false when it was a decision.</returns>
    private static bool IstStoerung(int? status)
        => status is 429 or (>= 500 and < 600);

    /// <summary>F-M608: die Pausen der Titel-Leiter — nach Fehlschlag 1 zwei, nach 2 vier, nach 3 acht Sekunden.</summary>
    private static readonly int[] TitelLeiterPausenMs = { 2000, 4000, 8000 };

    /// <summary>
    /// F-M608 (Operator-Anweisung 18.11.2026: „Anlegen, nachsuchen, fail 1: 2 Sekunden pause, fail 2: 4
    /// sekunden pause, fail 3: 8 Sekunden pause. Fail4: alles pairs des movies der serie dauerhaft
    /// schließen" — „Bei tvst und mst natürlich nur"): faehrt die Leiter fuer EINEN Titel ab.
    /// <para>
    /// Vier Zuege. Zwischen den Zuegen 2 s, 4 s, 8 s — die Seite braucht nach dem Anlegen manchmal einen
    /// Moment, bis die Nachsuche den Titel findet, und genau dafuer sind die Pausen da. Vor jedem Zug wird
    /// der Speicher-des-Laufs geleert (<paramref name="zwischenZug"/>), sonst antwortet der Resolver mit
    /// dem gemerkten Fehlschlag und der naechste Zug waere sinnlos. Nach dem vierten Fehlschlag wird der
    /// Titel dauerhaft geschlossen: damit sind ALLE Paare dieses Films bzw. dieser Serie entschieden.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The resolver's answer type.</typeparam>
    /// <param name="quelle">The address — <c>tvsubtitles</c> or <c>moviesubtitles</c>.</param>
    /// <param name="titelId">IMDb id, else TMDb id, of the title.</param>
    /// <param name="versuch">One attempt.</param>
    /// <param name="gelungen">Whether an attempt succeeded.</param>
    /// <param name="grund">The status of a failed attempt.</param>
    /// <param name="zwischenZug">Clears the resolver's per-run memory (never null).</param>
    /// <param name="schonBelegt">
    /// F-M610: Zustaende, die ein BELEG sind, aber keine Aufloesung — „der Film existiert, die Kennung ist
    /// nicht lesbar". Sie zaehlen als Erfolg der Leiter, damit das Paar ueber F-M603 geschlossen wird und
    /// NICHT der ganze Titel. Vorher schloss die Leiter nach vier solchen Zuegen den Titel fuer immer,
    /// obwohl die Seite den Film fuehrt — gemessen 18.11.2026 an 12 Titeln.
    /// </param>
    /// <param name="stoerung">
    /// F-M610: Zustaende ohne Urteil („unclear", Serverfehler). Sie zaehlen nicht als Fehlschlag, und der
    /// Titel wird NICHT geschlossen — sonst waere ein Lauf waehrend einer Serverstoerung ein Dauerurteil,
    /// genau das, was F-M609 fuer die Paare schon verhindert.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Die letzte Antwort, ob sie gelungen ist, und ob der Titel geschlossen wurde.</returns>
    private async Task<(T Ergebnis, bool Gelungen, bool Geschlossen)> MitTitelLeiterAsync<T>(
        string quelle,
        string titelId,
        Func<Task<T>> versuch,
        Func<T, bool> gelungen,
        Func<T, string?> grund,
        Func<T, bool>? schonBelegt,
        Func<T, bool>? stoerung,
        Action zwischenZug,
        CancellationToken ct)
    {
        T ergebnis = default!;
        string? letzterGrund = null;
        bool letzteStoerung = false;
        int zuege = _config.DryRun ? 1 : Jellyfin.Plugin.SubScribe.Registry.TitleFailureStore.ClosingAttempt;

        for (int zug = 1; zug <= zuege; zug++)
        {
            if (zug > 1)
            {
                int pause = TitelLeiterPausenMs[Math.Min(zug - 2, TitelLeiterPausenMs.Length - 1)];
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[Sub Scribe] {Source} title {Title}: attempt {Zug}/{Zuege} failed ({Reason}) — waiting {Pause} ms (F-M608)",
                    quelle, titelId, zug - 1, zuege, letzterGrund ?? "?", pause);
                await Task.Delay(pause, ct).ConfigureAwait(false);
                zwischenZug();
            }

            ergebnis = await versuch().ConfigureAwait(false);

            // F-M610: ein Beleg ohne Aufloesung („existiert, Kennung nicht lesbar") ist KEIN Fehlschlag
            // der Leiter — das Paar wird ueber F-M603 geschlossen, der Titel bleibt offen.
            if (gelungen(ergebnis) || (schonBelegt?.Invoke(ergebnis) ?? false))
            {
                Jellyfin.Plugin.SubScribe.Registry.TitleFailureStore.Clear(quelle, titelId);
                return (ergebnis, true, false);
            }

            letzterGrund = grund(ergebnis) ?? "?";
            letzteStoerung = stoerung?.Invoke(ergebnis) ?? false;
            if (!letzteStoerung)
            {
                // F-M610: eine Stoerung zaehlt nicht als Fehlschlag — sie ist kein Urteil.
                Jellyfin.Plugin.SubScribe.Registry.TitleFailureStore.Note(quelle, titelId, letzterGrund);
            }
        }

        if (!_config.DryRun && !letzteStoerung)
        {
            Jellyfin.Plugin.SubScribe.Registry.TitleFailureStore.Close(quelle, titelId, letzterGrund ?? "?");
            LogUtil.Normal(_logger,
                "[Sub Scribe] {Source} title {Title}: {Zuege} attempts failed ({Reason}) — every pair of this title is closed for good (F-M608)",
                quelle, titelId, zuege, letzterGrund ?? "?");
            return (ergebnis, false, true);
        }

        if (letzteStoerung)
        {
            LogUtil.PerItem(_config.LogMode, _logger,
                "[Sub Scribe] {Source} title {Title}: {Zuege} attempts without a verdict ({Reason}) — nothing closed, the next run asks again (F-M610)",
                quelle, titelId, zuege, letzterGrund ?? "?");
        }

        return (ergebnis, false, false);
    }

    private Task<UploadResult> SendToSourceAsync(
        Configuration.SubtitleProvider quelle, string srtContent, string neutralName, string lang,
        string releaseName, string quality, string? imdbId, string? tmdbId, bool isSeries,
        int season, int episode, bool hearingImpaired, string? mediaHash, string? mediaPath,
        string? framerate, CancellationToken ct) => quelle switch
    {
        Configuration.SubtitleProvider.Subdl => _api.UploadSubtitleAsync(
            srtContent, neutralName, lang, releaseName, quality, imdbId, tmdbId, isSeries, season,
            episode, ct, hearingImpaired, framerate),
        Configuration.SubtitleProvider.TvSubtitles => SendToTvSubtitlesAsync(
            srtContent, neutralName, lang, releaseName, quality, imdbId, tmdbId, isSeries, season,
            episode, hearingImpaired, mediaHash, mediaPath, ct),
        Configuration.SubtitleProvider.MovieSubtitles => SendToMovieSubtitlesAsync(
            srtContent, neutralName, lang, releaseName, quality, imdbId, tmdbId, isSeries, mediaHash,
            mediaPath, ct),
        _ => Task.FromResult(UploadResult.Skipped("no-upload-path")),
    };

    /// <summary>
    /// F-M508 (Etappe 2b): sends one subtitle to the FILM address of the tvst family
    /// (moviesubtitles.org).
    /// <para>
    /// Die Arbeitsteilung ist die der Serien-Adresse: die AUFLOESUNG der Filmmummer („welcher Film ist
    /// diese Datei") gehoert in den Store und steht in <see cref="Api.Endpoints.MsubMovieResolver"/> —
    /// gespeicherte Nummer, sonst enge Suche ueber TMDb-Titel und TMDb-Jahr. Fehlt die Nummer am Ende,
    /// wird diese Adresse uebersprungen und die Kette laeuft weiter. Der TRANSPORT ist der Endpunkt.
    /// </para>
    /// <para>
    /// F-M513: FEHLT DER FILM, WIRD ER HIER ANGELEGT — und nur hier. <see cref="Api.Endpoints.MsubMovieResolver.ResolveOrCreateForMediaAsync"/>
    /// ist die einzige Fassung, die anlegen darf, und dieser Zweig ist ihre einzige Aufrufstelle: er
    /// wird ausschliesslich betreten, wenn der Upload-Schalter der Film-Adresse an ist (Gate der Kette,
    /// <c>SourceAllowed</c>), und der Trockenlauf baut die Anlege-Nutzlast, ohne sie zu senden. Ob
    /// angelegt, gefunden oder verweigert wurde, steht im <see cref="Api.Endpoints.MsubMovieResolution.Status"/>
    /// — nicht nur im Log.
    /// </para>
    /// <para>
    /// GESPERRT BLEIBT SIE UEBER DEN SCHALTER: der Upload-Schalter dieser Adresse ist im ungesetzten Fall
    /// aus (F-M507/F-M508). Geprueft wird gegen Fixtures und Zaehl-Handler; ohne ihn geht kein POST gegen
    /// die Seite.
    /// </para>
    /// </summary>
    /// <param name="srtContent">Der Untertiteltext.</param>
    /// <param name="neutralName">Der neutrale Dateiname des Uploads.</param>
    /// <param name="lang">Der Sprachcode.</param>
    /// <param name="releaseName">Der Release-NAME der Mediendatei — der Endpunkt laesst daraus die Gruppe
    /// schneiden (F-M505); der Dateiname selbst geht nicht in das Feld.</param>
    /// <param name="quality">Die erkannte Quelle/Qualitaet (Rip-Feld der Seite).</param>
    /// <param name="imdbId">IMDb-Kennung, wenn bekannt.</param>
    /// <param name="tmdbId">TMDb-Kennung, wenn bekannt — ohne sie gibt es keinen fuehrenden Titel.</param>
    /// <param name="isSeries">Wahr fuer eine Serie: dann ist diese Adresse nicht zustaendig.</param>
    /// <param name="mediaHash">Medium (OSHash) fuer die Nummer im Store.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Das Ergebnis dieser Adresse.</returns>
    private async Task<UploadResult> SendToMovieSubtitlesAsync(
        string srtContent, string neutralName, string lang, string releaseName, string quality,
        string? imdbId, string? tmdbId, bool isSeries, string? mediaHash, string? mediaPath,
        CancellationToken ct)
    {
        if (isSeries)
        {
            return UploadResult.Skipped("film-source-films-only");
        }

        // F-M538: dasselbe fuer die Film-Adresse — sie hat ihr eigenes Sprachset, nicht das der Serienseite.
        if (Api.MovieSubtitlesClient.SiteLanguage(lang) is null)
        {
            LogUtil.PerItem(_config.LogMode, _logger,
                "[moviesubtitles] {Name} [{Lang}] — skipped: the site has no such language (F-M538)",
                neutralName, lang);
            return UploadResult.Skipped("second-source-no-language");
        }

        // F-M508: ohne TMDb-Kennung gibt es keinen fuehrenden Titel — dann wird nicht geraten.
        if (string.IsNullOrWhiteSpace(tmdbId))
        {
            LogUtil.PerItem(_config.LogMode, _logger,
                "[moviesubtitles] {Name} [{Lang}] — skipped: no TMDb id, so there is no leading title (F-M508)",
                neutralName, lang);
            return UploadResult.Skipped("film-source-no-tmdb-id");
        }

        var movie = await _tmdb.MovieInfoAsync(tmdbId!, ct).ConfigureAwait(false);
        if (movie is null)
        {
            LogUtil.PerItem(_config.LogMode, _logger,
                "[moviesubtitles] {Name} [{Lang}] — skipped: TMDb answered no film record for {Tmdb} (F-M508)",
                neutralName, lang, tmdbId);
            return UploadResult.Skipped("film-source-no-film-record");
        }

        // F-M608: Titel-Leiter (anlegen → nachsuchen, Pausen 2/4/8 s, nach dem vierten Fehlschlag ist der
        // Titel dauerhaft geschlossen — damit sind alle Paare dieses Films entschieden).
        string msubTitelId = !string.IsNullOrWhiteSpace(imdbId) ? imdbId! : (tmdbId ?? mediaHash ?? string.Empty);
        string msubGesperrt = string.Empty;
        bool msubAusStore = Jellyfin.Plugin.SubScribe.Registry.TitleFailureStore.IsClosed("moviesubtitles", msubTitelId, out msubGesperrt);
        bool msubTitelZu = _titelGeschlossen.Contains("moviesubtitles|" + msubTitelId) || msubAusStore;

        // F-M619: die Frage „haelt die Sperre?" wird an der Aufrufstelle beantwortet, mit dem Protokoll
        // der Pipeline — eine Zeile je Titel und Lauf, unabhaengig davon, was der Store selbst meldet.
        LogUtil.PerItem(_config.LogMode, _logger,
            "[moviesubtitles] title {Title}: store says closed={Closed} (\"{Reason}\"), run memo={Memo} (F-M619)",
            msubTitelId, msubAusStore, msubGesperrt, _titelGeschlossen.Contains("moviesubtitles|" + msubTitelId));
        if (msubTitelZu)
        {
            if (string.IsNullOrEmpty(msubGesperrt))
            {
                msubGesperrt = "closed in this run";
            }

            LogUtil.PerItem(_config.LogMode, _logger,
                "[moviesubtitles] {Name} [{Lang}] — title {Title} is closed after repeated failed creations ({Reason}), nothing asked (F-M608)",
                neutralName, lang, msubTitelId, msubGesperrt);
            return UploadResult.Skipped("film-source-title-closed");
        }

        var (aufloesung, msubGelungen, msubGeschlossen) = await MitTitelLeiterAsync(
                "moviesubtitles",
                msubTitelId,
                () => _msubMovies.ResolveOrCreateForMediaAsync(Registry, mediaHash, movie, _config.DryRun, ct, mediaPath),
                a => a.Resolved,
                a => a.Status,
                // F-M610: „existiert, Kennung nicht lesbar" ist ein Beleg — Paar zu, Titel offen.
                a => string.Equals(a.Status, Api.Endpoints.MsubMovieStatus.CreatedWithoutId, StringComparison.Ordinal)
                    || string.Equals(a.Status, Api.Endpoints.MsubMovieStatus.ExistsWithoutId, StringComparison.Ordinal),
                // F-M610: „unclear" ist keine Auskunft — nichts zaehlen, nichts schliessen.
                a => string.Equals(a.Status, Api.Endpoints.MsubMovieStatus.Unclear, StringComparison.Ordinal),
                () => _msubMovies.BeginRun(),
                ct)
            .ConfigureAwait(false);
        if (!msubGelungen)
        {
            // F-M610: nur ein inhaltliches Nein schliesst den Titel. Ohne Urteil bleibt alles offen.
            if (!msubGeschlossen)
            {
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[moviesubtitles] {Name} [{Lang}] — title {Title} got no verdict after the ladder ({Status}), nothing closed (F-M610)",
                    neutralName, lang, msubTitelId, aufloesung?.Status ?? "?");
                return UploadResult.NoVerdict("film-source-title-no-verdict");
            }

            LogUtil.PerItem(_config.LogMode, _logger,
                "[moviesubtitles] {Name} [{Lang}] — title {Title} could not be created and found after the ladder ({Status}), nothing sent (F-M608)",
                neutralName, lang, msubTitelId, aufloesung?.Status ?? "?");
            _titelGeschlossen.Add("moviesubtitles|" + msubTitelId);
            return UploadResult.Skipped("film-source-title-closed");
        }

        if (!aufloesung.Resolved)
        {
            // F-M508: KEIN Blocker. Die Zeile nennt den Grund (Status), damit im Lauf steht, warum die
            // Film-Adresse fuer dieses Medium nichts bekommt, und die Kette laeuft weiter.
            //
            // F-M513: die Status nennen jetzt auch die Anlage — `exists-other-year` heisst „der Titel
            // steht da, aber mit einem anderen Jahr, also NICHT anlegen", `created-id-not-readable`
            // heisst „angelegt, aber ohne tvid gibt es keinen Upload". Der Text der Seite steht als
            // Detail-Zeile daneben, nicht in der Kurzzeile.
            LogUtil.PerItem(_config.LogMode, _logger,
                "[moviesubtitles] {Name} [{Lang}] — skipped: no film id ({Status}) (F-M508)",
                neutralName, lang, aufloesung.Status);
            if (!string.IsNullOrEmpty(aufloesung.Answer))
            {
                LogUtil.Detail(_logger, "[moviesubtitles] the site answered: {Answer}", aufloesung.Answer);
            }

            // F-M603 (Operator-Entscheidung 18.11.2026, Weg „A"): „angelegt, aber die Id ist nicht
            // lesbar" ist ein ANGENOMMENER Versuch. Ohne Marke blieb das Paar ewig offen und die Datei
            // kam in jedem Lauf wieder (Oak Street, 18.11.2026).
            // F-M607 (Operator-Anweisung 18.11.2026: „Erst müssen wir den titel neu angelegt haben, wenn wir
            // ihn dann suchen und er taucht nicht auf -> skip, pair schließen."): die Reihenfolge ist
            // Anlegen → Nachsuche. Taucht der Titel in der Nachsuche NICHT auf, ist das Paar entschieden und
            // wird nicht erneut versucht — der Grund steht daneben.
            //
            // Zwei Faelle sind ein Beleg der Seite, dass der Titel dort steht (nur die Kennung fehlt):
            //   created-id-not-readable  — angelegt, Id nicht lesbar
            //   exists-id-not-readable   — die Seite meldet „fuehrt den Titel schon", Id nicht lesbar
            // Beide schliessen das Paar (F-M603), ohne einen Upload zu behaupten. Alles andere
            // (not-found, exists-other-year, create-failed, unclear, no-title, no-year) bleibt ein SKIP und
            // wird von F-M605 als inhaltliches Nein gemerkt — damit ist es ebenfalls geschlossen.
            if (string.Equals(aufloesung.Status, "created-id-not-readable", StringComparison.Ordinal)
                || string.Equals(aufloesung.Status, "exists-id-not-readable", StringComparison.Ordinal))
            {
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[moviesubtitles] {Name} [{Lang}] — the title was created and the search names no id ({Status}); the pair is settled (F-M603/F-M607)",
                    neutralName, lang, aufloesung.Status);
                return UploadResult.AcceptedWithoutId("film-source-movie-" + aufloesung.Status);
            }

            return UploadResult.Skipped("film-source-movie-" + aufloesung.Status);
        }

        // Die zweite Tuer des Trockenlaufs steht im Endpunkt (Vorgabewert true); die Kette reicht ihren
        // eigenen Schalter herein, damit ein Aufruf ohne Konfiguration nichts sendet (F-M483/F-M496).
        var endpoint = new Api.Endpoints.MovieSubtitlesUploadEndpoint(_msubClient, dryRun: _config.DryRun);
        var ticket = new Api.Endpoints.UploadTicket(
            movie.Name,
            imdbId,
            tmdbId,
            Season: 0,
            Episode: 0,
            lang,
            neutralName,
            System.Text.Encoding.UTF8.GetBytes(srtContent),
            quality,
            // F-M505: der RELEASE-NAME, nicht die Gruppe — der Endpunkt (bzw. BuildUpload) schneidet die
            // Gruppe heraus. Stuende hier schon die Gruppe, waere deren „Gruppe" leer und das Feld leer.
            releaseName,
            HearingImpaired: false,
            DoubleEpisode: false,
            SeasonPack: false,
            TvstShowId: null,
            MsubMovieId: aufloesung.MovieId,
            MovieYear: movie.ReleaseYear ?? 0);

            // F-M606 (Operator-Frage 18.11.2026: „Erst fragen ob die id bespielbar ist, vor dem hochladen geht
            // nicht?"): erst die Kennung pruefen, dann senden. Antwortet die Seite nicht mit dem Upload-
            // Formular, wird nichts hochgeladen — der Grund wird gemerkt (F-M605) und die Spur nicht in jedem
            // Lauf neu gefragt.
            if (!_config.DryRun && !await _msubClient
                .FilmUsableForUploadAsync(aufloesung.MovieId ?? 0, ct)
                .ConfigureAwait(false))
            {
            LogUtil.PerItem(_config.LogMode, _logger,
                "[moviesubtitles] {Name} [{Lang}] — the film id is not usable for an upload (no form on add.php?let=…), nothing sent (F-M606)",
                neutralName, lang);
            return UploadResult.Skipped("film-source-id-not-usable");
            }

            var answer = await endpoint.SendAsync(ticket, ct).ConfigureAwait(false);
        LogUtil.PerItem(_config.LogMode, _logger,
            "[moviesubtitles] {Name} [{Lang}] — {Result}{Reason}",
            neutralName, lang,
            answer.Ok ? "accepted" : "refused",
            string.IsNullOrEmpty(answer.Reason) ? string.Empty : ": " + answer.Reason);

        // F-M508: die Antwort der Seite wird durchgereicht, nicht gedeutet. Eine Id nennt sie nicht —
        // dann steht hier „unbekannt" und die Kette vergibt die eigene Marke (own:moviesubtitles:…).
        if (!string.IsNullOrEmpty(answer.Answer))
        {
            LogUtil.Detail(_logger, "[moviesubtitles] answer: {Answer}", answer.Answer);
        }

        LogUtil.PerItem(_config.LogMode, _logger,
            "[moviesubtitles] id of our upload: {Id}",
            answer.Id ?? (answer.Ok ? "not named by the site" : "none"));

        // F-M609: 2xx angenommen; 429/5xx ohne Urteil (Paar bleibt offen); alles andere ist ein Nein.
        if (answer.Ok)
        {
            return UploadResult.Success(answer.Id);
        }

        return IstStoerung(answer.Status)
            ? UploadResult.NoVerdict(answer.Reason ?? "film-source-no-answer")
            : UploadResult.Failed(answer.Reason ?? "film-source-refused");
    }

    /// <summary>
    /// F-M496: die Trockenlauf-Zeile EINER Quelle. Sie steht hier EINMAL, weil der Bericht sonst fuer jede
    /// Quelle eine eigene Zeichenkette haette und die naechste Quelle keine bekaeme.
    /// </summary>
    /// <param name="quelle">Die Quelle, ueber die die Zeile spricht.</param>
    /// <returns>Die Vorlage fuer <see cref="LogUtil.PerItem"/>.</returns>
    private static string DryRunLine(Configuration.SubtitleProvider quelle) =>
        "[" + Api.Endpoints.Provenance.Name(quelle) + "] DRY-RUN would upload {Name} [{Lang}]";

    /// <summary>
    /// F-M488/F-M490/F-M508: die Marke EINER Quelle aus den Ergebnissen der Kette. Hat die Quelle
    /// genommen, ist ihre Marke ihre eigene Id — oder, wenn sie keine nennt, eine selbst vergebene
    /// (<c>own:&lt;quelle&gt;:&lt;Zeit&gt;:&lt;hex&gt;</c>). Hat sie nicht genommen, bleibt beides leer; eine
    /// Marke fuer einen Upload, der nicht stattfand, waere eine Luege im Store.
    /// </summary>
    /// <param name="ergebnisse">Die Ergebnisse der Kette, je Quelle.</param>
    /// <param name="quelle">Die Quelle, deren Marke gefragt ist.</param>
    /// <param name="knownId">Eine bereits bekannte Id (nur SubDL liest sie aus der Antwort).</param>
    /// <param name="contentHash">Inhalts-Hash des Textes (Grundlage der eigenen Marke).</param>
    /// <param name="releaseName">Release-Name (Grundlage der eigenen Marke).</param>
    /// <param name="jetzt">Der Zeitpunkt des Uploads — fuer Id und Stempel derselbe.</param>
    /// <returns>Die Id (oder null) und der Zeitpunkt (oder null).</returns>
    /// <summary>
    /// F-M603: schreibt die Marke EINER Quelle, die den Versuch angenommen, aber keine Kennung genannt hat.
    /// Die uebrigen Quellenfelder bleiben unberuehrt (<c>MarkEmbed</c>/<c>MarkSidecar</c> schreiben nur,
    /// was bekannt ist), und der Status bleibt „uploaded" — der Inhaltsbeleg ist das, was das Paar
    /// schliesst; die Statistik zaehlt den Versuch weiterhin als SKIP, nicht als Upload.
    /// </summary>
    private void MarkAcceptedSource(
        bool isLoose, string? loosePath, string? mediaHash, int subPos, string lang, bool hearingImpaired,
        bool forced, string contentHash, Configuration.SubtitleProvider quelle, string id, DateTime at)
    {
        string? subdl = quelle == Configuration.SubtitleProvider.Subdl ? id : null;
        string? tvst = quelle == Configuration.SubtitleProvider.TvSubtitles ? id : null;
        string? msub = quelle == Configuration.SubtitleProvider.MovieSubtitles ? id : null;
        DateTime? subdlAt = quelle == Configuration.SubtitleProvider.Subdl ? at : null;
        DateTime? tvstAt = quelle == Configuration.SubtitleProvider.TvSubtitles ? at : null;
        DateTime? msubAt = quelle == Configuration.SubtitleProvider.MovieSubtitles ? at : null;

        Registry.MarkAndFlush(() =>
        {
            if (isLoose && !string.IsNullOrEmpty(loosePath))
            {
                Registry.MarkSidecar(
                    contentHash, mediaHash ?? contentHash, lang, hearingImpaired, forced, SubtitleStatus.Uploaded,
                    fileName: Path.GetFileName(loosePath), path: loosePath,
                    subdlUploadId: subdl, tvSubtitlesUploadId: tvst, moviesubtitlesUploadId: msub,
                    subdlUploadedAt: subdlAt, tvSubtitlesUploadedAt: tvstAt, moviesubtitlesUploadedAt: msubAt);
            }
            else
            {
                Registry.MarkEmbed(
                    mediaHash, subPos, lang, hearingImpaired, SubtitleStatus.Uploaded, contentHash: contentHash,
                    subdlUploadId: subdl, tvSubtitlesUploadId: tvst, moviesubtitlesUploadId: msub,
                    subdlUploadedAt: subdlAt, tvSubtitlesUploadedAt: tvstAt, moviesubtitlesUploadedAt: msubAt);
            }
        });
    }

    private static (string? Id, DateTime? At) MarkOf(
        IReadOnlyDictionary<Configuration.SubtitleProvider, UploadResult> ergebnisse,
        Configuration.SubtitleProvider quelle, string? knownId, string contentHash, string releaseName,
        DateTime jetzt)
    {
        // F-M603: auch ein ANGENOMMENER Versuch ohne Id bekommt seine Marke — sonst bleibt das Paar
        // offen und der Lauf wiederholt ihn ewig.
        if (!ergebnisse.TryGetValue(quelle, out var ergebnis) || (!ergebnis.Ok && !ergebnis.SourceAccepted))
        {
            return (null, null);
        }

        var id = knownId ?? ergebnis.SourceId
            ?? Api.Endpoints.Provenance.OwnUploadId(quelle, contentHash, releaseName, jetzt);
        return (id, jetzt);
    }

    /// <summary>
    /// F-M485: sends one subtitle to the SERIES address of the tvst family (tvsubtitles.net).
    /// <para>
    /// F-M501 (Operator-Anweisung 15.11.2026): Name und Jahr kommen jetzt aus TMDb und die
    /// Seriennummer aus dem Store — nicht mehr aus dem Dateinamen. Der Name aus dem Dateinamen war eine
    /// Vermutung über eine Schreibweise (der Uploader hat ihn getippt), und die Nummer der Serie wurde
    /// bei jedem Aufruf neu gesucht, weshalb der Store sie nie lernen konnte. Die Reihenfolge steht in
    /// <see cref="Api.Endpoints.TvstShowResolver"/>: gespeicherte Nummer → eng verglichener TMDb-Titel →
    /// anlegen und erneut suchen. Fehlt am Ende die Nummer, wird tvst übersprungen; die Kette laeuft
    /// weiter, denn die andere Quelle bekommt ihren Versuch unabhaengig davon.
    /// </para>
    /// </summary>
    /// <summary>
    /// F-M572: der Folgentitel, den die Bibliothek kennt („Tombstone" für „Mo" S01E05). Ein Platzhalter
    /// („Episode 5", „Folge 5", leer) geht nicht in den Kommentar — er wäre nur eine zweite falsche
    /// Auskunft neben der, die die Seite selbst schon zeigt.
    /// </summary>
    /// <param name="mediaPath">Pfad der Mediendatei.</param>
    /// <returns>Der Titel, oder null.</returns>
    private string? EpisodeTitleOf(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return null;
        }

        try
        {
            var item = _libraryManager.FindByPath(mediaPath, false);
            string? name = item?.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name) || EpisodenPlatzhalter.IsMatch(name))
            {
                return null;
            }

            return name;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("[Sub Scribe] no episode title for {Path}: {Msg}", mediaPath, ex.Message);
            return null;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex EpisodenPlatzhalter = new(
        @"^(episode|folge|ep\.?)\s*\d+$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private async Task<UploadResult> SendToTvSubtitlesAsync(
        string srtContent, string neutralName, string lang, string releaseName, string quality,
        string? imdbId, string? tmdbId, bool isSeries, int season, int episode, bool hearingImpaired,
        string? mediaHash, string? mediaPath, CancellationToken ct)
    {
        if (!isSeries || season <= 0 || episode <= 0)
        {
            return UploadResult.Skipped("second-source-episodes-only");
        }

        // F-M538: die Seite fuehrt diese Sprache nicht — also wird sie nicht gesendet UND nicht als
        // Fehlschlag gebucht. Vorher antwortete der Endpunkt „nicht angenommen", die Kette machte
        // daraus ein Failed, und das Log meldete fuer dasselbe Paar in jedem Lauf
        // „UPLOAD FAILED …: tvsubtitles.net has no language 'HR'".
        if (Api.TvSubtitlesClient.SiteLanguage(lang) is null)
        {
            LogUtil.PerItem(_config.LogMode, _logger,
                "[tvsubtitles] {Name} [{Lang}] — skipped: the site has no such language (F-M538)",
                neutralName, lang);
            return UploadResult.Skipped("second-source-no-language");
        }

        // F-M501: ohne TMDb-Kennung gibt es keinen fuehrenden Titel — dann wird nicht geraten.
        if (string.IsNullOrWhiteSpace(tmdbId))
        {
            LogUtil.PerItem(_config.LogMode, _logger,
                "[tvsubtitles] {Name} [{Lang}] — skipped: no TMDb id, so there is no leading title (F-M501)",
                neutralName, lang);
            return UploadResult.Skipped("second-source-no-tmdb-id");
        }

        var series = await _tmdb.SeriesInfoAsync(tmdbId!, ct).ConfigureAwait(false);
        if (series is null)
        {
            LogUtil.PerItem(_config.LogMode, _logger,
                "[tvsubtitles] {Name} [{Lang}] — skipped: TMDb answered no series record for {Tmdb} (F-M501)",
                neutralName, lang, tmdbId);
            return UploadResult.Skipped("second-source-no-series-record");
        }

        // F-M608: dieselbe Leiter fuer die Serie — anlegen, nachsuchen, 2/4/8 s, nach dem vierten
        // Fehlschlag sind alle Paare dieser Serie geschlossen.
        string tvstTitelId = !string.IsNullOrWhiteSpace(imdbId) ? imdbId! : (tmdbId ?? mediaHash ?? string.Empty);
        string tvstGesperrt = string.Empty;
        bool tvstAusStore = Jellyfin.Plugin.SubScribe.Registry.TitleFailureStore.IsClosed("tvsubtitles", tvstTitelId, out tvstGesperrt);
        bool tvstTitelZu = _titelGeschlossen.Contains("tvsubtitles|" + tvstTitelId) || tvstAusStore;
        LogUtil.PerItem(_config.LogMode, _logger,
            "[tvsubtitles] title {Title}: store says closed={Closed} (\"{Reason}\"), run memo={Memo} (F-M619)",
            tvstTitelId, tvstAusStore, tvstGesperrt, _titelGeschlossen.Contains("tvsubtitles|" + tvstTitelId));
        if (tvstTitelZu)
        {
            if (string.IsNullOrEmpty(tvstGesperrt))
            {
                tvstGesperrt = "closed in this run";
            }

            LogUtil.PerItem(_config.LogMode, _logger,
                "[tvsubtitles] {Name} [{Lang}] — title {Title} is closed after repeated failed creations ({Reason}), nothing asked (F-M608)",
                neutralName, lang, tvstTitelId, tvstGesperrt);
            return UploadResult.Skipped("second-source-title-closed");
        }

        var (show, tvstGelungen, tvstGeschlossen) = await MitTitelLeiterAsync(
                "tvsubtitles",
                tvstTitelId,
                () => _tvstShows.ResolveForMediaAsync(Registry, mediaHash, series, _config.DryRun, ct, mediaPath),
                s => s.Resolved,
                s => s.Status,
                // F-M610: „angelegt, Kennung unbekannt" ist ein Beleg — Paar zu, Titel offen.
                s => string.Equals(s.Status, Api.Endpoints.TvstShowStatus.CreatedIdUnknown, StringComparison.Ordinal),
                s => string.Equals(s.Status, Api.Endpoints.TvstShowStatus.Unclear, StringComparison.Ordinal),
                () => _tvstShows.BeginRun(),
                ct)
            .ConfigureAwait(false);
        if (!tvstGelungen)
        {
            if (!tvstGeschlossen)
            {
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[tvsubtitles] {Name} [{Lang}] — title {Title} got no verdict after the ladder ({Status}), nothing closed (F-M610)",
                    neutralName, lang, tvstTitelId, show?.Status ?? "?");
                return UploadResult.NoVerdict("second-source-title-no-verdict");
            }

            _titelGeschlossen.Add("tvsubtitles|" + tvstTitelId);
            LogUtil.PerItem(_config.LogMode, _logger,
                "[tvsubtitles] {Name} [{Lang}] — title {Title} could not be created and found after the ladder ({Status}), nothing sent (F-M608)",
                neutralName, lang, tvstTitelId, show?.Status ?? "?");
            return UploadResult.Skipped("second-source-title-closed");
        }

        if (!show.Resolved)
        {
            // F-M603 (Operator-Frage 18.11.2026: „Müsste ja derselbe uploader sein?"): JA — dieselbe
            // Luecke wie bei der Film-Adresse. `created-id-unknown` heisst „die Seite hat die Serie
            // angelegt, die Nummer ist nicht lesbar"; ohne Marke bleibt das Paar fuer tvst offen und
            // die Datei kehrt in jeden Lauf zurueck. Der angenommene Versuch bekommt deshalb seine
            // Marke (Weg „A"), die Statistik zaehlt ihn weiterhin als SKIP.
            if (string.Equals(show.Status, Api.Endpoints.TvstShowStatus.CreatedIdUnknown, StringComparison.Ordinal))
            {
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[tvsubtitles] {Name} [{Lang}] — created, the id is not readable ({Status}); the pair is settled (F-M603)",
                    neutralName, lang, show.Status);
                return UploadResult.AcceptedWithoutId("second-source-show-created-id-unknown");
            }

            // F-M501: KEIN Blocker. Die Zeile nennt den Grund (Status), damit im Lauf steht, warum tvst
            // fuer dieses Medium nichts bekommt, und die Kette laeuft weiter.
            LogUtil.PerItem(_config.LogMode, _logger,
                "[tvsubtitles] {Name} [{Lang}] — skipped: no show id ({Status}) (F-M501)",
                neutralName, lang, show.Status);
            return UploadResult.Skipped("second-source-show-" + show.Status);
        }

        // F-M496 (Operator-Anweisung 15.11.2026): der eigene Schalter der Quelle wird dem Endpunkt
        // mitgegeben, statt `false` zu stehen. F-M483 hat den Endpunkt genau dafuer gebaut ("a dry run
        // builds the same body and sends nothing — which is also the default, so a mis-wired caller
        // cannot upload by accident"); die Kette prueft den Trockenlauf vorher selbst, hier steht die
        // zweite Tuer.
        var endpoint = new Api.Endpoints.TvSubtitlesUploadEndpoint(_tvstClient, dryRun: _config.DryRun);
        var ticket = new Api.Endpoints.UploadTicket(
            series.Name,
            imdbId,
            tmdbId,
            season,
            episode,
            lang,
            neutralName,
            System.Text.Encoding.UTF8.GetBytes(srtContent),
            // F-M498: die Seite kennt nur sieben Rip-Werte; "WEB-DL" aus dem Release-Namen wird
            // hier auf "WEB" gezogen, sonst lehnt sie jede Folge ab.
            TvSubtitlesClient.RipFromQuality(quality),
            // F-M505 (Operator 15.11.2026: "Release on tvst is not perfect"): die Seite fuehrt in
            // ihrer Release-Spalte die GRUPPE, nicht den Dateinamen — 193 Eintraege der ersten
            // Middle-Staffel geprueft: 2HD, REWARD, FQM, FEVER, POW4, CTU, LOL, DIMENSION, 91 leer,
            // kein einziger mit Punkten. Hier stand der Mediendateiname; die Seite schnitt ihn am
            // Bindestrich ab, sodass "The Middle.S01E21.MULTI.1080p.WEB-DL" zu lesen war und die
            // Gruppe (FTMVHD) ganz fehlte. Ohne erkennbare Gruppe bleibt das Feld leer — das ist
            // ein erlaubter Wert und die Mehrheit auf der Seite.
            SeasonPack.ReleaseGroup(releaseName) ?? string.Empty,
            hearingImpaired,
            DoubleEpisode: false,
            SeasonPack: false,
            // F-M501: die aufgeloeste Nummer — der Endpunkt holt die Serienliste damit gar nicht erst.
            TvstShowId: show.ShowId,
            EpisodeTitle: EpisodeTitleOf(mediaPath),
            // F-M574: das Jahr des Titels — bei der Serie das Jahr der ERSTAUSSTRAHLUNG („Mo" 2022), nicht
            // das der Datei.
            MovieYear: series.FirstAirYear ?? 0);

        var answer = await endpoint.SendAsync(ticket, ct).ConfigureAwait(false);
        // LOGGING.md 1.9/1.11: kein leeres Suffix und kein gequotetes Wort — zwei Vorlagen
        // statt eines angehängten Leerstrings, `:l` druckt das Wort ohne Anführungszeichen.
        LogUtil.PerItem(_config.LogMode, _logger,
            string.IsNullOrEmpty(answer.Reason)
                ? "[tvsubtitles] {Name} [{Lang}] — {Result:l}"
                : "[tvsubtitles] {Name} [{Lang}] — {Result:l}: {Reason}",
            neutralName, lang,
            answer.Ok ? "sent" : "refused",
            answer.Reason);
        LogUtil.PerItem(_config.LogMode, _logger,
            "[tvsubtitles] id of our upload: {Id}", answer.Id ?? "none — we write our own marker (F-M612)");
        // F-M609: dasselbe Urteil fuer die Serien-Adresse.
        if (answer.Ok)
        {
            return UploadResult.Success(answer.Id);
        }

        return IstStoerung(answer.Status)
            ? UploadResult.NoVerdict(answer.Reason ?? "second-source-no-answer")
            : UploadResult.Failed(answer.Reason ?? "second-source-refused");
    }

    /// <summary>
    /// F-M495 (Operator-Anweisung 15.11.2026: „beide Endpunkte hintereinander, in der eingestellten
    /// Reihenfolge, und vor dem Start jedes Endpunkts dessen Toggle"): EIN Pruefpunkt fuer „darf diese
    /// Quelle jetzt hochladen".
    ///
    /// Der Upload waehlt sein Ziel nicht mehr am Anfang des Laufs aus (das alte `vonZweiter` kannte
    /// genau eine Quelle); er geht die Quellen nacheinander durch. Damit waere der Toggle an drei
    /// Stellen zu lesen — Kette, Gate und Sammelstufe —, und die naechste Stelle vergisst ihn. Also
    /// fragen alle drei diese Methode und lesen den Schalter nirgends sonst.
    /// </summary>
    /// <param name="provider">Die Quelle, die hochladen soll.</param>
    /// <returns>Wahr, wenn diese Quelle per Upload-Schalter freigeschaltet ist.</returns>
    private bool SourceAllowed(Configuration.SubtitleProvider provider) => _config.IsUploadEnabled(provider);

    /// <summary>
    /// F-M499 (gemessen 15.11.2026, 16:17): die Quellen, die in DIESEM Lauf wirklich antreten — die
    /// Doppelfrage „Schalter UND Konfiguration", die auch die Kette vor dem Start jeder Quelle stellt
    /// (<see cref="SourceAllowed"/>, F-M477, und <see cref="Configuration.PluginConfiguration.IsConfiguredForUpload"/>,
    /// F-M497).
    /// <para>
    /// Sie steht hier EINMAL, weil der Kopf des Laufs sie fuer die Frage „ist diese Datei fuer alle
    /// Quellen fertig" braucht und die Kette fuer die Frage „wer bekommt diesen Untertitel". Zwei
    /// Aufzaehlungen derselben Bedingung waeren zwei Wahrheiten: der Kopf koennte eine Datei als
    /// fertig weglegen, weil eine Quelle fehlt, die die Kette sehr wohl fragen wuerde.
    /// </para>
    /// <para>
    /// Im Trockenlauf gilt die Konfigurationsfrage NICHT (F-M496): er macht keinen Aufruf und braucht
    /// deshalb keine Zugangsdaten — er zeigt nur, was ein echter Lauf taete.
    /// </para>
    /// <para>
    /// F-M509: die Medienart schneidet die Adresse der anderen Art heraus — Serien gehen an
    /// tvsubtitles.net, Filme an moviesubtitles.org. Ohne diesen Schnitt stuende die jeweils fremde
    /// Adresse als „offene Quelle" in der Datei und die Datei wuerde nie fertig. Ist die Medienart nicht
    /// bekannt (<c>null</c>), bleiben beide Adressen stehen: lieber eine Zeile zu viel als eine Datei,
    /// die als fertig gilt, obwohl eine Quelle sie noch nicht hat.
    /// </para>
    /// </summary>
    /// <param name="isSeries">Wahr für eine Serie, falsch für einen Film, null wenn (noch) nicht bekannt.</param>
    /// <returns>Die Quellen dieses Laufs, in der Reihenfolge, in der sie angetreten werden.</returns>
    /// <summary>
    /// F-M550: der Name der Quelle, die geantwortet hat. Kennt das Ergebnis keine (kein Versand
    /// gelungen), steht dort „no source answered" — geraten wird nichts.
    /// </summary>
    /// <param name="result">The upload result.</param>
    /// <returns>The source's log name.</returns>
    private static string QuellName(UploadResult result) =>
        result.Source is { } quelle
            ? Api.Endpoints.Provenance.Name(quelle)
            : "no source answered";

    private List<Configuration.SubtitleProvider> UploadSources(bool? isSeries) =>
        UploadSourceOrder(isSeries)
            .Where(SourceAllowed)
            .Where(quelle => _config.DryRun || _config.IsConfiguredForUpload(quelle))
            .Where(QueuedForSource)
            .ToList();

    /// <summary>
    /// F-M546: hat diese Quelle das aktuelle Item eingereiht? Ohne Meldung (oder bei einem Lauf ohne
    /// Queue je Quelle) bleibt alles wie vorher — die Eingrenzung darf nur weglassen, was ohnehin nicht
    /// gewollt war, nie eine Quelle, die das Item angefordert hat.
    /// </summary>
    /// <param name="quelle">The source in question.</param>
    /// <returns>True when this source may work on the current item.</returns>
    private bool QueuedForSource(Configuration.SubtitleProvider quelle) =>
        _queuedSourcesForItem == null || _queuedSourcesForItem.Contains(quelle);

    /// <summary>
    /// F-M496 (Operator-Anweisung 15.11.2026): die Reihenfolge, in der die Kette die Quellen fragt —
    /// die konfigurierte Download-Reihenfolge, denn eine eigene Einstellung fuer den Upload gibt es
    /// nicht (F-M495). Sie steht hier EINMAL, weil der Trockenlauf sie ebenfalls braucht: zwei
    /// Aufzaehlungen derselben Reihenfolge waeren zwei Wahrheiten, und der Trockenlauf wuerde sie
    /// irgendwann anders erzaehlen als der echte Lauf sie nimmt.
    /// </para>
    /// <para>
    /// F-M504 (Nutzerergaenzung 15.11.2026: „Dritte bis n quelle sind ebenfalls denkbar"): die
    /// Reihenfolge kommt aus <see cref="Api.Endpoints.SourceOrder"/> und nicht mehr aus einem
    /// Zwei-Werte-Vergleich. Hier stand bis jetzt ein Array mit genau zwei Quellen; eine dritte Quelle
    /// waere darin nie angetreten. Die Kette selbst lief schon ueber die Liste — sie hat die neue
    /// Quelle also ohne Aenderung mitgenommen.
    /// </para>
    /// <para>
    /// F-M509 (Operator-Klarstellung 15.11.2026: „Tvst is splitted into tvst and moviesub itself"): die
    /// Reihenfolge gilt auf FAMILIEN-Ebene. „tvst zuerst" heisst bei einer Serie tvsubtitles.net zuerst
    /// und bei einem Film moviesubtitles.org zuerst; SubDL folgt in beiden Faellen. Die Liste hat
    /// deshalb in jeder Medienart genau zwei Eintraege, und die Medienart waehlt die Adresse der Familie
    /// (<see cref="Api.Endpoints.SourceOrder.ForMedia"/>) — keine dritte Position in der Reihenfolge und
    /// keine dritte Einstellung.
    /// </para>
    /// <para>
    /// F-M508: die Liste wird auf die Quellen eingegrenzt, fuer die es einen UPLOAD-Endpunkt gibt
    /// (<see cref="Api.Endpoints.SubtitleEndpointCatalog.HasUploadPath"/>). Der Upload der Film-Adresse
    /// ist seit Etappe 2b verdrahtet — er steht hier also, wenn seine Medienart zutrifft, und der
    /// Schalter der Quelle entscheidet, ob er angetreten wird (und der ist standardmaessig aus).
    /// </para>
    /// </summary>
    /// <param name="isSeries">Wahr für eine Serie, falsch für einen Film, null wenn nicht bekannt.</param>
    /// <returns>Die Quellen in der Reihenfolge, in der sie angetreten werden — ohne Quellen, die keinen Upload-Weg haben.</returns>
    private Configuration.SubtitleProvider[] UploadSourceOrder(bool? isSeries) =>
        (isSeries is { } art
            ? Api.Endpoints.SourceOrder.ForMedia(_config.DownloadOrder, art)
            : Api.Endpoints.SourceOrder.ForMedia(_config.DownloadOrder, isSeries: true)
                .Concat(Api.Endpoints.SourceOrder.ForMedia(_config.DownloadOrder, isSeries: false))
                .Distinct())
            .Where(Api.Endpoints.SubtitleEndpointCatalog.HasUploadPath)
            .ToArray();

    /// <summary>
    /// F-M489: has ANY upload source still to get this pair? Asked per source, because a tvst upload must
    /// not keep the pair out of a SubDL upload and vice versa. Provenance is unknown at collection time
    /// without hashing the content, and unknown provenance is allowed everywhere (F-M487) — that is the
    /// safe direction here: it can only make the pair look MORE in need, never less.
    /// <para>
    /// F-M508: die Film-Adresse der Familie wird mitgefragt. Sie ist die dritte Quelle im Tor, nicht in
    /// der Reihenfolge: die Reihenfolge ordnet zwei Quellen (SubDL und die Familie), das Tor fragt jede
    /// Adresse, die in diesem Build hochladen kann.
    /// </para>
    /// </summary>
    /// <param name="mediaHash">Parent media hash.</param>
    /// <param name="language">Language code.</param>
    /// <param name="hearingImpaired">HI flag of the pair.</param>
    /// <param name="isSeries">Wahr für eine Serie: dann zählt nur die Serien-Adresse der Familie.</param>
    /// <returns>True when at least one enabled source that could take it does not have it yet.</returns>
    /// <summary>
    /// F-M586 (Operator-Anweisung 17.11.2026: „Die spur sollte dann für tvsubtitles oder mst geschlossen
    /// werden, das paar. Unsupported language oder so"): führt diese Quelle die Sprache überhaupt?
    /// <para>
    /// Die beiden Seiten der Familie haben je ein eigenes Sprachset, und ein Code, den das Formular nicht
    /// kennt, fällt dort auf den ersten Eintrag zurück (der Upload landet als Englisch, F-M480/F-M538).
    /// tvsubtitles.net bietet 24 Sprachen; Norwegisch, Slowakisch und Estnisch sind nicht dabei
    /// (gemessen 17.11.2026 am <c>setlang</c>-Select beider Seiten und an den Flaggen-Dateien).
    /// SubDL führt keine solche Tabelle im Plugin und bekommt deshalb keine Einschränkung.
    /// </para>
    /// <para>
    /// Die Frage ist eine reine Eigenschaft von Sprache und Quelle — kein Schalter, kein Store, keine
    /// Herkunft. Sie darf deshalb überall dort gefragt werden, wo eine Quelle als „will das noch"
    /// gezählt wird; vorher stand tvsubtitles für Norwegisch in jedem Lauf in der DUE-Zeile.
    /// </para>
    /// </summary>
    /// <param name="quelle">Die Quelle.</param>
    /// <param name="language">Sprache des Paares.</param>
    /// <param name="isSeries">Wahr für eine Serie, falsch für einen Film, null wenn unbekannt.</param>
    /// <returns>True, wenn die Quelle diese Sprache annehmen kann.</returns>
    public static bool NimmtSprache(Configuration.SubtitleProvider quelle, string? language, bool? isSeries) => quelle switch
    {
        // SubDL fuehrt praktisch jede Sprache; das Plugin hat fuer diese Adresse keine Tabelle.
        Configuration.SubtitleProvider.Subdl => true,
        // F-M509: die Serienseite ist nur fuer Serien zustaendig, die Filmseite nur fuer Filme. Ist die
        // Medienart unbekannt, zaehlt jede Seite, die die Sprache fuehrt — das kann eine Quelle hoechstens
        // WEITER als offen zaehlen, nie eine faelschlich schliessen.
        Configuration.SubtitleProvider.TvSubtitles =>
            Api.TvSubtitlesClient.SiteLanguage(language) is not null && isSeries != false,
        Configuration.SubtitleProvider.MovieSubtitles =>
            Api.MovieSubtitlesClient.SiteLanguage(language) is not null && isSeries != true,
        _ => true,
    };

    private bool AnyUploadSourceStillWants(string? mediaHash, string language, bool hearingImpaired, bool isSeries) =>
        // F-M580 (Operator-Anweisung 17.11.2026: „Es ist ein verdict, paar schließen."): ein gespeichertes
        // Urteil ist ein Endzustand — auch fuer eine SIDECAR-Datei, deren Spruch am Inhalt haengt und
        // nicht an einer Position. Ohne diese Zeile blieb eine Datei, deren einziger Kandidat laengst als
        // „identical subtitle already known" abgelehnt war, fuer immer „still open", wurde in jedem Lauf
        // erneut durchgegangen und zaehlte denselben Spruch erneut als „gestoppt vor dem Senden"
        // (gemessen 17.11.2026: Best.Medicine.S02E06, fuenfmal dieselbe Abfolge).
        !Registry.PairIsDecided(mediaHash, language, hearingImpaired)
        // F-M581 (Operator-Freigabe 17.11.2026): diese Frage prueft nur noch den BELEG. Bis hierher fragte
        // sie zusaetzlich Queue (F-M546) und Sprachset der Seite (F-M538) — und antwortete damit „keiner
        // will es" auch dann, wenn nur die Queue leer oder die Sprache auf der Seite unbekannt war. Der
        // Aufrufer schrieb diese Antwort als `duplicate-self-echo` („schon hochgeladen") in den Store, und
        // weil „abgelehnt" ein Endzustand ist, war das Paar damit fuer immer zu. Gemessen 17.11.2026:
        // 700 solcher Zeilen an 43 Dateien, bei Invasion S01E03/04/08 je 38 Spuren (ET, HR, ES …) — ganze
        // Dateien „erledigt" ohne eine einzige Sendung; 5.114 solche Zeilen im Store.
        // Queue und Sprachset gehoeren in die KETTE: die Kette prueft beides selbst, bevor sie eine Quelle
        // antreten laesst (QueuedForSource im Einreiher, SiteLanguage in SendToTv/ SendToMsub, F-M538).
        && Api.Endpoints.UploadGate.Decide(
            SourceAllowed(Configuration.SubtitleProvider.Subdl),
            // F-M509: nur die Adresse der Familie, die DIESE Medienart fuehrt, kann etwas wollen.
            SourceAllowed(Configuration.SubtitleProvider.TvSubtitles) && isSeries
                && NimmtSprache(Configuration.SubtitleProvider.TvSubtitles, language, isSeries),
            origin: null,
            Registry.IsUploadedTo(mediaHash, language, hearingImpaired, Configuration.SubtitleProvider.Subdl),
            isSeries && Registry.IsUploadedTo(mediaHash, language, hearingImpaired, Configuration.SubtitleProvider.TvSubtitles),
            SourceAllowed(Configuration.SubtitleProvider.MovieSubtitles) && !isSeries
                && NimmtSprache(Configuration.SubtitleProvider.MovieSubtitles, language, isSeries),
            !isSeries && Registry.IsUploadedTo(mediaHash, language, hearingImpaired, Configuration.SubtitleProvider.MovieSubtitles))
        .SkipReason is null;

    /// <summary>
    /// F-M584 (Operator-Anweisung 17.11.2026: „Dann wieder nur für die eigene source vergleichen"):
    /// eine gespeicherte Ablehnung sperrt eine Spur nur, wenn sie für DIESE Quelle gilt.
    /// <para>
    /// <c>duplicate-self-echo</c> heißt „eine Quelle hat das Paar schon". Bis hierher las der Pack-Bauer
    /// nur „abgelehnt" und warf die Spur weg — auch für die Quelle, die das Paar NICHT hat. Gemessen
    /// 17.11.2026 an Hunting Alice Bell S01E01: die EN-HI-Spur stand bei tvsubtitles, war im Store als
    /// <c>duplicate-self-echo</c> vermerkt, und der SubDL-Pack fiel mit „episode 1 cannot join" aus —
    /// obwohl SubDL die Folge nie bekommen hat.
    /// </para>
    /// <para>
    /// Verlangt noch irgendeine eingeschaltete Quelle das Paar (Schalter, Medienart, Beleg — F-M581),
    /// ist die Spur verfügbar. Qualitätsurteile (zu klein, QA, Pack trägt es, Sprache unbekannt,
    /// Extraktion gescheitert) bleiben quellenneutral und sperren weiter für alle.
    /// </para>
    /// </summary>
    /// <param name="mediaHash">Medien-Hash der Datei.</param>
    /// <param name="position">Index der eingebetteten Textspur.</param>
    /// <param name="language">Sprache des Paares.</param>
    /// <param name="hearingImpaired">HI-Merker des Paares.</param>
    /// <param name="isSeries">Wahr für eine Serie.</param>
    /// <returns>True, wenn die Ablehnung die Spur für alle Quellen sperrt.</returns>
    private bool SpurIstGesperrt(string? mediaHash, int position, string language, bool hearingImpaired, bool isSeries)
    {
        if (mediaHash is null)
        {
            return false;
        }

        string? reason = Registry.EmbedRejectedReason(mediaHash, position);
        if (reason is null)
        {
            return false;
        }

        if (reason == RejectReason.SelfEcho
            && AnyUploadSourceStillWants(mediaHash, language, hearingImpaired, isSeries))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// F-M585: traegt der Staffelpack dieser Staffel die Folge? Der Pack-Beleg steht seit F-M585 nur
    /// noch in der <c>packs</c>-Zeile (PackId + <c>Episodes</c>), nicht mehr auf der Folgezeile — die
    /// Folgezeile ist ausschliesslich der Einzel-Upload. Damit sind die beiden Traeger getrennt und
    /// keiner kann den anderen fuer „schon erledigt" erklaeren.
    /// </summary>
    /// <param name="imdbId">Serien-IMDb.</param>
    /// <param name="season">Staffel.</param>
    /// <param name="language">Sprache des Paares.</param>
    /// <param name="hearingImpaired">HI-Variante.</param>
    /// <param name="episode">Folgennummer.</param>
    /// <returns>Wahr, wenn ein Pack dieser Staffel die Folge traegt.</returns>
    private bool CoveredByPack(string? imdbId, int season, string language, bool hearingImpaired, int episode)
    {
        if (string.IsNullOrWhiteSpace(imdbId) || season < 1 || episode < 1)
        {
            return false;
        }

        var pack = Registry.GetPack(imdbId, season, language, hearingImpaired);
        if (pack is null || string.IsNullOrWhiteSpace(pack.Episodes))
        {
            return false;
        }

        return pack.Episodes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(x => int.TryParse(x, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) && n == episode);
    }

    /// <summary>
    /// F-M581: hat IRGENDEINE Quelle dieses Paar nachweislich? Nur dann ist „schon oben" eine wahre
    /// Aussage und darf als <c>duplicate-self-echo</c> im Store stehen.
    /// </summary>
    private bool IrgendeineQuelleHat(string? mediaHash, string language, bool hearingImpaired, bool isSeries) =>
        Registry.IsUploadedTo(mediaHash, language, hearingImpaired, Configuration.SubtitleProvider.Subdl)
        || (isSeries && Registry.IsUploadedTo(mediaHash, language, hearingImpaired, Configuration.SubtitleProvider.TvSubtitles))
        || (!isSeries && Registry.IsUploadedTo(mediaHash, language, hearingImpaired, Configuration.SubtitleProvider.MovieSubtitles));

    /// <summary>
    /// F-M500: was ein Settle-Urteil fuer den Kandidaten bedeutet.
    /// </summary>
    private enum ContentSettle
    {
        /// <summary>Kein Quellenbeleg fuer den Inhalt: die Ablehnung wird geschrieben (quellenneutral, F-M499).</summary>
        Reject,

        /// <summary>Der Inhalt liegt bei mindestens einer Quelle, und keine offene Quelle darf ihn mehr nehmen.</summary>
        Settled,

        /// <summary>
        /// Der Inhalt liegt bei einer Quelle, aber eine andere darf ihn nehmen und hat ihn nicht — der
        /// Kandidat ist NICHT erledigt und geht weiter an die Kette.
        /// </summary>
        DueForAnotherSource
    }

    /// <summary>
    /// F-M500 (gemessen 15.11.2026, 16:24 im scharfen Lauf): entscheidet, was ein Skip-/Settle-Grund mit
    /// einem Kandidaten macht — und zwar JE QUELLE.
    /// <para>
    /// Der Anlass: 27 eingebettete FR-Spuren endeten als
    /// <c>"identical subtitle already uploaded earlier (F-M298)"</c>, weil der Inhalt bei SubDL lag;
    /// tvsubtitles bekam auf diesen Dateien nie einen Versuch, der Lauf meldete
    /// <c>"0 uploaded | 34 rejected, 61 stream(s) skipped"</c>. Das Urteil "schon hochgeladen" war
    /// quellenblind — es las <c>Status=Uploaded</c> und damit "irgendwo hochgeladen".
    /// </para>
    /// <para>
    /// Die Regel steht hier EINMAL, weil alle vier Stellen (Reject-Replay eingebettet und Sidecar,
    /// QA-Dedupe, Skip der Kette) dieselbe Frage stellen. Sie schreibt in jedem Fall genau eine Zeile:
    /// liegt der Inhalt bei Quellen, wird die Spur <c>uploaded</c> mit DIESER Quelle ihrer Marke
    /// (F-M488/F-M490) — eine Quelle ohne den Inhalt bleibt offen. Liegt er nirgends, bleibt es bei der
    /// quellenneutralen Ablehnung (F-M499: eine Ablehnung ist ein Urteil ueber den Untertitel, nicht
    /// ueber den Endpunkt).
    /// </para>
    /// <para>
    /// "Offen" heisst: eingeschaltet und konfiguriert (<see cref="UploadSources"/>, F-M496/F-M497),
    /// ohne den Inhalt, UND die Herkunft erlaubt diese Quelle (<see cref="Api.Endpoints.Provenance.MayUploadTo"/>,
    /// F-M487). Das Herkunftstor wird hier nur GELESEN, nicht nachgebaut: ein von SubDL heruntergeladener
    /// Untertitel bleibt bei tvsubtitles <c>cross-source</c> und bekommt keinen Versuch — auch nicht auf
    /// diesem Weg.
    /// </para>
    /// </summary>
    /// <param name="mediaHash">Parent media hash.</param>
    /// <param name="isLoose">True for a sidecar, false for an embedded position.</param>
    /// <param name="subPos">Embedded stream position.</param>
    /// <param name="lang">Language code.</param>
    /// <param name="hearingImpaired">HI variant.</param>
    /// <param name="forced">Forced flag.</param>
    /// <param name="contentHash">Content hash of the text.</param>
    /// <param name="reason">Skip reason as reported.</param>
    /// <param name="fileName">Sidecar file name.</param>
    /// <param name="path">Sidecar full path.</param>
    /// <returns>Das Urteil und — bei <see cref="ContentSettle.DueForAnotherSource"/> — die offenen Quellen.</returns>
    /// <param name="settleOnAnySkipReason">
    /// True an der Stelle, an der die Kette den Kandidaten fallenlaesst: dort ist der Grund ein SKIP-Grund
    /// (<c>already-uploaded:…</c>, <c>cross-source</c>, <c>not-configured</c>) und kein Urteil ueber den
    /// Untertitel — er darf eine nachweislich hochgeladene Spur nicht als abgelehnt ueberschreiben.
    /// <c>duplicate-remote</c> bleibt ausgenommen: das ist das Urteil des Servers und quellenneutral.
    /// </param>
    private (ContentSettle Verdict, string Open) SettleDuplicateContent(
        string? mediaHash, bool isLoose, int subPos, string lang, bool hearingImpaired, bool forced,
        string? contentHash, string? reason, string? fileName = null, string? path = null,
        bool settleOnAnySkipReason = false)
    {
        void WriteReject() => Registry.MarkAndFlush(() => Registry.RecordSkippedContent(
            mediaHash, isLoose, subPos, lang, hearingImpaired, forced, contentHash, reason,
            Array.Empty<Configuration.SubtitleProvider>(),
            fileName: fileName, path: path));

        // Nur "duplicate-content" ist ein Urteil ueber den INHALT ("wir haben diesen Text schon").
        // Jede andere Ablehnung — auch die des Servers (duplicate-remote) — bleibt quellenneutral.
        // Am Ende der Kette dagegen ist der Grund ein SKIP-Grund und kein Urteil: dort zaehlt der Beleg.
        bool inhaltSurteil = reason == RejectReason.DuplicateContent
            || (settleOnAnySkipReason && reason != RejectReason.DuplicateRemote);
        if (!inhaltSurteil || string.IsNullOrEmpty(contentHash))
        {
            WriteReject();
            return (ContentSettle.Reject, string.Empty);
        }

        // F-M509: hier ist die Medienart nicht verlaesslich bekannt (die Zeile kann aus einer Ablehnung
        // ohne TMDb-Aufloesung stammen). Sie wird nicht geraten: ohne sie zaehlen beide Adressen der
        // Familie. Das kann einen Kandidaten hoechstens WEITER an die Kette schicken, nie einen Beleg
        // verschlucken.
        // F-M586: eine Quelle, die diese Sprache gar nicht führt, ist weder Beleg noch offene Quelle.
        // Vorher zählte tvsubtitles für Norwegisch als „will das noch", und weil „abgelehnt" erst nach
        // dem Beleg greift, stand das Paar in jedem Lauf erneut in der DUE-Zeile (1.316 solcher Zeilen,
        // 104 davon für Norwegisch). Getrennt gezaehlt wird, WER aus Sprachgruenden ausfiel: nur dann
        // traegt das Urteil den Grund `unsupported-language` statt eines Inhaltsgrundes.
        var alleQuellen = UploadSources(isSeries: null);
        var quellen = alleQuellen
            .Where(quelle => NimmtSprache(quelle, lang, isSeries: null))
            .ToList();
        bool nurSpracheAusgeschlossen = quellen.Count < alleQuellen.Count;
        // Ohne eingeschaltete Quelle gibt es keine offene: dann zaehlt jeder Beleg im Bestand, damit ein
        // Lauf mit abgeschaltetem Upload den Inhalt nicht als "nirgends hochgeladen" ablehnt.
        var beleg = (quellen.Count > 0 ? quellen : UploadSourceOrder(isSeries: null).ToList())
            .Where(quelle => Registry.IsContentUploadedFor(contentHash, quelle))
            .ToList();

        if (beleg.Count == 0)
        {
            // F-M582 (Operator-Anweisung 17.11.2026: „Es muss schon zur selben Quelle duplicate sein. Es soll
            // bei tvst hochgeladen werden auch wenn es bei subdl schon vorliegt."): ohne Beleg ist der Text
            // fuer KEINE Quelle ein Duplikat — er ist nur „in dieser Datei nochmal vorhanden". Frueher wurde
            // hier eine quellenneutrale Ablehnung geschrieben und der Kandidat fiel weg; bei SubDL lag der
            // Text, tvst bekam ihn nie.
            // Jetzt gilt: darf irgendeine eingeschaltete Quelle den Text noch nehmen (Herkunft unbekannt),
            // geht der Kandidat an die Kette. Erst wenn keine Quelle ihn nehmen DARF — Herkunft bekannt
            // heisst: heruntergeladen, geht nirgends hoch (F-M487) —, ist das Paar entschieden.
            string? herkunftOhneBeleg = Registry.GetOrigin(contentHash).Origin;
            var darfNoch = quellen
                .Where(quelle => Api.Endpoints.Provenance.MayUploadTo(herkunftOhneBeleg, quelle))
                .ToList();
            if (darfNoch.Count > 0)
            {
                return (ContentSettle.DueForAnotherSource, string.Join(", ", darfNoch.Select(Api.Endpoints.Provenance.Name)));
            }

            // F-M586: bleibt nur die Sprache als Grund uebrig, heisst das Urteil so.
            if (nurSpracheAusgeschlossen)
            {
                reason = RejectReason.UnsupportedLanguage;
            }

            WriteReject();
            return (ContentSettle.Settled, string.Empty);
        }

        // Dieselbe Herkunft, die auch die Kette liest (F-M487) — hier nur als Frage "darf diese Quelle ihn".
        string? herkunft = Registry.GetOrigin(contentHash).Origin;
        var offen = quellen
            .Where(quelle => !beleg.Contains(quelle) && Api.Endpoints.Provenance.MayUploadTo(herkunft, quelle))
            .ToList();

        var belegte = beleg.ToList();
        // F-M586: faellt die letzte offene Quelle nur wegen ihrer Sprachliste weg, traegt das Urteil
        // diesen Grund — das Paar ist dann fuer sie entschieden, nicht „noch offen".
        if (offen.Count == 0 && nurSpracheAusgeschlossen)
        {
            reason = RejectReason.UnsupportedLanguage;
        }

        Registry.MarkAndFlush(() => Registry.RecordSkippedContent(
            mediaHash, isLoose, subPos, lang, hearingImpaired, forced, contentHash, reason, belegte,
            fileName: fileName, path: path));

        return offen.Count == 0
            ? (ContentSettle.Settled, string.Empty)
            : (ContentSettle.DueForAnotherSource, string.Join(", ", offen.Select(Api.Endpoints.Provenance.Name)));
    }
}
