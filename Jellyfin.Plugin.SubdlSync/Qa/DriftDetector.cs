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
// F-M295: the drift detector. Pure arithmetic (no I/O, no logging) so a test can
// drive it with synthetic cues and a synthetic envelope. The model is described
// in DriftVerdict.cs; this file is the arithmetic.
//
// THE PARAMETER VALUES ARE MEASURED, NOT CHOSEN
//
// Each was fixed against a reference set (drifting hearing-impaired files), a
// clean control file (must yield NOTHING) and a synthetic case (known steps
// planted in a clean file, must come back). Changing one means re-running both
// tests. Sources, so a later reader does not have to guess:
//
//   OffsetGrid         -8..+18 s in 0.1 s steps. Covers every offset measured in
//                      practice (largest was +12.2 s) with margin.
//   Eps 0.30           Share of cues with no speech reference (SDH notes,
//                      [music], song lines). Without this floor one such cue
//                      drives the whole offset.
//   LamStart 0.25 s    Penalty scale for "the island started that long before the
//                      cue". Small on purpose: a well-timed cue leads the speech
//                      by a few tenths of a second, so a large gap is unlikely.
//   LamEnd 0.60 s      Same for the weaker end-to-end channel.
//   WEnd 0.55          Weight of the end channel. Below 1 so it supports the
//                      start channel rather than leading it.
//   MinBoundaryGap 120 s  Two boundaries closer than this are one. Measured gap
//                      median was 8.5 min.
//   BayesFactorThreshold 3.0  log-BF a boundary must clear. log-BF 3 is roughly
//                      20:1; below it the two-offset model has not earned its
//                      extra parameter.
//   SizePrior 4.0/2.5 s   Measured drift steps are 1.07..3.77 s, median 2.84 s.
//                      This weakly favours that range and only RANKS — a large
//                      step is still reported when the evidence is strong, which
//                      is the "genuinely different cut" case.
//   Scales 1/3/9       Cue subsampling per resolution. A real boundary holds at
//                      all three; a noise artefact usually appears at one.
//
// AN EXACT SIMPLIFICATION, VERIFIED NUMERICALLY
//
// The marginal likelihood of the two-offset model factorises:
//     logsum_{d1,d2} (A[d1] + B[d2])  ==  lse(A) + lse(B)
// because the double sum separates. The Python prototype computed the full
// outer sum (260x260 = 67600 terms per candidate); this port uses the factored
// form, which is O(states) instead of O(states^2) per candidate and yields the
// SAME value to floating-point rounding. It is an equivalence, not a behaviour
// change — the self-test asserts it against the explicit outer sum.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SubdlScribe.Qa;

/// <summary>
/// F-M295: cue-vs-speech drift detection over a cue list and a speech envelope.
/// </summary>
public static class DriftDetector
{
    /// <summary>Audio frame length used by the envelope tables, in seconds.</summary>
    public const double FrameSec = 0.020;

    /// <summary>Offset grid lower bound in seconds.</summary>
    public const double OffsetGridMin = -8.0;

    /// <summary>Offset grid upper bound in seconds.</summary>
    public const double OffsetGridMax = 18.0;

    /// <summary>Offset grid step in seconds.</summary>
    public const double OffsetGridStep = 0.1;

    /// <summary>Share of cues assumed to carry no speech reference.</summary>
    public const double Eps = 0.30;

    /// <summary>Penalty scale for the cue-start channel, in seconds.</summary>
    public const double LamStart = 0.25;

    /// <summary>Penalty scale for the cue-end channel, in seconds.</summary>
    public const double LamEnd = 0.60;

    /// <summary>Weight of the cue-end channel.</summary>
    public const double WEnd = 0.55;

    /// <summary>Minimum gap between two reported boundaries, in seconds.</summary>
    public const double MinBoundaryGapSec = 120.0;

    /// <summary>log Bayes factor a boundary must clear.</summary>
    public const double BayesFactorThreshold = 3.0;

    /// <summary>Smallest step that counts as a boundary, in seconds.</summary>
    public const double MinStepSec = 0.8;

    /// <summary>Width of the step-size prior, in seconds.</summary>
    public const double SizePriorSigma = 4.0;

    /// <summary>Step size the prior treats as neutral, in seconds.</summary>
    public const double SizePriorRef = 2.5;

    /// <summary>Sentinel for "no island in range" in the envelope tables.</summary>
    public const double NoIsland = 1e5;

    /// <summary>Offset grid, ascending.</summary>
    private static readonly double[] States = BuildStates();

