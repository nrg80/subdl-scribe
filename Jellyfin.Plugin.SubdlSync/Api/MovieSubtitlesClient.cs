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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// F-M506 (Etappe 1, 15.11.2026): the moviesubtitles.org client — the THIRD source, and the one that
/// only serves films. Like tvsubtitles.net the site has no API: <c>/api.php</c>, <c>/sitemap.xml</c>,
/// <c>/rss.xml</c> answer 302 to <c>/</c>, <c>/login.php</c>/<c>/panel.php</c>/<c>/upload.php</c> as well.
/// Everything runs over HTML and the site's own forms.
/// <para>
/// The path is the site's own navigation (measured 15.11.2026, see <c>docs/MOVIESUBTITLES.md</c>):
/// <c>movies-&lt;A..Z|0&gt;.html</c> (the film list) or <c>search.php?q=</c> or
/// <c>add.php?filter=</c> → <c>movie-&lt;id&gt;.html</c> (entries, grouped by language) →
/// <c>subtitle-&lt;id&gt;.html</c> (rip, release, file name, parts) → <c>download-&lt;id&gt;.html</c>
/// (302 with a RELATIVE <c>Location</c>) → <c>files/&lt;…&gt;.zip</c>.
/// </para>
/// <para>
/// WHAT THIS CLASS IS AND IS NOT: it is the source's method surface — resolve, list, fetch, download,
/// build an upload, send an upload, create a film. The DOWNLOAD side is wired since F-M507 (Etappe 2a):
/// <see cref="Endpoints.MovieSubtitlesDownloadEndpoint"/> uses <see cref="FindMovieAsync"/>,
/// <see cref="MovieAsync"/> and <see cref="DownloadAsync"/>, the movie number lives in
/// <c>MediaEntity.MsubMovieId</c>, and the source has its two switches. The UPLOAD side is wired since
/// F-M508 (Etappe 2b): <see cref="Endpoints.MovieSubtitlesUploadEndpoint"/> builds the form out of
/// <see cref="BuildUpload"/> and hands it to <see cref="UploadAsync"/>. CREATING A FILM is built since
/// F-M513: <see cref="BuildAddMovie"/>/<see cref="AddMovieAsync"/> post <c>add-movie.php</c>, and
/// <see cref="CheckForAddAsync"/> is the one search that both resolves and guards against a duplicate.
/// </para>
/// <para>
/// WAS UNBELEGT BLEIBT: die ANTWORT der Seite auf einen POST (<c>add.php</c> wie
/// <c>add-movie.php</c>) — sie wird nicht gedeutet, sondern nachgesehen. Und die Sprachabbildung ist
/// weiterhin nicht gegen die echte Seite belegt; solange gilt die Auflage „Bevor wir falsche Sprachen
/// hochladen, laden wir nix hoch", und der Upload-Schalter der Quelle bleibt die Sperre.
/// </para>
/// <para>
/// Every parser is static and takes the HTML as a string, so the suite under
/// <c>scripts/tests/moviesubtitles</c> drives them with the pages the site really served instead of the
/// live site.
/// </para>
/// </summary>
public sealed class MovieSubtitlesClient
{
    /// <summary>The site root. https only — the http endpoint redirects (301) to <c>https://www.</c>.</summary>
    public const string BaseUrl = "https://www.moviesubtitles.org";

    /// <summary>
    /// F-M506: the language table of THIS site — thirteen values, taken verbatim from
    /// <c>&lt;select name="lang"&gt;</c> on <c>add.php</c> (measured 15.11.2026). It is NOT the
    /// tvsubtitles.net table: that one carries 24 codes, this one 13, and the two disagree in the parts
    /// that matter (no <c>cz</c>, no <c>cn</c>, no <c>jp</c>, no European Portuguese, no Dutch, no
    /// Swedish, no Danish, no Finnish, no Korean, no Bulgarian, no Romanian).
    /// <para>
    /// The value carries (a) the display name exactly as the form spells it — <c>portugese(br)</c>, with
    /// the site's own typo — and (b) the code the site writes into the download name. That code is the
    /// select value itself (measured: <c>Oppenheimer_2023.WEB-DL.MA.en.zip</c> and
    /// <c>Matrix_The_1999.ru(2).zip</c>), not an ISO-639-2/B form — which is exactly the difference to
    /// <see cref="TvSubtitlesClient.Languages"/>, whose third column is an ISO3 code.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, MovieSubtitlesLanguage> Languages =
        new Dictionary<string, MovieSubtitlesLanguage>(StringComparer.Ordinal)
        {
            ["en"] = new("en", "english", "en"),
            ["es"] = new("es", "spanish", "es"),
            ["fr"] = new("fr", "french", "fr"),
            ["de"] = new("de", "german", "de"),
            ["br"] = new("br", "portugese(br)", "br"),
            ["ru"] = new("ru", "russian", "ru"),
            ["ua"] = new("ua", "ukrainian", "ua"),
            ["it"] = new("it", "italian", "it"),
            ["gr"] = new("gr", "greek", "gr"),
            ["ar"] = new("ar", "arabic", "ar"),
            ["hu"] = new("hu", "hungarian", "hu"),
            ["pl"] = new("pl", "polish", "pl"),
            ["tr"] = new("tr", "turkish", "tr"),
        };

