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
// F-M307: the offset fit. The offset is a piecewise-constant function of time,
// fitted by EXACT dynamic programming. Pure arithmetic — no I/O, no logging, no
// ffmpeg — so a test can drive it with synthetic cues and a synthetic envelope.
//
// WHAT IS COMPARED, AND HOW
//
// Nothing is cross-correlated. The measure is one scalar per candidate shift, and it
// is an AVERAGE, not a correlation coefficient: nothing is centred and nothing is
// normalised against a second curve.
//
//   1. per cue: the mean of p(t) over the shifted cue window, read from a cumulative
//      sum of p(t). A window that would fall outside the audio scores 0 and is
//      EXCLUDED — never clipped to the edge, because a clipped window is short and
//      its mean is therefore too high (the fit exploited exactly that: it returned
//      +20.0 / -19.25 / +19.75 s on cues that were never moved).
//   2. per candidate shift: the arithmetic mean of the per-cue means. Higher means
//      the cues sit on more speech. Chance level is the file's own mean of p(t).
//
// The result is the table S[cue, shift] plus a fits-inside flag. The fit reads only
// that table, never the audio again, which is what makes an exhaustive optimum
// affordable.
//
// THE FIT
//
// Maximise, over ALL placements of change points:
//
//     sum over cues of (score at that cue's shift)  -  Z * sigma * sqrt(cues in segment)
//
// where sigma is read from the data as the median absolute change of a cue's score per
// grid step. Solved exactly by DP over (block, state) pairs: blocks of BlockCues cues,
// candidate shifts on a StateSec grid across [-MaxOffsetSec, +MaxOffsetSec].
//
// WHY THE CHARGE IS SQRT-SHAPED
//
// A segment of L cues may pick the best of all candidate shifts, so it gains by luck
// alone an amount that grows as sqrt(L). Charging Z*sigma*sqrt(L) means a step must
// earn more than its own luck, and the charge grows faster than the luck available.
// Splitting is never free. This is the rule that stops the fit from inventing steps; a
// fixed shift-size threshold cannot do the job, because a threshold cannot distinguish
// a real small step from noise.
//
// THE DO-NOTHING FIT COMPETES
//
// One segment with shift exactly 0 is always an alternative inside the same objective,
// evaluated with the shift FIXED at zero — not free to take the best value. Letting it
// take the best value makes "do nothing" strictly stronger than doing nothing, and it
// then beats genuine structure. A file with no drift therefore scores better left
// alone, and is left alone.
//
// THE DEPLOY RULE
//
// A shift is written only if all of: the cues the fit actually moves show a mean score
// gain above DeployZ standard errors of that mean (a PAIRED test over the MOVED cues only —
// averaging over the whole file dilutes a 9 %-step's evidence tenfold and rejects real
// corrections); the file as a whole does not get worse; and the largest shift is at
// least MinShiftSec. DeployZ is a SEPARATE constant from Z: Z is the charge inside the
// DP and shapes the segments, DeployZ only decides whether a result is written. Raising
// Z to tighten the deploy rule would change the segmentation underneath it.
// Consequences, both intended: a file already in sync is returned
// byte-identical, and a repeat run changes nothing (idempotence).
//
// THE MEASURED LIMIT
//
// A step shorter than about MinSegFrac of the file's cues is not recovered: below that
// the charge cannot pay for a segment. Measured below the floor, the fit emitted junk
// shifts of +17.5 s on cues that were never moved — so the floor is a guard, not a
// tuning value. A short scene that is out of sync on its own therefore stays out of
// sync. The floor is a FRACTION of the file's cues, so a 45-minute episode and a
// 100-minute film get the same relative resolution.
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SubdlScribe.Qa;

/// <summary>
/// F-M307: the piecewise-constant offset fit, solved exactly by dynamic programming.
/// </summary>
public static class OffsetFit
{
    /// <summary>Frame length in seconds for the probability curve.</summary>
    public const double FrameSec = 0.020;

    /// <summary>Upper bound of the shift search, in seconds.</summary>
    public const double MaxOffsetSec = 20.0;

    /// <summary>Step of the shift grid, in seconds.</summary>
    public const double StateSec = 0.25;