    private static double[] BuildStates()
    {
        int n = (int)Math.Round((OffsetGridMax - OffsetGridMin) / OffsetGridStep);
        var s = new double[n];
        for (int i = 0; i < n; i++)
        {
            s[i] = OffsetGridMin + (i * OffsetGridStep);
        }

        return s;
    }

    /// <summary>Gets the number of offset states.</summary>
    public static int StateCount => States.Length;

    /// <summary>log-sum-exp over a span of an array.</summary>
    public static double LogSumExp(ReadOnlySpan<double> x)
    {
        double m = double.NegativeInfinity;
        for (int i = 0; i < x.Length; i++)
        {
            if (x[i] > m)
            {
                m = x[i];
            }
        }

        if (double.IsNegativeInfinity(m))
        {
            return double.NegativeInfinity;
        }

        double sum = 0;
        for (int i = 0; i < x.Length; i++)
        {
            sum += Math.Exp(x[i] - m);
        }

        return m + Math.Log(sum);
    }

    /// <summary>
    /// Runs the detector. All times in seconds. <paramref name="onSec"/> holds
    /// speech-island starts, <paramref name="offSec"/> their ends, both ascending.
    /// </summary>
    /// <param name="cueStartsSec">Cue start times in seconds, ascending.</param>
    /// <param name="cueEndsSec">Cue end times in seconds, ascending.</param>
    /// <param name="onSec">Speech-island starts in seconds.</param>
    /// <param name="offSec">Speech-island ends in seconds.</param>
    /// <param name="durationSec">Media duration in seconds.</param>
    /// <param name="scales">Cue subsampling per resolution; defaults to 1/3/9.</param>
    /// <returns>The verdict.</returns>
    public static DriftVerdict Detect(
        double[] cueStartsSec,
        double[] cueEndsSec,
        double[] onSec,
        double[] offSec,
        double durationSec,
        int[]? scales = null)
    {
        scales ??= [1, 3, 9];

        if (cueStartsSec.Length < 40)
        {
            return new DriftVerdict { Ran = false, SkipReason = $"only {cueStartsSec.Length} cues" };
        }

        if (onSec.Length == 0)
        {
            return new DriftVerdict { Ran = false, SkipReason = "no speech islands found" };
        }

        if (durationSec <= 0)
        {
            return new DriftVerdict { Ran = false, SkipReason = "no duration" };
        }

        int nFrame = (int)(durationSec / FrameSec) + 2;
        double[] before = SecondsSinceLastIslandStart(onSec, nFrame);
        double[] after = SecondsToNextIslandEnd(offSec, nFrame);

        var perScale = new List<List<Boundary>>();
        double[,]? baseCum = null;
        double[]? baseCueStarts = null;

        foreach (int sc in scales)
        {
            var subIdx = new List<int>();
            for (int i = 0; i < cueStartsSec.Length; i += sc)
            {
                subIdx.Add(i);
            }

            if (subIdx.Count < 40)
            {
                continue;
            }

            double[] subStarts = subIdx.Select(i => cueStartsSec[i]).ToArray();
            double[,] cum = BuildCumulative(cueStartsSec, cueEndsSec, subIdx, before, after, nFrame);
            perScale.Add(FindBoundaries(cum, subStarts, 0, subIdx.Count, 0));

            if (baseCum is null)
            {
                baseCum = cum;
                baseCueStarts = subStarts;
            }
        }

        if (baseCum is null || baseCueStarts is null || perScale.Count == 0)
        {
            return new DriftVerdict { Ran = false, SkipReason = "too few cues per resolution" };
        }

        var consensus = Consensus(perScale);
        int nCue = baseCueStarts.Length;
        double medianOffset = BestOffset(baseCum, 0, nCue);

        if (consensus.Count == 0)
        {
            return new DriftVerdict
            {
                Ran = true,
                Drifts = false,
                MedianOffsetSec = medianOffset,
                SpanSec = 0,
                BoundaryCount = 0,
                MaxBayesFactor = 0
            };
        }

        // Span from the offsets actually chosen per segment, not from summed step
        // sizes — the two differ when a step is absorbed next to a boundary.
        var edges = new List<int> { 0 };
        foreach (Boundary b in consensus.OrderBy(x => x.T))
        {
            int idx = Math.Clamp(LowerBound(baseCueStarts, b.T), 1, nCue - 1);
            if (idx > edges[^1])
            {
                edges.Add(idx);
            }
        }

        if (edges[^1] < nCue)
        {
            edges.Add(nCue);
        }

        var segOffsets = new List<double>();
        for (int i = 0; i < edges.Count - 1; i++)
        {
            segOffsets.Add(BestOffset(baseCum, edges[i], edges[i + 1]));
        }

        double span = segOffsets.Count > 1 ? segOffsets[^1] - segOffsets[0] : 0;
        return new DriftVerdict
        {
            Ran = true,
            Drifts = true,
            MedianOffsetSec = medianOffset,
            SpanSec = Math.Abs(span),
            BoundaryCount = consensus.Count,
            BoundaryTimesSec = consensus.OrderBy(x => x.T).Select(x => x.T).ToList(),
            MaxBayesFactor = consensus.Max(x => x.Bf)
        };
    }

