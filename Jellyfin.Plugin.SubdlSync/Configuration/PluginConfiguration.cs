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
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SubdlScribe.Configuration;

/// <summary>
/// Update interval for the scheduled upload task (F-M21).
/// </summary>
public enum UpdateInterval
{
    /// <summary>Never run automatically; dashboard trigger only.</summary>
    Never,

    /// <summary>Manual trigger only (dashboard).</summary>
    Manual,

    /// <summary>Once a day.</summary>
    Daily,

    /// <summary>Twice a day (every 12 hours).</summary>
    TwiceDaily,

    /// <summary>Once a week.</summary>
    Weekly,

    /// <summary>Twice a week (every 3.5 days).</summary>
    TwiceWeekly,

    /// <summary>Once a month.</summary>
    Monthly,

    /// <summary>On arrival: when a new file is added to a selected library (F-M1a).</summary>
    OnArrival
}

/// <summary>
/// (10.09.2026, user decision): OSHash cache revalidation cadence.
/// </summary>
public enum OshashRefreshMode
{
    /// <summary>Trust the cached fingerprint (size+mtime) — only a fingerprint mismatch
    /// recomputes. Zero media reads in the steady state.</summary>
    Never,

    /// <summary>Force one full recompute per cached file per week.</summary>
    Weekly,

    /// <summary>Force one full recompute per cached file per month (default).</summary>
    Monthly,

    /// <summary>Force one full recompute per cached file per year.</summary>
    Yearly
}
/// <summary>
/// (14.09.2026, user decision): database-refresh cadence. The refresh fires on
/// its own diced weekly anchor; Weekly/Monthly/Yearly pick how often that anchor
/// actually fires (every nth week), Never disables it entirely.
/// </summary>
/// F-M210a: `Never` disables, where no manual start button exists.
public enum PruneMode
{
    /// <summary>Database refresh disabled — no scheduled fires, manual dashboard runs only.</summary>
    Never,

    /// <summary>Fire on every weekly anchor.</summary>
    Weekly,

    /// <summary>Fire on every 4th weekly anchor (≈ monthly).</summary>
    Monthly,

    /// <summary>Fire on every 52nd weekly anchor (≈ yearly).</summary>
    Yearly
}


/// <summary>
/// Log verbosity (F-M24a).
/// </summary>
/// F-M224: the plugin's own log mode, independent of the server's level.
public enum LogLevelMode
{
    /// <summary>Summary per run + errors.</summary>
    Normal,

    /// <summary>Per item: QA results, language detection, duplicate check.</summary>
    Verbose,

    /// <summary>API bodies (truncated), ffmpeg args, timings.</summary>
    Debug
}

