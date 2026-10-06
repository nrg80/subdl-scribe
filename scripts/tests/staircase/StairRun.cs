// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// F-M300 end-to-end check on REAL material: the staircase correction, driven through the
// shipped code and judged against ground truth that took no part in the measurement.
//
// WHY THIS EXISTS SEPARATELY FROM T115. T115 proves the arithmetic on a synthetic episode
// whose cues were built from a known burst list. That is necessary and not sufficient: it
// cannot show that the detector finds REAL act breaks in REAL audio, and it cannot show that
// the two parts compose — decode real audio, detect real segments, shift real cues, land on
// real speech.
//
// THE GROUND TRUTH IS THE SUBTITLE INSIDE THE CONTAINER, and it is never handed to the gate.
// It is extracted here, kept aside, and used only to SCORE the result at the end. A
// correction scored with the detector that produced it is the inverse of its own measurement
// and always reports success; that is the trap this file is shaped to avoid.
//
// WHAT IS PLANTED, AND WHY THAT SHAPE. The real defect this feature exists for is a release
// whose subtitle was timed against a DIFFERENT cut: constant for a while, then a jump, then
// constant again. So the downloaded file is faked by pushing every cue at or after a
// threshold back by a fixed amount, with two thresholds — a re-cut with two steps. The
// detector must find the movement on its own, without being told where it is.
//
// Usage: dotnet run -- <mediaFile> <subStreamIndex> [audioMap]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Qa;

namespace StairTest;

public static class StairRun
{
    private static readonly Regex Ts = new(
        @"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})[,.](\d{3})",
        RegexOptions.Compiled);

    private static readonly (double AtSec, double OffsetSec)[] Planted =
    {
        (15 * 60, -2.5),
        (25 * 60, -4.5),
    };

