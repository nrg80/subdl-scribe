// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

// T203 / F-M480-F-M482 (operator order 15.11.2026): the tvsubtitles.net client is driven with the
// pages the site actually served (fixtures/, captured 15.11.2026), never with the live site — a suite
// that touches the network cannot tell a broken parser from a broken day on their side.
//
// Two halves:
//   1. the PARSERS against real HTML: show list, seasons, episode pages, episode label, entries,
//      the download page's JavaScript file name;
//   2. the UPLOAD against the form's own limits: which inputs are refused, in which ORDER the fields
//      go out, that the author field is absent, and that a dry run builds a body and sends nothing.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Api;

internal static class Program
{
    private static int _failures;
    private static int _checks;

    private static string _fixtures = string.Empty;

    private static async Task<int> Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";
        _fixtures = Path.Combine(root, "scripts/tests/tvsubtitles/fixtures");

        // ---- 1. the language table is the SITE's, not ISO 639-1 --------------------------------
        Check("24 languages are offered, as many as the form's select carries", TvSubtitlesClient.Languages.Count == 24);
        Check("the codes that trip an ISO-minded implementation are the site's own",
            TvSubtitlesClient.Languages["br"].Iso3 == "pob"      // Brazilian Portuguese
            && TvSubtitlesClient.Languages["pt"].Iso3 == "por"   // European Portuguese
            && TvSubtitlesClient.Languages["cn"].Iso3 == "chi"
            && TvSubtitlesClient.Languages["jp"].Iso3 == "jpn"
            && TvSubtitlesClient.Languages["gr"].Iso3 == "gre"
            && TvSubtitlesClient.Languages["cz"].Iso3 == "cze"
            && TvSubtitlesClient.Languages["ro"].Iso3 == "rum"
            && TvSubtitlesClient.Languages["ru"].Iso3 == "rus",
            "pt/br, cn/jp, gr/cz/ro are the pairs an ISO table gets wrong");
        Check("the rip list is the select's, plus the empty value",
            TvSubtitlesClient.Rips.Count == 8 && TvSubtitlesClient.Rips.Contains("BluRay")
            && TvSubtitlesClient.Rips.Contains(string.Empty));

