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

namespace Jellyfin.Plugin.SubdlScribe.Data;

/// <summary>
/// The four states a subtitle can be in. Everything else is derived.
/// <para>
/// There is deliberately no "settled"/"done" state: "is this position finished?" is answered by
/// the presence of one of the three stored states below. The previous model carried a separate
/// settled row next to the outcome row, which duplicated every verdict and forced every reader
/// to know both layers.
/// </para>
/// </summary>
public static class SubtitleStatus
{
    /// <summary>The subtitle was uploaded to SubDL.</summary>
    public const string Uploaded = "uploaded";

    /// <summary>The subtitle was not accepted. <see cref="SubtitleState.Reason"/> says by whom and why.</summary>
    public const string Rejected = "rejected";

    /// <summary>The subtitle was downloaded from SubDL.</summary>
    public const string Downloaded = "downloaded";

    /// <summary>
    /// F-M257: the track was SEEN, no verdict exists. Written by whoever reads the streams or the
    /// directory without doing anything about it (seeder scan, download pipeline) — it records the
    /// file fact (language, HI) and deliberately claims nothing else.
    /// <para>
    /// Needed because the embedded area had exactly ONE writer (the upload pipeline), so with
    /// <c>UploadEnabled=false</c> — the shipped default — no embedded row existed at all. A reader
    /// that depends on that area (the HI question, F-M254) then answered "HI absent" forever for a
    /// container that already carried the track, and nothing could ever correct it.
    /// </para>
    /// <para>
    /// NOT terminal by design: <see cref="SubtitleStatus.Uploaded"/> and
    /// <see cref="SubtitleStatus.Rejected"/> are verdicts that close a position, an observation is
    /// not. Every terminality check must keep filtering for the verdict values, otherwise this state
    /// would mark work as done that was never attempted.
    /// </para>
    /// </summary>
    public const string Observed = "observed";

    /// <summary>Nothing recorded yet. Never stored — absence of a row.</summary>
    public const string Pending = "pending";
}

/// <summary>
/// Why a subtitle was rejected. Stored in <see cref="SubtitleState.Reason"/> so the verdict itself
/// stays one state (<see cref="SubtitleStatus.Rejected"/>).
/// <para>
/// The distinction that matters operationally is WHO rejected: a remote rejection is final and
/// unactionable, a local quality rejection means the file itself could be improved, and a
/// self-echo rejection only says "we already uploaded this pair in this run".
/// </para>
/// </summary>
public static class RejectReason
{
    // --- rejected by SubDL (remote, final) ---
    /// <summary>SubDL already holds this exact subtitle content.</summary>
    public const string DuplicateRemote = "duplicate-remote";

    /// <summary>SubDL reports identical content.</summary>
    public const string DuplicateContent = "duplicate-content";

    // --- rejected by our own quality gates ---
    /// <summary>Below the 2 KB floor.</summary>
    public const string TooSmall = "too-small";

    /// <summary>Fewer than 30 cues.</summary>
    public const string TooFewCues = "too-few-cues";

    /// <summary>Detected content language does not match the tag (suffix carries the detail).</summary>
    public const string LanguageMismatch = "lang-mismatch";

    /// <summary>Timings unparseable, non-monotonic or implausible.</summary>
    public const string BadStructure = "bad-structure";

    /// <summary>Cue span does not match the item runtime.</summary>
    public const string RuntimeMismatch = "runtime-mismatch";

    // --- rejected because the stream carries no usable language tag ---
    /// <summary>Untagged stream, resolve-und disabled.</summary>
    public const string UndOff = "und-off";

    /// <summary>Untagged stream, too little text to detect a language.</summary>
    public const string UndTooSmall = "und-too-small";

    /// <summary>Untagged stream, language could not be detected.</summary>
    public const string UndDetectionFailed = "und-detection-failed";

    /// <summary>Language tag could not be mapped to a SubDL language.</summary>
    public const string UnmappedLanguage = "unmapped-language";

    // --- rejected by our own self-echo guard ---
    /// <summary>This (file, language, HI) pair was already uploaded in this run.</summary>
    public const string SelfEcho = "duplicate-self-echo";

    // --- download side: a candidate was fetched, screened and discarded ---
    /// <summary>A download candidate was fetched and failed a gate. The gate is the reason suffix.</summary>
    public const string CandidateRejected = "candidate-rejected";
}

