// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// F-M340: both fits against a PLANTED truth. This is the decisive measurement for der Polygonzug
// proposal, because it needs no judgement: a known displacement is written into a file the
// operator confirmed as good, and each model's answer is compared with that known truth per cue.
//
// WHY A PLANTED TRUTH AND NOT JUST THE REAL FILES
//
// On a real file nobody knows the true drift, so "der Polygonzug found a wave" cannot be called wrong.
// With a planted linear drift the answer is known exactly: a model that represents continuous
// drift should recover it, and one that cannot should miss it by roughly its own step size.
//
// The staircase is planted as the CONTROL: if neither model recovers it, the measurement is
// broken, not the models.
//
// USAGE: truthrun
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Jellyfin.Plugin.SubdlScribe.Qa;

internal static class TruthRun
{
    private const string CacheDir = "/opt/data/drift-lab/cache-levels";
    private static readonly Regex TsRe = new(@"\d{2}:\d{2}:\d{2},\d{3}", RegexOptions.Compiled);

    private const string GoodMkv = "/data/movies/Better.Call.Saul/S01/Better.Call.Saul.S01E04.720p.HDTV.X264-MRSK.mkv";
    private const string GoodSrt = "/opt/data/drift-lab/raw/bcs_raw.sdh.srt";

    private static int Main()
    {
        string original = File.ReadAllText(GoodSrt);
        (double[] starts, double[] ends) = DriftGate.ParseCues(original);
        double[] levels = DecodeCached(GoodMkv);
        double duration = starts.Length > 0 ? starts[^1] : 0;
        Console.WriteLine($"{Path.GetFileName(GoodSrt)}: {starts.Length} cues, last cue {duration / 60:0.0} min");

        var cases = new List<(string Label, Func<double, double> Disp)>
        {
            ("control: nothing planted", _ => 0.0),

            // Continuous drift — the case der Polygonzug proposal is ABOUT. A tape stretched at a
            // constant rate moves every cue in proportion to its position.
            ("LINEAR +8 s over the file", t => duration > 0 ? 8.0 * t / duration : 0.0),
            ("LINEAR -6 s over the file", t => duration > 0 ? -6.0 * t / duration : 0.0),

            // Steps — what the incumbent is built for, as the control.
            ("STEP +4 s from 15 min", t => t >= 900 ? 4.0 : 0.0),
            ("STEP +4 s from 15 min, +7 s from 30 min", t => t >= 1800 ? 7.0 : t >= 900 ? 4.0 : 0.0)
        };

        Console.WriteLine();
        Console.WriteLine($"{"planted",42} | {"MODEL",8} | {"reverted",8} | {"RMS err",8} | {"max err",8} | {"moved",6} | shape");
        Console.WriteLine(new string('-', 120));

        foreach ((string label, Func<double, double> f) in cases)
        {
            var disp = new double[starts.Length];
            for (int i = 0; i < starts.Length; i++)
            {
                disp[i] = f(starts[i]);
            }

            string shifted = Rewrite(original, starts, ends, disp);
            (double[] ps, double[] pe) = DriftGate.ParseCues(shifted);

            // The correction must be the NEGATIVE of the planted displacement.
            var truth = new double[disp.Length];
            for (int i = 0; i < disp.Length; i++)
            {
                truth[i] = -disp[i];
            }

            bool nothingPlanted = label.StartsWith("control", StringComparison.Ordinal);

            diagStarts = ps;
            diagEnds = pe;
            diagLevels = levels;
            OffsetFit.FitResult inc = OffsetFit.Fit(ps, pe, levels);
            // SegmentValues are the APPLIED value per segment (the writer ADDS AppliedShiftsSec,
            // and SegmentOffsetsSec is the same number negated). Comparing one model's offsets
            // against the other's applied shifts would show a sign flip that is not a difference
            // between the models at all.
            var incShape = inc.SegmentOffsetsSec.Select(x => Math.Round(-x, 2)).ToList();
            Report(label, "staircase", nothingPlanted, truth,
                inc.AppliedShiftsSec, inc.Reverted, inc.MovedCues,
                inc.SegmentOffsetsSec.Select(x => Math.Round(x, 2)).ToList());

            StuetzstellenFit.StuetzstellenResult stuetz = StuetzstellenFit.Fit(ps, pe, levels);
            stObj = stuetz.Objective;
            stRecomputed = stuetz.KnotCueIndex.Length > 1
                ? ObjectiveOfPath(diagStarts, diagEnds, diagLevels, stuetz.KnotCueIndex, stuetz.KnotValuesSec)
                : double.NaN;
            stObjZero = stuetz.ZeroObjective;
            Report(label, "stuetzstellen", nothingPlanted, truth,
                stuetz.AppliedShiftsSec, stuetz.Reverted, stuetz.MovedCues,
                stuetz.KnotValuesSec.Select(x => Math.Round(x, 1)).ToList());

            Console.WriteLine();
        }

        return 0;
    }