        // ---- 1b. ISO -> the site's codes, and ONLY here (F-M502) --------------------------------
        // Die Seite kennt vier Kuerzel, die kein ISO sind (gr/cz/cn/jp). Jellyfin liefert
        // Drei-Buchstaben-Codes, die Konfiguration teils Zwei-Buchstaben-Codes; beide muessen auf
        // das Kuerzel der Seite fallen, sonst lehnt BuildUpload den Upload ab.
        Check("the four deviations map in both spellings (F-M502)",
            TvSubtitlesClient.SiteLanguage("el") == "gr" && TvSubtitlesClient.SiteLanguage("ell") == "gr"
            && TvSubtitlesClient.SiteLanguage("gre") == "gr"
            && TvSubtitlesClient.SiteLanguage("cs") == "cz" && TvSubtitlesClient.SiteLanguage("ces") == "cz"
            && TvSubtitlesClient.SiteLanguage("cze") == "cz"
            && TvSubtitlesClient.SiteLanguage("zh") == "cn" && TvSubtitlesClient.SiteLanguage("zho") == "cn"
            && TvSubtitlesClient.SiteLanguage("chi") == "cn"
            && TvSubtitlesClient.SiteLanguage("ja") == "jp" && TvSubtitlesClient.SiteLanguage("jpn") == "jp",
            "found: el/ell/gre→" + TvSubtitlesClient.SiteLanguage("gre")
            + ", cs/ces/cze→" + TvSubtitlesClient.SiteLanguage("cze")
            + ", zh/zho/chi→" + TvSubtitlesClient.SiteLanguage("chi")
            + ", ja/jpn→" + TvSubtitlesClient.SiteLanguage("jpn"));
        Check("the other page languages map from their ISO-639-2 forms (F-M502)",
            TvSubtitlesClient.SiteLanguage("eng") == "en"
            && TvSubtitlesClient.SiteLanguage("spa") == "es"
            && TvSubtitlesClient.SiteLanguage("fra") == "fr" && TvSubtitlesClient.SiteLanguage("fre") == "fr"
            && TvSubtitlesClient.SiteLanguage("deu") == "de" && TvSubtitlesClient.SiteLanguage("ger") == "de"
            && TvSubtitlesClient.SiteLanguage("por") == "pt" && TvSubtitlesClient.SiteLanguage("pob") == "br"
            && TvSubtitlesClient.SiteLanguage("rus") == "ru" && TvSubtitlesClient.SiteLanguage("ukr") == "ua"
            && TvSubtitlesClient.SiteLanguage("ita") == "it" && TvSubtitlesClient.SiteLanguage("ara") == "ar"
            && TvSubtitlesClient.SiteLanguage("hun") == "hu" && TvSubtitlesClient.SiteLanguage("pol") == "pl"
            && TvSubtitlesClient.SiteLanguage("tur") == "tr"
            && TvSubtitlesClient.SiteLanguage("nld") == "nl" && TvSubtitlesClient.SiteLanguage("dut") == "nl"
            && TvSubtitlesClient.SiteLanguage("swe") == "sv" && TvSubtitlesClient.SiteLanguage("dan") == "da"
            && TvSubtitlesClient.SiteLanguage("fin") == "fi" && TvSubtitlesClient.SiteLanguage("kor") == "ko"
            && TvSubtitlesClient.SiteLanguage("bul") == "bg"
            && TvSubtitlesClient.SiteLanguage("ron") == "ro" && TvSubtitlesClient.SiteLanguage("rum") == "ro",
            "found: ukr→" + TvSubtitlesClient.SiteLanguage("ukr")
            + ", dut→" + TvSubtitlesClient.SiteLanguage("dut")
            + ", rum→" + TvSubtitlesClient.SiteLanguage("rum"));
        Check("the site's own codes stay what they are (F-M502)",
            TvSubtitlesClient.Languages.Keys.All(k => TvSubtitlesClient.SiteLanguage(k) == k),
            "a code the page carries must not be rewritten");
        Check("case does not matter (F-M502)",
            TvSubtitlesClient.SiteLanguage("DE") == "de"
            && TvSubtitlesClient.SiteLanguage("DeU") == "de"
            && TvSubtitlesClient.SiteLanguage("GRE") == "gr"
            && TvSubtitlesClient.SiteLanguage("Pt-Br") == "br"
            && TvSubtitlesClient.SiteLanguage("  jpn  ") == "jp");
        Check("a region or script suffix is reduced to the base code — except pt-BR (F-M502)",
            TvSubtitlesClient.SiteLanguage("pt-BR") == "br"
            && TvSubtitlesClient.SiteLanguage("pt_BR") == "br"
            && TvSubtitlesClient.SiteLanguage("pt-PT") == "pt"
            && TvSubtitlesClient.SiteLanguage("zh-Hans") == "cn"
            && TvSubtitlesClient.SiteLanguage("zh_CN") == "cn"
            && TvSubtitlesClient.SiteLanguage("ja-JP") == "jp"
            && TvSubtitlesClient.SiteLanguage("es-419") == "es",
            "found: pt-BR→" + TvSubtitlesClient.SiteLanguage("pt-BR")
            + ", pt-PT→" + TvSubtitlesClient.SiteLanguage("pt-PT")
            + ", zh-Hans→" + TvSubtitlesClient.SiteLanguage("zh-Hans"));
        Check("an unknown code yields null instead of a guess (F-M502)",
            TvSubtitlesClient.SiteLanguage("xx") is null
            && TvSubtitlesClient.SiteLanguage("klingon") is null
            && TvSubtitlesClient.SiteLanguage("zz-ZZ") is null
            && TvSubtitlesClient.SiteLanguage(string.Empty) is null
            && TvSubtitlesClient.SiteLanguage("   ") is null
            && TvSubtitlesClient.SiteLanguage(null) is null);
        var langData = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nhello\n");
        var greek = Build(lang: "gre", data: langData);
        var german = Build(lang: "deu", data: langData);
        Check("BuildUpload checks the language through the mapping and sends the site's code (F-M502)",
            greek!.Fields.Single(f => f.Key == "lang").Value == "gr"
            && greek.LanguageName == "greek" && greek.Iso3 == "gre"
            && german!.Fields.Single(f => f.Key == "lang").Value == "de"
            && german.LanguageName == "german" && german.Iso3 == "ger");
        var refusal = Refusal(() => Build(lang: "xx", data: langData));
        Check("an unmapped language is refused with the site's list, in one line (F-M502)",
            refusal is not null
            && refusal.Contains("language must be one of the site's codes", StringComparison.Ordinal)
            && refusal.Contains("gr", StringComparison.Ordinal)
            && refusal.Contains("cz", StringComparison.Ordinal),
            "found: " + (refusal ?? "<no refusal>"));
        var isoName = Build(lang: "gre", fileName: "The Middle - 2x24.gre.srt", data: langData)!;
        var (isoBody, _) = TvSubtitlesClient.BuildUploadBody(isoName);
        Check("the upload's file name keeps its ISO3 ending from the table (F-M502)",
            isoName.FileName == "The Middle - 2x24.gre.srt"
            && isoName.Iso3 == TvSubtitlesClient.Languages["gr"].Iso3
            && Encoding.UTF8.GetString(isoBody)
                .Contains("filename=\"The Middle - 2x24.gre.srt\"", StringComparison.Ordinal));

