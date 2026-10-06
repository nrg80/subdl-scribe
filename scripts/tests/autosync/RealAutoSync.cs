// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// F-M296 end-to-end test on REAL material, driving the FEATURE (not a copy of it)
// and checking its output against a source that took no part in the measurement.
//
// WHY THE FIRST VERSION OF THIS TEST WAS WORTHLESS
//
// It checked the result by the SPREAD of the anchor offsets against the plain
// subtitle — and reported 0.00 s before and 0.00 s after, in every case, including
// the ones that were wrong. A constant shift moves every cue by the same amount, so
// the spread of the offsets is 0 BY CONSTRUCTION whatever the shift is. The check
// could not have failed, which makes it a decoration, not a test. What has to be
// checked is the MEDIAN offset — where the file sits, not how much it scatters. The
// same mistake would have hidden a sign error in the feature itself.
//
// AND THE SIGN MUST BE ESTABLISHED, NOT ASSUMED
//
// The detector's number and the correction to apply are related by a sign, and
// getting it backwards doubles the error instead of removing it (measured elsewhere
// in this project: −1.50 s became −3.00 s). So this test does not assume the
// relation: it plants a shift, reads the detector, applies BOTH candidate
// directions, and prints which one lands the file on the plain subtitle. The
// feature's own value is then compared against that measured answer.
//
// THE CASES
//
//   1. SIGN — plant +5 s, apply +measured and −measured, show which one lands on the
//      plain subtitle. Prints the evidence; also asserts it, so a future sign flip
//      fails loudly.
//   2. CLEAN — a subtitle already in sync must come back unapplied. (The plain file
//      of this episode measures about −0.50 s against its own audio, which is this
//      material's measurement floor and the reason MinShiftSec is not smaller.)
//   3. PLANTED — a known shift is removed: the feature runs, and its returned text is
//      checked against the plain subtitle by MEDIAN anchor offset.
//
// Run: dotnet run --project scripts/tests/autosync -- real <dir> <basename>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Pipeline;
using Jellyfin.Plugin.SubdlScribe.Qa;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoSyncTest;

