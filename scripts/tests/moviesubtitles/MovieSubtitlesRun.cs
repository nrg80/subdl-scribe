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

// T228 / F-M506 (Etappe 1, 15.11.2026): the moviesubtitles.org client is driven with the pages the
// site actually served (fixtures/, captured 15.11.2026), never with the live site — a suite that
// touches the network cannot tell a broken parser from a broken day on their side.
//
// Four halves:
//   1. the SITE'S OWN TABLES: 13 languages, nine rips — and the proof that neither table is the
//      tvsubtitles one;
//   2. the RESOLVER: inverted titles, the year, and no guessing when two films match;
//   3. the PARSERS against real HTML: three list shapes, the film page (language from the flag),
//      the entry page, the relative redirect target;
//   4. the UPLOAD: which inputs are refused, in which ORDER the parts go out, that the author field is
//      absent when empty, that `release` carries the group and not the file name, and that a dry run
//      builds a body and sends nothing.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Api;
using Jellyfin.Plugin.SubScribe.Api.Endpoints;
using Jellyfin.Plugin.SubScribe.Configuration;

internal static class Program
{
    private static int _failures;
    private static int _checks;

    private static string _fixtures = string.Empty;

    private static async Task<int> Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";
        _fixtures = Path.Combine(root, "scripts/tests/moviesubtitles/fixtures");

        // ---- 1. the two tables are the SITE's, and neither is the sister's -----------------------
        Check("13 languages are offered, as many as the form's select carries",
            MovieSubtitlesClient.Languages.Count == 13,
            "counted " + MovieSubtitlesClient.Languages.Count);
        Check("the display names keep the site's own spelling",
            MovieSubtitlesClient.Languages["br"].Name == "portugese(br)"
            && MovieSubtitlesClient.Languages["ua"].Name == "ukrainian"
            && MovieSubtitlesClient.Languages["gr"].Name == "greek"
            && MovieSubtitlesClient.Languages["en"].Name == "english",
            "found br=" + MovieSubtitlesClient.Languages["br"].Name);
        Check("the table is NOT the tvsubtitles one (13 against 24, no pt, no cz/cn/jp)",
            TvSubtitlesClient.Languages.Count == 24
            && !MovieSubtitlesClient.Languages.ContainsKey("pt")
            && !MovieSubtitlesClient.Languages.ContainsKey("cz")
            && !MovieSubtitlesClient.Languages.ContainsKey("cn")
            && !MovieSubtitlesClient.Languages.ContainsKey("jp")
            && MovieSubtitlesClient.Languages["br"].FileCode == "br"
            && TvSubtitlesClient.Languages["br"].Iso3 == "pob",
            "the sister carries 24 codes and an ISO3 column; this site carries 13 and a two-letter one");
        // Das Auswahlfeld traegt ACHT benannte Werte und den leeren zweimal (erster und letzter Eintrag) —
        // neun verschiedene Werte, wenn man den leeren mitzaehlt. Das Protokoll spricht von „neun Werte,
        // dazu der leere Fall"; die Seite zaehlt den leeren mit. Die Tabelle folgt der Seite.
        Check("the rip list is the select's nine distinct values (eight names plus the empty), in form order",
            MovieSubtitlesClient.Rips.Count == 9
            && MovieSubtitlesClient.Rips.Take(8).SequenceEqual(new[]
            {
                "DVDRip", "BluRay", "BrRip", "BDRip", "HDRip", "WEB", "WEBRip", "WEB-DL",
            })
            && MovieSubtitlesClient.Rips[8] == string.Empty,
            "found " + MovieSubtitlesClient.Rips.Count + ": " + string.Join(",", MovieSubtitlesClient.Rips));
        Check("the rip list is NOT the tvsubtitles one (HDTV/HEVC/DVDSCR exist only there)",
            TvSubtitlesClient.Rips.Contains("HDTV") && TvSubtitlesClient.Rips.Contains("HEVC")
            && !MovieSubtitlesClient.Rips.Contains("HDTV") && !MovieSubtitlesClient.Rips.Contains("HEVC")
            && MovieSubtitlesClient.Rips.Contains("WEB-DL") && !TvSubtitlesClient.Rips.Contains("WEB-DL"));

        // ---- 1b. ISO -> the site's codes, and only what the 13 know (F-M506) ---------------------
        Check("the site's own codes stay what they are",
            MovieSubtitlesClient.Languages.Keys.All(k => MovieSubtitlesClient.SiteLanguage(k) == k),
            "a code the page carries must not be rewritten");
        Check("the ISO-639 forms the 13 know are mapped",
            MovieSubtitlesClient.SiteLanguage("eng") == "en"
            && MovieSubtitlesClient.SiteLanguage("spa") == "es"
            && MovieSubtitlesClient.SiteLanguage("fra") == "fr" && MovieSubtitlesClient.SiteLanguage("fre") == "fr"
            && MovieSubtitlesClient.SiteLanguage("deu") == "de" && MovieSubtitlesClient.SiteLanguage("ger") == "de"
            && MovieSubtitlesClient.SiteLanguage("pob") == "br"
            && MovieSubtitlesClient.SiteLanguage("rus") == "ru"
            && MovieSubtitlesClient.SiteLanguage("uk") == "ua" && MovieSubtitlesClient.SiteLanguage("ukr") == "ua"
            && MovieSubtitlesClient.SiteLanguage("ita") == "it"
            && MovieSubtitlesClient.SiteLanguage("el") == "gr" && MovieSubtitlesClient.SiteLanguage("ell") == "gr"
            && MovieSubtitlesClient.SiteLanguage("gre") == "gr"
            && MovieSubtitlesClient.SiteLanguage("ara") == "ar"
            && MovieSubtitlesClient.SiteLanguage("hun") == "hu"
            && MovieSubtitlesClient.SiteLanguage("pol") == "pl"
            && MovieSubtitlesClient.SiteLanguage("tur") == "tr",
            "found: ukr→" + MovieSubtitlesClient.SiteLanguage("ukr")
            + ", gre→" + MovieSubtitlesClient.SiteLanguage("gre")
            + ", ger→" + MovieSubtitlesClient.SiteLanguage("ger"));
        Check("pt-BR is the one region that is a language of its own — pt-PT is refused, not guessed",
            MovieSubtitlesClient.SiteLanguage("pt-BR") == "br"
            && MovieSubtitlesClient.SiteLanguage("pt_BR") == "br"
            && MovieSubtitlesClient.SiteLanguage("pt-PT") is null
            && MovieSubtitlesClient.SiteLanguage("pt") is null
            && MovieSubtitlesClient.SiteLanguage("por") is null,
            "the site carries ONE Portuguese (the Brazilian one); por→" + (MovieSubtitlesClient.SiteLanguage("por") ?? "<null>"));
        Check("a language the 13 do not carry is refused instead of falling back to English",
            MovieSubtitlesClient.SiteLanguage("nl") is null && MovieSubtitlesClient.SiteLanguage("dut") is null
            && MovieSubtitlesClient.SiteLanguage("sv") is null && MovieSubtitlesClient.SiteLanguage("da") is null
            && MovieSubtitlesClient.SiteLanguage("fi") is null && MovieSubtitlesClient.SiteLanguage("ko") is null
            && MovieSubtitlesClient.SiteLanguage("zh") is null && MovieSubtitlesClient.SiteLanguage("zho") is null
            && MovieSubtitlesClient.SiteLanguage("chi") is null
            && MovieSubtitlesClient.SiteLanguage("ja") is null && MovieSubtitlesClient.SiteLanguage("jpn") is null
            && MovieSubtitlesClient.SiteLanguage("bg") is null
            && MovieSubtitlesClient.SiteLanguage("cs") is null && MovieSubtitlesClient.SiteLanguage("ces") is null
            && MovieSubtitlesClient.SiteLanguage("cze") is null
            && MovieSubtitlesClient.SiteLanguage("ro") is null && MovieSubtitlesClient.SiteLanguage("rum") is null
            && MovieSubtitlesClient.SiteLanguage("xx") is null && MovieSubtitlesClient.SiteLanguage("   ") is null
            && MovieSubtitlesClient.SiteLanguage(string.Empty) is null && MovieSubtitlesClient.SiteLanguage(null) is null,
            "found: zh→" + (MovieSubtitlesClient.SiteLanguage("zh") ?? "<null>")
            + ", cs→" + (MovieSubtitlesClient.SiteLanguage("cs") ?? "<null>")
            + ", xx→" + (MovieSubtitlesClient.SiteLanguage("xx") ?? "<null>"));
        Check("case, whitespace and a script suffix do not matter",
            MovieSubtitlesClient.SiteLanguage("DE") == "de"
            && MovieSubtitlesClient.SiteLanguage("DeU") == "de"
            && MovieSubtitlesClient.SiteLanguage("  GRE  ") == "gr"
            && MovieSubtitlesClient.SiteLanguage("Pt-Br") == "br"
            && MovieSubtitlesClient.SiteLanguage("zh-Hans") is null
            && MovieSubtitlesClient.SiteLanguage("uk-UA") == "ua");

        // ---- 1c. the rip bridge ------------------------------------------------------------------
        Check("every value of the form maps from its own spelling",
            MovieSubtitlesClient.Rips.Where(r => r.Length > 0)
                .All(r => MovieSubtitlesClient.RipFromQuality(r) == r),
            "found: " + string.Join(",", MovieSubtitlesClient.Rips.Take(9)
                .Select(r => r + "→" + MovieSubtitlesClient.RipFromQuality(r))));
        Check("WEB-DL stays WEB-DL and does not collapse into WEB",
            MovieSubtitlesClient.RipFromQuality("Oppenheimer.2023.WEB-DL.x265") == "WEB-DL"
            && MovieSubtitlesClient.RipFromQuality("WEBRip") == "WEBRip"
            && MovieSubtitlesClient.RipFromQuality("WEB") == "WEB"
            && MovieSubtitlesClient.RipFromQuality("BluRay.x265-GROUP") == "BluRay",
            "source before codec: found " + MovieSubtitlesClient.RipFromQuality("Oppenheimer.2023.WEB-DL.x265"));
        // DIE RIP-FRAGE: die Seite kennt KEINEN Codec. tvst zieht x265 auf HEVC, weil sein Auswahlfeld
        // HEVC fuehrt; hier gibt es diesen Wert nicht, und ein Codec als Rip waere eine Behauptung ueber
        // die Quelle, die die Seite nicht macht. Ein Codec allein laesst das Feld deshalb LEER (die
        // Seite speichert dann keinen Rip — ein erlaubter Wert), waehrend die QUELLE im selben Namen
        // weiter greift ("BluRay.x265" → BluRay).
        Check("a codec alone is NO rip here (x265 is not HEVC — the site has no codec)",
            MovieSubtitlesClient.RipFromQuality("x265") == string.Empty
            && MovieSubtitlesClient.RipFromQuality("1080p.x265.BONE") == string.Empty
            && MovieSubtitlesClient.RipFromQuality("HEVC") == string.Empty
            && MovieSubtitlesClient.RipFromQuality("H.265") == string.Empty,
            "found x265→'" + MovieSubtitlesClient.RipFromQuality("x265") + "'");
        Check("a source the site does not carry stays empty instead of borrowing another value",
            MovieSubtitlesClient.RipFromQuality("HDTV") == string.Empty
            && MovieSubtitlesClient.RipFromQuality("DVDSCR") == string.Empty
            && MovieSubtitlesClient.RipFromQuality("1080p") == string.Empty
            && MovieSubtitlesClient.RipFromQuality(null) == string.Empty
            && MovieSubtitlesClient.RipFromQuality("  ") == string.Empty,
            "HDTV→'" + MovieSubtitlesClient.RipFromQuality("HDTV") + "'");