    public static async Task<int> Main(string[] args)
    {
        // 2026-10-06: second entry, `measure`. The three episodes that drifted worst in the field
        // have NO text subtitle in the container (PGS image only, or none at all), so the plant-and-
        // score route above has no ground truth to score against. This mode measures what the
        // shipped code DOES to a given subtitle against the AUDIO — the only yardstick those files
        // have — and reports it before and after the correction. The subtitle is never handed to
        // the gate as truth; it is the thing being measured.
        //
        // Usage: stairrun measure <mediaFile> <srtFile> [audioMap]
        if (args.Length >= 3 && args[0] == "measure")
        {
            return await MeasureAsync(args[1], args[2], args.Length > 3 ? args[3] : "0:a:0")
                .ConfigureAwait(false);
        }

        if (args.Length < 2)
        {
            Console.WriteLine("usage: stairrun <mediaFile> <subStreamIndex> [audioMap]");
            Console.WriteLine("       stairrun measure <mediaFile> <srtFile> [audioMap]");
            return 2;
        }

        string media = args[0];
        string subIndex = args[1];
        string audioMap = args.Length > 2 && args[2] != "clean" ? args[2] : "0:a:0";
        bool clean = args.Contains("clean");

        if (!File.Exists(media))
        {
            Console.WriteLine($"no such media: {media}");
            return 2;
        }

        string ffmpeg = FindFfmpeg();
        Console.WriteLine("=== F-M300 staircase correction — end-to-end on real material ===");
        Console.WriteLine($"    {Path.GetFileName(media)}");
        Console.WriteLine($"    ffmpeg: {ffmpeg}");
        Console.WriteLine();

        // 1. Ground truth: the subtitle as authored, out of the container.
        string truthPath = Path.Combine(Path.GetTempPath(), "stair-truth.srt");
        if (!ExtractSubtitle(ffmpeg, media, subIndex, truthPath))
        {
            Console.WriteLine("  FAIL: could not extract the embedded subtitle (ground truth)");
            return 1;
        }

        string truth = await File.ReadAllTextAsync(truthPath).ConfigureAwait(false);
        double[] truthStarts = Starts(truth);
        Console.WriteLine($"  ground truth   {truthStarts.Length,4} cues");

        if (truthStarts.Length < 40)
        {
            Console.WriteLine("  FAIL: too few cues to measure anything");
            return 1;
        }

        // 2. The "downloaded" file: the same subtitle, re-cut. The gate never sees `truth`.
        //
        // `clean` skips the planting and measures the embedded subtitle AS IT IS. That is the
        // control this whole file needs: it establishes whether the container's own subtitle is
        // in sync with the audio track, which is the assumption the scoring rests on. If the
        // audio holds a different version than the subtitle was timed to, the gate will report a
        // real offset for an untouched file — and then this test measures the mismatch between
        // two sources rather than the quality of the correction.
        string planted = clean ? truth : Plant(truth, Planted);
        double[] plantedStarts = Starts(planted);
        double before = Worst(plantedStarts, truthStarts);
        if (clean)
        {
            Console.WriteLine("  control: the embedded subtitle UNTOUCHED — a correct gate reports "
                + "offset ~0 here");
        }
        else
        {
            Console.WriteLine($"  planted        {Planted.Length} step(s) over a "
                + $"{Planted[^1].OffsetSec:0.0}s range -> worst line {before:0.00}s off");
        }

        Console.WriteLine();

        // 3. The shipped gate, on the real audio. Nothing about the steps is passed in.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var sw = Stopwatch.StartNew();
        DriftVerdict v = await DriftGate
            .RunAsync(media, planted, ffmpeg, new NullLogger(), cts.Token, audioMap)
            .ConfigureAwait(false);
        sw.Stop();

        Console.WriteLine($"  gate [{sw.Elapsed.TotalSeconds:0.0}s] {v.Describe()}");
        if (!v.Ran)
        {
            Console.WriteLine($"  FAIL: the gate did not run ({v.SkipReason}) — nothing measured");
            return 1;
        }

        Console.WriteLine($"    drifts={v.Drifts}  segments={v.SegmentOffsetsSec.Count}  "
            + $"span={v.SpanSec:0.00}s");
        for (int i = 0; i < v.SegmentOffsetsSec.Count; i++)
        {
            Console.WriteLine($"      seg {i}: from {v.SegmentStartTimesSec[i] / 60:0.0}min "
                + $"offset {v.SegmentOffsetsSec[i]:+0.00;-0.00}s");
        }

        if (!v.Drifts)
        {
            if (clean)
            {
                // The CONTROL's best possible outcome: the gate finds NO drift on an UNTOUCHED file.
                // That is what a correct gate does, and it is the strongest available evidence that
                // the embedded subtitle really is in sync with the audio — so the scoring yardstick
                // this episode is judged with is sound. Reporting it as a failure (which this branch
                // did until 06.10.2026) inverted the meaning of the one run that validates the other.
                Console.WriteLine();
                Console.WriteLine($"  CONTROL OK: the untouched embedded subtitle reads "
                    + $"{v.MedianOffsetSec:+0.00;-0.00}s and NO drift — the yardstick is sound");
                Console.WriteLine("CONTROL DONE");
                return 0;
            }

            Console.WriteLine("  FAIL: two planted steps of 2.5s and 4.5s were not detected");
            return 1;
        }

        // 4. The correction, applied through the shipped function.
        (bool ok, string corrected, string why, int guarded) = SubtitleSync.ShiftByStaircase(
            planted, v.SegmentStartTimesSec, v.SegmentOffsetsSec);
        Console.WriteLine();
        Console.WriteLine($"  correction: ok={ok} guarded={guarded} ({why})");
        if (!ok)
        {
            Console.WriteLine("  FAIL: the correction refused");
            return 1;
        }

        // 5. Score against the ground truth — the file the gate never saw.
        double[] fixedStarts = Starts(corrected);
        if (fixedStarts.Length != truthStarts.Length)
        {
            Console.WriteLine($"  FAIL: cue count changed {truthStarts.Length} -> {fixedStarts.Length}");
            return 1;
        }

        double after = Worst(fixedStarts, truthStarts);
        Console.WriteLine();
        Console.WriteLine("=== result ===");
        Console.WriteLine($"  worst line to a true cue:  {before:0.00}s  ->  {after:0.00}s");
        Console.WriteLine($"  cues: {plantedStarts.Length}  (unchanged: "
            + $"{fixedStarts.Length == plantedStarts.Length})");

        // WHERE the residual sits, per segment plus the tail after the last one. Aggregate
        // numbers cannot tell a spurious segment from an unlucky one: a segment that covers
        // two cues and carries a nonsense offset looks identical to a real step in a total, and
        // only the per-region view shows which of the two happened. Ground truth is the score,
        // as everywhere else in this file.
        Console.WriteLine();
        Console.WriteLine("  per segment (cues, worst line BEFORE -> AFTER, offset applied):");
        var edges = new List<double>(v.SegmentStartTimesSec);
        edges.Add(double.MaxValue);
        for (int i = 0; i < v.SegmentOffsetsSec.Count; i++)
        {
            double lo = v.SegmentStartTimesSec[i];
            double hi = i + 1 < v.SegmentStartTimesSec.Count ? v.SegmentStartTimesSec[i + 1] : double.MaxValue;
            int n = 0;
            double wBefore = 0;
            double wAfter = 0;
            for (int k = 0; k < plantedStarts.Length; k++)
            {
                if (plantedStarts[k] < lo || plantedStarts[k] >= hi)
                {
                    continue;
                }

                n++;
                wBefore = Math.Max(wBefore, Math.Abs(plantedStarts[k] - truthStarts[k]));
                wAfter = Math.Max(wAfter, Math.Abs(fixedStarts[k] - truthStarts[k]));
            }

            Console.WriteLine($"    seg {i} from {lo / 60,5:0.0}min  {n,4} cues  "
                + $"{wBefore,6:0.00}s -> {wAfter,6:0.00}s  offset {v.SegmentOffsetsSec[i]:+0.00;-0.00}s");
        }

        await File.WriteAllTextAsync("/tmp/stair-corrected.srt", corrected).ConfigureAwait(false);
        Console.WriteLine("  corrected file: /tmp/stair-corrected.srt");

        int fails = 0;
        if (clean)
        {
            // The CONTROL. Nothing was planted, so the only way a shift can help is if the
            // container's subtitle is genuinely off — in which case the gate is right and this
            // test's ground truth is the wrong yardstick. It says which, it does not guess.
            if (Math.Abs(v.MedianOffsetSec) > 1.5)
            {
                Console.WriteLine($"  NOTE: the UNTOUCHED embedded subtitle reads {v.MedianOffsetSec:+0.00;-0.00}s "
                    + "off the audio — the container's subtitle is NOT synced to the audio track");
                Console.WriteLine("        selected here, so this episode cannot score a correction: the");
                Console.WriteLine("        comparison would measure that mismatch, not the staircase.");
            }
            else
            {
                Console.WriteLine($"  CONTROL OK: untouched file reads {v.MedianOffsetSec:+0.00;-0.00}s "
                    + "— the scoring yardstick is sound");
            }
        }
        else if (after >= before)
        {
            Console.WriteLine("  FAIL: the correction did not improve the file");
            fails++;
        }

        if (!clean && after > 1.0)
        {
            Console.WriteLine($"  FAIL: residual {after:0.00}s is above the 1.0s bound");
            fails++;
        }

        if (!clean && guarded > plantedStarts.Length / 10)
        {
            Console.WriteLine($"  FAIL: the order guard fired on {guarded} cues — a broad fire "
                + "flattens the staircase into one constant shift");
            fails++;
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? (clean ? "CONTROL DONE" : "STAIRCASE OK") : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// Measures the shipped correction against the AUDIO on a real file that carries no text
    /// subtitle. Reports the verdict, the segments, and the applied shift — the same numbers the
    /// field episode produced, so a regression is visible instead of argued about.
    /// </summary>
    private static async Task<int> MeasureAsync(string media, string srtPath, string audioMap)
    {
        if (!File.Exists(media) || !File.Exists(srtPath))
        {
            Console.WriteLine($"missing input: media={File.Exists(media)} srt={File.Exists(srtPath)}");
            return 2;
        }

        string ffmpeg = FindFfmpeg();
        string text = await File.ReadAllTextAsync(srtPath).ConfigureAwait(false);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));

        Console.WriteLine("=== F-M300 staircase — measured against the AUDIO (no text subtitle in container) ===");
        Console.WriteLine($"    {Path.GetFileName(media)}");
        Console.WriteLine($"    {Path.GetFileName(srtPath)}");
        Console.WriteLine($"    audioMap {audioMap}   ffmpeg {ffmpeg}");
        Console.WriteLine();

        var sw = Stopwatch.StartNew();
        DriftVerdict v = await DriftGate
            .RunAsync(media, text, ffmpeg, new NullLogger(), cts.Token, audioMap)
            .ConfigureAwait(false);
        sw.Stop();

        Console.WriteLine($"  gate [{sw.Elapsed.TotalSeconds:0.0}s] {v.Describe()}");
        if (!v.Ran)
        {
            Console.WriteLine($"  the gate did not run ({v.SkipReason})");
            return 1;
        }

        Console.WriteLine($"    drifts={v.Drifts}  median={v.MedianOffsetSec:+0.00;-0.00}s  "
            + $"segments={v.SegmentOffsetsSec.Count}  span={v.SpanSec:0.00}s");
        for (int i = 0; i < v.SegmentOffsetsSec.Count; i++)
        {
            Console.WriteLine($"      seg {i}: from {v.SegmentStartTimesSec[i] / 60:0.0}min"
                + $"  offset {v.SegmentOffsetsSec[i]:+0.00;-0.00}s");
        }

        if (!v.Drifts)
        {
            Console.WriteLine();
            Console.WriteLine("  VERDICT: the code finds NO drift here — the file is left alone.");
            return 0;
        }

        (bool ok, string corrected, string why, int guarded) = SubtitleSync.ShiftByStaircase(
            text, v.SegmentStartTimesSec, v.SegmentOffsetsSec);
        Console.WriteLine();
        Console.WriteLine($"  correction: ok={ok} guarded={guarded} ({why})");

        if (!ok)
        {
            Console.WriteLine("  VERDICT: the code REFUSES — the file is left alone.");
            return 0;
        }

        // Re-measure the corrected text: what the code's own gate says about its own output.
        // That is the number the log line carries, and the only one this material can supply.
        DriftVerdict after = await DriftGate
            .RunAsync(media, corrected, ffmpeg, new NullLogger(), cts.Token, audioMap)
            .ConfigureAwait(false);
        Console.WriteLine($"  after        {after.Describe()}");
        Console.WriteLine($"    drifts={after.Drifts}  median={after.MedianOffsetSec:+0.00;-0.00}s  "
            + $"segments={after.SegmentOffsetsSec.Count}  span={after.SpanSec:0.00}s");

        Console.WriteLine();
        Console.WriteLine("  NOTE: this measures the code against the AUDIO only. It cannot say the file is");
        Console.WriteLine("        now correct — only whether the code found drift, what it did, and whether");
        Console.WriteLine("        its own gate still sees drift afterwards. A true score needs ground truth");
        Console.WriteLine("        this container does not carry.");

        await File.WriteAllTextAsync("/tmp/stair-corrected.srt", corrected).ConfigureAwait(false);
        Console.WriteLine("  corrected file: /tmp/stair-corrected.srt");
        return 0;
    }

