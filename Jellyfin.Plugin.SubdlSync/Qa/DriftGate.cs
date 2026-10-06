// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// F-M295: the ffmpeg-facing half of the drift gate. Decodes the audio to a mono
// envelope, derives speech islands, converts the SRT to cue times and hands both
// to <see cref="DriftDetector"/>. Split from the detector so the model stays
// testable without a media file.
//
// THE SPEECH ISLANDS ARE A THRESHOLD ON THE ENVELOPE, NOT A SPEECH RECOGNISER
//
// Measured: the 10th/90th percentile of the frame level separates speech from
// silence reliably on this material (the files are broadcast rips, not music
// videos). The thresholds sit at 30 %/55 % of that range with hysteresis and a
// 0.15 s minimum island, which on a 44 min episode yields ~1400 islands against
// ~540 cues — the surplus is what makes the anchor usable. A dedicated VAD would
// be more accurate and is NOT required here, because the detector sums hundreds
// of cues per window: the per-cue noise averages out (that is also why the
// single-cue variant failed, 21 % precision).
//
// FAIL-OPEN, LIKE EVERY OTHER GATE
//
// No ffmpeg, unreadable audio, no speech detected, too few cues: the gate returns
// "did not run" and the file passes. A download is never discarded because an
// analysis tool was unavailable; the log line says which precondition was missing.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Pipeline;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe.Qa;

/// <summary>
/// F-M295: runs the drift gate against a media file and an SRT. Owns the ffmpeg
/// call and the envelope maths; the decision itself is in <see cref="DriftDetector"/>.
/// </summary>
public static class DriftGate
{
    /// <summary>Sample rate for the mono envelope decode.</summary>
    public const int SampleRate = 8000;

    /// <summary>Frame length in seconds for the envelope.</summary>
    public const double FrameSec = DriftDetector.FrameSec;

    /// <summary>Minimum island length in seconds.</summary>
    public const double MinIslandSec = 0.15;

    /// <summary>Minimum gap in seconds between two islands.</summary>
    public const double MinGapSec = 0.25;

    private static readonly Regex CueRegex = new(
        @"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})[,.](\d{3})",
        RegexOptions.Compiled);