/// <summary>
/// Plugin configuration for SubDL Scribe (F-M18 – F-M22, F-M24a-c, F-M28-F-M30, F-M34/M35).
/// </summary>
    // Removed requirements (no longer implemented): F-M17, F-M17b, F-M17e, F-M24, F-M24c, F-M31, F-M32, F-M33
    
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        // F-M18: default = NO library selected (explicit opt-in)
        SelectedLibraries = new List<string>();
        // F-M20 (03.10.2026, user decision): the transfer RATE per hour, clamped 100-500. It paces
        // the interval between two real transfers (3600/rate, ±30 %, F-M26) — it is NOT a ceiling.
        // The plugin kept a per-run hourly call bucket here until 03.10.2026 and could stop a run on
        // it; SubDL publishes DAILY counters only, so that budget was our own invention and stopped
        // runs the server would have allowed. Removed.
        // (17.09.2026): default raised 100 -> 400, range 100-500 (user decision, Matrix DM)
        UploadsPerHour = 400;
        // F-M22: default DryRun OFF — user explicitly wants uploads to run live when enabled.
        DryRun = false;
        // F-M24a
        LogMode = LogLevelMode.Normal;
        // F-M34/M35: lists stay EMPTY in the constructor — XmlSerializer APPENDS to
        // constructor-populated lists, doubling entries on every save cycle.
        // Defaults come from the Effective* properties below.
        SkipDirPatterns = new List<string>();
        SkipFilePatterns = new List<string>();
    }

    // F-M34/M35 defaults — comprehensive in-progress markers for BitTorrent clients,
    // browser and download managers (case-insensitive substring match):
    // BitTorrent: .!qB (qBittorrent), .!ut (µTorrent), .bc! (BitComet), .!bt (mainline BitTorrent),
    //             .bt! (BitSpirit), .az! (Vuze/Azureus), .part (generic), .partial, .downloading
    // Browsers:   .crdownload (Chrome), .opdownload (Opera)
    // DDL mgrs:   .ob! (Orbit), .fb!/.jc! (FlashGet), .td (Thunder/Xunlei), .dtapart (DownThemAll), .tmp
    // Junk:       sample/trailer releases
    private static readonly List<string> DefaultSkipDirPatterns = new() { "incomplete", "downloading", ".tmp" };
    private static readonly List<string> DefaultSkipFilePatterns = new()
    {
        "sample", "trailer", "incomplete",
        ".!qb", ".!ut", ".bc!", ".!bt", ".az!", ".bt!", ".part", ".partial", ".downloading",
        ".crdownload", ".opdownload",
        ".ob!", ".fb!", ".jc!", ".td", ".dtapart", ".tmp"
    };

    /// <summary>Gets the effective dir patterns: configured list, or defaults when empty. Computed — not persisted.</summary>
    [System.Xml.Serialization.XmlIgnore]
    public List<string> EffectiveSkipDirPatterns => SkipDirPatterns is { Count: > 0 } ? SkipDirPatterns : DefaultSkipDirPatterns;

    /// <summary>Gets the effective file patterns: configured list, or defaults when empty. Computed — not persisted.</summary>
    [System.Xml.Serialization.XmlIgnore]
    public List<string> EffectiveSkipFilePatterns => SkipFilePatterns is { Count: > 0 } ? SkipFilePatterns : DefaultSkipFilePatterns;

    /// <summary>Gets or sets selected library names (F-M18). Empty = none.</summary>
    public List<string> SelectedLibraries { get; set; }

    /// <summary>Gets or sets the SubDL username (F-M19).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the SubDL password (F-M19). Stored in config (JF-internal).</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Gets or sets the SubDL API key (F-M19, preferred if set).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the transfer rate per hour (F-M20). It sets the pacing interval between two
    /// real transfers (3600/rate, ±30 %) and is the base for the bare-call interval when no
    /// explicit pause is configured. It is a RATE, not a ceiling: nothing counts calls against it
    /// and nothing can be refused for exceeding it.
    /// </summary>
    public int UploadsPerHour { get; set; }

    /// <summary>
    /// (user decision 15.09.2026; range/default confirmed 03.10.2026): explicit pause
    /// between two API calls in seconds (0.1–10, default 0.5). Overrides the derived
    /// 3600/cap spacing when smaller. SubDL allows 600 req/min = 0.1 s spacing, so the
    /// floor is the server's own fastest allowed rate and the range never permits an
    /// unpaused burst.
    /// </summary>
    public double MinCallPauseSec { get; set; } = 0.5;

    /// <summary>
    /// (16.09.2026, user request): follow-up rounds 2..n after round 1,
    /// switchable per direction in the expert area (General tab). TRUE (default)
    /// = after round 1, keep alternating seeded runs while fresh arrivals exist.
    /// FALSE = exactly one round per cycle (single upload / single download run).
    /// </summary>
    public bool FollowUpRoundsUpload { get; set; } = true;
    public bool FollowUpRoundsDownload { get; set; } = true;

    /// <summary>
    /// F-M151/F-M152: the ID quality gate has no switch. It validates and corrects
    /// Jellyfin's IMDb/TMDb ids against TMDb before an upload and before a search,
    /// and it is what makes the TMDb key load-bearing — so it cannot be turned off
    /// without turning off the correctness it provides. A switch existed here and
    /// was read by no code path (removed 01.10.2026): a control that does nothing
    /// is worse than none, because it claims a choice that does not exist.
    /// </summary>
    /// <summary>
    /// Gets or sets a value indicating whether the UPLOAD pipeline also runs on
    /// new media arrivals (user decision 09.09.2026, symmetric to DownloadOnArrival).
    /// Independent of the scheduled rhythm below. DEFAULT ON (user decision 09.09.2026).
    /// </summary>
    public bool UploadOnArrival { get; set; } = true;


    /// <summary>
    /// Gets or sets a value indicating whether the DOWNLOAD pipeline also runs on
    /// new media arrivals (user decision 09.09.2026) — independent of the upload
    /// direction's OnArrival mode; combines with DownloadRefetchInterval (the
    /// per-item refetch gap still applies, new arrivals always pass immediately).
    /// DEFAULT ON (user decision 09.09.2026).
    /// </summary>
    public bool DownloadOnArrival { get; set; } = true;

    /// <summary>
    /// (user decision 10.09.2026): how often the cached OSHash (oshash-cache.json in
    /// the plugin data dir) is distrusted and the media hash recomputed from the file, even when size+mtime match.
    /// Default: Monthly. Never = trust the fingerprint until it mismatches;
    /// Always = pre-F-M88c behaviour (hash every run).
    /// </summary>
    public OshashRefreshMode OshashRefresh { get; set; } = OshashRefreshMode.Monthly;

    // ─── F-M48 (user decision 08.09.2026): per-installation random schedule anchors ───
    // Diced ONCE by Plugin.DiceSchedulerConfig() when empty; never re-rolled by config
    // edits. Format: "HH:mm" (daily), "D HH:mm" with D=1..7 Mon..Sun (weekly),
    // day number 1..28 + "HH:mm" (monthly).
    // NOTE: an additional per-fire jitter (Random.Shared.Next(0, 31) minutes on top of the
    // diced anchor) was described here but is NOT implemented — no call site adds it. The
    // anti-herd function comes from the diced anchor itself plus the recovery offsets
    // (F-M51, F-M182). Do not assume an anchor jitter exists.

    /// <summary>Diced daily fire time, "HH:mm". Empty = not diced yet.</summary>
    public string RandomDailyTime { get; set; } = string.Empty;

    /// <summary>Diced weekly fire time, "D HH:mm" (D=1..7 Mon..Sun). Empty = not diced yet.</summary>
    public string RandomWeeklyTime { get; set; } = string.Empty;

    // ─── (user decision 14.09.2026): database-refresh cadence in the Expert tab —
    // Never disables the anchor fire entirely; Weekly/Monthly/Yearly pick how
    // often the weekly anchor actually fires (every nth week).

    /// <summary>Database refresh cadence: Never/Weekly/Monthly/Yearly.</summary>
    public PruneMode PruneMode { get; set; } = PruneMode.Weekly;

    // ─── (user decision 14.09.2026): separate dice for database refresh + oshash —
    // sharing RandomWeeklyTime made the weekly anchor a designed breaking point
    // (refresh, rehash and refetch fires all at once, one lock). Each maintenance
    // job gets its own diced weekly anchor, same dice-once-never-reroll rules.

    /// <summary>Diced database-refresh anchor, "D HH:mm" (D=1..7 Mon..Sun). Empty = not diced yet.</summary>
    public string RandomPruneTime { get; set; } = string.Empty;

    /// <summary>Diced OSHash-refresh anchor, "D HH:mm" (D=1..7 Mon..Sun). Empty = not diced yet.</summary>
    public string RandomOshashTime { get; set; } = string.Empty;

    /// <summary>Diced monthly day-of-month, 1..28. 0 = not diced yet.</summary>
    public int RandomMonthlyDay { get; set; }

    /// <summary>Diced monthly fire time, "HH:mm". Empty = not diced yet.</summary>
    public string RandomMonthlyTime { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether uploads are suppressed (F-M22).</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets the log verbosity (F-M24a).</summary>
    public LogLevelMode LogMode { get; set; }

    #region Upload settings (F-M18 – F-M22, F-M24a, F-M34/M35)

    /// <summary>Gets or sets directory patterns to skip (F-M34).</summary>
    public List<string> SkipDirPatterns { get; set; }

    /// <summary>Gets or sets filename substring patterns to skip (F-M35).</summary>
    public List<string> SkipFilePatterns { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the upload pipeline is enabled.
    /// Master switch for ALL upload runs (scheduled, manual trigger, on-arrival). Default: off (opt-in).
    /// </summary>
    public bool UploadEnabled { get; set; }

    #endregion

    #region Download pipeline ([D], F-M41–M47)

    /// <summary>
    /// Gets or sets a value indicating whether the download pipeline is enabled (F-M41).
    /// Default true so the plugin works out of the box; users may disable it.
    /// </summary>
    public bool DownloadEnabled { get; set; } = true;

    /// <summary>Gets or sets the target languages, e.g. "EN, FR, ES" (F-M42). Empty = download disabled. (user decision 08.09.2026, Arabic added 30.09.2026): default AR, EN, ES, FR, HI, ZH (alphabetical) — DE/RU/PT were removed deliberately (DE is uploaded by hand, RU/PT have thin coverage).</summary>
    public string DownloadLanguages { get; set; } = "AR, EN, ES, FR, HI, ZH";

    /// <summary>Gets or sets a value indicating whether missing subs are downloaded even if embedded streams exist (F-M41). Default false: only items without the language.</summary>
    public bool DownloadOnlyMissing { get; set; } = true;

    /// <summary>Gets or sets the runtime tolerance in seconds for post-download validation (F-M43 Stufe 2). 0 = disabled. Default 600s to cover intro skip and credits end (subtitles often start late and end before the credits).</summary>
    public int DownloadRuntimeToleranceSec { get; set; } = 600;

    /// <summary>Gets or sets a value indicating whether the hearing-impaired (SDH) variant is additionally downloaded when available (F-M42b). Default off: regular subs only.</summary>
    public bool DownloadHearingImpaired { get; set; }

    /// <summary>Gets or sets the FPS tolerance in percent for the pre-download FPS check (F-M43 Stufe 1). 0 = disabled.</summary>
    public int DownloadFpsTolerancePercent { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether the IMDB/TMDB match is a hard requirement (F-M45).
    /// Default true: no provider id → no download. False: risky title-based search fallback.
    /// </summary>
    public bool DownloadRequireImdb { get; set; } = true;

    /// <summary>Gets or sets the score weight for a release-group match (F-M44 expert mode). Default 1000.</summary>
    public int DownloadScoreWeightGroup { get; set; } = 1000;

    /// <summary>Gets or sets the score weight per overlapping release token (F-M44 expert mode). Default 10.</summary>
    public int DownloadScoreWeightToken { get; set; } = 10;

    /// <summary>Gets or sets the score weight per download count, capped tie-breaker (F-M44 expert mode). Default 1. criterion dropped (v2 has no per-sub download count); property kept for config compatibility, no longer used.</summary>
    public int DownloadScoreWeightDownload { get; set; } = 1;

    /// <summary>Gets or sets the v2 match_score threshold in percent (, user decision 15.09.2026). Main path downloads only candidates with API match_score >= this value. Default 80 (0.80).</summary>

    /// <summary>Gets or sets the download refetch/rhythm interval — per-item refetch gap AND the download rhythm (F-M47/F-M57). DEFAULT: Weekly (user decision 09.09.2026). OBSOLETE since F-M111 — kept for XML compat, replaced by RefetchInterval.</summary>
    public UpdateInterval DownloadRefetchInterval { get; set; } = UpdateInterval.Weekly;

    /// <summary>
    /// Gets or sets THE unified refetch interval (F-M111, user decision 12.09.2026):
    /// one rhythm for both directions — per-item refetch gap AND the scheduled
    /// cycle tick. GUI: General section. Migration: defaults to Weekly.
    /// </summary>
    /// F-M210: every scheduled job is driven by its OWN setting; this one governs ONLY the pipeline
    /// cycles.
    public UpdateInterval RefetchInterval { get; set; } = UpdateInterval.Weekly;

    /// <summary>
    /// Gets or sets the max candidate downloads per (item, language) (F-M50).
    /// Every downloaded candidate costs daily quota even when rejected afterwards
    /// (runtime check) — this cap stops quota-burning candidate walks. 0 = unlimited.
    /// Default: 3.
    /// <para>
    /// Two derived numbers come from this setting, both via <see cref="Pipeline.DownloadBudget"/>:
    /// the loop's cap is raised to the keep-best count (F-M242) when that is higher — a budget
    /// below it would make "keep X saves X files" unreachable — and the search's early-stop
    /// threshold (F-M95) is this same effective value, so the search never fetches fewer
    /// candidates than the loop may try, nor more than it keeps.
    /// </para>
    /// </summary>
    public int DownloadMaxCandidatesPerLanguage { get; set; } = 3;

    /// <summary>
    /// (user decision 11.09.2026): download the best X subtitles per
    /// language instead of only the best one. X = 1 (default) keeps today's
    /// behaviour (one .srt per language, JF shows one track). X &gt; 1 saves the
    /// top X QA-passed candidates per language as numbered sidecar files
    /// ("name.en.02.srt", "name.en.03.srt", ...) — Jellyfin then offers X
    /// selectable tracks for that language. Each saved subtitle still consumes
    /// download quota, so keep X small.
    /// </summary>
    public int DownloadKeepBestPerLanguage { get; set; } = 1;

    /// <summary>
    /// F-M60 (user decision 09.09.2026): max consecutive file-access/extraction
    /// failures per item (file not found, ffmpeg extraction failed) before the
    /// item is skipped as "file-missing" in both pipelines — instead of failing
    /// EVERY run with an error for a file that is gone from disk but not from
    /// the Jellyfin catalog. Applies to BOTH directions (upload + download),
    /// one counter per item shared across directions. A success resets the
    /// counter (move/rename/NAS-comeback case). 0 = never give up (old behaviour).
    /// Default: 3.
    /// </summary>
    public int FileRetryLimit { get; set; } = 3;

    /// <summary>
    /// F-M66 (user decision 09.09.2026): how many consecutive id-resolution
    /// failures (60 s metadata wait + TMDB id→IMDB + TMDB title search all
    /// failed) an item may accumulate before it is skipped without any TMDB
    /// calls until its metadata improves. A resolved id resets the counter.
    /// 0 = never give up (old behaviour: retry the full ladder every run).
    /// Default: 3.
    /// </summary>
    public int IdRetryLimit { get; set; } = 3;

    /// <summary>
    /// (user decision 11.09.2026): how many consecutive QA failures
    /// (structure reject / runtime reject on downloaded candidates) a (item,
    /// language) pair may accumulate before it is skipped without any SubDL
    /// search or fetch until a save succeeds. Rationale: a broken-JF-runtime
    /// item (stub/bad ffprobe) burns up to MaxCandidatesPerLanguage fetches per
    /// refetch cycle forever; the counter ends that loop (live evidence:
    /// 21 fetches/day wasted on test stubs). A saved subtitle resets the
    /// counter. 0 = never give up (old behaviour). Default: 3.
    /// </summary>
    public int DownloadQaRetryLimit { get; set; } = 3;
    /// <summary>
    /// (user decision 11.09.2026): download QA gate — minimum cue count (≥30,
    /// same F-M16 threshold as upload). Catches valid-but-stub SRTs (parseable but
    /// A handful of cues on a 2 h film). Rejects are memorized.
    /// Default: true.
    /// </summary>
    public bool QaDownloadMinCues { get; set; } = true;

    /// <summary>
    /// (user decision 11.09.2026): download QA gate — language verification
    /// (same F-M15 logic as upload: CLD2 content detection vs stream tag,
    /// family exceptions MS/EU/GL/CA/DA/NO). Fail-open when CLD2 has no verdict.
    /// Default: true.
    /// </summary>
    public bool QaDownloadVerifyLanguage { get; set; } = true;

    /// <summary>
    /// F-M296 (development): download auto-sync — shift a fetched subtitle by the
    /// single constant offset measured against the audio, before it is saved.
    /// <para>
    /// A downloaded subtitle is frequently whole seconds off: the release's own
    /// timing does not match this rip. The offset is measurable to about 0.2 s
    /// (planted shifts of −3 / +2 / +4 / +8 / +12 s came back as −3.20 / +1.80 /
    /// +3.80 / +7.80 / +11.80 s), and a large constant shift is NOT mistaken for
    /// drift, which is what makes a correction possible at all.
    /// </para>
    /// <para>
    /// The corrected file is written as usual AND the untouched original is kept
    /// beside it as the one-entry archive <c>&lt;name&gt;.&lt;lang&gt;.srt.unsynced.zip</c>
    /// (F-M306). Registered is the
    /// hash of the CORRECTED file, so the duplicate guard sees exactly what lies on
    /// disk. A file whose offset MOVES is repaired by the staircase of F-M300, not by
    /// a single offset — no average over a drift is ever applied.
    /// </para>
    /// <para>
    /// F-M300 (development): a DRIFTING file is no longer refused. The segments the gate found are
    /// the repair — each cue is shifted by the offset of its own segment, a staircase — which took
    /// the worst-cue residual over 36 drifting episodes from a 10.74 s median to 4.51 s, 33 of 36
    /// better. Two of the 36 come out worse and no reference-free signal separates them, so a
    /// same-language reference (F-M297) is preferred when one exists and the untouched original is
    /// kept either way.
    /// </para>
    /// <para>
    /// On by default. The cost is one full audio decode per saved file — measured at
    /// 11 s wall / 21 s CPU for a 49 min HEVC episode on the Pi 5 — and the run shares
    /// one decode between the drift gate and this switch when both are on. A staircase adds no
    /// second decode; it is the same verdict applied per segment.
    /// </para>
    /// Default: true.
    /// </summary>
    public bool QaDownloadAutoSync { get; set; } = true;

    /// <summary>
    /// F-M296 (development): the audio track an audio-reading gate decodes, chosen by
    /// LANGUAGE rather than by stream order.
    /// <para>
    /// Priority: (1) a track in the subtitle's own language, (2) English, (3) the
    /// first track without a language tag, else the first track. Measured on 304 files
    /// with sidecar subtitles, this picks a different track than the first in 34 of
    /// 387 (file, language) cases (~9 %) — typically an Italian release whose first
    /// track is the Italian dub, with the English original on track 1. 76 files carry
    /// no language tag on their first track at all.
    /// </para>
    /// <para>
    /// The effect on the VERDICT is small (both tracks of the one multi-track file
    /// measured closely agreed: span 17.4 vs. 18.1 s); what the switch removes is the
    /// assumption that the first track is the right one, which is measurably false.
    /// </para>
    /// Default: true.
    /// </summary>
    public bool QaDownloadAudioTrackByLanguage { get; set; } = true;

    /// <summary>
    /// F-M261 (user decision 30.09.2026): allocate missing language codes to the media files
    /// themselves. Expert / General.
    /// <para>
    /// A subtitle track with no language tag, or the literal <c>und</c>/<c>undefined</c>, cannot be
    /// mapped: <see cref="LanguageMapper.MapToSubdl"/> invents the code <c>UN</c> through its
    /// two-letter fallback, and a null tag produces no registry row at all. Either way every reader
    /// of the embedded area is answered from nothing — the language reads as absent for a track
    /// that is right there, and the item is queued and searched for what it already carries.
    /// </para>
    /// <para>
    /// With this on, the seeder resolves those tracks (one ffmpeg pass per file, F-M5), applies the
    /// 2 KB floor and the offline detector (<c>QaGates.DetectLanguage</c>), writes the found
    /// language back into the container as a real track tag, and records the file under its NEW
    /// hash together with the languages it now carries. What the detector cannot decide is left
    /// WITHOUT a tag on purpose: a wrong language would claim coverage the file does not have.
    /// </para>
    /// <para>
    /// This is one switch and not two, because the two halves are not independent: the file was
    /// already re-read to detect the language, and a language written into the file is the only
    /// form other tools can see. The write is what makes the file <em>change identity</em>, so the
    /// gate runs before the hash and the file is re-registered under it (see
    /// <see cref="Registry.ContentHashRegistry.ReplaceMediaIdentity"/>).
    /// </para>
    /// <para>
    /// Default: false. Measured on a 525 MB episode with 44 text tracks: extraction 0.8 s,
    /// detection 13.7 s, tag write 29.9 s — the write dominates because the container is copied
    /// through. It runs once per file and never again, but a first pass over an untagged library
    /// is a long task and must not be the default.
    /// </para>
    /// </summary>
    public bool AllocateMissingLanguageCodes { get; set; }

    /// <summary>
    /// (user decision 11.09.2026): ItemAdded debounce window in minutes —
    /// how long the arrival watcher waits after the first event before running
    /// the cycle (download first, then upload), so that files arriving together
    /// (season import, NAS sync) are processed in ONE cycle.
    /// (user decision 11.09.2026): default back to 5 (10 was too sluggish).
    /// </summary>
    public int ArrivalDebounceMinutes { get; set; } = 5;

    /// <summary>
    /// (user decision 14.09.2026): when a due job (anchor fire, recovery
    /// fire, event batch, prune) finds the global run lock busy, the scheduler
    /// does NOT drop it — it defers the job by this many minutes and retries on
    /// the next tick after the new due time. Default: 15.
    /// </summary>
    public int JobSpacingMinutes { get; set; } = 15;

    /// <summary>
    /// (user request 14.09.2026): number of pages fetched per SubDL API
    /// search (10 results per page server-side). Applies to the download
    /// Search. (user decision 14.09.2026):
    /// default 3 — all wanted languages go in ONE call (server-side filter,
    /// Early-stop) so 3 pages = 30 results is normally plenty; deeper
    /// digging is opt-in. Clamp 1–100.
    /// </summary>
    public int SearchMaxPages { get; set; } = 3;



    /// <summary>Gets or sets a value indicating whether the download pipeline runs in dry-run mode (no file writes, no external subs placed).</summary>
    public bool DownloadDryRun { get; set; } = false;

    /// <summary>
    /// (user decision 20.09.2026): override for the ffmpeg executable path used to
    /// extract embedded text subtitles. Empty means auto-detect: Jellyfin's configured
    /// encoder path first, then "ffmpeg" in PATH, then common system locations.
    /// </summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the DOWNLOAD pipeline waits out a daily
    /// SubDL limit hit and then continues (F-M49). Downloads can hit TWO quotas:
    /// the account-wide API request quota (2,000/day free tier) AND the download
    /// quota (~50/day via direct dl.subdl.com links) — a 429 from either stops
    /// the run when this is off. Default off: the run stops; remaining items keep
    /// their refetch clock and the next scheduled run picks them up. On: the run
    /// waits (once per run, at most 24 h) until the server-reported reset time and
    /// retries the current candidate; a second 429 in the same run stops.
    /// </summary>
    public bool DownloadContinueAfterLimit { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the UPLOAD pipeline waits out the
    /// account-wide SubDL API request quota (2,000/day free tier, HTTP 429) once per
    /// run and continues after the server-reported reset. Uploads draw only from the
    /// API quota (no download quota involved). Default off: the run stops; the next
    /// run picks up the remaining items.
    /// History: DownloadContinueAfterLimit → ContinueAfterDailyLimit (general, 08.09.2026
    /// evening) → split into UploadContinueAfterLimit + DownloadContinueAfterLimit
    /// (user decision: download can hit TWO quotas — API requests AND ~50 downloads/day —
    /// while upload only hits the API quota).
    /// </summary>
    public bool UploadContinueAfterLimit { get; set; } = true;

    #endregion

    /// <summary>
    /// F-M176 (user decision 21.09.2026): cadence for the upload postprocessing task.
    /// Independent from the upload pipeline. Manual disables it entirely.
    /// </summary>
    public UpdateInterval UploadPostprocessInterval { get; set; } = UpdateInterval.Daily;

    /// <summary>
    /// F-M176: diced postprocessing anchor, "D HH:mm" (D=1..7 Mon..Sun). Empty = not diced yet.
    /// </summary>
    public string RandomPostprocessTime { get; set; } = string.Empty;

    /// <summary>
    /// F-M176: minimum spacing in hours between two postprocessing runs. Prevents storms
    /// when the scheduler fires close to an event. Default 1 hour.
    /// </summary>
    public int PostprocessMinSpacingHours { get; set; } = 1;

    /// <summary>
    /// F-M176: max mySubtitles pages fetched per postprocessing run. Default 5.
    /// </summary>
    public int PostprocessMaxPages { get; set; } = 5;

    /// <summary>
    /// F-M176: max runtime in minutes for one postprocessing run. Safety kill-switch.
    /// Default 5 minutes.
    /// </summary>
    public int PostprocessMaxRuntimeMinutes { get; set; } = 5;

    #region QA gates (B2, F-M13–M16) — the release-aware duplicate pre-check (F-M17a) lives in postprocessing (F-M184)

    /// <summary>
    /// Gets or sets a value indicating whether SRT parser validation runs before upload (F-M13):
    /// valid cue timings, monotonically growing start timestamps, plausible cue durations.
    /// Default on (user decision 08.09.2026 — all QA gates on by default).
    /// </summary>
    public bool QaValidateSrt { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether sync plausibility runs before upload (F-M14):
    /// cue span vs. item runtime, concentration check. Default on.
    /// </summary>
    public bool QaCheckSync { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether language verification runs before upload (F-M15):
    /// detected content language vs. stream tag; mismatch → skip. Default on.
    /// </summary>
    public bool QaVerifyLanguage { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the minimum-size gate is enforced beyond the
    /// hard 2 KB floor (F-M16): ≥30 cues required. Default on — the hard 2 KB floor
    /// stays active independently of this flag.
    /// </summary>
    public bool QaMinCues { get; set; } = true;

    /// <summary>
    /// Legacy property — DEAD. The release-aware duplicate pre-check it used to gate was
    /// removed (F-M184); duplicates are handled by the postprocessing job. No code path
    /// reads this value any more: the one remaining caller (the local self-echo guard in
    /// the upload pipeline) is unconditional. Kept only so old config files deserialize
    /// without error.
    /// </summary>
    [Obsolete("Removed with the duplicate pre-check (F-M184). Not read by any code path.")]
    public bool QaDuplicateCheck
    {
        get => true;
        set { /* ignored */ }
    }

    /// <summary>
    /// F-M19/F-M203: the four credentials the plugin cannot run without, in ONE place
    /// so both pipelines and the settings page answer the same question.
    /// <para>
    /// They are not alternatives: the SubDL login pair authenticates the UPLOAD (its
    /// three steps carry a Bearer token from /login), the SubDL API key authenticates
    /// search, file download and the quota read, and the TMDb key is what resolves and
    /// corrects the ids in both directions. A missing one is not a degraded mode, it is
    /// a run that cannot do its job — so the run is refused up front with the field
    /// named, instead of failing later at the first call that needs it.
    /// </para>
    /// </summary>
    /// <returns>The human-readable labels of every missing credential; empty when complete.</returns>
    public List<string> MissingCredentials()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Username))
        {
            missing.Add("SubDL email");
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            missing.Add("SubDL password");
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            missing.Add("SubDL API key");
        }

        if (string.IsNullOrWhiteSpace(TmdbApiKey))
        {
            missing.Add("TMDb API key (v3)");
        }

        return missing;
    }

    /// <summary>
    /// Gets or sets the REQUIRED TMDB API key (v3), the TMDb-authoritative source for
    /// id resolution (F-M202/F-M203, user decision 25.09.2026). It resolves missing
    /// IMDb/TMDb ids, corrects Jellyfin's ids, and decides film vs series.
    /// Empty/null → nothing runs: a run without the key is refused up front (F-M203),
    /// because the id resolution it feeds is not optional in either direction. The GUI
    /// refuses to save an empty key.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Legacy property (F-M17f): the own-account mySubtitles fetch that ran before
    /// upload was removed on 08.09.2026 — own uploads already appear in the normal
    /// search results, so the extra fetch was redundant. Kept for deserialization
    /// only; the value is no longer read by any code path. Since F-M184 the duplicate
    /// decision happens in postprocessing (mySubtitles sweep).
    /// </summary>
    public bool QaOwnUploadCheck { get; set; } = true;

    /// <summary>
    /// F-M74 (user decision 10.09.2026): resolve 'und' (undefined-language) subtitle
    /// streams via language detection before upload. ON: the detected language replaces
    /// the tag and the stream uploads normally; detection failure → skip. OFF: every
    /// 'und' stream is removed from the upload set (skip, logged). Default: on.
    /// </summary>
    public bool UploadResolveUnd { get; set; } = true;

    #endregion

    #region Status counters (F-M23/M24): cumulative, persisted in the plugin config

    /// <summary>Gets or sets the total subtitles uploaded (all runs).</summary>
    public long StatusUploaded { get; set; }

    /// <summary>Gets or sets the total subtitles downloaded (all runs).</summary>
    public long StatusDownloaded { get; set; }

    /// <summary>Statistics period start (UTC) — set on "Reset statistics",
    /// shown as "since" in the one-line stats display.</summary>
    public DateTime? StatusStatsSinceUtc { get; set; }

    /// <summary>
    /// True once the cumulative counters were adopted into the database (25.09.2026).
    /// <para>
    /// Guards the one-time migration. Without this flag a restart after a database reset would
    /// re-import the old XML totals into the freshly emptied data file, and the counters would
    /// silently jump back up — exactly the drift the move to the database is meant to remove.
    /// The flag lives here because the configuration survives a database reset while the data
    /// file does not.
    /// </para>
    /// </summary>
    public bool StatusCountersMigratedToDb { get; set; }
    #endregion
}