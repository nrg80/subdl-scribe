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
// F-M297: repairs a drifting subtitle against a same-language reference, and
// REFUSES the repair when the measurement says it made things worse.
//
// WHY TEXT AND NOT AUDIO
//
// Correcting from audio alone was measured on 36 drifting episodes: 33 improved,
// 2 were made worse, and no threshold on any number read off the run separated the
// two (six candidates tried — see the skill note). Worse, run on a CLEAN file it
// invents damage: the reference track of Invasion S01E06, which sits at 0.00 s by
// text against its own plain sibling, came back from the audio path with ten
// segments and offsets hopping from −2.70 s to +17.90 s. A drifting file has no
// single offset, and the audio's segment boundaries do not land on the real ones.
//
// Text has neither problem. Two cues carrying identical text ARE the same line, so
// `target − reference` is that line's true error to the centisecond — no model, no
// audio, no drift of its own. A jump in that difference is a real cut, and a cut is
// a STEP between two anchors, not a value that has to be averaged across it. That
// distinction is the whole fix: the first version interpolated a ramp between 60 s
// bins and dragged a step backwards over two minutes, leaving lines 2.44 s early
// while the median looked excellent.
//
// THE FOUR RULES, EACH ONE A MEASURED REPAIR
//
//  1. NEAREST anchor, not a windowed median and not interpolation. Keeps a step
//     confined to the gap between two adjacent anchors.
//
//  2. MONOTONE, in WHICHEVER DIRECTION FITS. A real drift curve does not reverse.
//     But the direction is a property of the file, not an assumption: PAVA is a
//     non-decreasing fit, so applying it to a falling sequence collapses the result
//     to the mean — every cue gets the same shift, no drift is removed and the file
//     simply moves (measured: 9.05 s → 9.05 s, spread unchanged, middle worse). Four
//     of the 36 files drift DOWNWARD, so both directions are fitted and the smaller
//     sum of squared residuals wins.
//
//  3. ORDER IS NEVER TRADED FOR A SMALLER NUMBER. Where anchors are sparse a cue can
//     end up nearer a LATER anchor, receive the offset from the far side of a step,
//     move backwards past its neighbour — and since the list is re-sorted by start
//     time, the two SWAP. Measured: two lines of dialogue came out inverted and one
//     was squeezed to 0.26 s while every timing number looked fine.
//
//  4. THE GUARD COMPARES AGAINST THE PREVIOUS CUE'S END, not its start. Two
//     neighbours whose shifts differ by slightly more than the gap keep their start
//     order while their ENDS collide — the earlier cue's end lands after the later
//     cue's start, which a player renders as stacked text. Measured: one cue squeezed
//     to 0.20 s and a 0.111 s overlap.
//
//  5. ITERATE, BECAUSE ONE PASS LEAVES RESIDUE. Three passes, each re-measuring the
//     anchors against the reference. (F-M297)
//
// AND THEN IT CHECKS ITSELF
//
// A correction measured with the tool that produced it proves nothing: it is the
// exact inverse of its own measurement, so it always reports success. The check here
// is the anchor residual computed against the REFERENCE — a file that took no part in
// the correction. The result is accepted only when the WORST single-cue residual
// improves. Measured on the 36 files: this flags exactly the 2 real failures and 0 of
// the 33 successes, and the same two with all three statistics (median, p90, worst),
// which is why a plain "is it worse" comparison needs no invented threshold.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubdlScribe.Qa;

/// <summary>
/// F-M297: anchor-based drift correction with a self-check that can refuse.
/// </summary>
public static class AnchorSync
{
    /// <summary>Shortest cue text that may serve as an anchor, in characters.</summary>
    public const int MinAnchorLength = 15;

    /// <summary>Anchor pairs further apart than this are treated as mismatches, in seconds.</summary>
    public const double AnchorGuardSec = 90.0;

    /// <summary>Number of correction passes. One leaves residue; three is the measured setting.</summary>
    public const int Passes = 3;

    /// <summary>
    /// Minimum gap kept between one cue's end and the next cue's start, in seconds.
    /// <para>
    /// 0.04 s, not a smaller value chosen for tidiness: these subtitles butt cue against cue —
    /// measured on a real episode, <b>560 of 724 gaps are below 0.04 s</b> and the median gap is
    /// <b>0.002 s</b>. A guard set at 0.01 s therefore fires on almost every cue of a file that
    /// is not overlapping at all, and each firing carries the previous shift forward instead of
    /// letting the correction through. The prototype used 0.04 s.
    /// </para>
    /// </summary>
    public const double MinGapSec = 0.04;

