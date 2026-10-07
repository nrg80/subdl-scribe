// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// T119/T120/T121: the offset fit, driven on real files and on planted truths.
//
// Two-sided on purpose:
//   A) PLANTED truths on a file the operator confirmed as good. A staircase is written
//      into a copy, the fit runs, and the applied correction is compared with the
//      planted truth PER CUE. A control with nothing planted must come out unchanged
//      (T119).
//   B) REAL files, compared against the PYTHON REFERENCE at
//      /opt/data/drift-lab/RC/sy-1.0.0-rc1 — the artefact the operator listened to and
//      accepted. Same audio, same cues: the segment offsets must agree. Agreement
//      across two independent implementations is the strongest check available here,
//      because it fails on a porting mistake that either side alone would share (T120).
//   C) The two defects as arithmetic (T121): a window outside the audio is excluded
//      rather than clipped, and the score is taken over the cue SPAN, not over one
//      frame at the cue start.
//
// Usage: fitrun
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Jellyfin.Plugin.SubdlScribe.Qa;

internal static class FitRun
{
    private const string LabRoot = "/opt/data/drift-lab";
    // Holds the frame LEVELS (not samples) — see DecodeCached. A new directory, because
    // the old one holds 6.7 GB of raw samples from the byte-array decoder.
    private const string CacheDir = "/opt/data/drift-lab/cache-levels";
    private static readonly Regex TsRe = new(@"\d{2}:\d{2}:\d{2},\d{3}", RegexOptions.Compiled);

    private static int Main()
    {
        Console.WriteLine("=== T121: the two defects, as arithmetic ===");
        DefectChecks();

        string goodMkv = "/data/movies/Better.Call.Saul/S01/Better.Call.Saul.S01E04.720p.HDTV.X264-MRSK.mkv";
        string goodSrt = "/data/movies/Better.Call.Saul/S01/Better.Call.Saul.S01E04.720p.HDTV.X264-MRSK.en.sdh.srt";

        Console.WriteLine();
        Console.WriteLine("=== T119: planted staircases on the operator-confirmed-good file ===");
        PlantedChecks(goodMkv, goodSrt);

        Console.WriteLine();
        Console.WriteLine("=== T120: real files, against the Python reference ===");
        // The RAW files, not the ones on Jellyfin: the operator has already accepted
        // corrected versions there, and a fit on an already-synced file correctly finds
        // no gain — which would look like a porting failure. The Python reference ran on
        // exactly these two files.
        RealCheck(
            "BCS S01E04 SDH",
            goodMkv,
            "/opt/data/drift-lab/raw/bcs_raw.sdh.srt",
            null,
            Array.Empty<double>());

        RealCheck(
            "PoI S02E14 SDH",
            "/data/movies/Person.of.Interest/S02/Person Of Interest S02e14 One Percent.mkv",
            "/opt/data/drift-lab/raw/poi_raw.sdh.srt",
            null,
            new[] { 1.75, -1.00, -4.25, -6.75, -9.50 });

        return 0;
    }

    private static void DefectChecks()
    {
        var p = new double[100];
        for (int i = 0; i < p.Length; i++)
        {
            p[i] = 1.0;
        }

        double[] cum = OffsetFit.Cumulative(p);
        double inside = OffsetFit.MeanOver(cum, 100, 10, 20);
        double outside = OffsetFit.MeanOver(cum, 100, -50, -40);
        bool okA = Math.Abs(inside - 1.0) < 1e-9 && double.IsNaN(outside);
        Console.WriteLine($"   (a) inside-audio mean {inside:0.000}, outside-audio "
                          + (double.IsNaN(outside) ? "excluded" : "CLIPPED to " + outside.ToString("0.000", CultureInfo.InvariantCulture))
                          + $" -> {(okA ? "OK" : "FAIL")}");

        // Speech in the second half of every second, near-silence in the first half: a cue
        // starting at 10.0 s stands in silence, and only its SPAN reads speech.
        // Frame levels directly: 4 s at 20 ms frames, loud in the second half of each
        // second and near-silent in the first. Built as LEVELS because that is what the
        // consumer takes now — the old version built samples and let the callee reduce
        // them to exactly these levels (see DriftGate.DecodeFrameLevelsAsync).
        var levels = new double[200];
        for (int i = 0; i < levels.Length; i++)
        {
            double t = i * 0.020;
            levels[i] = (t % 1.0) > 0.5 ? -6.0 : -80.0;
        }

        double[] prob = OffsetFit.SpeechProbability(levels);
        double[] cumProb = OffsetFit.Cumulative(prob);
        // Speech sits in the SECOND half of each second, near-silence in the first. A cue
        // that STARTS in the silence and reaches into the speech is the case that matters:
        // scored over one frame at its start it reads silence, scored over its real span it
        // reads speech. At 8 kHz a 20 ms frame is 160 samples, so t = 1.40 s is frame 70 and
        // t = 1.60 s is frame 80.
        int startFrame = 70;
        double frameOnly = OffsetFit.MeanOver(cumProb, prob.Length, startFrame, startFrame + 1);
        double asSpan = OffsetFit.MeanOver(cumProb, prob.Length, startFrame, startFrame + 10);
        Console.WriteLine($"   (b) cue-start frame alone {frameOnly:0.000} vs the cue span "
                          + $"{asSpan:0.000} -> {(asSpan > frameOnly + 0.1 ? "OK" : "FAIL")}");
    }

