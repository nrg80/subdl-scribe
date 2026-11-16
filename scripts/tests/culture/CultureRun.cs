// F-M341: does the log line keep a DECIMAL POINT when the host culture wants a comma?
//
// The defect was culture-dependent, so a check under the machine's default culture proves
// nothing — it passes on any English host. This machine has no ICU (dotnet only runs with
// DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1), so a real de-DE culture cannot be constructed here.
// A NumberFormatInfo CAN be built without ICU and carries the one property that matters —
// the decimal separator — so the mechanism is exercised exactly: the same number, formatted
// the interpolated way and the invariant way, under a comma culture.
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Jellyfin.Plugin.SubdlScribe.Qa;

internal static class CultureRun
{
    private const string CacheDir = "/opt/data/drift-lab/cache-levels";

    private static int Main()
    {
        // A culture that wants a comma, exactly like the German host that produced "t = +8,62".
        var commaCulture = new NumberFormatInfo { NumberDecimalSeparator = ",", NumberGroupSeparator = "." };
        Console.WriteLine($"comma culture: decimal separator '{commaCulture.NumberDecimalSeparator}'");

        double t = 8.62, scoreBefore = 0.4622, scoreAfter = 0.5172;
        int moved = 114, guarded = 3;

        // The DEFECT: an interpolated double with no provider follows the current culture.
        string legacy = string.Format(commaCulture,
            "corrected 3 segments, {0} cue(s) moved, t = {1:+0.00;-0.00}, score {2:0.0000} -> {3:0.0000}, {4} order-guarded",
            moved, t, scoreBefore, scoreAfter, guarded);

        // The FIX: every number is formatted with InvariantCulture.
        string fixedLine = "corrected " + 3.ToString(CultureInfo.InvariantCulture) + " segments, "
                           + moved.ToString(CultureInfo.InvariantCulture) + " cue(s) moved, t = "
                           + t.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + ", "
                           + "score " + scoreBefore.ToString("0.0000", CultureInfo.InvariantCulture) + " -> "
                           + scoreAfter.ToString("0.0000", CultureInfo.InvariantCulture) + ", "
                           + guarded.ToString(CultureInfo.InvariantCulture) + " order-guarded";

        Console.WriteLine();
        Console.WriteLine("interpolated (the defect): " + legacy);
        Console.WriteLine("invariant (the fix):       " + fixedLine);
        Console.WriteLine();
        bool defectShows = legacy.Contains("t = +8,62");
        bool fixedKeepsDot = Regex.IsMatch(fixedLine, @"t = \+8\.62") && Regex.IsMatch(fixedLine, @"score 0\.\d{4} -> 0\.\d{4}");
        Console.WriteLine($"  comma appears without the provider: {(defectShows ? "YES" : "no")}");
        Console.WriteLine($"  invariant form stays dot-only:      {(fixedKeepsDot ? "YES" : "NO")}");

        // The REAL path: a fit on a real file, with the message built exactly as the plugin builds it.
        string srt = "/data/movies/Better.Call.Saul/S02/Better.Call.Saul.S02E04.720p.HDTV.X264-MRSK.en.99.srt";
        if (!File.Exists(srt))
        {
            Console.WriteLine("\n(real-file check skipped: subtitle not found)");
            return defectShows && fixedKeepsDot ? 0 : 1;
        }

        string media = srt[..srt.IndexOf(".en.99.srt", StringComparison.Ordinal)] + ".mkv";
        string text = File.ReadAllText(srt, Encoding.UTF8);
        (double[] starts, double[] ends) = DriftGate.ParseCues(text);
        double[] levels = DecodeCached(media);
        OffsetFit.FitResult fit = OffsetFit.Fit(starts, ends, levels);

        string how = fit.Segments == 1
            ? "constant " + fit.AppliedShiftsSec[0].ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + "s"
            : fit.Segments.ToString(CultureInfo.InvariantCulture) + " segments ("
              + string.Join(" / ", fit.SegmentOffsetsSec.ConvertAll(v => (-v).ToString("+0.00;-0.00", CultureInfo.InvariantCulture))) + ")";
        string real = "corrected " + how + ", " + fit.MovedCues.ToString(CultureInfo.InvariantCulture) + " cue(s) moved, t = "
                      + fit.TMoved.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + ", "
                      + "score " + fit.ScoreBefore.ToString("0.0000", CultureInfo.InvariantCulture) + " -> "
                      + fit.ScoreAfter.ToString("0.0000", CultureInfo.InvariantCulture) + ", "
                      + fit.GuardedCues.ToString(CultureInfo.InvariantCulture) + " order-guarded";

        Console.WriteLine();
        Console.WriteLine("REAL fit message:");
        Console.WriteLine("  " + real);
        // The message contains ordinary sentence commas, so "no comma at all" would be the wrong
        // test. What must not exist is a comma BETWEEN DIGITS — a decimal comma in a number.
        bool numericComma = Regex.IsMatch(real, @"\d,\d");
        bool ok = Regex.IsMatch(real, @"t = [+-]\d+\.\d\d") && Regex.IsMatch(real, @"score \d+\.\d{4} -> \d+\.\d{4}")
                  && !numericComma;
        Console.WriteLine($"  parsable with a dot:                     {(ok ? "YES" : "NO")}"
                          + $"  (decimal comma between digits: {(numericComma ? "PRESENT" : "none")})");
        return defectShows && fixedKeepsDot && ok ? 0 : 1;
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
}