    /// <summary>A cue may not become shorter than this, in seconds.</summary>
    public const double MinDurationSec = 0.20;

    /// <summary>The worst single-cue residual a correction must beat, in seconds.</summary>
    public const double ImprovementMarginSec = 0.2;

    /// <summary>One cue: start, end, raw text.</summary>
    /// <param name="StartSec">Cue start in seconds.</param>
    /// <param name="EndSec">Cue end in seconds.</param>
    /// <param name="Text">Cue text as it appears in the file.</param>
    public readonly record struct Cue(double StartSec, double EndSec, string Text);

    /// <summary>Residual statistics of a target against a reference.</summary>
    /// <param name="Count">Number of anchor pairs.</param>
    /// <param name="MedianSec">Median absolute residual.</param>
    /// <param name="P90Sec">90th percentile absolute residual.</param>
    /// <param name="WorstSec">Worst absolute residual.</param>
    public readonly record struct Residuals(int Count, double MedianSec, double P90Sec, double WorstSec);

    /// <summary>The outcome of an anchor-based correction.</summary>
    /// <param name="Applied">True when the corrected cues beat the original and are returned.</param>
    /// <param name="Cues">Corrected cues when <see cref="Applied"/>, else the input.</param>
    /// <param name="Before">Residuals of the input against the reference.</param>
    /// <param name="After">Residuals of the correction against the reference.</param>
    /// <param name="ClampedOverlaps">Cues whose end had to be pulled back to remove an overlap.</param>
    /// <param name="Reason">Human-readable outcome, for the log.</param>
    public readonly record struct Result(
        bool Applied,
        IReadOnlyList<Cue> Cues,
        Residuals Before,
        Residuals After,
        int ClampedOverlaps,
        string Reason);