        // ---- 2. the comment carries the ids, the author stays out -------------------------------
        Check("the comment names the ids and then the client that wrote it",
            TvSubtitlesClient.Comment(1422, "tt1442464")
                == "TMDB 1422 / IMDb tt1442464 / " + TvSubtitlesClient.ClientTag,
            "found: " + TvSubtitlesClient.Comment(1422, "tt1442464"));
        Check("the client tag carries the running version, read from the assembly",
            TvSubtitlesClient.ClientTag.StartsWith("Sub Scribe ", StringComparison.Ordinal)
            && TvSubtitlesClient.ClientTag.Length > "Sub Scribe ".Length
            && TvSubtitlesClient.ClientTag != "Sub Scribe 0.0.0.0");
        Check("one id is enough, an unknown id stays out, the client tag never does",
            TvSubtitlesClient.Comment(1422, null) == "TMDB 1422 / " + TvSubtitlesClient.ClientTag
            && TvSubtitlesClient.Comment(null, "tt1442464") == "IMDb tt1442464 / " + TvSubtitlesClient.ClientTag
            && TvSubtitlesClient.Comment(null, null) == TvSubtitlesClient.ClientTag);
        Check("an extra text sits between the ids and the client tag",
            TvSubtitlesClient.Comment(1422, null, "recheck") == "TMDB 1422 / recheck / " + TvSubtitlesClient.ClientTag);

        // ---- 3. the upload refuses what the form would silently swallow -------------------------
        var data = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nhello\n");
        Check("a valid upload is accepted", BuildOk(data) is not null);
        Check("show id 0 is refused (no series at all)", Refused(() => Build(showId: 0, data: data)));
        Check("season 0 and 23 are refused", Refused(() => Build(season: 0, data: data)) && Refused(() => Build(season: 23, data: data)));
        Check("episode -1 and 101 are refused", Refused(() => Build(episode: -1, data: data)) && Refused(() => Build(episode: 101, data: data)));
        // F-M502: ein ISO-Code wird nicht mehr abgelehnt, sondern abgebildet (gre → gr, pt-BR → br);
        // abgelehnt wird nur, was die Bruecke nicht kennt.
        Check("an unknown language is refused, an ISO code is not (F-M502)",
            Refused(() => { _ = Build(lang: "xx", data: data); })
            && Refused(() => { _ = Build(lang: string.Empty, data: data); })
            && !Refused(() => { _ = Build(lang: "gre", data: data); }));
        Check("an unknown rip is refused", Refused(() => Build(rip: "HDTV.XviD", data: data)));
        Check("an upload without any id is refused — the comment would be empty",
            Refused(() => TvSubtitlesClient.BuildUpload(
                512, 2, 24, "de", "x.srt", data, "HDTV", "FEVER", null, null)));
        Check("an empty file and a wrong extension are refused",
            Refused(() => Build(data: Array.Empty<byte>())) && Refused(() => Build(fileName: "x.ass", data: data)));