    private static double stRecomputed;
    private static double stObj;
    private static double stObjZero;

    private static double[] diagStarts = Array.Empty<double>();
    private static double[] diagEnds = Array.Empty<double>();
    private static double[] diagLevels = Array.Empty<double>();

    private static void Report(
        string label, string model, bool nothingPlanted, double[] truth, double[] applied,
        bool reverted, int moved, List<double> shape)
    {
        if (model == "stuetzstellen" && diagStarts.Length > 0)
        {
            Console.WriteLine($"      [diag] DP claims {stObj:0.0}, recomputed {stRecomputed:0.0} => "
                              + $"{(Math.Abs(stObj - stRecomputed) < 0.5 ? "BOOKKEEPING OK" : "BUG: MISMATCH")}  knots={string.Join("/", shape.Take(6))}");
        }

        if (nothingPlanted)
        {
            if (model == "stuetzstellen")
            {
                var line = BestSingleLine(diagStarts, diagEnds, diagLevels);
                Console.WriteLine($"      [diag] DP claims {stObj:0.0}, recomputed {stRecomputed:0.0} "
                                  + $"=> {(Math.Abs(stObj - stRecomputed) < 0.5 ? "BOOKKEEPING OK" : "BUG: MISMATCH")}");
                Console.WriteLine($"      [diag] 1-line {line.Obj:0.0} (v0={line.V0:0.00} v1={line.V1:0.00}) zero={stObjZero:0.0}");
                Console.WriteLine($"      [diag] DP knots={string.Join("/", shape)}");
            }
            // A control must come out UNCHANGED. Any movement here is a defect, so the error is
            // reported against zero rather than against a truth nobody planted.
            double worstControl = applied.Length == 0 ? 0 : applied.Max(Math.Abs);
            Console.WriteLine($"{label,42} | {model,8} | {reverted,8} | {"-",8} | {worstControl,8:0.00} | {moved,6} | "
                              + $"{(reverted ? "left alone" : "MOVED " + string.Join("/", shape.Take(6)))}");
            return;
        }

        int n = Math.Min(applied.Length, truth.Length);
        if (n == 0 || reverted)
        {
            double truthMax = truth.Length == 0 ? 0 : truth.Max(Math.Abs);
            Console.WriteLine($"{label,42} | {model,8} | {reverted,8} | {"n/a",8} | {truthMax,8:0.00} | {moved,6} | "
                              + "REFUSED — truth was up to " + truthMax.ToString("0.00", CultureInfo.InvariantCulture) + " s");
            return;
        }

        double sum = 0;
        double worst = 0;
        for (int i = 0; i < n; i++)
        {
            double e = applied[i] - truth[i];
            sum += e * e;
            worst = Math.Max(worst, Math.Abs(e));
        }

        Console.WriteLine($"{label,42} | {model,8} | {reverted,8} | {Math.Sqrt(sum / n),8:0.00} | {worst,8:0.00} | {moved,6} | "
                          + string.Join("/", shape.Take(6)));
    }

    /// <summary>
    /// Brute force over EVERY (start value, end value) pair for a single straight line across the
    /// whole file — the same model the DP is supposed to search. If this finds a better objective
    /// than the DP returned, the DP is wrong, not the model.
    /// </summary>
    private static (double Obj, double V0, double V1) BestSingleLine(
        double[] starts, double[] ends, double[] levels)
    {
        double[] pFit = OffsetFit.Dilate(OffsetFit.SpeechProbability(levels), OffsetFit.SlackSec);
        double[] cum = OffsetFit.Cumulative(pFit);
        (double[,] s, bool[,] w) = OffsetFit.CueTable(starts, ends, cum, pFit.Length);
        double sigma = OffsetFit.DebugSigma(s);
        double[] states = OffsetFit.DebugStates();
        int ns = states.Length;
        int zero = Array.IndexOf(states, 0.0);
        double charge = OffsetFit.Z * sigma * Math.Sqrt(starts.Length) * Math.Sqrt(2.0);
        double tLo = starts[0];
        double tSpan = starts[^1] - tLo;

        double best = double.NegativeInfinity;
        double bv0 = 0, bv1 = 0;
        for (int a = 0; a < ns; a++)
        {
            double va = states[a];
            for (int b = 0; b < ns; b++)
            {
                double vb = states[b];
                double acc = 0;
                for (int c = 0; c < starts.Length; c++)
                {
                    double f = tSpan > 0 ? (starts[c] - tLo) / tSpan : 0.0;
                    double v = va + ((vb - va) * f);
                    int idx = zero + (int)Math.Round(v / OffsetFit.StateSec);
                    if (idx < 0)
                    {
                        idx = 0;
                    }

                    if (idx >= ns)
                    {
                        idx = ns - 1;
                    }

                    acc += s[c, idx];
                }

                if (acc > best)
                {
                    best = acc;
                    bv0 = va;
                    bv1 = vb;
                }
            }
        }

        return (best - charge, bv0, bv1);
    }