    /// <summary>
    /// Strips the markup that keeps otherwise identical lines from matching: HTML-style
    /// tags, brace and bracket annotations (SDH sound cues), and whitespace runs.
    /// </summary>
    /// <param name="text">Raw cue text.</param>
    /// <returns>Comparable text.</returns>
    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string x = Regex.Replace(text, "<[^>]+>", string.Empty);
        x = Regex.Replace(x, "\\{[^}]*\\}", " ");
        x = Regex.Replace(x, "\\[[^\\]]*\\]", " ");
        return Regex.Replace(x, "\\s+", " ").Trim();
    }

    /// <summary>
    /// Splits SRT text into cues. Index lines and the timestamp line are dropped; the
    /// remaining lines are joined because a cue may span two lines.
    /// </summary>
    /// <param name="srtText">SRT content.</param>
    /// <returns>Cues sorted by start time.</returns>
    public static List<Cue> Parse(string srtText)
    {
        var cues = new List<Cue>();
        if (string.IsNullOrEmpty(srtText))
        {
            return cues;
        }

        var ts = new Regex(
            @"(\d{2}):(\d{2}):(\d{2})[,. ](\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})[,. ](\d{3})");
        string norm = srtText.Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (string block in Regex.Split(norm.Trim(), "\n\\s*\n"))
        {
            Match m = ts.Match(block);
            if (!m.Success)
            {
                continue;
            }

            double s = Secs(m, 1);
            double e = Secs(m, 5);
            var body = new List<string>();
            foreach (string line in block.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length == 0 || t.Contains("-->", StringComparison.Ordinal)
                    || Regex.IsMatch(t, @"^\d+$"))
                {
                    continue;
                }

                body.Add(t);
            }

            cues.Add(new Cue(s, e, string.Join("\n", body)));
        }

        return cues.OrderBy(c => c.StartSec).ToList();
    }

    /// <summary>
    /// Anchor residuals of <paramref name="target"/> against <paramref name="reference"/>:
    /// cues carrying identical cleaned text are the same line, and their start difference
    /// is that line's true error.
    /// </summary>
    /// <param name="reference">Reference cues (the plain track).</param>
    /// <param name="target">Cues to measure.</param>
    /// <returns>Statistics, or a zero count when fewer than 20 pairs exist.</returns>
    public static Residuals Measure(IReadOnlyList<Cue> reference, IReadOnlyList<Cue> target)
    {
        var diffs = AnchorDiffs(reference, target);
        if (diffs.Count < 20)
        {
            return default;
        }

        var abs = diffs.Select(d => Math.Abs(d.Delta)).OrderBy(x => x).ToArray();
        return new Residuals(
            abs.Length,
            Median(abs),
            abs[(int)(0.9 * (abs.Length - 1))],
            abs[^1]);
    }

    /// <summary>
    /// Corrects <paramref name="target"/> against <paramref name="reference"/>, and returns
    /// the correction ONLY when it improves the worst single-cue residual.
    /// </summary>
    /// <param name="reference">Reference cues (plain track, same language).</param>
    /// <param name="target">Cues to correct.</param>
    /// <returns>The result. Never throws.</returns>
    public static Result Correct(IReadOnlyList<Cue> reference, IReadOnlyList<Cue> target)
    {
        if (reference.Count == 0 || target.Count == 0)
        {
            return new Result(false, target, default, default, 0, "no cues on one side");
        }

        Residuals before = Measure(reference, target);
        if (before.Count < 20)
        {
            return new Result(false, target, before, default, 0,
                $"only {before.Count} matching lines — too few to anchor on");
        }

        var cues = target.ToList();
        int clamped = 0;
        for (int pass = 0; pass < Passes; pass++)
        {
            var anchors = AnchorDiffs(reference, cues);
            if (anchors.Count < 20)
            {
                break;
            }

            List<(double Time, double Delta)> pairs = anchors
                .Select<(double Time, double Delta), (double Time, double Delta)>(a => (a.Time, a.Delta))
                .OrderBy(p => p.Time)
                .ToList();

            List<double> shifts = NearestAnchorShifts(cues, pairs);
            for (int i = 0; i < cues.Count; i++)
            {
                cues[i] = new Cue(
                    cues[i].StartSec - shifts[i],
                    cues[i].EndSec - shifts[i],
                    cues[i].Text);
            }

            cues = cues.OrderBy(c => c.StartSec).ToList();
            clamped += ClampOverlaps(cues);
        }

        Residuals after = Measure(reference, cues);
        if (after.Count < 20)
        {
            return new Result(false, target, before, after, clamped,
                "measurement after the correction found too few matching lines");
        }

        // THE REFUSAL. A correction is only accepted when it improves the WORST single
        // line. The median alone hid a 2.44 s error on one line in an earlier run, so the
        // worst value is the one that decides.
        if (after.WorstSec >= before.WorstSec - ImprovementMarginSec)
        {
            return new Result(false, target, before, after, clamped,
                $"worst {before.WorstSec:0.00}s -> {after.WorstSec:0.00}s: no improvement, kept as downloaded");
        }

        return new Result(true, cues, before, after, clamped,
            $"worst {before.WorstSec:0.00}s -> {after.WorstSec:0.00}s (median {before.MedianSec:0.00}s -> {after.MedianSec:0.00}s)");
    }

    /// <summary>
    /// Pairs of (reference time, target-minus-reference) for cues carrying identical text,
    /// with ambiguous text (appearing more than once on either side) dropped.
    /// </summary>
    private static List<(double Time, double Delta)> AnchorDiffs(
        IReadOnlyList<Cue> reference,
        IReadOnlyList<Cue> target)
    {
        var index = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (Cue c in reference)
        {
            string key = Clean(c.Text);
            if (key.Length >= MinAnchorLength)
            {
                if (!index.TryGetValue(key, out List<double>? list))
                {
                    list = [];
                    index[key] = list;
                }

                list.Add(c.StartSec);
            }
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Cue c in target)
        {
            string key = Clean(c.Text);
            if (index.ContainsKey(key))
            {
                seen[key] = seen.GetValueOrDefault(key) + 1;
            }
        }

        var outList = new List<(double, double)>();
        foreach (Cue c in target)
        {
            string key = Clean(c.Text);
            if (!index.TryGetValue(key, out List<double>? times)
                || times.Count != 1
                || seen.GetValueOrDefault(key) != 1)
            {
                continue;
            }

            double d = c.StartSec - times[0];
            if (Math.Abs(d) < 120)
            {
                outList.Add((c.StartSec, d));
            }
        }

        return outList;
    }

    /// <summary>
    /// Shift per cue from the nearest anchor in time, made monotone and order-safe.
    /// </summary>
    private static List<double> NearestAnchorShifts(
        IReadOnlyList<Cue> cues,
        IReadOnlyList<(double Time, double Delta)> anchors)
    {
        var raw = new List<double>(cues.Count);
        int j = 0;
        foreach (Cue c in cues)
        {
            while (j + 1 < anchors.Count
                   && Math.Abs(anchors[j + 1].Time - c.StartSec) <= Math.Abs(anchors[j].Time - c.StartSec))
            {
                j++;
            }

            raw.Add(anchors[j].Delta);
        }

        List<double> mono = MonotoneBothDirections(raw);

        // Order guard: compare against the PREVIOUS CUE'S END, because that is what a
        // player renders. A shift may never consume a gap.
        var eff = new List<double>(cues.Count);
        for (int i = 0; i < cues.Count; i++)
        {
            if (i == 0)
            {
                eff.Add(mono[0]);
                continue;
            }

            double gap = cues[i].StartSec - cues[i - 1].EndSec;
            if (gap < MinGapSec)
            {
                eff.Add(eff[i - 1]);
            }
            else
            {
                double limit = eff[i - 1] + gap - MinGapSec;
                eff.Add(Math.Min(mono[i], limit));
            }
        }

        return eff;
    }

    /// <summary>
    /// Monotone fit, taking whichever of the two directions fits better. See rule 2.
    /// </summary>
    private static List<double> MonotoneBothDirections(IReadOnlyList<double> raw)
    {
        List<double> inc = Pava(raw);
        List<double> rev = raw.Reverse().ToList();
        List<double> decFitted = Pava(rev);
        decFitted.Reverse();

        double sseInc = 0, sseDec = 0;
        for (int i = 0; i < raw.Count; i++)
        {
            sseInc += (raw[i] - inc[i]) * (raw[i] - inc[i]);
            sseDec += (raw[i] - decFitted[i]) * (raw[i] - decFitted[i]);
        }

        return sseInc <= sseDec ? inc : decFitted;
    }

    /// <summary>Pool-adjacent-violators: non-decreasing least-squares fit.</summary>
    private static List<double> Pava(IReadOnlyList<double> values)
    {
        var vals = new List<double>();
        var wts = new List<double>();
        var cnt = new List<int>();
        foreach (double v in values)
        {
            vals.Add(v);
            wts.Add(1.0);
            cnt.Add(1);
            while (vals.Count > 1 && vals[^2] > vals[^1])
            {
                double v2 = vals[^1], w2 = wts[^1];
                int c2 = cnt[^1];
                vals.RemoveAt(vals.Count - 1);
                wts.RemoveAt(wts.Count - 1);
                cnt.RemoveAt(cnt.Count - 1);

                double v1 = vals[^1], w1 = wts[^1];
                int c1 = cnt[^1];
                vals.RemoveAt(vals.Count - 1);
                wts.RemoveAt(wts.Count - 1);
                cnt.RemoveAt(cnt.Count - 1);

                vals.Add(((v1 * w1) + (v2 * w2)) / (w1 + w2));
                wts.Add(w1 + w2);
                cnt.Add(c1 + c2);
            }
        }

        var outList = new List<double>(values.Count);
        for (int i = 0; i < vals.Count; i++)
        {
            for (int k = 0; k < cnt[i]; k++)
            {
                outList.Add(vals[i]);
            }
        }

        return outList;
    }

    /// <summary>
    /// Pulls a cue's end back so it does not reach the next cue's start, enforcing the
    /// duration floor only where it does not create a new overlap.
    /// </summary>
    /// <param name="cues">Cues, corrected in place (they must be sorted by start).</param>
    /// <returns>How many cues had their end pulled back.</returns>
    private static int ClampOverlaps(List<Cue> cues)
    {
        int pulled = 0;
        for (int i = 0; i < cues.Count - 1; i++)
        {
            double limit = cues[i + 1].StartSec - MinGapSec;
            if (cues[i].EndSec > limit)
            {
                // The floor may not win here: if the gap is genuinely below the floor there is
                // nothing to protect, and forcing the floor would push this cue's end past the
                // next cue's start — turning a small overlap into a larger one.
                double start = cues[i].StartSec;
                cues[i] = new Cue(start, Math.Max(start + MinDurationSec, limit), cues[i].Text);
                if (cues[i].EndSec > limit)
                {
                    cues[i] = new Cue(start, limit, cues[i].Text);
                }

                pulled++;
            }
        }

        return pulled;
    }

    private static double Median(double[] sorted)
        => sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2.0;

    private static double Secs(Match m, int g)
        => (int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) * 3600.0)
           + (int.Parse(m.Groups[g + 1].Value, CultureInfo.InvariantCulture) * 60.0)
           + int.Parse(m.Groups[g + 2].Value, CultureInfo.InvariantCulture)
           + (int.Parse(m.Groups[g + 3].Value, CultureInfo.InvariantCulture) / 1000.0);
}