    /// <summary>
    /// Runs the gate. Never throws: every failure path returns a "did not run"
    /// verdict with the reason, so the caller can log it and let the file through.
    /// </summary>
    /// <param name="mediaPath">Media file to analyse.</param>
    /// <param name="srtText">The subtitle text (already decoded).</param>
    /// <param name="ffmpegPath">Resolved ffmpeg path; empty/absent fails open.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="audioMap">Audio stream to decode, e.g. <c>0:a:1</c>. Defaults to the first.</param>
    /// <returns>The verdict.</returns>
    public static async Task<DriftVerdict> RunAsync(
        string mediaPath,
        string srtText,
        string? ffmpegPath,
        ILogger logger,
        CancellationToken ct,
        string audioMap = "0:a:0")
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            return Skip("ffmpeg not available");
        }

        (double[] starts, double[] ends) = ParseCues(srtText);
        if (starts.Length < 40)
        {
            return Skip($"only {starts.Length} cues");
        }

        float[] samples;
        try
        {
            samples = await DecodeMonoAsync(ffmpegPath, mediaPath, ct, audioMap).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[SubDL] drift gate: audio decode failed for {File}", Path.GetFileName(mediaPath));
            return Skip("audio decode failed");
        }

        if (samples.Length == 0)
        {
            return Skip("no audio samples");
        }

        double durationSec = samples.Length / (double)SampleRate;
        (double[] onSec, double[] offSec) = SpeechIslands(samples);
        if (onSec.Length == 0)
        {
            return Skip("no speech islands");
        }

        try
        {
            return DriftDetector.Detect(starts, ends, onSec, offSec, durationSec);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[SubDL] drift gate: detector failed for {File}", Path.GetFileName(mediaPath));
            return Skip("detector error");
        }
    }

    private static DriftVerdict Skip(string reason) => new() { Ran = false, SkipReason = reason };

    /// <summary>Cue start and end times in seconds, ascending by start.</summary>
    public static (double[] Starts, double[] Ends) ParseCues(string srtText)
    {
        var starts = new List<double>();
        var ends = new List<double>();
        foreach (Match m in CueRegex.Matches(srtText))
        {
            double s = ToSeconds(m, 1);
            double e = ToSeconds(m, 5);
            starts.Add(s);
            ends.Add(e);
        }

        if (starts.Count == 0)
        {
            return ([], []);
        }

        var order = Enumerable.Range(0, starts.Count).OrderBy(i => starts[i]).ToArray();
        return (order.Select(i => starts[i]).ToArray(), order.Select(i => ends[i]).ToArray());
    }

    private static double ToSeconds(Match m, int g)
        => (int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) * 3600.0)
           + (int.Parse(m.Groups[g + 1].Value, CultureInfo.InvariantCulture) * 60.0)
           + int.Parse(m.Groups[g + 2].Value, CultureInfo.InvariantCulture)
           + (int.Parse(m.Groups[g + 3].Value, CultureInfo.InvariantCulture) / 1000.0);

    /// <summary>
    /// Decodes one audio stream to mono float samples at <see cref="SampleRate"/>.
    /// Reads the raw stream from stdout, so no temp file is written.
    /// </summary>
    /// <param name="ffmpegPath">ffmpeg executable.</param>
    /// <param name="mediaPath">Container to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="audioMap">
    /// Audio stream to decode, e.g. <c>0:a:1</c>. Defaults to the first. The caller
    /// decides the track by language (F-M296, <see cref="AudioTrackChoice"/>): a
    /// hard <c>0:a:0</c> reads the wrong track on ~9 % of this library's files,
    /// typically an Italian release whose first track is the dub and whose English
    /// original sits on track 1.
    /// </param>
    /// <returns>The samples.</returns>
    public static async Task<float[]> DecodeMonoAsync(
        string ffmpegPath, string mediaPath, CancellationToken ct, string audioMap = "0:a:0")
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(mediaPath);
        psi.ArgumentList.Add("-map");
        // One named audio stream only. No trailing '?' — an index that does not exist
        // must fail loudly rather than silently write a different stream (the trap
        // documented in FfmpegTools.ExtractAllAsync). The track itself is chosen by
        // LANGUAGE in AudioTrackChoice; the index is the result, not the rule.
        psi.ArgumentList.Add(audioMap);
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(SampleRate.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("f32le");
        psi.ArgumentList.Add("-");

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            return [];
        }

        using var ms = new MemoryStream();
        Task copy = proc.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        Task drain = proc.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(copy, drain).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        byte[] raw = ms.ToArray();
        if (raw.Length < 4)
        {
            return [];
        }

        var samples = new float[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, samples, 0, samples.Length * 4);
        return samples;
    }

    /// <summary>
    /// Speech islands from the frame envelope: hysteresis on the 10th/90th
    /// percentile band, minimum length and minimum gap applied.
    /// </summary>
    public static (double[] OnSec, double[] OffSec) SpeechIslands(float[] samples)
    {
        int frame = (int)Math.Round(SampleRate * FrameSec);
        int n = samples.Length / frame;
        if (n < 10)
        {
            return ([], []);
        }

        var level = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            int baseIdx = i * frame;
            for (int j = 0; j < frame; j++)
            {
                double v = samples[baseIdx + j];
                sum += v * v;
            }

            level[i] = 20.0 * Math.Log10(Math.Sqrt(sum / frame) + 1e-9);
        }

        double[] sorted = level.OrderBy(v => v).ToArray();
        double lo = Percentile(sorted, 0.10);
        double hi = Percentile(sorted, 0.90);
        if (hi - lo < 6.0)
        {
            return ([], []); // no speech/silence contrast — music, ambience
        }

        double th = lo + (0.55 * (hi - lo));
        double tl = lo + (0.30 * (hi - lo));

        var on = new List<double>();
        var off = new List<double>();
        bool state = false;
        int islandStart = -1;
        double lastEnd = double.NegativeInfinity;

        for (int i = 0; i < n; i++)
        {
            if (!state)
            {
                if (level[i] > th)
                {
                    state = true;
                    islandStart = i;
                }
            }
            else if (level[i] < tl)
            {
                state = false;
                double s = islandStart * FrameSec;
                double e = i * FrameSec;
                if (e - s >= MinIslandSec && s - lastEnd >= MinGapSec)
                {
                    on.Add(s);
                    off.Add(e);
                    lastEnd = e;
                }
            }
        }

        if (state)
        {
            double s = islandStart * FrameSec;
            double e = n * FrameSec;
            if (e - s >= MinIslandSec && s - lastEnd >= MinGapSec)
            {
                on.Add(s);
                off.Add(e);
            }
        }

        return (on.ToArray(), off.ToArray());
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        double idx = p * (sorted.Length - 1);
        int i0 = (int)Math.Floor(idx);
        int i1 = Math.Min(i0 + 1, sorted.Length - 1);
        double frac = idx - i0;
        return (sorted[i0] * (1 - frac)) + (sorted[i1] * frac);
    }
}
