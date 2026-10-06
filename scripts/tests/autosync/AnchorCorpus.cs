// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// F-M297 verification driver: the C# anchor correction over the whole real corpus.
//
// WHY THIS EXISTS
//
// The port is only worth anything if it reproduces the numbers the Python prototype
// produced on the same files. The prototype is the one that has been measured: 36
// drifting episodes, worst single-cue residual 13.35 s -> 2.13 s on the reference set,
// and the refusal rule flags exactly the 2 files the audio path made worse. A port that
// silently drifts from that is a rewrite, not a port, and nothing in the unit tests would
// notice — hence a driver over the real library.
//
// WHAT IT RUNS
//
// For each episode that has a .2 slot (the text-anchor result the user confirmed by ear):
//   1. read the ORIGINAL .en.sdh.srt — never the .2, which is already repaired
//   2. read the .2 as the measurement reference for the QS line
//   3. anchor-correct the original against the same-language plain .en.srt
//   4. report before/after and whether the correction was accepted or refused
//
// The QS reference and the correction reference are deliberately different questions: the
// correction is anchored to the plain sibling (that is the method), while the QS asks
// whether the result lands where the ear-confirmed .2 sits.
//
// Run: dotnet run --project scripts/tests/autosync -c Release -- corpus
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Qa;

namespace AutoSyncTest;

internal static class AnchorCorpus
{
    internal static int Run(string[] args)
    {
        const string root = "/data/movies/Person.of.Interest";
        var files = new List<(string Dir, string Base)>();
        foreach (string season in Directory.GetDirectories(root).OrderBy(x => x))
        {
            foreach (string f in Directory.GetFiles(season).OrderBy(x => x))
            {
                if (!f.EndsWith(".en.sdh.2.srt", StringComparison.Ordinal))
                {
                    continue;
                }

                string b = Path.GetFileName(f)[..^".en.sdh.2.srt".Length];
                if (File.Exists(Path.Combine(season, b + ".en.sdh.srt")))
                {
                    files.Add((season, b));
                }
            }
        }

        Console.WriteLine($"=== F-M297 anchor correction over the real corpus ({files.Count} episodes) ===");
        var rows = new List<(string Name, double W0, double W1, double M0, double M1, bool Applied, string Reason)>();

        foreach ((string dir, string b) in files)
        {
            try
            {
                var plain = AnchorSync.Parse(File.ReadAllText(Path.Combine(dir, b + ".en.srt")));
                var target = AnchorSync.Parse(File.ReadAllText(Path.Combine(dir, b + ".en.sdh.srt")));
                var slot2 = AnchorSync.Parse(File.ReadAllText(Path.Combine(dir, b + ".en.sdh.2.srt")));

                AnchorSync.Residuals qsBefore = AnchorSync.Measure(slot2, target);
                AnchorSync.Result res = AnchorSync.Correct(plain, target);
                AnchorSync.Residuals qsAfter = res.Applied
                    ? AnchorSync.Measure(slot2, res.Cues)
                    : qsBefore;

                rows.Add((b, qsBefore.WorstSec, qsAfter.WorstSec,
                    qsBefore.MedianSec, qsAfter.MedianSec, res.Applied, res.Reason));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {b,-52} ERROR {ex.GetType().Name}: {ex.Message[..Math.Min(50, ex.Message.Length)]}");
            }
        }

        Console.WriteLine();
        foreach (var r in rows.OrderByDescending(x => x.W1 - x.W0))
        {
            string mark = r.W1 > r.W0 + 0.2 ? "WORSE" : (r.W1 < r.W0 - 0.2 ? "better" : "same");
            string name = r.Name.Length > 50 ? r.Name[^50..] : r.Name;
            Console.WriteLine($"  {name,-50} QS worst {r.W0,6:0.00}->{r.W1,6:0.00}s "
                              + $"median {r.M0,5:0.00}->{r.M1,5:0.00}s {mark,-7} "
                              + $"{(r.Applied ? "applied" : "refused")}");
        }

        int better = rows.Count(r => r.W1 < r.W0 - 0.2);
        int worse = rows.Count(r => r.W1 > r.W0 + 0.2);
        int applied = rows.Count(r => r.Applied);
        Console.WriteLine($"\n  {rows.Count} episodes: {better} better, {worse} worse, "
                          + $"{rows.Count - better - worse} unchanged | correction applied on {applied}");
        if (rows.Count > 0)
        {
            Console.WriteLine($"  QS worst median: {Med(rows.Select(r => r.W0)):0.00}s -> {Med(rows.Select(r => r.W1)):0.00}s");
            Console.WriteLine($"  QS median median: {Med(rows.Select(r => r.M0)):0.00}s -> {Med(rows.Select(r => r.M1)):0.00}s");
        }

        return 0;
    }

    private static double Med(IEnumerable<double> xs)
    {
        double[] a = xs.OrderBy(x => x).ToArray();
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[(a.Length / 2) - 1] + a[a.Length / 2]) / 2.0;
    }
}