    /// <summary>
    /// F-M506: die Bruecke von ISO auf die Kuerzel DIESER Seite — und nur hier (dieselbe Trennung wie
    /// F-M502 fuer tvsubtitles.net: kein gemeinsames Mapping mit SubDL, keins mit der Schwester).
    /// <para>
    /// Die Liste ist absichtlich KURZ. Aufgenommen ist nur, was die 13er-Liste wirklich kennt:
    /// <c>eng spa fra/fre deu/ger pob rus uk/ukr ita el/ell/gre ara hun pol tur</c> und die Region, die
    /// die Sprache ist (<c>pt-BR</c>/<c>pt_BR</c> → <c>br</c>). NICHT aufgenommen — und damit
    /// verweigert — sind die Codes, die die Seite nicht fuehrt: <c>pt</c>/<c>por</c> (europaeisches
    /// Portugiesisch; die Seite kennt NUR die brasilianische Fassung, und <c>pt</c> auf <c>br</c> zu
    /// ziehen waere eine falsche Kennzeichnung), <c>nl dut nld sv swe da dan fi fin ko kor cn zh zho chi
    /// jp ja jpn bg bul cs ces cze ro ron rum</c>.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> LanguageAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["eng"] = "en",
            ["spa"] = "es",
            ["fra"] = "fr",
            ["fre"] = "fr",
            ["deu"] = "de",
            ["ger"] = "de",
            ["pob"] = "br",
            ["rus"] = "ru",
            ["uk"] = "ua",
            ["ukr"] = "ua",
            ["ita"] = "it",
            ["el"] = "gr",
            ["ell"] = "gr",
            ["gre"] = "gr",
            ["ara"] = "ar",
            ["hun"] = "hu",
            ["pol"] = "pl",
            ["tur"] = "tr",
        };

    /// <summary>
    /// F-M506: der Sprachcode, den die Seite versteht — aus einem ISO-Code, einem Seitenkuerzel oder
    /// einer Angabe mit Region/Skript (<c>pt-BR</c>, <c>zh-Hans</c>).
    /// <para>
    /// Gross-/Kleinschreibung zaehlt nicht. Die Region entscheidet nur dort, wo die Seite die Sprache
    /// danach trennt — und das tut sie nur bei Portugiesisch: <c>pt-BR</c> ist <c>br</c>. Bei allen
    /// anderen Sprachen faellt die Region weg; hilft das nicht weiter, ist die Antwort <c>null</c>.
    /// </para>
    /// </summary>
    /// <param name="code">Der ankommende Code, in beliebiger Schreibweise.</param>
    /// <returns>Ein Kuerzel aus <see cref="Languages"/>, oder null — dann ist die Sprache der Seite
    /// unbekannt und wird NICHT auf Englisch zurueckgefallen und nicht geraten.</returns>
    public static string? SiteLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var text = code.Trim().ToLowerInvariant().Replace('_', '-');

        // Die eine Stelle, an der die REGION die Sprache ist: brasilianisches Portugiesisch.
        // Muss vor der Grundcode-Reduktion stehen, sonst faellt "pt-br" auf "pt" und damit ins Leere.
        if (string.Equals(text, "pt-br", StringComparison.Ordinal))
        {
            return "br";
        }

        var dash = text.IndexOf('-', StringComparison.Ordinal);
        var baseCode = dash > 0 ? text[..dash] : text;

        if (LanguageAliases.TryGetValue(baseCode, out var mapped))
        {
            return mapped;
        }

        // Das Kuerzel der Seite selbst (en, de, br, gr, ua …) ist bereits das Ziel.
        return Languages.ContainsKey(baseCode) ? baseCode : null;
    }

    /// <summary>
    /// F-M506: the rip select of the upload form, in the form's own order, plus the empty value — nine
    /// values and the empty one. It is NOT the tvsubtitles.net list (<c>WEB HDTV HEVC DVDRip DVDSCR
    /// BDRip BluRay</c>): <c>HDRip</c>, <c>BrRip</c>, <c>WEBRip</c> and <c>WEB-DL</c> exist only here,
    /// <c>HDTV</c>, <c>HEVC</c> and <c>DVDSCR</c> only there. A shared table would send a value the form
    /// silently stores as empty.
    /// </summary>
    public static readonly IReadOnlyList<string> Rips =
        new[] { "DVDRip", "BluRay", "BrRip", "BDRip", "HDRip", "WEB", "WEBRip", "WEB-DL", string.Empty };

    /// <summary>
    /// F-M506: die Qualitaetsangabe eines Dateinamens auf einen Rip-Wert DIESER Seite ziehen — Quelle
    /// vor Codec, erster Treffer gewinnt.
    /// <para>
    /// DER UNTERSCHIED ZU <see cref="TvSubtitlesClient.RipFromQuality"/> IST ABSICHT: dort endet die
    /// Zuordnung bei <c>HEVC</c>, weil das Auswahlfeld der Seite <c>HEVC</c> kennt. Hier kennt es keinen
    /// Codec — und deshalb gibt es hier auch KEINEN Codec-Rueckfall: <c>x265</c> allein ergibt den leeren
    /// Wert, nicht <c>HEVC</c>. Ein Codec als Rip waere eine Behauptung ueber die Quelle, die die Seite
    /// nicht macht. Aus demselben Grund bleibt <c>HDTV</c> leer (die Seite kennt es nicht), waehrend
    /// <c>BluRay.x265</c> → <c>BluRay</c> und <c>WEB-DL.x265</c> → <c>WEB-DL</c> ergibt: die QUELLE steht
    /// vor dem Codec.
    /// </para>
    /// <para>
    /// Der leere Wert ist ein erlaubter Wert — die Seite speichert dann keinen Rip, und die Mehrheit
    /// ihrer Eintraege steht so da.
    /// </para>
    /// </summary>
    /// <param name="quality">Qualitaets-/Release-Angabe, wie sie im Dateinamen steht.</param>
    /// <returns>Ein Wert aus <see cref="Rips"/>; leer, wenn die Angabe keine Quelle dieser Seite nennt.</returns>
    public static string RipFromQuality(string? quality)
    {
        var q = (quality ?? string.Empty).ToUpperInvariant();

        // Reihenfolge ist Absicht: die genaueren Schreibweisen zuerst, "WEB" allein zuletzt — sonst
        // verschluckt es "WEB-DL" und "WEBRip", die die Seite getrennt fuehrt.
        if (q.Contains("WEB-DL", StringComparison.Ordinal) || q.Contains("WEBDL", StringComparison.Ordinal)
            || q.Contains("WEB.DL", StringComparison.Ordinal))
        {
            return "WEB-DL";
        }

        if (q.Contains("WEBRIP", StringComparison.Ordinal) || q.Contains("WEB-RIP", StringComparison.Ordinal)
            || q.Contains("WEB RIP", StringComparison.Ordinal))
        {
            return "WEBRip";
        }

        if (q.Contains("DVDRIP", StringComparison.Ordinal) || q.Contains("DVD-RIP", StringComparison.Ordinal)
            || q.Contains("DVD-R", StringComparison.Ordinal))
        {
            return "DVDRip";
        }

        if (q.Contains("BLURAY", StringComparison.Ordinal) || q.Contains("BLU-RAY", StringComparison.Ordinal)
            || q.Contains("BDREMUX", StringComparison.Ordinal))
        {
            return "BluRay";
        }

        if (q.Contains("BRRIP", StringComparison.Ordinal) || q.Contains("BR-RIP", StringComparison.Ordinal))
        {
            return "BrRip";
        }

        if (q.Contains("BDRIP", StringComparison.Ordinal) || q.Contains("BD-RIP", StringComparison.Ordinal))
        {
            return "BDRip";
        }

        if (q.Contains("HDRIP", StringComparison.Ordinal) || q.Contains("HD-RIP", StringComparison.Ordinal))
        {
            return "HDRip";
        }

        if (q.Contains("WEB", StringComparison.Ordinal))
        {
            return "WEB";
        }

        // Kein Codec-Rueckfall (siehe oben), kein HDTV, nichts geraten.
        return string.Empty;
    }

    // ---- the pages' shapes, measured 15.11.2026 ------------------------------------------------

    // The film list (movies-<L>.html): one <div class="movies"> around all rows, each row an anchor
    // with class="ma" that holds name, year, downloads and the language flags.
    private static readonly Regex MovieRow = new(
        "href=\\\"(?:/)?movie-(\\d+)\\.html\\\"\\s+class=\\\"ma\\\">(.*?)</a>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex RowName = new(
        "class=\\\"mname\\\"[^>]*>(.*?)</div>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex RowYear = new(
        "class=\\\"myear\\\"[^>]*>\\s*(\\d{4})", RegexOptions.CultureInvariant);

    // The upload view (add.php?let=… / add.php?filter=…): <li><a href="add.php?tvid=24811"><b>Oppenheimer</b></a> 2023</li>
    private static readonly Regex AddRow = new(
        "href=\\\"add\\.php\\?tvid=(\\d+)\\\">(.*?)</a>\\s*(\\d{4})",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    // The search result list (search.php?q=…): <a href="/movie-607.html">Dune (1984)</a> — the year is
    // part of the link text here, not a separate cell.
    private static readonly Regex PlainMovieLink = new(
        "href=\\\"(?:/)?movie-(\\d+)\\.html\\\"[^>]*>([^<]+)</a>", RegexOptions.CultureInvariant);

    // One entry of a film page: the OUTER anchor carries title="Download … subtitles", the inner one
    // repeats the same id. Matching the outer one keeps one block per entry.
    private static readonly Regex EntryAnchor = new(
        "<a href=\"(?:/)?subtitle-(\\d+)\\.html\"[^>]*?title=\"Download[^\"]*\"[^>]*>\\s*<div class=\"subtitle\">",
        RegexOptions.CultureInvariant);

    private static readonly Regex FlagImage = new(
        "images/flags/([a-z]{1,6})\\.gif", RegexOptions.CultureInvariant);

    private static readonly Regex EntryTitle = new(
        "<b>(.*?)</b>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex CellRip = new(
        "<td[^>]*title=\"Rip\"[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex CellRelease = new(
        "<td[^>]*title=\"release\"[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex CellParts = new(
        "<td[^>]*title=\"parts\"[^>]*>\\s*(\\d*)\\s*</td>", RegexOptions.CultureInvariant);

    private static readonly Regex CellUploaded = new(
        "<td[^>]*title=\"uploaded\"[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex CellDownloads = new(
        "<td[^>]*title=\"downloaded\"[^>]*>\\s*(\\d*)\\s*</td>", RegexOptions.CultureInvariant);

    // The section heading of a language block: <span style="color:#666666"><b>English  subtitles:</b></span>
    private static readonly Regex SectionHeading = new(
        "<span style=\"color:#666666\"><b>(.*?)</b></span>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex MovieHeading = new(
        "<h2>(.*?)</h2>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex DownloadHeading = new(
        "<h2>\\s*Download\\s+(.*?)\\s+subtitles\\s*</h2>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex DescriptionBlock = new(
        "<p class=\"description\">(.*?)</p>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex InfoBlock = new(
        "<div class=\"info\">(.*?)</div>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex BackToMovie = new(
        "class=\"back_to_movie\"><a href=\"movie-(\\d*)\\.html\"", RegexOptions.CultureInvariant);

    private static readonly Regex DownloadLink = new(
        "href=\"download-(\\d+)\\.html\"", RegexOptions.CultureInvariant);

    // The subtitle page states several candidate names separated by a literal <n> (not <br>).
    private static readonly Regex FileNameSplit = new(
        "<n>|<br\\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TagStripper = new("<[^>]+>", RegexOptions.CultureInvariant);

    private static readonly Regex TrailingYear = new(
        "\\((\\d{4})\\)\\s*$", RegexOptions.CultureInvariant);

    // "Matrix, The" -> "the matrix": the site stores the article behind a comma.
    private static readonly Regex InvertedArticle = new(
        "^(.*),\\s*(the|a|an)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // F-M507: the article the site moves to the back — only the English ones, because only those are
    // what it inverts ("Matrix, The", not "Matrix, Der"). A German/French title keeps its article.
    private static readonly Regex LeadingArticle = new(
        "^(the|a|an)\\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieSubtitlesClient"/> class.
    /// </summary>
    /// <param name="http">The HttpClient to use. <see cref="SubdlApiClient.NewHttpClient"/> gives one
    /// that identifies itself as this plugin; a caller may pass its own for a test.</param>
    public MovieSubtitlesClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            // The site does not check the user agent (measured: same bytes with and without), but it
            // is a web page and a browser string is the honest default.
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (X11; Linux aarch64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
        }
    }

    /// <summary>
    /// F-M506: the film list of one letter, as the DOWNLOAD view shows it (<c>movies-&lt;L&gt;.html</c>).
    /// Only films with at least one entry appear here (487 for O against 492 in the upload view).
    /// </summary>
    /// <param name="letter">A..Z, or <c>#</c> for the digit list (the page calls it <c>movies-0.html</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The films in page order.</returns>
    public async Task<IReadOnlyList<MovieSubtitlesMovie>> MoviesAsync(
        char letter, CancellationToken cancellationToken = default)
    {
        var suffix = char.IsAsciiLetter(letter)
            ? char.ToUpperInvariant(letter).ToString()
            : "#";
        var slug = suffix == "#" ? "0" : suffix;
        var html = await GetTextAsync(BaseUrl + "/movies-" + slug + ".html", null, cancellationToken)
            .ConfigureAwait(false);
        return ParseMovieList(html);
    }

    /// <summary>
    /// F-M506: the site's own search. It answers <c>HTTP 500</c> and a VALID result list in the body —
    /// the status is therefore not read as an error here, only the body is used. An empty body or an
    /// empty list is an empty answer, not an exception; the caller decides what that means.
    /// </summary>
    /// <param name="query">The search term, in any spelling — the filter is a plain substring match.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The films the page lists.</returns>
    public async Task<IReadOnlyList<MovieSubtitlesMovie>> SearchAsync(
        string? query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("search term missing", nameof(query));
        }

        var url = BaseUrl + "/search.php?q=" + Uri.EscapeDataString(query.Trim());
        var html = await GetTextAsync(url, null, cancellationToken, tolerateErrorStatus: true)
            .ConfigureAwait(false);
        return ParseMovieList(html);
    }

    /// <summary>
    /// F-M506: the same list in the upload view (<c>add.php?filter=</c>) — the way the site itself finds
    /// the <c>tvid</c> of a film. The filter is a plain substring match somewhere in the title, so the
    /// answer wants a year comparison, not a name match (<c>filter=dark</c> answers 162 films).
    /// </summary>
    /// <param name="query">The filter term.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The films the page lists.</returns>
    public async Task<IReadOnlyList<MovieSubtitlesMovie>> FilterAsync(
        string? query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("filter term missing", nameof(query));
        }

        var url = BaseUrl + "/add.php?filter=" + Uri.EscapeDataString(query.Trim());
        var html = await GetTextAsync(url, null, cancellationToken).ConfigureAwait(false);
        return ParseMovieList(html);
    }

    /// <summary>
    /// F-M507 (Etappe 2a): der FILM zu einem TMDb-Titel und einem TMDb-Jahr — gesucht, eng verglichen,
    /// ohne Raten.
    /// <para>
    /// Die Suche der Seite ist ein Teilstring-Filter, kein Namensvergleich: <c>search.php?q=The
    /// Matrix</c> findet <c>Matrix, The</c> NICHT, weil die Seite den Artikel hinter den Komma stellt.
    /// Deshalb werden die Schreibweisen der Reihe nach gefragt (<see cref="SearchTerms"/>: der Titel, der
    /// Titel ohne führenden Artikel, die invertierte Form) und jede Antwort durch
    /// <see cref="ResolveMovie"/> geschickt, das Titel UND Jahr vergleicht und bei Mehrdeutigkeit nichts
    /// liefert.
    /// </para>
    /// <para>
    /// Gelesen wird ausschließlich die Suche (<c>search.php</c>), nie das Upload-Formular
    /// (<c>add.php</c>): der Download-Pfad betritt die Anlege-Seite nicht, auch nicht lesend mit einem
    /// Filter.
    /// </para>
    /// </summary>
    /// <param name="title">Der TMDb-Titel des Films.</param>
    /// <param name="year">Das TMDb-Jahr, oder null/0 wenn es unbekannt ist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Die Filmnummer, oder null (kein Treffer, mehr als ein Treffer, kein Titel).</returns>
    public async Task<int?> FindMovieAsync(
        string? title, int? year, CancellationToken cancellationToken = default)
    {
        foreach (var term in SearchTerms(title))
        {
            IReadOnlyList<MovieSubtitlesMovie> films;
            try
            {
                films = await SearchAsync(term, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Eine unlesbare Antwort ist kein Treffer — die nächste Schreibweise bekommt ihren
                // Versuch, und wenn keine trifft, bleibt es bei null. Der Aufrufer protokolliert.
                continue;
            }

            if (ResolveMovie(films, title, year) is { } id)
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// F-M507: die Schreibweisen, die für einen Titel gefragt werden — in der Reihenfolge ihres
    /// Versuchs.
    /// <para>
    /// <c>The Matrix</c> → <c>The Matrix</c>, <c>Matrix</c> (ohne führenden Artikel, weil der
    /// Teilstring-Filter der Seite <c>Matrix, The</c> so findet), <c>Matrix, The</c>
    /// (die Inversion, die die Seite selbst schreibt). Leer, wenn der Titel nichts hergibt — dann wird
    /// gar nicht gesucht.
    /// </para>
    /// </summary>
    /// <param name="title">Der Titel, wie er geschrieben steht.</param>
    /// <returns>Die Suchbegriffe, erster zuerst; leer ohne brauchbaren Titel.</returns>
    public static IReadOnlyList<string> SearchTerms(string? title)
    {
        var terms = new List<string>();
        var (name, _) = SplitTitleYear(title);
        if (name.Length < 2)
        {
            return terms;
        }

        void Add(string? term)
        {
            var trimmed = (term ?? string.Empty).Trim();
            if (trimmed.Length >= 2 && !terms.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                terms.Add(trimmed);
            }
        }

        Add(name);

        var leading = LeadingArticle.Match(name);
        if (leading.Success)
        {
            var rest = name[leading.Length..].Trim().TrimStart(',');
            Add(rest);

            // Die Schreibweise der Seite: "Matrix, The".
            Add(rest + ", " + leading.Groups[1].Value);
        }

        return terms;
    }
    /// <param name="movieId">The film id (the same number as the <c>tvid</c> of the upload form).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page as parsed. A film without subtitles answers HTTP 200 with an empty entry list.</returns>
    public async Task<MovieSubtitlesMoviePage> MovieAsync(
        int movieId, CancellationToken cancellationToken = default)
    {
        if (movieId <= 0)
        {
            throw new ArgumentException("movie id must be positive", nameof(movieId));
        }

        var html = await GetTextAsync(
            string.Format(CultureInfo.InvariantCulture, "{0}/movie-{1}.html", BaseUrl, movieId),
            null, cancellationToken).ConfigureAwait(false);
        return ParseMoviePage(html);
    }

    /// <summary>
    /// F-M506: one entry's own page — rip, release, file name(s), size, parts.
    /// </summary>
    /// <param name="subtitleId">The entry id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page, or null for the garbage an unknown id produces (HTTP 200 with an empty heading,
    /// an empty flag and <c>size: 9140 kb</c>).</returns>
    public async Task<MovieSubtitlesSubtitlePage?> SubtitleAsync(
        int subtitleId, CancellationToken cancellationToken = default)
    {
        if (subtitleId <= 0)
        {
            throw new ArgumentException("subtitle id must be positive", nameof(subtitleId));
        }

        var html = await GetTextAsync(
            string.Format(CultureInfo.InvariantCulture, "{0}/subtitle-{1}.html", BaseUrl, subtitleId),
            null, cancellationToken).ConfigureAwait(false);
        return ParseSubtitlePage(html);
    }

    /// <summary>
    /// F-M506: parses a film list. Three shapes land here because the site lists films in three views
    /// that share the ids but not the markup: the download list (<c>movies-&lt;L&gt;.html</c>), the search
    /// result list, and the upload list (<c>add.php?let=…</c>/<c>add.php?filter=…</c>).
    /// </summary>
    /// <param name="html">The page source.</param>
    /// <returns>The films in page order, one per id — a page that states nothing yields an empty list.</returns>
    /// <remarks>
    /// The year sits in its own cell in two of the shapes and inside the link text in the third; the LAST
    /// four-digit parenthesis wins, because a title may carry a year itself
    /// (<c>O Beautiful (2002)</c>). A name that repeats the row's year in parentheses loses that
    /// repetition, so the name compares against a TMDb title.
    /// </remarks>
    public static IReadOnlyList<MovieSubtitlesMovie> ParseMovieList(string html)
    {
        var films = new List<MovieSubtitlesMovie>();
        if (string.IsNullOrEmpty(html))
        {
            return films;
        }

        var seen = new HashSet<int>();

        void Add(int id, string rawName, int statedYear, IReadOnlyList<string> languages)
        {
            if (id <= 0 || !seen.Add(id))
            {
                return;
            }

            var (name, year) = SplitTitleYear(rawName);
            if (year <= 0)
            {
                year = statedYear;
            }

            films.Add(new MovieSubtitlesMovie(id, name, year, languages));
        }

        foreach (Match row in MovieRow.Matches(html))
        {
            if (!int.TryParse(row.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            var block = row.Groups[2].Value;
            var name = RowName.Match(block);
            var year = RowYear.Match(block);
            var languages = new List<string>();
            foreach (Match flag in FlagImage.Matches(block))
            {
                var code = flag.Groups[1].Value.ToLowerInvariant();
                if (Languages.ContainsKey(code) && !languages.Contains(code))
                {
                    languages.Add(code);
                }
            }

            var stated = year.Success
                && int.TryParse(year.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
                ? y
                : 0;
            Add(id, name.Success ? Clean(name.Groups[1].Value) : string.Empty, stated, languages);
        }

        // The upload view: id in the link, name in <b>, year as bare text behind the link.
        foreach (Match row in AddRow.Matches(html))
        {
            if (!int.TryParse(row.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || !int.TryParse(row.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
            {
                continue;
            }

            Add(id, Clean(row.Groups[2].Value), year, Array.Empty<string>());
        }

        // The search list: the year is part of the link text.
        foreach (Match link in PlainMovieLink.Matches(html))
        {
            if (!int.TryParse(link.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            var text = Clean(link.Groups[2].Value);
            if (text.Length == 0)
            {
                continue;
            }

            Add(id, text, 0, Array.Empty<string>());
        }

        return films;
    }

    /// <summary>
    /// F-M513: alle Filme einer Liste, deren Titel dem gesuchten entspricht — nach
    /// <see cref="TitleKey"/>, also mit rueckgaengig gemachter Artikelstellung (<c>Matrix, The</c> =
    /// <c>The Matrix</c>) und ohne ein wiederholtes Jahr. Die Liste ist die gemeinsame Grundlage beider
    /// Entscheidungen: <see cref="ResolveMovie"/> nimmt daraus die eindeutige Nummer, und der
    /// Doppeleintrag-Schutz (<see cref="ResolveForAdd"/>) sieht daraus, ob es den Titel schon gibt.
    /// </summary>
    /// <param name="candidates">The films from <see cref="ParseMovieList"/>.</param>
    /// <param name="title">Der gesuchte Titel, in beliebiger Artikelstellung.</param>
    /// <returns>Die Treffer in der Reihenfolge der Liste; leer, wenn keiner passt.</returns>
    public static IReadOnlyList<MovieSubtitlesMovie> TitleMatches(
        IReadOnlyList<MovieSubtitlesMovie> candidates, string? title)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var wanted = TitleKey(title);
        if (wanted.Length == 0)
        {
            return Array.Empty<MovieSubtitlesMovie>();
        }

        return candidates
            .Where(f => f.Id > 0 && string.Equals(TitleKey(f.Name), wanted, StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>
    /// F-M506: der Film zu einem Titel und einem Jahr — eng verglichen, nach derselben Normalisierung,
    /// die <see cref="TvSubtitlesClient.NormalizeShowName"/> fuer Serien anwendet (Gross-/Kleinschreibung
    /// egal, <c>&amp;</c>/<c>and</c>/<c>und</c> als dasselbe Wort, Leerraum zu einem Leerzeichen), dazu
    /// die Artikel-Inversion der Seite (<c>Matrix, The</c> = <c>The Matrix</c>) und das Jahr.
    /// <para>
    /// DIE REGEL BEI MEHRDEUTIGKEIT: bleibt nach Titel UND Jahr mehr als ein Kandidat, wird NICHTS
    /// geliefert. Die Seite hat keine Suche nach Jahr und keine Wunsch-Id; <c>filter=dune</c> antwortet
    /// fuenf Filme. Ein geratener Treffer waere eine Nummer, die im Store steht und einen anderen Film
    /// meint.
    /// </para>
    /// </summary>
    /// <param name="candidates">The films from <see cref="ParseMovieList"/>.</param>
    /// <param name="title">The title to look for — in practice the TMDb title, in either article order.</param>
    /// <param name="year">The year, or null/0 when it is unknown — then only a UNIQUE title match counts.</param>
    /// <returns>The film id, or null when nothing or more than one film matches.</returns>
    public static int? ResolveMovie(
        IReadOnlyList<MovieSubtitlesMovie> candidates, string? title, int? year)
    {
        int? found = null;
        var hits = 0;
        foreach (var film in TitleMatches(candidates, title))
        {
            // Ein falsches Jahr ist kein Treffer — auch nicht "ungefaehr".
            if (year is > 0 && film.Year != year.Value)
            {
                continue;
            }

            hits++;
            found = film.Id;
        }

        return hits == 1 ? found : null;
    }

    /// <summary>
    /// F-M513: die Entscheidung VOR dem Anlegen — aus einer Trefferliste, ohne Netz, ohne Store.
    /// <para>
    /// DIE DREI FAELLE, und nur diese:
    /// <list type="bullet">
    /// <item><description><b>gleiches Jahr</b> — der Titel steht mit <b>genau einer</b> Zeile dieses
    /// Jahres da: diese Nummer wird genommen, nichts angelegt.</description></item>
    /// <item><description><b>anderes Jahr</b> — der Titel steht da, aber nicht mit diesem Jahr (oder mit
    /// unbekanntem Jahr, oder mit mehr als einer Zeile): es wird <b>nichts</b> angelegt. Ein zweiter
    /// Eintrag fuer denselben Titel waere genau das Doppel, das der Nutzer nicht will.</description></item>
    /// <item><description><b>kein Treffer</b> — der Titel steht gar nicht da (und die Suche hat lesbar
    /// geantwortet): anlegen.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="candidates">Die Filme aus <see cref="ParseMovieList"/> — die Antwort EINER Suche.</param>
    /// <param name="title">Der Titel, wie er angelegt werden soll.</param>
    /// <param name="year">Das Jahr, das angelegt werden soll; 0 ist keine Angabe und wird abgewiesen.</param>
    /// <returns>Die Entscheidung samt Nummer bzw. gefundenem Jahr.</returns>
    public static MovieSubtitlesAddCheck ResolveForAdd(
        IReadOnlyList<MovieSubtitlesMovie> candidates, string? title, int year)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (year <= 0)
        {
            throw new ArgumentException("year missing — without it the comparison is not narrow", nameof(year));
        }

        // Eine Liste IST eine Antwort, auch die leere: sie kommt aus einer gelesenen Seite.
        return ClassifyForAdd(TitleMatches(candidates, title), year, answers: 1);
    }

    /// <summary>
    /// F-M513: die eine Stelle, an der aus den Titeltreffern eine Entscheidung wird — damit die
    /// Listen-Fassung (<see cref="ResolveForAdd"/>) und die Netz-Fassung
    /// (<see cref="CheckForAddAsync"/>) nicht auseinanderlaufen koennen.
    /// </summary>
    /// <param name="matches">Die Titel-Treffer, dublettenfrei.</param>
    /// <param name="year">Das gewuenschte Jahr.</param>
    /// <param name="answers">Wie viele Suchbegriffe lesbar geantwortet haben.</param>
    /// <returns>Die Entscheidung.</returns>
    private static MovieSubtitlesAddCheck ClassifyForAdd(
        IReadOnlyList<MovieSubtitlesMovie> matches, int year, int answers)
    {
        var sameYear = matches.Where(f => f.Year == year).ToArray();
        if (sameYear.Length == 1)
        {
            return new MovieSubtitlesAddCheck(
                MovieSubtitlesAddStatus.Take, sameYear[0].Id, year, matches.Count, answers);
        }

        if (matches.Count > 0)
        {
            // Kein EINDEUTIGER Treffer mit diesem Jahr, aber der Titel steht da: nicht anlegen. Das
            // gefundene Jahr geht mit, damit die Logzeile sagen kann, WAS da steht — bei mehreren
            // Zeilen desselben Jahres ist das Jahr selbst der Befund, und `Hits` nennt die Anzahl.
            var foundYear = sameYear.Length > 1 ? year : matches[0].Year;
            return new MovieSubtitlesAddCheck(
                MovieSubtitlesAddStatus.Refuse, null, foundYear, matches.Count, answers);
        }

        // Gar kein Titeltreffer — aber nur dann, wenn die Suche ueberhaupt lesbar geantwortet hat.
        // Eine Seite, die nicht antwortet, sagt nicht "gibt es nicht".
        return new MovieSubtitlesAddCheck(
            answers > 0 ? MovieSubtitlesAddStatus.Create : MovieSubtitlesAddStatus.Unclear,
            null, 0, 0, answers);
    }

    /// <summary>
    /// F-M506: the comparison key of a film title — the inversion undone, a repeated year removed, and
    /// then exactly the three adjustments of <see cref="TvSubtitlesClient.NormalizeShowName"/>.
    /// </summary>
    /// <param name="title">The title as written.</param>
    /// <returns>The key; empty when the title was empty.</returns>
    public static string TitleKey(string? title)
    {
        var (name, _) = SplitTitleYear(title);
        if (name.Length == 0)
        {
            return string.Empty;
        }

        var inverted = InvertedArticle.Match(name);
        if (inverted.Success)
        {
            // "Matrix, The" -> "The Matrix". Der Artikel wandert nach vorn, sonst vergleicht die
            // Seite eine Schreibweise gegen eine andere.
            name = inverted.Groups[2].Value + " " + inverted.Groups[1].Value;
        }

        return TvSubtitlesClient.NormalizeShowName(name);
    }

    /// <summary>
    /// F-M506: parses a film page — the heading and every entry, with the language taken from the FLAG
    /// IMAGE of its own block (the section heading and the word in the title are read as a cross-check,
    /// but the flag is what the entry carries).
    /// </summary>
    /// <param name="html">The source of <c>movie-&lt;id&gt;.html</c>.</param>
    /// <returns>The page. A film without subtitles yields no entries, not an error.</returns>
    /// <remarks>
    /// An entry whose block carries no flag is skipped: an entry whose language is unknown cannot be
    /// matched against a target language. The <c>filename</c> is NOT on this page — the site writes it
    /// only on the entry's own page; what the entry's title carries is the pattern
    /// <c>(&lt;Rip&gt;.&lt;Release&gt;)</c>, and that is parsed into the two cells instead.
    /// </remarks>
    public static MovieSubtitlesMoviePage ParseMoviePage(string html)
    {
        var entries = new List<MovieSubtitlesEntry>();
        var sections = new List<string>();
        if (string.IsNullOrEmpty(html))
        {
            return new MovieSubtitlesMoviePage(string.Empty, 0, 0, sections, entries);
        }

        foreach (Match heading in SectionHeading.Matches(html))
        {
            // "English  subtitles:" -> "English"; "Portugese(br)  subtitles:" -> "Portugese(br)".
            var label = Clean(heading.Groups[1].Value);
            label = Regex.Replace(label, ":\\s*$", string.Empty, RegexOptions.CultureInvariant).Trim();
            label = Regex.Replace(
                label, "\\s*subtitles\\s*$", string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
            if (label.Length == 0)
            {
                continue;
            }

            var code = Languages.Values.FirstOrDefault(
                l => string.Equals(l.Name, label, StringComparison.OrdinalIgnoreCase));
            var section = code?.Code ?? label.ToLowerInvariant();
            if (!sections.Contains(section))
            {
                sections.Add(section);
            }
        }

        var anchors = EntryAnchor.Matches(html);
        for (var i = 0; i < anchors.Count; i++)
        {
            var start = anchors[i].Index;
            var end = i + 1 < anchors.Count ? anchors[i + 1].Index : html.Length;
            var block = html[start..end];

            var flag = FlagImage.Match(block);
            if (!flag.Success)
            {
                continue;
            }

            var language = flag.Groups[1].Value.ToLowerInvariant();
            if (!Languages.ContainsKey(language))
            {
                continue;
            }

            var rip = CellRip.Match(block);
            var release = CellRelease.Match(block);
            var parts = CellParts.Match(block);
            var uploaded = CellUploaded.Match(block);
            var downloads = CellDownloads.Match(block);
            var title = EntryTitle.Match(block);

            entries.Add(new MovieSubtitlesEntry(
                anchors[i].Groups[1].Value,
                language,
                rip.Success ? Clean(rip.Groups[1].Value) : string.Empty,
                release.Success ? Clean(release.Groups[1].Value) : string.Empty,
                parts.Success ? ToInt(parts.Groups[1].Value) : 0,
                title.Success ? Clean(title.Groups[1].Value) : string.Empty,
                uploaded.Success ? Clean(uploaded.Groups[1].Value) : string.Empty,
                downloads.Success ? ToInt(downloads.Groups[1].Value) : 0));
        }

        var heading2 = MovieHeading.Match(html);
        var (name2, year2) = SplitTitleYear(heading2.Success ? Clean(heading2.Groups[1].Value) : string.Empty);
        var description = DescriptionBlock.Match(html);
        var downloads2 = description.Success ? ToInt(FirstNumber(description.Groups[1].Value)) : 0;

        return new MovieSubtitlesMoviePage(name2, year2, downloads2, sections, entries);
    }

    /// <summary>
    /// F-M506: parses one entry's own page (<c>subtitle-&lt;id&gt;.html</c>).
    /// </summary>
    /// <param name="html">The page source.</param>
    /// <returns>The page, or null when the page is the signature of an UNKNOWN id: HTTP 200 with an
    /// empty heading <c>Download () subtitles</c>, an empty flag <c>images/flags/.gif</c>, no file name
    /// and a <c>movie-.html</c> back link. That is not an answer, that is garbage.</returns>
    /// <remarks>
    /// The file name list is a HINT, not the archive's content: entry 2058 states <c>parts: 2</c> and
    /// five candidate names separated by a literal <c>&lt;n&gt;</c>, while the ZIP holds ONE file. Only
    /// the bytes decide what is inside (<see cref="DownloadAsync"/>).
    /// </remarks>
    public static MovieSubtitlesSubtitlePage? ParseSubtitlePage(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        var heading = DownloadHeading.Match(html);
        var (name, year) = SplitTitleYear(heading.Success ? Clean(heading.Groups[1].Value) : string.Empty);

        // The language sits in the description block; the right column carries flag images for the site
        // statistics as well, and reading the first flag of the whole page would land on those.
        var description = DescriptionBlock.Match(html);
        var language = string.Empty;
        if (description.Success)
        {
            var flag = FlagImage.Match(description.Groups[1].Value);
            if (flag.Success)
            {
                var code = flag.Groups[1].Value.ToLowerInvariant();
                language = Languages.ContainsKey(code) ? code : string.Empty;
            }
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var fileNames = new List<string>();
        foreach (Match info in InfoBlock.Matches(html))
        {
            var raw = info.Groups[1].Value;
            var text = Clean(raw);
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                var key = text[..colon].Trim().ToLowerInvariant();
                if (!fields.ContainsKey(key))
                {
                    fields[key] = text[(colon + 1)..].Trim();
                }
            }

            // The separator between two candidate names is a literal "<n>", so the split has to happen
            // BEFORE the tags are stripped — otherwise the names are glued together.
            if (text.StartsWith("filename:", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var part in FileNameSplit.Split(raw))
                {
                    var candidate = Clean(part);
                    if (candidate.Contains(':', StringComparison.Ordinal))
                    {
                        candidate = candidate[(candidate.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                    }

                    if (candidate.Length > 0 && !fileNames.Contains(candidate))
                    {
                        fileNames.Add(candidate);
                    }
                }
            }
        }

        var back = BackToMovie.Match(html);
        var movieId = back.Success ? ToInt(back.Groups[1].Value) : 0;
        var link = DownloadLink.Match(html);

        // Die Muellseite erkennt man an allem drei zusammen: keine Kennung, kein Dateiname, keine
        // Sprache. Fehlt nur eines davon, ist die Seite lesbar und der Aufrufer entscheidet (IsUsable).
        if (movieId <= 0 && fileNames.Count == 0 && language.Length == 0)
        {
            return null;
        }

        return new MovieSubtitlesSubtitlePage(
            movieId,
            name,
            year,
            language,
            fields.TryGetValue("rip", out var rip) ? rip : string.Empty,
            fields.TryGetValue("release", out var release) ? release : string.Empty,
            fileNames,
            fields.TryGetValue("parts", out var parts) ? ToInt(parts) : 0,
            fields.TryGetValue("size", out var size) ? size : string.Empty,
            fields.TryGetValue("uploaded", out var uploaded) ? uploaded : string.Empty,
            fields.TryGetValue("number of downloads", out var downloads) ? downloads : string.Empty,
            link.Success ? link.Groups[1].Value : string.Empty);
    }

    /// <summary>
    /// F-M506: the target of the download redirect — a RELATIVE <c>Location</c>, e.g.
    /// <c>files/Oppenheimer_2023.WEB-DL.MA.en.zip</c>, or with brackets because the name existed already:
    /// <c>files/Matrix_The_1999.ru(2).zip</c>. The brackets are part of the name and stay.
    /// </summary>
    /// <param name="location">The raw <c>Location</c> header value (or a whole URL, which is returned
    /// unchanged so a followed redirect and a raw header go through the same check).</param>
    /// <returns>The target as written, or null for the ERROR signature of an unknown id: a redirect to
    /// <c>files/</c> with no name at all.</returns>
    public static string? ParseDownloadTarget(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        var target = System.Net.WebUtility.HtmlDecode(location.Trim());
        if (target.Length == 0)
        {
            return null;
        }

        // "files/" (or "files") is what an unknown id redirects to: no name, therefore no file.
        var trimmed = target.TrimEnd('/');
        if (trimmed.EndsWith("/files", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "files", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Kein Endungs-Katalog: was die Seite schickt, entscheiden die Bytes (Zip oder nackte .srt).
        // Ein Ziel ohne Namen ist dagegen keine Datei.
        var cut = target.LastIndexOf('/');
        var name = cut >= 0 ? target[(cut + 1)..] : target;
        return name.Trim().Length == 0 ? null : target;
    }

    /// <summary>
    /// F-M506: makes an absolute address out of the redirect target. Brackets, commas and apostrophes
    /// stay literal (they are legal in a path and the site names its files that way); only what really
    /// has to be encoded is encoded — a space becomes <c>%20</c>, a non-ASCII byte its UTF-8 escape.
    /// </summary>
    /// <param name="target">The value from <see cref="ParseDownloadTarget"/>.</param>
    /// <returns>The absolute URL.</returns>
    public static string DownloadUrl(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var text = target.Trim();
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        return BaseUrl + "/" + EscapePath(text.TrimStart('/'));
    }

    /// <summary>
    /// F-M506: is this payload a ZIP? The local header <c>PK\u0003\u0004</c> decides — not a content
    /// type, which the site does not always set.
    /// </summary>
    /// <param name="payload">The bytes that came back.</param>
    /// <returns>True for a ZIP archive.</returns>
    public static bool IsZip(byte[] payload) =>
        payload is { Length: > 4 } && payload[0] == 0x50 && payload[1] == 0x4B
        && payload[2] == 0x03 && payload[3] == 0x04;

    /// <summary>
    /// F-M506: fetches one subtitle — the redirect, the bytes, the right file out of the ZIP.
    /// <para>
    /// The site answers <c>download-&lt;id&gt;.html</c> with a 302 to a RELATIVE <c>files/…</c>. The
    /// redirect is followed here (a handler may also follow it itself — then the final URI names the
    /// file); the bytes decide what came back, because the archive and a bare <c>.srt</c> both occur.
    /// </para>
    /// <para>
    /// WHICH FILE: an archive may hold one file, or several <c>.partN</c> segments of one film. Taken is
    /// the file that carries the language code in its name; if none does, the first one. The case is
    /// marked in the result (<see cref="MovieSubtitlesDownload.Members"/>,
    /// <see cref="MovieSubtitlesDownload.PickedByLanguage"/>, <see cref="MovieSubtitlesDownload.IsMultiPart"/>)
    /// rather than decided silently: <c>parts: 3</c> is a cut film, not a pack, and its segments are not
    /// interchangeable.
    /// </para>
    /// <para>
    /// The bytes are handed over UNTOUCHED — the archive carries UTF-8, decoding is the pipeline's
    /// business, not the transport's.
    /// </para>
    /// </summary>
    /// <param name="subtitleId">The entry id.</param>
    /// <param name="language">The site's language code, or null when the caller does not care which
    /// member of an archive it gets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The member name, its bytes, all member names and whether the choice was made by language.</returns>
    public async Task<MovieSubtitlesDownload> DownloadAsync(
        int subtitleId, string? language = null, CancellationToken cancellationToken = default)
    {
        if (subtitleId <= 0)
        {
            throw new ArgumentException("subtitle id must be positive", nameof(subtitleId));
        }

        var subtitlePage = string.Format(
            CultureInfo.InvariantCulture, "{0}/subtitle-{1}.html", BaseUrl, subtitleId);
        var downloadPage = string.Format(
            CultureInfo.InvariantCulture, "{0}/download-{1}.html", BaseUrl, subtitleId);

        // Two GETs in the site's own order: the download page belongs to the entry page.
        await GetBytesAsync(subtitlePage, null, cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(downloadPage, subtitlePage, cancellationToken).ConfigureAwait(false);

        byte[] payload;
        string? name = null;

        if (IsRedirect(response.StatusCode))
        {
            var target = ParseDownloadTarget(response.Headers.Location?.OriginalString)
                ?? throw new InvalidOperationException(
                    "the download page redirected to an empty target — unknown subtitle id (moviesubtitles.org): "
                    + subtitleId.ToString(CultureInfo.InvariantCulture));
            name = FileNameOf(target);
            payload = await GetBytesAsync(DownloadUrl(target), downloadPage, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            response.EnsureSuccessStatusCode();
            payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            // Ein Handler, der die Weiterleitung selbst folgt, liefert hier die Bytes der Datei — dann
            // nennt die letzte Adresse den Namen.
            var final = response.RequestMessage?.RequestUri?.OriginalString;
            name = ParseDownloadTarget(final) is { } resolved ? FileNameOf(resolved) : null;
        }

        if (!IsZip(payload))
        {
            // Nackte .srt: nichts auszupacken, der Name kommt aus der Adresse.
            var raw = name ?? ("subtitle-" + subtitleId.ToString(CultureInfo.InvariantCulture) + ".srt");
            return new MovieSubtitlesDownload(raw, payload, new[] { raw }, false);
        }

        using var stream = new MemoryStream(payload, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var members = archive.Entries
            .Where(e => e.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (members.Length == 0)
        {
            throw new InvalidOperationException(
                "no .srt inside the ZIP (moviesubtitles.org): "
                + string.Join(", ", archive.Entries.Take(3).Select(e => e.Name)));
        }

        var code = SiteLanguage(language);
        var member = members.FirstOrDefault(m => CarriesLanguage(m.Name, code)) ?? members[0];
        var picked = code is not null && CarriesLanguage(member.Name, code);

        using var entryStream = await member.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await entryStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new MovieSubtitlesDownload(
            member.Name,
            buffer.ToArray(),
            members.Select(m => m.Name).ToArray(),
            picked);
    }

    /// <summary>
    /// F-M506: the tag this client writes into the comment — the plugin name and the version that is
    /// actually running, read from the assembly so a build cannot carry a stale number.
    /// </summary>
    public static string ClientTag { get; } =
        "Sub Scribe " + (typeof(MovieSubtitlesClient).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");

    /// <summary>
    /// F-M506: the comment field of an upload — the TMDb and IMDb ids plus this client's tag, the same
    /// shape the tvsubtitles upload uses (F-M481). The site has no field for either id.
    /// </summary>
    /// <param name="tmdbId">The TMDb id, when known.</param>
    /// <param name="imdbId">The IMDb id, when known.</param>
    /// <param name="extra">Optional text, placed between the ids and the client tag.</param>
    /// <returns>The comment line, e.g. <c>TMDB 872585 / IMDb tt15398776 / Sub Scribe 12.1.12.394</c>.</returns>
    public static string Comment(long? tmdbId, string? imdbId, string? extra = null)
    {
        var parts = new List<string>();
        if (tmdbId is > 0)
        {
            parts.Add("TMDB " + tmdbId.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            parts.Add("IMDb " + imdbId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(extra))
        {
            parts.Add(extra.Trim());
        }

        // Operator order 17.11.2026: the comment carries the ids only — no client tag.
        return string.Join(" / ", parts);
    }

    /// <summary>
    /// F-M506: builds the upload form's parts and validates them against what the page accepts. Nothing
    /// is sent here, and no name is invented: what the form does not offer is refused with one line.
    /// </summary>
    /// <param name="movieId">The film id (the form's <c>tvid</c>) — 0 is refused, see remarks.</param>
    /// <param name="language">A code from <see cref="Languages"/>, an ISO code, or an ISO code with region.</param>
    /// <param name="releaseName">The release name of the media file — the RELEASE GROUP is taken from it
    /// through <see cref="SeasonPack.ReleaseGroup"/> and goes into the <c>release</c> field (F-M505); the
    /// file name itself is NOT sent. A name that states no group sends an empty field.</param>
    /// <param name="fileName">The file name of the upload part; must end in .srt or .zip.</param>
    /// <param name="data">The bytes of the subtitle.</param>
    /// <param name="rip">A value from <see cref="Rips"/>; empty and null are allowed.</param>
    /// <param name="author">Free text for the <c>author</c> field — OMITTED when empty, see remarks.</param>
    /// <param name="tmdbId">The TMDb id — the comment carries the ids, so at least one id is required.</param>
    /// <param name="imdbId">The IMDb id.</param>
    /// <param name="commentExtra">Optional trailing comment text.</param>
    /// <param name="extraFiles">At most two further parts for <c>UFile2</c>/<c>UFile3</c> (multi-CD
    /// uploads). Empty parts are never sent.</param>
    /// <returns>The finished upload payload.</returns>
    /// <remarks>
    /// The field order is the form's own: <c>tvid lang rip release author UFile UFile2 UFile3 comments go</c>.
    /// The AUTHOR field is left out entirely when it is empty — an absent multipart part is the only way
    /// to guarantee that whatever the page pre-fills is not overwritten. <c>go</c> is always <c>1</c>,
    /// exactly as the form's hidden field.
    /// </remarks>
    public static MovieSubtitlesUpload BuildUpload(
        int movieId,
        string? language,
        string? releaseName,
        string fileName,
        byte[] data,
        string? rip = null,
        string? author = null,
        long? tmdbId = null,
        string? imdbId = null,
        string? commentExtra = null,
        IReadOnlyList<MovieSubtitlesUploadFile>? extraFiles = null)
    {
        var problems = new List<string>();
        if (movieId <= 0)
        {
            problems.Add("movie id (tvid) must come from the film list (0 is refused)");
        }

        var siteLanguage = SiteLanguage(language);
        if (siteLanguage is null)
        {
            problems.Add("language must be one of the site's codes (ISO codes are mapped): "
                + string.Join(", ", Languages.Keys.OrderBy(k => k, StringComparer.Ordinal)));
        }

        var ripValue = rip ?? string.Empty;
        if (!Rips.Contains(ripValue))
        {
            problems.Add("rip must be empty or one of: "
                + string.Join(", ", Rips.Where(r => r.Length > 0)));
        }

        if (tmdbId is null && string.IsNullOrWhiteSpace(imdbId))
        {
            problems.Add("tmdb or imdb id required — the comments field carries them");
        }

        if (data is null || data.Length == 0)
        {
            problems.Add("subtitle file is empty");
        }

        var name = string.IsNullOrWhiteSpace(fileName) ? "subtitle.srt" : fileName.Trim();
        if (!IsSubtitleName(name))
        {
            problems.Add("file name must end in .srt or .zip");
        }

        var extras = extraFiles ?? Array.Empty<MovieSubtitlesUploadFile>();
        if (extras.Count > 2)
        {
            problems.Add("at most two further files — the form has three slots (UFile, UFile2, UFile3)");
        }

        foreach (var extra in extras)
        {
            if (!IsSubtitleName(extra.FileName))
            {
                problems.Add("extra file name must end in .srt or .zip: " + extra.FileName);
            }

            if (extra.Data is null || extra.Data.Length == 0)
            {
                problems.Add("extra file is empty: " + extra.FileName);
            }
        }

        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", problems), "upload");
        }

        var group = SeasonPack.ReleaseGroup(releaseName) ?? string.Empty;

        var parts = new List<MovieSubtitlesUploadPart>
        {
            MovieSubtitlesUploadPart.Field("tvid", movieId.ToString(CultureInfo.InvariantCulture)),
            MovieSubtitlesUploadPart.Field("lang", siteLanguage!),
            MovieSubtitlesUploadPart.Field("rip", ripValue),
            MovieSubtitlesUploadPart.Field("release", group),
        };

        // Fehlt, wenn leer: ein fehlender Teil ueberschreibt keine Vorbelegung der Seite.
        if (!string.IsNullOrWhiteSpace(author))
        {
            parts.Add(MovieSubtitlesUploadPart.Field("author", author.Trim()));
        }

        parts.Add(MovieSubtitlesUploadPart.File("UFile", name, data!));
        if (extras.Count > 0)
        {
            parts.Add(MovieSubtitlesUploadPart.File("UFile2", extras[0].FileName.Trim(), extras[0].Data));
        }

        if (extras.Count > 1)
        {
            parts.Add(MovieSubtitlesUploadPart.File("UFile3", extras[1].FileName.Trim(), extras[1].Data));
        }

        parts.Add(MovieSubtitlesUploadPart.Field("comments", Comment(tmdbId, imdbId, commentExtra)));
        parts.Add(MovieSubtitlesUploadPart.Field("go", "1"));

        return new MovieSubtitlesUpload(parts, Languages[siteLanguage!].Name, siteLanguage!, group);
    }

    /// <summary>
    /// F-M506: builds the multipart body of an upload and hands back the boundary with it. Exposed so a
    /// suite can assert the exact bytes without a network call.
    /// </summary>
    /// <param name="upload">The payload from <see cref="BuildUpload"/>.</param>
    /// <returns>The body and its boundary.</returns>
    public static (byte[] Body, string Boundary) BuildUploadBody(MovieSubtitlesUpload upload)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var boundary = "----sub-scribe" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        using var buffer = new MemoryStream();

        void Write(string text) => buffer.Write(Encoding.UTF8.GetBytes(text));

        foreach (var part in upload.Parts)
        {
            Write("--" + boundary + "\r\n");
            if (part.FileName is null)
            {
                Write("Content-Disposition: form-data; name=\"" + part.Name + "\"\r\n\r\n");
                Write(part.Value + "\r\n");
                continue;
            }

            Write("Content-Disposition: form-data; name=\"" + part.Name + "\"; filename=\"" + part.FileName + "\"\r\n");
            Write("Content-Type: application/octet-stream\r\n\r\n");
            if (part.Data is { Length: > 0 })
            {
                buffer.Write(part.Data, 0, part.Data.Length);
            }

            Write("\r\n");
        }

        Write("--" + boundary + "--\r\n");
        return (buffer.ToArray(), boundary);
    }

    /// <summary>
    /// F-M506: sends the upload to <c>add.php</c>. Without <paramref name="dryRun"/> false nothing
    /// leaves the process — the body is built either way, so a dry run measures exactly what a real one
    /// would send. The dry-run flag comes from the caller and is never read from the configuration here.
    /// </summary>
    /// <param name="upload">The payload from <see cref="BuildUpload"/>.</param>
    /// <param name="dryRun">True (default) builds the body and sends nothing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result, including the byte count and the response text.</returns>
    public async Task<MovieSubtitlesUploadResult> UploadAsync(
        MovieSubtitlesUpload upload, bool dryRun = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var (body, boundary) = BuildUploadBody(upload);
        if (dryRun)
        {
            return new MovieSubtitlesUploadResult(false, null, null, body.Length, boundary);
        }

        var tvid = upload.Parts.FirstOrDefault(p => p.Name == "tvid")?.Value ?? "0";
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/add.php");
        request.Headers.Referrer = new Uri(BaseUrl + "/add.php?tvid=" + tvid);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType =
            System.Net.Http.Headers.MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + boundary);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var plain = TagStripper.Replace(Regex.Replace(text, "\\s+", " ", RegexOptions.CultureInvariant), " ");
        return new MovieSubtitlesUploadResult(
            true, (int)response.StatusCode, plain.Trim(), body.Length, boundary);
    }

    // ---- F-M513: einen FILM anlegen ---------------------------------------------------------------
    //
    // Nutzerauftrag 16.11.2026 („Dann einmal mit neu anlegen testen"): ein Film, den die Seite NICHT
    // fuehrt, soll beim Upload angelegt und der Untertitel dazu hochgeladen werden.
    //
    // BELEGT ist nur das Formular (aufgenommen 15.11.2026, docs/MOVIESUBTITLES.md): GET /add-movie.php
    // liefert es, POST /add-movie.php nimmt `name`, `year` (maxlength=4) und `go`. UNBELEGT ist die
    // ANTWORT auf diesen POST — ob die Seite eine Film-Id zurueckgibt, ob sie weiterleitet, ob sie ueber
    //haupt annimmt. Deshalb wird hier NICHTS geraten: nach dem POST wird nachgesehen
    // (CheckForAddAsync, dieselbe enge Suche wie vorher), und wenn die Nachsuche den Film nicht nennt,
    // heisst das Ergebnis „angelegt, Id nicht lesbar" — mit dem Text der Antwort daneben.

    /// <summary>
    /// F-M513: das aelteste Jahr, das als Filmjahr plausibel ist — 1888 (der erste Film).
    /// </summary>
    public const int MinAddMovieYear = 1888;

    /// <summary>
    /// F-M513: das juengste Jahr, das das Formular annimmt — das laufende Jahr plus drei. Filme werden
    /// angekuendigt, bevor sie laufen; „+3" deckt diesen Vorlauf ab und laesst die Pruefung trotzdem
    /// eine Zahl bleiben statt einer offenen Tuer. Das Formular selbst sagt nur <c>maxlength=4</c>.
    /// </summary>
    public static int MaxAddMovieYear => DateTime.UtcNow.Year + 3;

    /// <summary>
    /// F-M513: baut das Anlege-Formular (<c>add-movie.php</c>) — die Felder in der Reihenfolge der Seite:
    /// <c>name</c>, <c>year</c>, <c>go</c>. Nichts wird gesendet, keine Nummer erfunden.
    /// <para>
    /// GEPRUEFT WIRD VOR DEM SENDEN: ein leerer Name und ein Jahr ausserhalb
    /// <see cref="MinAddMovieYear"/>..<see cref="MaxAddMovieYear"/> werden mit
    /// <see cref="ArgumentException"/> abgewiesen — dann entsteht keine Anfrage. Ein einbuchstabiger
    /// Titel ist erlaubt (es gibt Filme, die so heissen).
    /// </para>
    /// </summary>
    /// <param name="name">Der Filmtitel, wie er angelegt werden soll.</param>
    /// <param name="year">Das Jahr, vierstellig, plausibel.</param>
    /// <returns>Die fertige Nutzlast.</returns>
    public static MovieSubtitlesAddMovie BuildAddMovie(string name, int year)
    {
        var problems = new List<string>();
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            problems.Add("name missing");
        }

        if (year < MinAddMovieYear || year > MaxAddMovieYear)
        {
            problems.Add("year must be " + MinAddMovieYear.ToString(CultureInfo.InvariantCulture)
                + ".." + MaxAddMovieYear.ToString(CultureInfo.InvariantCulture));
        }

        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", problems), nameof(name));
        }

        var parts = new List<MovieSubtitlesUploadPart>
        {
            MovieSubtitlesUploadPart.Field("name", clean),
            MovieSubtitlesUploadPart.Field("year", year.ToString(CultureInfo.InvariantCulture)),
            MovieSubtitlesUploadPart.Field("go", "1"),
        };

        return new MovieSubtitlesAddMovie(clean, year, parts);
    }

    /// <summary>
    /// F-M513: der multipart-Rumpf des Anlege-Formulars, mit seinem Boundary. Herausgeloest, damit eine
    /// Suite die Bytes ohne Netz pruefen kann — und damit Trockenlauf und Ernstfall denselben Rumpf
    /// bauen (er wird EINMAL gebaut, vor der Trockenlauf-Weiche).
    /// </summary>
    /// <param name="addMovie">Die Nutzlast aus <see cref="BuildAddMovie"/>.</param>
    /// <returns>Der Rumpf und sein Boundary.</returns>
    public static (byte[] Body, string Boundary) BuildAddMovieBody(MovieSubtitlesAddMovie addMovie)
    {
        ArgumentNullException.ThrowIfNull(addMovie);
        var boundary = "----sub-scribe" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        using var buffer = new MemoryStream();

        foreach (var part in addMovie.Parts)
        {
            buffer.Write(Encoding.UTF8.GetBytes(
                "--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + part.Name + "\"\r\n\r\n"
                + part.Value + "\r\n"));
        }

        buffer.Write(Encoding.UTF8.GetBytes("--" + boundary + "--\r\n"));
        return (buffer.ToArray(), boundary);
    }

    /// <summary>
    /// F-M515: der Wortlaut der Seite, wenn sie einen Film WIRKLICH angelegt hat. LIVE BELEGT am
    /// 16.11.2026 (ein echter POST, multipart, <c>go=1</c>, <c>/add-movie.php</c>): HTTP 200,
    /// 8467 Bytes, im Rumpf
    /// <c>&lt;div class="subtitle2"&gt;&lt;b&gt;Movie has been added. Thank you.&lt;/b&gt;&lt;/div&gt;</c> —
    /// kein <c>Location</c>-Kopf, keine Film-Id, kein Link auf den neuen Film.
    /// </summary>
    public const string AddMovieCreatedMarker = "Movie has been added";

    /// <summary>
    /// F-M515: der Wortlaut, mit dem die Seite ein ZWEITES Anlegen desselben Titels abweist. LIVE
    /// BELEGT am 16.11.2026 (zweiter POST desselben Namens): HTTP 200, 8459 Bytes,
    /// <c>&lt;div class="subtitle2"&gt;&lt;b&gt;This Movie already exist&lt;/b&gt;&lt;/div&gt;</c>. Die Seite
    /// schreibt „exist", nicht „exists" — der Marker ist der ihre.
    /// <para>
    /// DIESER SATZ IST KEIN BELEG DAFÜR, DASS DER FILM AUFFINDBAR IST: derselbe Satz kam für einen Film,
    /// den Suche, Buchstabenliste und <c>add.php?filter=</c> nicht führen (live gemessen). Er belegt nur,
    /// dass die Seite die Anlage verweigert — die Nummer kommt deshalb weiter aus der Nachsuche, nie aus
    /// dieser Antwort.
    /// </para>
    /// </summary>
    public const string AddMovieExistsMarker = "This Movie already exist";

    /// <summary>
    /// F-M515: was die Antwort auf <c>POST /add-movie.php</c> BESAGT — und nur das, was die zwei
    /// belegten Marker besagen.
    /// <para>
    /// <see cref="MovieSubtitlesAddOutcome.Created"/> nur beim Marker „Movie has been added“;
    /// <see cref="MovieSubtitlesAddOutcome.Exists"/> beim Marker „This Movie already exist“;
    /// <b>alles andere</b> ist <see cref="MovieSubtitlesAddOutcome.Rejected"/> — auch ein 200 mit dem
    /// unveränderten Formular (live gemessen: 8399 Bytes, keine Meldung, kein Eintrag). Es gibt keinen
    /// dritten belegten Satz; eine Ablehnung wird deshalb nicht erfunden, sondern als „kein Marker“
    /// gemeldet, mit dem Textauszug daneben.
    /// </para>
    /// </summary>
    /// <param name="plainText">Der Antworttext, Tags und Leerraum zusammengezogen.</param>
    /// <returns>Einer der Werte aus <see cref="MovieSubtitlesAddOutcome"/>.</returns>
    public static string ClassifyAddMovieAnswer(string? plainText)
    {
        var text = plainText ?? string.Empty;
        if (text.Contains(AddMovieCreatedMarker, StringComparison.OrdinalIgnoreCase))
        {
            return MovieSubtitlesAddOutcome.Created;
        }

        if (text.Contains(AddMovieExistsMarker, StringComparison.OrdinalIgnoreCase))
        {
            return MovieSubtitlesAddOutcome.Exists;
        }

        return MovieSubtitlesAddOutcome.Rejected;
    }

    /// <summary>F-M515: der Anfang des Antworttexts, damit eine Ablehnung ihren Grund mitbringt.</summary>
    private static string Excerpt(string? text)
    {
        var clean = (text ?? string.Empty).Trim();
        return clean.Length <= 200 ? clean : clean[..200] + "…";
    }

    /// <summary>
    /// F-M513/F-M515: legt einen Film an (<c>POST /add-movie.php</c>), deutet die Antwort an ihren
    /// belegten Markern und sieht danach nach, welche Nummer er bekommen hat.
    /// <para>
    /// DIESE METHODE PRUEFT NICHT, OB ES DEN FILM SCHON GIBT — das tut <see cref="CheckForAddAsync"/>,
    /// und der Aufrufer fragt sie vorher. Sie legt an, was man ihr gibt.
    /// </para>
    /// <para>
    /// DIE ANTWORT WIRD GELESEN, NICHT GEDEUTET (F-M515, live belegt): „Movie has been added“ heißt
    /// angelegt, „This Movie already exist“ heißt verweigert. Bei BEIDEN läuft genau EINE Nachsuche —
    /// dieselbe enge Suche wie vorher (Titel und Jahr, alle Schreibweisen) —, und die Nummer kommt aus
    /// ihrer Trefferliste, nie aus der Antwort (die nennt keine). Ein zweiter POST fällt nie: „existiert
    /// schon“ führt zur Suche, nicht zu einem zweiten Anlage-Versuch und nicht zu einem Fehler.
    /// </para>
    /// <para>
    /// KEIN MARKER IST KEINE ANLAGE: alles, was weder der eine noch der andere Satz ist — das
    /// unveränderte Formular eingeschlossen —, ist
    /// <see cref="MovieSubtitlesAddOutcome.Rejected"/> mit <c>Ok = false</c> und dem Textauszug als
    /// Grund. Ein HTTP-Fehler ist <see cref="MovieSubtitlesAddOutcome.HttpError"/> mit dem Status als
    /// Grund.
    /// </para>
    /// <para>
    /// ANNAHME, DIE DER LIVE-TEST GEPRUEFT HAT UND DIE NICHT HIELT: dass die Seite einen angenommenen
    /// Film sofort unter demselben Titel in ihrer Suche führt. Gemessen am 16.11.2026 führte sie ihn
    /// NICHT (Suche, Buchstabenliste und <c>add.php?filter=</c> blieben leer) — das Ergebnis ist dann
    /// <see cref="MovieSubtitlesAddOutcome.CreatedIdUnknown"/>: „angelegt, Id nicht lesbar“, mit dem
    /// Text der Antwort daneben und ohne Nummer.
    /// </para>
    /// </summary>
    /// <param name="addMovie">Die Nutzlast aus <see cref="BuildAddMovie"/>.</param>
    /// <param name="dryRun">True (Vorgabe) baut den Rumpf und sendet nichts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Das Ergebnis: angelegt, angelegt-ohne-lesbare-Id, existiert-schon oder abgelehnt.</returns>
    public async Task<MovieSubtitlesAddMovieResult> AddMovieAsync(
        MovieSubtitlesAddMovie addMovie, bool dryRun = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addMovie);
        var (body, boundary) = BuildAddMovieBody(addMovie);
        if (dryRun)
        {
            // Derselbe Rumpf wie im Ernstfall, kein Byte auf der Leitung.
            return new MovieSubtitlesAddMovieResult(
                false, null, null, null, body.Length, boundary, MovieSubtitlesAddOutcome.DryRun, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/add-movie.php");
        request.Headers.Referrer = new Uri(BaseUrl + "/add-movie.php");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType =
            System.Net.Http.Headers.MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + boundary);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var plain = TagStripper.Replace(Regex.Replace(text, "\\s+", " ", RegexOptions.CultureInvariant), " ")
            .Trim();

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            return new MovieSubtitlesAddMovieResult(
                false, status, "HTTP " + status.ToString(CultureInfo.InvariantCulture), plain,
                body.Length, boundary, MovieSubtitlesAddOutcome.HttpError, null);
        }

        var verdict = ClassifyAddMovieAnswer(plain);
        if (verdict == MovieSubtitlesAddOutcome.Rejected)
        {
            // Kein Marker: die Seite hat NICHT angelegt — dann wird auch nicht nachgesehen, denn es gibt
            // nichts zu finden. „Angelegt“ behauptet nur, wer den Satz der Seite gelesen hat.
            return new MovieSubtitlesAddMovieResult(
                false, (int)response.StatusCode, "no creation marker — " + Excerpt(plain), plain,
                body.Length, boundary, MovieSubtitlesAddOutcome.Rejected, null);
        }

        // Beide belegten Antworten fuehren zu GENAU EINER Nachsuche — und zu keinem zweiten POST.
        var check = await TryCheckForAddAsync(addMovie, cancellationToken).ConfigureAwait(false);
        var id = check.Status == MovieSubtitlesAddStatus.Take ? check.MovieId : null;

        if (verdict == MovieSubtitlesAddOutcome.Exists)
        {
            // Die Seite verweigert die Anlage. Ob sie den Film fuehrt, sagt allein die Nachsuche; nennt
            // sie ihn nicht, bleibt es bei „existiert schon, Id nicht lesbar“ — kein zweiter Versuch.
            return new MovieSubtitlesAddMovieResult(
                false, (int)response.StatusCode, AddMovieExistsMarker, plain, body.Length, boundary,
                id is > 0 ? MovieSubtitlesAddOutcome.Exists : MovieSubtitlesAddOutcome.ExistsUnknownId,
                id);
        }

        return new MovieSubtitlesAddMovieResult(
            true,
            (int)response.StatusCode,
            null,
            plain,
            body.Length,
            boundary,
            id is > 0 ? MovieSubtitlesAddOutcome.Created : MovieSubtitlesAddOutcome.CreatedIdUnknown,
            id);
    }

    /// <summary>
    /// F-M515: die Nachsuche nach dem Anlegen — einmal, für beide belegten Antworten. Eine gescheiterte
    /// Suche ist keine Nummer: sie ergibt <see cref="MovieSubtitlesAddStatus.Unclear"/>.
    /// </summary>
    private async Task<MovieSubtitlesAddCheck> TryCheckForAddAsync(
        MovieSubtitlesAddMovie addMovie, CancellationToken cancellationToken)
    {
        try
        {
            return await CheckForAddAsync(addMovie.Name, addMovie.Year, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new MovieSubtitlesAddCheck(MovieSubtitlesAddStatus.Unclear, null, 0, 0, 0);
        }
    }

    /// <summary>
    /// F-M513: die eine Suche VOR dem Anlegen — und zugleich die Aufloesung. Sie fragt die
    /// Schreibweisen aus <see cref="SearchTerms"/> der Reihe nach (der Titel, der Titel ohne fuehrenden
    /// Artikel, die invertierte Form <c>Matrix, The</c>), sammelt die Titeltreffer ein und entscheidet
    /// nach <see cref="ResolveForAdd"/>.
    /// <para>
    /// WARUM EINE SUCHRUNDE UND NICHT ZWEI: „vor dem Anlegen genau einmal suchen" heisst, dass die
    /// Frage „welche Nummer hat der Film" und die Frage „gibt es ihn schon" dieselbe Antwort benutzen.
    /// Zwei Runden waeren zwei Antworten, und die zweite koennte die erste widerlegen.
    /// </para>
    /// <para>
    /// EINE UNLESBARE ANTWORT IST KEINE ANTWORT: erst wenn mindestens eine Schreibweise lesbar
    /// geantwortet hat, darf „gibt es nicht" gelten (<see cref="MovieSubtitlesAddStatus.Create"/>).
    /// Antwortet keine, ist das Ergebnis <see cref="MovieSubtitlesAddStatus.Unclear"/> — und dann wird
    /// nichts angelegt.
    /// </para>
    /// </summary>
    /// <param name="title">Der Filmtitel.</param>
    /// <param name="year">Das Jahr; 0 ist keine Angabe und wird abgewiesen.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Die Entscheidung; <see cref="MovieSubtitlesAddCheck.Answers"/> nennt die lesbaren Antworten.</returns>
    public async Task<MovieSubtitlesAddCheck> CheckForAddAsync(
        string? title, int year, CancellationToken cancellationToken = default)
    {
        if (year <= 0)
        {
            throw new ArgumentException("year missing — without it the comparison is not narrow", nameof(year));
        }

        var terms = SearchTerms(title);
        if (terms.Count == 0)
        {
            throw new ArgumentException("title missing — nothing to look for", nameof(title));
        }

        var matches = new List<MovieSubtitlesMovie>();
        var seen = new HashSet<int>();
        var answers = 0;
        foreach (var term in terms)
        {
            IReadOnlyList<MovieSubtitlesMovie> films;
            try
            {
                films = await SearchAsync(term, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Diese Schreibweise war nicht lesbar. Sie zaehlt NICHT als Antwort — die naechste
                // bekommt ihren Versuch, und ob ueberhaupt etwas lesbar war, sagt `answers`.
                continue;
            }

            answers++;
            foreach (var film in TitleMatches(films, title))
            {
                if (seen.Add(film.Id))
                {
                    matches.Add(film);
                }
            }

            // Ein Treffer mit DIESEM Jahr beendet die Suche: eine weitere Schreibweise kann die Antwort
            // nur wiederholen, und die Seite ist ein Nebenamt, kein Cache.
            if (matches.Any(f => f.Year == year))
            {
                break;
            }
        }

        return ClassifyForAdd(matches, year, answers);
    }

    private static bool IsSubtitleName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && (name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    private static bool IsRedirect(System.Net.HttpStatusCode status) => status is
        System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.Found
        or System.Net.HttpStatusCode.SeeOther or System.Net.HttpStatusCode.TemporaryRedirect
        or System.Net.HttpStatusCode.PermanentRedirect;

    /// <summary>Der Dateiname aus einem Ziel — Klammern, Punkte und Umlaute bleiben, wie sie stehen.</summary>
    private static string FileNameOf(string target)
    {
        var cut = target.LastIndexOf('/');
        var name = cut >= 0 ? target[(cut + 1)..] : target;
        try
        {
            return Uri.UnescapeDataString(name);
        }
        catch (UriFormatException)
        {
            return name;
        }
    }

    /// <summary>Traegt der Dateiname das Sprachkuerzel als eigenes Segment (<c>…MA.en.srt</c>, <c>…part1.ru.srt</c>)?</summary>
    private static bool CarriesLanguage(string fileName, string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return false;
        }

        var name = fileName.ToLowerInvariant();
        var needle = "." + code.ToLowerInvariant();
        return name.Contains(needle + ".", StringComparison.Ordinal)
            || name.EndsWith(needle, StringComparison.Ordinal);
    }

    /// <summary>Percent-encodes only what a path cannot carry; brackets, commas and apostrophes stay.</summary>
    private static string EscapePath(string path)
    {
        const string Keep = "-._~/,()'!&+@;=:$";
        var builder = new StringBuilder(path.Length + 8);
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || Keep.Contains(c, StringComparison.Ordinal))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>"O Beautiful (2002) (2002)" -> ("O Beautiful", 2002) — die LETZTE Jahresklammer zaehlt.</summary>
    private static (string Name, int Year) SplitTitleYear(string? text)
    {
        var name = Clean(text ?? string.Empty);
        var m = TrailingYear.Match(name);
        if (!m.Success
            || !int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
        {
            return (name, 0);
        }

        name = name[..m.Index].Trim();

        // Steht dasselbe Jahr ein zweites Mal davor, ist es die Wiederholung des Titels, nicht sein Text.
        var again = TrailingYear.Match(name);
        if (again.Success && again.Groups[1].Value == m.Groups[1].Value)
        {
            name = name[..again.Index].Trim();
        }

        return (name, year);
    }

    private static string Clean(string html)
    {
        var text = System.Net.WebUtility.HtmlDecode(TagStripper.Replace(html, string.Empty));
        return Regex.Replace(text, "\\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static string FirstNumber(string html)
    {
        var m = Regex.Match(html, "\\d+", RegexOptions.CultureInvariant);
        return m.Success ? m.Value : "0";
    }

    private static int ToInt(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private async Task<string> GetTextAsync(
        string url, string? referer, CancellationToken cancellationToken, bool tolerateErrorStatus = false)
    {
        using var response = await SendAsync(url, referer, cancellationToken).ConfigureAwait(false);
        if (!tolerateErrorStatus)
        {
            response.EnsureSuccessStatusCode();
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private async Task<byte[]> GetBytesAsync(string url, string? referer, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(url, referer, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, string? referer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(referer))
        {
            request.Headers.Referrer = new Uri(referer);
        }

        // A caller that follows redirects itself gets the file bytes straight from here; one that does
        // not (a test handler) gets the 302 and its Location. Both are read below.
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// F-M506: one language of the site's own table — the select value, the display name as the form spells
/// it, and the code the site writes into the download name.
/// </summary>
/// <param name="Code">The value of <c>&lt;option value="…"&gt;</c>, which is also what the upload sends.</param>
/// <param name="Name">The display name, verbatim — <c>portugese(br)</c> keeps the site's spelling.</param>
/// <param name="FileCode">The code as it appears in <c>files/…</c>; measured for <c>en</c> and <c>ru</c>,
/// and by construction the select value itself (this site does not use an ISO3 form).</param>
public sealed record MovieSubtitlesLanguage(string Code, string Name, string FileCode);

/// <summary>
/// F-M506: one film of a list — the id is the <c>tvid</c> of the upload form, one number, global.
/// </summary>
/// <param name="Id">The film id.</param>
/// <param name="Name">The name as the site writes it, with the article behind a comma when it has one.</param>
/// <param name="Year">The four-digit year, 0 when the row states none.</param>
/// <param name="Languages">The language codes of the row's flags; empty in the search and upload views.</param>
public sealed record MovieSubtitlesMovie(int Id, string Name, int Year, IReadOnlyList<string> Languages);

/// <summary>
/// F-M506: one entry of a film page.
/// </summary>
/// <param name="SubtitleId">The id used by <c>subtitle-&lt;id&gt;.html</c> and <c>download-&lt;id&gt;.html</c>.</param>
/// <param name="Language">The site's code, taken from the flag image of the entry's own block.</param>
/// <param name="Rip">The rip cell, empty when the entry has none — and free values occur
/// (<c>1080p</c>, <c>720p.BlueRay</c>), which is why reading never insists on the nine form values.</param>
/// <param name="Release">The release cell, often empty although the title states it.</param>
/// <param name="Parts">The <c>parts</c> cell — 1 for a single file, 2..3 for a cut film.</param>
/// <param name="Title">The entry's title line, which carries the site's own pattern
/// <c>&lt;Titel&gt; &lt;Jahr&gt; &lt;sprache&gt; subtitles (&lt;Rip&gt;.&lt;Release&gt;)</c>.</param>
/// <param name="Uploaded">The upload timestamp as written.</param>
/// <param name="Downloads">The download counter, 0 when absent.</param>
public sealed record MovieSubtitlesEntry(
    string SubtitleId, string Language, string Rip, string Release, int Parts,
    string Title, string Uploaded, int Downloads);

/// <summary>
/// F-M506: a parsed film page — heading and entries, plus the language sections the page groups them by.
/// </summary>
/// <param name="Name">The film's name from the heading, without the year.</param>
/// <param name="Year">The year from the heading; 0 when the heading states none.</param>
/// <param name="Downloads">The download counter of the page, 0 for a film without entries.</param>
/// <param name="Sections">The language codes of the section headings, in page order — the cross-check
/// against the flags.</param>
/// <param name="Entries">The entries in page order.</param>
public sealed record MovieSubtitlesMoviePage(
    string Name, int Year, int Downloads, IReadOnlyList<string> Sections, IReadOnlyList<MovieSubtitlesEntry> Entries);

/// <summary>
/// F-M506: one entry's own page.
/// </summary>
/// <param name="MovieId">The film id from the <c>Back to …</c> link; 0 for the garbage signature.</param>
/// <param name="MovieName">The film's name from the heading.</param>
/// <param name="Year">The year from the heading, 0 when it states none.</param>
/// <param name="Language">The site's code from the description's flag, empty when it carries none.</param>
/// <param name="Rip">The rip the page states, empty when it states none.</param>
/// <param name="Release">The release the page states, empty when it states none.</param>
/// <param name="FileNames">The candidate file names the page lists — a HINT, not the archive's content.</param>
/// <param name="Parts">The <c>parts</c> the page states, 0 when it states none.</param>
/// <param name="Size">The size as written.</param>
/// <param name="Uploaded">The upload timestamp as written.</param>
/// <param name="Downloads">The download counter as written.</param>
/// <param name="DownloadId">The id of the <c>download-&lt;id&gt;.html</c> link.</param>
public sealed record MovieSubtitlesSubtitlePage(
    int MovieId, string MovieName, int Year, string Language, string Rip, string Release,
    IReadOnlyList<string> FileNames, int Parts, string Size, string Uploaded, string Downloads,
    string DownloadId)
{
    /// <summary>Is the page worth an entry in the store? It needs a film id and at least one file name.</summary>
    public bool IsUsable => MovieId > 0 && FileNames.Count > 0;
}

/// <summary>
/// F-M506: what a download produced — and WHICH file, because the case matters.
/// </summary>
/// <param name="Name">The name of the returned member (or of the bare <c>.srt</c>).</param>
/// <param name="Data">The bytes, untouched.</param>
/// <param name="Members">All <c>.srt</c> members of the archive (one entry for a bare file).</param>
/// <param name="PickedByLanguage">True when the choice was made by the language code in the name,
/// false when the first member was taken because none carried it.</param>
public sealed record MovieSubtitlesDownload(
    string Name, byte[] Data, IReadOnlyList<string> Members, bool PickedByLanguage)
{
    /// <summary>More than one file back: a cut film in <c>.partN</c> segments, not a pack.</summary>
    public bool IsMultiPart => Members.Count > 1;
}

/// <summary>
/// F-M506: one file of an upload — used for <c>UFile2</c>/<c>UFile3</c>.
/// </summary>
/// <param name="FileName">The name in the multipart part.</param>
/// <param name="Data">The bytes.</param>
public sealed record MovieSubtitlesUploadFile(string FileName, byte[] Data);

/// <summary>
/// F-M506: one part of the upload form — a text field, or a file field with its bytes.
/// </summary>
/// <param name="Name">The form field's name, verbatim.</param>
/// <param name="Value">The value of a text field; empty for a file part.</param>
/// <param name="FileName">The file name for a file part; null for a text field.</param>
/// <param name="Data">The bytes for a file part; null for a text field.</param>
public sealed record MovieSubtitlesUploadPart(string Name, string Value, string? FileName, byte[]? Data)
{
    /// <summary>A text field.</summary>
    /// <param name="name">The field's name.</param>
    /// <param name="value">The field's value.</param>
    /// <returns>The part.</returns>
    public static MovieSubtitlesUploadPart Field(string name, string value) => new(name, value, null, null);

    /// <summary>A file part.</summary>
    /// <param name="name">The field's name.</param>
    /// <param name="fileName">The file name.</param>
    /// <param name="data">The bytes.</param>
    /// <returns>The part.</returns>
    public static MovieSubtitlesUploadPart File(string name, string fileName, byte[] data) =>
        new(name, string.Empty, fileName, data);
}

/// <summary>
/// F-M506: a finished upload payload — the form's parts in the form's order, plus the two derived names.
/// </summary>
/// <param name="Parts">The parts in the order the page sends them.</param>
/// <param name="LanguageName">The language as the page spells it.</param>
/// <param name="SiteCode">The site's code for it — what the <c>lang</c> field carries.</param>
/// <param name="ReleaseGroup">The release group derived from the release name; empty when it states none.</param>
public sealed record MovieSubtitlesUpload(
    IReadOnlyList<MovieSubtitlesUploadPart> Parts, string LanguageName, string SiteCode, string ReleaseGroup)
{
    /// <summary>The text fields in order — what a test reads to check names and order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Fields =>
        Parts.Where(p => p.FileName is null)
            .Select(p => new KeyValuePair<string, string>(p.Name, p.Value))
            .ToArray();

    /// <summary>The file parts in order (UFile, UFile2, UFile3).</summary>
    public IReadOnlyList<MovieSubtitlesUploadPart> Files =>
        Parts.Where(p => p.FileName is not null).ToArray();
}

/// <summary>
/// F-M506: what an upload did — or what it would have sent, in a dry run.
/// </summary>
/// <param name="Sent">False in a dry run; nothing left the process.</param>
/// <param name="Status">The HTTP status, null in a dry run.</param>
/// <param name="Response">The response text with tags and whitespace collapsed, null in a dry run.</param>
/// <param name="BodyBytes">The size of the multipart body that was (or would be) sent.</param>
/// <param name="Boundary">The multipart boundary used.</param>
public sealed record MovieSubtitlesUploadResult(
    bool Sent, int? Status, string? Response, int BodyBytes, string Boundary);

/// <summary>
/// F-M513: die Nutzlast des Anlege-Formulars (<c>add-movie.php</c>) — <c>name</c>, <c>year</c>,
/// <c>go</c>, in der Reihenfolge der Seite.
/// </summary>
/// <param name="Name">Der Titel, wie er gesendet wird (getrimmt).</param>
/// <param name="Year">Das Jahr, wie es gesendet wird.</param>
/// <param name="Parts">Die Feldteile in der Reihenfolge des Formulars.</param>
public sealed record MovieSubtitlesAddMovie(
    string Name, int Year, IReadOnlyList<MovieSubtitlesUploadPart> Parts)
{
    /// <summary>Die Textfelder in Reihenfolge — was eine Pruefung liest, um Namen und Ordnung zu belegen.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Fields =>
        Parts.Where(p => p.FileName is null)
            .Select(p => new KeyValuePair<string, string>(p.Name, p.Value))
            .ToArray();
}

/// <summary>
/// F-M513/F-M515: die Ausgaenge eines Anlege-Versuchs.
/// </summary>
public static class MovieSubtitlesAddOutcome
{
    /// <summary>Trockenlauf: der Rumpf wurde gebaut, nichts gesendet.</summary>
    public const string DryRun = "dry-run";

    /// <summary>Die Seite sagte „Movie has been added“, und die Nachsuche nannte die Nummer des neuen Films.</summary>
    public const string Created = "created";

    /// <summary>Die Seite sagte „Movie has been added“, die Nachsuche nannte keine Nummer: „angelegt, Id nicht lesbar“.</summary>
    public const string CreatedIdUnknown = "created-id-unknown";

    /// <summary>
    /// F-M515: die Seite sagte „This Movie already exist“ — sie hat NICHTS angelegt, und die Nachsuche
    /// nannte trotzdem eine Nummer (der Film ist also auffindbar). Kein zweiter Anlage-Versuch.
    /// </summary>
    public const string Exists = "exists";

    /// <summary>
    /// F-M515: die Seite sagte „This Movie already exist“, die Nachsuche nannte aber keine Nummer. Live
    /// gemessen: genau das passiert bei einem eben angelegten Film, den die Seite (noch) nirgends führt.
    /// </summary>
    public const string ExistsUnknownId = "exists-id-not-readable";

    /// <summary>
    /// F-M515: HTTP 2xx, aber KEIN belegter Marker — die Seite hat nicht angelegt (live gemessen: das
    /// unveraenderte Formular, 8399 Bytes, ohne Meldung). <c>Ok = false</c>, der Textauszug ist der Grund.
    /// </summary>
    public const string Rejected = "rejected";

    /// <summary>Kein 2xx — der Status ist der Grund, der Text der Seite geht mit.</summary>
    public const string HttpError = "http-error";
}

/// <summary>
/// F-M513: was ein Anlege-Versuch ergeben hat — einschliesslich der Frage, ob die Nummer lesbar war.
/// </summary>
/// <param name="Ok">True nur bei HTTP 2xx. Ein 2xx sagt NICHT, dass die Id bekannt ist — das sagt
/// <paramref name="Outcome"/>.</param>
/// <param name="Status">Der HTTP-Status; null im Trockenlauf.</param>
/// <param name="Reason">Der Grund einer Ablehnung (<c>HTTP 503</c>), sonst null.</param>
/// <param name="Response">Der Text der Antwort, Tags und Leerraum zusammengezogen; null im Trockenlauf.</param>
/// <param name="BodyBytes">Die Groesse des Rumpfs, der (oder im Trockenlauf: der haette) gesendet wurde.</param>
/// <param name="Boundary">Das Multipart-Boundary.</param>
/// <param name="Outcome">Einer der Werte aus <see cref="MovieSubtitlesAddOutcome"/>.</param>
/// <param name="MovieId">Die Nummer des neuen Films — nur gesetzt, wenn die Nachsuche sie nannte.</param>
public sealed record MovieSubtitlesAddMovieResult(
    bool Ok, int? Status, string? Reason, string? Response, int BodyBytes, string Boundary,
    string Outcome, int? MovieId);

/// <summary>
/// F-M513: die drei Ausgaenge der Frage „darf ich den Film anlegen?" — plus den Fall, dass die Frage
/// nicht beantwortbar war. Der Aufrufer entscheidet daran, ob gesendet wird.
/// </summary>
public static class MovieSubtitlesAddStatus
{
    /// <summary>Der Titel steht mit DIESEM Jahr da: diese Nummer nehmen, nichts anlegen.</summary>
    public const string Take = "take";

    /// <summary>Der Titel steht da, aber nicht mit diesem Jahr: nichts anlegen, nicht hochladen.</summary>
    public const string Refuse = "refuse";

    /// <summary>Kein Titeltreffer, und die Suche hat lesbar geantwortet: anlegen.</summary>
    public const string Create = "create";

    /// <summary>Keine lesbare Antwort — „gibt es nicht" ist damit nicht belegt: nichts anlegen.</summary>
    public const string Unclear = "unclear";
}

/// <summary>
/// F-M513: die Entscheidung vor dem Anlegen — sie gehoert ins Ergebnis, nicht nur ins Log.
/// </summary>
/// <param name="Status">Einer der Werte aus <see cref="MovieSubtitlesAddStatus"/>.</param>
/// <param name="MovieId">Die zu nehmende Nummer — nur bei <see cref="MovieSubtitlesAddStatus.Take"/>.</param>
/// <param name="FoundYear">Das Jahr, unter dem der Titel schon steht (0 = die Seite nennt keines);
/// nur bei <see cref="MovieSubtitlesAddStatus.Refuse"/> von Bedeutung.</param>
/// <param name="Hits">Wie viele Zeilen den Titel tragen — 2 und mehr heisst „nicht eindeutig", und
/// auch dann wird nichts angelegt.</param>
/// <param name="Answers">Wie viele Suchbegriffe lesbar geantwortet haben.</param>
public sealed record MovieSubtitlesAddCheck(
    string Status, int? MovieId, int FoundYear, int Hits, int Answers);
