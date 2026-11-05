// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// F-M441: the pack fill writes its texts into the run's working directory instead of holding every
// subtitle track of every touched file in memory for the whole run (prod 04.11.2026: 689 files,
// ~21.000 texts, ~900 MB).

using Jellyfin.Plugin.SubdlScribe.Api;

namespace WorkFilesRun;

internal static class Program
{
    private static int _checks;
    private static int _failed;

    private static void Check(bool ok, string text)
    {
        _checks++;
        if (!ok)
        {
            _failed++;
            Console.WriteLine("  [FAIL] " + text);
        }
    }

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "workfiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var run = new RunWorkFiles(root);
            string srt = "1\r\n00:00:01,000 --> 00:00:03,000\r\nHallo Welt — äöü\r\n\r\n";

            Check(!run.TryRead("tt1", 1, 1, "DE", false, 2, out _), "F-M441: an empty run has no text");
            Check(run.Count() == 0, "F-M441: nothing is on disk before the first write");

            run.Write("tt1", 1, 1, "DE", false, 2, srt);
            Check(run.TryRead("tt1", 1, 1, "DE", false, 2, out string? back) && back == srt,
                "F-M441: the written text reads back byte for byte");
            Check(run.Count() == 1, "F-M441: the text is one file in the run directory");

            // Der Name trägt den Schlüssel — im Ordner ist zu sehen, was drin liegt.
            string name = Path.GetFileName(Directory.EnumerateFiles(run.Directory, "*.srt").Single());
            Check(name == "tt1_s01e01_de_p2.srt", "F-M441: the working file reads as tt1_s01e01_de_p2.srt, was " + name);
            run.Write("tt1", 2, 12, "pt-BR", true, 7, "x");
            Check(File.Exists(Path.Combine(run.Directory, "tt1_s02e12_pt-br_hi_p7.srt")),
                "F-M441: a language with punctuation and the HI variant stay readable in the name");

            // Getrennte Schlüssel: Folge, Sprache, Variante und Spur teilen sich nichts.
            Check(!run.TryRead("tt1", 1, 2, "DE", false, 2, out _), "F-M441: another episode is another file");
            Check(!run.TryRead("tt1", 1, 1, "EN", false, 2, out _), "F-M441: another language is another file");
            Check(!run.TryRead("tt1", 1, 1, "DE", true, 2, out _), "F-M441: the HI variant is another file");
            Check(!run.TryRead("tt1", 1, 1, "DE", false, 3, out _), "F-M441: another track is another file");
            Check(!run.TryRead(null, 1, 1, "DE", false, 2, out _), "F-M441: a missing IMDb id is a miss, not a crash");

            run.Write("tt1", 1, 1, "DE", false, 2, "second");
            Check(run.TryRead("tt1", 1, 1, "DE", false, 2, out string? again) && again == "second",
                "F-M441: a second write of the same slot replaces the text");

            // Der Lauf räumt seinen Ordner selbst weg.
            Check(run.Purge() >= 2, "F-M441: the end of the run removes its working files");
            Check(!Directory.Exists(run.Directory), "F-M441: the run directory itself is gone");
            Check(!run.TryRead("tt1", 1, 1, "DE", false, 2, out _), "F-M441: nothing survives the purge");

            // Und ein abgebrochener Lauf wird beim nächsten Start weggeräumt, ein frischer nicht.
            string stale = Path.Combine(root, "run-20260101-000000-deadbeef");
            string fresh = Path.Combine(root, "run-20990101-000000-00000000");
            Directory.CreateDirectory(stale);
            Directory.CreateDirectory(fresh);
            File.WriteAllText(Path.Combine(stale, "x.srt"), "alt");
            Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-5));
            Directory.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);
            int swept = RunWorkFiles.PurgeStale(root, TimeSpan.FromDays(2));
            Check(swept == 1, "F-M441: the start of a run sweeps one left-over directory, was " + swept);
            Check(!Directory.Exists(stale), "F-M441: the left-over directory of the killed run is gone");
            Check(Directory.Exists(fresh), "F-M441: a fresh directory of another run stays");

            Console.WriteLine($"workfiles: {_checks} checks, {(_failed == 0 ? "all green" : _failed + " failed")}.");
            return _failed == 0 ? 0 : 1;
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // Temp folder; the OS cleans up.
            }
        }
    }
}
