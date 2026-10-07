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
// WHY THE STAIRCASE IS NOT A SECOND VARIANT BESIDE THE CONSTANT ONE
//
// Both cases are one measurement answering two questions. "Does the offset move?" and "how far
// is the file off?" come out of the same detector run: planted constant shifts of -3.0 / +2.0 /
// +4.0 / +8.0 / +12.0 s come back as -3.20 / +1.80 / +3.80 / +7.80 / +11.80 s and report
// `drifts=False` at every one of them, so a LARGE CONSTANT SHIFT IS NOT MISTAKEN FOR DRIFT.
// When it does report `drifts=True`, the segments it found ARE the repair, because the step
// structure is real and not an artefact of the search: on the same files a permutation test puts
// the staircase fit 14x better on the real ordering than on a shuffled one (0.30 s against
// 4.13 s residual), it finds 4-5 steps per episode whose sizes sum to the total drift
// (11.98 s against 10.92 s), and each step lands between two adjacent dialogue lines.
//
// THE FORMER REFUSAL WAS RIGHT ABOUT THE AVERAGE AND WRONG ABOUT THE CONCLUSION
//
// A drifting file has no valid SINGLE offset — shifting it by the average moves one part right
// and spoils another — and that part still holds. But "no single offset" does not mean "no
// correction": measured over the 36 drifting episodes of this library, shifting each cue by the
// offset of ITS OWN segment takes the worst single-cue residual from a 10.74 s median to 4.51 s,
// with 33 of 36 better.
//
// THE LIMIT, STATED BECAUSE IT IS MEASURED
//
// Two of the 36 (S01E04 8.00 -> 21.30 s, S01E06 2.47 -> 11.91 s) come out WORSE, and no
// reference-free signal separated them from the 33 successes — six candidates were tried
// (monotone distortion, split-half disagreement, remaining drift after the correction, run-back
// against the main direction, outlier offset, raw drift span) and every one of them OVERLAPS.
// With 2 failures in 36, any threshold computed from that same distribution is a circle. The
// staircase is therefore applied and those two are accepted as the price of a ~6 s average gain,
// UNLESS a same-language reference subtitle exists — then the anchor path (F-M297) is the better
// route and the caller prefers it. This file only applies what it is handed.
//
// The gate still refuses when it could not run at all (no ffmpeg, no speech, too few cues):
// a tool being unavailable is not a licence to guess.
//
// THE ORIGINAL IS KEPT, ALWAYS — AS A ONE-ENTRY ARCHIVE
//
// The unmodified bytes are written beside the corrected file as
// `<base>.<lang>.srt.unsynchronized.zip` (F-M306), holding one entry named
// `<base>.<lang>.srt.unsynchronized`, before the corrected file lands. The loose
// copy is NOT written any more: the operator ordered exactly one artefact kept
// ("Nur das zip ablegen. Wenn ich es entpacken will mache ich das selber"), so
// unpacking is his step. The name sits AFTER `.srt` on purpose: this plugin finds
// sidecars with `EnumerateFiles(dir, baseName + "*.srt")`, which ignores that name,
// while `<base>.<lang>.unsynchronized.srt` WOULD match and the name parser would
// then read `unsynchronized` as a language code (measured). Jellyfin does not index
// the suffix form as an external subtitle track either, so exactly one new track
// appears per corrected file. The entry keeps the `.unsynchronized` name so that
// unpacking it into the media folder cannot clobber the corrected `<base>.<lang>.srt`.
//
// THE ORDER, AND THE HASH (user specification)
//
//   fetch → sync → normalize → write `<...>.srt` AND the archive
//   → register the hash OF THE SYNCHRONIZED srt
//
// The hash is registered over the corrected content because that is what lies on
// disk and what later goes up to SubDL. Normalization is idempotent, so whether
// it runs before or after the shift is immaterial; it runs before the hash, as the
// upload path already requires (F-M185).
//
// THE SIGN, ESTABLISHED BY MEASUREMENT (the expensive mistake here)
//
// The detector's number and the correction to apply are related by a sign, and the
// wrong choice DOUBLES the error instead of removing it. Measured on a real episode
// with a planted +5.0 s shift: the detector read +4.50 s; applying −4.50 s landed the
// file 0.50 s from its plain subtitle, while applying +4.50 s landed it at +9.50 s.
// **The correction is MINUS the detector's value**: `srt_time − measured = synced`.
// The first version of the pipeline added it, and every corrected file came out
// exactly twice as far off as it went in. The end-to-end test caught it; a unit test
// could not, because the shift function was correct in isolation — only the SIGN of
// what was handed to it was wrong.
//
// THE SHIFT IS APPLIED TO TIMES ONLY
//
// Text is carried through byte-for-byte, the source's byte style (BOM / CRLF) is
// reused, and a shift that would push a cue below zero is refused rather than
// clamped — clamping would silently change one cue's relation to its neighbour
// while leaving the rest moved.
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
/// F-M296: shifts a downloaded subtitle by the constant offset measured against
/// the audio, unless the offset moves.
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
    /// Measured, not chosen: the plain subtitle of a real episode — the very file that
    /// nothing is wrong with — measures **−0.50 s** against its own audio. That value is
    /// this material's measurement floor (an SDH/plain cue leads the speech onset by a
    /// per-cue varying amount), and it is the same order as a small real offset. A floor
    /// of 0.20 s therefore MOVED a file that was already in sync, which the end-to-end
    /// test caught. At 1.0 s a finding is at least twice the floor, so applying it can
    /// still be expected to remove more error than it adds.
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

        DriftVerdict verdict;
        try
        {
            verdict = await DriftGate.RunAsync(mediaPath, srtText, ffmpegPath, logger, ct, audioMap)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[SubDL] auto-sync: gate threw for {File}", System.IO.Path.GetFileName(mediaPath));
            return new Result(false, 0, srtText, null, "gate error");
        }

        if (!verdict.Ran)
        {
            return new Result(false, 0, srtText, null, "not measured: " + (verdict.SkipReason ?? "not run"));
        }

        // F-M300: a moving offset is repaired by a STAIRCASE, not refused. Each cue is moved by
        // the offset of its own segment; see the header for the measurement, and for the two
        // files of the 36 that this makes worse — which is why a same-language reference
        // (F-M297) wins when one exists.
        if (verdict.Drifts)
        {
            if (verdict.SegmentOffsetsSec.Count == 0)
            {
                return new Result(
                    false,
                    0,
                    srtText,
                    null,
                    $"drifts ({verdict.SpanSec:0.0}s over {verdict.BoundaryCount} boundary/boundaries) but no segment offsets were reported — left as downloaded");
            }

            (bool sok, string sshifted, string swhy, int sguarded) = ShiftByStaircase(
                srtText, verdict.SegmentStartTimesSec, verdict.SegmentOffsetsSec);
            if (!sok)
            {
                return new Result(false, 0, srtText, null, "staircase not applied: " + swhy);
            }

            double largest = verdict.SegmentOffsetsSec.Max(Math.Abs);
            if (largest > MaxShiftSec)
            {
                return new Result(
                    false,
                    0,
                    srtText,
                    null,
                    $"staircase step {largest:0.0}s beyond the {MaxShiftSec:0}s limit — not applied");
            }

            return new Result(
                true,
                largest,
                sshifted,
                null,
                $"staircase over {verdict.SegmentOffsetsSec.Count} segments across a {verdict.SpanSec:0.0}s drift ({sguarded} cue(s) order-guarded)");
        }

        double shift = verdict.MedianOffsetSec;
        if (Math.Abs(shift) < MinShiftSec)
        {
            return new Result(false, 0, srtText, null, $"already in sync ({shift:+0.00;-0.00}s)");
        }

        if (Math.Abs(shift) > MaxShiftSec)
        {
            return new Result(false, 0, srtText, null, $"shift {shift:+0.0;-0.0}s beyond the {MaxShiftSec:0}s limit — not applied");
        }

        // SIGN: the detector's number is "how far the cue sits AFTER the speech", so the
        // correction is its NEGATIVE. Established by measurement, not by reasoning — with
        // a planted +5.0 s the detector read +4.50 s, and −4.50 s landed the file on its
        // plain subtitle while +4.50 s landed it at +9.50 s (the error doubled). See the
        // file header; the end-to-end test asserts this and prints both directions.
        double applied = -shift;

        (bool ok, string shifted, string why) = ShiftBy(srtText, applied);
        if (!ok)
        {
            return new Result(false, 0, srtText, null, why);
        }

        return new Result(true, applied, shifted, null, $"shifted {applied:+0.00;-0.00}s");
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

        // Refuse rather than clamp a cue below zero: clamping one cue silently changes its
        // relation to its neighbour while the rest still moves.
        double minStart = starts.Min();
        if (minStart + eff[0] < 0)
        {
            return (false, srtText, $"first cue {minStart:0.00}s would go negative under {eff[0]:+0.00;-0.00}s", 0);
        }

        var sb = new StringBuilder(srtText.Length);
        int last = 0;
        for (int k = 0; k < cues; k++)
        {
            for (int h = 0; h < 2; h++)
            {
                Match m = matches[(2 * k) + h];
                sb.Append(srtText, last, m.Index - last);
                sb.Append(Fmt((h == 0 ? starts[k] : ends[k]) + eff[k]));
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

        // Refuse rather than clamp: clamping one cue would change its relation to
        // its neighbour while the rest of the file still moves.
        if (minStart + shiftSec < 0)
        {
            return (false, srtText, $"first cue {minStart:0.00}s would go negative under {shiftSec:+0.00;-0.00}s");
        }

        int touched = 0;
        string outText = TsRegex.Replace(srtText, m =>
        {
            touched++;
            return Fmt(ToSec(m) + shiftSec);
        });

        if (touched == 0)
        {
            return (false, srtText, "no timestamps replaced");
        }

        return (true, outText, $"shifted {touched} timestamps by {shiftSec:+0.00;-0.00}s");
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
    /// <returns>Path with <c>.unsynchronized</c> appended after <c>.srt</c>.</returns>
    public static string UnsyncPathFor(string correctedPath) => correctedPath + ".unsynchronized";

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
