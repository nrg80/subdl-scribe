// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// F-M340: the STÜTZSTELLEN model — the operator's proposal (09.10.2026), built as a SECOND fit
// so it can be measured against the incumbent instead of replacing it on a hunch.
//
// THE MODEL, IN THE OPERATOR'S TERMS
//
// "Polynomzug mit Stützstellen hoher Signifikanz, dazwischen linear interpolieren. Je steiler
// der Gradient, desto mehr Stützstellen dazwischen."
//
// So the scarce resource is the STÜTZSTELLE — a chosen position carrying a measured value. The
// path runs through the chosen knots and is a STRAIGHT LINE between two of them. A file that
// drifts at a constant rate is one straight line from the first knot to the last and costs one
// decision; a file that bends needs another knot and must earn it. That is the operator's
// "je steiler, desto mehr Stützstellen" expressed as a price rather than as a rule.
//
// WHY NOT A VALUE AT EVERY BLOCK BOUNDARY
//
// The first build of this file put a knot on EVERY block boundary and charged only for a change
// of slope. That is silently a staircase again: a jump from 0 s to +5 s has slope zero on both
// sides, so it cost NOTHING, and the fit could recover the incumbent's steps for free while
// keeping the Polygonzug's extra freedom. Measured on the operator-confirmed-good file with nothing
// planted: 20 knots, +6.5 objective points out of pure noise, 335 cues moved, up to 19 s — the
// Polygonzug would have wrecked a file the incumbent correctly leaves byte-identical. Charging for a
// KNOT, not for a bend, is what makes the stützstelle the resource the operator described.
//
// WHAT IS CHARGED
//
// Per piece, Z * sigma * sqrt(cues in piece) — the incumbent's own charge on the incumbent's own
// sigma, so the two objectives weigh the same evidence on the same scale and a difference
// between their answers is the MODEL. A straight line has two free endpoints instead of one free
// value, so the piece's luck ceiling carries the standard extreme-value factor for a second
// parameter (see SlopeLuck); without it the charge prices ~1/3 of what the line can harvest.
//
// THE DO-NOTHING HYPOTHESIS COMPETES, on the incumbent's terms: the flat, all-zero path is
// compared inside the same objective, so a file with no drift wins by being left alone.
//
// THE DEPLOY RULE, THE ORDER GUARD AND sigma ARE THE INCUMBENT'S, read from OffsetFit rather
// than copied.
//
// MEASURED AGAINST THE OPERATOR'S OWN VERDICTS the incumbent's segment values are NOT monotonic
// on 6 of 8 files he judged — including files he confirmed as GOOD (S04E06:
// -17.75 -17.25 -15.75 -13.75 -14.00 -11.75). That is why this model is measured beside the
// incumbent and not switched on by assumption.
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SubdlScribe.Qa;

/// <summary>
/// F-M340: the piecewise-LINEAR (stützstellen) offset fit — knots at chosen block boundaries,
/// a straight line between them, charged per piece.
/// </summary>
public static class StuetzstellenFit
{
    /// <summary>
    /// Result of <see cref="Fit"/>, in the shape the deploy rule and the writer already use.
    /// </summary>
    public sealed class StuetzstellenResult
    {
        /// <summary>True when the fit actually ran.</summary>
        public bool Ran { get; init; }

        /// <summary>Why it did not run, when <see cref="Ran"/> is false.</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>True when the deploy rule refused the result; shifts are then all zero.</summary>
        public bool Reverted { get; init; }

        /// <summary>Knots the path runs through.</summary>
        public int Knots { get; init; }

        /// <summary>Pieces the path is made of — one more than the interior bends.</summary>
        public int Pieces { get; init; }

        /// <summary>Per-cue shift actually to write, after the order guard.</summary>
        public double[] AppliedShiftsSec { get; init; } = Array.Empty<double>();

        /// <summary>Cues the fit moves by more than the reporting threshold.</summary>
        public int MovedCues { get; init; }

        /// <summary>Paired t over the moved cues.</summary>
        public double TMoved { get; init; }

        /// <summary>Mean raw score before the correction.</summary>
        public double ScoreBefore { get; init; }

        /// <summary>Mean raw score after the correction.</summary>
        public double ScoreAfter { get; init; }

        /// <summary>Cues in the file.</summary>
        public int CueCount { get; init; }