    /// <summary>
    /// Dilation of the probability curve, in seconds. Subtitle boundaries are not
    /// frame-exact — a cue leads or trails the speech by a few tenths. A max-filter of
    /// this width treats a cue as covered when speech is anywhere within it, which
    /// sharpens the score peak. It is used only for the FIT; the before/after report
    /// uses the undilated curve so the two numbers stay comparable.
    /// </summary>
    public const double SlackSec = 0.30;

    /// <summary>DP resolution: change points may sit on this cue grid.</summary>
    public const int BlockCues = 40;

    /// <summary>
    /// Shortest segment, as a FRACTION of the file's cues. Scale-invariant between an
    /// episode and a feature film. Measured sweep: 2 % -> junk and a +17 s shift on a
    /// known-good file, 5 % -> junk 17.5 s, 8 % -> same, 12 % -> clean.
    /// </summary>
    public const double MinSegFrac = 0.12;

    /// <summary>Floor for the shortest segment, in cues.</summary>
    public const int MinSegFloor = 40;

    /// <summary>
    /// Significance of a step, in standard errors of its own gain, as charged INSIDE the
    /// fit. This is the DP charge only — it decides where segments are placed. Do not
    /// raise it to make the deploy rule stricter: that changes the segmentation, and the
    /// measured separation below (1.51 s against 2.66 s) was taken at 1.0.
    /// </summary>
    public const double Z = 1.0;

    /// <summary>
    /// Significance the deploy rule demands of the cues the fit MOVES, in standard errors
    /// of their mean gain. Separate from <see cref="Z"/> on purpose: that one is the DP
    /// charge and shapes the segments, this one only decides whether the result is written.
    /// <para>
    /// Measured on 34 real fits of one series (one morning, all language variants), against
    /// the operator's own listening verdicts: every file he reported as audibly WRONG sat
    /// at t = 1.06 / 1.15 / 1.38 / 1.51, every file he confirmed as GOOD at t = 2.66 and
    /// 8.45. Nothing sits between 1.51 and 2.66, so the gap separates the two groups
    /// completely, while <see cref="Z"/> = 1.0 let all four bad files through — they cleared
    /// it by a hair and were written. At 2.0 the four are refused (left as downloaded) and
    /// no good file is lost.
    /// </para>
    /// <para>
    /// This is a threshold fitted on the run it judges, which this class's own notes warn
    /// against — but the failure it catches is not a subtle one: those fits had landed on
    /// the wrong piece of sound (adjacent-segment jumps of 10.25-34.00 s against at most
    /// 5.50 s for the good ones), so a weak paired test is the observable symptom of a
    /// wrong fit. Watch for a run of refusals; re-derive the bound as verdicts accumulate.
    /// </para>
    /// </summary>
    public const double DeployZ = 2.0;

    /// <summary>
    /// A segment whose shift sits this close to the search bound has no measurable
    /// answer — the curve was flat or monotone and the argmax ran off the end.
    /// </summary>
    public const double EdgeMarginSec = 2.0;

    private static readonly double[] States = BuildStates();
    private static readonly int ZeroState = Array.IndexOf(States, 0.0);

    /// <summary>Gets the number of candidate shifts.</summary>
    public static int StateCount => States.Length;

    /// <summary>Gets the shift of a state index, in seconds.</summary>
    /// <param name="k">State index.</param>
    /// <returns>Shift in seconds.</returns>
    public static double StateValue(int k) => States[k];

    private static double[] BuildStates()
    {
        var list = new List<double>();
        for (double d = -MaxOffsetSec; d <= MaxOffsetSec + (StateSec / 2); d += StateSec)
        {
            list.Add(Math.Round(d, 4));
        }

        return list.ToArray();
    }

    /// <summary>
    /// Speech probability per frame, in [0,1], from a band-passed mono decode's frame
    /// levels. The
    /// method is THRESHOLD-FREE: frame levels are normalised against the file's own
    /// 10th/90th percentile, so no absolute level is assumed and any recording level
    /// works.
    /// </summary>
    /// <param name="level">Frame levels in dB, one per <see cref="FrameSec"/> frame.</param>
    /// <returns>The undilated probability curve, one value per <see cref="FrameSec"/> frame.</returns>
    /// <remarks>
    /// Takes the frame levels rather than the samples: the levels ARE the only thing the
    /// probability is computed from, so materialising the samples would cost ~1 GB on a
    /// 140-minute track to produce the same 3 MB of levels (see
    /// <c>DriftGate.DecodeFrameLevelsAsync</c>).
    /// </remarks>
    public static double[] SpeechProbability(double[] level)
    {
        int n = level.Length;
        if (n < 2)
        {
            return [];
        }

        double[] sorted = (double[])level.Clone();
        Array.Sort(sorted);
        double lo = Percentile(sorted, 0.10);
        double hi = Percentile(sorted, 0.90);

        var p = new double[n];
        if (hi - lo >= 3.0)
        {
            for (int i = 0; i < n; i++)
            {
                p[i] = Math.Clamp((level[i] - lo) / (hi - lo), 0.0, 1.0);
            }
        }

        return p;
    }

