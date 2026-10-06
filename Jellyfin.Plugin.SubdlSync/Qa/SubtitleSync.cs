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
// F-M296: the download auto-sync. It SHIFTS a downloaded subtitle by the single
// constant offset the audio says it carries — but only while the offset really
// is constant.
//
// WHY THIS IS SEPARATE FROM THE DRIFT GATE
//
// The drift gate (F-M295) answers "does the offset move?". That question and
// "how far is the file off?" are two halves of the same arithmetic and were
// measured together: planted constant shifts of -3.0 / +2.0 / +4.0 / +8.0 /
// +12.0 s come back as -3.20 / +1.80 / +3.80 / +7.80 / +11.80 s — accurate to
// about 0.2 s (the residual is the ~0.3 s SDH lead per cue moving the median).
// Crucially, at every one of those values the detector reported `drifts=False,
// boundaries=0`: a LARGE CONSTANT SHIFT IS NOT MISTAKEN FOR DRIFT. That is what
// makes this feature possible at all — the shift is measurable and the moving
// case is separable from it.
//
// WHAT IT REFUSES TO DO
//
// A drifting file has NO valid single offset. Shifting it by the average would
// move one part right and spoil another, and the measurement is explicit that
// the average describes no real state (a file drifting from -2 s to +10 s has
// no offset). So: `Drifts = true` ⇒ nothing is written, the file is reported.
// The same refusal applies when the gate could not run (no ffmpeg, no speech,
// too few cues) — a tool being unavailable is not a licence to guess.
//
// THE ORIGINAL IS KEPT, ALWAYS
//
// The unsynchronized bytes are written beside the corrected file as
// `<base>.<lang>.srt.unsynchronized` before the corrected file lands. The
// suffix sits AFTER `.srt` on purpose: this plugin finds sidecars with
// `EnumerateFiles(dir, baseName + "*.srt")`, which ignores that name, while
// `<base>.<lang>.unsynchronized.srt` WOULD match and the name parser would then
// read `unsynchronized` as a language code (measured). Jellyfin does not index
// the suffix form as an external subtitle track either, so exactly one new track
// appears per corrected file.
//
// THE ORDER, AND THE HASH (user specification)
//
//   fetch → sync → normalize → write `<...>.srt` AND `<...>.srt.unsynchronized`
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

        // The refusal that makes this feature safe: a moving offset has no single
        // valid correction, so the file passes through untouched and is reported.
        if (verdict.Drifts)
        {
            return new Result(
                false,
                0,
                srtText,
                null,
                $"drifts ({verdict.SpanSec:0.0}s over {verdict.BoundaryCount} boundary/boundaries) — no single offset, left as downloaded");
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