/// <summary>
/// Compatibility record for the database itself (area 0). Exactly one row, updated on startup.
/// <para>
/// Read at startup to answer "can I safely touch this file?". A data file written by a NEWER
/// plugin version than the one running must not be modified — the newer version may rely on
/// fields this build does not know, and writing would silently drop them.
/// </para>
/// </summary>
/// F-M195b: structural changes are NOT migrated. An unrecognised schema version is ignored
/// rather than rewritten; the database reset is the intended path.
public class MetaEntity
{
    /// <summary>Fixed primary key.</summary>
    public string Id { get; set; } = "db";

    /// <summary>
    /// Structure version of this data file. Bumped by hand whenever the shape of the stored
    /// documents changes in a way an older build could not handle.
    /// </summary>
    // F-M195: raised by hand whenever the stored shape changes; a higher value disables writing.
    public int SchemaVersion { get; set; }

    /// <summary>Plugin version that wrote this file last.</summary>
    public string? PluginVersion { get; set; }

    /// <summary>Jellyfin server version, for diagnosing API-related breakage.</summary>
    public string? JellyfinVersion { get; set; }

    /// <summary>When this record was last written.</summary>
    public DateTime Updated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Area 2: one row per video file, keyed by the stable OSHash/media hash.
/// <para>
/// The embedded subtitle streams of this file live in <see cref="EmbedTrackEntity"/> with this
/// hash as their parent — they have no existence of their own, which is why they are stored
/// beneath the file rather than beside it.
/// </para>
/// </summary>
/// F-M196: one record per video file, keyed by the media hash (OSHash). For a series episode
/// the stored ids are the SHOW ids, never episode or season ids.
/// F-M38: state is split by the kind of thing described - embeds live under their video file,
/// sidecars carry their own record, and the file record holds path, ids and the derived aggregates.
public class MediaEntity
{
    /// <summary>Primary key: OSHash/media content hash (16 hex chars).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Latest known absolute path to the media file.</summary>
    public string? Path { get; set; }

    /// <summary>Jellyfin item ID, when available.</summary>
    public string? JellyfinItemId { get; set; }

    /// <summary>IMDb ID (ttNNNNNNNN). For series this is the SHOW id, never an episode id.</summary>
    public string? ImdbId { get; set; }

    /// <summary>TMDb ID. For series this is the SHOW id, never an episode or season id.</summary>
    public string? TmdbId { get; set; }

    /// <summary>SubDL release ID (sd_id) when known.</summary>
    public string? SdId { get; set; }

    /// <summary>True for series episodes, false for movies.</summary>
    public bool IsSeries { get; set; }

    /// <summary>Season number (series only).</summary>
    public int? Season { get; set; }

    /// <summary>Episode number (series only).</summary>
    public int? Episode { get; set; }

    /// <summary>Languages present in this file, comma separated and sorted. Derived from the embedded tracks.</summary>
    public string? LanguagesAvailable { get; set; }

    /// <summary>
    /// When every embedded subtitle of this file reached a terminal state (uploaded or rejected).
    /// <para>
    /// Explicitly NOT "the video file was uploaded" — the video file never leaves this machine.
    /// Only subtitle data travels to SubDL.
    /// </para>
    /// </summary>
    // F-M88c: the upload-side completion mark; a file whose embedded side is complete is skipped.
    public DateTime? SubtitlesUploadedAt { get; set; }

    // F-M283 (user decision 02.10.2026): the download-side completion mark is GONE.
    //
    // `SubtitlesDownloadedAt` (a timestamp) and `SubtitlesDownloadedLanguages` (a comma-separated
    // language list, later carrying `:hi` tokens) used to sit here. They were a second truth beside
    // the rows that describe the subtitles, and the two drifted: the reader that decided whether to
    // WRITE the mark and the reader that trusted it asked for different evidence, so a variant
    // embedded in the container kept the mark from ever being written and the item was re-downloaded
    // on every pass (41 downloads in one run against a 50/day limit, 35 marks withheld, the same
    // content hash for three consecutive days).
    //
    // Completeness is derived instead, on every ask, from the files and the embedded rows
    // (`Registry.SubtitleCoverage`). Nothing needs invalidating when a subtitle is deleted because
    // nothing was ever stored as "done" — and the hearing-impaired variant is judged as what it is:
    // its own subtitle datum, with its own language and its own flag.

    /// <summary>Last time a SubDL search ran for this file.</summary>
    public DateTime? LastSearchUtc { get; set; }

    /// <summary>Target-language list the last search was made for.</summary>
    public string? LastSearchLanguages { get; set; }

