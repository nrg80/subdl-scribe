
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.SubdlScribe.Configuration;
using Jellyfin.Plugin.SubdlScribe.Data;
using Jellyfin.Plugin.SubdlScribe.Pipeline;
using Jellyfin.Plugin.SubdlScribe.Registry;

// T81: findet die Registry die vom Seeder geschriebene Sprache, wo die STREAMLISTE sie nicht sieht?
internal static class SwapRun
{
    private static int Main(string[] args)
    {
        string media = args[0], root = args[1], ffmpeg = args[2];
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        string work = Path.Combine(root, "item.mkv");
        File.Copy(media, work, true);

        using var db = new SubdlDbContext(Path.Combine(root, "db"), null);
        var reg = new ContentHashRegistry(db, null, null);
        var cfg = new PluginConfiguration { LogMode = LogLevelMode.Normal, AllocateMissingLanguageCodes = true };

        // 1) Der Seeder-Weg: Gate MIT Registry -> schreibt Tags, schreibt die Zeilen
        string h1 = reg.GetMediaHash(work)!;
        reg.EnsureMedia(h1, "aa11bb22cc33dd44aa11bb22cc33dd44", work);
        var streams = Probe(work);
        var gate = new LanguageTagGate(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, cfg, reg);
        var res = gate.RunAsync(work, streams, ffmpeg, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine("Seeder-Gate: " + res.UntaggedSeen + " untagged, " + res.Wanted.Count + " erkannt, " + res.Written + " geschrieben");
        string cur = res.NewHash ?? h1;
        reg.EnsureMedia(cur, "aa11bb22cc33dd44aa11bb22cc33dd44", work);
        foreach (var (pos, lang, hi) in res.Tracks) reg.ObserveEmbed(cur, pos, lang, hi);
        Console.WriteLine("  Registry-Zeilen unter " + cur + ": " + reg.GetEmbeds(cur).Count);

        // 2) Die STREAMLISTE, wie Jellyfin sie liefert (gecacht -> alte Tags)
        var stale = Probe(work);   // frisch gelesen, aber wir simulieren Jellyfins Cache mit dem ALTEN Stand
        var staleStreams = streams; // der Stand VOR dem Schreiben
        var viaStreams = Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming.EmbeddedPresentLanguages(staleStreams);
        var viaRegistry = reg.EmbeddedLanguages(work);
        Console.WriteLine();
        Console.WriteLine("EmbeddedPresentLanguages (Streamliste, ALTER Stand): [" + string.Join(",", viaStreams.OrderBy(x=>x)) + "]");
        Console.WriteLine("EmbeddedLanguages        (Registry, aktuell)      : [" + string.Join(",", viaRegistry.OrderBy(x=>x)) + "]");

        // 3) Die Frage: welche Sprachen sind ueber die Registry sichtbar, die die Streamliste NICHT hat?
        var onlyRegistry = viaRegistry.Where(x => !viaStreams.Contains(x)).OrderBy(x => x).ToList();
        Console.WriteLine();
        Console.WriteLine("Nur ueber die Registry sichtbar: [" + string.Join(",", onlyRegistry) + "]");
        Console.WriteLine("Erwartet: die 4 aufgeloesten Sprachen RU + ZH (die Streamliste kennt sie nicht)");
        bool ok = onlyRegistry.Count > 0 && onlyRegistry.All(x => x is "RU" or "ZH");
        Console.WriteLine();
        Console.WriteLine(ok ? ">>> OK: die Registry liefert genau die Sprachen, die die Streamliste verschweigt"
                            : ">>> FEHLER: erwartet RU/ZH, bekam " + string.Join(",", onlyRegistry));
        return ok ? 0 : 1;
    }

    private static List<MediaBrowser.Model.Entities.MediaStream> Probe(string media)
    {
        var psi = new System.Diagnostics.ProcessStartInfo { FileName = "/usr/bin/ffprobe", RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-v","error","-show_entries","stream=index,codec_type,codec_name:stream_tags=language","-of","csv=p=0", media })
            psi.ArgumentList.Add(a);
        var pr = System.Diagnostics.Process.Start(psi)!;
        string raw = pr.StandardOutput.ReadToEnd(); pr.WaitForExit();
        var known = new HashSet<string> { "video","audio","subtitle","data","attachment" };
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
            outl.Add(new MediaBrowser.Model.Entities.MediaStream {
                Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle,
                Codec = f.Skip(1).FirstOrDefault(x => !known.Contains(x)) ?? "subrip",
                Language = tag });
        }
        return outl;
    }
}