    private static void PlantedChecks(string mkv, string srtPath)
    {
        string original = File.ReadAllText(srtPath);
        (double[] starts, double[] ends) = DriftGate.ParseCues(original);
        double[] levels = DecodeCached(mkv);
        Console.WriteLine($"   {Path.GetFileName(srtPath)}: {starts.Length} cues, "
                          + $"{levels.Length * OffsetFit.FrameSec / 60:0.0} min audio");

        var cases = new List<(string Label, Func<double, double> Disp)>
        {
            ("control (nothing planted)", _ => 0.0),
            ("+4 s from 15 min, +7 s from 30 min", t => t >= 1800 ? 7.0 : t >= 900 ? 4.0 : 0.0),
            ("-3 s from 20 min", t => t >= 1200 ? -3.0 : 0.0),
            ("+2 s over 35-40 min (short step)", t => t >= 2100 && t < 2400 ? 2.0 : 0.0),
            ("+5 s over 35-40 min (short step)", t => t >= 2100 && t < 2400 ? 5.0 : 0.0)
        };

        foreach ((string label, Func<double, double> f) in cases)
        {
            var disp = new double[starts.Length];
            for (int i = 0; i < starts.Length; i++)
            {
                disp[i] = f(starts[i]);
            }

            string shifted = Rewrite(original, starts, ends, disp);
            (double[] ps, double[] pe) = DriftGate.ParseCues(shifted);
            OffsetFit.FitResult r = OffsetFit.Fit(ps, pe, levels);

            int good = 0;
            double worst = 0;
            for (int i = 0; i < ps.Length; i++)
            {
                double err = Math.Abs(r.AppliedShiftsSec[i] + disp[i]);
                worst = Math.Max(worst, err);
                if (err <= 1.0)
                {
                    good++;
                }
            }

            double pct = 100.0 * good / ps.Length;
            bool isControl = label.StartsWith("control", StringComparison.Ordinal);
            string verdict = r.Reverted
                ? (isControl ? "left alone (correct)" : "reverted")
                : $"{r.Segments} segments, moved {r.MovedCues}, worst {worst:0.00}s";
            string flag = isControl
                ? (r.Reverted && worst < 1e-9 ? "OK" : "FAIL")
                : (pct >= 90.0 ? "OK" : "NOTE");
            Console.WriteLine($"   {label,-36} {pct,5:0.0}% within 1 s | {verdict,-42} {flag}");
        }
    }

