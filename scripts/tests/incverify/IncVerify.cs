// F-M340: is the .01 on disk byte-identical to what the INCUMBENT fit produces from the .99?
// If yes, no rewrite is needed and the operator's already-listened files stay untouched.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.SubdlScribe.Qa;

internal static class IncVerify
{
    private const string CacheDir = "/opt/data/drift-lab/cache-levels";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static int Main(string[] args)
    {
        // "apply" writes the .01 where it differs. The ORIGINAL is never lost: slot 99 holds the
        // pre-correction bytes, which is what the reserved range exists for. Without the flag the
        // program only reports, so a check can always be run without touching the share.
        bool apply = args.Length > 1 && args[1] == "apply";
        var jobs = JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(args[0]), JsonOpts);
        int same = 0, differ = 0, missing = 0;
        foreach (Job j in jobs)
        {
            string p01 = j.Srt[..^".99.srt".Length] + ".01.srt";
            if (!File.Exists(p01)) { missing++; Console.WriteLine($"  FEHLT 01: {Path.GetFileName(p01)}"); continue; }

            string text99 = File.ReadAllText(j.Srt, Encoding.UTF8);
            (double[] starts, double[] ends) = DriftGate.ParseCues(text99);
            string media = Path.IsPathRooted(j.Media) ? j.Media : Path.Combine(j.Dir, j.Media);
            double[] levels = DecodeCached(media);
            OffsetFit.FitResult r = OffsetFit.Fit(starts, ends, levels);

            string expect;
            if (r.Reverted)
            {
                // No proven gain: the corrected slot carries the SAME bytes as the original.
                expect = text99;
            }
            else
            {
                (bool ok, string shifted, string _) = SubtitleSync.ShiftByStaircasePerCue(text99, r.AppliedShiftsSec);
                expect = ok ? shifted : text99;
            }

            string onDisk = File.ReadAllText(p01, Encoding.UTF8);
            bool eq = expect == onDisk;
            if (eq)
            {
                same++;
            }
            else
            {
                differ++;
                if (apply)
                {
                    File.WriteAllText(p01, expect, new UTF8Encoding(false));
                }
            }
            Console.WriteLine($"  {(eq ? "gleich " : (apply ? "GESCHRIEBEN" : "ABWEICHT"))} {Path.GetFileName(p01),-62} rev={r.Reverted,-5} segs={r.Segments}");
        }

        Console.WriteLine($"\ngleich: {same}  abweichend: {differ}  fehlend: {missing}");
        return 0;
    }

    private static double[] DecodeCached(string media)
    {
        string raw = Path.Combine(CacheDir, Path.GetFileName(media) + ".levels");
        if (File.Exists(raw))
        {
            string[] lines = File.ReadAllLines(raw);
            var c = new double[lines.Length];
            for (int i = 0; i < lines.Length; i++) c[i] = double.Parse(lines[i], CultureInfo.InvariantCulture);
            return c;
        }
        double[] lv = DriftGate.DecodeFrameLevelsAsync("/usr/bin/ffmpeg", media, CancellationToken.None, "0:a:0").GetAwaiter().GetResult();
        Directory.CreateDirectory(CacheDir);
        File.WriteAllLines(raw, lv.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
        return lv;
    }

    private sealed class Job
    {
        public string Srt { get; set; } = string.Empty;
        public string Dir { get; set; } = string.Empty;
        public string Base { get; set; } = string.Empty;
        public string Media { get; set; } = string.Empty;
    }
}