        // ---- 2. the resolver: inverted titles, the year, no guessing ------------------------------
        Check("the title key undoes the article inversion of the site",
            MovieSubtitlesClient.TitleKey("Matrix, The") == MovieSubtitlesClient.TitleKey("The Matrix")
            && MovieSubtitlesClient.TitleKey("Old Man and the Sea, The")
                == MovieSubtitlesClient.TitleKey("The Old Man and the Sea")
            && MovieSubtitlesClient.TitleKey("Ocean's Thirteen") == "ocean's thirteen",
            "found: " + MovieSubtitlesClient.TitleKey("Matrix, The"));
        Check("the title key uses the same three adjustments as the series resolver (case, &/and/und, space)",
            MovieSubtitlesClient.TitleKey("Fast  &   Furious") == MovieSubtitlesClient.TitleKey("Fast and Furious")
            && MovieSubtitlesClient.TitleKey("Fast und Furious") == MovieSubtitlesClient.TitleKey("Fast and Furious")
            && MovieSubtitlesClient.TitleKey("  THE   MATRIX ") == MovieSubtitlesClient.TitleKey("The Matrix")
            && MovieSubtitlesClient.TitleKey(string.Empty).Length == 0,
            "found: " + MovieSubtitlesClient.TitleKey("Fast  &   Furious"));

        var oList = MovieSubtitlesClient.ParseMovieList(Fixture("movies-O.html"));
        Check("the film list of letter O parses into 487 rows (the download view counts only films with entries)",
            oList.Count == 487,
            "parsed " + oList.Count);
        Check("a row carries id, name, year and its flags",
            oList[0].Id == 21254 && oList[0].Name == "O Amor no Divã" && oList[0].Year == 2016
            && oList.First(f => f.Id == 239).Languages.Contains("ua"),
            "first row: " + oList[0]);
        Check("a year repeated inside the name is the year, not part of the name",
            oList.First(f => f.Id == 16856).Name == "O Beautiful"
            && oList.First(f => f.Id == 16856).Year == 2002,
            "found: " + oList.First(f => f.Id == 16856).Name);
        Check("the inverted spelling of the site is kept in the name (the key undoes it)",
            oList.Any(f => f.Id == 15994 && f.Name == "Object of My Affection, The" && f.Year == 1998),
            "an entry ending in ', The' did not survive the parse");

        var dune = MovieSubtitlesClient.ParseMovieList(Fixture("search-dune.html"));
        Check("the 500-answer of search.php still parses into its five films",
            dune.Count == 5 && dune[0].Id == 607 && dune[0].Name == "Dune" && dune[0].Year == 1984,
            "parsed " + dune.Count + ": " + string.Join(" | ", dune.Select(d => d.Name + " (" + d.Year + ")")));
        Check("a bracketed part of a search title is not mistaken for the year",
            dune[4].Name == "Suna no onna (Woman of the Dunes AKA Woman in the Dunes)" && dune[4].Year == 1964,
            "found: " + dune[4].Name + " / " + dune[4].Year);

        var addList = MovieSubtitlesClient.ParseMovieList(Fixture("add-let-O.html"));
        var filtered = MovieSubtitlesClient.ParseMovieList(Fixture("add-filter-oppenheimer.html"));
        Check("the upload view of the same letter parses too — and it is the longer list (492)",
            addList.Count == 492 && addList.Any(f => f.Id == 24811 && f.Name == "Oppenheimer" && f.Year == 2023),
            "parsed " + addList.Count);
        Check("the filter view answers exactly one film",
            filtered.Count == 1 && filtered[0].Id == 24811 && filtered[0].Year == 2023,
            "parsed " + filtered.Count);
        Check("a page that lists nothing yields an empty list instead of throwing",
            MovieSubtitlesClient.ParseMovieList(string.Empty).Count == 0
            && MovieSubtitlesClient.ParseMovieList("<html><body>nope</body></html>").Count == 0);

        Check("Dune by year: 1984 and 2021 are two different films",
            MovieSubtitlesClient.ResolveMovie(dune, "Dune", 1984) == 607
            && MovieSubtitlesClient.ResolveMovie(dune, "Dune: Part One", 2021) == 24043,
            "1984→" + MovieSubtitlesClient.ResolveMovie(dune, "Dune", 1984)
            + ", 2021→" + MovieSubtitlesClient.ResolveMovie(dune, "Dune: Part One", 2021));
        Check("a wrong year is no hit — the year is not a hint",
            MovieSubtitlesClient.ResolveMovie(dune, "Dune", 2022) is null
            && MovieSubtitlesClient.ResolveMovie(dune, "Dune", 1999) is null,
            "2022→" + (MovieSubtitlesClient.ResolveMovie(dune, "Dune", 2022)?.ToString() ?? "<null>"));
        Check("the TMDb spelling finds the site's inverted title",
            MovieSubtitlesClient.ResolveMovie(oList, "The Opposite of Sex", 1998) == 9830
            && MovieSubtitlesClient.ResolveMovie(oList, "The Object of My Affection", 1998) == 15994,
            "The Opposite of Sex→" + (MovieSubtitlesClient.ResolveMovie(oList, "The Opposite of Sex", 1998)?.ToString() ?? "<null>"));
        Check("the comparison is narrow: no substring, no dropped article",
            MovieSubtitlesClient.ResolveMovie(oList, "Object of My Affection", 1998) is null
            && MovieSubtitlesClient.ResolveMovie(dune, "Dun", 1984) is null
            && MovieSubtitlesClient.ResolveMovie(oList, "Obey", 2018) == 21443,
            "an article-less spelling must not match an inverted title");
        var ambiguous = new[]
        {
            new MovieSubtitlesMovie(11, "Dune", 1984, Array.Empty<string>()),
            new MovieSubtitlesMovie(12, "Dune", 1984, Array.Empty<string>()),
        };
        Check("two candidates with the same name and year are NOT guessed",
            MovieSubtitlesClient.ResolveMovie(ambiguous, "Dune", 1984) is null
            && MovieSubtitlesClient.ResolveMovie(ambiguous, "Dune", null) is null
            && MovieSubtitlesClient.ResolveMovie(ambiguous, string.Empty, 1984) is null,
            "a guesshere would put a wrong id into the store");

        // ---- 3. the parsers against the pages the site served -------------------------------------
        var small = MovieSubtitlesClient.ParseMoviePage(Fixture("movie-24811.html"));
        Check("a film page yields its heading and its entries grouped by language",
            small.Name == "Oppenheimer" && small.Year == 2023 && small.Entries.Count == 3
            && small.Sections.SequenceEqual(new[] { "en", "br" }),
            "found " + small.Name + " (" + small.Year + "), " + small.Entries.Count + " entries, sections "
            + string.Join(",", small.Sections));
        var first = small.Entries[0];
        Check("an entry carries the language of its FLAG plus rip, release, parts and id",
            first.SubtitleId == "132989" && first.Language == "en" && first.Rip == "WEB-DL"
            && first.Release == "MA" && first.Parts == 1 && first.Downloads == 315
            && first.Title == "Oppenheimer 2023 english subtitles (WEB-DL.MA)",
            "found: " + first);
        Check("an entry without rip or release in its cells keeps them empty instead of inventing them",
            small.Entries[2].Release == string.Empty && small.Entries[2].Rip == "BluRay"
            && small.Entries[2].Language == "br",
            "found: " + small.Entries[2]);

        var matrix = MovieSubtitlesClient.ParseMoviePage(Fixture("movie-4.html"));
        Check("the biggest film page yields 54 entries in 12 sections (no Ukrainian there)",
            matrix.Entries.Count == 54 && matrix.Sections.Count == 12 && !matrix.Sections.Contains("ua"),
            "found " + matrix.Entries.Count + " entries, " + matrix.Sections.Count + " sections");
        Check("free rip values of old entries survive as written (the nine are a WRITE list only)",
            matrix.Entries.Any(e => e.Rip == "1080p") && matrix.Entries.Any(e => e.Rip == "720p.BlueRay")
            && matrix.Entries.Any(e => e.Rip.Length == 0),
            "found: " + string.Join(",", matrix.Entries.Select(e => e.Rip).Distinct().Take(8)));

        var thirteen = MovieSubtitlesClient.ParseMoviePage(Fixture("movie-239.html"));
        Check("the page with all thirteen languages shows thirteen sections",
            thirteen.Sections.Count == 13 && thirteen.Sections.Contains("ua") && thirteen.Entries.Count == 51,
            "found " + thirteen.Sections.Count + " sections, " + thirteen.Entries.Count + " entries");
        Check("every entry names a language the page actually offers, and every id is unique",
            matrix.Entries.All(e => MovieSubtitlesClient.Languages.ContainsKey(e.Language))
            && thirteen.Entries.All(e => MovieSubtitlesClient.Languages.ContainsKey(e.Language))
            && matrix.Entries.Select(e => e.SubtitleId).Distinct().Count() == matrix.Entries.Count,
            "an entry whose language is unknown must be skipped, not guessed");
        Check("the section heading and the flag of the entry agree — the redundancy is a cross-check",
            thirteen.Entries.All(e => thirteen.Sections.Contains(e.Language)),
            "an entry names a language its section does not");

        var doubleYear = MovieSubtitlesClient.ParseMoviePage(Fixture("movie-16856.html"));
        Check("a year written twice in the heading is read once (O Beautiful (2002) (2002))",
            doubleYear.Name == "O Beautiful" && doubleYear.Year == 2002,
            "found: " + doubleYear.Name + " / " + doubleYear.Year);
        var emptyFilm = MovieSubtitlesClient.ParseMoviePage(Fixture("movie-6928.html"));
        Check("a film without subtitles is a page with no entries, not an error",
            emptyFilm.Entries.Count == 0 && emptyFilm.Sections.Count == 0 && emptyFilm.Year == 1999
            && emptyFilm.Downloads == 0,
            "found " + emptyFilm.Entries.Count + " entries, " + emptyFilm.Downloads + " downloads");

        var entryPage = MovieSubtitlesClient.ParseSubtitlePage(Fixture("subtitle-132989.html"));
        Check("an entry page parses film id, language, rip, release and file name",
            entryPage is not null && entryPage.IsUsable
            && entryPage.MovieId == 24811 && entryPage.MovieName == "Oppenheimer" && entryPage.Year == 2023
            && entryPage.Language == "en" && entryPage.Rip == "WEB-DL" && entryPage.Release == "MA"
            && entryPage.FileNames.SequenceEqual(new[] { "Oppenheimer.WEB-DL.MA.en.srt" })
            && entryPage.Parts == 0 && entryPage.DownloadId == "132989",
            "found: " + entryPage);
        var partsEntry = MovieSubtitlesClient.ParseSubtitlePage(Fixture("subtitle-2058.html"));
        Check("an entry that states parts=2 lists its candidate names — a hint, not the archive",
            partsEntry is not null && partsEntry.Parts == 2 && partsEntry.Language == "ru"
            && partsEntry.FileNames.Count == 4
            && partsEntry.FileNames[0] == "Matrix The.part1.ru.srt"
            && partsEntry.FileNames[1] == "Matrix The.part2.ru.srt"
            && partsEntry.MovieId == 4 && partsEntry.Rip == string.Empty,
            "the page names five, one of them twice: " + string.Join(" | ", partsEntry?.FileNames ?? Array.Empty<string>()));
        Check("an unknown entry id is not an entry — HTTP 200 with garbage parses to null",
            MovieSubtitlesClient.ParseSubtitlePage(Fixture("subtitle-unknown.html")) is null
            && MovieSubtitlesClient.ParseSubtitlePage(string.Empty) is null,
            "the page states 'Download () subtitles' and 'size: 9140 kb'; nothing of it is a subtitle");