    /// <summary>Dilates a curve by a max-filter, so a cue counts as covered within the slack.</summary>
    /// <param name="p">Probability curve.</param>
    /// <param name="slackSec">Half-width of the filter in seconds.</param>
    /// <returns>The dilated curve, same length.</returns>
    public static double[] Dilate(double[] p, double slackSec)
    {
        int w = Math.Max(1, (int)Math.Round(slackSec / FrameSec));
        var d = (double[])p.Clone();
        for (int j = 1; j <= w; j++)
        {
            for (int i = 0; i + j < p.Length; i++)
            {
                d[i] = Math.Max(d[i], p[i + j]);
            }

            for (int i = j; i < p.Length; i++)
            {
                d[i] = Math.Max(d[i], p[i - j]);
            }
        }

        return d;
    }

    /// <summary>Cumulative sum of a curve, with a leading zero, as <c>[i] = sum of the first i</c>.</summary>
    /// <param name="p">Curve.</param>
    /// <returns>The cumulative array of length <c>p.Length + 1</c>.</returns>
    public static double[] Cumulative(double[] p)
    {
        var cum = new double[p.Length + 1];
        for (int i = 0; i < p.Length; i++)
        {
            cum[i + 1] = cum[i] + p[i];
        }

        return cum;
    }

    /// <summary>Mean of the curve over a half-open frame window, or NaN when it does not fit.</summary>
    /// <param name="cum">Cumulative array from <see cref="Cumulative"/>.</param>
    /// <param name="nFrame">Number of frames in the curve.</param>
    /// <param name="a">First frame.</param>
    /// <param name="b">Frame after the last.</param>
    /// <returns>The mean over the window.</returns>
    public static double MeanOver(double[] cum, int nFrame, int a, int b)
    {
        if (a < 0 || b > nFrame || b <= a)
        {
            return double.NaN;
        }

        return (cum[b] - cum[a]) / (b - a);
    }

    /// <summary>
    /// Builds the cue table: per cue and candidate shift, the mean speech probability
    /// over the shifted window, or 0 when the window does not fit inside the audio.
    /// </summary>
    /// <param name="starts">Cue start times in seconds.</param>
    /// <param name="ends">Cue end times in seconds.</param>
    /// <param name="cum">Cumulative array of the dilated curve.</param>
    /// <param name="nFrame">Frames in the curve.</param>
    /// <returns>(score, fits) — both <c>[cue, state]</c>.</returns>
    public static (double[,] Score, bool[,] Fits) CueTable(
        double[] starts, double[] ends, double[] cum, int nFrame)
    {
        int nc = starts.Length;
        var score = new double[nc, States.Length];
        var fits = new bool[nc, States.Length];
        for (int k = 0; k < States.Length; k++)
        {
            double d = States[k];
            for (int c = 0; c < nc; c++)
            {
                int a = (int)Math.Round((starts[c] + d) / FrameSec);
                int b = Math.Max((int)Math.Round((ends[c] + d) / FrameSec), a + 1);
                bool ok = a >= 0 && b <= nFrame;
                if (ok)
                {
                    score[c, k] = (cum[b] - cum[a]) / (b - a);
                    fits[c, k] = true;
                }
            }
        }

        return (score, fits);
    }