    /// <summary>Cue start times, in seconds.</summary>
    private static double[] Starts(string srt)
    {
        var list = new List<double>();
        foreach (Match m in Ts.Matches(srt))
        {
            list.Add(Sec(m));
        }

        return list.ToArray();
    }

    private static double Sec(Match m) => (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600)
        + (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60)
        + int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)
        + (int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) / 1000.0);

    /// <summary>The largest distance from a cue to the cue at the same index.</summary>
    private static double Worst(double[] got, double[] want)
    {
        int n = Math.Min(got.Length, want.Length);
        double worst = 0;
        for (int i = 0; i < n; i++)
        {
            worst = Math.Max(worst, Math.Abs(got[i] - want[i]));
        }

        return worst;
    }

    /// <summary>Pushes every cue at or after a threshold back, per the step table.</summary>
    private static string Plant(string srt, (double AtSec, double OffsetSec)[] steps)
    {
        return Ts.Replace(srt, m =>
        {
            double start = Sec(m);
            double off = steps.Where(s => start >= s.AtSec).Sum(s => s.OffsetSec);
            return Stamp(start + off) + " --> " + Stamp(Sec(m, 4) + off);
        });
    }

    private static string Stamp(double sec)
    {
        if (sec < 0)
        {
            sec = 0;
        }

        var t = TimeSpan.FromSeconds(sec);
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}",
            (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
    }

    /// <summary>Second timestamp of a match (groups start at 0), offset by 4.</summary>
    private static double Sec(Match m, int _)
    {
        double s = (int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture) * 3600)
            + (int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) * 60)
            + int.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture)
            + (int.Parse(m.Groups[8].Value, CultureInfo.InvariantCulture) / 1000.0);
        return s;
    }

    private static bool ExtractSubtitle(string ffmpeg, string media, string subIndex, string outPath)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(media);
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:s:" + subIndex);
        psi.ArgumentList.Add("-c:s");
        psi.ArgumentList.Add("srt");
        psi.ArgumentList.Add(outPath);

        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode == 0 && File.Exists(outPath) && new FileInfo(outPath).Length > 100;
    }

    private static string FindFfmpeg()
    {
        string[] cands =
        {
            "/usr/bin/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/opt/data/jf-test/ffmpeg/ffmpeg-git-20240629-arm64-static/ffmpeg",
        };
        return cands.FirstOrDefault(File.Exists) ?? "ffmpeg";
    }

    private sealed class NullLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception exception,
            Func<TState, Exception, string> formatter)
        {
        }
    }
}
