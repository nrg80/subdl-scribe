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

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// F-M480 (operator order 15.11.2026): the tvsubtitles.net client — the second source, and the only
/// one that is reached by reading its web pages rather than by an API. The site has NO API: <c>/api*</c>,
/// <c>/xmlrpc.php</c> and <c>api.tvsubtitles.net</c> all answer 404/DNS (measured 15.11.2026), only
/// <c>/rss.xml</c> exists and carries no per-episode data.
/// <para>
/// The path is fixed and it is the site's own navigation:
/// <c>/add.html</c> (show list) → <c>/tvshow-&lt;id&gt;.html</c> (seasons) →
/// <c>/tvshow-&lt;id&gt;-&lt;season&gt;.html</c> (episode pages) → <c>/episode-&lt;id&gt;.html</c> (entries) →
/// <c>/subtitle-&lt;id&gt;.html</c> → <c>/download-&lt;id&gt;.html</c> (the file name sits in JavaScript
/// fragments) → the ZIP under <c>files/</c>.
/// </para>
/// <para>
/// WHAT THIS CLASS IS AND IS NOT: it is the source's method surface — resolve, list, fetch, build an
/// upload, send an upload. It is NOT wired into the pipelines: the switches for this source report
/// "kein Anbieter verbaut" (F-M478) until a caller exists. Keeping the client separate from the
/// wiring is the point: the protocol can be tested against captured pages without a download run.
/// </para>
/// <para>
/// Every parser is static and takes the HTML as a string, so the suites can drive them with the
/// captured pages under <c>scripts/tests/tvsubtitles/fixtures</c> instead of the live site.
/// </para>
/// </summary>
public sealed class TvSubtitlesClient
{
    /// <summary>The site root. https only — the http endpoint redirects.</summary>
    public const string BaseUrl = "https://www.tvsubtitles.net";

    /// <summary>Maximum season number the upload form accepts (its own select offers 1–22).</summary>
    public const int MaxSeason = 22;

    /// <summary>Maximum episode number the upload form accepts (its own select offers 0–100).</summary>
    public const int MaxEpisode = 100;