    /// <summary>
    /// Fits the offset. Returns the segments, the per-cue applied shift, and the report
    /// the deploy rule needs.
    /// </summary>
    /// <param name="starts">Cue start times in seconds.</param>
    /// <param name="ends">Cue end times in seconds.</param>
    /// <param name="level">Frame levels in dB, one per <see cref="FrameSec"/> frame.</param>
    /// <param name="minSegFraction">Override for <see cref="MinSegFrac"/>, for tests.</param>
    /// <returns>The fit.</returns>
    public static FitResult Fit(
        double[] starts,
        double[] ends,
        double[] level,
        double? minSegFraction = null)
    {
        double[] pFit = Dilate(SpeechProbability(level), SlackSec);
        double[] pRaw = SpeechProbability(level);
        if (pFit.Length < 4 || starts.Length == 0)
        {
            return FitResult.NotMeasured("audio too short");
        }

        int nFrame = pFit.Length;
        double[] cumFit = Cumulative(pFit);
        double[] cumRaw = Cumulative(pRaw);
        (double[,] s, bool[,] w) = CueTable(starts, ends, cumFit, nFrame);

        // The REPORT uses the UNDILATED curve, so "before" and "after" are on the same
        // scale. SLACK exists only to sharpen the peak for the fit; scoring the report on
        // the dilated curve inflates both numbers (measured 0.5434 against 0.5385) and
        // makes the deploy rule decide on a quantity that is not the one being improved.
        (double[,] sRaw, bool[,] _) = CueTable(starts, ends, cumRaw, nFrame);

        double sigma = MedianAbsStep(s);
        int nCues = starts.Length;
        double frac = minSegFraction ?? MinSegFrac;
        int minSeg = Math.Max(MinSegFloor, (int)Math.Round(frac * nCues));
        int[] bounds = MakeBounds(nCues);

        List<(int Lo, int Hi, int State)> segs = FitDp(s, w, bounds, minSeg, sigma);
        RefineBoundaries(s, segs);
        DropEdgeSegments(segs);

        // NO post-filter on segment length here. The reference implementation has none
        // either, and adding one silently diverges: on the confirmed-good BCS file the fit
        // produces a 10-cue segment at the file end with a -17 s shift, and the reference
        // leaves it standing while an extra length filter removed it — a different answer
        // from the same inputs. The length floor belongs INSIDE the DP (FitDp), and the
        // deploy rule below is what decides whether anything is written at all.

        // The do-nothing hypothesis competes on equal terms, with the shift FIXED at 0.
        if (segs.Count > 1
            && ZeroObjective(s, w, sigma) >= Objective(s, w, segs, sigma))
        {
            segs = [(0, nCues, ZeroState)];
        }

        double[] eff = ShiftsToEffective(segs, nCues);

        // Order guard FIRST, then the decision: the guard pulls individual cues away from
        // their segment's value at a step, and those pulled values are what the deploy rule
        // must score. Guarding afterwards would let a correction through on evidence that
        // does not describe what would actually be written.
        int guardCount = GuardOrder(starts, ends, eff);

        // NO-REGRESSION: only write a correction that proves itself. The evidence lives
        // in the cues the fit MOVES, so the paired test runs there.
        int moved = 0;
        double sumDiff = 0, sumSq = 0;
        double rawSum = 0, newSum = 0;
        for (int c = 0; c < nCues; c++)
        {
            double before = sRaw[c, ZeroState];
            double after = sRaw[c, StateOf(eff[c])];
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

        bool reverted = moved == 0 || tMoved <= DeployZ || scoreAfter < scoreBefore - 1e-9
                        || largest < SubtitleSync.MinShiftSec;
        if (reverted)
        {
            Array.Clear(eff);
            segs = [(0, nCues, ZeroState)];
            scoreAfter = scoreBefore;
            tMoved = 0;
            moved = 0;
        }

        return new FitResult
        {
            Ran = true,
            Reverted = reverted,
            Segments = segs.Count,
            SegmentStartTimesSec = segs.ConvertAll(g => starts[g.Lo]),
            SegmentOffsetsSec = segs.ConvertAll(g => -States[g.State]),
            AppliedShiftsSec = eff,
            MovedCues = moved,
            TMoved = tMoved,
            ScoreBefore = scoreBefore,
            ScoreAfter = scoreAfter,
            CueCount = nCues,
            MinSegCues = minSeg,
            GuardedCues = guardCount
        };
    }

    /// <summary>State index closest to a shift value.</summary>
    /// <param name="sec">Shift in seconds.</param>
    /// <returns>State index.</returns>
    public static int StateOf(double sec)
    {
        int k = (int)Math.Round((sec + MaxOffsetSec) / StateSec);
        return Math.Clamp(k, 0, States.Length - 1);
    }

    private static double[] ShiftsToEffective(
        List<(int Lo, int Hi, int State)> segs, int nCues)
    {
        var eff = new double[nCues];
        for (int c = 0; c < nCues; c++)
        {
            double v = States[segs[0].State];
            foreach ((int lo, int hi, int st) in segs)
            {
                if (c >= lo)
                {
                    v = States[st];
                }
            }

            eff[c] = v;
        }

        return eff;
    }

    /// <summary>
    /// Applies the order guard to the per-cue shifts: no cue overtakes its predecessor's
    /// end. A cue that would land below zero is clamped by the writer, not here.
    /// <para>
    /// This runs BEFORE the deploy rule decides, and that order is load-bearing. At a step
    /// the two neighbours move by different amounts, and a step wider than the gap between
    /// them would push the earlier cue's end past the later cue's start — which a player
    /// renders as stacked text. The guard therefore pulls individual cues away from their
    /// segment's value, and those pulled values are what the deploy rule must score.
    /// Measured on the confirmed-good BCS file: without the guard the rule saw 118 moved
    /// cues and t = +2.74 and APPLIED the correction, while the reference — which guards
    /// first — saw 114 cues and t = +0.325 and correctly REFUSED it.
    /// </para>
    /// <para>
    /// The bound is a LOWER one, because the shift is ADDED: <c>eff[k] ≥ eff[k−1] − gap +
    /// MinGapSec</c>. A gap already below the floor must not be shrunk further — measured,
    /// 560 of 724 gaps in this material are under 0.04 s with a median of 0.002 s, so
    /// demanding the floor everywhere carries one shift through the whole file.
    /// </para>
    /// </summary>
    /// <param name="starts">Cue starts.</param>
    /// <param name="ends">Cue ends.</param>
    /// <param name="eff">Per-cue shifts, modified in place.</param>
    /// <returns>The number of cues the guard touched.</returns>
    public static int GuardOrder(double[] starts, double[] ends, double[] eff)
    {
        int guarded = 0;
        for (int k = 1; k < eff.Length; k++)
        {
            double gap = starts[k] - ends[k - 1];
            double lower = gap < SubtitleSync.MinGapSec
                ? eff[k - 1]
                : eff[k - 1] - gap + SubtitleSync.MinGapSec;
            if (eff[k] < lower)
            {
                eff[k] = lower;
                guarded++;
            }
        }

        // A cue that lands below zero is CLAMPED to zero by the WRITER, never fixed here.
        // Raising the whole file instead (what this block used to do) silently replaced the
        // fitted shift with the largest one the first cue allows. Measured on BCS S01E04
        // German: the fit's own peak is -9.00 s and the leading title card sits at 5.07 s,
        // so the block lifted EVERY cue to -5.07 s — where t collapsed to +0.68 and the
        // deploy rule reported "no proven gain" on a file that was nine seconds out
        // (operator heard it, 07.10.2026). Operator order: apply the fit as measured, clamp
        // whatever lands below zero.
        return guarded;
    }

    /// <summary>
    /// Exposes the measured sigma (the median absolute score change per grid step), so a
    /// cross-language test can compare the two implementations' inputs, not just their
    /// output. A disagreement there points at the table; agreeing inputs with different
    /// output points at the fit.
    /// </summary>
    /// <param name="s">Cue table.</param>
    /// <returns>Sigma, as the fit uses it.</returns>
    public static double DebugSigma(double[,] s) => MedianAbsStep(s);

    private static double MedianAbsStep(double[,] s)
    {
        int nc = s.GetLength(0), ns = s.GetLength(1);
        if (ns < 2)
        {
            return 1e-9;
        }

        // EVERY cue contributes, not a subsample: the reference implementation takes the
        // median over the whole table, and a subsample shifts the median enough to change
        // which steps the charge can pay for. Measured: sampling every fourth cue gave
        // sigma 0.00879 against the reference 0.00899 on the same audio, and the fit then
        // kept a 17 s tail segment the reference rejected.
        var diffs = new List<double>(nc * (ns - 1));
        for (int c = 0; c < nc; c++)
        {
            for (int k = 0; k + 1 < ns; k++)
            {
                diffs.Add(Math.Abs(s[c, k + 1] - s[c, k]));
            }
        }

        if (diffs.Count == 0)
        {
            return 1e-9;
        }

        diffs.Sort();
        double m = diffs[diffs.Count / 2];
        return m > 1e-9 ? m : 1e-9;
    }

    private static int[] MakeBounds(int nCues)
    {
        var b = new List<int>();
        for (int i = 0; i < nCues; i += BlockCues)
        {
            b.Add(i);
        }

        if (b.Count > 0 && nCues - b[^1] < BlockCues / 2)
        {
            b.RemoveAt(b.Count - 1);
        }

        b.Add(nCues);
        return b.ToArray();
    }

    private static List<(int Lo, int Hi, int State)> FitDp(
        double[,] s, bool[,] w, int[] bounds, int minSegCues, double sigma)
    {
        int nb = bounds.Length - 1;
        int minBlocks = Math.Max(1, (int)Math.Ceiling(minSegCues / (double)BlockCues));
        int ns = States.Length;
        const double Neg = -1e15;

        var blockSum = new double[nb, ns];
        for (int b = 0; b < nb; b++)
        {
            for (int k = 0; k < ns; k++)
            {
                double acc = 0;
                for (int c = bounds[b]; c < bounds[b + 1]; c++)
                {
                    acc += s[c, k];
                }

                blockSum[b, k] = acc;
            }
        }

        var cum = new double[nb + 1, ns];
        for (int b = 0; b < nb; b++)
        {
            for (int k = 0; k < ns; k++)
            {
                cum[b + 1, k] = cum[b, k] + blockSum[b, k];
            }
        }

        var v = new double[nb + 1];
        var back = new int[nb + 1];
        var state = new int[nb + 1];
        for (int i = 0; i <= nb; i++)
        {
            v[i] = Neg;
        }

        v[0] = 0;
        for (int j = 1; j <= nb; j++)
        {
            for (int i = 0; i < j; i++)
            {
                // A segment must be long enough IN CUES, not in blocks: the block count is
                // only an approximation of the constant, and a block at the file end can be
                // short enough to slip a 10-cue segment through. Both implementations
                // produced exactly that as a tail segment, with the score deciding its SIGN
                // by near-boundary noise in the dilated curve. The whole-file fit is always
                // allowed, which is what lets a short file be handled at all.
                int length = j - i;
                bool wholeFile = i == 0 && j == nb;
                if (!wholeFile && (bounds[j] - bounds[i]) < minSegCues)
                {
                    continue;
                }

                if (length < minBlocks && !wholeFile)
                {
                    continue;
                }

                if (i > 0 && v[i] <= Neg / 2)
                {
                    continue;
                }

                int best = 0;
                double bestVal = double.NegativeInfinity;
                for (int k = 0; k < ns; k++)
                {
                    double val = cum[j, k] - cum[i, k];
                    if (val > bestVal)
                    {
                        bestVal = val;
                        best = k;
                    }
                }

                double charge = Z * sigma * Math.Sqrt(bounds[j] - bounds[i]);
                double tot = v[i] + bestVal - charge;
                if (tot > v[j])
                {
                    v[j] = tot;
                    back[j] = i;
                    state[j] = best;
                }
            }
        }

        if (v[nb] <= Neg / 2)
        {
            return [(0, bounds[nb], ZeroState)];
        }

        var segs = new List<(int Lo, int Hi, int State)>();
        int jj = nb;
        while (jj > 0)
        {
            int i = back[jj];
            segs.Add((bounds[i], bounds[jj], state[jj]));
            jj = i;
        }

        segs.Reverse();
        return segs;
    }

    private static void RefineBoundaries(double[,] s, List<(int Lo, int Hi, int State)> segs)
    {
        for (int k = 1; k < segs.Count; k++)
        {
            (int plo, int phi, int pk) = segs[k - 1];
            (int lo, int hi, int ck) = segs[k];
            if (hi - plo < 8)
            {
                continue;
            }

            double best = double.NegativeInfinity;
            int cut = -1;
            int step = Math.Max(1, (hi - plo) / 64);
            for (int i = plo + 4; i < hi - 3; i += step)
            {
                double tot = Sum(s, plo, i, pk) + Sum(s, i, hi, ck);
                if (tot > best)
                {
                    best = tot;
                    cut = i;
                }
            }

            if (cut > 0 && plo + 4 <= cut && cut <= hi - 4)
            {
                segs[k - 1] = (plo, cut, pk);
                segs[k] = (cut, hi, ck);
            }
        }
    }

    private static double Sum(double[,] s, int lo, int hi, int k)
    {
        double acc = 0;
        for (int c = lo; c < hi; c++)
        {
            acc += s[c, k];
        }

        return acc;
    }

    private static void DropEdgeSegments(List<(int Lo, int Hi, int State)> segs)
    {
        int k = 0;
        while (k < segs.Count && segs.Count > 1)
        {
            if (Math.Abs(States[segs[k].State]) >= MaxOffsetSec - EdgeMarginSec)
            {
                if (k > 0)
                {
                    segs[k - 1] = (segs[k - 1].Lo, segs[k].Hi, segs[k - 1].State);
                }
                else
                {
                    segs[k + 1] = (segs[k].Lo, segs[k + 1].Hi, segs[k + 1].State);
                }

                segs.RemoveAt(k);
                continue;
            }

            k++;
        }
    }

    private static double Objective(
        double[,] s, bool[,] w, List<(int Lo, int Hi, int State)> segs, double sigma)
    {
        double tot = 0;
        foreach ((int lo, int hi, int st) in segs)
        {
            double best = double.NegativeInfinity;
            int bk = 0;
            for (int k = 0; k < States.Length; k++)
            {
                double val = Sum(s, lo, hi, k);
                if (val > best)
                {
                    best = val;
                    bk = k;
                }
            }

            int justified = 0;
            for (int c = lo; c < hi; c++)
            {
                if (w[c, bk])
                {
                    justified++;
                }
            }

            tot += best - (Z * sigma * Math.Sqrt(justified));
        }

        return tot;
    }

    private static double ZeroObjective(double[,] s, bool[,] w, double sigma)
    {
        int nc = s.GetLength(0);
        double sum = 0;
        int justified = 0;
        for (int c = 0; c < nc; c++)
        {
            sum += s[c, ZeroState];
            if (w[c, ZeroState])
            {
                justified++;
            }
        }

        return sum - (Z * sigma * Math.Sqrt(justified));
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

    /// <summary>F-M307: the result of <see cref="Fit"/>.</summary>
    public sealed class FitResult
    {
        /// <summary>Gets a value indicating whether the fit ran at all.</summary>
        public bool Ran { get; init; }

        /// <summary>Gets the reason the fit did not run.</summary>
        public string? SkipReason { get; init; }

        /// <summary>Gets a value indicating whether the deploy rule refused the correction.</summary>
        public bool Reverted { get; init; }

        /// <summary>Gets the number of segments.</summary>
        public int Segments { get; init; }

        /// <summary>Gets the start time of each segment, in seconds.</summary>
        public List<double> SegmentStartTimesSec { get; init; } = [];

        /// <summary>
        /// Gets the measured offset per segment, in seconds, as the detector reads it
        /// ("how far the cue sits AFTER the speech"). The correction is its NEGATIVE.
        /// </summary>
        public List<double> SegmentOffsetsSec { get; init; } = [];

        /// <summary>Gets the shift to APPLY per cue, in seconds.</summary>
        public double[] AppliedShiftsSec { get; init; } = [];

        /// <summary>Gets the number of cues the fit moves.</summary>
        public int MovedCues { get; init; }

        /// <summary>Gets the paired t of the score gain over the moved cues.</summary>
        public double TMoved { get; init; }

        /// <summary>Gets the whole-file speech score before the correction.</summary>
        public double ScoreBefore { get; init; }

        /// <summary>Gets the whole-file speech score after the correction.</summary>
        public double ScoreAfter { get; init; }

        /// <summary>Gets the cue count the fit saw.</summary>
        public int CueCount { get; init; }

        /// <summary>Gets the shortest segment the fit allowed, in cues.</summary>
        public int MinSegCues { get; init; }

        /// <summary>Gets the number of cues the order guard touched.</summary>
        public int GuardedCues { get; init; }

        /// <summary>Builds a "did not run" result.</summary>
        /// <param name="reason">Why it did not run.</param>
        /// <returns>The result.</returns>
        public static FitResult NotMeasured(string reason)
            => new() { Ran = false, SkipReason = reason };
    }
}