/// <summary>End-to-end driver: real media file, real ffmpeg, the real feature.</summary>
public static class RealAutoSync
{
    /// <summary>Runs the end-to-end cases.</summary>
    /// <param name="args">["real", dir, basename].</param>
    /// <returns>0 when every case passed, 1 otherwise.</returns>
    public static async System.Threading.Tasks.Task<int> RunAsync(string[] args)
    {
        string dir = args[1];
        string baseName = args[2];
        string media = null;
        foreach (string ext in new[] { ".mkv", ".mp4" })
        {
            string p = Path.Combine(dir, baseName + ext);
            if (File.Exists(p))
            {
                media = p;
                break;
            }
        }

        if (media == null)
        {
            Console.WriteLine($"no media file for {baseName} in {dir}. The .mkv carries the audio; a subtitle alone cannot be tested.");
            return 1;
        }

        string plain = Path.Combine(dir, baseName + ".en.srt");
        if (!File.Exists(plain))
        {
            Console.WriteLine($"reference {Path.GetFileName(plain)} missing — without it the result cannot be checked independently.");
            return 1;
        }

        string ffmpeg = FfmpegTools.ResolvePath(
            new Jellyfin.Plugin.SubdlScribe.Configuration.PluginConfiguration(), NullLogger.Instance) ?? "ffmpeg";
        var log = NullLogger.Instance;

        Console.WriteLine("=== F-M296 auto-sync — end-to-end on real audio ===");
        Console.WriteLine($"    media     {Path.GetFileName(media)}");
        Console.WriteLine($"    reference {Path.GetFileName(plain)}  (independent: it takes no part in the shift)");
        Console.WriteLine($"    threshold MinShiftSec {SubtitleSync.MinShiftSec:0.00}s, limit {SubtitleSync.MaxShiftSec:0}s");
        Console.WriteLine();

        string plainText = await File.ReadAllTextAsync(plain).ConfigureAwait(false);
        int failures = 0;

        // ---- Case 1: establish the SIGN on this material, with evidence.
        Console.WriteLine("[1] sign: which direction lands the file on the plain subtitle?");
        {
            const double planted = 5.0;
            (_, string shifted, _) = SubtitleSync.ShiftBy(plainText, planted);
            var v = await DriftGate.RunAsync(media, shifted, ffmpeg, log, default).ConfigureAwait(false);
            double m = v.MedianOffsetSec;
            Console.WriteLine($"    planted {planted:+0.0;-0.0}s -> detector reads {m:+0.00;-0.00}s (drift={v.Drifts})");

            (_, string minusM, _) = SubtitleSync.ShiftBy(shifted, -m);
            (_, string plusM, _) = SubtitleSync.ShiftBy(shifted, +m);
            double medMinus = MedianDiff(plainText, minusM);
            double medPlus = MedianDiff(plainText, plusM);
            Console.WriteLine($"    apply {-m:+0.00;-0.00}s -> median vs. plain {medMinus:+0.00;-0.00}s");
            Console.WriteLine($"    apply {+m:+0.00;-0.00}s -> median vs. plain {medPlus:+0.00;-0.00}s");

            // The correction must be the one whose median against the plain subtitle is
            // near zero; asserting it makes a future sign flip fail here.
            bool ok = double.IsNaN(medMinus) || Math.Abs(medMinus) < Math.Abs(medPlus);
            bool nearZero = double.IsNaN(medMinus) || Math.Abs(medMinus) < 0.7;
            Console.WriteLine($"    [{(ok && nearZero ? "PASS" : "FAIL")}] the correction is −detector "
                + $"(and it lands within 0.7 s of the plain subtitle)");
            failures += ok && nearZero ? 0 : 1;
        }

        Console.WriteLine();

        // ---- Case 2: a subtitle already in sync must not be moved.
        Console.WriteLine("[2] clean subtitle: the feature must return \"not applied\"");
        {
            var r = await SubtitleSync.SyncAsync(media, plainText, "0:a:0", ffmpeg, log, default)
                .ConfigureAwait(false);
            var v = await DriftGate.RunAsync(media, plainText, ffmpeg, log, default).ConfigureAwait(false);
            Console.WriteLine($"    detector reads {v.MedianOffsetSec:+0.00;-0.00}s; feature applied={r.Applied} ({r.Reason})");
            bool ok = !r.Applied;
            Console.WriteLine($"    [{(ok ? "PASS" : "FAIL")}] an in-sync file is left alone (floor {SubtitleSync.MinShiftSec:0.0}s "
                + "> this material's ~0.5 s measurement offset)");
            failures += ok ? 0 : 1;
        }

        Console.WriteLine();

        // ---- Case 3: a planted shift is removed, checked by MEDIAN against plain.
        Console.WriteLine("[3] planted constant shift, removed by the feature and checked by MEDIAN");
        foreach (double planted in new[] { 2.5, -1.8, 6.0 })
        {
            (bool sh, string shifted, string whySh) = SubtitleSync.ShiftBy(plainText, planted);
            if (!sh)
            {
                Console.WriteLine($"    [FAIL] could not plant {planted:+0.0;-0.0}s: {whySh}");
                failures++;
                continue;
            }

            double plantedMedian = MedianDiff(plainText, shifted);
            var r = await SubtitleSync.SyncAsync(media, shifted, "0:a:0", ffmpeg, log, default)
                .ConfigureAwait(false);
            // SyncAsync returns the CORRECTED TEXT and the shift it applied — the test must
            // use the feature's output, not re-derive it. An earlier version of this test
            // shifted again by the raw detector value, which inverted a correct correction
            // back into a wrong one.
            double afterMedian = r.Applied ? MedianDiff(plainText, r.Corrected) : plantedMedian;
            bool applied = r.Applied;
            bool landed = Math.Abs(afterMedian) < 0.7;
            bool better = Math.Abs(afterMedian) < Math.Abs(plantedMedian);
            Console.WriteLine($"    planted {planted:+0.0;-0.0}s: was {plantedMedian:+0.00;-0.00}s from plain; "
                + $"feature {r.Reason}; now {afterMedian:+0.00;-0.00}s");
            Console.WriteLine($"    [{(applied && landed && better ? "PASS" : "FAIL")}] applied, landed within 0.7 s, better than before");
            failures += applied && landed && better ? 0 : 1;
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CASES PASSED" : $"{failures} CASE(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// MEDIAN offset between two subtitles, paired by identical text. Median, not
    /// spread: a constant shift leaves the spread at zero whatever its size, so only
    /// the median can tell whether the file moved into place. NaN when fewer than 20
    /// unique pairs exist — an honest "not measurable" rather than a number from too
    /// little data.
    /// </summary>
    private static double MedianDiff(string refText, string otherText)
    {
        static List<(double S, string C)> Parse(string t) =>
            System.Text.RegularExpressions.Regex
                .Split(t.Replace("\r\n", "\n", StringComparison.Ordinal).Trim(), @"\n\s*\n")
                .Select(b =>
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        b, @"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->");
                    if (!m.Success)
                    {
                        return (S: -1.0, C: string.Empty);
                    }

                    string body = string.Join(
                        " ",
                        b.Split('\n')
                            .Where(l => !l.Contains("-->", StringComparison.Ordinal)
                                        && !System.Text.RegularExpressions.Regex.IsMatch(l.Trim(), @"^\d+$")))
                        .Trim();
                    string clean = System.Text.RegularExpressions.Regex
                        .Replace(System.Text.RegularExpressions.Regex
                            .Replace(body, @"\[[^\]]*\]", " "), @"\s+", " ").Trim();
                    return (S: (int.Parse(m.Groups[1].Value) * 3600.0)
                               + (int.Parse(m.Groups[2].Value) * 60.0)
                               + int.Parse(m.Groups[3].Value)
                               + (int.Parse(m.Groups[4].Value) / 1000.0), C: clean);
                })
                .Where(c => c.S >= 0)
                .ToList();

        var a = Parse(refText);
        var b = Parse(otherText);
        var idx = a.Where(c => c.C.Length >= 15)
            .GroupBy(c => c.C)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().S);

        var diffs = b.Where(c => c.C.Length >= 15 && idx.ContainsKey(c.C))
            .Select(c => c.S - idx[c.C])
            .OrderBy(d => d)
            .ToList();

        if (diffs.Count < 20)
        {
            return double.NaN;
        }

        int n = diffs.Count;
        return n % 2 == 1 ? diffs[n / 2] : (diffs[(n / 2) - 1] + diffs[n / 2]) / 2.0;
    }
}
