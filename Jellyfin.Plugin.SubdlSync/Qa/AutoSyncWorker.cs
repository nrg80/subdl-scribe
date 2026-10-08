// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// F-M323 (operator order 08.10.2026): the auto-sync is a WORKER of its own.
//
// THE CONTRACT, in the operator's words: "Bekommt dann das sub übergeben und gibt status und
// output zurück" — it is handed the subtitle, and it hands back a status and its output.
//
// WHAT THIS TYPE IS. A worker, not a helper: it owns the decision of what happened per file, it
// reports its own status and detail, and the caller does not have to re-derive the rules to read
// the answer. Before this the outcome was derived from a run summary three files away: the
// download counted a file as "already good", and a dispatcher later guessed the worker's colour
// from those counts — so a download that failed for an unrelated reason (a network error) painted
// the ALIGNMENT red. The worker now states its own outcome; the run summary carries the
// alignment's own failures, and the row follows that instead of the download's failure count.
//
// WHY THE MEASUREMENT CAME ALONG (F-M310). The time a fit spends is credited back to the download
// pacing, so a stopwatch has to wrap the fit. It wrapped it in the CALLER before, once per call
// site; one call, one place now: the worker measures itself and reports `ElapsedMs`, so a second
// fit path cannot bypass the stopwatch by forgetting to wrap it. The caller keeps the accounting
// (limiter + run total), because those are the run's books.
//
// WHAT IS DELIBERATELY NOT HERE. No file writing, no slot numbering, no statistics counters, no
// hunt decision across candidates — the operator's own cut. A correction that filed itself into
// 01/99 would be two writers for one directory, and the count of what was really MOVED (F-M322)
// is the run's number, not the worker's.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe.Qa;

/// <summary>
/// F-M323: the auto-sync as a worker — a subtitle in, a status and its output back.
/// <para>
/// One entry point for both tracks (main and hearing-impaired): the HI variant goes through the
/// same correction as the main track (F-M307), so the two cannot drift apart, and both are
/// measured here rather than at each call site (F-M310).
/// </para>
/// </summary>
public static class AutoSyncWorker
{
    /// <summary>The name of this worker as the GUI lists it.</summary>
    public const string Name = "Auto-Sync";

    /// <summary>
    /// F-M323: the status this worker reports. Separate from <see cref="Registry.WorkerRunRegistry.Outcome"/>
    /// on purpose — that vocabulary is per RUN (one row, one word), while this one is per FILE, and
    /// the run row is derived from a whole run's worth of these.
    /// </summary>
    public static class Statuses
    {
        /// <summary>The fit moved the subtitle; a corrected artifact is ready to write.</summary>
        public const string Ok = "ok";

        /// <summary>The fit measured and declined — the file as downloaded is the best version.</summary>
        public const string AlreadyGood = "already-good";

        /// <summary>The alignment is switched off; nothing was asked of this worker.</summary>
        public const string Disabled = "disabled";

        /// <summary>The measurement declined for a reason that says nothing about the file.</summary>
        public const string RefusedAudio = "refused-audio";

        /// <summary>The measurement declined for a reason about THIS file — another may align.</summary>
        public const string RefusedFile = "refused-file";

        /// <summary>The worker could not measure at all (no ffmpeg, undecodable audio, fit error).</summary>
        public const string Failed = "failed";
    }

    /// <summary>
    /// F-M323: what the worker hands back.
    /// <para>
    /// <see cref="Corrected"/> is the subtitles' TEXT — the worker does not write it, because the
    /// file's slot (01 upward) and its reserved copy (99 downward) are the download run's decision
    /// (F-M317), and so is the encoding of the artifact.
    /// </para>
    /// </summary>
    /// <param name="Status">One of <see cref="Statuses"/>. Never null.</param>
    /// <param name="Applied">True only when a correction was really applied.</param>
    /// <param name="Corrected">The corrected text, or the input untouched when nothing was applied.</param>
    /// <param name="Reason">Human-readable outcome, for the log and the worker's detail line.</param>
    /// <param name="ElapsedMs">Wall time the fit spent, for the caller's pacing credit (F-M310).</param>
    /// <param name="AlreadyGood">True when the refusal was "no proven gain" (F-M321/F-M322).</param>
    /// <param name="CandidateSpecific">True when another candidate could produce a different answer (F-M318).</param>
    public readonly record struct Outcome(
        string Status,
        bool Applied,
        string Corrected,
        string Reason,
        long ElapsedMs,
        bool AlreadyGood,
        bool CandidateSpecific)
    {
        /// <summary>Gets a value indicating whether this file counts as the work DONE.</summary>
        public bool IsWorkDone => Applied || AlreadyGood;

        /// <summary>Gets a value indicating whether the worker was unable to measure.</summary>
        public bool IsBroken => Status == Statuses.Failed;
    }