    /// <summary>One reported boundary.</summary>
    private readonly record struct Boundary(double T, double Size, double Bf);

    /// <summary>Seconds since the last island start at or before each frame.</summary>
    private static double[] SecondsSinceLastIslandStart(double[] onSec, int nFrame)
    {
        var res = new double[nFrame];
        Array.Fill(res, NoIsland);
        if (onSec.Length == 0)
        {
            return res;
        }

        var frames = onSec
            .Select(t => Math.Clamp((int)Math.Round(t / FrameSec), 0, nFrame - 1))
            .Distinct()
            .OrderBy(f => f)
            .ToArray();

        int p = 0;
        for (int f = 0; f < nFrame; f++)
        {
            while (p < frames.Length && frames[p] <= f)
            {
                p++;
            }

            res[f] = p == 0 ? NoIsland : (f - frames[p - 1]) * FrameSec;
        }

        return res;
    }

    /// <summary>Seconds until the next island end at or after each frame.</summary>
    private static double[] SecondsToNextIslandEnd(double[] offSec, int nFrame)
    {
        var res = new double[nFrame];
        Array.Fill(res, NoIsland);
        if (offSec.Length == 0)
        {
            return res;
        }

        var frames = offSec
            .Select(t => Math.Clamp((int)Math.Round(t / FrameSec), 0, nFrame - 1))
            .Distinct()
            .OrderBy(f => f)
            .ToArray();

        int p = 0;
        for (int f = 0; f < nFrame; f++)
        {
            while (p < frames.Length && frames[p] < f)
            {
                p++;
            }

            res[f] = p >= frames.Length ? NoIsland : (frames[p] - f) * FrameSec;
        }

        return res;
    }

    /// <summary>
    /// Cumulative per-state log-likelihood: <c>cum[i,k]</c> sums the first i cues
    /// (of the subsampled set) at offset state k.
    /// </summary>
    private static double[,] BuildCumulative(
        double[] cueStartsSec,
        double[] cueEndsSec,
        List<int> subIdx,
        double[] before,
        double[] after,
        int nFrame)
    {
        int n = subIdx.Count;
        int s = States.Length;
        var cum = new double[n + 1, s];
        double floor = Eps / (OffsetGridMax - OffsetGridMin);

        for (int c = 0; c < n; c++)
        {
            int i = subIdx[c];
            double cs = cueStartsSec[i];
            double ce = cueEndsSec[i];

            for (int k = 0; k < s; k++)
            {
                double d = States[k];

                int fo = Math.Clamp((int)Math.Round((cs - d) / FrameSec), 0, nFrame - 1);
                int fe = Math.Clamp((int)Math.Round((ce - d) / FrameSec), 0, nFrame - 1);

                double tb = before[fo];
                double p = tb > 300 ? floor : ((1.0 - Eps) * Math.Exp(-tb / LamStart)) + floor;

                double ta = after[fe];
                double q = ta > 300 ? floor : ((1.0 - Eps) * Math.Exp(-ta / LamEnd)) + floor;

                cum[c + 1, k] = cum[c, k] + Math.Log(p) + (WEnd * Math.Log(q));
            }
        }

        return cum;
    }

    /// <summary>Per-state sums over the cue span [lo,hi).</summary>
    private static double[] SegmentSums(double[,] cum, int lo, int hi)
    {
        int s = cum.GetLength(1);
        var v = new double[s];
        for (int k = 0; k < s; k++)
        {
            v[k] = cum[hi, k] - cum[lo, k];
        }

        return v;
    }

    /// <summary>log-marginal of one offset over [lo,hi).</summary>
    private static double OneOffsetLogM(double[,] cum, int lo, int hi)
        => LogSumExp(SegmentSums(cum, lo, hi));