    /// <summary>Last time this media record was seen/updated.</summary>
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Shared state fields of a subtitle record, in both areas 2 and 3.
/// </summary>
public abstract class SubtitleState
{
    /// <summary>Two or three letter language code.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Hearing-impaired / SDH variant.</summary>
    public bool HearingImpaired { get; set; }

    /// <summary>
    /// F-M284 (user decision 02.10.2026): the track is FORCED — it carries the lines of
    /// foreign-language scenes, not the film's dialogue.
    /// <para>
    /// An Eigenschaft OF THIS DATUM, exactly like <see cref="HearingImpaired"/>: a forced subtitle
    /// is its own row under its own hash, never a flag on the media file. It never counts as
    /// coverage, so a language whose only track is forced stays open and is searched for (F-M246).
    /// </para>
    /// <para>
    /// Unlike the hearing-impaired flag it is not bilateral: Jellyfin reports it on its streams, but
    /// SubDL has no forced counterpart — no search filter, no response field, and 0 of 22 candidates
    /// named it when measured live on 02.10.2026. So a forced datum is OBSERVED, never sought,
    /// fetched or uploaded. Recording it is what stops it from being mistaken for the film's
    /// dialogue.
    /// </para>
    /// </summary>
    public bool Forced { get; set; }

    /// <summary>MD5 of the normalized SRT content, in SubDL's own format. Null when the content was never read.</summary>
    public string? ContentHash { get; set; }

    /// <summary>One of <see cref="SubtitleStatus"/>. Absence of a row means pending.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Rejection reason (one of <see cref="RejectReason"/>, suffixes allowed), else null.</summary>
    public string? Reason { get; set; }

    /// <summary>When <see cref="Status"/> was last set.</summary>
    public DateTime StatusAt { get; set; } = DateTime.UtcNow;

    /// <summary>SubDL upload or download id this verdict refers to, when known.</summary>
    public string? SubdlId { get; set; }
}

/// <summary>
/// Area 2, beneath the file: one row per embedded subtitle stream.
/// <para>
/// The key is (media hash, stream position) because that IS the identity of an embedded track —
/// it has no name and no independent existence. Two streams of one file can share a language
/// (a normal and an SDH variant, or two releases), so the language alone must never identify the
/// row; conflating them is what made one rejected stream block all its siblings.
/// </para>
/// </summary>
/// F-M197: one record per embedded subtitle stream, keyed by media hash + stream position.
/// The position is the identity of a track: it has no name of its own, and two streams of one
/// file can share a language (a normal and an SDH variant).
public class EmbedTrackEntity : SubtitleState
{
    /// <summary>
    /// Primary key: "&lt;media hash&gt;|&lt;sub position&gt;". A stable business key rather than a
    /// database-assigned id, so an upsert always updates the row it means to update.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Parent media hash.</summary>
    public string MediaHash { get; set; } = string.Empty;

    /// <summary>ffmpeg stream position (0:s:N).</summary>
    public int SubPos { get; set; }

    /// <summary>
    /// When language DETECTION last ran for this position, or null when it never did.
    /// <para>
    /// Detection is the expensive half of the gate (extraction plus classification), and for a
    /// track the detector cannot classify the answer never changes. Recording the attempt is what
    /// lets the next scan skip it instead of re-opening the media file: the seeder's cheap
    /// pre-check only asks whether an untagged track EXISTS, and a track that stays untagged
    /// passes that question forever. Per TRACK, not per file — a file can have one unresolved
    /// track beside forty settled ones.
    /// </para>
    /// <para>
    /// A tag write gives the file a new hash and therefore fresh rows, which is the one event
    /// that should retry. Set by the gate, read by the gate and by the refresh worker.
    /// </para>
    /// </summary>
    public DateTime? DetectionAttemptedAt { get; set; }

    /// <summary>How the last detection attempt ended — one of <see cref="DetectionOutcome"/>, else null.</summary>
    public string? DetectionOutcome { get; set; }
}

/// <summary>
/// How a per-track language detection attempt ended. Stored on the embedded track row alongside
/// <see cref="EmbedTrackEntity.DetectionAttemptedAt"/>.
/// </summary>
public static class DetectionOutcome
{
    /// <summary>A language was detected and written into the container.</summary>
    public const string Resolved = "resolved";

    /// <summary>The track held no text at all, so there was nothing to classify.</summary>
    public const string NoText = "no-text";

    /// <summary>The track held less text than the 2 KB floor — a verdict would be garbage.</summary>
    public const string TooSmall = "too-small";

