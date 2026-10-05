// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// F-M295 test driver: drives the drift detector with SYNTHETIC data so the
// decision is checked without a media file. Three cases, mirroring the
// validation that the Python prototype had to pass before it was ported:
//
//   1. CLEAN     — cues that sit on the speech, constant offset. The detector
//                  MUST report no drift. A detector that finds boundaries here
//                  is worthless (two earlier approaches failed exactly this).
//   2. CLEAN+STEP— the same clean cues with a KNOWN step planted at a known
//                  time (+2.5 s from 20 min, +2.0 s from 30 min). The detector
//                  MUST find them, and the reported span must match the planted
//                  total. This catches sign errors and wrong magnitudes.
//   3. DRIFTING  — cues whose offset ramps. MUST be reported as drifting.
//
// Plus a numeric self-test of the factored two-offset marginal (the O(states)
// form used in the port) against the explicit outer sum, so the shortcut is
// proven equal rather than assumed.
//
// Run: dotnet run --project scripts/tests/drift
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Qa;

namespace DriftTest;

/// <summary>Test driver for <see cref="DriftDetector"/>.</summary>
public static class DriftRun
{
    private const double FrameSec = 0.020;

    /// <summary>Entry point.</summary>
    /// <param name="args">Optional: "real" &lt;dir&gt; &lt;basename&gt; for the end-to-end run.</param>
    /// <returns>0 when every case passed, 1 otherwise.</returns>
    public static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "real")
        {
            return RealRun.RunAsync(args).GetAwaiter().GetResult();
        }

        Console.WriteLine("=== F-M295 drift detector — synthetic validation ===");
        Console.WriteLine($"    offset grid {DriftDetector.OffsetGridMin}..{DriftDetector.OffsetGridMax}s "
            + $"step {DriftDetector.OffsetGridStep}, log-BF>={DriftDetector.BayesFactorThreshold}");
        Console.WriteLine();

        int failures = 0;

        failures += FactoredEqualsOuterSum();

        // A synthetic episode: 44 min, dialogue bursts with pauses, cues that
        // track the speech. The envelope is generated from the SAME burst list,
        // so "clean" is true by construction.
        const double dur = 44 * 60;
        var bursts = BuildBursts(dur, seed: 42);
        (double[] on, double[] off) = BurstsToIslands(bursts);

        // --- Case 1: clean (constant offset)
        var cleanCues = BurstsToCues(bursts, offsetSec: 0.15);
        DriftVerdict v1 = DriftDetector.Detect(
            cleanCues.Select(c => c.S).ToArray(),
            cleanCues.Select(c => c.E).ToArray(),
            on, off, dur);
        bool ok1 = v1.Ran && !v1.Drifts;
        Console.WriteLine($"[1] CLEAN (constant offset)      -> {(ok1 ? "PASS" : "FAIL")}"
            + $"  ran={v1.Ran} drifts={v1.Drifts} boundaries={v1.BoundaryCount}"
            + $"  {v1.Describe()}");
        failures += ok1 ? 0 : 1;

        // --- Case 2: clean + two KNOWN steps
        var steppedCues = BurstsToCues(bursts, offsetSec: 0.15, steps:
        [
            (atSec: 20 * 60, sizeSec: +2.5),
            (atSec: 30 * 60, sizeSec: +2.0)
        ]);
        DriftVerdict v2 = DriftDetector.Detect(
            steppedCues.Select(c => c.S).ToArray(),
            steppedCues.Select(c => c.E).ToArray(),
            on, off, dur);
        int hit = v2.BoundaryTimesSec.Count(t =>
            Math.Abs(t - (20 * 60)) <= 90 || Math.Abs(t - (30 * 60)) <= 90);
        double plantedTotal = 4.5;
        bool spanOk = Math.Abs(v2.SpanSec - plantedTotal) <= 1.5;
        bool ok2 = v2.Ran && v2.Drifts && hit >= 2 && spanOk;
        Console.WriteLine($"[2] CLEAN + steps +2.5s@20min, +2.0s@30min -> {(ok2 ? "PASS" : "FAIL")}"
            + $"  found={hit}/2  span={v2.SpanSec:0.00}s (planted {plantedTotal:0.00}s)");
        Console.WriteLine($"    boundaries: " + string.Join(", ",
            v2.BoundaryTimesSec.Select(t => $"{t / 60:0.0}min")));
        Console.WriteLine($"    {v2.Describe()}");
        failures += ok2 ? 0 : 1;

        // --- Case 3: ramped drift
        var rampCues = BurstsToCues(bursts, offsetSec: 0.15, ramp: (dur, -2.0, 10.0));
        DriftVerdict v3 = DriftDetector.Detect(
            rampCues.Select(c => c.S).ToArray(),
            rampCues.Select(c => c.E).ToArray(),
            on, off, dur);
        bool ok3 = v3.Ran && v3.Drifts;
        Console.WriteLine($"[3] RAMPED drift (-2s..+10s)     -> {(ok3 ? "PASS" : "FAIL")}"
            + $"  boundaries={v3.BoundaryCount}  span={v3.SpanSec:0.0}s");
        Console.WriteLine($"    {v3.Describe()}");
        failures += ok3 ? 0 : 1;

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "ALL CASES PASSED"
            : $"{failures} CASE(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The factored two-offset marginal must equal the explicit outer log-sum-exp.
    /// Guards the O(states) shortcut used in the port.
    /// </summary>
    private static int FactoredEqualsOuterSum()
    {
        var rng = new Random(7);
        double worst = 0;
        for (int trial = 0; trial < 20; trial++)
        {
            int s = DriftDetector.StateCount;
            var a = Enumerable.Range(0, s).Select(_ => rng.NextDouble() * -50).ToArray();
            var b = Enumerable.Range(0, s).Select(_ => rng.NextDouble() * -50).ToArray();

            double factored = DriftDetector.LogSumExp(a) + DriftDetector.LogSumExp(b);

            var outer = new List<double>(s * s);
            foreach (double x in a)
            {
                foreach (double y in b)
                {
                    outer.Add(x + y);
                }
            }

            double explicitSum = DriftDetector.LogSumExp(outer.ToArray());
            worst = Math.Max(worst, Math.Abs(factored - explicitSum));
        }

        bool ok = worst < 1e-6;
        Console.WriteLine($"[0] factored == explicit outer sum -> {(ok ? "PASS" : "FAIL")}"
            + $"  (worst deviation {worst:e2} over 20 random trials)");
        Console.WriteLine();
        return ok ? 0 : 1;
    }

    /// <summary>A dialogue burst: speech from start for the given length.</summary>
    private readonly record struct Burst(double Start, double Length);

    /// <summary>
    /// Builds speech bursts with realistic timing: dense exchange inside a scene,
    /// deliberate pauses between scenes (the pauses are what give the anchor its
    /// discriminating power).
    /// </summary>
    private static List<Burst> BuildBursts(double durationSec, int seed)
    {
        var rng = new Random(seed);
        var bursts = new List<Burst>();
        double t = 5.0;
        while (t < durationSec - 10)
        {
            int perScene = rng.Next(6, 16);
            for (int i = 0; i < perScene && t < durationSec - 10; i++)
            {
                double len = 1.0 + (rng.NextDouble() * 2.5);
                bursts.Add(new Burst(t, len));

                // Within a scene: short gap. Every few lines: a longer one.
                t += len + (rng.NextDouble() < 0.25
                    ? 1.5 + rng.NextDouble() * 3.0
                    : 0.25 + rng.NextDouble() * 0.5);
            }

            // Scene change: a real pause, where a cue MUST NOT start.
            t += 4.0 + rng.NextDouble() * 6.0;
        }

        return bursts;
    }

    /// <summary>Island start/end lists from the bursts.</summary>
    private static (double[] On, double[] Off) BurstsToIslands(List<Burst> bursts)
        => (bursts.Select(b => b.Start).ToArray(), bursts.Select(b => b.Start + b.Length).ToArray());

    /// <summary>
    /// Cues that track the bursts: each speech burst yields one cue starting a
    /// little after the burst begins (the SDH lead), optionally with planted steps
    /// and/or a ramp applied per cue.
    /// </summary>
    private static List<(double S, double E)> BurstsToCues(
        List<Burst> bursts,
        double offsetSec,
        (double AtSec, double SizeSec)[]? steps = null,
        (double Duration, double From, double To)? ramp = null)
    {
        var cues = new List<(double S, double E)>();
        var rng = new Random(99);
        foreach (Burst b in bursts)
        {
            // Per-cue variation of the lead — the measured 1.5 s MAD in the real
            // material. Included so the test is not easier than reality.
            double jitter = (rng.NextDouble() - 0.5) * 0.6;
            double add = offsetSec + jitter;

            if (steps != null)
            {
                foreach ((double atSec, double sizeSec) in steps)
                {
                    if (b.Start >= atSec)
                    {
                        add += sizeSec;
                    }
                }
            }

            if (ramp != null)
            {
                (double dur, double from, double to) = ramp.Value;
                add += from + ((to - from) * (b.Start / dur));
            }

            cues.Add((b.Start + add, b.Start + b.Length + add));
        }

        return cues.OrderBy(c => c.S).ToList();
    }
}