    /// <summary>
    /// F-M323: hands the worker one subtitle and gets its status and output back.
    /// </summary>
    /// <param name="mediaPath">Media file the subtitle belongs to.</param>
    /// <param name="srtText">Decoded subtitle text — the thing this worker is handed.</param>
    /// <param name="audioMap">ffmpeg map argument for the audio track to read, e.g. <c>0:a:1</c>.</param>
    /// <param name="ffmpegPath">Resolved ffmpeg path; absent fails the worker's measurement.</param>
    /// <param name="switchedOn">The alignment switch. Off: the worker stands down.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome. Never throws for a measurement problem.</returns>
    public static async Task<Outcome> RunAsync(
        string mediaPath,
        string srtText,
        string audioMap,
        string? ffmpegPath,
        bool switchedOn,
        ILogger logger,
        CancellationToken ct)
    {
        if (!switchedOn)
        {
            // Standing down is not a failure: the operator turned the switch off, so nothing was
            // asked of this worker and nothing is broken (F-M268: grey, not red).
            return new Outcome(
                Statuses.Disabled, false, srtText, "alignment switched off", 0, false, false);
        }

        var sw = Stopwatch.StartNew();
        SubtitleSync.Result result;
        try
        {
            result = await SubtitleSync.SyncAsync(mediaPath, srtText, audioMap, ffmpegPath, logger, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            // F-M310: measured around the CALL, not around a write, and in a finally block because
            // a cancelled or failed fit spent the time too. The worker reports it; the caller books
            // it (limiter + run total), those being the run's books.
            sw.Stop();
        }

        long ms = (long)sw.Elapsed.TotalMilliseconds;

        if (result.Applied)
        {
            return new Outcome(Statuses.Ok, true, result.Corrected, result.Reason, ms, false, false);
        }

        // A refusal is an OUTCOME, not an error, and it splits three ways. The worker states which
        // one it is, so the caller does not re-derive it from the reason string:
        //   - "no proven gain" (F-M321): the file as downloaded IS the best version. The caller
        //     files it as good (01 and a byte-identical 99) and ends the hunt;
        //   - a verdict on the AUDIO or the tooling (F-M318): every candidate would be refused
        //     identically, so hunting on spends quota for the same answer;
        //   - a verdict on THIS FILE (a shift beyond the limit, too few cues): another candidate
        //     may well align, so the hunt stays alive.
        bool alreadyGood = SubtitleSync.RefusalMeansAlreadyGood(result.Reason);
        bool candidateSpecific = SubtitleSync.RefusalIsCandidateSpecific(result.Reason);

        string status;
        if (alreadyGood)
        {
            status = Statuses.AlreadyGood;
        }
        else if (IsMeasurementFailure(result.Reason))
        {
            status = Statuses.Failed;
        }
        else if (candidateSpecific)
        {
            status = Statuses.RefusedFile;
        }
        else
        {
            status = Statuses.RefusedAudio;
        }

        return new Outcome(status, false, srtText, result.Reason, ms, alreadyGood, candidateSpecific);
    }

    /// <summary>
    /// F-M323: whether the worker was UNABLE TO MEASURE, as opposed to measuring and declining.
    /// <para>
    /// One predicate, matched on the exact prefixes <see cref="SubtitleSync.SyncAsync"/> returns,
    /// because that is the only place the distinction exists. A missing ffmpeg and undecodable
    /// audio are machine facts; <c>fit error</c> is the fit throwing. All three are RED on the
    /// worker's row — something is broken — while a measurement that declined is grey: nothing is
    /// broken, the file simply needed no correction.
    /// </para>
    /// <para>
    /// The three audio/tool prefixes are the same set <see cref="SubtitleSync.RefusalIsCandidateSpecific"/>
    /// excludes, deliberately: there is one definition of "this says nothing about the file".
    /// <c>not measured: only N cues</c> is NOT here — it is a verdict on the file (F-M318).
    /// </para>
    /// </summary>
    /// <param name="reason">The reason from <see cref="SubtitleSync.SyncAsync"/>.</param>
    /// <returns>True when the worker could not measure at all.</returns>
    public static bool IsMeasurementFailure(string? reason)
        => reason != null
           && (reason.StartsWith("ffmpeg not available", StringComparison.Ordinal)
               || reason.StartsWith("audio decode failed", StringComparison.Ordinal)
               || reason.StartsWith("not measured: no audio samples", StringComparison.Ordinal)
               || reason.StartsWith("fit error", StringComparison.Ordinal));
}