    /// <summary>The classifier returned no confident verdict for the text it was given.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// Area 3: one row per loose .srt sidecar file.
/// <para>
/// Keyed by the normalized CONTENT hash rather than by name or language. That is what makes a
/// sidecar an independent thing: the same subtitle text stays "known" when the file is renamed
/// or moved, and two sidecars that share a language but differ in content stay two rows instead
/// of collapsing into one. The filename is kept for reference only and deliberately plays no part
/// in the identity.
/// </para>
/// </summary>
/// F-M199: one record per loose .srt file, keyed by the normalized content hash (F-M186), so a
/// sidecar stays known after a rename or a move, and two sidecars differing in text stay two rows.
public class SidecarEntity : SubtitleState
{
    /// <summary>Primary key: MD5 of the normalized content.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>File name (not the full path) of the sidecar, for diagnostics.</summary>
    public string? FileName { get; set; }

    /// <summary>Media hash of the file this sidecar sits next to.</summary>
    public string MediaHash { get; set; } = string.Empty;

    /// <summary>Full path of the sidecar as last seen, for diagnostics.</summary>
    public string? Path { get; set; }

    /// <summary>IMDb id of the media, for lookups that do not know the media hash.</summary>
    public string? ImdbId { get; set; }

    /// <summary>TMDb id of the media (show id for series).</summary>
    public string? TmdbId { get; set; }

    /// <summary>True when the parent file is a series episode.</summary>
    public bool IsSeries { get; set; }

    /// <summary>Season number of the parent file (series only).</summary>
    public int? Season { get; set; }

    /// <summary>Episode number of the parent file (series only).</summary>
    public int? Episode { get; set; }
}

/// <summary>
/// Area 4: download candidates that were fetched and then discarded, remembered per
/// (item, language) so the same candidate is never fetched and screened twice.
/// <para>
/// Kept apart from areas 2 and 3 because its grain is neither a stream nor a piece of content —
/// it is "this remote release id was burned for this file and language". SubDL's own id is the
/// only identifier such a verdict has.
/// </para>
/// </summary>
/// F-M200: one record per fetched-and-discarded candidate, keyed by item id + language + SubDL id.
/// Its grain is "this remote release was burned for this file and language".
public class RejectedCandidateEntity
{
    /// <summary>Primary key: "&lt;item id&gt;|&lt;language&gt;|&lt;subdl id&gt;".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Jellyfin item id the candidate was fetched for.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Language the candidate was fetched for.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>SubDL release id that was burned.</summary>
    public string SubdlId { get; set; } = string.Empty;

    /// <summary>Why it was discarded (one of <see cref="RejectReason"/>, suffixes allowed).</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>When the candidate was discarded.</summary>
    public DateTime Updated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Area 1: OSHash cache entry mapping a media path to its computed hash.
/// <para>
/// Pure cache — losing it costs a recomputation and nothing else, which is why it is keyed by
/// path while every other area is keyed by a hash.
/// </para>
/// </summary>
public class OshashEntity
{
    /// <summary>Absolute media path as primary key.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Computed OSHash value.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>File size at time of hashing.</summary>
    public long Size { get; set; }

    /// <summary>Last hash computation timestamp.</summary>
    public DateTime Updated { get; set; } = DateTime.UtcNow;

    /// <summary>File mtime (unix seconds) the hash was computed against.</summary>
    public long MtimeUnix { get; set; }
}

/// <summary>
/// Generic key/value counters with optional expiration (retry and QA-fail budgets).
/// </summary>
public class CounterEntity
{
    /// <summary>LiteDB auto-id.</summary>
    public int Id { get; set; }

    /// <summary>Counter key, namespaced, e.g. "not-found:tt1234567".</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Current integer value.</summary>
    public int Value { get; set; }

    /// <summary>Optional expiration (UTC). Null = never.</summary>
    public DateTime? Expires { get; set; }

    /// <summary>Last update timestamp.</summary>
    public DateTime Updated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// High-level pipeline run bookkeeping.
/// </summary>
public class PipelineRunEntity
{
    /// <summary>LiteDB auto-id.</summary>
    public int Id { get; set; }

    /// <summary>Direction: download, upload or both.</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>Trigger source: scheduled, manual, event.</summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>Run start time.</summary>
    public DateTime Started { get; set; } = DateTime.UtcNow;

    /// <summary>Run end time; null while running.</summary>
    public DateTime? Ended { get; set; }

