// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// F-M440: the text this account already uploaded is kept on disk so a pack fill reads it instead of
// extracting every subtitle track of the container again (prod 04.11.2026: 689 files, ~900 MB held).

using Jellyfin.Plugin.SubdlScribe.Api;

namespace OwnTextRun;

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
        string data = Path.Combine(Path.GetTempPath(), "owntext-" + Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(data, "own-texts");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new OwnTextStore(dir);
            string srt = "1\r\n00:00:01,000 --> 00:00:03,000\r\nHallo Welt — äöü\r\n\r\n";

            Check(!store.TryGet("tt1", 1, 1, "DE", false, out _), "F-M440: an empty store misses");
            store.Put("tt1", 1, 1, "DE", false, srt);
            Check(store.TryGet("tt1", 1, 1, "DE", false, out string? back) && back == srt,
                "F-M440: the stored text comes back byte for byte");
            Check(Directory.EnumerateFiles(dir, "*.srt.gz").Any(), "F-M440: the text is stored gzipped on disk");

            // Der Name trägt den Schlüssel — der Aufräumer braucht keinen Index.
            string name = Path.GetFileName(Directory.EnumerateFiles(dir, "*.srt.gz").First());
            Check(name == "tt1_s01e01_de.srt.gz", "F-M440: the file name reads as tt1_s01e01_de.srt.gz, was " + name);
            store.Put("tt1", 2, 12, "pt-BR", true, "x");
            Check(Directory.EnumerateFiles(dir, "tt1_s02e12_pt-br_hi.srt.gz").Any(),
                "F-M440: a language with punctuation and the HI variant stay readable in the name");

            // Getrennte Schlüssel: Folge, Sprache und Variante dürfen sich nicht teilen.
            Check(!store.TryGet("tt1", 1, 2, "DE", false, out _), "F-M440: another episode is another key");
            Check(!store.TryGet("tt1", 1, 1, "EN", false, out _), "F-M440: another language is another key");
            Check(!store.TryGet("tt1", 1, 1, "DE", true, out _), "F-M440: the HI variant is another key");
            Check(!store.TryGet(null, 1, 1, "DE", false, out _), "F-M440: a missing IMDb id is a miss, not a crash");

            store.Put("tt1", 1, 1, "DE", true, "hi-version");
            Check(store.TryGet("tt1", 1, 1, "DE", true, out string? hi) && hi == "hi-version",
                "F-M440: the HI variant lives beside the plain one");
            Check(store.TryGet("tt1", 1, 1, "DE", false, out string? plain) && plain == srt,
                "F-M440: writing the HI variant does not overwrite the plain one");

            store.Put("tt1", 1, 1, "DE", false, "second");
            Check(store.TryGet("tt1", 1, 1, "DE", false, out string? again) && again == "second",
                "F-M440: a second upload of the same slot replaces the text");

            // Aufräumen (a): der Eintrag ist weg, also der Text auch.
            Check(store.Remove("tt1", 1, 1, "DE", false), "F-M440: Remove drops the text of a deleted entry");
            Check(!store.TryGet("tt1", 1, 1, "DE", false, out _), "F-M440: the removed text is gone");
            Check(!store.Remove("tt1", 1, 1, "DE", false), "F-M440: removing twice is a no-op");

            // Aufräumen (b): der tägliche Durchlauf behält nur, was noch gedeckt ist.
            store.Put("tt1", 3, 1, "DE", false, "kept");
            store.Put("tt1", 3, 2, "DE", false, "orphan");
            int dropped = store.Sweep(k => k.Episode is 1 or 12, TimeSpan.FromDays(90));
            Check(dropped == 1, "F-M440: the sweep drops exactly the text without an uploaded entry, was " + dropped);
            Check(store.TryGet("tt1", 3, 1, "DE", false, out string? kept) && kept == "kept",
                "F-M440: the covered text survives the sweep");
            Check(!store.TryGet("tt1", 3, 2, "DE", false, out _), "F-M440: the orphaned text is gone");

            // Und der Altersdeckel: ohne Lesen altert der Text, mit Lesen bleibt er.
            store.Put("tt9", 1, 1, "DE", false, "old");
            string oldPath = Directory.EnumerateFiles(dir, "tt9_s01e01_de.srt.gz").Single();
            File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-200));
            Check(store.Sweep(_ => true, TimeSpan.FromDays(90)) == 1,
                "F-M440: a text unread for longer than the limit goes");
            store.Put("tt9", 1, 2, "DE", false, "fresh");
            Check(store.TryGet("tt9", 1, 2, "DE", false, out _), "F-M440: the fresh text reads back");
            Check(store.Sweep(_ => true, TimeSpan.FromDays(90)) == 0,
                "F-M440: reading a text marks it as still needed, so the sweep leaves it");

            Check(OwnTextStore.PurgeDirectory(data) >= 1, "F-M440: the reset purges the store");
            Check(!store.TryGet("tt1", 3, 1, "DE", false, out _), "F-M440: nothing is readable after the purge");

            Console.WriteLine($"owntext: {_checks} checks, {(_failed == 0 ? "all green" : _failed + " failed")}.");
            return _failed == 0 ? 0 : 1;
        }
        finally
        {
            Directory.Delete(data, true);
        }
    }
}
