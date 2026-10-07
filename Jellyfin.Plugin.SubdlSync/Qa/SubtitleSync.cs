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
// F-M296/F-M300: the download auto-sync. It SHIFTS a downloaded subtitle by the offset
// measured against the audio — a single constant while the offset really is constant, and a
// STAIRCASE, one offset per segment, once the offset moves.
//
// THE METHOD: ONE FIT, NOT TWO CASES (F-M307)
//
// The offset is not a number, it is a FUNCTION of time, and it is fitted as one:
// a piecewise-constant function, found by exact dynamic programming (OffsetFit).
// A constant offset is the same fit with one segment, so "the file is uniformly
// off" and "the offset moves" are no longer two questions with two answers that
// can contradict each other — they are one question with one answer. That
// structure is why the earlier split failed: a gate decided WHICH case applied,
// and every disagreement between the gate and the corrector became a wrong
// file. The gate and the staircase are gone; this file runs the fit.
//
// WHAT THE FIT MAXIMISES
//
// Speech probability per 20 ms frame, threshold-free: frame levels are normalised
// against the file's own 10th/90th percentile, so a quiet recording and a loud one
// produce comparable curves and no absolute loudness threshold is needed. For each
// cue the score is the mean speech probability over the cue's window at a given
// shift, on a 0.25 s grid over +-20 s. The fit maximises the total score minus a
// charge of Z * sigma * sqrt(segment cues) per segment, which is what stops it from
// inventing a step for every stray cue; sigma is the median absolute score change
// per grid step, measured on the file's own curve.
//
// A window that lies OUTSIDE the audio is EXCLUDED, never scored as zero and never
// clipped. Clipping shortened the window and thereby raised its mean, and the fit
// then returned +20.0 / -19.25 / +19.75 s on cues that were never moved — the
// degenerate answer at the edge of the search range, which is the signature of a
// score that rewards a shorter window.
//
// WHAT MAKES IT DEPLOYABLE
//
// The fit only writes when the correction PROVES itself: a paired test over the
// MOVED cues only, the whole file must not get worse, and the largest shift must
// reach MinShiftSec. A file that is already correct is therefore left exactly as
// it is, which is what makes a second run move the timeline by 0.000 s. When the
// fit proves no gain the file is reported, not guessed at: a tool that cannot
// measure is not a licence to shift something anyway.
//
// THE ORIGINAL IS KEPT, ALWAYS - AS A ONE-ENTRY ARCHIVE
//
// The unmodified bytes are written beside the corrected file as
// `<base>.<lang>.srt.unsynced.zip` (F-M306), holding one entry named
// `<base>.<lang>.srt.unsynced`, before the corrected file lands. The loose
// copy is NOT written: exactly one artefact is kept, so unpacking is the
// operator's step. The name sits AFTER `.srt` on purpose: this plugin finds
// sidecars with `EnumerateFiles(dir, baseName + "*.srt")`, which ignores that
// name, while `<base>.<lang>.unsynced.srt` WOULD match and the name parser
// would then read `unsynced` as a language code (measured). Jellyfin does
// not index the suffix form as an external subtitle track either, so exactly one
// new track appears per corrected file. The entry keeps the `.unsynced` name
// so that unpacking it into the media folder cannot clobber the corrected file.
//
// THE SHIFT IS APPLIED TO TIMES ONLY
//
// Text is carried through byte-for-byte, the fitted shift is applied AS MEASURED,
// and a cue that would land below zero is CLAMPED to zero (operator order,
// 07.10.2026: the fit decides the offset, the clamp only stops a cue running off
// the front of the file), and a cue is never
// pulled further from its own segment's value than the order guard allows - the
// guard exists because a large step can otherwise push a cue past its neighbour
// and invert two lines.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe.Qa;

/// <summary>
/// F-M307: corrects a downloaded subtitle by the piecewise-constant offset fitted
/// against the audio. The fit is the only correction route (OffsetFit).
/// </summary>
public static class SubtitleSync
{
    private static readonly Regex TsRegex = new(
        @"(?<h>\d{2}):(?<m>\d{2}):(?<s>\d{2})[,.](?<f>\d{3})",
        RegexOptions.Compiled);