        /// <summary>Cues the order guard pulled away from their interpolated value.</summary>
        public int GuardedCues { get; init; }

        /// <summary>Objective reached by the best path.</summary>
        public double Objective { get; init; }

        /// <summary>Objective of doing nothing.</summary>
        public double ZeroObjective { get; init; }

        /// <summary>Knot values in seconds, ascending by time.</summary>
        public double[] KnotValuesSec { get; init; } = Array.Empty<double>();

        /// <summary>The measured sigma the charge was built from.</summary>
        public double Sigma { get; init; }

        /// <summary>Cue index of each knot, so a listener can jump to it.</summary>
        public int[] KnotCueIndex { get; init; } = Array.Empty<int>();
    }

    /// <summary>
    /// Fits the stützstellen path. Same inputs as <see cref="OffsetFit.Fit"/>, same deploy rule,
    /// so the two answers are directly comparable on the same file.
    /// </summary>
    /// <param name="starts">Cue start times in seconds.</param>
    /// <param name="ends">Cue end times in seconds.</param>
    /// <param name="level">Frame levels in dB, one per <see cref="OffsetFit.FrameSec"/> frame.</param>
    /// <param name="minSegFraction">Override for the piece floor, for tests.</param>
    /// <returns>The path, its per-cue shifts and the report the deploy rule needs.</returns>
    public static StuetzstellenResult Fit(
        double[] starts,
        double[] ends,
        double[] level,
        double? minSegFraction = null)
    {
        double[] pFit = OffsetFit.Dilate(OffsetFit.SpeechProbability(level), OffsetFit.SlackSec);
        if (pFit.Length < 4 || starts.Length == 0)
        {
            return new StuetzstellenResult { Ran = false, Reason = "audio too short" };
        }

        int nFrame = pFit.Length;
        double[] cumFit = OffsetFit.Cumulative(pFit);
        double[] pRaw = OffsetFit.SpeechProbability(level);
        double[] cumRaw = OffsetFit.Cumulative(pRaw);
        (double[,] s, bool[,] w) = OffsetFit.CueTable(starts, ends, cumFit, nFrame);

        // The REPORT runs on the undilated curve, exactly as the incumbent's does, so a score
        // compared between the two fits is the same quantity.
        (double[,] sRaw, bool[,] _) = OffsetFit.CueTable(starts, ends, cumRaw, nFrame);

        double sigma = OffsetFit.DebugSigma(s);
        double[] states = OffsetFit.DebugStates();
        int ns = states.Length;
        int zero = Array.IndexOf(states, 0.0);
        int nCues = starts.Length;
        int[] bounds = OffsetFit.DebugBounds(nCues);
        int nb = bounds.Length - 1;

        // The piece floor is the incumbent's, for the same reason: below it the charge cannot pay
        // for a piece and the fit emits junk shifts. A whole-file piece is always allowed, which
        // is what lets a short file be handled at all.
        double frac = minSegFraction ?? OffsetFit.MinSegFrac;
        int minSegCues = Math.Max(OffsetFit.MinSegFloor, (int)Math.Round(frac * nCues));

        double slopeLuck = SlopeLuck(ns);
        double Charge(int cues) => OffsetFit.Z * sigma * Math.Sqrt(cues) * slopeLuck;

        // dp[j][k]: best objective of a path whose LAST knot sits on boundary j with value
        // states[k]. The path may be a single piece (j == nb from 0) or many.
        var dp = new double[nb + 1][];
        var backKnot = new int[nb + 1][];
        var backPrev = new int[nb + 1][];
        for (int j = 0; j <= nb; j++)
        {
            dp[j] = new double[ns];
            backKnot[j] = new int[ns];
            backPrev[j] = new int[ns];
            Array.Fill(dp[j], double.NegativeInfinity);
            Array.Fill(backKnot[j], -1);
        }

        // The first knot is free: the file's own start carries no evidence of where it came from,
        // and charging the very first stützstelle would only ever forbid the flat path.
        for (int k = 0; k < ns; k++)
        {
            dp[0][k] = 0;
        }

        for (int j = 1; j <= nb; j++)
        {
            for (int i = 0; i < j; i++)
            {
                int cuesInPiece = bounds[j] - bounds[i];
                bool wholeFile = i == 0 && j == nb;
                if (!wholeFile && cuesInPiece < minSegCues)
                {
                    continue;
                }

                double charge = Charge(cuesInPiece);
                int lo = bounds[i];
                int hi = bounds[j];
                double tLo = starts[lo];
                double tSpan = starts[hi - 1] - tLo;

                for (int ki = 0; ki < ns; ki++)
                {
                    double baseVal = dp[i][ki];
                    if (double.IsNegativeInfinity(baseVal))
                    {
                        continue;
                    }

                    baseVal -= charge;
                    double vi = states[ki];
                    for (int kj = 0; kj < ns; kj++)
                    {
                        double vj = states[kj];
                        double acc = 0;
                        for (int c = lo; c < hi; c++)
                        {
                            double f = tSpan > 0 ? (starts[c] - tLo) / tSpan : 0.0;
                            acc += s[c, NearestState(vi + ((vj - vi) * f), states, zero)];
                        }

                        double val = baseVal + acc;
                        if (val > dp[j][kj])
                        {
                            dp[j][kj] = val;
                            backKnot[j][kj] = ki;
                            backPrev[j][kj] = i;
                        }
                    }
                }
            }
        }

        int bestLast = -1;
        double bestVal = double.NegativeInfinity;
        for (int k = 0; k < ns; k++)
        {
            if (dp[nb][k] > bestVal)
            {
                bestVal = dp[nb][k];
                bestLast = k;
            }
        }

        if (bestLast < 0)
        {
            return new StuetzstellenResult { Ran = false, Reason = "no admissible path" };
        }

        // Walk the path back out: knots as (boundary, value) pairs.
        var knotBound = new List<int>();
        var knotVal = new List<int>();
        int cb = nb;
        int ck = bestLast;
        while (cb >= 0)
        {
            knotBound.Add(cb);
            knotVal.Add(ck);
            if (cb == 0)
            {
                break;
            }

            int prevB = backPrev[cb][ck];
            int prevK = backKnot[cb][ck];
            if (prevB < 0)
            {
                return new StuetzstellenResult { Ran = false, Reason = "broken DP trace" };
            }

            cb = prevB;
            ck = prevK;
        }

        knotBound.Reverse();
        knotVal.Reverse();

        // EDGE RULE — the incumbent's, applied to knots. A knot sitting within EdgeMarginSec of
        // the search bound has no measurable answer: the curve was flat or monotone and the
        // argmax ran off the end. Measured on the planted-linear cases, the free first and last
        // knots ran to -17.5 s and -19.8 s and carried the RMS error from ~0.7 s (what a clean
        // straight line reaches) up to 1.7 s. The incumbent drops such a segment and lets its
        // neighbour cover the range; here the same is done by removing the knot and holding the
        // surviving neighbour's value flat over the gap it leaves.
        var keep = new List<int>();
        for (int p = 0; p < knotVal.Count; p++)
        {
            bool edge = p == 0 || p == knotVal.Count - 1;
            bool atBound = Math.Abs(states[knotVal[p]]) >= OffsetFit.MaxOffsetSec - OffsetFit.EdgeMarginSec;
            if (edge && atBound)
            {
                continue;
            }

            keep.Add(p);
        }

        var eff = new double[nCues];
        if (keep.Count == 0)
        {
            // Every anchor was unmeasurable, which is the flat file: nothing to write.
            Array.Clear(eff);
        }
        else if (keep.Count == 1)
        {
            Array.Fill(eff, states[knotVal[keep[0]]]);
        }
        else
        {
            double vFirst = states[knotVal[keep[0]]];
            int firstCue = bounds[knotBound[keep[0]]];
            for (int c = 0; c < firstCue; c++)
            {
                eff[c] = vFirst;
            }

            for (int q = 0; q + 1 < keep.Count; q++)
            {
                int lo = bounds[knotBound[keep[q]]];
                int hi = bounds[knotBound[keep[q + 1]]];
                double vi = states[knotVal[keep[q]]];
                double vj = states[knotVal[keep[q + 1]]];
                double tLo = starts[lo];
                double tSpan = starts[hi - 1] - tLo;
                for (int c = lo; c < hi; c++)
                {
                    double f = tSpan > 0 ? (starts[c] - tLo) / tSpan : 0.0;
                    eff[c] = vi + ((vj - vi) * f);
                }
            }

            double vLast = states[knotVal[keep[^1]]];
            int lastCue = bounds[knotBound[keep[^1]]];
            for (int c = lastCue; c < nCues; c++)
            {
                eff[c] = vLast;
            }
        }

        // The do-nothing hypothesis, on the incumbent's terms.
        double zeroObjective = OffsetFit.ZeroObjectiveFor(s, w, sigma);

        // Order guard FIRST, then the decision: the guard's pulled values are what would be
        // written, so they are what the deploy rule must score.
        int guardCount = OffsetFit.GuardOrder(starts, ends, eff);

        int moved = 0;
        double sumDiff = 0, sumSq = 0, rawSum = 0, newSum = 0;
        for (int c = 0; c < nCues; c++)
        {
            double before = sRaw[c, zero];
            double after = sRaw[c, NearestState(eff[c], states, zero)];
            rawSum += before;
            newSum += after;
            if (Math.Abs(eff[c]) > 0.05)
            {
                double diff = after - before;
                moved++;
                sumDiff += diff;
                sumSq += diff * diff;
            }
        }

        double scoreBefore = rawSum / nCues;
        double scoreAfter = newSum / nCues;
        double tMoved = 0;
        if (moved > 1)
        {
            double mean = sumDiff / moved;
            double var = Math.Max((sumSq - (moved * mean * mean)) / (moved - 1), 0.0);
            double se = Math.Sqrt(var / moved);
            tMoved = se > 0 ? mean / se : 0.0;
        }

        double largest = 0;
        foreach (double v in eff)
        {
            largest = Math.Max(largest, Math.Abs(v));
        }

        bool reverted = bestVal <= zeroObjective
                        || moved == 0 || tMoved <= OffsetFit.DeployZ
                        || scoreAfter < scoreBefore - 1e-9
                        || largest < SubtitleSync.MinShiftSec;
        if (reverted)
        {
            Array.Clear(eff);
            scoreAfter = scoreBefore;
            tMoved = 0;
            moved = 0;
        }

        var knotValues = new double[knotVal.Count];
        var knotCues = new int[knotBound.Count];
        for (int p = 0; p < knotVal.Count; p++)
        {
            knotValues[p] = states[knotVal[p]];
            knotCues[p] = bounds[knotBound[p]];
        }

        return new StuetzstellenResult
        {
            Ran = true,
            Reverted = reverted,
            Knots = knotVal.Count,
            Pieces = knotVal.Count - 1,
            AppliedShiftsSec = eff,
            MovedCues = moved,
            TMoved = tMoved,
            ScoreBefore = scoreBefore,
            ScoreAfter = scoreAfter,
            CueCount = nCues,
            GuardedCues = guardCount,
            Objective = bestVal,
            ZeroObjective = zeroObjective,
            KnotValuesSec = knotValues,
            Sigma = sigma,
            KnotCueIndex = knotCues
        };
    }