        var upload = BuildOk(data)!;
        var order = upload.Fields.Select(f => f.Key).ToArray();
        Check("the fields leave in the form's own order",
            order.SequenceEqual(new[] { "tvshow", "season", "episode", "lang", "rip", "release", "comment", "go" }),
            "found: " + string.Join(",", order));
        Check("the author field is NOT sent at all",
            !upload.Fields.Any(f => f.Key == "author"),
            "an absent part is the only way to keep whatever the page pre-fills");
        Check("the ids land in the comment field, the release stays its own field",
            upload.Fields.Single(f => f.Key == "comment").Value
                == "TMDB 1422 / IMDb tt1442464 / " + TvSubtitlesClient.ClientTag
            && upload.Fields.Single(f => f.Key == "release").Value == "FEVER");
        var flags = BuildOk(data, doubleEpisode: true, hearingImpaired: true)!;
        Check("double episode and hearing-impaired are appended as \"1\" and only when set",
            flags.Fields.Single(f => f.Key == "double").Value == "1"
            && flags.Fields.Single(f => f.Key == "hi").Value == "1"
            && !upload.Fields.Any(f => f.Key is "double" or "hi"));

        // ---- 4. the multipart body: measured, not sent -----------------------------------------
        var (body, boundary) = TvSubtitlesClient.BuildUploadBody(upload);
        var text = Encoding.UTF8.GetString(body);
        Check("the body carries the file part with its name and octet-stream type",
            text.Contains("name=\"upfile\"; filename=\"The Middle - 2x24.srt\"", StringComparison.Ordinal)
            && text.Contains("Content-Type: application/octet-stream", StringComparison.Ordinal));
        Check("every field is a part of its own",
            upload.Fields.All(f => text.Contains("name=\"" + f.Key + "\"\r\n\r\n" + f.Value, StringComparison.Ordinal)));
        Check("the body opens with the boundary and closes with the double one",
            text.StartsWith("--" + boundary + "\r\n", StringComparison.Ordinal)
            && text.EndsWith("--" + boundary + "--\r\n", StringComparison.Ordinal));
        Check("the subtitle bytes are inside the body unchanged",
            body.AsSpan().IndexOf(data) >= 0);

        var handler = new CountingHandler();
        var client = new TvSubtitlesClient(new HttpClient(handler));
        var dry = await client.UploadAsync(upload).ConfigureAwait(false);
        Check("a dry run says it sent nothing and still reports the body size",
            !dry.Sent && dry.Status is null && handler.Requests == 0 && dry.BodyBytes == body.Length);
        var real = await client.UploadAsync(upload, dryRun: false).ConfigureAwait(false);
        Check("a real run posts multipart to /add1.php with the upload page as referer",
            real.Sent && handler.Requests == 1
            && handler.LastUrl == "https://www.tvsubtitles.net/add1.php"
            && handler.LastReferer == "https://www.tvsubtitles.net/add.html"
            && handler.LastContentType!.StartsWith("multipart/form-data; boundary=", StringComparison.Ordinal));
        Check("the response text is stripped of tags and collapsed",
            real.Response == "ok" && handler.RepliedWith == 200);

        // ---- 5. creating a show ----------------------------------------------------------------
        var addShow = TvSubtitlesClient.BuildAddShow("Beispielserie Test", 2026, 3);
        Check("the create-show payload is name, year_s, seasons, go in that order",
            addShow.Fields.Select(f => f.Key).SequenceEqual(new[] { "name", "year_s", "seasons", "go" }));
        Check("an implausible year and a season count of 0 are refused",
            Refused(() => TvSubtitlesClient.BuildAddShow("X", 1800, 3))
            && Refused(() => TvSubtitlesClient.BuildAddShow("X", 2026, 0))
            && Refused(() => TvSubtitlesClient.BuildAddShow(" ", 2026, 3)));

        // ---- 6. the parsers against the pages the site served -----------------------------------
        var addHtml = Fixture("add-tvshow-select.html");
        var shows = TvSubtitlesClient.ParseShows(addHtml);
        Check("the show select parses into id -> name", shows.Count >= 8 && shows.ContainsKey(512),
            "parsed " + shows.Count + " entries");
        Check("the show resolves by name", TvSubtitlesClient.ResolveShow(shows, "the middle") == 512);
        // F-M501: der Vergleich ist eng — Gross/Kleinschreibung und Leerraum zaehlen nicht, sonst gilt
        // Gleichheit. Die fruehere "enthaelt"-Stufe ist weg: "The Middle" findet NICHT
        // "Malcolm in the Middle" und nicht "The Middle Man".
        Check("the name comparison is narrow: case and whitespace only (F-M501)",
            TvSubtitlesClient.ResolveShow(shows, "  The   MIDDLE ") == 512
            && TvSubtitlesClient.ResolveShow(shows, "Malcolm in the Middle") == 422
            && TvSubtitlesClient.ResolveShow(shows, "The Middle") == 512);
        Check("an unknown show resolves to null, and no substring matches (F-M501)",
            TvSubtitlesClient.ResolveShow(shows, "Nope-Nope-Nope") is null
            && TvSubtitlesClient.ResolveShow(shows, "Middle") is null
            && TvSubtitlesClient.ResolveShow(shows, "The Middle Ma") is null);

