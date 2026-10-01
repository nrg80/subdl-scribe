// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.SubdlScribe.Configuration;
using Jellyfin.Plugin.SubdlScribe.Data;
using Jellyfin.Plugin.SubdlScribe.Pipeline;
using Jellyfin.Plugin.SubdlScribe.Registry;

// F-M262: welche Zeile erscheint bei welchem LogLevelMode?
internal static class LogRun
{
    // Sammelt jede Zeile mit ihrem Textmarker — genau wie Jellyfin sie schreiben wuerde.
    internal sealed class Cap : Microsoft.Extensions.Logging.ILogger
    {
        public readonly List<(Microsoft.Extensions.Logging.LogLevel Lvl, string Msg)> Lines = new();
        public IDisposable? BeginScope<TState>(TState s) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel l) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel l, Microsoft.Extensions.Logging.EventId e, TState s, Exception? x, Func<TState, Exception?, string> f)
            => Lines.Add((l, f(s, x)));
    }

    private static int Main(string[] args)
    {
        string media = args[0], root = args[1], ffmpeg = args[2];
        // F-M263: die Dry-Run-Faelle mitmessen. Erwartung: erkannt wird, GESCHRIEBEN wird nicht.
        var cases = new List<(string Name, LogLevelMode Mode, bool UpDry, bool DownDry, bool ExpectWrite)>
        {
            ("Normal  (kein Dry-Run)", LogLevelMode.Normal,  false, false, true),
            ("Verbose (kein Dry-Run)", LogLevelMode.Verbose, false, false, true),
            ("Debug   (kein Dry-Run)", LogLevelMode.Debug,   false, false, true),
            ("Normal  + DRYRUN UPLOAD", LogLevelMode.Normal,  true,  false, false),
            ("Normal  + DRYRUN DOWNLOAD", LogLevelMode.Normal, false, true,  false),
            ("Verbose + DRYRUN BEIDE", LogLevelMode.Verbose,  true,  true,  false),
        };

        int fails = 0;
        foreach (var (name, mode, upDry, downDry, expectWrite) in cases)
        {
            string work = Path.Combine(Path.GetDirectoryName(media)!, "log_" + name.Replace(" ", "_").Replace("(", "").Replace(")", "").Replace("+", "p") + ".mkv");
            File.Copy(media, work, true);
            string dbDir = Path.Combine(root, "db_" + name.GetHashCode());
            if (Directory.Exists(dbDir)) Directory.Delete(dbDir, true);
            Directory.CreateDirectory(dbDir);

            using var db = new SubdlDbContext(dbDir, null);
            var reg = new ContentHashRegistry(db, null, null);
            var cfg = new PluginConfiguration
            {
                LogMode = mode,
                AllocateMissingLanguageCodes = true,
                DryRun = upDry,
                DownloadDryRun = downDry
            };
            string h1 = reg.GetMediaHash(work)!;
            reg.EnsureMedia(h1, "aabbccdd11223344aabbccdd11223344", work);
            long sz1 = new FileInfo(work).Length;
            long mt1 = new FileInfo(work).LastWriteTimeUtc.Ticks;

            var cap = new Cap();
            var gate = new LanguageTagGate(cap, cfg, reg);
            var streams = Probe(work);
            var res = gate.RunAsync(work, streams, ffmpeg, CancellationToken.None).GetAwaiter().GetResult();

            string h2 = reg.GetMediaHash(work)!;
            bool wrote = res.Written > 0;
            bool fileChanged = h1 != h2 || sz1 != new FileInfo(work).Length || mt1 != new FileInfo(work).LastWriteTimeUtc.Ticks;
            int normalLines = cap.Lines.Count(l => l.Msg.StartsWith("[N] ", StringComparison.Ordinal));

            Console.WriteLine("===== " + name + "  (erkannt: " + res.Wanted.Count + ", geschrieben: " + res.Written + ") =====");
            foreach (var (lvl, msg) in cap.Lines)
                Console.WriteLine("  [" + Level(lvl) + "] " + msg);
            Console.WriteLine("  -> Datei geaendert? " + (fileChanged ? "JA" : "nein")
                + "   Normal-Zeilen: " + normalLines
                + "   Hash " + (h1 == h2 ? "gleich" : "anders"));
            bool ok = wrote == expectWrite && fileChanged == expectWrite && normalLines == (expectWrite ? 1 : 0);
            if (!ok) fails++;
            Console.WriteLine("  -> " + (ok ? "OK" : "FAIL — erwartet: geschrieben=" + expectWrite));
            Console.WriteLine();
        }
        Console.WriteLine(fails == 0 ? "ALLE DRY-RUN-FAELLE GRUEN" : fails + " FEHLER");
        return fails == 0 ? 0 : 1;
    }

    private static int OldMain(string media, string root, string ffmpeg)
    {
        foreach (var mode in new[] { LogLevelMode.Normal, LogLevelMode.Verbose, LogLevelMode.Debug })
        {
            string work = Path.Combine(Path.GetDirectoryName(media)!, "log_" + mode + ".mkv");
            File.Copy(media, work, true);
            string dbDir = Path.Combine(root, "db_" + mode);
            if (Directory.Exists(dbDir)) Directory.Delete(dbDir, true);
            Directory.CreateDirectory(dbDir);

            using var db = new SubdlDbContext(dbDir, null);
            var reg = new ContentHashRegistry(db, null, null);
            var cfg = new PluginConfiguration { LogMode = mode, AllocateMissingLanguageCodes = true };
            reg.EnsureMedia(reg.GetMediaHash(work)!, "aabbccdd11223344aabbccdd11223344", work);

            var cap = new Cap();
            var gate = new LanguageTagGate(cap, cfg, reg);
            var streams = Probe(work);
            var res = gate.RunAsync(work, streams, ffmpeg, CancellationToken.None).GetAwaiter().GetResult();

            Console.WriteLine("===== LogMode = " + mode + "  (geschrieben: " + res.Written + ") =====");
            foreach (var (lvl, msg) in cap.Lines)
                Console.WriteLine("  [" + Level(lvl) + "] " + msg);
            Console.WriteLine();
        }
        return 0;
    }

    private static string Level(Microsoft.Extensions.Logging.LogLevel l) => l switch
    {
        Microsoft.Extensions.Logging.LogLevel.Information => "INF",
        Microsoft.Extensions.Logging.LogLevel.Warning => "WRN",
        Microsoft.Extensions.Logging.LogLevel.Error => "ERR",
        _ => l.ToString().Substring(0, 3).ToUpperInvariant()
    };

    // ffprobe -> MediaStream-Liste, gleiche Spaltenlogik wie der Seeder-Harness
    private static List<MediaBrowser.Model.Entities.MediaStream> Probe(string media)
    {
        var psi = new System.Diagnostics.ProcessStartInfo { FileName = "/usr/bin/ffprobe", RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-v","error","-show_entries","stream=index,codec_type,codec_name:stream_tags=language:stream_disposition=forced","-of","csv=p=0", media })
            psi.ArgumentList.Add(a);
        var pr = System.Diagnostics.Process.Start(psi)!;
        string raw = pr.StandardOutput.ReadToEnd(); pr.WaitForExit();
        var known = new HashSet<string> { "video", "audio", "subtitle", "data", "attachment" };
        var outl = new List<MediaBrowser.Model.Entities.MediaStream>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(',');
            string type = f.FirstOrDefault(x => known.Contains(x)) ?? "video";
            if (type != "subtitle") continue;
            string? tag = null;
            for (int c = f.Length - 1; c >= 1; c--)
            {
                var v = f[c];
                if (known.Contains(v)) continue;
                if (v.Any(ch => char.IsDigit(ch)) || v.Contains("_")) break;
                if (v.Length >= 2 && v.Length <= 9 && v.All(char.IsLetter)) tag = v;
                break;
            }
            outl.Add(new MediaBrowser.Model.Entities.MediaStream
            {
                Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle,
                Codec = f.Skip(1).FirstOrDefault(x => !known.Contains(x)) ?? "subrip",
                Language = tag
            });
        }
        return outl;
    }
}