        // ---- 3b. the redirect target ---------------------------------------------------------------
        Check("the relative Location of the download survives, brackets and all",
            MovieSubtitlesClient.ParseDownloadTarget("files/Oppenheimer_2023.WEB-DL.MA.en.zip")
                == "files/Oppenheimer_2023.WEB-DL.MA.en.zip"
            && MovieSubtitlesClient.ParseDownloadTarget("files/Matrix_The_1999.ru(2).zip")
                == "files/Matrix_The_1999.ru(2).zip",
            "found: " + MovieSubtitlesClient.ParseDownloadTarget("files/Matrix_The_1999.ru(2).zip"));
        Check("the target becomes an absolute address without escaping the brackets away",
            MovieSubtitlesClient.DownloadUrl("files/Matrix_The_1999.ru(2).zip")
                == "https://www.moviesubtitles.org/files/Matrix_The_1999.ru(2).zip"
            && MovieSubtitlesClient.DownloadUrl("/files/Ocean´s Rising.zip")
                == "https://www.moviesubtitles.org/files/Ocean%C2%B4s%20Rising.zip",
            "found: " + MovieSubtitlesClient.DownloadUrl("files/Matrix_The_1999.ru(2).zip")
            + " / " + MovieSubtitlesClient.DownloadUrl("/files/Ocean´s Rising.zip"));
        Check("an unknown id redirects to 'files/' — that is the error signature, not a target",
            MovieSubtitlesClient.ParseDownloadTarget("files/") is null
            && MovieSubtitlesClient.ParseDownloadTarget("files") is null
            && MovieSubtitlesClient.ParseDownloadTarget(string.Empty) is null
            && MovieSubtitlesClient.ParseDownloadTarget(null) is null
            && MovieSubtitlesClient.ParseDownloadTarget("https://www.moviesubtitles.org/files/A.zip")
                == "https://www.moviesubtitles.org/files/A.zip",
            "found: " + (MovieSubtitlesClient.ParseDownloadTarget("files/") ?? "<null>"));
        Check("a ZIP is recognised from its own bytes, a page is not",
            MovieSubtitlesClient.IsZip(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 })
            && !MovieSubtitlesClient.IsZip(Encoding.UTF8.GetBytes("<html>kein zip</html>"))
            && !MovieSubtitlesClient.IsZip(Array.Empty<byte>()));

        // ---- 4. the upload ------------------------------------------------------------------------
        var data = Encoding.UTF8.GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\nhello\r\n");
        Check("a valid upload is accepted",
            BuildOk(data) is { } ok && ok.Fields.Count == 6,
            "a build without author has six text fields");
        var upload = BuildOk(data)!;
        Check("the parts leave in the form's own order",
            upload.Fields.Select(f => f.Key)
                .SequenceEqual(new[] { "tvid", "lang", "rip", "release", "comments", "go" })
            && upload.Parts.Select(p => p.Name)
                .SequenceEqual(new[] { "tvid", "lang", "rip", "release", "UFile", "comments", "go" }),
            "found: " + string.Join(",", upload.Parts.Select(p => p.Name)));
        Check("go is 1, tvid is the film id, and lang is the SITE's code — even from an ISO code",
            upload.Fields.Single(f => f.Key == "go").Value == "1"
            && upload.Fields.Single(f => f.Key == "tvid").Value == "24811"
            && upload.Fields.Single(f => f.Key == "lang").Value == "gr"
            && upload.LanguageName == "greek" && upload.SiteCode == "gr",
            "an ISO code (gre) must be sent as the select value (gr)");
        Check("the author field is NOT sent when it is empty, and is sent when it is set",
            !upload.Fields.Any(f => f.Key == "author")
            && BuildOk(data, author: "Hermes")!.Fields.Single(f => f.Key == "author").Value == "Hermes",
            "an absent part is the only way to keep whatever the page pre-fills");
        Check("release carries the release GROUP, never the file name",
            upload.Fields.Single(f => f.Key == "release").Value == "FTMVHD"
            && upload.ReleaseGroup == "FTMVHD",
            "found: '" + upload.Fields.Single(f => f.Key == "release").Value + "'");
        Check("a release name that states no group sends an empty release field",
            BuildOk(data, releaseName: "Oppenheimer (2023)")!.ReleaseGroup == string.Empty
            && BuildOk(data, releaseName: null)!.ReleaseGroup == string.Empty,
            "found: '" + BuildOk(data, releaseName: "Oppenheimer (2023)")!.ReleaseGroup + "'");
        Check("comments carries the ids only, exactly like the tvst upload",
            upload.Fields.Single(f => f.Key == "comments").Value
                == MovieSubtitlesClient.Comment(872585, "tt15398776")
            && MovieSubtitlesClient.Comment(872585, "tt15398776")
                == TvSubtitlesClient.Comment(872585, "tt15398776")
            && MovieSubtitlesClient.Comment(872585, null) == "TMDB 872585"
            && !MovieSubtitlesClient.Comment(872585, "tt15398776").Contains("Sub Scribe", StringComparison.Ordinal),
            "found: " + MovieSubtitlesClient.Comment(872585, "tt15398776"));
        Check("UFile2 and UFile3 are sent only when given, and then in their slots",
            upload.Files.Count == 1 && upload.Parts.Single(p => p.Name == "UFile").FileName == "Oppenheimer.WEB-DL.MA.gr.srt",
            "found " + upload.Files.Count + " file parts");
        var twoCds = BuildOk(
            data,
            extraFiles: new[]
            {
                new MovieSubtitlesUploadFile("Oppenheimer.CD2.srt", data),
                new MovieSubtitlesUploadFile("Oppenheimer.CD3.srt", data),
            })!;
        Check("a multi-CD upload puts the extra files behind UFile, before the comments",
            twoCds.Parts.Select(p => p.Name).SequenceEqual(new[]
            {
                "tvid", "lang", "rip", "release", "UFile", "UFile2", "UFile3", "comments", "go",
            }),
            "found: " + string.Join(",", twoCds.Parts.Select(p => p.Name)));

        var (body, boundary) = MovieSubtitlesClient.BuildUploadBody(upload);
        var text = Encoding.UTF8.GetString(body);
        Check("the body carries the file part with its name and octet-stream type",
            text.Contains("name=\"UFile\"; filename=\"Oppenheimer.WEB-DL.MA.gr.srt\"", StringComparison.Ordinal)
            && text.Contains("Content-Type: application/octet-stream", StringComparison.Ordinal));
        Check("every text field is a part of its own",
            upload.Fields.All(f => text.Contains("name=\"" + f.Key + "\"\r\n\r\n" + f.Value, StringComparison.Ordinal)));
        Check("the body opens with the boundary and closes with the double one",
            text.StartsWith("--" + boundary + "\r\n", StringComparison.Ordinal)
            && text.EndsWith("--" + boundary + "--\r\n", StringComparison.Ordinal));
        Check("the subtitle bytes are inside the body unchanged",
            body.AsSpan().IndexOf(data) >= 0);

        // ---- 4b. refusals — none of them touches the network --------------------------------------
        var handler = new CountingHandler();
        var client = new MovieSubtitlesClient(new HttpClient(handler));
        Check("movie id 0 is refused (the form would file it under no film)",
            Refused(() => Build(movieId: 0, data: data)));
        Check("an unknown language is refused, an ISO code is not",
            Refused(() => Build(lang: "xx", data: data))
            && Refused(() => Build(lang: string.Empty, data: data))
            && Refused(() => Build(lang: null, data: data))
            && !Refused(() => Build(lang: "gre", data: data)));
        Check("the refusal names the site's own thirteen codes in one line",
            Refusal(() => Build(lang: "xx", data: data)) is { } message
            && message.Contains("language must be one of the site's codes", StringComparison.Ordinal)
            && message.Contains("br", StringComparison.Ordinal)
            && message.Contains("ua", StringComparison.Ordinal)
            && !message.Contains("'xx'", StringComparison.Ordinal),
            "found: " + (Refusal(() => Build(lang: "xx", data: data)) ?? "<no refusal>"));
        Check("a rip outside the nine is refused — HDTV is tvst, not this site",
            Refused(() => Build(rip: "HDTV", data: data))
            && Refused(() => Build(rip: "HEVC", data: data))
            && !Refused(() => Build(rip: string.Empty, data: data))
            && !Refused(() => Build(rip: "WEB-DL", data: data)));
        Check("an upload without any id is refused — the comments field would carry only us",
            Refused(() => MovieSubtitlesClient.BuildUpload(
                24811, "en", null, "x.srt", data, "WEB-DL", null, null, null)));
        Check("an empty file, a wrong extension and a third extra file are refused",
            Refused(() => Build(data: Array.Empty<byte>()))
            && Refused(() => Build(fileName: "x.ass", data: data))
            && Refused(() => Build(data: data, extraFiles: new[]
            {
                new MovieSubtitlesUploadFile("a.srt", data),
                new MovieSubtitlesUploadFile("b.srt", data),
                new MovieSubtitlesUploadFile("c.srt", data),
            })));
        Check("a refused upload built no request at all",
            handler.Requests == 0);

        // ---- 4c. dry run against a counting handler ----------------------------------------------
        var dry = await client.UploadAsync(upload).ConfigureAwait(false);
        Check("a dry run says it sent nothing and still reports the body size",
            !dry.Sent && dry.Status is null && handler.Requests == 0 && dry.BodyBytes == body.Length,
            "requests=" + handler.Requests + ", bytes=" + dry.BodyBytes);
        var real = await client.UploadAsync(upload, dryRun: false).ConfigureAwait(false);
        Check("a real run posts multipart to add.php with the form page as referer",
            real.Sent && handler.Requests == 1
            && handler.LastUrl == "https://www.moviesubtitles.org/add.php"
            && handler.LastReferer == "https://www.moviesubtitles.org/add.php?tvid=24811"
            && handler.LastContentType!.StartsWith("multipart/form-data; boundary=", StringComparison.Ordinal),
            "url=" + handler.LastUrl + ", referer=" + handler.LastReferer);
        Check("the answer is stripped of tags and collapsed",
            real.Response == "ok" && handler.RepliedWith == 200,
            "found: " + (real.Response ?? "<null>"));

        // ---- 4e. F-M508: the UPLOAD ENDPOINT — built against fixtures, sent to nobody ------------
        // In dieser Etappe geht kein POST gegen die Seite. Der Endpunkt wird hier ausschliesslich mit
        // einem Zaehl-Handler gefahren: was er baut, was er ablehnt, und was er bei einer Antwort ohne
        // Id meldet. Der Trockenlauf ist der Vorgabewert, also kann ein falsch verdrahteter Aufrufer
        // nichts senden.
        var endpointHandler = new CountingHandler();
        var endpointClient = new MovieSubtitlesClient(new HttpClient(endpointHandler));
        var endpoint = new MovieSubtitlesUploadEndpoint(endpointClient);

        static UploadTicket FilmTicket(
            string lang = "gre", int season = 0, int episode = 0, bool seasonPack = false,
            int? movieId = 24811, int year = 2023, string title = "Oppenheimer", string tmdb = "872585",
            string imdb = "tt15398776", string? release = "Oppenheimer.2023.1080p.WEB-DL.DDP5.1.Atmos.H.264-FTMVHD",
            byte[]? data = null, string? rip = "WEB-DL") =>
            new(title, imdb, tmdb, season, episode, lang, "Oppenheimer.WEB-DL.MA.gr.srt",
                data ?? Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:03,000\nEin Satz.\n\n"),
                rip, release, HearingImpaired: false, DoubleEpisode: false, SeasonPack: seasonPack,
                TvstShowId: null, MsubMovieId: movieId, MovieYear: year);

        Check("the film address is wired and names itself",
            endpoint.Provider == SubtitleProvider.MovieSubtitles && endpoint.Wired,
            "provider=" + endpoint.Provider);

        var serieAmFilmEndpunkt = await endpoint.SendAsync(FilmTicket(season: 2, episode: 5)).ConfigureAwait(false);
        Check("a ticket with season and episode is refused — a film has neither (F-M508)",
            !serieAmFilmEndpunkt.Ok && endpointHandler.Requests == 0
            && serieAmFilmEndpunkt.Reason!.Contains("no season and no episode", StringComparison.Ordinal),
            "found: " + serieAmFilmEndpunkt.Reason);

        var packAmFilmEndpunkt = await endpoint.SendAsync(FilmTicket(seasonPack: true)).ConfigureAwait(false);
        Check("a season pack is refused — this source uploads one film file (F-M508)",
            !packAmFilmEndpunkt.Ok && packAmFilmEndpunkt.Reason!.Contains("no season packs", StringComparison.Ordinal),
            "found: " + packAmFilmEndpunkt.Reason);

        var fremdeSprache = await endpoint.SendAsync(FilmTicket(lang: "xx")).ConfigureAwait(false);
        Check("a language the site does not carry is refused, before anything is built (F-M508)",
            !fremdeSprache.Ok && fremdeSprache.Reason!.Contains("has no language 'xx'", StringComparison.Ordinal),
            "found: " + fremdeSprache.Reason);

        var ohneNummer = await endpoint.SendAsync(FilmTicket(movieId: null, year: 0)).ConfigureAwait(false);
        Check("without the film number AND without a year nothing is guessed — refused (F-M508)",
            !ohneNummer.Ok && ohneNummer.Reason!.Contains("title AND its year", StringComparison.Ordinal),
            "found: " + ohneNummer.Reason);
        Check("a refused ticket built no request at all — not even a search",
            endpointHandler.Requests == 0, "requests=" + endpointHandler.Requests);

        // Der Trockenlauf ist der Vorgabewert: er baut denselben Rumpf und sendet nichts.
        var trocken = await endpoint.SendAsync(FilmTicket()).ConfigureAwait(false);
        Check("a dry run reports the body it WOULD send and sends nothing (F-M508)",
            trocken.Ok && trocken.DryRun && trocken.Id is null && trocken.Answer is null
            && trocken.Bytes > 0 && endpointHandler.Requests == 0,
            "bytes=" + trocken.Bytes + ", requests=" + endpointHandler.Requests);

        // Der scharfe Weg — gegen den Zaehl-Handler, NICHT gegen die Seite. Er belegt, dass der Endpunkt
        // die Formularfelder in der Reihenfolge der Seite schickt und die Antwort durchreicht.
        var scharf = new MovieSubtitlesUploadEndpoint(new MovieSubtitlesClient(new HttpClient(endpointHandler)), dryRun: false);
        var angenommen = await scharf.SendAsync(FilmTicket()).ConfigureAwait(false);
        var gesendeterRumpf = endpointHandler.LastBody == null ? string.Empty : Encoding.UTF8.GetString(endpointHandler.LastBody);
        Check("a real send posts the form's fields in the form's order, release carrying the group only (F-M508)",
            endpointHandler.Requests == 1
            && gesendeterRumpf.Contains("name=\"tvid\"\r\n\r\n24811", StringComparison.Ordinal)
            && gesendeterRumpf.Contains("name=\"lang\"\r\n\r\ngr", StringComparison.Ordinal)
            && gesendeterRumpf.Contains("name=\"rip\"\r\n\r\nWEB-DL", StringComparison.Ordinal)
            && gesendeterRumpf.Contains("name=\"release\"\r\n\r\nFTMVHD", StringComparison.Ordinal)
            && !gesendeterRumpf.Contains("Oppenheimer.2023.1080p", StringComparison.Ordinal)
            // author bleibt weg, solange das Plugin keinen nennt — ein fehlender Teil ueberschreibt nichts
            && !gesendeterRumpf.Contains("name=\"author\"", StringComparison.Ordinal)
            && gesendeterRumpf.Contains("name=\"UFile\"; filename=\"Oppenheimer.WEB-DL.MA.gr.srt\"", StringComparison.Ordinal)
            && gesendeterRumpf.IndexOf("name=\"tvid\"", StringComparison.Ordinal)
               < gesendeterRumpf.IndexOf("name=\"comments\"", StringComparison.Ordinal)
            && gesendeterRumpf.IndexOf("name=\"comments\"", StringComparison.Ordinal)
               < gesendeterRumpf.LastIndexOf("name=\"go\"", StringComparison.Ordinal),
            "body: " + (gesendeterRumpf.Length > 400 ? gesendeterRumpf[..400] : gesendeterRumpf));

        Check("an answer with 2xx is reported as ACCEPTED with NO id — never an invented one (F-M508)",
            angenommen.Ok && !angenommen.DryRun && angenommen.Id is null
            && angenommen.Answer == "ok" && angenommen.Reason is null,
            "ok=" + angenommen.Ok + " id=" + (angenommen.Id ?? "<null>") + " answer=" + angenommen.Answer);

        var eigeneKennung = Jellyfin.Plugin.SubScribe.Api.Endpoints.Provenance.OwnUploadId(
            SubtitleProvider.MovieSubtitles, "abc123", "Oppenheimer.2023.1080p.WEB-DL", new DateTime(2026, 11, 15, 18, 4, 5, DateTimeKind.Utc));
        var kennungTeile = eigeneKennung.Split(':');
        Check("the mark for an id-less upload is ours, prefixed with the source (F-M488/F-M508)",
            kennungTeile.Length == 4
            && kennungTeile[0] == "own" && kennungTeile[1] == "moviesubtitles"
            && kennungTeile[2] == "20261115T180405Z"
            && kennungTeile[3].Length == 8
            && kennungTeile[3].All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
            && Jellyfin.Plugin.SubScribe.Api.Endpoints.Provenance.IsOwnUploadId(eigeneKennung)
            && eigeneKennung != Jellyfin.Plugin.SubScribe.Api.Endpoints.Provenance.OwnUploadId(
                SubtitleProvider.MovieSubtitles, "abc124", "Oppenheimer.2023.1080p.WEB-DL", new DateTime(2026, 11, 15, 18, 4, 5, DateTimeKind.Utc)),
            "found: " + eigeneKennung);

        endpointHandler.Status = HttpStatusCode.InternalServerError;
        endpointHandler.Body = "<html><b>error:</b> no such film</html>";
        var abgelehnt = await scharf.SendAsync(FilmTicket(movieId: 999999)).ConfigureAwait(false);
        Check("a non-2xx answer is a refusal that quotes the status and passes the site's text through (F-M508)",
            !abgelehnt.Ok && abgelehnt.Reason == "HTTP 500"
            // der Text der Seite wird durchgereicht (nur Tags/Whitespace zusammengezogen) — nicht gedeutet
            && abgelehnt.Answer!.StartsWith("error:", StringComparison.Ordinal)
            && abgelehnt.Answer!.Contains("no such film", StringComparison.Ordinal)
            && abgelehnt.Id is null,
            "reason=" + abgelehnt.Reason + " answer=" + abgelehnt.Answer);
        endpointHandler.Status = HttpStatusCode.OK;
        endpointHandler.Body = "<html><b>ok</b>\n</html>";

        // ---- 4g. F-M511: die Qualitaet geht durch die Zuordnung der Filmseite, NICHT roh ins Formular --
        // Gemessen 15.11.2026 auf jf-test: die Kette reichte die Qualitaet des Release-Namens roh ins
        // Ticket (gemessen "web"), BuildUpload wies sie ab — "rip must be empty or one of: DVDRip,
        // BluRay, BrRip, BDRip, HDRip, WEB, WEBRip, WEB-DL (Parameter 'upload')" —, und der Endpunkt
        // machte daraus Ok=false. Nur die LEERE Angabe kam durch, also scheiterte JEDER Film-Upload.
        // Die Abbildung sitzt jetzt EINMAL im Endpunkt (MovieSubtitlesClient.RipFromQuality), vor der
        // Trockenlauf-Weiche; die Kette reicht weiter die rohe Qualitaet herein.
        var ripFaelle = new (string Roh, string Erwartet)[]
        {
            ("web", "WEB"),
            ("1080p BluRay x264", "BluRay"),
            ("WEB-DL", "WEB-DL"),      // kennt diese Seite — die tvst-Zuordnung macht daraus "WEB"
            ("HDTV", string.Empty),    // kennt diese Seite NICHT (das ist tvst)
            ("x265", string.Empty),    // ein Codec allein ist keine Quelle
            (string.Empty, string.Empty),
        };

        var rohAngenommen = new List<string>();
        var rumpfGleich = new List<string>();
        var rumpfAbweichend = new List<string>();
        var rohBytes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var fall in ripFaelle)
        {
            var roh = await endpoint.SendAsync(FilmTicket(rip: fall.Roh)).ConfigureAwait(false);
            var ziel = await endpoint.SendAsync(FilmTicket(rip: fall.Erwartet)).ConfigureAwait(false);
            rohBytes[fall.Roh] = roh.Bytes;
            if (roh.Ok && roh.DryRun && roh.Reason is null)
            {
                rohAngenommen.Add(fall.Roh);
            }
            else
            {
                rumpfAbweichend.Add("\"" + fall.Roh + "\" REFUSED: " + roh.Reason);
            }

            if (roh.Bytes == ziel.Bytes)
            {
                rumpfGleich.Add(fall.Roh);
            }
            else
            {
                rumpfAbweichend.Add("\"" + fall.Roh + "\" " + roh.Bytes + " != \"" + fall.Erwartet + "\" " + ziel.Bytes);
            }
        }

        Check("eine rohe Qualitaet kommt durch den Film-Endpunkt — der gemessene Ok=false-Fall ist weg (F-M511)",
            rohAngenommen.Count == ripFaelle.Length,
            "angenommen: " + string.Join(", ", rohAngenommen) + " | " + string.Join("; ", rumpfAbweichend));

        Check("die Nutzlast der rohen Angabe ist byte-gleich der Nutzlast ihres zugeordneten Werts — Trockenlauf wie Ernst (F-M511)",
            rumpfGleich.Count == ripFaelle.Length,
            "gleich: " + string.Join(", ", rumpfGleich) + " | " + string.Join("; ", rumpfAbweichend));

        // Der scharfe Weg geht gegen einen Zaehl-Handler, NICHT gegen die Seite: er belegt, was im Rumpf steht.
        var ripHandler = new CountingHandler();
        var ripScharf = new MovieSubtitlesUploadEndpoint(new MovieSubtitlesClient(new HttpClient(ripHandler)), dryRun: false);
        var webScharf = await ripScharf.SendAsync(FilmTicket(rip: "web")).ConfigureAwait(false);
        var webRumpf = ripHandler.LastBody == null ? string.Empty : Encoding.UTF8.GetString(ripHandler.LastBody);
        Check("'web' geht als 'WEB' hinaus, und der Trockenlauf nennt dieselbe Laenge (F-M511)",
            webScharf.Ok && ripHandler.Requests == 1
            && webRumpf.Contains("name=\"rip\"\r\n\r\nWEB\r\n", StringComparison.Ordinal)
            && !webRumpf.Contains("name=\"rip\"\r\n\r\nWEB-", StringComparison.Ordinal)
            && ripHandler.LastBody!.Length == rohBytes["web"],
            "requests=" + ripHandler.Requests + ", trocken=" + rohBytes["web"]
            + ", ernst=" + (ripHandler.LastBody?.Length ?? 0)
            + ", rumpf=" + (webRumpf.Length > 300 ? webRumpf[..300] : webRumpf));

        var hdtvScharf = await ripScharf.SendAsync(FilmTicket(rip: "HDTV")).ConfigureAwait(false);
        var hdtvRumpf = ripHandler.LastBody == null ? string.Empty : Encoding.UTF8.GetString(ripHandler.LastBody);
        var x265Trocken = await endpoint.SendAsync(FilmTicket(rip: "x265")).ConfigureAwait(false);
        Check("HDTV und x265 senden das LEERE Rip-Feld — erlaubt, nichts erfunden (F-M511)",
            hdtvScharf.Ok && ripHandler.Requests == 2
            && hdtvRumpf.Contains("name=\"rip\"\r\n\r\n\r\n", StringComparison.Ordinal)
            && x265Trocken.Ok && rohBytes["x265"] == rohBytes[string.Empty],
            "hdtv-requests=" + ripHandler.Requests
            + ", x265-Bytes=" + rohBytes["x265"] + ", leer-Bytes=" + rohBytes[string.Empty]);

        var webdlScharf = await ripScharf.SendAsync(FilmTicket(rip: "WEB-DL")).ConfigureAwait(false);
        var webdlRumpf = ripHandler.LastBody == null ? string.Empty : Encoding.UTF8.GetString(ripHandler.LastBody);
        Check("WEB-DL bleibt WEB-DL — die tvst-Zuordnung ('WEB-DL' -> 'WEB') wird NICHT geborgt (F-M511)",
            webdlScharf.Ok && ripHandler.Requests == 3
            && webdlRumpf.Contains("name=\"rip\"\r\n\r\nWEB-DL\r\n", StringComparison.Ordinal)
            && TvSubtitlesClient.RipFromQuality("WEB-DL") == "WEB",
            "rumpf=" + (webdlRumpf.Length > 300 ? webdlRumpf[..300] : webdlRumpf));

        // ---- 4h. F-M513: einen FILM ANLEGEN — mit Doppeleintrag-Schutz und ehrlicher Id ------------
        // Nutzerauftrag 16.11.2026: „Dann einmal mit neu anlegen testen". Ein Film, den die Seite NICHT
        // fuehrt, soll beim Upload angelegt werden. Belegt ist nur das Formular (name, year, go); was die
        // Seite auf den POST ANTWORTET, ist unbelegt. Deshalb wird die Id nachgesehen, nicht geraten —
        // und der Doppeleintrag-Schutz entscheidet vorher. Kein POST gegen die Seite: alles gegen
        // Zaehl-Handler.
        var anlage = MovieSubtitlesClient.BuildAddMovie("  Paddington in Peru  ", 2026);
        Check("das Anlege-Formular traegt name, year, go in der Reihenfolge der Seite (F-M513)",
            anlage.Fields.Count == 3
            && anlage.Fields[0].Key == "name" && anlage.Fields[0].Value == "Paddington in Peru"
            && anlage.Fields[1].Key == "year" && anlage.Fields[1].Value == "2026"
            && anlage.Fields[2].Key == "go" && anlage.Fields[2].Value == "1"
            && anlage.Name == "Paddington in Peru" && anlage.Year == 2026,
            "fields: " + string.Join(",", anlage.Fields.Select(f => f.Key + "=" + f.Value)));

        var (anlageRumpf, _) = MovieSubtitlesClient.BuildAddMovieBody(anlage);
        var anlageText = Encoding.UTF8.GetString(anlageRumpf);
        Check("der Rumpf traegt genau diese drei Felder, in dieser Reihenfolge — und nichts sonst (F-M513)",
            anlageText.Contains("name=\"name\"\r\n\r\nPaddington in Peru\r\n", StringComparison.Ordinal)
            && anlageText.Contains("name=\"year\"\r\n\r\n2026\r\n", StringComparison.Ordinal)
            && anlageText.Contains("name=\"go\"\r\n\r\n1\r\n", StringComparison.Ordinal)
            && anlageText.IndexOf("name=\"name\"", StringComparison.Ordinal)
               < anlageText.IndexOf("name=\"year\"", StringComparison.Ordinal)
            && anlageText.IndexOf("name=\"year\"", StringComparison.Ordinal)
               < anlageText.IndexOf("name=\"go\"", StringComparison.Ordinal)
            && !anlageText.Contains("filename=", StringComparison.Ordinal)
            && !anlageText.Contains("tvid", StringComparison.Ordinal),
            "body: " + anlageText);

        var anlageLeer = new AddMovieSiteHandler();
        var anlageLeerClient = new MovieSubtitlesClient(new HttpClient(anlageLeer));
        var jahrGrenze = MovieSubtitlesClient.MaxAddMovieYear;
        Check("ein leerer Name und ein unplausibles Jahr werden VOR dem Senden abgewiesen — ohne Anfrage (F-M513)",
            Refused(() => MovieSubtitlesClient.BuildAddMovie("   ", 2026))
            && Refused(() => MovieSubtitlesClient.BuildAddMovie(string.Empty, 2026))
            && Refused(() => MovieSubtitlesClient.BuildAddMovie("X", 1887))
            && Refused(() => MovieSubtitlesClient.BuildAddMovie("X", jahrGrenze + 1))
            // die Grenzen selbst sind erlaubt — und ein einbuchstabiger Titel auch (es gibt den Film „X")
            && !Refused(() => MovieSubtitlesClient.BuildAddMovie("X", 1888))
            && !Refused(() => MovieSubtitlesClient.BuildAddMovie("X", jahrGrenze))
            && anlageLeer.Posts == 0 && anlageLeer.Gets == 0,
            "grenze=" + jahrGrenze + ", posts=" + anlageLeer.Posts);

        // Trockenlauf: derselbe Rumpf, kein Byte auf der Leitung. Die Laenge ist die des Ernstfalls,
        // weil die Nutzlast VOR der Weiche gebaut wird — wie beim Upload (F-M511).
        var anlageTrocken = await anlageLeerClient.AddMovieAsync(anlage).ConfigureAwait(false);
        Check("der Trockenlauf baut denselben Rumpf und sendet nichts (F-M513)",
            anlageTrocken.Outcome == MovieSubtitlesAddOutcome.DryRun && !anlageTrocken.Ok
            && anlageTrocken.Status is null && anlageTrocken.MovieId is null
            && anlageTrocken.BodyBytes == anlageRumpf.Length
            && anlageLeer.Posts == 0 && anlageLeer.Gets == 0,
            "bytes=" + anlageTrocken.BodyBytes + " erwartet=" + anlageRumpf.Length
            + " posts=" + anlageLeer.Posts + " gets=" + anlageLeer.Gets);

        // Der Ernstfall gegen einen Zaehl-Handler: POST an /add-movie.php, danach die Nachsuche.
        var anlageSeite = new AddMovieSiteHandler();
        anlageSeite.Films.Add((4242, "Paddington in Peru", 2026));
        var anlageClient = new MovieSubtitlesClient(new HttpClient(anlageSeite));
        var anlageErnst = await anlageClient.AddMovieAsync(anlage, dryRun: false).ConfigureAwait(false);
        var gesendeteAnlage = anlageSeite.LastPostBody == null
            ? string.Empty
            : Encoding.UTF8.GetString(anlageSeite.LastPostBody);
        Check("der Ernstfall postet multipart an /add-movie.php und liest die Id aus der Nachsuche (F-M513)",
            anlageErnst.Ok && anlageErnst.Outcome == MovieSubtitlesAddOutcome.Created
            && anlageErnst.MovieId == 4242 && anlageSeite.Posts == 1 && anlageSeite.Gets >= 1
            && anlageSeite.LastPostUrl == "https://www.moviesubtitles.org/add-movie.php"
            && anlageSeite.LastReferer == "https://www.moviesubtitles.org/add-movie.php"
            && anlageSeite.LastContentType!.StartsWith("multipart/form-data; boundary=", StringComparison.Ordinal)
            && gesendeteAnlage.Contains("name=\"name\"\r\n\r\nPaddington in Peru\r\n", StringComparison.Ordinal)
            && anlageErnst.BodyBytes == anlageRumpf.Length,
            "outcome=" + anlageErnst.Outcome + " id=" + (anlageErnst.MovieId?.ToString() ?? "<null>")
            + " posts=" + anlageSeite.Posts + " gets=" + anlageSeite.Gets
            + " url=" + (anlageSeite.LastPostUrl ?? "<null>")
            + " bytes=" + anlageErnst.BodyBytes + "/" + anlageRumpf.Length);

        // Ein HTTP-Fehler ist KEINE Annahme: Ok=false, der Status ist der Grund, keine Nachsuche.
        var fehlerSeite = new AddMovieSiteHandler
        {
            PostStatus = HttpStatusCode.InternalServerError,
            PostBody = "<html><b>error:</b> could not add movie</html>",
        };
        var anlageFehler = await new MovieSubtitlesClient(new HttpClient(fehlerSeite))
            .AddMovieAsync(anlage, dryRun: false).ConfigureAwait(false);
        Check("ein HTTP-Fehler beim Anlegen ist Ok=false mit dem Status als Grund (F-M513)",
            !anlageFehler.Ok && anlageFehler.Outcome == MovieSubtitlesAddOutcome.HttpError
            && anlageFehler.Reason == "HTTP 500" && anlageFehler.Status == 500
            && anlageFehler.MovieId is null
            && anlageFehler.Response!.Contains("could not add movie", StringComparison.Ordinal)
            && fehlerSeite.Posts == 1 && fehlerSeite.Gets == 0,
            "ok=" + anlageFehler.Ok + " reason=" + anlageFehler.Reason
            + " gets=" + fehlerSeite.Gets + " answer=" + anlageFehler.Response);

        // ANGELEGT, ID NICHT LESBAR: der POST wird angenommen, die Nachsuche findet nichts. Dann wird
        // KEINE Nummer erfunden — und alle Schreibweisen werden gefragt, bevor das feststeht. Der Rumpf
        // ist die ECHTE Antwort der Seite vom 16.11.2026 (8467 Bytes, „Movie has been added. Thank you.").
        var stummSeite = new AddMovieSiteHandler { PostBody = Fixture("add-movie-added.html") };
        var stumm = await new MovieSubtitlesClient(new HttpClient(stummSeite))
            .AddMovieAsync(MovieSubtitlesClient.BuildAddMovie("The Matrix", 2026), dryRun: false)
            .ConfigureAwait(false);
        Check("angelegt ohne lesbare Id: ehrliches Ergebnis mit dem Text der Antwort, keine erfundene Nummer (F-M513)",
            stumm.Ok && stumm.Outcome == MovieSubtitlesAddOutcome.CreatedIdUnknown
            && stumm.MovieId is null && stumm.Status == 200
            && stumm.Response!.Contains("Movie has been added", StringComparison.Ordinal)
            && stumm.Response!.Contains("Thank you", StringComparison.Ordinal)
            && stummSeite.Posts == 1 && stummSeite.Gets == 3
            && stummSeite.Queries.SequenceEqual(new[] { "The Matrix", "Matrix", "Matrix, The" }),
            "outcome=" + stumm.Outcome + " gets=" + stummSeite.Gets
            + " queries=" + string.Join(" | ", stummSeite.Queries));

        // ---- 4h2. F-M515: die ANTWORT auf add-movie.php wird gelesen — an den echten Bytes ----------
        // Live belegt am 16.11.2026 mit zwei echten POSTs (multipart, go=1): Erfolg = HTTP 200, 8467
        // Bytes, „Movie has been added. Thank you."; verweigert = HTTP 200, 8459 Bytes, „This Movie
        // already exist". Beide Fixtures sind diese Antworten, unveraendert. Ein dritter Satz ist NICHT
        // belegt — und das unveraenderte Formular (8399 Bytes) ist live die Antwort auf einen POST, der
        // nichts anlegt: es traegt keinen Marker.
        var clientQuelle = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/MovieSubtitlesClient.cs"));
        Check("die zwei belegten Antworten werden an ihrem Marker erkannt, alles andere ist KEINE Anlage (F-M515)",
            MovieSubtitlesClient.ClassifyAddMovieAnswer("Movie has been added. Thank you.")
                == MovieSubtitlesAddOutcome.Created
            && MovieSubtitlesClient.ClassifyAddMovieAnswer("This Movie already exist")
                == MovieSubtitlesAddOutcome.Exists
            && MovieSubtitlesClient.ClassifyAddMovieAnswer(Fixture("add-movie.html"))
                == MovieSubtitlesAddOutcome.Rejected
            && MovieSubtitlesClient.ClassifyAddMovieAnswer(string.Empty) == MovieSubtitlesAddOutcome.Rejected
            && MovieSubtitlesClient.AddMovieCreatedMarker == "Movie has been added"
            && MovieSubtitlesClient.AddMovieExistsMarker == "This Movie already exist",
            "created='" + MovieSubtitlesClient.AddMovieCreatedMarker + "' exists='"
            + MovieSubtitlesClient.AddMovieExistsMarker + "'");

        // Erfolg + Nachsuche nennt den Film: die Nummer kommt aus der Nachsuche, nicht aus der Antwort.
        var echtErfolgSeite = new AddMovieSiteHandler { PostBody = Fixture("add-movie-added.html") };
        echtErfolgSeite.Films.Add((26230, "The Little Rascals", 1994));
        var echtErfolg = await new MovieSubtitlesClient(new HttpClient(echtErfolgSeite))
            .AddMovieAsync(MovieSubtitlesClient.BuildAddMovie("The Little Rascals", 1994), dryRun: false)
            .ConfigureAwait(false);
        Check("die echte Erfolgs-Antwort ergibt 'created' mit der Nummer aus der Nachsuche — genau ein POST (F-M515)",
            echtErfolg.Ok && echtErfolg.Outcome == MovieSubtitlesAddOutcome.Created
            && echtErfolg.MovieId == 26230 && echtErfolg.Reason is null && echtErfolg.Status == 200
            && echtErfolg.Response!.Contains("Movie has been added", StringComparison.Ordinal)
            && echtErfolgSeite.Posts == 1,
            "outcome=" + echtErfolg.Outcome + " id=" + (echtErfolg.MovieId?.ToString() ?? "<null>")
            + " posts=" + echtErfolgSeite.Posts);

        // „existiert schon" + Nachsuche nennt den Film: Ok=false (NICHTS angelegt), die Nummer wird
        // uebernommen, und es faellt KEIN zweiter POST.
        var besitzSeite = new AddMovieSiteHandler { PostBody = Fixture("add-movie-exists.html") };
        besitzSeite.Films.Add((23716, "Paddington 2", 2017));
        var besitz = await new MovieSubtitlesClient(new HttpClient(besitzSeite))
            .AddMovieAsync(MovieSubtitlesClient.BuildAddMovie("Paddington 2", 2017), dryRun: false)
            .ConfigureAwait(false);
        Check("'This Movie already exist' fuehrt zur Nachsuche und zur vorhandenen Nummer — kein zweiter POST (F-M515)",
            !besitz.Ok && besitz.Outcome == MovieSubtitlesAddOutcome.Exists && besitz.MovieId == 23716
            && besitz.Reason == "This Movie already exist" && besitz.Status == 200
            && besitzSeite.Posts == 1 && besitzSeite.Gets >= 1,
            "ok=" + besitz.Ok + " outcome=" + besitz.Outcome
            + " id=" + (besitz.MovieId?.ToString() ?? "<null>")
            + " posts=" + besitzSeite.Posts + " gets=" + besitzSeite.Gets);

        // „existiert schon" OHNE Treffer in der Nachsuche — der live gemessene Regelfall: die Seite
        // verweigert, fuehrt den Film aber nirgends. Keine Id, kein zweiter POST, kein Fehler.
        var geisterSeite = new AddMovieSiteHandler { PostBody = Fixture("add-movie-exists.html") };
        var geist = await new MovieSubtitlesClient(new HttpClient(geisterSeite))
            .AddMovieAsync(MovieSubtitlesClient.BuildAddMovie("The Little Rascals", 1994), dryRun: false)
            .ConfigureAwait(false);
        Check("'existiert schon' ohne Treffer ist 'exists-id-not-readable' — keine Id, kein zweiter POST (F-M515)",
            !geist.Ok && geist.Outcome == MovieSubtitlesAddOutcome.ExistsUnknownId && geist.MovieId is null
            && geisterSeite.Posts == 1 && geisterSeite.Gets == 3
            && geist.Response!.Contains("This Movie already exist", StringComparison.Ordinal),
            "ok=" + geist.Ok + " outcome=" + geist.Outcome + " posts=" + geisterSeite.Posts
            + " gets=" + geisterSeite.Gets);

        // Das unveraenderte Formular ist live die Antwort auf einen POST, der nichts anlegt. Es traegt
        // keinen Marker — also wird nicht gesucht, nichts gespeichert und nichts behauptet.
        var formularSeite = new AddMovieSiteHandler { PostBody = Fixture("add-movie.html") };
        var formular = await new MovieSubtitlesClient(new HttpClient(formularSeite))
            .AddMovieAsync(MovieSubtitlesClient.BuildAddMovie("The Little Rascals", 1994), dryRun: false)
            .ConfigureAwait(false);
        Check("das unveraenderte Formular ist KEINE Anlage: 'rejected', kein GET, keine Id (F-M515)",
            !formular.Ok && formular.Outcome == MovieSubtitlesAddOutcome.Rejected
            && formular.MovieId is null && formular.Status == 200
            && formular.Reason!.StartsWith("no creation marker", StringComparison.Ordinal)
            && formular.Response!.Contains("Add Movie", StringComparison.Ordinal)
            && formularSeite.Posts == 1 && formularSeite.Gets == 0,
            "outcome=" + formular.Outcome + " reason=" + formular.Reason
            + " gets=" + formularSeite.Gets);

        Check("der Client klassifiziert die Antwort, der Aufloeser fuehrt sie in drei Zweige (F-M515)",
            clientQuelle.Contains("ClassifyAddMovieAnswer(plain)", StringComparison.Ordinal)
            && clientQuelle.Contains("MovieSubtitlesAddOutcome.Rejected", StringComparison.Ordinal)
            && clientQuelle.Contains("MovieSubtitlesAddOutcome.ExistsUnknownId", StringComparison.Ordinal)
            && ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/Endpoints/MsubMovieResolver.cs"))
                .Contains("MsubMovieStatus.ExistsFound", StringComparison.Ordinal)
            && ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/Endpoints/MsubMovieResolver.cs"))
                .Contains("MsubMovieStatus.ExistsWithoutId", StringComparison.Ordinal)
            && ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/Endpoints/MsubMovieResolver.cs"))
                .Contains("MovieSubtitlesClient.AddMovieExistsMarker", StringComparison.Ordinal),
            "client/aufloeser-Bindung");


        // ---- der Doppeleintrag-Schutz: drei Faelle, je eine Entscheidung --------------------------
        var vorhanden = new[] { new MovieSubtitlesMovie(4, "Matrix, The", 1999, Array.Empty<string>()) };
        var gleichesJahr = MovieSubtitlesClient.ResolveForAdd(vorhanden, "The Matrix", 1999);
        Check("Doppeleintrag, gleiches Jahr: die vorhandene Nummer wird genommen, nichts angelegt (F-M513)",
            gleichesJahr.Status == MovieSubtitlesAddStatus.Take && gleichesJahr.MovieId == 4
            && gleichesJahr.Hits == 1 && gleichesJahr.Answers == 1,
            "status=" + gleichesJahr.Status + " id=" + (gleichesJahr.MovieId?.ToString() ?? "<null>"));

        var anderesJahr = MovieSubtitlesClient.ResolveForAdd(vorhanden, "The Matrix", 2026);
        Check("Doppeleintrag, anderes Jahr: verweigert, und das gefundene Jahr steht im Ergebnis (F-M513)",
            anderesJahr.Status == MovieSubtitlesAddStatus.Refuse && anderesJahr.MovieId is null
            && anderesJahr.FoundYear == 1999 && anderesJahr.Hits == 1,
            "status=" + anderesJahr.Status + " foundYear=" + anderesJahr.FoundYear);

        var keinTreffer = MovieSubtitlesClient.ResolveForAdd(
            Array.Empty<MovieSubtitlesMovie>(), "The Matrix", 1999);
        Check("Doppeleintrag, kein Treffer: anlegen (F-M513)",
            keinTreffer.Status == MovieSubtitlesAddStatus.Create && keinTreffer.MovieId is null
            && keinTreffer.Hits == 0);

        var zweimal = new[]
        {
            new MovieSubtitlesMovie(4, "Matrix, The", 1999, Array.Empty<string>()),
            new MovieSubtitlesMovie(77, "The Matrix", 1999, Array.Empty<string>()),
        };
        var mehrdeutig = MovieSubtitlesClient.ResolveForAdd(zweimal, "The Matrix", 1999);
        Check("zwei Zeilen mit demselben Titel und Jahr sind kein Treffer, sondern ein Grund, nichts anzulegen (F-M513)",
            mehrdeutig.Status == MovieSubtitlesAddStatus.Refuse && mehrdeutig.MovieId is null
            && mehrdeutig.Hits == 2,
            "status=" + mehrdeutig.Status + " hits=" + mehrdeutig.Hits);

        // Die eine Suchrunde im Netz: der Teilstring-Filter der Seite findet „The Matrix" NICHT, den
        // Kern schon — und nach dem ersten Treffer mit diesem Jahr wird nicht weitergefragt.
        var suchSeite = new AddMovieSiteHandler();
        suchSeite.Films.Add((4, "Matrix, The", 1999));
        var such = await new MovieSubtitlesClient(new HttpClient(suchSeite))
            .CheckForAddAsync("The Matrix", 1999).ConfigureAwait(false);
        Check("die eine Suchrunde nimmt die Schreibweise, die die Seite versteht — und hoert beim Treffer auf (F-M513)",
            such.Status == MovieSubtitlesAddStatus.Take && such.MovieId == 4
            && suchSeite.Gets == 2 && suchSeite.Queries[0] == "The Matrix"
            && suchSeite.Posts == 0,
            "status=" + such.Status + " gets=" + suchSeite.Gets
            + " queries=" + string.Join(" | ", suchSeite.Queries));

        var blindSeite = new AddMovieSiteHandler { ThrowOnSearch = true };
        var blind = await new MovieSubtitlesClient(new HttpClient(blindSeite))
            .CheckForAddAsync("Paddington in Peru", 2026).ConfigureAwait(false);
        Check("eine unlesbare Suche belegt NICHT, dass es den Film nicht gibt — nichts wird angelegt (F-M513)",
            blind.Status == MovieSubtitlesAddStatus.Unclear && blind.MovieId is null && blind.Answers == 0
            && blindSeite.Posts == 0,
            "status=" + blind.Status + " answers=" + blind.Answers);

        // Die Anlage steht im UPLOAD-Pfad und nirgends sonst, ihre Zeile ist fest, und die neue Nummer
        // geht in dieselbe Spalte wie die Auflösung.
        var anlageUploadQuelle = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        var anlageDownloadQuelle = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs"));
        var anlageAufloeserQuelle = ReadText(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/Endpoints/MsubMovieResolver.cs"));
        Check("angelegt wird nur im Upload-Pfad — der Download-Pfad ruft die anlegende Fassung nicht (F-M513)",
            anlageUploadQuelle.Contains(
                "ResolveOrCreateForMediaAsync(Registry, mediaHash, movie, _config.DryRun, ct, mediaPath)", StringComparison.Ordinal)
            && !anlageDownloadQuelle.Contains("ResolveOrCreateForMediaAsync", StringComparison.Ordinal)
            && anlageDownloadQuelle.Contains(
                "ResolveForMediaAsync(Registry, mediaHash, movie, _config.DownloadDryRun, ct, mediaPath)", StringComparison.Ordinal));

        Check("die Anlage hat ihre feste Logzeile, und die neue Nummer geht sofort in MsubMovieId (F-M513)",
            anlageAufloeserQuelle.Contains("neu angelegt — Id {Id} — {Note}", StringComparison.Ordinal)
            && anlageAufloeserQuelle.Contains(
                "Persist(registry, mediaHash, mediaPath, angelegt.MovieId.Value)", StringComparison.Ordinal)
            // F-M517: die Spalte wird jetzt im Store geschrieben (Hash ODER Pfad), nicht mehr hier
            && ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Registry/ContentHashRegistry.cs"))
                .Contains("media.MsubMovieId = movieId.ToString(", StringComparison.Ordinal)
            && anlageAufloeserQuelle.Contains("MovieSubtitlesAddStatus.Refuse", StringComparison.Ordinal));

        // ---- F-M517: die Nummer geht AUCH OHNE Medien-Hash in die Zeile, die ihr PFAD adressiert ----
        // Gemessen 16.11.2026, 04:13 auf jf-test: `[moviesubtitles] ?: 'Hotel Transylvania 2' (2015) ist
        // Film 13392 — nicht gespeichert: kein Medien-Hash bekannt`. Die Datei ist ein Symlink ohne Ziel,
        // `GetMediaHash` liefert deshalb null — die Medienzeile existierte aber (aus einem Lauf, der sie
        // über ihren Hash angelegt hatte) und trägt den Pfad. Genau diese Zeile wird jetzt geschrieben;
        // ohne Hash UND ohne Zeile bleibt es beim ehrlichen „nicht gespeichert".
        const string fm517Pfad =
            "/opt/data/jf-test/media/movies-test/Hotel Transylvania 2 (2015)/Hotel Transylvania 2 (2015).mkv";
        const string fm517Hash = "a1b2c3d4e5f60718";
        string fm517Root = Path.Combine(Path.GetTempPath(), "msub-fm517-" + Environment.ProcessId);
        try
        {
            if (Directory.Exists(fm517Root))
            {
                Directory.Delete(fm517Root, true);
            }

            Directory.CreateDirectory(fm517Root);

            // (a) Zeile vorhanden, Hash nicht: schreiben über den Pfad, Zeilenzahl unverändert.
            var pfadSeite = new AddMovieSiteHandler();
            pfadSeite.Films.Add((13392, "Hotel Transylvania 2", 2015));
            using (var db = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(
                Path.Combine(fm517Root, "mit-zeile"), null))
            {
                var reg = new Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry(db, null, null);
                reg.EnsureMedia(fm517Hash, jellyfinItemId: null, path: fm517Pfad);
                int vorher = db.Media.Count();

                var aufloeser = new MsubMovieResolver(new MovieSubtitlesClient(new HttpClient(pfadSeite)), null);
                var ohneHash = await aufloeser
                    .ResolveForMediaAsync(
                        reg,
                        mediaHash: null,
                        new TmdbMovieInfo("Hotel Transylvania 2", "Hotel Transylvania 2", 2015),
                        dryRun: false,
                        CancellationToken.None,
                        mediaPath: fm517Pfad)
                    .ConfigureAwait(false);

                int nachher = db.Media.Count();
                Check("ohne Medien-Hash, aber mit Pfad-Zeile: die gefundene Filmnummer steht danach in der Zeile (F-M517)",
                    ohneHash.Resolved && ohneHash.MovieId == 13392 && ohneHash.Status == MsubMovieStatus.Found
                    && db.Media.FindById(fm517Hash)?.MsubMovieId == "13392"
                    && reg.MsubMovieIdAtPath(fm517Pfad) == "13392"
                    && nachher == vorher && vorher == 1,
                    "id=" + (db.Media.FindById(fm517Hash)?.MsubMovieId ?? "<null>")
                    + " zeilen=" + vorher + "->" + nachher);

                // Der Pfad ist danach auch die LESE-Adresse: der zweite Aufruf sucht nicht mehr.
                var zweite = await aufloeser
                    .ResolveForMediaAsync(
                        reg,
                        mediaHash: null,
                        new TmdbMovieInfo("Hotel Transylvania 2", "Hotel Transylvania 2", 2015),
                        dryRun: false,
                        CancellationToken.None,
                        mediaPath: fm517Pfad)
                    .ConfigureAwait(false);
                Check("die über den Pfad gespeicherte Nummer wird beim naechsten Mal gelesen, nicht neu gesucht (F-M517)",
                    zweite.Status == MsubMovieStatus.Stored && zweite.MovieId == 13392 && pfadSeite.Gets == 1,
                    "status=" + zweite.Status + " gets=" + pfadSeite.Gets);
            }

            // (b) Keine Zeile, kein Hash: nichts wird geschrieben, und der Text sagt warum.
            var leereSeite = new AddMovieSiteHandler();
            leereSeite.Films.Add((13392, "Hotel Transylvania 2", 2015));
            using (var dbLeer = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(
                Path.Combine(fm517Root, "ohne-zeile"), null))
            {
                var regLeer = new Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry(dbLeer, null, null);
                var ohneZeile = await new MsubMovieResolver(new MovieSubtitlesClient(new HttpClient(leereSeite)), null)
                    .ResolveForMediaAsync(
                        regLeer,
                        mediaHash: null,
                        new TmdbMovieInfo("Hotel Transylvania 2", "Hotel Transylvania 2", 2015),
                        dryRun: false,
                        CancellationToken.None,
                        mediaPath: "/opt/data/jf-test/media/movies-test/Ohne Zeile (2000)/Ohne Zeile (2000).mkv")
                    .ConfigureAwait(false);
                Check("ohne Hash UND ohne Pfad-Zeile: nichts geschrieben, die Nummer gilt nur fuer diesen Lauf (F-M517)",
                    ohneZeile.Resolved && ohneZeile.MovieId == 13392
                    && dbLeer.Media.Count() == 0
                    && MediaNumberNote.Of(Jellyfin.Plugin.SubScribe.Registry.MediaNumberWrite.None) == MediaNumberNote.NotStored
                    && MediaNumberNote.NotStored.Contains("gilt nur für diesen Lauf", StringComparison.Ordinal)
                    && MediaNumberNote.Of(Jellyfin.Plugin.SubScribe.Registry.MediaNumberWrite.ByPath)
                        == MediaNumberNote.ByPath,
                    "zeilen=" + dbLeer.Media.Count());
            }

            // (c) Zwei Zeilen mit demselben Pfad: der Pfad ist dann KEINE Adresse — keine wird gewaehlt.
            using (var dbViel = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(
                Path.Combine(fm517Root, "mehrdeutig"), null))
            {
                var regViel = new Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry(dbViel, null, null);
                regViel.EnsureMedia("1111222233334444", jellyfinItemId: null, path: fm517Pfad);
                regViel.EnsureMedia("5555666677778888", jellyfinItemId: null, path: fm517Pfad);
                var geschrieben = regViel.SetMsubMovieId(null, fm517Pfad, 13392);
                Check("ein Pfad, den MEHRERE Zeilen tragen, ist keine Adresse — nichts geschrieben, nichts geraten (F-M517)",
                    geschrieben == Jellyfin.Plugin.SubScribe.Registry.MediaNumberWrite.None
                    && regViel.GetMedia("1111222233334444")?.MsubMovieId is null
                    && regViel.GetMedia("5555666677778888")?.MsubMovieId is null
                    && regViel.MsubMovieIdAtPath(fm517Pfad) is null,
                    "geschrieben=" + geschrieben + " zeilen=" + regViel.MediaAtPath(fm517Pfad).Count);
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

        // ---- 4d. the download path, driven by a handler that serves a real redirect ---------------
        var downloadHandler = new RedirectHandler();
        var downloadClient = new MovieSubtitlesClient(new HttpClient(downloadHandler));
        var download = await downloadClient.DownloadAsync(2058, "ru").ConfigureAwait(false);
        Check("the client follows the 302 and hands back the member the language names",
            download.Name == "Matrix The.part1.ru.srt" && download.Data.Length == 2
            && download.Members.Count == 2 && download.PickedByLanguage && download.IsMultiPart,
            "both segments carry '.ru', so the first one wins: " + download.Name
            + ", members=" + string.Join(",", download.Members));
        downloadHandler.Target = "files/";
        downloadHandler.Payload = Array.Empty<byte>();
        var emptyTargetRefused = await ThrowsAsync<InvalidOperationException>(
            () => downloadClient.DownloadAsync(999999, null)).ConfigureAwait(false);
        Check("a redirect to 'files/' is refused as the unknown-id signature",
            emptyTargetRefused,
            "an empty target must not become a download");
        downloadHandler.Target = "files/Matrix_The_1999.ru(2).srt";
        downloadHandler.Payload = Encoding.UTF8.GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\n");
        var bare = await downloadClient.DownloadAsync(2058, "ru").ConfigureAwait(false);
        Check("bare .srt bytes are returned as they are, without unpacking",
            bare.Members.Count == 1 && bare.Name == "Matrix_The_1999.ru(2).srt" && !bare.IsMultiPart
            && bare.Data.Length == downloadHandler.Payload.Length,
            "the site serves the archive and the bare file; found " + bare.Name + " with " + bare.Members.Count + " member(s)");

        // ---- 4f. F-M510: der Titel-Jahr-Vergleich gegen TMDb --------------------------------
        // Gemessen 15.11.2026 im ersten Film-Lauf auf jf-test: das Item heisst "Oppenheimer (2023)",
        // TMDb wurde mit genau diesem Text gefragt und antwortete ohne Treffer —
        //   [TMDB] strict title+year test found no hit for "Oppenheimer (2023)" (2023) — not confirming
        // —, obwohl der Film unter "Oppenheimer" mit release_date 2023-07-19 die richtige Antwort ist.
        // Der Handler unten bildet die gemessene Antwort nach: die Fassung MIT Klammer findet nichts,
        // die reine Fassung findet den Film. Abgetrennt wird nur ein ABSCHLIESSENDES Klammer-Jahr.
        var tmdbHandler = new TmdbHandler();
        var tmdb = new TmdbImdbResolver(new HttpClient(tmdbHandler), "test-key");

        Check("ein abschliessendes (Jahr) wird fuer den Vergleich abgetrennt — der gemessene Film-Fall",
            TmdbImdbResolver.ComparisonTitle("Oppenheimer (2023)") == "Oppenheimer",
            "got \"" + TmdbImdbResolver.ComparisonTitle("Oppenheimer (2023)") + "\"");
        Check("eine Jahres-SPANNE in Klammern wird genauso abgetrennt",
            TmdbImdbResolver.ComparisonTitle("The Office (2019–2023)") == "The Office"
            && TmdbImdbResolver.ComparisonTitle("The Office (2019-2023)") == "The Office",
            "got \"" + TmdbImdbResolver.ComparisonTitle("The Office (2019–2023)") + "\"");
        Check("ein Jahr in eckigen Klammern wird abgetrennt",
            TmdbImdbResolver.ComparisonTitle("Dune [2023]") == "Dune",
            "got \"" + TmdbImdbResolver.ComparisonTitle("Dune [2023]") + "\"");
        Check("ein echter Klammer-Titel bleibt unangetastet — nur ein ABSCHLIESSENDES Jahr zaehlt",
            TmdbImdbResolver.ComparisonTitle("(Ghost) Riders In the Sky (American Outlaws_ Live …)")
                == "(Ghost) Riders In the Sky (American Outlaws_ Live …)",
            "got \"" + TmdbImdbResolver.ComparisonTitle("(Ghost) Riders In the Sky (American Outlaws_ Live …)") + "\"");
        Check("ein Jahr im Titel ohne Klammer bleibt stehen",
            TmdbImdbResolver.ComparisonTitle("Blade Runner 2049") == "Blade Runner 2049");
        Check("nur EIN abschliessendes Jahr geht — ein doppeltes laesst das erste stehen",
            TmdbImdbResolver.ComparisonTitle("O Beautiful (2002) (2002)") == "O Beautiful (2002)",
            "got \"" + TmdbImdbResolver.ComparisonTitle("O Beautiful (2002) (2002)") + "\"");

        var strictFilm = await tmdb.ResolveByTitleYearAsync("Oppenheimer (2023)", 2023, preferSeries: false, CancellationToken.None)
            .ConfigureAwait(false);
        Check("der strenge Titel+Jahr-Test findet den Film fuer einen Namen, der sein Jahr mittraegt",
            strictFilm.Found && strictFilm.Tmdb == "872585" && strictFilm.Imdb == "tt15398776"
            && !strictFilm.IsSeries,
            "found=" + strictFilm.Found + " tmdb=" + (strictFilm.Tmdb ?? "<null>") + " imdb=" + (strictFilm.Imdb ?? "<null>"));

        var suchUrls = tmdbHandler.Urls.Where(u => u.Contains("search/", StringComparison.Ordinal)).ToList();
        Check("die Suchanfrage trug den reinen Titel — das Klammer-Jahr ging nie an TMDb",
            suchUrls.Count == 1
            && suchUrls[0].Contains("query=Oppenheimer&", StringComparison.Ordinal)
            && !tmdbHandler.Urls.Any(u => u.Contains("2023", StringComparison.Ordinal)),
            "gefragt: " + string.Join(" | ", suchUrls));

        // Der gemessene Store-Stand: der Film-Eintrag trug tmdb=1422 (aus der Serien-Metadaten-Vorlage),
        // und die Zeile im Log lautete "keeping Jellyfin's ids". Mit dem reinen Titel ist die Antwort
        // eine Korrektur, keine Duldung.
        var pruefung = await tmdb.VerifyIdsAgainstTmdbAsync(
            "tt1442464", "1422", "Oppenheimer (2023)", 2023, isSeries: false, CancellationToken.None).ConfigureAwait(false);
        Check("der gemessene Store-Stand (tmdb 1422) wird jetzt korrigiert statt geduldet",
            pruefung.Corrected && pruefung.Tmdb == "872585",
            "corrected=" + pruefung.Corrected + " tmdb=" + (pruefung.Tmdb ?? "<null>"));

        // ---- 5. opt-in live run: the only part that touches the site — and it only READS ----------
        if (args.Contains("--live"))
        {
            Console.WriteLine();
            Console.WriteLine("--- live probe against moviesubtitles.org (GET only) ---");
            var liveHttp = ScribeApiClient.NewHttpClient(TimeSpan.FromSeconds(60));
            var live = new MovieSubtitlesClient(liveHttp);
            var hits = await live.SearchAsync("oppenheimer").ConfigureAwait(false);
            Console.WriteLine("  search 'oppenheimer': " + hits.Count + " film(s)"
                + (hits.Count > 0 ? ", first " + hits[0].Id + " " + hits[0].Name + " (" + hits[0].Year + ")" : string.Empty));
            foreach (var hit in hits.Take(5))
            {
                Console.WriteLine("    " + hit.Id + "  " + hit.Name + " (" + hit.Year + ")");
            }

            var id = MovieSubtitlesClient.ResolveMovie(hits, "Oppenheimer", 2023);
            Console.WriteLine("  resolves to: " + (id?.ToString() ?? "<nothing>"));
            if (id is int movieId)
            {
                var page = await live.MovieAsync(movieId).ConfigureAwait(false);
                Console.WriteLine("  movie " + movieId + ": " + page.Name + " (" + page.Year + "), "
                    + page.Entries.Count + " entries, sections " + string.Join(",", page.Sections));
                foreach (var e in page.Entries.Take(5))
                {
                    Console.WriteLine("    " + e.SubtitleId + " " + e.Language + " " + e.Rip + " " + e.Release + " parts " + e.Parts);
                }

                var pick = page.Entries.FirstOrDefault(e => e.Language == "en");
                if (pick is not null)
                {
                    var detail = await live.SubtitleAsync(int.Parse(pick.SubtitleId, System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                    Console.WriteLine("  entry " + pick.SubtitleId + ": " + (detail is null ? "<unparsable>" : detail.FileNames.FirstOrDefault()));
                    try
                    {
                        var file = await live.DownloadAsync(
                            int.Parse(pick.SubtitleId, System.Globalization.CultureInfo.InvariantCulture),
                            pick.Language).ConfigureAwait(false);
                        Console.WriteLine("  download: " + file.Name + ", " + file.Data.Length + " bytes, "
                            + file.Members.Count + " member(s), by language: " + file.PickedByLanguage);
                    }
                    catch (InvalidOperationException ex)
                    {
                        Console.WriteLine("  download refused: " + ex.Message);
                    }
                }
            }
        }

        Console.WriteLine();
        if (_failures > 0)
        {
            Console.WriteLine($">>> MOVIESUBTITLES: {_failures} von {_checks} Pruefungen fehlgeschlagen");
            return 1;
        }

        Console.WriteLine($">>> MOVIESUBTITLES OK: {_checks} Pruefungen bestanden");
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

    /// <summary>F-M513: eine Quelle des Repos lesen — fuer die Quelltext-Bindungen (wie in der Suite `endpoints`).</summary>
    private static string ReadText(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : throw new FileNotFoundException("source missing: " + path);

    /// <summary>
    /// F-M513: die Seite fuer das ANLEGEN — <c>/add-movie.php</c> nimmt den POST an (oder nicht), und
    /// <c>/search.php</c> antwortet wie ihr Teilstring-Filter: nur Filme, deren Titel den Suchbegriff
    /// traegt. Genau daran haengt die Nachsuche nach der neuen Nummer, und genau darum ist „The Matrix"
    /// der interessante Fall: den findet die Seite nur ueber den Kern oder die invertierte Form.
    /// </summary>
    private sealed class AddMovieSiteHandler : HttpMessageHandler
    {
        /// <summary>Die Filme, die die Suche kennt.</summary>
        public List<(int Id, string Name, int Year)> Films { get; } = new();

        /// <summary>Der Status, mit dem <c>add-movie.php</c> antwortet.</summary>
        public HttpStatusCode PostStatus { get; set; } = HttpStatusCode.OK;

        /// <summary>Der Text, mit dem <c>add-movie.php</c> antwortet.</summary>
        public string PostBody { get; set; } = "<html><b>Thank you.</b> Your movie has been added.</html>";

        /// <summary>True: die Suche antwortet gar nicht (Ausnahme) — die unlesbare Antwort.</summary>
        public bool ThrowOnSearch { get; set; }

        public int Posts { get; private set; }

        public int Gets { get; private set; }

        /// <summary>Die Suchbegriffe in der Reihenfolge, in der sie gefragt wurden.</summary>
        public List<string> Queries { get; } = new();

        public string? LastUrl { get; private set; }

        /// <summary>Die Adresse des POST — die Nachsuche danach ueberschreibt <see cref="LastUrl"/>.</summary>
        public string? LastPostUrl { get; private set; }

        public string? LastReferer { get; private set; }

        public string? LastContentType { get; private set; }

        public byte[]? LastPostBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri?.ToString();

            if (request.Method == HttpMethod.Post)
            {
                Posts++;
                LastPostUrl = LastUrl;
                LastReferer = request.Headers.Referrer?.ToString();
                LastContentType = request.Content?.Headers.ContentType?.ToString();
                LastPostBody = request.Content is null
                    ? null
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                await Task.Yield();
                return new HttpResponseMessage(PostStatus)
                {
                    Content = new StringContent(PostBody, Encoding.UTF8, "text/html"),
                };
            }

            Gets++;
            if (ThrowOnSearch)
            {
                throw new HttpRequestException("the site did not answer");
            }

            var uri = request.RequestUri!;
            var marker = uri.Query.IndexOf("q=", StringComparison.Ordinal);
            var query = marker < 0 ? string.Empty : Uri.UnescapeDataString(uri.Query[(marker + 2)..]);
            Queries.Add(query);

            var rows = Films
                .Where(f => f.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(f => "<a href=\"/movie-" + f.Id + ".html\">" + f.Name + " (" + f.Year + ")</a>")
                .ToList();
            var html = rows.Count == 0
                ? "<html><div class=\"left_articles\">nothing found</div></html>"
                : "<html>" + string.Join("\n", rows) + "</html>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html"),
            };
        }
    }

    private static MovieSubtitlesUpload? BuildOk(
        byte[] data,
        string? author = null,
        string? releaseName = "Oppenheimer.2023.1080p.WEB-DL.DDP5.1.Atmos.H.264-FTMVHD",
        IReadOnlyList<MovieSubtitlesUploadFile>? extraFiles = null) =>
        MovieSubtitlesClient.BuildUpload(
            24811, "gre", releaseName, "Oppenheimer.WEB-DL.MA.gr.srt", data,
            "WEB-DL", author, 872585, "tt15398776", null, extraFiles);

    private static MovieSubtitlesUpload? Build(
        int movieId = 24811,
        string? lang = "en",
        string fileName = "x.srt",
        byte[]? data = null,
        string? rip = "WEB-DL",
        IReadOnlyList<MovieSubtitlesUploadFile>? extraFiles = null) =>
        MovieSubtitlesClient.BuildUpload(
            movieId, lang, "Oppenheimer.2023.1080p.WEB-DL.x265-FTMVHD", fileName,
            data ?? Encoding.UTF8.GetBytes("1\n"), rip, null, 872585, "tt15398776", null, extraFiles);

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

    private static async Task<bool> ThrowsAsync<T>(Func<Task> action)
        where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
            return false;
        }
        catch (T)
        {
            return true;
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

    /// <summary>
    /// F-M510: TMDb, wie es am 15.11.2026 geantwortet hat — die Fassung MIT Klammer-Jahr findet nichts,
    /// die reine Fassung findet "Oppenheimer" (2023, tmdb 872585, tt15398776). Jede Anfrage wird
    /// mitgeschrieben, damit eine Pruefung belegen kann, WELCHE Frage hinausging.
    /// </summary>
    private sealed class TmdbHandler : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Urls.Add(url);

            string body;
            if (url.Contains("/search/", StringComparison.Ordinal))
            {
                // Genau die gemessene Antwort: der reine Titel trifft, der Klammer-Text nicht.
                body = url.Contains("query=Oppenheimer&", StringComparison.Ordinal)
                    ? "{\"page\":1,\"results\":[{\"media_type\":\"movie\",\"id\":872585,\"title\":\"Oppenheimer\",\"release_date\":\"2023-07-19\"}]}"
                    : "{\"page\":1,\"results\":[]}";
            }
            else
            {
                body = "{\"id\":872585,\"imdb_id\":\"tt15398776\",\"title\":\"Oppenheimer\",\"release_date\":\"2023-07-19\"}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>An HTTP handler that answers "ok" and remembers what it was handed.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public string? LastUrl { get; private set; }

        /// <summary>Die Adresse des POST — die Nachsuche danach ueberschreibt <see cref="LastUrl"/>.</summary>
        public string? LastPostUrl { get; private set; }

        public string? LastReferer { get; private set; }

        public string? LastContentType { get; private set; }

        /// <summary>The request body, so a suite can assert the form's fields without a network call.</summary>
        public byte[]? LastBody { get; private set; }

        public int RepliedWith { get; private set; } = 200;

        /// <summary>The status the fake site answers with — a refusal is a status, not an exception.</summary>
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        /// <summary>The body the fake site answers with — the endpoint must pass it through unchanged.</summary>
        public string Body { get; set; } = "<html><b>ok</b>\n</html>";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastUrl = request.RequestUri?.ToString();
            LastReferer = request.Headers.Referrer?.ToString();
            LastContentType = request.Content?.Headers.ContentType?.ToString();
            LastBody = request.Content == null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            await Task.Yield();
            RepliedWith = (int)Status;
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "text/html"),
            };
        }
    }

    /// <summary>
    /// Serves the download chain without the site: the entry page as a page, the download page as the
    /// 302 the site really sends, and <c>files/…</c> as a small ZIP with two language members.
    /// </summary>
    private sealed class RedirectHandler : HttpMessageHandler
    {
        public string Target { get; set; } = "files/Matrix_The_1999.ru(2).zip";

        public byte[] Payload { get; set; } = ZipOf(
            ("Matrix The.part1.ru.srt", Encoding.UTF8.GetBytes("ab")),
            ("Matrix The.part2.ru.srt", Encoding.UTF8.GetBytes("xyz")));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.EndsWith("download-2058.html", StringComparison.Ordinal)
                || url.EndsWith("download-999999.html", StringComparison.Ordinal))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(Target, UriKind.RelativeOrAbsolute);
                return Task.FromResult(redirect);
            }

            if (url.Contains("/files/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Payload),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><div class=\"left_articles\"></div></html>", Encoding.UTF8, "text/html"),
            });
        }

        private static byte[] ZipOf(params (string Name, byte[] Data)[] members)
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, data) in members)
                {
                    var entry = archive.CreateEntry(name);
                    using var entryStream = entry.Open();
                    entryStream.Write(data, 0, data.Length);
                }
            }

            return stream.ToArray();
        }
    }
}