    /// <summary>Largest shift the feature will apply, in seconds. Beyond this the finding is treated as implausible.</summary>
    public const double MaxShiftSec = 20.0;

    /// <summary>
    /// Below this the file is not moved, in seconds.
    /// <para>
    /// Measured, not chosen: a subtitle file that nothing is wrong with still measures a
    /// small nonzero offset against its own audio, because a cue leads the speech onset by
    /// a per-cue varying amount. That is this material's measurement floor, and it is the
    /// same order as a small real offset, so a lower floor MOVED files that were already in
    /// sync. At 1.0 s a finding is at least twice the floor, and the fit additionally has to
    /// prove a gain (see OffsetFit's deploy rule), so the floor is a backstop rather than
    /// the decision.
    /// </para>
    /// </summary>
    public const double MinShiftSec = 1.0;

    /// <summary>The outcome of one sync attempt.</summary>
    /// <param name="Applied">True when a corrected file was written.</param>
    /// <param name="ShiftSec">The shift that was applied (0 when nothing was written).</param>
    /// <param name="Corrected">Corrected SRT text when written, else the input.</param>
    /// <param name="UnsyncPath">Path of the preserved original, when one was written.</param>
    /// <param name="Reason">Human-readable outcome, for the log.</param>
    public readonly record struct Result(
        bool Applied,
        double ShiftSec,
        string Corrected,
        string? UnsyncPath,
        string Reason);

    /// <summary>
    /// Measures and applies the shift for one downloaded subtitle.
    /// </summary>
    /// <param name="mediaPath">Media file the subtitle belongs to.</param>
    /// <param name="srtText">Decoded subtitle text.</param>
    /// <param name="audioMap">ffmpeg map argument for the audio track to read, e.g. <c>0:a:1</c>.</param>
    /// <param name="ffmpegPath">Resolved ffmpeg path; absent fails open.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result. Never throws for a measurement problem.</returns>
    public static async Task<Result> SyncAsync(
        string mediaPath,
        string srtText,
        string audioMap,
        string? ffmpegPath,
        ILogger logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !System.IO.File.Exists(ffmpegPath))
        {
            return new Result(false, 0, srtText, null, "ffmpeg not available");
        }

        // F-M307: the fit REPLACES the gate's verdict plus the staircase. One route: the
        // offset is a piecewise-constant function of time, fitted by exact DP, and the
        // per-cue shift it produces is applied directly. There is no separate "does it
        // drift?" question any more — a constant offset is the same fit with one segment,
        // so the two cases cannot disagree.
        OffsetFit.FitResult fit;
        double[] levels;
        try
        {
            levels = await DriftGate.DecodeFrameLevelsAsync(ffmpegPath, mediaPath, ct, audioMap)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[SubDL] auto-sync: decode failed for {File}", System.IO.Path.GetFileName(mediaPath));
            return new Result(false, 0, srtText, null, "audio decode failed");
        }

        if (levels.Length == 0)
        {
            return new Result(false, 0, srtText, null, "not measured: no audio samples");
        }

        (double[] fst, double[] fen) = DriftGate.ParseCues(srtText);
        if (fst.Length < 40)
        {
            return new Result(false, 0, srtText, null, $"not measured: only {fst.Length} cues");
        }

        try
        {
            fit = OffsetFit.Fit(fst, fen, levels);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[SubDL] auto-sync: fit threw for {File}", System.IO.Path.GetFileName(mediaPath));
            return new Result(false, 0, srtText, null, "fit error");
        }

        if (!fit.Ran)
        {
            return new Result(false, 0, srtText, null, "not measured: " + (fit.SkipReason ?? "not run"));
        }

        if (fit.Reverted)
        {
            return new Result(
                false,
                0,
                srtText,
                null,
                $"no proven gain ({fit.MovedCues} cue(s) moved, t = {fit.TMoved:+0.00;-0.00}) — left as downloaded");
        }

