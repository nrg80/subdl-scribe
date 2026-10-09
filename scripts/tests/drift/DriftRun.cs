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

        // --- Case 1b: a LARGE CONSTANT shift must stay "steady" AND report the
        // value. A file shifted by a fixed amount is the case a correction CAN
        // fix, so the gate must (a) not mistake it for drift and (b) name the
        // amount. The value is compared against the planted one — this is the
        // direct answer to "does it work for a constant shift".
        Console.WriteLine();
        Console.WriteLine("    constant shift, planted vs. reported:");
        foreach (double shift in new[] { -3.0, 2.0, 4.0, 8.0, 12.0 })
        {
            var shifted = BurstsToCues(bursts, offsetSec: shift);
            DriftVerdict vs = DriftDetector.Detect(
                shifted.Select(c => c.S).ToArray(),
                shifted.Select(c => c.E).ToArray(),
                on, off, dur);
            double err = vs.MedianOffsetSec - shift;
            bool okS = vs.Ran && !vs.Drifts && vs.BoundaryCount == 0
                       && Math.Abs(err) <= 0.35;
            Console.WriteLine($"      [{((okS ? "PASS" : "FAIL"))}] planted {shift:+0.0;-0.0}s"
                + $" -> reported {vs.MedianOffsetSec:+0.00;-0.00}s (error {err:+0.00;-0.00}s)"
                + $", drifts={vs.Drifts}, boundaries={vs.BoundaryCount}");
            failures += okS ? 0 : 1;
        }

        Console.WriteLine();
        Console.WriteLine("    constant shift PLUS a step (must drift, not read steady):");
        {
            var mixed = BurstsToCues(bursts, offsetSec: 4.0, steps: [(atSec: 20 * 60, sizeSec: +2.5)]);
            DriftVerdict vm = DriftDetector.Detect(
                mixed.Select(c => c.S).ToArray(),
                mixed.Select(c => c.E).ToArray(),
                on, off, dur);
            bool okM = vm.Ran && vm.Drifts && vm.SpanSec > 1.5;
            Console.WriteLine($"      [{((okM ? "PASS" : "FAIL"))}] planted +4.0s then +2.5s"
                + $" -> drifts={vm.Drifts}, span {vm.SpanSec:0.00}s, "
                + $"{vm.BoundaryCount} boundary/boundaries");
            failures += okM ? 0 : 1;
        }

        Console.WriteLine();

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

        // --- Case 4: the STAIRCASE repair (F-M300) ---
        Console.WriteLine();
        failures += StaircaseRepair(bursts, dur, on, off);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "ALL CASES PASSED"
            : $"{failures} CASE(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// F-M300: the staircase correction is driven on the same synthetic episode with KNOWN
    /// steps, and judged against the BURST LIST — the ground truth that took no part in the
    /// measurement. That is the whole point: a correction scored with the detector that produced
    /// it is the exact inverse of its own measurement and always reports success, so the check
    /// here is the distance to where the cues belong, not the detector's own numbers.
    /// </summary>
    /// <param name="bursts">The speech bursts the cues were derived from.</param>
    /// <param name="dur">Episode duration in seconds.</param>
    /// <param name="on">Island starts.</param>
    /// <param name="off">Island ends.</param>
    /// <returns>0 when every assertion held, 1 otherwise.</returns>
    private static int StaircaseRepair(
        List<Burst> bursts, double dur, double[] on, double[] off)
    {
        int failures = 0;
        Console.WriteLine("[4] STAIRCASE repair, judged against the burst list (not the detector)");

        // Two planted steps, both POSITIVE, as the real material measured them.
        var stepped = BurstsToCues(bursts, offsetSec: 4.0, steps:
        [
            (atSec: 20 * 60, sizeSec: +2.5),
            (atSec: 30 * 60, sizeSec: +2.0)
        ]);

        double[] starts = stepped.Select(c => c.S).ToArray();
        double[] ends = stepped.Select(c => c.E).ToArray();

        double[] burstStarts = bursts.Select(b => b.Start).ToArray();

        double before = WorstToNearestBurst(starts, burstStarts);
        DriftVerdict v = DriftDetector.Detect(starts, ends, on, off, dur);
        bool detected = v.Ran && v.Drifts && v.SegmentOffsetsSec.Count >= 2;
        Console.WriteLine($"      [{((detected ? "PASS" : "FAIL"))}] drift detected with segments"
            + $"  drifts={v.Drifts}  segments={v.SegmentOffsetsSec.Count}"
            + $"  span {v.SpanSec:0.00}s  boundaries={v.BoundaryCount}");
        failures += detected ? 0 : 1;
        if (!detected)
        {
            return 1;
        }

        // The offsets are what the repair uses; print them so a wrong segment is visible.
        Console.WriteLine("      segment offsets: "
            + string.Join(" ", v.SegmentOffsetsSec.Select(o => $"{o:+0.00;-0.00}")));
        Console.WriteLine("      segment starts : "
            + string.Join(" ", v.SegmentStartTimesSec.Select(t => $"{t / 60:0.0}min")));

        string text = Render(stepped);
        (bool ok, string shifted, string why, int guarded) =
            SubtitleSync.ShiftByStaircase(text, v.SegmentStartTimesSec, v.SegmentOffsetsSec);
        Console.WriteLine($"      [{((ok ? "PASS" : "FAIL"))}] staircase applied: {why}, {guarded} order-guarded");
        failures += ok ? 0 : 1;
        if (!ok)
        {
            return failures;
        }

        // Re-parse the shifted text and score it against the ground truth.
        var after = Parse(shifted);
        double worst = WorstToNearestBurst(after.Select(c => c.S).ToArray(), burstStarts);

        // The honest bound. A cue is wrong exactly when the measured boundary did not land on the
        // real one and the cue therefore took its NEIGHBOUR's offset — so the error at such a cue
        // is the planted STEP, plus the per-cue jitter. The detector's positional uncertainty is
        // documented as ±1-2 min (DriftVerdict), which is why the boundary here lands at 29.7 min
        // for a step planted at 30 min. Demanding a residual at the jitter floor would be
        // demanding an accuracy the method does not have; the claim is that the file is FIXED to
        // within one step at a handful of cues, not that every cue is exact.
        const double plantedStep = 2.5;
        double bound = plantedStep + 0.30;
        bool improved = worst < before - 0.5;
        bool bounded = worst <= bound;
        Console.WriteLine($"      [{(improved ? "PASS" : "FAIL")}] worst distance to the true cue position"
            + $"  {before:0.00}s -> {worst:0.00}s  (a constant offset cannot do this: the two"
            + " planted steps are 2.5 s and 2.0 s apart from it)");
        Console.WriteLine($"      [{(bounded ? "PASS" : "FAIL")}] residual inside one misplaced step"
            + $"  ({worst:0.00}s <= {bound:0.00}s = the largest planted step {plantedStep:0.00}s + jitter)");
        failures += improved ? 0 : 1;
        failures += bounded ? 0 : 1;

        // THE STEP MUST SURVIVE THE ORDER GUARD. This is the assertion that matters: a guard
        // implemented with the wrong sign (or one that fires on nearly every cue) would flatten
        // the staircase into a single constant shift and still leave the file "moved". The
        // applied shift is therefore read back per segment, well INSIDE each one — away from the
        // boundary where the guard bites — and the differences must reproduce the planted steps.
        var applied = AppliedShifts(text, shifted);
        Console.WriteLine($"      applied shifts by segment: "
            + string.Join(" ", applied.Select(o => $"{o:+0.00;-0.00}")));
        bool stepsKept = applied.Count >= 3
            && Math.Abs((applied[1] - applied[0]) + 2.5) <= 0.45
            && Math.Abs((applied[2] - applied[1]) + 2.0) <= 0.45;
        Console.WriteLine($"      [{(stepsKept ? "PASS" : "FAIL")}] both planted steps survived"
            + $" the guard (want -2.50s and -2.00s between segments)");
        failures += stepsKept ? 0 : 1;

        // A correction moves TIMES ONLY: same cue count, and the ORDER is preserved — the n-th
        // cue still starts before the (n+1)-th, which is what a player renders.
        bool sameCount = after.Count == stepped.Count;
        bool ordered = true;
        for (int i = 1; i < after.Count; i++)
        {
            if (after[i].S < after[i - 1].S)
            {
                ordered = false;
                break;
            }
        }

        Console.WriteLine($"      [{(sameCount ? "PASS" : "FAIL")}] cue count unchanged ({after.Count})");
        Console.WriteLine($"      [{(ordered ? "PASS" : "FAIL")}] cue order preserved (no cue overtakes its neighbour)");
        failures += sameCount ? 0 : 1;
        failures += ordered ? 0 : 1;

        // The order guard may bite at a STEP — that is what it is for — but it must not fire
        // broadly, or it carries one cue's shift through the file and the staircase collapses.
        // The bound is derived, not guessed: with 661 cues over three segments, the cues that can
        // genuinely collide sit in the few-cue window around each of the 2 boundaries, so a dozen
        // is the honest ceiling. (Before the sign fix this read 22 — and that WAS the failure.)
        bool guardRare = guarded <= 12;
        Console.WriteLine($"      [{(guardRare ? "PASS" : "FAIL")}] the order guard stayed rare"
            + $"  ({guarded} of {after.Count} cues — a step legitimately needs a few,"
            + " a broad fire would flatten the staircase)");
        failures += guardRare ? 0 : 1;

        // A CONSTANT offset must NOT go through the staircase: the detector reports no drift, so
        // there are no segments, and the caller takes the constant path instead.
        var constant = BurstsToCues(bursts, offsetSec: 4.0);
        DriftVerdict vc = DriftDetector.Detect(
            constant.Select(c => c.S).ToArray(),
            constant.Select(c => c.E).ToArray(),
            on, off, dur);
        bool noSeg = vc.Ran && !vc.Drifts && vc.SegmentOffsetsSec.Count == 0;
        Console.WriteLine($"      [{(noSeg ? "PASS" : "FAIL")}] a constant offset reports NO segments"
            + $"  (drifts={vc.Drifts}, segments={vc.SegmentOffsetsSec.Count}) — constant stays constant");
        failures += noSeg ? 0 : 1;

        // The guard APPLIES the shift and CLAMPS the cue that would go negative: the fit
        // decides the offset, the clamp only stops a cue running off the front of the file.
        string tiny = "1\n00:00:00,100 --> 00:00:01,000\nhello there\n\n"
                    + "2\n00:01:00,000 --> 00:01:02,000\nsecond line here\n\n";
        (bool negOk, string negText, string negWhy, _) = SubtitleSync.ShiftByStaircase(
            tiny, [0.0, 30.0], [5.0, -1.0]);
        List<(double S, double E)> negCues = Parse(negText);
        double negFirst = negCues.Count > 0 ? negCues[0].S : double.NaN;
        bool clampedOk = negOk && negCues.Count == 2 && Math.Abs(negFirst) < 1e-6;
        Console.WriteLine($"      [{(clampedOk ? "PASS" : "FAIL")}] a negative first cue is applied and clamped to 0"
            + $"  (first cue {negFirst:0.000}s, {negCues.Count} cues): {negWhy}");
        failures += clampedOk ? 0 : 1;

        return failures;
    }

    /// <summary>
    /// Largest distance from any cue start to the NEAREST true burst start. This is the ground
    /// truth measure: the cues were built from the bursts, so a perfect correction puts every cue
    /// back on its burst (up to the per-cue jitter the detector cannot see).
    /// </summary>
    private static double WorstToNearestBurst(double[] cueStarts, double[] burstStarts)
    {
        double worst = 0;
        foreach (double c in cueStarts)
        {
            double best = double.MaxValue;
            foreach (double b in burstStarts)
            {
                best = Math.Min(best, Math.Abs(c - b));
            }

            worst = Math.Max(worst, best);
        }

        return worst;
    }

    /// <summary>
    /// The shift actually applied, in the convention of the correction itself
    /// (<c>corrected − original</c>, so a file moved earlier reads negative), sampled WELL INSIDE
    /// each segment — the first and last few cues of a run are where the order guard legitimately
    /// bites. This is what proves the staircase survived: if the guard had flattened it, every
    /// segment would report the same value and the planted steps would be gone.
    /// </summary>
    /// <param name="original">SRT text before the correction.</param>
    /// <param name="corrected">SRT text after it.</param>
    /// <returns>One applied shift per segment, in cue order.</returns>
    private static List<double> AppliedShifts(string original, string corrected)
    {
        var a = Parse(original);
        var b = Parse(corrected);
        int n = Math.Min(a.Count, b.Count);
        var shifts = new List<double>();
        for (int i = 0; i < n; i++)
        {
            shifts.Add(b[i].S - a[i].S);
        }

        // Split where the shift changes by more than half a second, then keep the middle of each
        // run — the guard's bite sits at the run's edges, not in its middle.
        var outList = new List<double>();
        int start = 0;
        for (int i = 1; i <= shifts.Count; i++)
        {
            bool breakHere = i == shifts.Count || Math.Abs(shifts[i] - shifts[i - 1]) > 0.5;
            if (!breakHere)
            {
                continue;
            }

            int len = i - start;
            if (len >= 4)
            {
                var run = shifts.GetRange(start, len);
                run.Sort();
                outList.Add(run[run.Count / 2]);
            }

            start = i;
        }

        return outList;
    }

    /// <summary>Renders cue times as SRT text, for the shift to work on.</summary>
    private static string Render(List<(double S, double E)> cues)
    {
        var sb = new System.Text.StringBuilder();
        int n = 1;
        foreach ((double s, double e) in cues)
        {
            sb.Append(n++).Append('\n')
              .Append(Fmt(s)).Append(" --> ").Append(Fmt(e)).Append('\n')
              .Append("a line of dialogue here").Append('\n').Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Parses SRT start/end times back out, so the test reads what was written.</summary>
    private static List<(double S, double E)> Parse(string srt)
    {
        var rx = new System.Text.RegularExpressions.Regex(
            @"(\d{2}):(\d{2}):(\d{2}),(\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2}),(\d{3})");
        var outp = new List<(double S, double E)>();
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(srt))
        {
            double s = (int.Parse(m.Groups[1].Value) * 3600.0) + (int.Parse(m.Groups[2].Value) * 60.0)
                     + int.Parse(m.Groups[3].Value) + (int.Parse(m.Groups[4].Value) / 1000.0);
            double e = (int.Parse(m.Groups[5].Value) * 3600.0) + (int.Parse(m.Groups[6].Value) * 60.0)
                     + int.Parse(m.Groups[7].Value) + (int.Parse(m.Groups[8].Value) / 1000.0);
            outp.Add((s, e));
        }

        return outp;
    }

    private static string Fmt(double t)
    {
        int h = (int)(t / 3600.0);
        int mi = (int)((t - (h * 3600.0)) / 60.0);
        double s = t - (h * 3600.0) - (mi * 60.0);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{h:00}:{mi:00}:{s:00.000}").Replace('.', ',');
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