    /// <summary>
    /// F-M480: the language codes are the SITE's abbreviations, not ISO 639-1 — Portuguese is <c>pt</c>,
    /// Brazilian Portuguese is <c>br</c>, Chinese is <c>cn</c>, Japanese is <c>jp</c>, Greek is <c>gr</c>,
    /// Czech is <c>cz</c>. Sending an ISO code the select does not offer makes the form fall back to its
    /// first entry (the upload lands as English), so the codes are kept verbatim from the page.
    /// The value is (name as written on the page, three-letter code used in file names).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Name, string Iso3)> Languages =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["en"] = ("english", "eng"),
            ["es"] = ("spanish", "spa"),
            ["fr"] = ("french", "fre"),
            ["de"] = ("german", "ger"),
            ["br"] = ("portuguese(br)", "pob"),
            ["ru"] = ("russian", "rus"),
            ["ua"] = ("ukrainian", "ukr"),
            ["it"] = ("italian", "ita"),
            ["gr"] = ("greek", "gre"),
            ["ar"] = ("arabic", "ara"),
            ["hu"] = ("hungarian", "hun"),
            ["pl"] = ("polish", "pol"),
            ["tr"] = ("turkish", "tur"),
            ["nl"] = ("dutch", "dut"),
            ["pt"] = ("portuguese", "por"),
            ["sv"] = ("swedish", "swe"),
            ["da"] = ("danish", "dan"),
            ["fi"] = ("finnish", "fin"),
            ["ko"] = ("korean", "kor"),
            ["cn"] = ("chinese", "chi"),
            ["jp"] = ("japanese", "jpn"),
            ["bg"] = ("bulgarian", "bul"),
            ["cz"] = ("czech", "cze"),
            ["ro"] = ("romanian", "rum"),
        };

    /// <summary>
    /// F-M502 (Operator-Anweisung 15.11.2026: „Aber halt tvst spezifisch. Das andere mapping ist subdl
    /// spezifisch."): die Bruecke von ISO- auf die Kuerzel der Seite, und zwar AUSSCHLIESSLICH hier.
    /// <para>
    /// Gemessen am 15.11.2026: die Tabelle oben ist vollstaendig — die Seite kennt genau diese 24
    /// Kuerzel —, aber vier davon sind NICHT ISO: <c>gr</c> statt <c>el</c>/<c>ell</c>/<c>gre</c>
    /// (Griechisch), <c>cz</c> statt <c>cs</c>/<c>ces</c>/<c>cze</c> (Tschechisch), <c>cn</c> statt
    /// <c>zh</c>/<c>zho</c>/<c>chi</c> (Chinesisch), <c>jp</c> statt <c>ja</c>/<c>jpn</c> (Japanisch).
    /// Jellyfin liefert Sprachen als DREI-Buchstaben-Codes (<c>gre</c>, <c>cze</c>, <c>chi</c>,
    /// <c>jpn</c>), die Konfiguration und die Zielsprachen teils als ZWEI-Buchstaben-Codes
    /// (<c>el</c>, <c>cs</c>, <c>zh</c>, <c>ja</c>) — beide Formen landeten vorher in der Pruefung von
    /// <see cref="BuildUpload"/> und der Upload wurde mit „language must be one of the site's codes"
    /// abgelehnt. Deshalb die vier Abweichungen in beiden Formen, dazu die gaengigen
    /// ISO-639-2-Formen (/B und /T) der uebrigen Seiten-Sprachen.
    /// </para>
    /// <para>
    /// Was hier ABSICHTLICH NICHT passiert: keine gemeinsame Tabelle mit SubDL (Operator-Anweisung
    /// oben — SubDL bildet in <see cref="SubdlApiClient"/> ab), kein Raten ausserhalb der Liste
    /// (unbekannt heisst null), und die Seiten-Kuerzel selbst bleiben unveraendert gueltig.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> LanguageAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Die vier Abweichungen der Seite, in jeder gaengigen ISO-Schreibweise.
            ["el"] = "gr",
            ["ell"] = "gr",
            ["gre"] = "gr",
            ["cs"] = "cz",
            ["ces"] = "cz",
            ["cze"] = "cz",
            ["zh"] = "cn",
            ["zho"] = "cn",
            ["chi"] = "cn",
            ["ja"] = "jp",
            ["jpn"] = "jp",

            // Die uebrigen Seiten-Sprachen in ISO-639-2/B und /T.
            ["uk"] = "ua",
            ["eng"] = "en",
            ["spa"] = "es",
            ["fra"] = "fr",
            ["fre"] = "fr",
            ["deu"] = "de",
            ["ger"] = "de",
            ["pob"] = "br",
            ["rus"] = "ru",
            ["ukr"] = "ua",
            ["ita"] = "it",
            ["ara"] = "ar",
            ["hun"] = "hu",
            ["pol"] = "pl",
            ["tur"] = "tr",
            ["nld"] = "nl",
            ["dut"] = "nl",
            ["por"] = "pt",
            ["swe"] = "sv",
            ["dan"] = "da",
            ["fin"] = "fi",
            ["kor"] = "ko",
            ["bul"] = "bg",
            ["ron"] = "ro",
            ["rum"] = "ro",
        };

    /// <summary>
    /// F-M502: der Sprachcode, den die Seite versteht — aus einem ISO-Code, einem Seiten-Kuerzel oder
    /// einer Angabe mit Region/Skript (<c>pt-BR</c>, <c>zh-Hans</c>).
    /// <para>
    /// Gross-/Kleinschreibung zaehlt nicht. Die Region entscheidet nur dort, wo die Seite die Sprache
    /// danach trennt: <c>pt-BR</c> ist brasilianisches Portugiesisch (<c>br</c>), waehrend <c>pt-PT</c>
    /// und <c>pt</c> europaesisches Portugiesisch (<c>pt</c>) bleiben. Bei allen anderen Sprachen
    /// faellt die Region weg (<c>zh-Hans</c>/<c>zh-CN</c> → <c>cn</c>).
    /// </para>
    /// </summary>
    /// <param name="code">Der ankommende Code, in beliebiger Schreibweise.</param>
    /// <returns>Ein Kuerzel aus <see cref="Languages"/>, oder null — dann ist die Sprache der Seite
    /// unbekannt und wird nicht geraten.</returns>
    public static string? SiteLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        // Ein Schraegstrich kommt in Tags vor ("pt_BR"); die Seite kennt nur den Bindestrich.
        var text = code.Trim().ToLowerInvariant().Replace('_', '-');

        // Die eine Stelle, an der die REGION die Sprache ist: brasilianisches Portugiesisch.
        // Muss vor der Grundcode-Reduktion stehen, sonst faellt "pt-br" auf europaesisches "pt".
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

        // Das Kuerzel der Seite selbst (en, de, br, gr, cz, cn, jp, ua …) ist bereits das Ziel.
        return Languages.ContainsKey(baseCode) ? baseCode : null;
    }

    /// <summary>
    /// F-M480: the rip select of the upload form, plus the empty value. Anything else is refused
    /// before the request is built — the form ignores an unknown value and stores an empty rip.
    /// </summary>
    public static readonly IReadOnlyList<string> Rips =
        new[] { "WEB", "HDTV", "HEVC", "DVDRip", "DVDSCR", "BDRip", "BluRay", string.Empty };

    private static readonly Regex SelectBlock = new(
        "<select[^>]*name=\"tvshow\".*?</select>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex Option = new(
        "<option value=\"(\\d+)\"[^>]*>(.*?)</option>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex SeasonLink = new(
        "tvshow-(\\d+)-(\\d+)\\.html", RegexOptions.CultureInvariant);

    private static readonly Regex EpisodeLink = new(
        "href=\"(?:/)?episode-(\\d+)(?:-[a-z]{2})?\\.html\"", RegexOptions.CultureInvariant);

    private static readonly Regex EpisodeHeading = new(
        "<h2>[^<]*?(\\d+)x(\\d+)</h2>", RegexOptions.CultureInvariant);

    private static readonly Regex EpisodeHeadingLoose = new(
        "(\\d+)x(\\d+)", RegexOptions.CultureInvariant);

    private static readonly Regex SubtitleAnchor = new(
        "<a href=\"/subtitle-(\\d+)\\.html\">", RegexOptions.CultureInvariant);

    private static readonly Regex Flag = new(
        "images/flags/([a-z]{1,6})\\.gif", RegexOptions.CultureInvariant);

    /// <summary>
    /// F-M498: die Seite kennt genau sieben Rip-Werte (WEB, HDTV, HEVC, DVDRip, DVDSCR, BDRip,
    /// BluRay). Release-Namen schreiben das aber anders — "WEB-DL", "WEBRip", "BRRip", "x265",
    /// "Blu-Ray". Gemessen 15.11.2026 im scharfen Lauf: jede Folge wurde mit
    /// "refused: rip must be one of …" abgelehnt, weil "WEB-DL" durchgereicht wurde und die
    /// Vorpruefung den Wert zu Recht verwarf. Die Zuordnung nimmt den ERSTEN Treffer, Quelle vor
    /// Codec: "WEB-DL x265" ist WEB, nicht HEVC. Findet sich nichts, bleibt das Feld leer — das
    /// ist ein erlaubter Wert, die Seite speichert dann keinen Rip.
    /// </summary>
    /// <param name="quality">Qualitaets-/Release-Angabe, wie sie im Dateinamen steht.</param>
    /// <returns>Ein Wert aus <see cref="Rips"/>.</returns>
    public static string RipFromQuality(string? quality)
    {
        var q = (quality ?? string.Empty).ToUpperInvariant();

        // Reihenfolge ist Absicht: erst die Quellen, dann der Codec als Rueckfall.
        if (q.Contains("DVDSCR", StringComparison.Ordinal))
        {
            return "DVDSCR";
        }

        if (q.Contains("DVDRIP", StringComparison.Ordinal) || q.Contains("DVD-R", StringComparison.Ordinal))
        {
            return "DVDRip";
        }

        if (q.Contains("BDRIP", StringComparison.Ordinal) || q.Contains("BD-RIP", StringComparison.Ordinal)
            || q.Contains("BRRIP", StringComparison.Ordinal) || q.Contains("BR-RIP", StringComparison.Ordinal))
        {
            return "BDRip";
        }

        if (q.Contains("BLURAY", StringComparison.Ordinal) || q.Contains("BLU-RAY", StringComparison.Ordinal)
            || q.Contains("BDREMUX", StringComparison.Ordinal))
        {
            return "BluRay";
        }

        if (q.Contains("HDTV", StringComparison.Ordinal))
        {
            return "HDTV";
        }

        if (q.Contains("WEB", StringComparison.Ordinal))
        {
            // WEB-DL, WEBRip, WEB = dieselbe Schublade der Seite.
            return "WEB";
        }

        if (q.Contains("HEVC", StringComparison.Ordinal) || q.Contains("X265", StringComparison.Ordinal)
            || q.Contains("H.265", StringComparison.Ordinal) || q.Contains("H265", StringComparison.Ordinal))
        {
            return "HEVC";
        }

        return string.Empty;
    }

    private static readonly Regex RipCell = new(
        "title=\"rip\">\\s*<img[^>]*>\\s*([^<]+)", RegexOptions.CultureInvariant);

    private static readonly Regex ReleaseCell = new(
        "title=\"release\">\\s*<img[^>]*>\\s*([^<]+)", RegexOptions.CultureInvariant);

    private static readonly Regex AgeCell = new(
        "<small[^>]*>([\\d.]{8,10} [\\d:]+)</small>", RegexOptions.CultureInvariant);

    // The download page holds the file name split over numbered JS variables: var s1='files/The Middle_2x24';
    // var s2='_HDTV.FEVER.en.zip'; Concatenated in order they are the path under the site root.
    private static readonly Regex JsNameFragment = new(
        "var s\\d+= *'([^']*)';", RegexOptions.CultureInvariant);

    private static readonly Regex TagStripper = new("<[^>]+>", RegexOptions.CultureInvariant);

    // F-M501: die beiden Angleichungen der Namenssuche — "und" als eigenes Wort, und jede Folge von
    // Leerraum als ein Leerzeichen.
    private static readonly Regex WordUnd = new(@"\bund\b", RegexOptions.CultureInvariant);

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.CultureInvariant);

    private readonly HttpClient _http;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvSubtitlesClient"/> class.
    /// </summary>
    /// <param name="http">The HttpClient to use. <see cref="SubdlApiClient.NewHttpClient"/> gives one
    /// that identifies itself as this plugin; a caller may pass its own for a test.</param>
    public TvSubtitlesClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (X11; Linux aarch64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
        }
    }

    /// <summary>
    /// F-M480: the show list of the upload form — id → name. This is the only place the site names its
    /// shows, so it answers both "which id is this series" and "does the series exist at all".
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Show id to name, in page order.</returns>
    public async Task<IReadOnlyDictionary<int, string>> ShowsAsync(CancellationToken cancellationToken = default)
    {
        var html = await GetStringAsync(BaseUrl + "/add.html", null, cancellationToken).ConfigureAwait(false);
        return ParseShows(html);
    }

    /// <summary>
    /// F-M480: parses the tvshow select out of the upload page.
    /// </summary>
    /// <param name="html">The page source.</param>
    /// <returns>Show id to name. A page without the select yields an empty map — the caller decides
    /// whether that is an error, so the parser never throws on a truncated page.</returns>
    public static IReadOnlyDictionary<int, string> ParseShows(string html)
    {
        var result = new Dictionary<int, string>();
        if (string.IsNullOrEmpty(html))
        {
            return result;
        }

        var block = SelectBlock.Match(html);
        if (!block.Success)
        {
            return result;
        }

        foreach (Match m in Option.Matches(block.Value))
        {
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            var name = TagStripper.Replace(m.Groups[2].Value, string.Empty).Trim();
            result[id] = System.Net.WebUtility.HtmlDecode(name);
        }

        return result;
    }

    /// <summary>
    /// F-M501 (Operator-Entscheidung 15.11.2026, nachgeschärft: „Doch Ähnlichkeitssuche. Aber
    /// groß/Kleinschreibung. And = und. Leerzeichen mehr oder weniger. Ähnlichkeit eng halten“): der
    /// Name, auf den zwei Seriennamen verglichen werden — und die EINZIGEN drei Angleichungen, die
    /// erlaubt sind.
    /// <list type="bullet">
    /// <item><description>Groß-/Kleinschreibung zählt nicht.</description></item>
    /// <item><description><c>&amp;</c>, <c>and</c> und <c>und</c> gelten als dasselbe Wort — als Wort,
    /// nicht als Buchstabenfolge: „Rundschau“ wird nicht zu „Randschau“.</description></item>
    /// <item><description>Mehrere oder fehlende Leerzeichen zählen nicht; jede Folge von Leerraum wird
    /// zu einem einzelnen Leerzeichen.</description></item>
    /// </list>
    /// <para>
    /// Was hier ABSICHTLICH NICHT passiert: führende Artikel werden nicht entfernt („The Middle“ bleibt
    /// „The Middle“), es gibt keinen Teilstring-Treffer („The Middle“ findet „The Middle Man“ nicht) und
    /// kein Ähnlichkeitsmaß (kein Levenshtein, keine Token-Quote, keine Jahres-Toleranz). Genau diese
    /// Grenze hält Doppeleinträge draußen: eine Schreibweise, die nur „ähnlich“ ist, ist eine andere
    /// Serie.
    /// </para>
    /// </summary>
    /// <param name="name">Der Name, wie er geschrieben steht.</param>
    /// <returns>Der Vergleichsschlüssel; leer, wenn der Name leer war.</returns>
    public static string NormalizeShowName(string? name)
    {
        var text = (name ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        // "&" ist ein Wort fuer sich: aus "Rick&Morty" wird "rick and morty".
        text = text.Replace("&", " and ", StringComparison.Ordinal);

        // "und" NUR als eigenes Wort — sonst wuerde aus "Rundschau" ein "Randschau".
        text = WordUnd.Replace(text, "and");

        // Leerraum: eine Folge wird zu einem Leerzeichen, aussen bleibt nichts stehen.
        return WhitespaceRun.Replace(text, " ").Trim();
    }

    /// <summary>
    /// F-M501: der Name einer Serie, so eng verglichen, wie es die Seite erlaubt — die drei
    /// Angleichungen aus <see cref="NormalizeShowName"/> und sonst Gleichheit.
    /// <para>
    /// Die frühere „enthält“-Stufe ist WEG (Operator-Entscheidung 15.11.2026: „Ähnlichkeit eng halten“,
    /// „Tmdb titel führt auch für Doppeleinträge“). Sie war der Grund, warum ein TMDb-Titel, den die
    /// Seite anders schreibt, still auf einen NAMENSÄHNLICHEN Doppeleintrag zeigte — und damit auf die
    /// falsche Serie, deren Nummer dann im Store gestanden hätte.
    /// </para>
    /// </summary>
    /// <param name="shows">The show map.</param>
    /// <param name="name">The name to look for — in practice the TMDb title.</param>
    /// <returns>The id, or null when no show carries exactly this name (after the three adjustments).</returns>
    public static int? ResolveShow(IReadOnlyDictionary<int, string> shows, string name)
    {
        ArgumentNullException.ThrowIfNull(shows);
        var wanted = NormalizeShowName(name);
        if (wanted.Length == 0)
        {
            return null;
        }

        foreach (var pair in shows)
        {
            if (pair.Key > 0 && string.Equals(NormalizeShowName(pair.Value), wanted, StringComparison.Ordinal))
            {
                return pair.Key;
            }
        }

        return null;
    }

    /// <summary>
    /// F-M480: the seasons that actually have a page for this show.
    /// </summary>
    /// <param name="showId">The show id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ascending season numbers.</returns>
    public async Task<IReadOnlyList<int>> SeasonsAsync(int showId, CancellationToken cancellationToken = default)
    {
        var html = await GetStringAsync(
            string.Format(CultureInfo.InvariantCulture, "{0}/tvshow-{1}.html", BaseUrl, showId), null, cancellationToken)
            .ConfigureAwait(false);
        return ParseSeasons(html, showId);
    }

    /// <summary>
    /// F-M480: parses the season numbers out of a show page.
    /// </summary>
    /// <param name="html">The page source.</param>
    /// <param name="showId">The show id the page belongs to.</param>
    /// <returns>Ascending season numbers, without duplicates.</returns>
    public static IReadOnlyList<int> ParseSeasons(string html, int showId)
    {
        var seasons = new SortedSet<int>();
        if (string.IsNullOrEmpty(html))
        {
            return seasons.ToList();
        }

        foreach (Match m in SeasonLink.Matches(html))
        {
            if (int.TryParse(m.Groups[1].Value, out var id) && id == showId
                && int.TryParse(m.Groups[2].Value, out var season) && season >= 1 && season <= MaxSeason)
            {
                seasons.Add(season);
            }
        }

        return seasons.ToList();
    }

    /// <summary>
    /// F-M480: the page ids of one season's episodes, in page order. They are NOT episode numbers —
    /// the label comes from the page itself (<see cref="EpisodeLabelAsync"/>), because the season list
    /// runs newest first.
    /// </summary>
    /// <param name="showId">The show id.</param>
    /// <param name="season">The season number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Episode page ids, deduplicated.</returns>
    public async Task<IReadOnlyList<int>> EpisodePagesAsync(
        int showId, int season, CancellationToken cancellationToken = default)
    {
        var url = string.Format(
            CultureInfo.InvariantCulture, "{0}/tvshow-{1}-{2}.html", BaseUrl, showId, season);
        var html = await GetStringAsync(url, null, cancellationToken).ConfigureAwait(false);
        return ParseEpisodePages(html);
    }

    /// <summary>
    /// F-M480: parses the episode page ids out of a season page.
    /// </summary>
    /// <param name="html">The page source.</param>
    /// <returns>Page ids in page order, deduplicated.</returns>
    public static IReadOnlyList<int> ParseEpisodePages(string html)
    {
        var seen = new HashSet<int>();
        var ordered = new List<int>();
        if (string.IsNullOrEmpty(html))
        {
            return ordered;
        }

        foreach (Match m in EpisodeLink.Matches(html))
        {
            if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                && seen.Add(id))
            {
                ordered.Add(id);
            }
        }

        return ordered;
    }

    /// <summary>
    /// F-M480: (season, episode) as the episode page states it in its heading.
    /// </summary>
    /// <param name="pageId">The episode page id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Season and episode, each 0 when the page does not say.</returns>
    public async Task<(int Season, int Episode)> EpisodeLabelAsync(
        int pageId, CancellationToken cancellationToken = default)
    {
        var html = await GetStringAsync(
            string.Format(CultureInfo.InvariantCulture, "{0}/episode-{1}.html", BaseUrl, pageId), null, cancellationToken)
            .ConfigureAwait(false);
        return ParseEpisodeLabel(html);
    }

    /// <summary>
    /// F-M480: parses the "2x24" out of an episode page heading, falling back to the first such pair
    /// anywhere on the page (the heading markup has changed once already).
    /// </summary>
    /// <param name="html">The page source.</param>
    /// <returns>Season and episode, each 0 when the page does not say.</returns>
    public static (int Season, int Episode) ParseEpisodeLabel(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return (0, 0);
        }

        var m = EpisodeHeading.Match(html);
        if (!m.Success)
        {
            m = EpisodeHeadingLoose.Match(html);
        }

        if (!m.Success)
        {
            return (0, 0);
        }

        if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var season)
            && int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var episode))
        {
            return (season, episode);
        }

        return (0, 0);
    }

    /// <summary>
    /// F-M480: the subtitles offered for one episode page.
    /// </summary>
    /// <param name="pageId">The episode page id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entries in page order.</returns>
    public async Task<IReadOnlyList<TvSubtitlesEntry>> EntriesAsync(
        int pageId, CancellationToken cancellationToken = default)
    {
        var html = await GetStringAsync(
            string.Format(CultureInfo.InvariantCulture, "{0}/episode-{1}.html", BaseUrl, pageId), null, cancellationToken)
            .ConfigureAwait(false);
        return ParseEntries(html);
    }

    /// <summary>
    /// F-M488 (operator order 15.11.2026): reads back which subtitle id our own upload got. The site
    /// does not answer the upload with an id, so the only honest way to know it is to look at the
    /// episode page afterwards and pick the entry that matches language and release.
    /// <para>
    /// Returns null when nothing matches — an id that was not seen is never invented, because the store
    /// would then vouch for a row that does not exist.
    /// </para>
    /// </summary>
    /// <param name="showName">Series name as the site spells it.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="language">Two-letter language code.</param>
    /// <param name="release">The release name we uploaded under.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subtitle id, or null.</returns>
    public async Task<string?> FindUploadedIdAsync(
        string showName, int season, int episode, string language, string release,
        CancellationToken cancellationToken = default)
    {
        var showId = ResolveShow(await ShowsAsync(cancellationToken).ConfigureAwait(false), showName);
        if (showId is null)
        {
            return null;
        }

        var page = await FindEpisodePageAsync(showId.Value, season, episode, cancellationToken).ConfigureAwait(false);
        if (page is null)
        {
            return null;
        }

        var entries = await EntriesAsync(page.Value, cancellationToken).ConfigureAwait(false);

        // F-M502: beide Seiten durch die Bruecke — der gesuchte Code kann ISO sein, der Eintrag traegt
        // das Kuerzel der Flagge. Nur wenn eine Seite unbekannt bleibt, gilt der rohe Wert.
        var wanted = SiteLanguage(language) ?? (language ?? string.Empty).Trim().ToLowerInvariant();
        foreach (var entry in entries)
        {
            var entryLanguage = SiteLanguage(entry.Language) ?? entry.Language.Trim().ToLowerInvariant();
            var sameLanguage = string.Equals(entryLanguage, wanted, StringComparison.Ordinal);
            var sameRelease = !string.IsNullOrWhiteSpace(release)
                && (string.Equals(entry.Release.Trim(), release.Trim(), StringComparison.OrdinalIgnoreCase)
                    || entry.Release.Contains(release.Trim(), StringComparison.OrdinalIgnoreCase));
            if (sameLanguage && sameRelease)
            {
                return entry.SubtitleId;
            }
        }

        return null;
    }

    /// <summary>
    /// F-M488: the episode page id for one episode — the season's pages are walked, because the page ids
    /// are NOT episode numbers (F-M480).
    /// </summary>
    /// <param name="showId">Show id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page id, or null.</returns>
    public async Task<int?> FindEpisodePageAsync(
        int showId, int season, int episode, CancellationToken cancellationToken = default)
    {
        var pages = await EpisodePagesAsync(showId, season, cancellationToken).ConfigureAwait(false);
        foreach (var page in pages)
        {
            var label = await EpisodeLabelAsync(page, cancellationToken).ConfigureAwait(false);
            if (label.Season == season && label.Episode == episode)
            {
                return page;
            }
        }

        return null;
    }

    /// <summary>
    /// F-M480: parses the entries of an episode page.
    /// <para>
    /// The language is NOT in the text — the only trustworthy carrier is the flag image, and it must be
    /// read per BLOCK (from one subtitle anchor up to the next), because a bare search finds the flag of
    /// the following entry and mislabels the current one. Entries without a flag are skipped: an entry
    /// whose language is unknown cannot be matched against a target language.
    /// </para>
    /// </summary>
    /// <param name="html">The page source.</param>
    /// <returns>The entries in page order.</returns>
    public static IReadOnlyList<TvSubtitlesEntry> ParseEntries(string html)
    {
        var entries = new List<TvSubtitlesEntry>();
        if (string.IsNullOrEmpty(html))
        {
            return entries;
        }

        var anchors = SubtitleAnchor.Matches(html);
        for (var i = 0; i < anchors.Count; i++)
        {
            var start = anchors[i].Index;
            var end = i + 1 < anchors.Count ? anchors[i + 1].Index : html.Length;
            var block = html.Substring(start, end - start);

            var flag = Flag.Match(block);
            if (!flag.Success)
            {
                continue;
            }

            var rip = RipCell.Match(block);
            var release = ReleaseCell.Match(block);
            var age = AgeCell.Match(block);
            entries.Add(new TvSubtitlesEntry(
                anchors[i].Groups[1].Value,
                flag.Groups[1].Value.ToLowerInvariant(),
                rip.Success ? rip.Groups[1].Value.Trim() : string.Empty,
                release.Success ? release.Groups[1].Value.Trim() : string.Empty,
                age.Success ? age.Groups[1].Value.Trim() : string.Empty));
        }

        return entries;
    }

    /// <summary>
    /// F-M480: the file name behind the download link. It is not in the link — the download page builds
    /// it in JavaScript from numbered fragments, which are concatenated in order.
    /// </summary>
    /// <param name="html">The source of <c>/download-&lt;id&gt;.html</c>.</param>
    /// <returns>The path under the site root, e.g. <c>files/The Middle_2x24_HDTV.FEVER.en.zip</c>,
    /// or null when the page carries no fragment.</returns>
    public static string? ParseDownloadFileName(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        var fragments = JsNameFragment.Matches(html).Select(m => m.Groups[1].Value).ToArray();
        if (fragments.Length == 0)
        {
            return null;
        }

        var name = string.Concat(fragments);
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// F-M480: is this payload a ZIP already? The site serves the archive straight from the download
    /// URL in one of its two shapes; the local header ("PK\u0003\u0004") is what tells them apart
    /// without trusting a content type the site does not always set.
    /// </summary>
    /// <param name="payload">The bytes that came back.</param>
    /// <returns>True for a ZIP archive.</returns>
    public static bool IsZip(byte[] payload) =>
        payload is { Length: > 4 } && payload[0] == 0x50 && payload[1] == 0x4B
        && payload[2] == 0x03 && payload[3] == 0x04;

    /// <summary>
    /// F-M480: fetches one subtitle and returns the first <c>.srt</c> out of the ZIP.
    /// <para>
    /// The bytes are handed over UNTOUCHED: the site serves UTF-8 next to cp1250, cp1251, cp1253,
    /// cp1254 and cp1256 depending on the language, and guessing here would repeat the codepage defect
    /// that made Russian, Greek and Arabic unreadable in the library on 15.11.2026. Decoding is the
    /// pipeline's business, not the transport's.
    /// </para>
    /// </summary>
    /// <param name="subtitleId">The entry's subtitle id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The member name inside the ZIP and the raw bytes.</returns>
    public async Task<(string Name, byte[] Data)> DownloadAsync(
        string subtitleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subtitleId))
        {
            throw new ArgumentException("subtitle id missing", nameof(subtitleId));
        }

        var subtitlePage = string.Format(
            CultureInfo.InvariantCulture, "{0}/subtitle-{1}.html", BaseUrl, subtitleId);
        var downloadPage = string.Format(
            CultureInfo.InvariantCulture, "{0}/download-{1}.html", BaseUrl, subtitleId);

        // Two GETs, in the site's own order: the download page expects the subtitle page as referer.
        await GetStringAsync(subtitlePage, null, cancellationToken).ConfigureAwait(false);
        var answer = await GetBytesAsync(downloadPage, subtitlePage, cancellationToken).ConfigureAwait(false);

        // F-M480, measured 15.11.2026: the download URL answers in TWO shapes depending on how the
        // request arrives — sometimes the "wait a second" page whose file name sits in JavaScript,
        // sometimes the ZIP itself. Both are handled from the bytes that actually came back instead of
        // assuming one shape and failing on the other.
        byte[] zip;
        if (IsZip(answer))
        {
            zip = answer;
        }
        else
        {
            var name = ParseDownloadFileName(Encoding.UTF8.GetString(answer))
                ?? throw new InvalidOperationException(
                    "the download page carried neither a ZIP nor a file name in its script (tvsubtitles.net)");
            var url = BaseUrl + "/" + Uri.EscapeUriString(name).Replace("?", "%3F", StringComparison.Ordinal);
            zip = await GetBytesAsync(url, downloadPage, cancellationToken).ConfigureAwait(false);
        }

        using var stream = new MemoryStream(zip, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var member = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "no .srt inside the ZIP (tvsubtitles.net): " + string.Join(", ", archive.Entries.Take(3).Select(e => e.Name)));

        using var entryStream = await member.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await entryStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return (member.Name, buffer.ToArray());
    }

    /// <summary>
    /// F-M481 (operator order 15.11.2026): the tag this client writes into the comment — the plugin name
    /// and the version that is actually running, read from the assembly so a build cannot carry a stale
    /// number. The uploader uses it to tell which client put a subtitle there.
    /// </summary>
    public static string ClientTag { get; } =
        "Sub Scribe " + (typeof(TvSubtitlesClient).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");

    /// <summary>
    /// F-M481: the comment field of an upload — the TMDb and IMDb ids plus this client's tag, because the
    /// site has no field for either and the operator wants both carried (order 15.11.2026: "Comment soll
    /// auch den Client neben den ids enthalten"). The author field stays empty.
    /// </summary>
    /// <param name="tmdbId">The TMDb id, when known.</param>
    /// <param name="imdbId">The IMDb id, when known.</param>
    /// <param name="extra">Optional text, placed between the ids and the client tag.</param>
    /// <returns>The comment line, e.g. <c>TMDB 1422 / IMDb tt1442464 / Sub Scribe 12.1.12.380</c>.</returns>
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

        // Always last, so the line ends with who wrote it — and never absent, which is also why the
        // "no id at all" refusal below stays: an empty id list would leave a comment that names only us.
        parts.Add(ClientTag);
        return string.Join(" / ", parts);
    }

    /// <summary>
    /// F-M480/F-M481: builds the upload form's field list and validates it against what the page
    /// accepts. Nothing is sent here.
    /// </summary>
    /// <param name="showId">The show id from <see cref="ShowsAsync"/> — 0 is not accepted (see remarks).</param>
    /// <param name="season">1–22.</param>
    /// <param name="episode">0–100.</param>
    /// <param name="language">A code from <see cref="Languages"/>.</param>
    /// <param name="fileName">File name sent in the part; must end in .srt or .zip.</param>
    /// <param name="data">The bytes of the subtitle.</param>
    /// <param name="rip">A value from <see cref="Rips"/>, empty allowed.</param>
    /// <param name="release">Free text (release group), empty allowed.</param>
    /// <param name="tmdbId">TMDb id — the comment carries the ids, so at least one id is required.</param>
    /// <param name="imdbId">IMDb id.</param>
    /// <param name="commentExtra">Optional trailing comment text.</param>
    /// <param name="doubleEpisode">True for a double episode ("double"=1).</param>
    /// <param name="hearingImpaired">True for the SDH variant ("hi"=1).</param>
    /// <returns>The finished upload payload.</returns>
    /// <remarks>
    /// The site's select has a "0 = none" entry; an upload with tvshow=0 is filed under no series at
    /// all and cannot be found again, so 0 is refused here. The AUTHOR field is deliberately not part
    /// of the payload: the operator asked for it to stay empty, and an absent multipart part is the only
    /// way to guarantee that whatever the page pre-fills is not overridden.
    /// </remarks>
    public static TvSubtitlesUpload BuildUpload(
        int showId,
        int season,
        int episode,
        string language,
        string fileName,
        byte[] data,
        string? rip = null,
        string? release = null,
        long? tmdbId = null,
        string? imdbId = null,
        string? commentExtra = null,
        bool doubleEpisode = false,
        bool hearingImpaired = false)
    {
        var problems = new List<string>();
        if (showId <= 0)
        {
            problems.Add("show id must come from the show list (0 = no series is refused)");
        }

        if (season < 1 || season > MaxSeason)
        {
            problems.Add("season must be 1.." + MaxSeason.ToString(CultureInfo.InvariantCulture));
        }

        if (episode < 0 || episode > MaxEpisode)
        {
            problems.Add("episode must be 0.." + MaxEpisode.ToString(CultureInfo.InvariantCulture));
        }

        // F-M502: die Pruefung laeuft ueber die Bruecke, nicht ueber die Tabelle — ein ISO-Code
        // (gre, cze, chi, jpn, deu …) oder ein Code mit Region (pt-BR) wird auf das Kuerzel der Seite
        // gezogen. Was die Bruecke nicht kennt, ist unbekannt und wird nicht geraten.
        var siteLanguage = SiteLanguage(language);
        if (siteLanguage is null)
        {
            problems.Add("language must be one of the site's codes (ISO codes are mapped): " +
                string.Join(", ", Languages.Keys.OrderBy(k => k, StringComparer.Ordinal)));
        }

        var ripValue = rip ?? string.Empty;
        if (!Rips.Contains(ripValue))
        {
            problems.Add("rip must be one of: " + string.Join(", ", Rips.Where(r => r.Length > 0)));
        }

        if (tmdbId is null && string.IsNullOrWhiteSpace(imdbId))
        {
            problems.Add("tmdb or imdb id required — the comment field carries them");
        }

        if (data is null || data.Length == 0)
        {
            problems.Add("subtitle file is empty");
        }

        var name = string.IsNullOrWhiteSpace(fileName) ? "subtitle.srt" : fileName.Trim();
        if (!name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("file name must end in .srt or .zip");
        }

        if (problems.Count > 0)
        {
            // F-M498: der Text prueft den ganzen Payload, nicht showId — der Parametername stand
            // falsch und haengte sich an jede Meldung an ("rip must be one of … (Parameter 'showId')").
            throw new ArgumentException(string.Join("; ", problems), "upload");
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("tvshow", showId.ToString(CultureInfo.InvariantCulture)),
            new("season", season.ToString(CultureInfo.InvariantCulture)),
            new("episode", episode.ToString(CultureInfo.InvariantCulture)),
            new("lang", siteLanguage!),
            new("rip", ripValue),
            new("release", release?.Trim() ?? string.Empty),
            new("comment", Comment(tmdbId, imdbId, commentExtra)),
            new("go", "1"),
        };

        if (doubleEpisode)
        {
            fields.Add(new KeyValuePair<string, string>("double", "1"));
        }

        if (hearingImpaired)
        {
            // F-M498: "hi" ist im Formular ein Kontrollkaestchen mit value="1" (am 15.11.2026 am
            // echten /add.html abgelesen). Ein nicht angehaktes Kaestchen wird gar nicht gesendet —
            // deshalb hier nur bei true, und niemals "hi=0": die Seite kennt keinen Gegenwert.
            fields.Add(new KeyValuePair<string, string>("hi", "1"));
        }

        // F-M502: die Sprache des Eintrags kommt von der Flagge und ist schon ein Seiten-Kuerzel; der
        // gesuchte Code kann dagegen ISO sein ("gre"). Beide laufen deshalb durch dieselbe Bruecke —
        // sonst faende die Ruecklesung einen Eintrag nicht, den der Upload gerade angelegt hat.
        return new TvSubtitlesUpload(
            fields, name, data!,
            Languages[siteLanguage!].Name, Languages[siteLanguage!].Iso3);
    }

    /// <summary>
    /// F-M480: builds the payload that creates a new show (<c>/add-tvshow1.php</c>). The page itself asks
    /// to check first — "Before adding new TV-Show please check if it already exist" — so a caller
    /// resolves the name through <see cref="ResolveShow"/> before it gets here.
    /// <para>
    /// F-M501: die Staffelzahl ist auf <see cref="MaxSeason"/> (1–22) begrenzt, weil die Seite genau so
    /// viele kennt — ihr Auswahlfeld bietet 1..22. Die frühere Grenze 1..100 war unsere eigene Prüfung
    /// und liess einen Wert durch, den das Formular stillschweigend auf seinen letzten Eintrag zieht:
    /// eine Serie mit 30 Staffeln wäre als 22-Staffel-Serie angelegt worden.
    /// </para>
    /// </summary>
    /// <param name="name">The show name.</param>
    /// <param name="year">Year of first broadcast, four digits.</param>
    /// <param name="seasons">Number of seasons, 1–22 (the page's own range).</param>
    /// <returns>The finished form payload.</returns>
    public static TvSubtitlesAddShow BuildAddShow(string name, int year, int seasons)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2)
        {
            problems.Add("name missing");
        }

        if (year < 1900 || year > 2100)
        {
            problems.Add("year must be four digits (1900..2100)");
        }

        if (seasons < 1 || seasons > MaxSeason)
        {
            problems.Add("seasons must be 1.." + MaxSeason.ToString(CultureInfo.InvariantCulture));
        }

        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", problems), nameof(name));
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("name", name.Trim()),
            new("year_s", year.ToString(CultureInfo.InvariantCulture)),
            new("seasons", seasons.ToString(CultureInfo.InvariantCulture)),
            new("go", "1"),
        };

        return new TvSubtitlesAddShow(fields);
    }

    /// <summary>
    /// F-M480: sends the upload. Without <paramref name="dryRun"/> false nothing leaves the process —
    /// the body is built either way, so a dry run measures exactly what a real one would send.
    /// </summary>
    /// <param name="upload">The payload from <see cref="BuildUpload"/>.</param>
    /// <param name="dryRun">True (default) builds the body and sends nothing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result, including the byte count and the response text.</returns>
    public Task<TvSubtitlesUploadResult> UploadAsync(
        TvSubtitlesUpload upload, bool dryRun = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var (body, boundary) = BuildUploadBody(upload);
        return SendAsync(BaseUrl + "/add1.php", BaseUrl + "/add.html", body, boundary, dryRun, cancellationToken);
    }

    /// <summary>
    /// F-M480: sends the "create show" form. Dry run is the default, exactly like the upload.
    /// </summary>
    /// <param name="addShow">The payload from <see cref="BuildAddShow"/>.</param>
    /// <param name="dryRun">True (default) builds the body and sends nothing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result, including the byte count and the response text.</returns>
    public Task<TvSubtitlesUploadResult> AddShowAsync(
        TvSubtitlesAddShow addShow, bool dryRun = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addShow);
        var (body, boundary) = BuildFieldBody(addShow.Fields);
        return SendAsync(
            BaseUrl + "/add-tvshow1.php", BaseUrl + "/add-tvshow.php", body, boundary, dryRun, cancellationToken);
    }

    /// <summary>
    /// F-M480: builds the multipart body of an upload and hands back the boundary with it. Exposed so a
    /// suite can assert the exact bytes without a network call.
    /// </summary>
    /// <param name="upload">The payload.</param>
    /// <returns>The body and its boundary.</returns>
    public static (byte[] Body, string Boundary) BuildUploadBody(TvSubtitlesUpload upload)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var boundary = "----sub-scribe" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        using var buffer = new MemoryStream();

        void Write(string text) => buffer.Write(Encoding.UTF8.GetBytes(text));

        foreach (var field in upload.Fields)
        {
            Write("--" + boundary + "\r\n");
            Write("Content-Disposition: form-data; name=\"" + field.Key + "\"\r\n\r\n");
            Write(field.Value + "\r\n");
        }

        Write("--" + boundary + "\r\n");
        Write("Content-Disposition: form-data; name=\"upfile\"; filename=\"" + upload.FileName + "\"\r\n");
        Write("Content-Type: application/octet-stream\r\n\r\n");
        buffer.Write(upload.Data, 0, upload.Data.Length);
        Write("\r\n");
        Write("--" + boundary + "--\r\n");

        return (buffer.ToArray(), boundary);
    }

    private static (byte[] Body, string Boundary) BuildFieldBody(IReadOnlyList<KeyValuePair<string, string>> fields)
    {
        var boundary = "----sub-scribe" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        using var buffer = new MemoryStream();
        foreach (var field in fields)
        {
            buffer.Write(Encoding.UTF8.GetBytes(
                "--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + field.Key + "\"\r\n\r\n"
                + field.Value + "\r\n"));
        }

        buffer.Write(Encoding.UTF8.GetBytes("--" + boundary + "--\r\n"));
        return (buffer.ToArray(), boundary);
    }

    private async Task<TvSubtitlesUploadResult> SendAsync(
        string url,
        string referer,
        byte[] body,
        string boundary,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (dryRun)
        {
            return new TvSubtitlesUploadResult(false, null, null, body.Length, boundary);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Referrer = new Uri(referer);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType =
            System.Net.Http.Headers.MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + boundary);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var plain = TagStripper.Replace(Regex.Replace(text, "\\s+", " ", RegexOptions.CultureInvariant), " ");
        return new TvSubtitlesUploadResult(
            true, (int)response.StatusCode, plain.Trim(), body.Length, boundary);
    }

    private async Task<string> GetStringAsync(string url, string? referer, CancellationToken cancellationToken)
    {
        var bytes = await GetBytesAsync(url, referer, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private async Task<byte[]> GetBytesAsync(string url, string? referer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(referer))
        {
            request.Headers.Referrer = new Uri(referer);
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// F-M480: one subtitle offered on an episode page.
/// </summary>
/// <param name="SubtitleId">The id used by <c>/subtitle-&lt;id&gt;.html</c> and <c>/download-&lt;id&gt;.html</c>.</param>
/// <param name="Language">The site's language code, taken from the flag image.</param>
/// <param name="Rip">The rip cell's text, empty when the entry has none.</param>
/// <param name="Release">The release cell's text, empty when the entry has none.</param>
/// <param name="Age">The timestamp the page shows, as written.</param>
public sealed record TvSubtitlesEntry(
    string SubtitleId, string Language, string Rip, string Release, string Age);

/// <summary>
/// F-M480: a finished upload payload — the form's fields, the file and the two derived names.
/// </summary>
/// <param name="Fields">The form fields in the order the page sends them.</param>
/// <param name="FileName">The file name in the multipart part.</param>
/// <param name="Data">The subtitle bytes.</param>
/// <param name="LanguageName">The language as the page spells it.</param>
/// <param name="Iso3">The three-letter code used in file names.</param>
public sealed record TvSubtitlesUpload(
    IReadOnlyList<KeyValuePair<string, string>> Fields, string FileName, byte[] Data, string LanguageName, string Iso3);

/// <summary>
/// F-M480: the "create show" payload.
/// </summary>
/// <param name="Fields">The form fields (name, year_s, seasons, go).</param>
public sealed record TvSubtitlesAddShow(IReadOnlyList<KeyValuePair<string, string>> Fields);

/// <summary>
/// F-M480: what an upload did — or what it would have sent, in a dry run.
/// </summary>
/// <param name="Sent">False in a dry run; nothing left the process.</param>
/// <param name="Status">The HTTP status, null in a dry run.</param>
/// <param name="Response">The response text with tags and whitespace collapsed, null in a dry run.</param>
/// <param name="BodyBytes">The size of the multipart body that was (or would be) sent.</param>
/// <param name="Boundary">The multipart boundary used.</param>
public sealed record TvSubtitlesUploadResult(
    bool Sent, int? Status, string? Response, int BodyBytes, string Boundary);
