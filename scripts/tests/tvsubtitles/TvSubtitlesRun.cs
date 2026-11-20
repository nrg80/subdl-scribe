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
//
// F-M516 (16.11.2026) adds the third part: the CREATE-SHOW path. The form is compared field by field
// against the page the site really serves, the answer is classified against the one answer that is
// proven (302 to /add-tvshow.php?done=yes), and the show id is taken from the site's own search —
// never from a guess and never from "highest id + 1".

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Api;
using Jellyfin.Plugin.SubScribe.Api.Endpoints;

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

        // ---- 2. the comment: three lines, the author stays out -----------------------------------
        // F-M574 (Operator-Vorgabe 17.11.2026): "Imdb: xxx / tmdb: xxx" / "SxxEyy: Tvshow title/
        // Episode Title" / "Year" — offizielle Schreibweise IMDb/TMDb, kein Client-Tag.
        var kommentar = Jellyfin.Plugin.SubScribe.Api.Endpoints.UploadComment.Series(
            "tt1442464", "1422", 2, 24, "The Middle", "The Man Hunt", 2009);
        Check("the comment carries ids, episode line and year in three lines",
            kommentar == "IMDb: tt1442464 / TMDb: 1422 — S02E24: The Middle / The Man Hunt — 2009",
            "found: " + kommentar.Replace("\n", " | ", StringComparison.Ordinal));
        Check("one id is enough, an unknown part stays out",
            Jellyfin.Plugin.SubScribe.Api.Endpoints.UploadComment.Series("tt1442464", null, 2, 24, "The Middle", "The Man Hunt", 0)
                == "IMDb: tt1442464 — S02E24: The Middle / The Man Hunt"
            && Jellyfin.Plugin.SubScribe.Api.Endpoints.UploadComment.Series(null, null, 2, 24, "The Middle", null, 0)
                == "S02E24: The Middle");
        Check("a placeholder-free episode line keeps the show title only, a film drops the line",
            Jellyfin.Plugin.SubScribe.Api.Endpoints.UploadComment.Movie("tt15398776", "872585", "Oppenheimer", 2023)
                == "IMDb: tt15398776 / TMDb: 872585 — Oppenheimer — 2023");

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
                == "IMDb: tt1442464 / TMDb: 1422 — S02E24"
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
            && handler.LastUrl == TvSubtitlesClient.WithClient("https://www.tvsubtitles.net/add1.php")
            && handler.LastReferer == "https://www.tvsubtitles.net/add.html"
            && handler.LastContentType!.StartsWith("multipart/form-data; boundary=", StringComparison.Ordinal));
        Check("the response text is stripped of tags and collapsed",
            real.Response == "ok" && handler.RepliedWith == 200);

        // ---- 4b. der Staffel-Pack (F-M641/F-M642) -----------------------------------------------
        // Die Seite hat einen Staffel-Slot: episode=0, Datei = EIN Zip, release = Pack-Name, Kommentar
        // = Staffelzeile. Ein Pack ist eine Sprache und eine Variante — HI und Nicht-HI werden nie
        // gemischt, deshalb beschreibt das `hi`-Feld den ganzen Container.
        var packZip = ZipOf(
            ("The Middle S02E01.de.srt", "1\n00:00:01,000 --> 00:00:02,000\nhallo\n"),
            ("The Middle S02E02.de.srt", "1\n00:00:01,000 --> 00:00:02,000\nhallo\n"),
            ("The Middle S02E02.de.srt", "1\n00:00:01,000 --> 00:00:02,000\nhallo\n"));
        var pack = TvSubtitlesClient.BuildSeasonPack(
            512, 2, "de", "The.Middle.S02.GERMAN.1080p.WEB-FEVER.zip", packZip, "WEB",
            "The.Middle.S02.GERMAN.1080p.WEB-FEVER", 1422, "tt1442464", false, "The Middle", 2009);
        Check("a pack goes into the season slot: episode=0, pack name in release (F-M641)",
            pack.Fields.Single(f => f.Key == "episode").Value == "0"
            && pack.Fields.Single(f => f.Key == "season").Value == "2"
            && pack.Fields.Single(f => f.Key == "release").Value == "The.Middle.S02.GERMAN.1080p.WEB-FEVER"
            && pack.FileName.EndsWith(".zip", StringComparison.Ordinal),
            string.Join(",", pack.Fields.Select(f => f.Key + "=" + f.Value)));
        Check("the pack sends the form's fields in the form's order, no `double` (F-M641)",
            pack.Fields.Select(f => f.Key)
                .SequenceEqual(new[] { "tvshow", "season", "episode", "lang", "rip", "release", "comment", "go" }),
            string.Join(",", pack.Fields.Select(f => f.Key)));
        Check("the pack's comment names the season, not an episode (F-M641)",
            pack.Fields.Single(f => f.Key == "comment").Value
                == "IMDb: tt1442464 / TMDb: 1422 — S02: The Middle — 2009"
            && !pack.Fields.Single(f => f.Key == "comment").Value.Contains("S02E", StringComparison.Ordinal),
            pack.Fields.Single(f => f.Key == "comment").Value);
        Check("the comment carries neither version nor client name (F-M641)",
            !pack.Fields.Single(f => f.Key == "comment").Value.Contains("Sub Scribe", StringComparison.Ordinal)
            && !System.Text.RegularExpressions.Regex.IsMatch(
                pack.Fields.Single(f => f.Key == "comment").Value, "[0-9]+\\.[0-9]+\\.[0-9]+"),
            pack.Fields.Single(f => f.Key == "comment").Value);
        Check("hi is sent only for a pack that is HI throughout (F-M641)",
            !pack.Fields.Any(f => f.Key is "hi" or "double")
            && TvSubtitlesClient.BuildSeasonPack(
                    512, 2, "de", "p.zip", packZip, "WEB", "P.S02.GERMAN.HI", 1422, "tt1442464",
                    hearingImpaired: true, showTitle: "The Middle")
                .Fields.Single(f => f.Key == "hi").Value == "1");
        Check("a pack must be one .zip and must carry its name (F-M641)",
            Refused(() => TvSubtitlesClient.BuildSeasonPack(
                512, 2, "de", "folge.srt", packZip, "WEB", "Name", 1422, "tt1442464"))
            && (Refusal(() => TvSubtitlesClient.BuildSeasonPack(
                    512, 2, "de", "p.zip", packZip, "WEB", null, 1422, "tt1442464")) ?? string.Empty)
                .Contains("pack name", StringComparison.Ordinal));
        Check("the pack's episodes come from its member names, ascending and without doubles (F-M641)",
            TvSubtitlesClient.EpisodesInZip(packZip).SequenceEqual(new[] { 1, 2 })
            && TvSubtitlesClient.EpisodesInZip(null).Count == 0
            && TvSubtitlesClient.EpisodesInZip(Encoding.UTF8.GetBytes("kein zip")).Count == 0);
        var (packBody, _) = TvSubtitlesClient.BuildUploadBody(pack);
        Check("the pack leaves as the zip it is, bytes unchanged (F-M641)",
            Encoding.UTF8.GetString(packBody).Contains(
                "name=\"upfile\"; filename=\"The.Middle.S02.GERMAN.1080p.WEB-FEVER.zip\"", StringComparison.Ordinal)
            && packBody.AsSpan().IndexOf(packZip) >= 0);

        // ---- 5. creating a show: the form, the answer, the number (F-M516) -----------------------
        var addShow = TvSubtitlesClient.BuildAddShow("Beispielserie Test", 2026, 3);
        Check("the create-show payload is name, year_s, seasons, go in that order",
            addShow.Fields.Select(f => f.Key).SequenceEqual(new[] { "name", "year_s", "seasons", "go" }));
        Check("an implausible year and a season count of 0 are refused",
            Refused(() => TvSubtitlesClient.BuildAddShow("X", 1800, 3))
            && Refused(() => TvSubtitlesClient.BuildAddShow("X", 2026, 0))
            && Refused(() => TvSubtitlesClient.BuildAddShow(" ", 2026, 3)));

        // Das echte Formular (Fixture vom 16.11.2026) — es ist multipart, und es hat genau vier Felder.
        var addFormHtml = Fixture("add-tvshow-form.html");
        Check("the site's create-show form is multipart and posts to /add-tvshow1.php",
            addFormHtml.Contains("action=\"/add-tvshow1.php\"", StringComparison.Ordinal)
            && addFormHtml.Contains("method=\"post\"", StringComparison.Ordinal)
            && addFormHtml.Contains("enctype=\"multipart/form-data\"", StringComparison.Ordinal));
        var formFields = FormFieldNames(addFormHtml);
        Check("the payload carries exactly the form's fields — none invented, none missing (F-M516)",
            formFields.SequenceEqual(addShow.Fields.Select(f => f.Key)),
            "form: " + string.Join(",", formFields)
            + " / payload: " + string.Join(",", addShow.Fields.Select(f => f.Key)));
        Check("the form's go is a hidden 1 and the send button has no name of its own (F-M516)",
            addFormHtml.Contains("<input name=\"go\" type=\"hidden\" value=\"1\">", StringComparison.Ordinal)
            && addShow.Fields.Single(f => f.Key == "go").Value == "1"
            && System.Text.RegularExpressions.Regex.IsMatch(addFormHtml, "<input type=\"submit\" value=\"Send\""));

        // Die Antwort der Seite — belegt ist genau der Erfolg, und der ist eine Weiterleitung.
        var doneHtml = Fixture("add-tvshow-done.html");
        Check("the site's confirmation is recognised on its own page, and not on the plain form (F-M516)",
            TvSubtitlesClient.AddShowConfirmed(doneHtml)
            && !TvSubtitlesClient.AddShowConfirmed(addFormHtml));
        Check("the measured success answer (302 to ?done=yes) is read as created (F-M516)",
            TvSubtitlesClient.ClassifyAddShow(
                302, "https://www.tvsubtitles.net/add-tvshow.php?done=yes", null, string.Empty)
                == TvstAddShowOutcome.Created
            && TvSubtitlesClient.ClassifyAddShow(
                200, null, "https://www.tvsubtitles.net/add-tvshow.php?done=yes", "TV-Show has been added. Thank you.")
                == TvstAddShowOutcome.Created,
            "the redirect target and the text behind it are the same marker");
        Check("an answer without the site's own marker is NEVER reported as created (F-M516)",
            TvSubtitlesClient.ClassifyAddShow(
                200, null, "https://www.tvsubtitles.net/add-tvshow.php",
                "Before adding new TV-Show please check if it already exist")
                == TvstAddShowOutcome.Unclear
            && TvSubtitlesClient.ClassifyAddShow(200, null, null, string.Empty) == TvstAddShowOutcome.Unclear
            && TvSubtitlesClient.ClassifyAddShow(302, "https://www.tvsubtitles.net/add-tvshow.php", null, string.Empty)
                == TvstAddShowOutcome.Unclear
            && TvSubtitlesClient.ClassifyAddShow(null, null, null, null) == TvstAddShowOutcome.Unclear);
        Check("a server error is a refusal, not a creation (F-M516)",
            TvSubtitlesClient.ClassifyAddShow(500, null, null, string.Empty) == TvstAddShowOutcome.Refused
            && TvSubtitlesClient.ClassifyAddShow(403, "/x", null, "denied") == TvstAddShowOutcome.Refused);

        // Der Draht: derselbe Rumpf im Trockenlauf, gesendet nur auf dryRun:false.
        var addHandler = new CountingHandler();
        var addClient = new TvSubtitlesClient(new HttpClient(addHandler));
        var addDry = await addClient.AddShowAsync(addShow).ConfigureAwait(false);
        Check("a dry run of the creation builds the body, says so, and sends nothing",
            !addDry.Sent && addDry.Status is null && addDry.Outcome == TvstAddShowOutcome.DryRun
            && addHandler.Requests == 0 && addDry.BodyBytes > 0);
        addHandler.ReplyStatus = HttpStatusCode.Found;
        addHandler.ReplyBody = string.Empty;
        addHandler.ReplyLocation = "https://www.tvsubtitles.net/add-tvshow.php?done=yes";
        var addReal = await addClient.AddShowAsync(addShow, dryRun: false).ConfigureAwait(false);
        Check("the real call posts multipart to /add-tvshow1.php with the form as referer (F-M516)",
            addReal.Sent && addHandler.Requests == 1
            && addHandler.LastUrl == TvSubtitlesClient.WithClient("https://www.tvsubtitles.net/add-tvshow1.php")
            && addHandler.LastReferer == "https://www.tvsubtitles.net/add-tvshow.php"
            && addHandler.LastContentType!.StartsWith("multipart/form-data; boundary=", StringComparison.Ordinal),
            "url=" + addHandler.LastUrl + " ctype=" + addHandler.LastContentType);
        Check("the creation's body is the form's four parts and carries no file",
            addHandler.LastBodyText.Split("Content-Disposition: form-data").Length - 1 == 4
            && addShow.Fields.All(f => addHandler.LastBodyText.Contains(
                "name=\"" + f.Key + "\"\r\n\r\n" + f.Value, StringComparison.Ordinal))
            && !addHandler.LastBodyText.Contains("filename=", StringComparison.Ordinal)
            && addReal.BodyBytes == addDry.BodyBytes,
            "parts=" + (addHandler.LastBodyText.Split("Content-Disposition: form-data").Length - 1));
        Check("the creation reads its own answer as created and keeps the redirect target (F-M516)",
            addReal.Outcome == TvstAddShowOutcome.Created && addReal.Status == 302
            && addReal.Location!.Contains("done=yes", StringComparison.Ordinal));

        // Die Nummer nach der Anlage: die Seitensuche (weich!) und die Entscheidung darüber.
        var searchHits = TvSubtitlesClient.ParseShowSearch(Fixture("search-show.html"));
        Check("the site's search yields the id and the bare name, without the year range (F-M516)",
            searchHits.Count == 1 && searchHits.ContainsKey(4242) && searchHits[4242] == "Weissensee"
            && TvSubtitlesClient.ResolveShow(searchHits, "weissensee") == 4242,
            "found: " + string.Join(" | ", searchHits.Select(h => h.Key + "=" + h.Value)));
        Check("\"No results found\" yields no hit at all (F-M516)",
            TvSubtitlesClient.ParseShowSearch(Fixture("search-no-results.html")).Count == 0);
        var softHits = TvSubtitlesClient.ParseShowSearch(Fixture("search-soft.html"));
        Check("the site's search is soft — a lookalike hit is not a match (F-M516)",
            softHits.Count == 2
            && TvSubtitlesClient.ResolveShow(softHits, "Pagan Peak") is null,
            "found: " + string.Join(" | ", softHits.Select(h => h.Value)));
        Check("the answer alone decides nothing: the show does (F-M516)",
            TvstShowResolver.SettleStatus(TvstAddShowOutcome.Created, 4242) == TvstShowStatus.Created
            && TvstShowResolver.SettleStatus(TvstAddShowOutcome.Unclear, 4242) == TvstShowStatus.Existed
            && TvstShowResolver.SettleStatus(TvstAddShowOutcome.Refused, 4242) == TvstShowStatus.Existed
            && TvstShowResolver.SettleStatus(TvstAddShowOutcome.Created, null) == TvstShowStatus.CreatedIdUnknown
            && TvstShowResolver.SettleStatus(TvstAddShowOutcome.Refused, null) == TvstShowStatus.Refused
            && TvstShowResolver.SettleStatus(TvstAddShowOutcome.Unclear, null) == TvstShowStatus.Unclear,
            "created+id, unclear+id, created without id, refused without id are four different answers");

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

        // ---- 6b. F-M517: die Seriennummer geht AUCH OHNE Medien-Hash in die Pfad-Zeile ----------
        // Der Serienfall hat DASSELBE Loch wie die Filmnummer: `TvstShowResolver.Persist` kehrte bei
        // fehlendem Medien-Hash stumm zurück, obwohl die Medienzeile existiert und ihren Pfad trägt.
        // Der gemessene Anlass (16.11.2026, jf-test) war eine Datei, deren Pfad ein Symlink ohne Ziel
        // ist — `GetMediaHash` liefert dann null.
        const string fm517Pfad =
            "/opt/data/jf-test/media/middle381/The Middle/Season 02/The Middle.S02E01.MULTI.1080p.WEB-DL.H265-FTMVHD.mkv";
        const string fm517Hash = "0deccedafea83e1a";
        string fm517Root = Path.Combine(Path.GetTempPath(), "tvst-fm517-" + Environment.ProcessId);
        try
        {
            if (Directory.Exists(fm517Root))
            {
                Directory.Delete(fm517Root, true);
            }

            Directory.CreateDirectory(fm517Root);

            // (a) Zeile vorhanden, Hash nicht: schreiben über den Pfad, Zeilenzahl unverändert.
            var listenSeite = new CountingHandler { ReplyBody = Fixture("add-tvshow-select.html") };
            using (var db = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(
                Path.Combine(fm517Root, "mit-zeile"), null))
            {
                var reg = new Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry(db, null, null);
                reg.EnsureMedia(fm517Hash, jellyfinItemId: null, path: fm517Pfad);
                int vorher = db.Media.Count();

                var aufloeser = new TvstShowResolver(new TvSubtitlesClient(new HttpClient(listenSeite)), null);
                var ohneHash = await aufloeser
                    .ResolveForMediaAsync(
                        reg,
                        mediaHash: null,
                        new TmdbSeriesInfo("The Middle", "The Middle", 2009, null),
                        dryRun: false,
                        CancellationToken.None,
                        mediaPath: fm517Pfad)
                    .ConfigureAwait(false);

                int nachher = db.Media.Count();
                Check("ohne Medien-Hash, aber mit Pfad-Zeile: die gefundene Seriennummer steht danach in der Zeile (F-M517)",
                    ohneHash.Resolved && ohneHash.ShowId == 512 && ohneHash.Status == TvstShowStatus.Found
                    && db.Media.FindById(fm517Hash)?.TvstShowId == "512"
                    && reg.TvstShowIdAtPath(fm517Pfad) == "512"
                    && nachher == vorher && vorher == 1,
                    "id=" + (db.Media.FindById(fm517Hash)?.TvstShowId ?? "<null>")
                    + " zeilen=" + vorher + "->" + nachher);

                var zweite = await aufloeser
                    .ResolveForMediaAsync(
                        reg,
                        mediaHash: null,
                        new TmdbSeriesInfo("The Middle", "The Middle", 2009, null),
                        dryRun: false,
                        CancellationToken.None,
                        mediaPath: fm517Pfad)
                    .ConfigureAwait(false);
                Check("die über den Pfad gespeicherte Seriennummer wird beim naechsten Mal gelesen, nicht neu gesucht (F-M517)",
                    zweite.Status == TvstShowStatus.Stored && zweite.ShowId == 512 && listenSeite.Requests == 1,
                    "status=" + zweite.Status + " requests=" + listenSeite.Requests);
            }

            // (b) Keine Zeile, kein Hash: nichts wird geschrieben, und der Text sagt warum.
            var zweiteListe = new CountingHandler { ReplyBody = Fixture("add-tvshow-select.html") };
            using (var dbLeer = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(
                Path.Combine(fm517Root, "ohne-zeile"), null))
            {
                var regLeer = new Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry(dbLeer, null, null);
                var ohneZeile = await new TvstShowResolver(new TvSubtitlesClient(new HttpClient(zweiteListe)), null)
                    .ResolveForMediaAsync(
                        regLeer,
                        mediaHash: null,
                        new TmdbSeriesInfo("The Middle", "The Middle", 2009, null),
                        dryRun: false,
                        CancellationToken.None,
                        mediaPath: "/opt/data/jf-test/media/middle381/Ohne Zeile/S01E01.mkv")
                    .ConfigureAwait(false);
                Check("ohne Hash UND ohne Pfad-Zeile: nichts geschrieben, die Nummer gilt nur fuer diesen Lauf (F-M517)",
                    ohneZeile.Resolved && ohneZeile.ShowId == 512
                    && dbLeer.Media.Count() == 0
                    && MediaNumberNote.Of(Jellyfin.Plugin.SubScribe.Registry.MediaNumberWrite.None) == MediaNumberNote.NotStored,
                    "zeilen=" + dbLeer.Media.Count());
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(fm517Root))
                {
                    Directory.Delete(fm517Root, true);
                }
            }
            catch (IOException)
            {
                // Ein haengender SQLite-Handle darf die Suite nicht umbringen; das Verzeichnis liegt im Temp.
            }
        }

        // ---- 7. opt-in live run: the only part that touches the site ----------------------------
        // Not a check, a probe: it prints what the live site answers so a protocol change shows up as
        // a visible difference instead of a red suite on somebody else's bad day. Run with --live.
        if (args.Contains("--live"))
        {
            Console.WriteLine();
            Console.WriteLine("--- live probe against tvsubtitles.net ---");
            var liveHttp = ScribeApiClient.NewHttpClient(TimeSpan.FromSeconds(60));
            var live = new TvSubtitlesClient(liveHttp);
            var liveShows = await live.ShowsAsync().ConfigureAwait(false);
            var middle = TvSubtitlesClient.ResolveShow(liveShows, "The Middle");
            Console.WriteLine($"  shows: {liveShows.Count}, The Middle -> {middle}");
            var liveSearch = await live.SearchShowsAsync("The Middle").ConfigureAwait(false);
            Console.WriteLine($"  search \"The Middle\": {liveSearch.Count} hit(s), narrow resolve -> "
                + $"{TvSubtitlesClient.ResolveShow(liveSearch, "The Middle")}");
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

        // ---- Season in the fixture, judged by what the site actually serves -----------------------
        // Operator 17.11.2026: "Staffel ins fixture und schauen das tvst das richtige macht". The
        // pages below are season 1 of The Middle (show 512), captured page by page on 17.11.2026 and
        // stored byte for byte. Nothing here builds its own HTML: the parser is asked the questions a
        // real run asks, and the release column answers the one that was wrong until F-M505 — the site
        // prints the uploader's GROUP there, not a file name. The single entry that does carry a file
        // name is our own upload from before that fix, and it is the reason the group is sent.
        var seasonDir = Path.Combine(_fixtures, "season-middle-s01");
        var seasonPage = await File.ReadAllTextAsync(Path.Combine(seasonDir, "tvshow-512-1.html")).ConfigureAwait(false);
        var seasonEpisodes = TvSubtitlesClient.ParseEpisodePages(seasonPage);
        Check("the captured season page lists 24 episode pages", seasonEpisodes.Count == 24, $"{seasonEpisodes.Count}");
        Check("every listed episode page is in the fixture",
            seasonEpisodes.All(id => File.Exists(Path.Combine(seasonDir, $"episode-{id}.html"))));
        Check("the season page names season 1 and no other",
            TvSubtitlesClient.ParseSeasons(seasonPage, 512).Contains(1));

        var labels = new List<(int Season, int Episode)>();
        foreach (var id in seasonEpisodes)
        {
            var page = await File.ReadAllTextAsync(Path.Combine(seasonDir, $"episode-{id}.html")).ConfigureAwait(false);
            labels.Add(TvSubtitlesClient.ParseEpisodeLabel(page));
        }

        Check("each episode page names its own episode number, 1 to 24 exactly once",
            labels.All(l => l.Season == 1)
            && labels.Select(l => l.Episode).Distinct().Count() == 24
            && labels.Min(l => l.Episode) == 1 && labels.Max(l => l.Episode) == 24,
            string.Join(",", labels.Select(l => $"{l.Season}x{l.Episode}")));

        // 1x05 holds ten subtitles: nine from other uploaders, one of ours.
        var episode05 = TvSubtitlesClient.ParseEntries(
            await File.ReadAllTextAsync(Path.Combine(seasonDir, "episode-22461.html")).ConfigureAwait(false));
        Check("1x05 carries ten subtitle entries", episode05.Count == 10, $"{episode05.Count}");
        Check("the release cells carry uploader groups — only our pre-F-M505 entry carries a file name",
            episode05.Count(e => e.Release == "FEVER") == 3
            && episode05.Count(e => e.Release == "REWARD") == 1
            && episode05.Count(e => e.Release.Length == 0) == 5
            && episode05.Count(e => e.Release.StartsWith("The Middle.S01E05", StringComparison.Ordinal)) == 1,
            string.Join(" | ", episode05.Select(e => $"{e.Language}:{e.Rip}:{e.Release}")));
        Check("the rip cells stay the site's own vocabulary",
            episode05.Count(e => e.Rip == "HDTV") == 8
            && episode05.Count(e => e.Rip == "WEB") == 1
            && episode05.Count(e => e.Rip == "DVDRip") == 1,
            string.Join(" | ", episode05.Select(e => e.Rip)));
        Check("languages come back as the site's two-letter codes",
            episode05.Count(e => e.Language == "fr") == 2
            && episode05.Count(e => e.Language == "en") == 1
            && episode05.Any(e => e.Language == "ro"),
            string.Join(",", episode05.Select(e => e.Language)));

        // ---- F-M522: die Serienliste wird einmal je Lauf geholt ----------------------------------
        // Gemessen auf prod am 16.11.2026: ein Download-Lauf fragte /add.html 53 mal nach einer Liste,
        // die sich waehrend des Laufs nicht aendern kann. Der Zwischenspeicher haengt am Client, und ein
        // Client lebt einen Lauf lang — mehr soll er nicht.
        string listenHtml = await File.ReadAllTextAsync(
            Path.Combine(_fixtures, "add-tvshow-select.html")).ConfigureAwait(false);
        var cacheHandler = new CountingHandler { ReplyBody = listenHtml };
        var cacheClient = new TvSubtitlesClient(new HttpClient(cacheHandler));
        var liste1 = await cacheClient.ShowsAsync().ConfigureAwait(false);
        var liste2 = await cacheClient.ShowsAsync().ConfigureAwait(false);
        Check(
            "F-M522: the show list is fetched once and answered from memory the second time",
            liste1.Count > 0 && liste2.Count == liste1.Count && cacheHandler.Requests == 1,
            "Eintraege " + liste1.Count + ", Anfragen " + cacheHandler.Requests.ToString(CultureInfo.InvariantCulture));

        var leerHandler = new CountingHandler { ReplyBody = "<html><body>kein select</body></html>" };
        var leerClient = new TvSubtitlesClient(new HttpClient(leerHandler));
        await leerClient.ShowsAsync().ConfigureAwait(false);
        await leerClient.ShowsAsync().ConfigureAwait(false);
        Check(
            "F-M522: an empty answer is never remembered — one bad page must not decide the whole run",
            leerHandler.Requests == 2,
            "Anfragen " + leerHandler.Requests.ToString(CultureInfo.InvariantCulture));

        // Ein echtes Anlegen aendert die Liste: der Speicher faellt, die naechste Frage geht wieder raus.
        var neuHandler = new CountingHandler { ReplyBody = listenHtml, ReplyStatus = HttpStatusCode.OK };
        var neuClient = new TvSubtitlesClient(new HttpClient(neuHandler));
        await neuClient.ShowsAsync().ConfigureAwait(false);
        var anlegen = new TvSubtitlesAddShow(new[]
        {
            new KeyValuePair<string, string>("tvshow", "The Middle"),
            new KeyValuePair<string, string>("submit", "Add"),
        });
        await neuClient.AddShowAsync(anlegen, dryRun: false).ConfigureAwait(false);
        await neuClient.ShowsAsync().ConfigureAwait(false);
        Check(
            "F-M522: adding a show drops the remembered list, so its new id is seen",
            neuHandler.Requests == 3,
            "Anfragen " + neuHandler.Requests.ToString(CultureInfo.InvariantCulture));

        // ---- F-M523: die aufgeloeste Seriennummer erreicht den Aufrufer ---------------------------
        // Vorher loeste der Download die Nummer bei jeder Suche neu auf und schrieb sie nie; nur der
        // Upload-Weg schrieb sie. Deshalb holte jeder Lauf die Serienliste erneut (F-M501).
        string staffelSeite = await File.ReadAllTextAsync(
            Path.Combine(_fixtures, "season-middle-s01", "tvshow-512-1.html")).ConfigureAwait(false);
        var fm523Handler = new CountingHandler
        {
            BodyFor = url => url.Contains("/add.html", StringComparison.Ordinal) ? listenHtml
                : url.Contains("/tvshow-", StringComparison.Ordinal) ? staffelSeite
                : url.Contains("/episode-", StringComparison.Ordinal)
                    ? File.ReadAllText(Path.Combine(
                        _fixtures,
                        "season-middle-s01",
                        url[(url.LastIndexOf('/') + 1)..].Split('?')[0]))
                    : "<html></html>",
        };
        var fm523Client = new TvSubtitlesClient(new HttpClient(fm523Handler));
        var fm523Endpoint = new TvSubtitlesDownloadEndpoint(fm523Client);
        await fm523Endpoint.SearchAsync(new SubtitleQuery("The Middle", null, null, 1, 5, "EN"))
            .ConfigureAwait(false);
        Check(
            "F-M523: the download search reports the show number it resolved",
            fm523Client.LastResolvedShowId == 512,
            fm523Client.LastResolvedShowId?.ToString(CultureInfo.InvariantCulture) ?? "null");

        await fm523Endpoint.SearchAsync(new SubtitleQuery("Gibt es nicht", null, null, 1, 5, "EN"))
            .ConfigureAwait(false);
        Check(
            "F-M523: a search that resolves nothing clears the number — no stale id from the last title",
            fm523Client.LastResolvedShowId is null,
            fm523Client.LastResolvedShowId?.ToString(CultureInfo.InvariantCulture) ?? "null");

        string dlQuelle = await File.ReadAllTextAsync(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Pipeline", "DownloadPipeline.cs")).ConfigureAwait(false);
        Check(
            "F-M523: the download writes the number — same path as the film id, nothing on a dry run",
            dlQuelle.Contains("Registry.SetTvstShowId(mediaHash, mediaPath, tvstId)", StringComparison.Ordinal)
            && dlQuelle.Contains("Registry.TvstShowIdAtPath(mediaPath)", StringComparison.Ordinal)
            && dlQuelle.Contains("&& !_config.DryRun", StringComparison.Ordinal));

        // ---- F-M543: die Folgenseite wird je Lauf einmal gesucht, nicht je Anfrage -----------------
        var suchHandler = new SeasonFixtureHandler();
        var suchClient = new TvSubtitlesClient(new HttpClient(suchHandler));
        var seite3 = await suchClient.FindEpisodePageAsync(512, 9, 3).ConfigureAwait(false);
        Check("F-M543: the episode page is found from the season list", seite3 == 9003,
            seite3?.ToString(CultureInfo.InvariantCulture) ?? "null");
        int epAnfragenNachErster = suchHandler.Episodes;
        int staffelAnfragen = suchHandler.Season;
        var nochmal = await suchClient.FindEpisodePageAsync(512, 9, 3).ConfigureAwait(false);
        Check("F-M543: the same question costs no second request",
            nochmal == seite3 && suchHandler.Episodes == epAnfragenNachErster && suchHandler.Season == staffelAnfragen,
            suchHandler.Episodes.ToString(CultureInfo.InvariantCulture) + " episode, " + suchHandler.Season.ToString(CultureInfo.InvariantCulture) + " season");
        var seite5 = await suchClient.FindEpisodePageAsync(512, 9, 5).ConfigureAwait(false);
        Check("F-M543: a later episode of the same season does not walk the list again",
            seite5 == 9005 && suchHandler.Season == 1 && suchHandler.Episodes - epAnfragenNachErster == 2,
            "season " + suchHandler.Season.ToString(CultureInfo.InvariantCulture) + ", +"
                + (suchHandler.Episodes - epAnfragenNachErster).ToString(CultureInfo.InvariantCulture) + " episode");
        suchClient.InvalidateSeasonPages(512, 9);
        await suchClient.FindEpisodePageAsync(512, 9, 1).ConfigureAwait(false);
        Check("F-M543: after our own create the season list is read again, the labels are not",
            suchHandler.Season == 2, suchHandler.Season.ToString(CultureInfo.InvariantCulture));

        Console.WriteLine();
        // ---- F-M533: Robustheit und Meldeweg der Seite ------------------------------------------
        var gut = new Jellyfin.Plugin.SubScribe.Api.TvSubtitlesEntry("123", "de", "WEB", "FTMVHD", "3 days");
        Check("F-M533: a documented entry is not a finding",
            Jellyfin.Plugin.SubScribe.Api.TvstMetadataChecks.CheckEntry(gut).Count == 0);

        var falscheSprache = new Jellyfin.Plugin.SubScribe.Api.TvSubtitlesEntry("123", "xx", "WEB", "", "3 days");
        Check("F-M533: an unknown language code is a finding",
            Jellyfin.Plugin.SubScribe.Api.TvstMetadataChecks.CheckEntry(falscheSprache)
                .Any(f => f.Path == "language"));

        var falscherRip = new Jellyfin.Plugin.SubScribe.Api.TvSubtitlesEntry("123", "de", "WEB-DL", "", "3 days");
        Check("F-M533: a rip outside the site's seven values is a finding",
            Jellyfin.Plugin.SubScribe.Api.TvstMetadataChecks.CheckEntry(falscherRip)
                .Any(f => f.Path == "rip"));

        Check("F-M533: an empty rip is allowed (the site's own normal case)",
            Jellyfin.Plugin.SubScribe.Api.TvstMetadataChecks
                .CheckEntry(new Jellyfin.Plugin.SubScribe.Api.TvSubtitlesEntry("123", "de", "", "", ""))
                .Count == 0);

        Check("F-M533: an episode label outside 1–22 / 0–100 is a finding",
            Jellyfin.Plugin.SubScribe.Api.TvstMetadataChecks.CheckEpisodeLabel(26, 5)
                .Any(f => f.Path == "season")
            && Jellyfin.Plugin.SubScribe.Api.TvstMetadataChecks.CheckEpisodeLabel(3, 140)
                .Any(f => f.Path == "episode"));
        Check("F-M533: a normal episode label is not a finding",
            Jellyfin.Plugin.SubScribe.Api.TvstMetadataChecks.CheckEpisodeLabel(3, 9).Count == 0);

        string tvstClient = await File.ReadAllTextAsync(
            System.IO.Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Api", "TvSubtitlesClient.cs"))
            .ConfigureAwait(false);
        Check("F-M533: the site's own report form is the channel (report_bad.php, sid + message)",
            tvstClient.Contains("/report_bad.php", StringComparison.Ordinal)
            && tvstClient.Contains("\"sid\"", StringComparison.Ordinal)
            && tvstClient.Contains("ReportBadAsync", StringComparison.Ordinal));

        // ── F-M537: the client rides along on every request to the site ─────────────────
        // Operator order 16.11.2026: „Dann schicken wir doch einfach den Client an tvst mit."
        var version = typeof(TvSubtitlesClient).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        Check("F-M537: the client tag names the product and the real assembly version",
            TvSubtitlesClient.SiteClientQuery == "sub-scribe-" + version, TvSubtitlesClient.SiteClientQuery);
        Check("F-M537: a URL without a query gets ?client=",
            TvSubtitlesClient.WithClient("https://www.tvsubtitles.net/episode-1.html")
                == "https://www.tvsubtitles.net/episode-1.html?client=" + TvSubtitlesClient.SiteClientQuery,
            TvSubtitlesClient.WithClient("https://www.tvsubtitles.net/episode-1.html"));
        Check("F-M537: a URL that already has a query gets &client=",
            TvSubtitlesClient.WithClient("https://www.tvsubtitles.net/search2.php?qs=Invasion")
                == "https://www.tvsubtitles.net/search2.php?qs=Invasion&client=" + TvSubtitlesClient.SiteClientQuery,
            TvSubtitlesClient.WithClient("https://www.tvsubtitles.net/search2.php?qs=Invasion"));
        Check("F-M537: every request builder goes through the tagging helper",
            System.Text.RegularExpressions.Regex.Matches(tvstClient, "new HttpRequestMessage\\(HttpMethod\\.(Get|Post), (\\w+)\\)")
                .All(m => m.Groups[2].Value == "ziel")
            && tvstClient.Contains("var ziel = WithClient(url);", StringComparison.Ordinal)
            && tvstClient.Contains("var ziel = WithClient(AddShowPostUrl);", StringComparison.Ordinal));

        // A real call through the client must show the parameter on the wire, not just in a helper.
        var tagHandler = new CountingHandler { ReplyStatus = System.Net.HttpStatusCode.OK, ReplyBody = "<html></html>" };
        var tagClient = new TvSubtitlesClient(new HttpClient(tagHandler));
        await tagClient.SearchShowsAsync("Weissensee", default).ConfigureAwait(false);
        Check("F-M537: a real search reaches the site with client= in the URL",
            tagHandler.LastUrl?.Contains("client=" + TvSubtitlesClient.SiteClientQuery, StringComparison.Ordinal) == true,
            tagHandler.LastUrl);


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

    /// <summary>
    /// F-M641: ein Zip mit den genannten Eintraegen — der Pack, den die Seite bekommt. Mehrere
    /// Eintraege mit demselben Namen sind Absicht: sie belegen, dass die Folgenliste ohne Dubletten
    /// gelesen wird.
    /// </summary>
    /// <param name="entries">Dateiname und Inhalt je Eintrag.</param>
    /// <returns>Die Zip-Bytes.</returns>
    private static byte[] ZipOf(params (string Name, string Text)[] entries)
    {
        using var puffer = new MemoryStream();
        using (var archiv = new ZipArchive(puffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var eintrag = archiv.CreateEntry(name).Open();
                var bytes = Encoding.UTF8.GetBytes(text);
                eintrag.Write(bytes, 0, bytes.Length);
            }
        }

        return puffer.ToArray();
    }

    private static TvSubtitlesUpload? BuildOk(byte[] data, bool doubleEpisode = false, bool hearingImpaired = false) =>
        TvSubtitlesClient.BuildUpload(
            512, 2, 24, "de", "The Middle - 2x24.srt", data, "HDTV", "FEVER",
            1422, "tt1442464", null, doubleEpisode, hearingImpaired);

    /// <summary>
    /// F-M516: die Feldnamen des Anlege-Formulars, so wie sie im echten HTML stehen — nur innerhalb des
    /// Formulars (die Seite trägt auch die Suchleiste und eine Sprachauswahl), und ohne den
    /// Absende-Knopf, der keinen Namen hat.
    /// </summary>
    /// <param name="html">Die Seite mit dem Formular.</param>
    /// <returns>Die Feldnamen in der Reihenfolge des Formulars.</returns>
    private static List<string> FormFieldNames(string html)
    {
        var start = html.IndexOf("action=\"/add-tvshow1.php\"", StringComparison.Ordinal);
        var end = html.IndexOf("</form>", start, StringComparison.Ordinal);
        var block = html[start..end];
        return System.Text.RegularExpressions.Regex
            .Matches(block, "<input[^>]*name=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

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
    /// <summary>
    /// F-M543: eine Staffel mit fuenf Folgen 9x01-9x05 und ein Zaehler fuer jede Anfrage. Die Seiten
    /// entstehen hier, damit die Reihenfolge feststeht — die echten Fixtures laufen neueste zuerst.
    /// </summary>
    private sealed class SeasonFixtureHandler : HttpMessageHandler
    {
        public int Season { get; private set; }

        public int Episodes { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            string body;
            if (name.StartsWith("tvshow-", StringComparison.Ordinal))
            {
                Season++;
                body = "<html><body>"
                    + string.Concat(System.Linq.Enumerable.Range(1, 5)
                        .Select(n => "<a href=\"/episode-" + (9000 + n).ToString(CultureInfo.InvariantCulture) + ".html\">E"
                            + n.ToString(CultureInfo.InvariantCulture) + "</a>"))
                    + "</body></html>";
            }
            else
            {
                Episodes++;
                var nummer = int.Parse(System.Text.RegularExpressions.Regex.Match(name, @"episode-(\d+)").Groups[1].Value, CultureInfo.InvariantCulture) - 9000;
                body = "<html><body><h2>Testserie 9x" + nummer.ToString("00", CultureInfo.InvariantCulture) + "</h2></body></html>";
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public string? LastUrl { get; private set; }

        public string? LastReferer { get; private set; }

        public string? LastContentType { get; private set; }

        /// <summary>F-M516: der Rumpf der letzten Anfrage, damit eine Pruefung die Teile zaehlen kann.</summary>
        public string LastBodyText { get; private set; } = string.Empty;

        /// <summary>F-M516: der Status, mit dem geantwortet wird (Vorgabe 200).</summary>
        public HttpStatusCode ReplyStatus { get; set; } = HttpStatusCode.OK;

        /// <summary>F-M523: Rumpf je URL — eine ganze Suche braucht Liste, Staffel- und Folgenseite.</summary>
        public Func<string, string>? BodyFor { get; set; }

        /// <summary>F-M516: der Rumpf der Antwort (Vorgabe „ok“).</summary>
        public string ReplyBody { get; set; } = "<html><b>ok</b>\n</html>";

        /// <summary>F-M516: der <c>Location</c>-Kopf, wenn die Antwort eine Weiterleitung ist.</summary>
        public string? ReplyLocation { get; set; }

        public int RepliedWith { get; private set; } = 200;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastUrl = request.RequestUri?.ToString();
            LastReferer = request.Headers.Referrer?.ToString();
            LastContentType = request.Content?.Headers.ContentType?.ToString();
            if (request.Content is not null)
            {
                LastBodyText = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            await Task.Yield();
            RepliedWith = (int)ReplyStatus;
            var body = BodyFor is null ? ReplyBody : BodyFor(LastUrl ?? string.Empty);
            var response = new HttpResponseMessage(ReplyStatus)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html"),
            };
            if (ReplyLocation is not null)
            {
                response.Headers.Location = new Uri(ReplyLocation);
            }

            return response;
        }
    }
}