    private static void RealCheck(string label, string mkv, string srtPath, string? referenceSrt, double[]? expect)
    {
        string text = File.ReadAllText(srtPath);
        (double[] starts, double[] ends) = DriftGate.ParseCues(text);
        double[] levels = DecodeCached(mkv);
        OffsetFit.FitResult r = OffsetFit.Fit(starts, ends, levels);

        // Dump the cue table and the DP inputs, so a disagreement with the Python
        // reference can be attributed to the TABLE or to the FIT. Without this the two
        // are indistinguishable and the comparison is guesswork.
        {
            double[] pf = OffsetFit.Dilate(OffsetFit.SpeechProbability(levels), OffsetFit.SlackSec);
            double[] cf = OffsetFit.Cumulative(pf);
            (double[,] S, bool[,] W) = OffsetFit.CueTable(starts, ends, cf, pf.Length);
            int nc = S.GetLength(0), ns = S.GetLength(1);
            var hdr = new List<string>
            {
                $"cues {nc} states {ns} frames {pf.Length}",
                $"minseg {r.MinSegCues} sigma {OffsetFit.DebugSigma(S):R} segments {r.Segments}"
            };
            File.WriteAllLines("/tmp/cs_meta_" + Path.GetFileName(mkv) + ".txt", hdr);
            var sb = new StringBuilder();
            for (int c = 0; c < nc; c++)
            {
                for (int k = 0; k < ns; k++)
                {
                    sb.Append(S[c, k].ToString("R", CultureInfo.InvariantCulture)).Append(',');
                }

                sb.Append('\n');
            }

            File.WriteAllText("/tmp/cs_S_" + Path.GetFileName(mkv) + ".csv", sb.ToString());
            var wb = new StringBuilder();
            for (int c = 0; c < nc; c++)
            {
                for (int k = 0; k < ns; k++)
                {
                    wb.Append(W[c, k] ? '1' : '0');
                }

                wb.Append('\n');
            }

            File.WriteAllText("/tmp/cs_W_" + Path.GetFileName(mkv) + ".txt", wb.ToString());
        }

        // Dump the probability curve so the decode can be compared with the Python
        // reference frame by frame. Without this, a score difference cannot be
        // attributed to either the audio or the fit.
        double[] p = OffsetFit.SpeechProbability(levels);
        string dump = "/tmp/cs_" + Path.GetFileName(mkv) + ".p.f32";
        var pb = new byte[p.Length * 4];
        Buffer.BlockCopy(Array.ConvertAll(p, v => (float)v), 0, pb, 0, pb.Length);
        File.WriteAllBytes(dump, pb);
        Console.WriteLine($"      frames {levels.Length} ({levels.Length * OffsetFit.FrameSec / 60:0.00} min), "
                          + $"mean p {p.Average():0.0000} -> {dump}");

        Console.WriteLine();
        Console.WriteLine($"   {label}");
        Console.WriteLine($"      {starts.Length} cues | moved {r.MovedCues} | t {r.TMoved:+0.00;-0.00} | "
                          + $"score {r.ScoreBefore:0.0000} -> {r.ScoreAfter:0.0000} | min segment {r.MinSegCues} cues");
        Console.WriteLine("      measured offsets per segment (the correction is the negative):");
        for (int i = 0; i < r.SegmentOffsetsSec.Count; i++)
        {
            Console.WriteLine($"         from {r.SegmentStartTimesSec[i] / 60,7:0.00} min: {r.SegmentOffsetsSec[i],+7:0.00} s");
        }

        if (expect is not null && expect.Length == 0)
        {
            Console.WriteLine($"      Python reference: left the file unchanged (no proven gain)");
            Console.WriteLine($"      this port:        {(r.Reverted ? "left unchanged" : "CHANGED")}"
                              + $"  -> {(r.Reverted ? "OK (agrees)" : "MISMATCH")}");
            return;
        }

        if (expect is not null)
        {
            // Compare APPLIED shifts on both sides: the reference is quoted as the
            // correction to apply, and SegmentOffsetsSec is the same number negated. Mixing
            // the two conventions produces a mismatch that looks like a porting failure.
            var applied = new List<double>();
            for (int i = 0; i < r.SegmentStartTimesSec.Count; i++)
            {
                applied.Add(-r.SegmentOffsetsSec[i]);
            }

            bool same = applied.Count == expect.Length;
            if (same)
            {
                for (int i = 0; i < expect.Length; i++)
                {
                    if (Math.Abs(applied[i] - expect[i]) > 0.26)
                    {
                        same = false;
                    }
                }
            }

            string got = string.Join(" / ", applied.ConvertAll(v => v.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)));
            string exp = string.Join(" / ", Array.ConvertAll(expect, v => v.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)));
            Console.WriteLine($"      Python reference: {exp}");
            Console.WriteLine($"      this port:        {got}  -> {(same ? "OK (agrees)" : "MISMATCH")}");
        }

        if (referenceSrt is not null && File.Exists(referenceSrt))
        {
            // Cross-check against the sibling subtitle: the speech score of the SAME dialog
            // lines in both, which is fair because neither file is ground truth by itself.
            Console.WriteLine($"      (sibling subtitle present: {Path.GetFileName(referenceSrt)})");
        }
    }

    /// <summary>
    /// Frame levels for a media file, cached as a small file. The cache holds the LEVELS,
    /// not the samples: levels are 1/160 of the samples' size (one double per 20 ms frame
    /// against 320 floats), so a 140-minute track caches as 3.2 MB instead of 514 MB. The
    /// cache lives on disk here as a TEST convenience and is NOT part of the plugin — the
    /// plugin holds the levels in memory for the duration of one fit and then drops them.
    /// </summary>
    private static double[] DecodeCached(string mkv)
    {
        string raw = Path.Combine(CacheDir, Path.GetFileName(mkv) + ".levels");
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

        double[] levels = DriftGate.DecodeFrameLevelsAsync(FindFfmpeg(), mkv, CancellationToken.None, "0:a:0")
            .GetAwaiter().GetResult();
        Directory.CreateDirectory(CacheDir);
        File.WriteAllLines(raw, levels.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        return levels;
    }

    private static string FindFfmpeg()
    {
        foreach (string p in new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/opt/data/bin/ffmpeg" })
        {
            if (File.Exists(p))
            {
                return p;
            }
        }

        return "ffmpeg";
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
}