        var seasons = TvSubtitlesClient.ParseSeasons(Fixture("tvshow-512.html"), 512);
        Check("the show page yields its nine seasons, ascending",
            seasons.SequenceEqual(Enumerable.Range(1, 9)), "found: " + string.Join(",", seasons));

        var pages = TvSubtitlesClient.ParseEpisodePages(Fixture("tvshow-512-2.html"));
        Check("the season page yields 24 episode pages without duplicates",
            pages.Count == 24 && pages.Distinct().Count() == 24, "found " + pages.Count);
        Check("the page ids are page ids, not episode numbers", pages[0] == 31911);

        var episodeHtml = Fixture("episode-31911.html");
        var label = TvSubtitlesClient.ParseEpisodeLabel(episodeHtml);
        Check("the label comes from the page itself (2x24)", label == (2, 24), "found " + label);

        var entries = TvSubtitlesClient.ParseEntries(episodeHtml);
        Check("the entries carry the flag's language, the rip and the release",
            entries.Count == 3
            && entries.All(e => TvSubtitlesClient.Languages.ContainsKey(e.Language))
            && entries[0].Rip.Length > 0 && entries[0].Release.Length > 0,
            "entries: " + string.Join(" | ", entries.Select(e => e.Language + "/" + e.Rip + "/" + e.Release)));
        Check("every entry has its own id", entries.Select(e => e.SubtitleId).Distinct().Count() == entries.Count);