    /// <summary>log-marginal of two offsets split at i, plus both offsets.</summary>
    private static (double LogM, double D1, double D2) TwoOffset(double[,] cum, int lo, int hi, int i)
    {
        double[] a = SegmentSums(cum, lo, i);
        double[] b = SegmentSums(cum, i, hi);
        int ia = ArgMax(a);
        int ib = ArgMax(b);

        // Factored form; identical to the explicit outer log-sum-exp (see header).
        return (LogSumExp(a) + LogSumExp(b), States[ia], States[ib]);
    }

    private static int ArgMax(double[] v)
    {
        int bi = 0;
        for (int i = 1; i < v.Length; i++)
        {
            if (v[i] > v[bi])
            {
                bi = i;
            }
        }

        return bi;
    }

    /// <summary>Best single offset over [lo,hi), in seconds.</summary>
    private static double BestOffset(double[,] cum, int lo, int hi)
        => States[ArgMax(SegmentSums(cum, lo, hi))];

    /// <summary>log-prior ratio for a step of the given size.</summary>
    public static double SizePrior(double size)
        => -0.5 * (((size / SizePriorSigma) * (size / SizePriorSigma))
                   - ((SizePriorRef / SizePriorSigma) * (SizePriorRef / SizePriorSigma)));

    /// <summary>Recursive boundary search: best split, then left and right of it.</summary>
    private static List<Boundary> FindBoundaries(double[,] cum, double[] cueStarts, int lo, int hi, int depth)
    {
        var result = new List<Boundary>();
        if (depth > 8 || hi - lo < 12)
        {
            return result;
        }

        double loT = cueStarts[lo] + MinBoundaryGapSec;
        double hiT = cueStarts[hi - 1] - MinBoundaryGapSec;
        if (hiT <= loT)
        {
            return result;
        }

        double logL1 = OneOffsetLogM(cum, lo, hi);
        double bestBf = double.NegativeInfinity;
        int bestI = -1;
        double bestD1 = 0, bestD2 = 0;

        void Consider(int i)
        {
            if (i <= lo || i >= hi)
            {
                return;
            }

            (double logM, double d1, double d2) = TwoOffset(cum, lo, hi, i);
            double bf = logM - logL1 + SizePrior(d2 - d1);
            if (bf > bestBf)
            {
                bestBf = bf;
                bestI = i;
                bestD1 = d1;
                bestD2 = d2;
            }
        }

        for (double t = loT; t <= hiT; t += 4.0)
        {
            Consider(Math.Clamp(LowerBound(cueStarts, t), lo + 4, hi - 4));
        }

        // Fine pass around the coarse winner.
        if (bestI >= 0)
        {
            int coarse = bestI;
            for (int i = Math.Max(lo + 4, coarse - 10); i <= Math.Min(hi - 4, coarse + 10); i++)
            {
                Consider(i);
            }
        }

        if (bestI < 0 || bestBf < BayesFactorThreshold || Math.Abs(bestD2 - bestD1) < MinStepSec)
        {
            return result;
        }

        result.Add(new Boundary(cueStarts[bestI], bestD2 - bestD1, bestBf));
        result.AddRange(FindBoundaries(cum, cueStarts, lo, bestI, depth + 1));
        result.AddRange(FindBoundaries(cum, cueStarts, bestI, hi, depth + 1));
        return result;
    }

    /// <summary>
    /// A boundary counts only when at least two resolutions see it. A real step
    /// holds at every resolution; a noise artefact usually appears at one.
    /// </summary>
    private static List<Boundary> Consensus(List<List<Boundary>> perScale)
    {
        var all = perScale.SelectMany(x => x).OrderBy(x => x.T).ToList();
        if (all.Count == 0)
        {
            return [];
        }

        const double tol = 90.0;
        var groups = new List<List<Boundary>>();
        var cur = new List<Boundary> { all[0] };
        foreach (Boundary b in all.Skip(1))
        {
            if (b.T - cur[^1].T <= tol)
            {
                cur.Add(b);
            }
            else
            {
                groups.Add(cur);
                cur = [b];
            }
        }

        groups.Add(cur);

        var outList = new List<Boundary>();
        foreach (var g in groups)
        {
            int present = perScale.Count(js => js.Any(x => Math.Abs(x.T - g[0].T) <= tol));
            if (present >= 2)
            {
                var sizes = g.Select(x => x.Size).OrderBy(x => x).ToList();
                outList.Add(new Boundary(g[0].T, sizes[sizes.Count / 2], g.Max(x => x.Bf)));
            }
        }

        return outList;
    }

    /// <summary>First index whose value is at least <paramref name="v"/>.</summary>
    private static int LowerBound(double[] arr, double v)
    {
        int lo = 0, hi = arr.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (arr[mid] < v)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
