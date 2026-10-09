// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// F-M340: runs BOTH fits — the incumbent piecewise-constant one and der Polygonzug — over the
// operator's files and writes the Polygonzug's answer as the next corrected slot (.02), so the two
// can be compared by ear in Jellyfin.
//
// WHY BOTH ON THE SAME FILE
//
// The question is not "does der Polygonzug run" but "does it beat the staircase on the files the
// operator has already judged". Running both here, on the same audio and the same cues, is what
// makes the two answers comparable: any difference is then the MODEL, not the material.
//
// The incumbent's answer is NOT written. It is already on disk as .01 from prod, and a second
// write of it would either collide or silently replace a file the operator has listened to.
//
// USAGE
//   stuetzrun <jobs.json> <out.jsonl> [limit]
// Appends one JSON line per file, so a run that dies on file 20 keeps the first 19.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Jellyfin.Plugin.SubdlScribe.Qa;

internal static class StuetzRun
{
    private const string CacheDir = "/opt/data/drift-lab/cache-levels";
    private static readonly Regex TsRe = new(@"\d{2}:\d{2}:\d{2},\d{3}", RegexOptions.Compiled);

    // Cached, not built per call: the analyzer rejects a fresh options object per read, and the
    // same instance is what the whole run wants anyway.
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The operator's own listening verdicts (09.10.2026), keyed by episode label. These are the
    /// ground truth the two models are weighed against — a model that flags a file he called GOOD
    /// is wrong no matter how it scores.
    /// </summary>
    private static readonly Dictionary<string, string> Verdicts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["S04E06.de"] = "gut",
        ["S04E02.de"] = "gut",
        ["S05E10.en"] = "gut",
        ["S02E06.en"] = "gut",
        ["S02E02.en"] = "gut",
        ["S02E10.en"] = "gut",
        ["S02E01.en"] = "SCHLECHT",
        ["S02E04.en"] = "SCHLECHT",
    };

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: stuetzrun <jobs.json> <out.jsonl> [limit]");
            return 2;
        }

        string jobsPath = args[0];
        string outPath = args[1];
        int limit = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : int.MaxValue;

        var jobs = JsonSerializer.Deserialize<List<Job>>(
            File.ReadAllText(jobsPath),
            JsonOpts);

        if (jobs == null || jobs.Count == 0)
        {
            Console.Error.WriteLine("no jobs");
            return 2;
        }

        var done = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(outPath))
        {
            foreach (string line in File.ReadLines(outPath))
            {
                try
                {
                    var prev = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
                    if (prev != null && prev.TryGetValue("srt", out JsonElement e))
                    {
                        done.Add(e.GetString() ?? string.Empty);
                    }
                }
                catch (JsonException)
                {
                    // A half-written last line from an interrupted run is skipped, not fatal.
                }
            }
        }

        int n = 0;
        foreach (Job j in jobs)
        {
            if (done.Contains(j.Srt))
            {
                continue;
            }

            if (n >= limit)
            {
                break;
            }

            n++;
            var row = new Dictionary<string, object>();
            try
            {
                RunOne(j, row);
            }
            catch (Exception ex)
            {
                row["srt"] = j.Srt;
                row["error"] = ex.GetType().Name + ": " + ex.Message;
            }

            File.AppendAllText(outPath, JsonSerializer.Serialize(row) + "\n");
            Console.WriteLine($"[{n}] {Path.GetFileName(j.Srt)} -> {row.GetValueOrDefault("write", "?")}");
        }

        Console.WriteLine($"done: {n} file(s) processed");
        return 0;
    }

    private static void RunOne(Job j, Dictionary<string, object> row)
    {
        row["srt"] = j.Srt;
        row["media"] = j.Media;

        string label = Label(j);
        row["label"] = label;
        row["verdict"] = Verdicts.TryGetValue(label, out string v) ? v : "unbekannt";

        string text = File.ReadAllText(j.Srt, Encoding.UTF8);
        (double[] starts, double[] ends) = DriftGate.ParseCues(text);
        row["cues"] = starts.Length;
        if (starts.Length < 8)
        {
            row["error"] = "too few cues";
            return;
        }

        // The job's "media" is a FILE NAME; the directory comes from the subtitle's own path.
        // Passing the bare name made ffmpeg fail and the decode return nothing, which the fits
        // then reported as "audio too short" — a silent-looking failure that was really a
        // wrong path. Resolved here so the field cannot be misused again.
        string media = Path.IsPathRooted(j.Media) ? j.Media : Path.Combine(j.Dir, j.Media);
        row["media_path"] = media;
        row["media_exists"] = File.Exists(media);
        double[] levels = DecodeCached(media);
        row["frames"] = levels.Length;
        row["minutes"] = Math.Round(levels.Length * OffsetFit.FrameSec / 60, 1);

        // (a) The incumbent, measured but not written.
        OffsetFit.FitResult inc = OffsetFit.Fit(starts, ends, levels);
        row["inc_reverted"] = inc.Reverted;
        row["inc_segments"] = inc.Segments;
        row["inc_offsets"] = inc.SegmentOffsetsSec.Select(x => Math.Round(x, 2)).ToList();
        row["inc_t"] = Math.Round(inc.TMoved, 2);
        row["inc_score"] = Math.Round(inc.ScoreAfter, 4);
        row["inc_maxshift"] = inc.AppliedShiftsSec.Length == 0 ? 0 : Math.Round(inc.AppliedShiftsSec.Max(Math.Abs), 2);

        // (b) The stuetz.
        StuetzstellenFit.StuetzstellenResult stuetz = StuetzstellenFit.Fit(starts, ends, levels);
        row["st_ran"] = stuetz.Ran;
        row["st_reverted"] = stuetz.Reverted;
        row["st_knots"] = stuetz.Knots;
        row["st_pieces"] = stuetz.Pieces;
        row["st_t"] = Math.Round(stuetz.TMoved, 2);
        row["st_score"] = Math.Round(stuetz.ScoreAfter, 4);
        row["st_moved"] = stuetz.MovedCues;
        row["st_guarded"] = stuetz.GuardedCues;
        row["st_maxshift"] = stuetz.AppliedShiftsSec.Length == 0 ? 0 : Math.Round(stuetz.AppliedShiftsSec.Max(Math.Abs), 2);
        row["st_knotvalues"] = stuetz.KnotValuesSec.Select(x => Math.Round(x, 2)).ToList();
        row["st_obj"] = double.IsFinite(stuetz.Objective) ? Math.Round(stuetz.Objective, 2) : stuetz.Objective;
        row["st_objzero"] = Math.Round(stuetz.ZeroObjective, 2);

        if (!stuetz.Ran)
        {
            row["write"] = "nicht-gelaufen: " + stuetz.Reason;
            return;
        }

        // The .02 is the corrected file. A Polygonzug that refused writes nothing: an unchanged copy
        // under a second slot would only add a menu entry that is byte-identical to the .99.
        if (stuetz.Reverted)
        {
            row["write"] = "uebersprungen (kein belegter Gewinn)";
            return;
        }

        // The name is derived from the SUBTITLE's own path, not from the media base name: the
        // language and the .sdh/.forced markers live in the subtitle name, and a file written as
        // "<media>.02.srt" carries NO language at all — SidecarNaming.Parse reads that as "no
        // language", so Jellyfin would not offer it as a German/English track and the comparison
        // by ear would be impossible. Measured here: the media-base form dropped ".en" and ".de".
        string outPath = j.Srt[..^".99.srt".Length] + ".02.srt";
        if (File.Exists(outPath))
        {
            // Never overwrite: an existing .02 may be a file the operator is listening to.
            row["write"] = "EXISTIERT-SCHON, nichts geschrieben";
            return;
        }

        (bool ok, string shifted, string reason) = SubtitleSync.ShiftByStaircasePerCue(text, stuetz.AppliedShiftsSec);
        if (!ok)
        {
            row["write"] = "Schreiben fehlgeschlagen: " + reason;
            return;
        }

        File.WriteAllText(outPath, shifted, new UTF8Encoding(false));
        row["write"] = Path.GetFileName(outPath);
        row["write_bytes"] = new FileInfo(outPath).Length;
    }

    /// <summary>Episode plus language, the key the operator's verdicts are filed under.</summary>
    private static string Label(Job j)
    {
        Match ep = Regex.Match(j.Base, @"S(\d{2})E(\d{2})");
        if (!ep.Success)
        {
            return Path.GetFileNameWithoutExtension(j.Srt);
        }

        // The language is the token BEFORE the slot and the optional marker, not simply the
        // second-to-last one: "<base>.en.sdh.99.srt" would otherwise file as "99" and every
        // verdict lookup would miss — which reads as "no verdict on record" rather than as a
        // bug, and would have silently emptied the comparison.
        string[] parts = Path.GetFileNameWithoutExtension(j.Srt).Split('.');
        int i = parts.Length - 1;
        if (i >= 0 && parts[i].All(char.IsDigit))
        {
            i--;
        }

        while (i > 0 && (parts[i].Equals("sdh", StringComparison.OrdinalIgnoreCase)
                         || parts[i].Equals("hi", StringComparison.OrdinalIgnoreCase)
                         || parts[i].Equals("forced", StringComparison.OrdinalIgnoreCase)))
        {
            i--;
        }

        string lang = i > 0 ? parts[i].ToLowerInvariant() : "??";
        return $"S{ep.Groups[1].Value}E{ep.Groups[2].Value}.{lang}";
    }

    /// <summary>Frame levels for a media file, cached as text — see the fit harness.</summary>
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

        double[] levels = DriftGate.DecodeFrameLevelsAsync(FindFfmpeg(), media, CancellationToken.None, "0:a:0")
            .GetAwaiter().GetResult();
        Directory.CreateDirectory(CacheDir);
        File.WriteAllLines(raw, levels.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
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

    private sealed class Job
    {
        public string Srt { get; set; } = string.Empty;
        public string Dir { get; set; } = string.Empty;
        public string Base { get; set; } = string.Empty;
        public string Media { get; set; } = string.Empty;
    }
}