    /// <summary>Number of items queued.</summary>
    public int ItemsTotal { get; set; }

    /// <summary>Number of items terminal.</summary>
    public int ItemsDone { get; set; }
}

/// <summary>
/// Cumulative subtitle counters shown in the configuration GUI.
/// <para>
/// These used to live in the plugin configuration XML (F-M23), which meant they were stored next to
/// user settings while the data they describe lives in the database — so a database reset left them
/// untouched and the display drifted away from the data it claimed to report (measured 25.09.2026:
/// after a reset the database held 311 uploaded rows while the display still said 743). They now live
/// in the database, which makes them part of the same reset and incapable of drifting.
/// </para>
/// <para>
/// Single row, fixed key <c>"status"</c> — this is a running total, not a per-item record.
/// </para>
/// </summary>
public class StatusStatsEntity
{
    /// <summary>Fixed primary key.</summary>
    public string Id { get; set; } = "status";

    /// <summary>Total subtitles uploaded since <see cref="SinceUtc"/>.</summary>
    public long Uploaded { get; set; }

    /// <summary>Total subtitles downloaded since <see cref="SinceUtc"/>.</summary>
    public long Downloaded { get; set; }

    // F-M218 (27.09.2026, user decision): cumulative quality counters next to the two
    // volume counters. A field per counter rather than a generic key/value row — the set
    // is deliberately small, fixed and read by the GUI, so fields keep it typed,
    // greppable and impossible to misspell at runtime.

    /// <summary>Items whose type/season/episode came from the FILE NAME instead of Jellyfin (F-M217).</summary>
    public long TypeCorrectedByFileName { get; set; }

    /// <summary>TMDb title searches that only matched after the year filter was dropped (F-M217).</summary>
    public long TmdbYearFilterMisses { get; set; }

    /// <summary>Download candidates a QA gate rejected (language/structure/min-cues/runtime).</summary>
    public long QaRejectedDownload { get; set; }

    /// <summary>Upload candidates a QA gate rejected (language/structure/runtime).</summary>
    public long QaRejectedUpload { get; set; }

    /// <summary>Start of the counting period; set by "Reset statistics", null before first use.</summary>
    public DateTime? SinceUtc { get; set; }

    /// <summary>Last time a counter changed.</summary>
    public DateTime Updated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The LAST run of one worker (scheduled task), one row per worker.
/// <para>
/// Deliberately not a run HISTORY: the key is the worker's own task key, so a new run overwrites the
/// previous row and the area can never grow. A history would need a retention policy and a prune
/// path; the question this answers — "when did each worker last run, and how did it end?" — needs
/// exactly one row per worker.
/// </para>
/// <para>
/// Stored in the DATA FILE rather than in memory: the previous status lived in static fields, so a
/// Jellyfin restart reset it to "never run" and the GUI claimed a task had never executed minutes
/// after it had (measured 30.09.2026). A database row survives a restart and is wiped by a database
/// reset, which is the honest scope for it.
/// </para>
/// </summary>
public class WorkerRunEntity
{
    /// <summary>The worker's task key — the row's own primary key, so it can never duplicate.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name of the worker as Jellyfin shows it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Run start (UTC); null for a worker that has not run yet.</summary>
    public DateTime? Started { get; set; }

    /// <summary>Run end (UTC); null while the worker is still running.</summary>
    public DateTime? Ended { get; set; }

    /// <summary>
    /// How the run ended: <c>ok</c>, <c>failed</c>, <c>cancelled</c>, <c>skipped</c> (disabled or
    /// deferred by the global run lock) or <c>never</c> for a worker that has not run yet.
    /// <para>
    /// One word, and the GUI shows it as-is. It used to render as a sentence plus a parenthesised
    /// detail, which the user rejected outright: "ein worker, eine ampel, ein datum, ein end status"
    /// (30.09.2026).
    /// </para>
    /// </summary>
    public string Outcome { get; set; } = "never";

    /// <summary>
    /// Detailed summary of the run, kept for diagnosis and the status endpoint. NOT shown in the
    /// Workers section: that line carries only light, date and outcome. Retained because it is the
    /// only durable trace of what a run actually did once the log has rotated.
    /// </summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// True when this run was a dry run, so the GUI can say so without the outcome line carrying a
    /// sentence. A dry run's numbers are hypothetical, and a reader who cannot see the flag would
    /// take the run for a real one — but the note belongs beside the outcome, not in the detail text.
    /// </summary>
    public bool DryRun { get; set; }
}
