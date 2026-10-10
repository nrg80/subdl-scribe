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

namespace Jellyfin.Plugin.SubdlScribe.Pipeline;

/// <summary>
/// F-M247 (user decision 28.09.2026: "Dryrun geht nie in die Statistik"): the cumulative counters a
/// finished run contributes — with a dry run contributing nothing.
/// <para>
/// A dry run does real work: it searches, ranks, runs the id test and the QA gates, and every
/// one of those steps bumps a counter in its summary. But it saves and uploads NOTHING. Letting
/// those numbers through means the statistics report subtitles nobody wrote, which is what
/// happened live on 28.09.2026 — 771 of the 858 "downloaded" entries came from one afternoon of
/// dry runs.
/// </para>
/// <para>
/// The rule lives HERE and only here, because <see cref="ScheduledTasks.SubdlEventDispatcher"/>
/// is the single writer of the statistics row. Gating the counters at each increment site would
/// mean six places that can each drift; gating them once, at the boundary, means a new counter
/// added later is either routed through this type or is visibly outside it.
/// </para>
/// </summary>
public readonly struct StatusCounterDelta
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StatusCounterDelta"/> struct.
    /// </summary>
    public StatusCounterDelta(
        long uploaded,
        long downloaded,
        long rejectedDownload,
        long rejectedUpload,
        long fittedToAudio,
        long languageCodesAllocated,
        long looseSubtitlesRenamed,
        long reuploadsPrevented = 0)
    {
        Uploaded = uploaded;
        Downloaded = downloaded;
        RejectedDownload = rejectedDownload;
        RejectedUpload = rejectedUpload;
        FittedToAudio = fittedToAudio;
        LanguageCodesAllocated = languageCodesAllocated;
        LooseSubtitlesRenamed = looseSubtitlesRenamed;
        ReuploadsPrevented = reuploadsPrevented;
    }

    /// <summary>Gets subtitles uploaded by this run.</summary>
    public long Uploaded { get; }

    /// <summary>Gets subtitles downloaded by this run.</summary>
    public long Downloaded { get; }

    /// <summary>Gets the F-M286 download candidates fetched and then thrown away.</summary>
    public long RejectedDownload { get; }

    /// <summary>Gets the F-M286 upload candidates discarded from the upload.</summary>
    public long RejectedUpload { get; }

    /// <summary>Gets the F-M308 downloaded subtitles fitted to their audio track.</summary>
    public long FittedToAudio { get; }

    /// <summary>Gets the F-M311 media files whose language codes were written into their container.</summary>
    public long LanguageCodesAllocated { get; }

    /// <summary>Gets the F-M313 loose subtitle files renamed so their name carries the language.</summary>
    public long LooseSubtitlesRenamed { get; }

    /// <summary>
    /// Gets the F-M345 subtitles that were NOT sent again because their content was already settled —
    /// per embedded track and per loose sidecar, so the number counts subtitles and not files.
    /// </summary>
    public long ReuploadsPrevented { get; }

    /// <summary>Gets a value indicating whether this delta leaves the statistics row untouched.</summary>
    public bool IsEmpty
        => Uploaded == 0 && Downloaded == 0
           && RejectedDownload == 0 && RejectedUpload == 0 && FittedToAudio == 0
           && LanguageCodesAllocated == 0 && LooseSubtitlesRenamed == 0 && ReuploadsPrevented == 0;

    /// <summary>
    /// F-M247: builds the delta for one direction run, discarding everything a dry run produced.
    /// <para>
    /// At most one of the two summaries is non-null for a given run, so each is judged on its own
    /// flag: a dry upload must not zero a real download's numbers, and the other way round.
    /// </para>
    /// </summary>
    /// <param name="upload">The upload summary, or null when this run was a download.</param>
    /// <param name="download">The download summary, or null when this run was an upload.</param>
    /// <param name="languageCodesAllocated">F-M311: codes the SEEDER wrote for this run. Not part of
    /// either summary — the seeder runs before both directions — so it arrives on its own and is
    /// passed through untouched. Zero already when a dry run or the switch stood the write down, so
    /// no dry-run filter is needed here: the count cannot exist in a run that wrote nothing.</param>
    /// <returns>The counters to add to the statistics row.</returns>
    public static StatusCounterDelta From(RunSummary? upload, DownloadRunSummary? download, long languageCodesAllocated = 0, long looseSubtitlesRenamed = 0, long reuploadsPrevented = 0)
    {
        bool uploadDry = upload?.IsDryRun == true;
        bool downloadDry = download?.IsDryRun == true;

        return new StatusCounterDelta(
            uploaded: uploadDry ? 0 : upload?.Uploaded ?? 0,
            downloaded: downloadDry ? 0 : download?.Downloaded ?? 0,
            rejectedDownload: downloadDry ? 0 : download?.RejectedCandidates ?? 0,
            rejectedUpload: uploadDry ? 0 : upload?.RejectedCandidates ?? 0,
            fittedToAudio: downloadDry ? 0 : download?.FittedToAudio ?? 0,
            languageCodesAllocated: languageCodesAllocated,
            looseSubtitlesRenamed: looseSubtitlesRenamed,
            reuploadsPrevented: reuploadsPrevented);
    }
}
