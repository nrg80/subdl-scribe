// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// F-M297 end-to-end proof: the REFUSAL rule on the material that failed the audio path.
//
// WHY THIS EXISTS ON TOP OF T112
//
// T112 proves the arithmetic and the reference rule. What it does NOT prove is the claim the
// whole feature rests on: that on the two episodes the AUDIO path made WORSE, the anchor path
// with its self-check does better and keeps the file when it cannot. Those two files are
// S01E04 (audio: worst 8.00 s -> 21.30 s) and S01E06 (audio: worst 2.47 s -> 11.91 s).
//
// The check is run against the .2 slot — the text-anchor result the user confirmed by ear. It
// is a witness, not the method: the correction itself never sees it, and it is read only to
// answer "did the result land where an independent route put it".
//
// Run: dotnet run --project scripts/tests/autosync -c Release -- refusal
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Qa;

namespace AutoSyncTest;

internal static class AnchorRefusal
{
    internal static int Run(string[] args)
    {
        Console.WriteLine("=== F-M297: the two files the AUDIO path made worse ===");
        Console.WriteLine("    (audio: S01E04 8.00->21.30 s, S01E06 2.47->11.91 s)");
        Console.WriteLine();

        var cases = new[]
        {
            ("/data/movies/Person.of.Interest/S01", "Person of Interest S01E04 720p Bluray x264 AC3 - TheKing"),
            ("/data/movies/Person.of.Interest/S01", "Person of Interest S01E06 720p Bluray x264 AC3 - TheKing")
        };

        int failed = 0;
        foreach ((string dir, string b) in cases)
        {
            var plain = AnchorSync.Parse(File.ReadAllText(Path.Combine(dir, b + ".en.srt")));
            var target = AnchorSync.Parse(File.ReadAllText(Path.Combine(dir, b + ".en.sdh.srt")));
            var slot2 = AnchorSync.Parse(File.ReadAllText(Path.Combine(dir, b + ".en.sdh.2.srt")));

            AnchorSync.Residuals qs0 = AnchorSync.Measure(slot2, target);
            AnchorSync.Result r = AnchorSync.Correct(plain, target);
            AnchorSync.Residuals qs1 = r.Applied ? AnchorSync.Measure(slot2, r.Cues) : qs0;

            Console.WriteLine($"  {b[^40..]}");
            Console.WriteLine($"    anchors against the plain sibling: {r.Before.Count} pairs");
            Console.WriteLine($"    correction: {r.Reason}");
            Console.WriteLine($"    applied={r.Applied} | overlaps clamped={r.ClampedOverlaps}");
            Console.WriteLine($"    QS against .2 (an independent route): worst {qs0.WorstSec:0.00} -> {qs1.WorstSec:0.00}s, "
                              + $"median {qs0.MedianSec:0.00} -> {qs1.MedianSec:0.00}s");

            bool ok = qs1.WorstSec < qs0.WorstSec;
            Console.WriteLine($"    [{(ok ? "ok" : "FAIL")}] the anchor path does better than 'left as downloaded' here");
            if (!ok)
            {
                failed++;
            }

            // Text and order survive — a correction may move a cue in time and nothing else.
            bool sameText = r.Cues.Count == target.Count
                && r.Cues.Select(c => c.Text).SequenceEqual(target.Select(c => c.Text));
            bool sameTimes = r.Cues.Count == target.Count
                && r.Cues.Zip(target, (a, x) => Math.Abs(a.StartSec - x.StartSec)).All(d => d <= 20.0 + 1e-6);
            Console.WriteLine($"    [{(sameText ? "ok" : "FAIL")}] text and order unchanged");
            Console.WriteLine($"    [{(sameTimes ? "ok" : "FAIL")}] every shift inside the plausible range");
            if (!sameText || !sameTimes)
            {
                failed++;
            }

            Console.WriteLine();
        }

        Console.WriteLine(failed == 0
            ? "  every check passed"
            : $"  {failed} check(s) FAILED");
        return failed == 0 ? 0 : 1;
    }
}