    /// <summary>
    /// Recomputes the objective of the path the DP RETURNED, independently of the DP. A mismatch
    /// means the DP's bookkeeping is wrong; a match means its answer really is the optimum of the
    /// objective it was given, and any nonsense in the shape is the OBJECTIVE's fault.
    /// </summary>
    private static double ObjectiveOfPath(double[] starts, double[] ends, double[] levels,
                                          int[] knotCues, double[] knotVals)
    {
        double[] pFit = OffsetFit.Dilate(OffsetFit.SpeechProbability(levels), OffsetFit.SlackSec);
        double[] cum = OffsetFit.Cumulative(pFit);
        (double[,] s, bool[,] _) = OffsetFit.CueTable(starts, ends, cum, pFit.Length);
        double sigma = OffsetFit.DebugSigma(s);
        double[] states = OffsetFit.DebugStates();
        int ns = states.Length;
        int zero = Array.IndexOf(states, 0.0);
        double slopeLuck = Math.Sqrt(2.0);

        double total = 0;
        for (int q = 0; q + 1 < knotCues.Length; q++)
        {
            int lo = knotCues[q];
            int hi = knotCues[q + 1];
            double vi = knotVals[q];
            double vj = knotVals[q + 1];
            double tLo = starts[lo];
            double tSpan = starts[hi - 1] - tLo;
            double acc = 0;
            for (int c = lo; c < hi; c++)
            {
                double f = tSpan > 0 ? (starts[c] - tLo) / tSpan : 0.0;
                double v = vi + ((vj - vi) * f);
                int idx = zero + (int)Math.Round(v / OffsetFit.StateSec);
                idx = Math.Max(0, Math.Min(ns - 1, idx));
                acc += s[c, idx];
            }

            double charge = OffsetFit.Z * sigma * Math.Sqrt(hi - lo) * slopeLuck;
            total += acc - charge;
        }

        return total;
    }

    private static string Rewrite(string text, double[] starts, double[] ends, double[] disp)
    {
        MatchCollection ms = TsRe.Matches(text);
        var sb = new StringBuilder();
        int last = 0;
        for (int j = 0; j < ms.Count; j++)
        {
            sb.Append(text, last, ms[j].Index - last);
            int cue = j / 2;
            double v = (j % 2 == 0 ? starts[cue] : ends[cue]) + disp[cue];
            sb.Append(Fmt(Math.Max(v, 0)));
            last = ms[j].Index + ms[j].Length;
        }

        sb.Append(text, last, text.Length - last);
        return sb.ToString();
    }

    private static string Fmt(double t)
    {
        int ms = (int)Math.Round(t * 1000);
        int h = ms / 3600000;
        ms -= h * 3600000;
        int m = ms / 60000;
        ms -= m * 60000;
        int s = ms / 1000;
        ms -= s * 1000;
        return $"{h:00}:{m:00}:{s:00},{ms:000}";
    }

    private static double[] DecodeCached(string media)
    {
        string raw = Path.Combine(CacheDir, Path.GetFileName(media) + ".levels");
        if (File.Exists(raw))
        {
            string[] lines = File.ReadAllLines(raw);
            var cached = new double[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                cached[i] = double.Parse(lines[i], CultureInfo.InvariantCulture);
            }

            return cached;
        }

        double[] levels = DriftGate.DecodeFrameLevelsAsync("/usr/bin/ffmpeg", media, CancellationToken.None, "0:a:0")
            .GetAwaiter().GetResult();
        Directory.CreateDirectory(CacheDir);
        File.WriteAllLines(raw, levels.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
        return levels;
    }
}