        var fileName = TvSubtitlesClient.ParseDownloadFileName(Fixture("download-page.html"));
        Check("the download page's JavaScript fragments name the file",
            fileName == "files/The Middle_2x24_HDTV.FEVER.en.zip", "found: " + fileName);
        Check("a page without fragments yields null instead of a broken path",
            TvSubtitlesClient.ParseDownloadFileName("<html>nothing</html>") is null);
        Check("a ZIP payload is recognised from its own bytes (the site answers in two shapes)",
            TvSubtitlesClient.IsZip(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 })
            && !TvSubtitlesClient.IsZip(Encoding.UTF8.GetBytes("<html><script>var s1='files/x.zip';</script></html>"))
            && !TvSubtitlesClient.IsZip(Array.Empty<byte>()));

        // ---- 7. opt-in live run: the only part that touches the site ----------------------------
        // Not a check, a probe: it prints what the live site answers so a protocol change shows up as
        // a visible difference instead of a red suite on somebody else's bad day. Run with --live.
        if (args.Contains("--live"))
        {
            Console.WriteLine();
            Console.WriteLine("--- live probe against tvsubtitles.net ---");
            var liveHttp = SubdlApiClient.NewHttpClient(TimeSpan.FromSeconds(60));
            var live = new TvSubtitlesClient(liveHttp);
            var liveShows = await live.ShowsAsync().ConfigureAwait(false);
            var middle = TvSubtitlesClient.ResolveShow(liveShows, "The Middle");
            Console.WriteLine($"  shows: {liveShows.Count}, The Middle -> {middle}");
            if (middle is int showId)
            {
                var liveSeasons = await live.SeasonsAsync(showId).ConfigureAwait(false);
                Console.WriteLine($"  seasons: {string.Join(",", liveSeasons)}");
                var livePages = await live.EpisodePagesAsync(showId, 2).ConfigureAwait(false);
                Console.WriteLine($"  S02 episode pages: {livePages.Count}, first {livePages.FirstOrDefault()}");
                if (livePages.Count > 0)
                {
                    var pageId = livePages[0];
                    var liveLabel = await live.EpisodeLabelAsync(pageId).ConfigureAwait(false);
                    var liveEntries = await live.EntriesAsync(pageId).ConfigureAwait(false);
                    Console.WriteLine($"  page {pageId} = {liveLabel.Season}x{liveLabel.Episode}, entries: {liveEntries.Count}");
                    foreach (var e in liveEntries.Take(5))
                    {
                        Console.WriteLine($"    {e.SubtitleId} {e.Language,-3} {e.Rip,-7} {e.Release,-10} {e.Age}");
                    }

                    var pick = liveEntries.FirstOrDefault(e => e.Language == "en") ?? liveEntries.FirstOrDefault();
                    if (pick is not null)
                    {
                        // Transport vs. parser, separated: fetch the download page here and show what came back.
                        var subUrl = TvSubtitlesClient.BaseUrl + "/subtitle-" + pick.SubtitleId + ".html";
                        var dlUrl = TvSubtitlesClient.BaseUrl + "/download-" + pick.SubtitleId + ".html";
                        var subReq = new HttpRequestMessage(HttpMethod.Get, subUrl);
                        await liveHttp.SendAsync(subReq).ConfigureAwait(false);
                        var dlReq = new HttpRequestMessage(HttpMethod.Get, dlUrl);
                        dlReq.Headers.Referrer = new Uri(subUrl);
                        var dlRes = await liveHttp.SendAsync(dlReq).ConfigureAwait(false);
                        var dlHtml = await dlRes.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Console.WriteLine($"  download page: HTTP {(int)dlRes.StatusCode}, {dlHtml.Length} chars, "
                            + $"parsed name: {TvSubtitlesClient.ParseDownloadFileName(dlHtml) ?? "<none>"}");
                        Console.WriteLine("  excerpt: " + System.Text.RegularExpressions.Regex.Replace(dlHtml, "\\s+", " ")[..Math.Min(240, dlHtml.Length)]);
                        try
                        {
                            var (name, bytes) = await live.DownloadAsync(pick.SubtitleId).ConfigureAwait(false);
                            Console.WriteLine($"  download {pick.SubtitleId} ({pick.Language}): {name}, {bytes.Length} bytes");
                        }
                        catch (InvalidOperationException ex)
                        {
                            Console.WriteLine("  download refused: " + ex.Message);
                        }
                    }
                }
            }
        }

        Console.WriteLine();
        if (_failures > 0)
        {
            Console.WriteLine($">>> TVSUBTITLES: {_failures} von {_checks} Pruefungen fehlgeschlagen");
            return 1;
        }

        Console.WriteLine($">>> TVSUBTITLES OK: {_checks} Pruefungen bestanden");
        return 0;
    }

    private static string Fixture(string name)
    {
        var path = Path.Combine(_fixtures, name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("fixture missing: " + path);
        }

        return File.ReadAllText(path);
    }

    private static TvSubtitlesUpload? BuildOk(byte[] data, bool doubleEpisode = false, bool hearingImpaired = false) =>
        TvSubtitlesClient.BuildUpload(
            512, 2, 24, "de", "The Middle - 2x24.srt", data, "HDTV", "FEVER",
            1422, "tt1442464", null, doubleEpisode, hearingImpaired);

    private static TvSubtitlesUpload? Build(
        int showId = 512,
        int season = 2,
        int episode = 24,
        string lang = "de",
        string fileName = "x.srt",
        byte[]? data = null,
        string? rip = "HDTV") =>
        TvSubtitlesClient.BuildUpload(
            showId, season, episode, lang, fileName, data ?? Encoding.UTF8.GetBytes("1\n"),
            rip, "FEVER", 1422, "tt1442464");

    private static bool Refused(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static bool Refused(Func<object> action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>F-M502: die Meldung einer Ablehnung, oder null wenn nichts abgelehnt wurde — damit
    /// eine Pruefung auch den TEXT lesen kann und nicht nur, DASS abgelehnt wurde.</summary>
    private static string? Refusal(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        _checks++;
        if (ok)
        {
            Console.WriteLine("  [PASS] " + what);
            return;
        }

        _failures++;
        Console.WriteLine("  [FAIL] " + what + (detail is null ? string.Empty : " — " + detail));
    }

    /// <summary>An HTTP handler that answers "ok" and remembers what it was handed.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public string? LastUrl { get; private set; }

        public string? LastReferer { get; private set; }

        public string? LastContentType { get; private set; }

        public int RepliedWith { get; private set; } = 200;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastUrl = request.RequestUri?.ToString();
            LastReferer = request.Headers.Referrer?.ToString();
            LastContentType = request.Content?.Headers.ContentType?.ToString();
            await Task.Yield();
            RepliedWith = 200;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><b>ok</b>\n</html>", Encoding.UTF8, "text/html"),
            };
        }
    }
}