    /// <summary>
    /// The luck a PIECE may gain from having two free endpoints instead of one free value.
    /// <para>
    /// A maximum over n independent candidate values sits about sqrt(2 ln n) standard deviations
    /// above the mean, which is what the incumbent's own charge is calibrated against (one free
    /// value per segment). A straight piece carries TWO free values, so its ceiling is the
    /// maximum over n^2 combinations where the second parameter is only weakly constrained by
    /// the first: sqrt(2 ln n^2) = sqrt(2) * sqrt(2 ln n). The factor is that ratio — the standard
    /// extreme-value correction — not a tuning constant.
    /// </para>
    /// </summary>
    /// <param name="states">Candidate values per endpoint.</param>
    /// <returns>The factor on the per-piece charge.</returns>
    private static double SlopeLuck(double states) => Math.Sqrt(2.0);

    /// <summary>State index whose value is closest to <paramref name="sec"/>.</summary>
    /// <param name="sec">Value in seconds.</param>
    /// <param name="states">Ascending state grid.</param>
    /// <param name="zero">Index of the zero state.</param>
    /// <returns>Clamped state index.</returns>
    private static int NearestState(double sec, double[] states, int zero)
    {
        // The grid is uniform, so the index follows from the step — but it is rounded against the
        // grid rather than trusted, because the endpoints are built by accumulation and a
        // systematic half-step error there would bias every interpolated value outward.
        int idx = zero + (int)Math.Round(sec / OffsetFit.StateSec);
        if (idx < 0)
        {
            return 0;
        }

        return idx >= states.Length ? states.Length - 1 : idx;
    }
}