        double largestShift = 0;
        foreach (double v in fit.AppliedShiftsSec)
        {
            largestShift = Math.Max(largestShift, Math.Abs(v));
        }

        if (largestShift > MaxShiftSec)
        {
            return new Result(
                false,
                0,
                srtText,
                null,
                $"shift {largestShift:0.0}s beyond the {MaxShiftSec:0}s limit — not applied");
        }

        (bool aok, string ashifted, string awhy) = ShiftByStaircasePerCue(srtText, fit.AppliedShiftsSec);
        if (!aok)
        {
            return new Result(false, 0, srtText, null, "not applied: " + awhy);
        }

        string how = fit.Segments == 1
            ? $"constant {fit.AppliedShiftsSec[0]:+0.00;-0.00}s"
            : $"{fit.Segments} segments ({string.Join(" / ", fit.SegmentOffsetsSec.ConvertAll(v => (-v).ToString("+0.00;-0.00", CultureInfo.InvariantCulture)))})";
        return new Result(
            true,
            largestShift,
            ashifted,
            null,
            $"corrected {how}, {fit.MovedCues} cue(s) moved, t = {fit.TMoved:+0.00;-0.00}, "
            + $"score {fit.ScoreBefore:0.0000} -> {fit.ScoreAfter:0.0000}, {fit.GuardedCues} order-guarded");
    }

    /// <summary>
    /// Applies a PER-CUE shift list: cue <c>k</c> moves by <paramref name="shiftsSec"/>[k].
    /// <para>
    /// F-M307 produces the shift per cue rather than per segment, because the order guard
    /// pulls individual cues away from their segment's value at a step. Applying the guarded
    /// values directly is what makes the written file identical to the one the deploy rule
    /// scored — recomputing a staircase from segment offsets here would silently re-introduce
    /// the very difference the guard exists to remove.
    /// </para>
    /// </summary>
    /// <param name="srtText">SRT text.</param>
    /// <param name="shiftsSec">One shift per cue, in cue order.</param>
    /// <returns>(ok, shifted text, reason).</returns>
    public static (bool Ok, string Text, string Reason) ShiftByStaircasePerCue(
        string srtText, double[] shiftsSec)
    {
        if (string.IsNullOrEmpty(srtText))
        {
            return (false, srtText, "empty content");
        }

        MatchCollection matches = TsRegex.Matches(srtText);
        if (matches.Count < 2)
        {
            return (false, srtText, "no timestamps found");
        }

        int cues = matches.Count / 2;
        if (shiftsSec.Length < cues)
        {
            return (false, srtText, $"shift list has {shiftsSec.Length} entries for {cues} cues");
        }

        var sb = new System.Text.StringBuilder();
        int last = 0;
        int clamped = 0;
        for (int j = 0; j < cues * 2; j++)
        {
            Match m = matches[j];
            sb.Append(srtText, last, m.Index - last);
            double t = ToSec(m) + shiftsSec[j / 2];
            if (t < 0)
            {
                t = 0;
                clamped++;
            }

            sb.Append(Fmt(t));
            last = m.Index + m.Length;
        }

        sb.Append(srtText, last, srtText.Length - last);
        return (true, sb.ToString(), clamped > 0
            ? $"applied per cue, {clamped} timestamp(s) clamped to 0"
            : "applied per cue");
    }

    /// <summary>
    /// Minimum gap kept between one cue's end and the next cue's start, in seconds.
    /// <para>
    /// The same reasoning applies as on the anchor route this constant once came from: these
    /// subtitles butt cue against cue — measured, <b>560 of 724 gaps are below 0.04 s</b> with a
    /// median of <b>0.002 s</b>. A guard set tighter would fire on almost every cue.
    /// </para>
    /// </summary>
    public const double MinGapSec = 0.04;

    /// <summary>
    /// Applies a STAIRCASE shift: each cue is moved by the offset of its OWN segment
    /// (F-M300). This is what repairs a drifting file, where one constant offset cannot.
    /// <para>
    /// Segment <c>k</c> covers the cues from <paramref name="segmentStartTimesSec"/>[k] up to
    /// the next entry, so a cue belongs to the last segment that starts at or before it.
    /// </para>
    /// <para>
    /// The order guard is <see cref="MinGapSec"/> against the previous cue's END, exactly as
    /// the anchor path does it: at a step the two neighbours shift by different amounts, and a
    /// step wider than the gap between them would otherwise make the earlier cue's end land
    /// after the later cue's start — which a player renders as stacked text. Where the guard
    /// bites, the previous shift is carried forward, so the step is absorbed at that cue
    /// instead of inverting the order.
    /// </para>
    /// </summary>
    /// <param name="srtText">SRT text.</param>
    /// <param name="segmentStartTimesSec">Segment start times, ascending; first is the file start.</param>
    /// <param name="segmentOffsetsSec">One measured offset per segment, same length.</param>
    /// <returns>(ok, shifted text, reason, steps actually kept) — the last is the count of order-guard bites.</returns>
    public static (bool Ok, string Text, string Reason, int Guarded) ShiftByStaircase(
        string srtText,
        IReadOnlyList<double> segmentStartTimesSec,
        IReadOnlyList<double> segmentOffsetsSec)
    {
        if (string.IsNullOrEmpty(srtText))
        {
            return (false, srtText, "empty content", 0);
        }

        if (segmentOffsetsSec.Count == 0 || segmentOffsetsSec.Count != segmentStartTimesSec.Count)
        {
            return (false, srtText, "no segments to apply", 0);
        }

        // The timestamps come in PAIRS: match 2k is cue k's start, match 2k+1 its end. Walking
        // the matches directly (rather than splitting into blocks) keeps the text between them
        // byte-for-byte and survives a file whose block separators are unorthodox.
        var matches = TsRegex.Matches(srtText);
        if (matches.Count < 2)
        {
            return (false, srtText, "no timestamps found", 0);
        }

        int cues = matches.Count / 2;
        var starts = new double[cues];
        var ends = new double[cues];
        for (int k = 0; k < cues; k++)
        {
            starts[k] = ToSec(matches[2 * k]);
            ends[k] = ToSec(matches[(2 * k) + 1]);
        }

        // Desired shift per cue: MINUS its own segment's offset (srt_time − measured = synced).
        // The sign is the one the constant path proved by measurement; see the header.
        var desired = new double[cues];
        int seg = 0;
        for (int k = 0; k < cues; k++)
        {
            while (seg + 1 < segmentStartTimesSec.Count && segmentStartTimesSec[seg + 1] <= starts[k])
            {
                seg++;
            }

            desired[k] = -segmentOffsetsSec[seg];
        }

        // Order guard, against the previous cue's END. The shift is ADDED here, so the gap
        // becomes `gap + eff[k] − eff[k−1]`: a step that moves cue k far EARLIER than its
        // neighbour would push it back over that neighbour's end, which a player renders as
        // stacked text. The bound is therefore a LOWER bound on eff[k]. Getting the direction
        // wrong mangles every cue at a step: measured by the test below, it turned a 0.4 s
        // residual into 1.92 s and "guarded" 22 cues that needed no guard.
        var eff = new double[cues];
        int guarded = 0;
        for (int k = 0; k < cues; k++)
        {
            if (k == 0)
            {
                eff[0] = desired[0];
                continue;
            }

            double gap = starts[k] - ends[k - 1];
            // A gap already below the floor must not be SHRUNK further (these subtitles butt cue
            // against cue — 560 of 724 gaps under 0.04 s in the real material, so demanding the
            // floor here would fire on nearly every cue and carry one shift through the whole
            // file); where there is room, the floor is kept.
            double lower = gap < MinGapSec ? eff[k - 1] : eff[k - 1] - gap + MinGapSec;
            eff[k] = Math.Max(desired[k], lower);
            if (eff[k] > desired[k] + 1e-9)
            {
                guarded++;
            }
        }

        // The fitted shift is applied as measured; a cue that would land below zero is
        // clamped to zero (operator order 07.10.2026). Clamping changes that one cue's
        // relation to its neighbour, which is the accepted price of not refusing a whole
        // file over a leading title card.
        var sb = new StringBuilder(srtText.Length);
        int last = 0;
        int clampedCount = 0;
        for (int k = 0; k < cues; k++)
        {
            for (int h = 0; h < 2; h++)
            {
                Match m = matches[(2 * k) + h];
                sb.Append(srtText, last, m.Index - last);
                double tv = (h == 0 ? starts[k] : ends[k]) + eff[k];
                if (tv < 0)
                {
                    tv = 0;
                    clampedCount++;
                }

                sb.Append(Fmt(tv));
                last = m.Index + m.Length;
            }
        }

        sb.Append(srtText, last, srtText.Length - last);

        double maxShift = 0;
        for (int k = 0; k < cues; k++)
        {
            maxShift = Math.Max(maxShift, Math.Abs(eff[k]));
        }

        return (true, sb.ToString(),
            $"staircase over {segmentOffsetsSec.Count} segments, largest shift {maxShift:0.00}s ({guarded} cue(s) order-guarded)",
            guarded);
    }

    /// <summary>
    /// Applies a constant shift to every timestamp. Text and line structure are
    /// carried through unchanged.
    /// </summary>
    /// <param name="srtText">SRT text.</param>
    /// <param name="shiftSec">Seconds to add (the measured offset IS the correction).</param>
    /// <returns>(ok, shifted text, reason when not ok).</returns>
    public static (bool Ok, string Text, string Reason) ShiftBy(string srtText, double shiftSec)
    {
        if (string.IsNullOrEmpty(srtText))
        {
            return (false, srtText, "empty content");
        }

        double minStart = double.MaxValue;
        foreach (Match m in TsRegex.Matches(srtText))
        {
            double t = ToSec(m);
            if (t < minStart)
            {
                minStart = t;
            }
        }

        if (minStart == double.MaxValue)
        {
            return (false, srtText, "no timestamps found");
        }

        // The shift is applied as measured; a cue that would land below zero is clamped to
        // zero (operator order 07.10.2026).
        int touched = 0;
        int clampedFlat = 0;
        string outText = TsRegex.Replace(srtText, m =>
        {
            touched++;
            double tv = ToSec(m) + shiftSec;
            if (tv < 0)
            {
                tv = 0;
                clampedFlat++;
            }

            return Fmt(tv);
        });

        if (touched == 0)
        {
            return (false, srtText, "no timestamps replaced");
        }

        return (true, outText, clampedFlat > 0
            ? $"shifted {touched} timestamps by {shiftSec:+0.00;-0.00}s, {clampedFlat} clamped to 0"
            : $"shifted {touched} timestamps by {shiftSec:+0.00;-0.00}s");
    }

    /// <summary>
    /// Reads the byte style of an existing file: (has BOM, uses CRLF). A rewritten
    /// subtitle must not silently change encodings nobody asked about.
    /// </summary>
    /// <param name="path">File to inspect.</param>
    /// <returns>Style flags; (true, true) when unreadable, the common case here.</returns>
    public static (bool Bom, bool Crlf) StyleOf(string path)
    {
        try
        {
            return StyleOfBytes(System.IO.File.ReadAllBytes(path));
        }
        catch
        {
            return (true, true);
        }
    }

    /// <summary>
    /// Byte style of a payload that is not on disk yet — the freshly fetched bytes.
    /// A corrected file must be written in the same shape the original would have been,
    /// or the correction silently changes the file's encoding.
    /// </summary>
    /// <param name="raw">Raw subtitle bytes as fetched.</param>
    /// <returns>Style flags; (true, true) for an empty payload.</returns>
    public static (bool Bom, bool Crlf) StyleOfBytes(byte[] raw)
    {
        if (raw.Length == 0)
        {
            return (true, true);
        }

        // UTF-16 payloads cannot carry a UTF-8 BOM; they are re-encoded as UTF-8 with
        // BOM, which is the shape this plugin writes everywhere else.
        bool bom = (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
                   || (raw.Length >= 2 && ((raw[0] == 0xFF && raw[1] == 0xFE) || (raw[0] == 0xFE && raw[1] == 0xFF)));
        bool crlf = raw.Length >= 2 && (raw[0] == 0xFF || raw[0] == 0xFE
            ? System.Text.Encoding.Unicode.GetString(raw).Contains("\r\n", StringComparison.Ordinal)
            : Encoding.UTF8.GetString(raw).Contains("\r\n", StringComparison.Ordinal));
        return (bom, crlf);
    }

    /// <summary>Path of the preserved original beside a corrected sidecar.</summary>
    /// <param name="correctedPath">The corrected sidecar's path.</param>
    /// <returns>Path with <c>.unsynced</c> appended after <c>.srt</c>.</returns>
    public static string UnsyncPathFor(string correctedPath) => correctedPath + ".unsynced";

    /// <summary>Path of the archived original beside a corrected sidecar.</summary>
    /// <param name="correctedPath">The corrected sidecar's path.</param>
    /// <returns>The copy's path with <c>.zip</c> appended.</returns>
    /// <remarks>
    /// F-M306 (development): the preserved original is archived as well, and the name sits after
    /// <c>.srt</c> for the same reason the plain copy's does — both listing patterns in this
    /// plugin match on the <c>.srt</c> ENDING, so a name ending in <c>.zip</c> matches neither and
    /// can never be read as a subtitle.
    /// </remarks>
    public static string UnsyncZipPathFor(string correctedPath) => UnsyncPathFor(correctedPath) + ".zip";

    /// <summary>Builds the zip that archives one preserved original.</summary>
    /// <param name="entryName">Name the single entry carries inside the archive.</param>
    /// <param name="payload">The original's bytes, written in verbatim.</param>
    /// <returns>The archive's bytes.</returns>
    /// <remarks>
    /// F-M306: one entry, and its payload is byte-identical to the plain copy because the SAME
    /// bytes are passed to both halves. The fixed timestamp and the deliberate absence of a
    /// directory entry make the archive deterministic — the same input yields the same bytes on
    /// every run, which is what lets T118 compare byte for byte instead of comparing sizes.
    /// </remarks>
    public static byte[] BuildUnsyncArchive(string entryName, byte[] payload)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using Stream s = entry.Open();
            s.Write(payload, 0, payload.Length);
        }

        return ms.ToArray();
    }

    private static double ToSec(Match m)
        => (int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) * 3600.0)
           + (int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) * 60.0)
           + int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture)
           + (int.Parse(m.Groups["f"].Value, CultureInfo.InvariantCulture) / 1000.0);

    private static string Fmt(double t)
    {
        t = Math.Max(0.0, t);
        int h = (int)(t / 3600.0);
        int mi = (int)((t - (h * 3600.0)) / 60.0);
        double s = t - (h * 3600.0) - (mi * 60.0);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{h:00}:{mi:00}:{s:00.000}").Replace('.', ',');
    }

    /// <summary>Encodes text with the given byte style, so the file keeps its encoding.</summary>
    /// <param name="text">Text (LF line endings).</param>
    /// <param name="bom">Write a UTF-8 BOM.</param>
    /// <param name="crlf">Write CRLF line endings.</param>
    /// <returns>The bytes to write.</returns>
    public static byte[] Encode(string text, bool bom, bool crlf)
    {
        string body = crlf
            ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)
            : text.Replace("\r\n", "\n", StringComparison.Ordinal);
        byte[] payload = Encoding.UTF8.GetBytes(body);
        if (!bom)
        {
            return payload;
        }

        var withBom = new byte[payload.Length + 3];
        withBom[0] = 0xEF;
        withBom[1] = 0xBB;
        withBom[2] = 0xBF;
        Array.Copy(payload, 0, withBom, 3, payload.Length);
        return withBom;
    }
}
