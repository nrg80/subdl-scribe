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

// T204 / F-M483-F-M484 (operator order 15.11.2026): two download endpoints and two upload endpoints,
// the rest of the plugin unchanged.
//
// What is asserted here, and why each one would bite if it drifted:
//   1. FOUR endpoints exist, two per direction, one per source — the whole point of the order.
//   2. The CATALOG hands out only what may run: switch on AND wired. A catalog that ignores the
//      wiring would let a caller believe tvsubtitles.net runs.
//   3. An implemented-but-unwired endpoint NAMES its reason, in the same words the page shows —
//      one sentence, not three (F-M366 line).
//   4. The SubDL download endpoint maps the client's candidate onto the neutral hit; the mapping is
//      pure, so it is checked without a network call.
//   5. The tvsubtitles download endpoint really walks the captured pages: show list → season →
//      episode page → entries filtered to the wanted language → fetch. The fake handler serves the
//      fixtures, so a changed page shape shows up as a red check.
//   6. The tvsubtitles upload endpoint builds the form body and sends NOTHING in a dry run, and
//      refuses a season pack and an unknown language with a reason.
//   7. The SubDL upload endpoint refuses what it cannot do (season pack, no id, non-UTF-8 bytes)
//      instead of half-sending it.

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
using Jellyfin.Plugin.SubdlScribe.Api;
using Jellyfin.Plugin.SubdlScribe.Api.Endpoints;
using Jellyfin.Plugin.SubdlScribe.Configuration;

internal static class Program
{
    private static int _failures;
    private static int _checks;
    private static string _fixtures = string.Empty;

    private static async Task<int> Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";
        _fixtures = Path.Combine(root, "scripts/tests/tvsubtitles/fixtures");

        // ---- 1. four endpoints, two per direction ----------------------------------------------
        var tvClient = new TvSubtitlesClient(new HttpClient(new FixtureHandler(_fixtures)));
        var subdlClient = new SubdlApiClient(new HttpClient(new FixtureHandler(_fixtures)));
        var catalog = new SubtitleEndpointCatalog(
            new IDownloadEndpoint[]
            {
                new SubdlDownloadEndpoint(subdlClient),
                new TvSubtitlesDownloadEndpoint(tvClient),
            },
            new IUploadEndpoint[]
            {
                new SubdlUploadEndpoint(subdlClient),
                new TvSubtitlesUploadEndpoint(tvClient, dryRun: true),
            });

        Check("there are two download endpoints, one per source",
            catalog.AllDownloads.Count == 2
            && catalog.AllDownloads.Select(e => e.Provider).Distinct().Count() == 2);
        Check("there are two upload endpoints, one per source",
            catalog.AllUploads.Count == 2
            && catalog.AllUploads.Select(e => e.Provider).Distinct().Count() == 2);
        Check("both sources are wired in both directions (F-M485)",
            catalog.AllDownloads.All(e => e.Wired) && catalog.AllUploads.All(e => e.Wired));

        // ---- 2. the catalog obeys switches AND wiring ------------------------------------------
        var aus = Conf(c => { c.DownloadEnabled = false; c.UploadEnabled = false; });
        Check("everything off yields no endpoint",
            catalog.EnabledDownloads(aus).Count == 0 && catalog.EnabledUploads(aus).Count == 0);

        var nurDownload = Conf(c => { c.DownloadEnabled = true; c.UploadEnabled = false; });
        Check("the download switch alone yields exactly the SubDL download endpoint",
            catalog.EnabledDownloads(nurDownload).Count == 1
            && catalog.EnabledDownloads(nurDownload)[0].Provider == SubtitleProvider.Subdl
            && catalog.EnabledUploads(nurDownload).Count == 0);

        var beide = Conf(c => { c.DownloadEnabled = true; c.UploadEnabled = true; });
        Check("switching SubDL on does not drag tvsubtitles into the run",
            catalog.EnabledDownloads(beide).All(e => e.Provider == SubtitleProvider.Subdl)
            && catalog.EnabledUploads(beide).All(e => e.Provider == SubtitleProvider.Subdl));

        var tvAn = Conf(c =>
        {
            c.DownloadEnabled = true;
            c.UploadEnabled = true;
            c.TvSubtitlesDownloadEnabled = true;
            c.TvSubtitlesUploadEnabled = true;
        });
        Check("the tvsubtitles switches now pull the second endpoint in, each on its own",
            catalog.EnabledDownloads(tvAn).Count == 2 && catalog.EnabledUploads(tvAn).Count == 2,
            "downloads=" + catalog.EnabledDownloads(tvAn).Count + " uploads=" + catalog.EnabledUploads(tvAn).Count);

        var tvNurDownload = Conf(c =>
        {
            c.DownloadEnabled = false;
            c.UploadEnabled = false;
            c.TvSubtitlesDownloadEnabled = true;
            c.TvSubtitlesUploadEnabled = false;
        });
        Check("tvsubtitles download alone runs while both SubDL directions stay off",
            catalog.EnabledDownloads(tvNurDownload).Count == 1
            && catalog.EnabledDownloads(tvNurDownload)[0].Provider == SubtitleProvider.TvSubtitles
            && catalog.EnabledUploads(tvNurDownload).Count == 0);

        var tvNurUpload = Conf(c =>
        {
            c.DownloadEnabled = false;
            c.UploadEnabled = false;
            c.TvSubtitlesDownloadEnabled = false;
            c.TvSubtitlesUploadEnabled = true;
        });
        Check("tvsubtitles upload alone runs while everything else is off",
            catalog.EnabledUploads(tvNurUpload).Count == 1
            && catalog.EnabledUploads(tvNurUpload)[0].Provider == SubtitleProvider.TvSubtitles
            && catalog.EnabledDownloads(tvNurUpload).Count == 0);

        var nurSubdlUpload = Conf(c =>
        {
            c.DownloadEnabled = false;
            c.UploadEnabled = true;
            c.TvSubtitlesUploadEnabled = false;
        });
        Check("SubDL upload alone stays alone", catalog.EnabledUploads(nurSubdlUpload).Count == 1
            && catalog.EnabledUploads(nurSubdlUpload)[0].Provider == SubtitleProvider.Subdl);

        // ---- 3. the refusal names its reason, in the page's words ------------------------------
        var refusedDown = catalog.Refused(tvAn, SubtitleDirection.Download);
        var refusedUp = catalog.Refused(tvAn, SubtitleDirection.Upload);
        Check("with both switches on nothing is refused", refusedDown.Count == 0 && refusedUp.Count == 0);
        var tvAus = Conf(c => { c.DownloadEnabled = true; c.TvSubtitlesDownloadEnabled = false; });
        var refusedTvAus = catalog.Refused(tvAus, SubtitleDirection.Download);
        Check("a source whose switch is off is listed as refused, not silently missing",
            refusedTvAus.Count == 1 && refusedTvAus[0].Provider == SubtitleProvider.TvSubtitles
            && refusedTvAus[0].Reason == "switched off");
        Check("the sentence for a source is the one the config page prints",
            PluginConfiguration.MissingProviderReason(SubtitleProvider.TvSubtitles) is { Length: > 20 }
            && PluginConfiguration.MissingProviderReason(SubtitleProvider.TvSubtitles)!
                .Contains("Staffelpacks", StringComparison.Ordinal));
        Check("a wired endpoint that is switched off says so",
            catalog.Refused(aus, SubtitleDirection.Download)
                .Any(r => r.Provider == SubtitleProvider.Subdl && r.Reason == "switched off"));

        // ---- 4. the SubDL mapping is pure and complete ----------------------------------------
        var candidate = new SubtitleCandidate
        {
            ReleaseName = "The.Middle.S02E24.720p.HDTV.x264-LOL",
            Language = "DE",
            Url = "https://dl.subdl.com/subtitle/abc123.zip",
            Season = 2,
            Episode = 24,
            FullSeason = false,
        };
        var map = SubdlDownloadEndpoint.Map(candidate);
        Check("a SubDL candidate becomes a neutral hit without losing its handle",
            map.Provider == SubtitleProvider.Subdl && map.Id == candidate.Url
            && map.Language == "DE" && map.Release == candidate.ReleaseName && !map.FullSeason);
        Check("SubDL has no rip cell, so the hit does not invent one", map.Rip.Length == 0);
        Check("the language bridge knows the site's vocabulary and refuses the rest",
            TvSubtitlesDownloadEndpoint.SiteLanguage("DE") == "de"
            && TvSubtitlesDownloadEndpoint.SiteLanguage("pt") == "pt"
            && TvSubtitlesDownloadEndpoint.SiteLanguage("xx") is null);

        // ---- 5. the tvsubtitles download endpoint walks the captured pages ---------------------
        var tvDownload = new TvSubtitlesDownloadEndpoint(tvClient);
        var query = new SubtitleQuery("The Middle", null, null, 2, 24, "en");
        var hits = await tvDownload.SearchAsync(query).ConfigureAwait(false);
        Check("the search finds the episode page and keeps the wanted language",
            hits.Count == 1 && hits[0].Language == "en" && hits[0].Id == "170678",
            "found: " + string.Join(",", hits.Select(h => h.Language + "/" + h.Id)));
        var andereSprache = await tvDownload.SearchAsync(query with { Language = "fr" }).ConfigureAwait(false);
        Check("a different language yields that language's entry instead",
            andereSprache.Count == 1 && andereSprache[0].Id == "190460");
        var falscheFolge = await tvDownload.SearchAsync(query with { Episode = 23 }).ConfigureAwait(false);
        Check("an episode the source does not carry yields nothing, not the nearest one",
            falscheFolge.Count == 0);
        var unbekannteSerie = await tvDownload.SearchAsync(query with { ShowName = "Nope-Nope" }).ConfigureAwait(false);
        Check("an unknown show yields nothing", unbekannteSerie.Count == 0);
        var ohneNamen = await tvDownload.SearchAsync(query with { ShowName = null }).ConfigureAwait(false);
        Check("a source without an id index needs the name — no name, no hits", ohneNamen.Count == 0);

        var payload = await tvDownload.FetchAsync(hits[0]).ConfigureAwait(false);
        Check("the fetch delivers name and bytes, undecoded",
            payload is not null && payload.Data.Length > 0 && payload.FileName.EndsWith(".srt", StringComparison.Ordinal),
            payload is null ? "no payload" : payload.FileName + " " + payload.Data.Length + " B");

        // ---- 6. the tvsubtitles upload endpoint: dry run, and refusals -------------------------
        var tvUpload = new TvSubtitlesUploadEndpoint(tvClient, dryRun: true);
        var ticket = new UploadTicket(
            "The Middle", "tt1442464", "1422", 2, 24, "de", "The.Middle.S02E24.de.srt",
            Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nhallo\n"), "HDTV", "FEVER", false, false, false);
        var dry = await tvUpload.SendAsync(ticket).ConfigureAwait(false);
        Check("a dry run reports a built body, sends nothing, and says it was a dry run",
            dry.Ok && dry.DryRun && dry.Bytes > ticket.Data.Length && dry.Provider == SubtitleProvider.TvSubtitles,
            "bytes=" + dry.Bytes);
        var pack = await tvUpload.SendAsync(ticket with { SeasonPack = true }).ConfigureAwait(false);
        Check("a season pack is refused — this source has none", !pack.Ok && pack.Bytes == 0
            && pack.Reason!.Contains("no season packs", StringComparison.Ordinal));
        var sprache = await tvUpload.SendAsync(ticket with { Language = "xx" }).ConfigureAwait(false);
        Check("an unknown language is refused before any request", !sprache.Ok && sprache.Reason!.Contains("xx", StringComparison.Ordinal));
        var ohneShow = await tvUpload.SendAsync(ticket with { ShowName = null }).ConfigureAwait(false);
        Check("without a show name nothing can be resolved", !ohneShow.Ok && ohneShow.Reason!.Contains("show name", StringComparison.Ordinal));
        var unbekannt = await tvUpload.SendAsync(ticket with { ShowName = "Nope-Nope" }).ConfigureAwait(false);
        Check("a show the site does not carry is refused with its name", !unbekannt.Ok
            && unbekannt.Reason!.Contains("Nope-Nope", StringComparison.Ordinal));

        // ---- 7. the SubDL upload endpoint refuses what it cannot do ---------------------------
        var subdlUpload = new SubdlUploadEndpoint(subdlClient);
        var keinPack = await subdlUpload.SendAsync(ticket with { SeasonPack = true }).ConfigureAwait(false);
        Check("SubDL refuses a season pack here — packs are the pipeline's job (F-M372)",
            !keinPack.Ok && keinPack.Reason!.Contains("season packs", StringComparison.Ordinal));
        var ohneImdb = await subdlUpload.SendAsync(ticket with { ImdbId = null }).ConfigureAwait(false);
        Check("SubDL refuses without an IMDb id", !ohneImdb.Ok && ohneImdb.Reason == "no-imdb");
        var cp1250 = await subdlUpload.SendAsync(
            ticket with { Data = new byte[] { 0x41, 0xF5, 0x42 } }).ConfigureAwait(false);
        Check("SubDL refuses bytes that are not UTF-8 instead of transcoding them",
            !cp1250.Ok && cp1250.Reason!.Contains("not UTF-8", StringComparison.Ordinal));
        var angemeldet = await subdlUpload.SendAsync(ticket).ConfigureAwait(false);
        Check("without a session the refusal is readable, not an exception",
            !angemeldet.Ok && angemeldet.Reason!.Contains("Not logged in", StringComparison.Ordinal));

        // ---- 8. die Einbindung in den Lauf: Quelltext-Bindung (F-M485) -----------------------
        var root2 = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";
        var downloadSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs"));
        var uploadSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        Check("the download pipeline asks the second source exactly where a language would be given up",
            // F-M494: der Schalter wird seit dem Einzelpruefpunkt ueber SourceAllowed gelesen — die
            // Absicht der Pruefung ist dieselbe: die zweite Quelle wird in der Sprachschleife gefragt,
            // bevor „nicht verfuegbar" geschrieben wird.
            (downloadSource.Contains("SourceAllowed(Configuration.SubtitleProvider.TvSubtitles)", StringComparison.Ordinal)
             || downloadSource.Contains("IsDownloadEnabled(Configuration.SubtitleProvider.TvSubtitles)", StringComparison.Ordinal))
            && downloadSource.Contains("ForeignCandidatesAsync(title, isSeries, season, episode, lang, ct)", StringComparison.Ordinal),
            "the fallback sits in the language loop, before \"not available\"");
        Check("a foreign candidate is fetched from its own endpoint, not over SubDL's routes",
            downloadSource.Contains("cand.Provider != Configuration.SubtitleProvider.Subdl", StringComparison.Ordinal)
            && downloadSource.Contains("Route = \"tvsubtitles\"", StringComparison.Ordinal));
        Check("the upload pipeline sends to the second source through the same gate, and its switch is asked (F-M485/F-M495)",
            uploadSource.Contains("SourceAllowed(Configuration.SubtitleProvider.TvSubtitles)", StringComparison.Ordinal)
            && uploadSource.Contains("SendToSecondSourceAsync(", StringComparison.Ordinal));
        Check("the SubDL upload call itself is untouched — the response body the caller reads stays",
            uploadSource.Contains("_api.UploadSubtitleAsync(srtContent, neutralName, lang, releaseName, quality, imdbId, tmdbId, isSeries, season, episode, ct, hearingImpaired, framerate)", StringComparison.Ordinal));
        Check("the second source never breaks the run — its errors are warnings",
            downloadSource.Contains("[tvsubtitles-D] search failed for", StringComparison.Ordinal));

        // ---- 9. Herkunft und Kreuzkontamination (F-M486/F-M487) -------------------------------
        Check("the store value names the source, and parses back",
            Provenance.Name(SubtitleProvider.Subdl) == "subdl"
            && Provenance.Name(SubtitleProvider.TvSubtitles) == "tvsubtitles"
            && Provenance.Parse("tvsubtitles") == SubtitleProvider.TvSubtitles
            && Provenance.Parse("SubDL") == SubtitleProvider.Subdl
            && Provenance.Parse(null) is null
            && Provenance.Parse("  ") is null);
        // F-M500 (Operator-Anweisung 15.11.2026, verschaerft): eine BEKANNTE Herkunft geht NIRGENDS hoch —
        // nicht zurueck zur eigenen Quelle (Reup) und nicht zur anderen (Kreuzkontamination). Vorher galt
        // "bekannt = nur zur eigenen Quelle"; das ist aufgehoben.
        Check("a tvsubtitles find goes nowhere — not to SubDL and not back to tvsubtitles",
            !Provenance.MayUploadTo("tvsubtitles", SubtitleProvider.Subdl)
            && !Provenance.MayUploadTo("tvsubtitles", SubtitleProvider.TvSubtitles)
            && !Provenance.MayUploadTo("subdl", SubtitleProvider.TvSubtitles)
            && !Provenance.MayUploadTo("subdl", SubtitleProvider.Subdl));
        Check("the origin is named in the reason the run logs",
            Provenance.OriginBlockedReason("subdl") is { } subdlSatz
            && subdlSatz.Contains("origin:subdl", StringComparison.Ordinal)
            && subdlSatz.Contains("never uploaded", StringComparison.Ordinal)
            && Provenance.OriginBlockedReason("tvsubtitles") is { } tvSatz
            && tvSatz.Contains("origin:tvsubtitles", StringComparison.Ordinal)
            && Provenance.OriginBlockedReason(null) is null,
            (Provenance.OriginBlockedReason("subdl") ?? "-") + " | " + (Provenance.OriginBlockedReason("tvsubtitles") ?? "-"));
        Check("unknown provenance stays allowed everywhere — a hand copy or an embedded track",
            Provenance.MayUploadTo(null, SubtitleProvider.Subdl)
            && Provenance.MayUploadTo(null, SubtitleProvider.TvSubtitles)
            && Provenance.MayUploadTo(string.Empty, SubtitleProvider.Subdl));
        Check("the refusal sentence names both sides",
            Provenance.CrossSourceReason("tvsubtitles", SubtitleProvider.Subdl)
                .Contains("tvsubtitles", StringComparison.Ordinal)
            && Provenance.CrossSourceReason("tvsubtitles", SubtitleProvider.Subdl)
                .Contains("subdl", StringComparison.Ordinal));
        Check("the download records the origin and the source's own id in the store",
            downloadSource.Contains("Api.Endpoints.Provenance.Name(cand.Provider)", StringComparison.Ordinal)
            && downloadSource.Contains("cand.Provider == Configuration.SubtitleProvider.Subdl ? cand.SubdlId : cand.Url", StringComparison.Ordinal));
        Check("the upload reads the origin from the store before it picks an endpoint",
            uploadSource.Contains("Registry.GetOrigin(contentHashEarly)", StringComparison.Ordinal)
            && uploadSource.Contains("UploadGate.Decide(", StringComparison.Ordinal)
            && uploadSource.Contains("herkunft,", StringComparison.Ordinal));
        Check("the store schema carries the two columns and a migration step",
            ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/Entities.cs"))
                .Contains("public string? OriginId { get; set; }", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/SubdlDbContext.cs"))
                .Contains("ALTER TABLE sidecars ADD COLUMN Origin TEXT NULL;", StringComparison.Ordinal)
            // F-M501: der Marker steht auf 13 (media.TvstShowId kam dazu); die Pruefung fragt den
            // AKTUELLEN Stand, weil ein stehengebliebener Marker die Migration nicht mehr anzeigt.
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/SubdlDbContext.cs"))
                .Contains("CurrentSchemaVersion = 13", StringComparison.Ordinal));

        // ---- 10. eigene Kennzeichen je Quelle und Richtung (F-M488) ---------------------------
        var entities = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/Entities.cs"));
        var dbc = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/SubdlDbContext.cs"));
        Check("four own fields exist, one per source and direction",
            entities.Contains("public string? SubdlDownloadId { get; set; }", StringComparison.Ordinal)
            && entities.Contains("public string? SubdlUploadId { get; set; }", StringComparison.Ordinal)
            && entities.Contains("public string? TvSubtitlesDownloadId { get; set; }", StringComparison.Ordinal)
            && entities.Contains("public string? TvSubtitlesUploadId { get; set; }", StringComparison.Ordinal));
        Check("schema 11 adds them to sidecars AND embeds",
            dbc.Contains("CurrentSchemaVersion = 13", StringComparison.Ordinal)
            && dbc.Contains("ALTER TABLE sidecars ADD COLUMN TvSubtitlesUploadId TEXT NULL;", StringComparison.Ordinal)
            && dbc.Contains("ALTER TABLE embeds ADD COLUMN TvSubtitlesDownloadId TEXT NULL;", StringComparison.Ordinal));
        Check("the download writes the field of its own source",
            downloadSource.Contains("subdlDownloadId: cand.Provider == Configuration.SubtitleProvider.Subdl ? cand.SubdlId : null", StringComparison.Ordinal)
            && downloadSource.Contains("tvSubtitlesDownloadId: cand.Provider == Configuration.SubtitleProvider.TvSubtitles ? cand.Url : null", StringComparison.Ordinal));
        Check("the upload writes SubDL's id into SubDL's field",
            uploadSource.Contains("subdlUploadId: subdlGenommen", StringComparison.Ordinal)
            && uploadSource.Contains("uploadId?.ToString(System.Globalization.CultureInfo.InvariantCulture)", StringComparison.Ordinal));
        Check("the second source's upload id is carried in the result and stored in its own field",
            uploadSource.Contains("tvSubtitlesUploadId: zweiteGenommen", StringComparison.Ordinal)
            && uploadSource.Contains("zweiteErgebnis!.SourceId", StringComparison.Ordinal));
        var ownId = Provenance.OwnUploadId(SubtitleProvider.TvSubtitles, "abc123", "The.Middle.S02E24.720p", new DateTime(2026, 11, 15, 14, 30, 12, DateTimeKind.Utc));
        Check("a missing id is replaced by our own, unmistakably prefixed",
            ownId.StartsWith("own:tvsubtitles:20261115T143012Z:", StringComparison.Ordinal)
            && Provenance.IsOwnUploadId(ownId)
            && !Provenance.IsOwnUploadId("170678"));
        Check("the minted id is reproducible for the same input and differs for another subtitle",
            ownId == Provenance.OwnUploadId(SubtitleProvider.TvSubtitles, "abc123", "The.Middle.S02E24.720p", new DateTime(2026, 11, 15, 14, 30, 12, DateTimeKind.Utc))
            && ownId != Provenance.OwnUploadId(SubtitleProvider.TvSubtitles, "def456", "The.Middle.S02E24.720p", new DateTime(2026, 11, 15, 14, 30, 12, DateTimeKind.Utc)));
        Check("only the source that took the upload gets its mark",
            uploadSource.Contains("subdlUploadId: subdlGenommen", StringComparison.Ordinal)
            && uploadSource.Contains("tvSubtitlesUploadId: zweiteGenommen", StringComparison.Ordinal)
            && !uploadSource.Contains("subdlUploadId: vonZweiter", StringComparison.Ordinal));
        Check("the id is read back from the episode page, never invented",
            ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/TvSubtitlesClient.cs"))
                .Contains("public async Task<string?> FindUploadedIdAsync(", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/UploadResult.cs"))
                .Contains("public string? SourceId { get; init; }", StringComparison.Ordinal));

        // ---- 11. Reup und Kreuzkontamination (F-M489) -----------------------------------------
        var g = UploadGate.Decide(subdlSwitch: true, tvSubtitlesSwitch: true, origin: null, subdlAlready: false, tvSubtitlesAlready: false);
        Check("nothing uploaded yet and both switches on: both sources may run", g.Subdl && g.TvSubtitles && g.SkipReason is null);
        Check("no reup on tvsubtitles: that source already has it",
            UploadGate.Decide(true, true, null, false, true) is { Subdl: true, TvSubtitles: false, SkipReason: null });
        Check("no reup on SubDL: that source already has it",
            UploadGate.Decide(true, true, null, true, false) is { Subdl: false, TvSubtitles: true, SkipReason: null });
        Check("both already have it: nothing runs, and the reason names both",
            UploadGate.Decide(true, true, null, true, true) is { Subdl: false, TvSubtitles: false, SkipReason: "already-uploaded:subdl+tvsubtitles" });
        Check("a SubDL upload does not block tvsubtitles and a tvst upload does not block SubDL",
            UploadGate.Decide(true, true, origin: null, true, false) is { Subdl: false, TvSubtitles: true, SkipReason: null }
            && UploadGate.Decide(true, true, origin: null, false, true) is { Subdl: true, TvSubtitles: false, SkipReason: null }
            && UploadGate.Decide(true, true, origin: null, true, true).SkipReason == "already-uploaded:subdl+tvsubtitles");
        Check("a known origin is refused at BOTH sources, with the origin named as the reason (F-M500)",
            UploadGate.Decide(true, true, "tvsubtitles", false, false) is { Subdl: false, TvSubtitles: false } tvTor
            && tvTor.SkipReason == "origin:tvsubtitles — downloaded there, never uploaded"
            && UploadGate.Decide(true, true, "subdl", false, false) is { Subdl: false, TvSubtitles: false } sdTor
            && sdTor.SkipReason == "origin:subdl — downloaded there, never uploaded");
        Check("switches off mean nothing runs",
            UploadGate.Decide(false, false, null, false, false).SkipReason == "no-upload-source");
        Check("the pipeline asks per source, through the one gate — twice, never inline",
            uploadSource.Contains("UploadGate.Decide(", StringComparison.Ordinal)
            && uploadSource.Split("UploadGate.Decide(").Length - 1 >= 2
            && uploadSource.Contains("Registry.IsUploadedTo(mediaHash, lang, hearingImpaired, Configuration.SubtitleProvider.TvSubtitles)", StringComparison.Ordinal)
            && !uploadSource.Contains("Registry.IsUploaded(mediaHash, cand.Lang", StringComparison.Ordinal));

        // ---- 12. "hochgeladen" je Quelle (F-M490) ---------------------------------------------
        var registrySource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/ContentHashRegistry.cs"));
        Check("each source has its own uploaded timestamp, sidecars and embeds alike",
            entities.Contains("public DateTime? SubdlUploadedAt { get; set; }", StringComparison.Ordinal)
            && entities.Contains("public DateTime? TvSubtitlesUploadedAt { get; set; }", StringComparison.Ordinal)
            && dbc.Contains("CurrentSchemaVersion = 13", StringComparison.Ordinal)
            && dbc.Contains("ALTER TABLE sidecars ADD COLUMN SubdlUploadedAt TEXT NULL;", StringComparison.Ordinal)
            && dbc.Contains("ALTER TABLE embeds ADD COLUMN TvSubtitlesUploadedAt TEXT NULL;", StringComparison.Ordinal));
        Check("the timestamp is set for the source that took the upload, never for the other",
            uploadSource.Contains("subdlUploadedAt: subdlGenommen ? DateTime.UtcNow : null", StringComparison.Ordinal)
            && uploadSource.Contains("tvSubtitlesUploadedAt: zweiteGenommen ? DateTime.UtcNow : null", StringComparison.Ordinal));
        Check("\"uploaded to this source\" is read from its own timestamp first",
            registrySource.Contains("x.TvSubtitlesUploadedAt != null || !string.IsNullOrEmpty(x.TvSubtitlesUploadId)", StringComparison.Ordinal)
            && registrySource.Contains("x.SubdlUploadedAt != null", StringComparison.Ordinal));
        Check("a timestamp is never overwritten with nothing",
            registrySource.Contains("if (subdlUploadedAt.HasValue)", StringComparison.Ordinal)
            && registrySource.Contains("if (tvSubtitlesUploadedAt.HasValue)", StringComparison.Ordinal));

        // ---- 13. Reihenfolge und Erfolgsbegriff (F-M491, F-M494) -----------------------------
        Check("the download order is a setting, and tvsubtitles is first by default (operator 15.11.2026)",
            downloadSource.Contains("DownloadOrder", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Configuration/PluginConfiguration.cs"))
                .Contains("DownloadSourceOrder.TvSubtitlesFirst;", StringComparison.Ordinal));
        Check("the GUI offers the order under downloads and saves the chosen value",
            ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Configuration/configPage.html"))
                .Contains("id=\"DownloadOrder\"", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Configuration/configPage.html"))
                .Contains("config.DownloadOrder =", StringComparison.Ordinal));
        Check("both endpoints are asked and their candidates are MERGED, not replaced (F-M494)",
            !downloadSource.Contains("langCands = fremd;", StringComparison.Ordinal)
            && downloadSource.Contains("candidates.AddRange(fremd);", StringComparison.Ordinal)
            && downloadSource.Contains("{Tv} tvsubtitles candidate(s), {Sd} SubDL candidate(s), order {Order}", StringComparison.Ordinal)
            && !downloadSource.Contains("tvZuerst || langCands.Count == 0", StringComparison.Ordinal));
        Check("the ranking leads with the source in the configured order, pack and score stay behind it (F-M494)",
            downloadSource.Contains(
                ".OrderByDescending(x => (x.Cand.Provider == Configuration.SubtitleProvider.TvSubtitles) == tvZuerst)",
                StringComparison.Ordinal)
            && downloadSource.Contains(".ThenByDescending(x => x.Pack)", StringComparison.Ordinal));
        Check("a source change restarts the correction hunt instead of ending it (F-M494)",
            downloadSource.Contains("var offeneAndereQuelle = ranked.Skip(attempts).Where(x => x.Cand.Provider != cand.Provider)", StringComparison.Ordinal)
            && downloadSource.Contains("correctionHuntAbandoned = false;", StringComparison.Ordinal)
            && downloadSource.Contains("refusalsThisRun = 0;", StringComparison.Ordinal));
        Check("the fetch path asks the candidate's own source switch, through ONE helper, before the fetch (F-M494)",
            downloadSource.Contains("private bool SourceAllowed(Configuration.SubtitleProvider provider) => _config.IsDownloadEnabled(provider);", StringComparison.Ordinal)
            && downloadSource.Contains("if (!SourceAllowed(cand.Provider))", StringComparison.Ordinal)
            && downloadSource.Contains("if (!SourceAllowed(hi.Provider))", StringComparison.Ordinal)
            && downloadSource.Contains("SourceAllowed(Configuration.SubtitleProvider.TvSubtitles)", StringComparison.Ordinal)
            && downloadSource.Contains("candidate from {Source} skipped: that source is switched off", StringComparison.Ordinal)
            && !downloadSource.Contains("if (_config.IsDownloadEnabled(Configuration.SubtitleProvider.TvSubtitles))", StringComparison.Ordinal));
        Check("a kept original is a download but not a success while auto-sync is on",
            downloadSource.Contains("if (!_config.QaDownloadAutoSync || alignedForNaming)", StringComparison.Ordinal)
            && downloadSource.Contains("summary.DownloadedWithoutSignificance++;", StringComparison.Ordinal));

        // ---- 14. Quellenabhaengige Vorpruefungen und Herkunft an jeder Ablage (F-M492/F-M493) -----
        var cfgSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Configuration/PluginConfiguration.cs"));
        var dispatcherSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/ScheduledTasks/SubdlEventDispatcher.cs"));
        Check("the only demanded credential is the TMDb key — SubDL's pair is a source question (F-M492/F-M497)",
            cfgSource.Contains("if (string.IsNullOrWhiteSpace(TmdbApiKey))", StringComparison.Ordinal)
            && !cfgSource.Contains("missing.Add(\"SubDL API key\")", StringComparison.Ordinal)
            && !cfgSource.Contains("missing.Add(\"SubDL email\")", StringComparison.Ordinal)
            && !cfgSource.Contains("missing.Add(\"SubDL password\")", StringComparison.Ordinal)
            && cfgSource.Contains("public bool IsConfiguredForUpload(SubtitleProvider provider)", StringComparison.Ordinal)
            && cfgSource.Contains("public bool IsConfiguredForDownload(SubtitleProvider provider)", StringComparison.Ordinal));
        Check("a cycle that does not ask SubDL is not held by the SubDL counters",
            dispatcherSource.Contains("if (!config.IsDownloadEnabled(Configuration.SubtitleProvider.Subdl))", StringComparison.Ordinal));
        Check("a spent SubDL quota does not hold a cycle that still has tvsubtitles",
            dispatcherSource.Contains("secondSourceCanRun", StringComparison.Ordinal)
            && dispatcherSource.Contains("this cycle runs on tvsubtitles.net only (F-M492)", StringComparison.Ordinal));
        var markCalls = System.Text.RegularExpressions.Regex.Matches(downloadSource, @"MarkDownloaded\(").Count;
        var originArgs = System.Text.RegularExpressions.Regex.Matches(downloadSource, @"origin: Api\.Endpoints\.Provenance\.Name\(").Count;
        Check($"every MarkDownloaded call carries its origin ({originArgs}/{markCalls})",
            markCalls >= 4 && originArgs == markCalls);
        Check("the run-end line names the downloads that did not pass the significance",
            downloadSource.Contains("{NotSignificant} downloaded without significance", StringComparison.Ordinal));
        var taskSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/ScheduledTasks/SubdlDownloadTask.cs"));
        var offStateSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/WorkerOffState.cs"));
        Check("the download task asks whether ANY source downloads, not whether SubDL does",
            taskSource.Contains("if (!config.AnyDownloadEnabled)", StringComparison.Ordinal)
            && !taskSource.Contains("IsDownloadEnabled(SubtitleProvider.Subdl)", StringComparison.Ordinal));
        var seederSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/ScheduledTasks/SubdlSeeder.cs"));
        Check("the queue fills for the direction, not for SubDL — a spent SubDL allowance does not empty it",
            seederSource.Contains("bool downloadQueueable = config.AnyDownloadEnabled", StringComparison.Ordinal)
            && seederSource.Contains("(downloadAllowed || config.IsDownloadEnabled(SubtitleProvider.TvSubtitles))", StringComparison.Ordinal)
            && seederSource.Contains("config.AnyUploadEnabled && IsUploadTodo", StringComparison.Ordinal));
        Check("the off-state of a direction is asked for the direction, not for SubDL",
            offStateSource.Contains("return !config.AnyDownloadEnabled", StringComparison.Ordinal)
            && offStateSource.Contains("return !config.AnyUploadEnabled", StringComparison.Ordinal));

        // ---- 15. Upload: beide Endpunkte hintereinander (F-M495) --------------------------------
        Check("the upload walks BOTH sources in the configured order — no single target any more",
            uploadSource.Contains("var quellenReihenfolge = UploadSourceOrder();", StringComparison.Ordinal)
            && uploadSource.Contains("_config.DownloadOrder == Configuration.PluginConfiguration.DownloadSourceOrder.SubdlFirst", StringComparison.Ordinal)
            && uploadSource.Contains("foreach (var quelle in quellenReihenfolge)", StringComparison.Ordinal)
            && uploadSource.Contains("bool subdlGenommen = subdlErgebnis is { Ok: true };", StringComparison.Ordinal)
            && uploadSource.Contains("bool zweiteGenommen = zweiteErgebnis is { Ok: true };", StringComparison.Ordinal)
            && !uploadSource.Contains("var vonZweiter = false;", StringComparison.Ordinal));
        Check("each endpoint's switch is asked right before its start — through ONE helper, skip instead of break (F-M495)",
            uploadSource.Contains("private bool SourceAllowed(Configuration.SubtitleProvider provider) => _config.IsUploadEnabled(provider);", StringComparison.Ordinal)
            && uploadSource.Contains("if (!SourceAllowed(quelle))", StringComparison.Ordinal)
            && uploadSource.Contains("source {Source} skipped: that source is switched off", StringComparison.Ordinal)
            && !uploadSource.Contains("_config.IsUploadEnabled(Configuration.SubtitleProvider.Subdl)", StringComparison.Ordinal)
            && !uploadSource.Contains("_config.IsUploadEnabled(Configuration.SubtitleProvider.TvSubtitles)", StringComparison.Ordinal));
        Check("the provenance gate still decides PER SOURCE — a refused source is skipped, the chain runs on (F-M495)",
            uploadSource.Contains("if (quelle == Configuration.SubtitleProvider.Subdl ? !darfSubdl : !darfZweite)", StringComparison.Ordinal)
            && uploadSource.Contains("Api.Endpoints.Provenance.OriginBlockedReason(herkunft)", StringComparison.Ordinal)
            && uploadSource.Contains("Api.Endpoints.Provenance.MayUploadTo(herkunft, quelle)", StringComparison.Ordinal)
            && uploadSource.Contains("Api.Endpoints.UploadGate.Decide(", StringComparison.Ordinal));

        // ---- 16. Trockenlauf: eine Zeile je Quelle, keine Zugangsdaten (F-M496) -----------------
        // Der Trockenlauf stand als EIN Ausstieg vor der Kette: eine Zeile fuer zwei Endpunkte, und
        // MissingCredentials verlangte fuer ihn Benutzername und Passwort, obwohl er keinen einzigen
        // Aufruf macht. Beides ist gemessen (jf-test, 15.11.2026) und beides wird hier festgehalten.
        var trockenOhneLogin = Conf(c =>
        {
            c.DryRun = true;
            c.UploadEnabled = true;
            c.SubdlUploadEnabled = true;
            c.TvSubtitlesUploadEnabled = true;
            c.Username = string.Empty;
            c.Password = string.Empty;
        });
        Check("a dry run is not refused for missing SubDL credentials (F-M496)",
            trockenOhneLogin.MissingCredentials().Count == 0
            && uploadSource.Contains("if (!_config.DryRun && !_config.IsConfiguredForUpload(quelle))", StringComparison.Ordinal),
            "missing=" + string.Join(",", trockenOhneLogin.MissingCredentials()));

        var dryLineTvst = uploadSource.IndexOf("[tvsubtitles] DRY-RUN would upload {Name} [{Lang}]", StringComparison.Ordinal);
        var dryBlockStart = uploadSource.IndexOf("if (_config.DryRun)", StringComparison.Ordinal);
        var secondSourceCall = uploadSource.IndexOf("await SendToSecondSourceAsync(", StringComparison.Ordinal);
        Check("the tvst branch has its own dry-run guard BEFORE its call — one line per source (F-M496)",
            dryLineTvst > 0 && dryBlockStart > 0 && secondSourceCall > 0
            && dryLineTvst > dryBlockStart
            && dryLineTvst < secondSourceCall
            && uploadSource.Contains("[SubDL] DRY-RUN would upload {Name} [{Lang}]", StringComparison.Ordinal)
            && uploadSource.Contains("foreach (var quelle in UploadSourceOrder())", StringComparison.Ordinal)
            && uploadSource.Contains("if (!SourceAllowed(quelle))", StringComparison.Ordinal)
            && uploadSource.Contains("TvSubtitlesUploadEndpoint(_tvstClient, dryRun: _config.DryRun)", StringComparison.Ordinal),
            "tvst line @" + dryLineTvst + ", dry block @" + dryBlockStart + ", call @" + secondSourceCall);

        // ---- 17. TMDb ist die einzige Pflicht, SubDL faellt ohne Konfiguration aus der Kette (F-M497) --
        var ohneTmdb = Conf(c => { c.TmdbApiKey = string.Empty; });
        var ohneSubdlKonfig = Conf(c =>
        {
            c.UploadEnabled = true;
            c.DownloadEnabled = true;
            c.Username = string.Empty;
            c.Password = string.Empty;
            c.ApiKey = string.Empty;
        });
        Check("missing TMDb blocks the run, missing SubDL configuration does not (F-M497)",
            ohneTmdb.MissingCredentials().Contains("TMDb API key (v3)")
            && ohneSubdlKonfig.MissingCredentials().Count == 0,
            "ohne Tmdb=" + string.Join(",", ohneTmdb.MissingCredentials())
            + " ohne SubDL=" + string.Join(",", ohneSubdlKonfig.MissingCredentials()));

        Check("SubDL without its credentials steps aside instead of blocking — both directions (F-M497)",
            !ohneSubdlKonfig.IsConfiguredForUpload(SubtitleProvider.Subdl)
            && ohneSubdlKonfig.IsConfiguredForUpload(SubtitleProvider.TvSubtitles)
            && !ohneSubdlKonfig.IsConfiguredForDownload(SubtitleProvider.Subdl)
            && ohneSubdlKonfig.IsConfiguredForDownload(SubtitleProvider.TvSubtitles)
            && uploadSource.Contains(
                "\"[SubDL] {Name} [{Lang}] — source {Source} skipped — not configured (required for SubDL only)\"",
                StringComparison.Ordinal)
            && downloadSource.Contains(
                "if (!_config.IsConfiguredForDownload(Configuration.SubtitleProvider.Subdl))", StringComparison.Ordinal)
            && downloadSource.Contains(
                "\"[SubDL-D] {File} — SubDL skipped — not configured (required for SubDL only)\"", StringComparison.Ordinal));

        // --- Abschnitt 16: Rip-Zuordnung fuer den tvst-Upload (F-M498) ---
        // Gemessen 15.11.2026 im scharfen Lauf: die Seite lehnt jede Folge mit "rip must be one of
        // WEB, HDTV, HEVC, DVDRip, DVDSCR, BDRip, BluRay" ab, weil der Release-Name "WEB-DL" sagt.
        var tvClientSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/TvSubtitlesClient.cs"));
        Check("der Release-Name wird auf einen der sieben Rip-Werte gezogen (F-M498)",
            tvClientSource.Contains("public static string RipFromQuality(string? quality)", StringComparison.Ordinal)
            && tvClientSource.Contains("return \"WEB\";", StringComparison.Ordinal)
            && tvClientSource.Contains("return \"HEVC\";", StringComparison.Ordinal)
            && tvClientSource.Contains("return \"BDRip\";", StringComparison.Ordinal)
            && tvClientSource.Contains("return \"BluRay\";", StringComparison.Ordinal));

        Check("der Endpunkt bekommt den Rip durch die Zuordnung, nicht den rohen N amen (F-M498)",
            uploadSource.Contains("TvSubtitlesClient.RipFromQuality(quality)", StringComparison.Ordinal));

        Check("das Hearing-impaired-Kaestchen wird gesendet, wenn der Untertitel es ist (F-M498)",
            tvClientSource.Contains("new KeyValuePair<string, string>(\"hi\", \"1\")", StringComparison.Ordinal)
            && tvClientSource.Contains("if (hearingImpaired)", StringComparison.Ordinal));

        Check("die Fehlermeldung nennt nicht mehr den falschen Parameter (F-M498)",
            !tvClientSource.Contains("nameof(showId));", StringComparison.Ordinal));

        // --- Abschnitt 17: die Upload-Queue fragt je Quelle, nicht quellenblind (F-M499) ---
        // Gemessen 15.11.2026, 16:04: nach 27 SubDL-Uploads reihte der Seeder 0 Folgen ein
        // ("scan finished (upload) — 0 upload queued"), weil "schon hochgeladen" aus Status=Uploaded
        // gelesen wurde — also "irgendwo hochgeladen". Der zweite Endpunkt bekam nie seinen Versuch.
        Check("die Upload-Queue fragt je Quelle statt quellenblind (F-M499)",
            seederSource.Contains("UploadTodoFor(item, mediaPath, config, quelle)", StringComparison.Ordinal)
            && seederSource.Contains("registry.ArePositionsTerminalFor(mediaHash", StringComparison.Ordinal)
            && seederSource.Contains("registry.CountKnownPairsFor(mediaHash, quelle)", StringComparison.Ordinal)
            && seederSource.Contains("registry.IsSidecarUploadedTo(looseHash, quelle)", StringComparison.Ordinal)
            && !seederSource.Contains("registry.ArePositionsTerminal(mediaHash", StringComparison.Ordinal)
            && !seederSource.Contains("registry.CountKnownPairs(mediaHash)", StringComparison.Ordinal)
            && !seederSource.Contains("registry.IsSidecarUploaded(looseHash)", StringComparison.Ordinal));

        Check("der Helfer nimmt nur eingeschaltete UND konfigurierte Quellen, und die Marke liest EINE Regel (F-M499)",
            seederSource.Contains("foreach (var quelle in UploadSources(config))", StringComparison.Ordinal)
            && seederSource.Contains(
                "if (config.IsUploadEnabled(quelle) && config.IsConfiguredForUpload(quelle))",
                StringComparison.Ordinal)
            && registrySource.Contains(
                "public bool ArePositionsTerminalFor(string? mediaHash, IEnumerable<int> positions, SubtitleProvider provider)",
                StringComparison.Ordinal)
            && registrySource.Contains(
                "public int CountKnownPairsFor(string? mediaHash, SubtitleProvider provider)",
                StringComparison.Ordinal)
            && registrySource.Contains(
                "public bool IsSidecarUploadedTo(string? contentHash, SubtitleProvider provider)",
                StringComparison.Ordinal)
            && registrySource.Contains(
                "return IsUploadedTo(mediaHash, track.Language, track.HearingImpaired ?? false, provider);",
                StringComparison.Ordinal));

        // --- Abschnitt 18: auch der KOPF des Laufs fragt je Quelle (F-M499) ---------------------
        // Gemessen 15.11.2026, 16:17 im scharfen Lauf: der Seeder hatte nach Abschnitt 17 27 Folgen
        // quellenweise eingereiht, und die Pipeline warf sie alle mit "[SubDL] SKIP {File} — upload
        // file-complete" wieder weg — die Dateimarke stand von den SubDL-Uploads. Ergebnis "0 uploaded".
        Check("die Pipeline ueberspringt nur, wenn ALLE eingeschalteten und konfigurierten Quellen fertig sind (F-M499)",
            uploadSource.Contains("Registry.IsSubtitlesUploadedFor(earlyHash, quelle)", StringComparison.Ordinal)
            && uploadSource.Contains("if (quellen.Count > 0 && offeneQuellen.Count == 0)", StringComparison.Ordinal)
            && uploadSource.Contains(
                "private List<Configuration.SubtitleProvider> UploadSources() =>", StringComparison.Ordinal)
            && uploadSource.Contains(
                "_config.DryRun || _config.IsConfiguredForUpload(quelle)", StringComparison.Ordinal)
            // Die blinde Frage darf im Kopf des Laufs nicht mehr stehen — sie war der Defekt.
            && !uploadSource.Contains("IsSubtitlesUploaded(", StringComparison.Ordinal));

        Check("das quellenweise Gegenstueck der Dateimarke existiert und filtert auf die Quelle (F-M499)",
            registrySource.Contains(
                "public bool IsSubtitlesUploadedFor(string? mediaHash, SubtitleProvider provider)",
                StringComparison.Ordinal)
            && registrySource.Contains(
                "tracks.All(track => IsEmbedTerminalFor(mediaHash, track.SubPos, provider))",
                StringComparison.Ordinal)
            // dieselbe Leseregel wie im Upload selbst: eigene Marke der Quelle, Ablehnung quellenneutral
            && registrySource.Contains(
                "return IsUploadedTo(mediaHash, track.Language, track.HearingImpaired ?? false, provider);",
                StringComparison.Ordinal));

        // --- Abschnitt 19: auch das SETTLE-Urteil fragt je Quelle (F-M500) ----------------------
        // Gemessen 15.11.2026, 16:24 im scharfen Lauf: 27 eingebettete FR-Spuren endeten als
        // "identical subtitle already uploaded earlier (F-M298)", weil der Inhalt bei SubDL lag —
        // tvsubtitles bekam nie einen Versuch, der Lauf meldete "0 uploaded | 34 rejected". Das Urteil
        // "schon hochgeladen" las Status=Uploaded und damit "irgendwo hochgeladen".
        Check("das Settle-Urteil nimmt die Quelle mit — vier Stellen, EINE Regel (F-M500)",
            registrySource.Contains("public bool IsContentUploadedFor(string? contentHash, SubtitleProvider provider)", StringComparison.Ordinal)
            && registrySource.Contains("public bool RecordSkippedContent(string? mediaHash, bool isLoose, int subPos, string lang,", StringComparison.Ordinal)
            && registrySource.Contains("IEnumerable<SubtitleProvider> settledSources,", StringComparison.Ordinal)
            && registrySource.Contains("Where(quelle => IsContentUploadedFor(contentHash, quelle))", StringComparison.Ordinal)
            && uploadSource.Contains("private (ContentSettle Verdict, string Open) SettleDuplicateContent(", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.Matches(uploadSource, @"SettleDuplicateContent\(").Count >= 5
            // die Zeile wird nur noch aus der EINEN Regel heraus geschrieben (zwei Aufrufe: Ablehnung/Settle)
            && System.Text.RegularExpressions.Regex.Matches(uploadSource, @"Registry\.RecordSkippedContent\(").Count == 2);

        Check("nur der Quellenbeleg schreibt die Marke — tvst zaehlt keine markenlose Zeile (F-M500)",
            registrySource.Contains("settledAsUploaded && quellen.Contains(SubtitleProvider.Subdl) ? DateTime.UtcNow : null", StringComparison.Ordinal)
            && registrySource.Contains("settledAsUploaded && quellen.Contains(SubtitleProvider.TvSubtitles) ? DateTime.UtcNow : null", StringComparison.Ordinal)
            // fuer tvsubtitles NUR seine eigene Marke: die markenlose Alt-/Pack-Zeile zaehlt nur bei SubDL
            && System.Text.RegularExpressions.Regex.Matches(registrySource, @"UploadedWithoutSourceMark\(x\)").Count == 2);

        // Der Umbau wird nicht nur gelesen, sondern gefahren: ein Inhalt bei SubDL darf tvst nicht
        // erledigen, zweimal bei derselben Quelle bleibt blockiert, und die Herkunft bleibt das Tor.
        string settleRoot = Path.Combine(Path.GetTempPath(), "endpoints-settle-" + Environment.ProcessId);
        try
        {
            if (Directory.Exists(settleRoot))
            {
                Directory.Delete(settleRoot, true);
            }

            Directory.CreateDirectory(settleRoot);
            using (var settleDb = new Jellyfin.Plugin.SubdlScribe.Data.SubdlDbContext(Path.Combine(settleRoot, "db"), null))
            {
                var settleReg = new Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry(settleDb, null, null);

                const string fileA = "aaaa1111bbbb2222";
                const string fileB = "cccc3333dddd4444";
                const string de = "DE";
                string textDe = "1\n00:00:01,000 --> 00:00:03,000\nEin Satz.\n\n";
                string hashDe = Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry.ComputeHash(textDe);

                // Der Inhalt liegt bei SubDL (Marke der Quelle, F-M490), unter ANDERER Dateiidentitaet.
                settleReg.EnsureMedia(fileB, "tt7654321", "/data/movies2/film.mkv");
                settleReg.MarkEmbed(fileB, 0, de, false, Jellyfin.Plugin.SubdlScribe.Data.SubtitleStatus.Uploaded,
                    contentHash: hashDe, subdlUploadedAt: DateTime.UtcNow);
                settleReg.EnsureMedia(fileA, "tt1234567", "/data/movies2/folge.mkv");

                Check("derselbe Inhalt bei SubDL erledigt tvsubtitles NICHT (F-M500)",
                    settleReg.IsContentUploadedFor(hashDe, SubtitleProvider.Subdl)
                    && !settleReg.IsContentUploadedFor(hashDe, SubtitleProvider.TvSubtitles)
                    && settleReg.IsContentUploadedForAnotherSource(hashDe, SubtitleProvider.TvSubtitles),
                    "subdl=" + settleReg.IsContentUploadedFor(hashDe, SubtitleProvider.Subdl)
                    + " tvst=" + settleReg.IsContentUploadedFor(hashDe, SubtitleProvider.TvSubtitles));

                // Zweimal bei DERSELBEN Quelle bleibt blockiert: der zweite SubDL-Versuch wird wieder
                // als "schon hochgeladen" verbucht und schreibt KEINE Ablehnung.
                bool settleSubdl = settleReg.RecordSkippedContent(fileA, false, 7, de, false, false, hashDe,
                    Jellyfin.Plugin.SubdlScribe.Data.RejectReason.DuplicateContent,
                    new[] { SubtitleProvider.Subdl });
                bool settleTwice = settleReg.RecordSkippedContent(fileA, false, 7, de, false, false, hashDe,
                    Jellyfin.Plugin.SubdlScribe.Data.RejectReason.DuplicateContent,
                    new[] { SubtitleProvider.Subdl });
                var rowA = settleReg.GetEmbed(fileA, 7);
                Check("zweimal bei derselben Quelle bleibt blockiert — ohne Ablehnung (F-M500)",
                    settleSubdl && settleTwice
                    && rowA?.Status == Jellyfin.Plugin.SubdlScribe.Data.SubtitleStatus.Uploaded
                    && rowA?.Reason is null
                    && settleReg.IsUploadedTo(fileA, de, false, SubtitleProvider.Subdl)
                    // die andere Quelle ist auf DIESER Spur weiterhin offen
                    && !settleReg.IsUploadedTo(fileA, de, false, SubtitleProvider.TvSubtitles),
                    "settled=" + settleSubdl + "/" + settleTwice + " status=" + rowA?.Status + " grund=" + (rowA?.Reason ?? "-"));

                // Ohne Quellenbeleg (nur eine Ablehnung im Bestand) bleibt es bei der Ablehnung, und die
                // ist quellenneutral: sie haelt BEIDE Quellen — wie festgelegt (F-M499).
                string textFr = "1\n00:00:04,000 --> 00:00:06,000\nUne phrase.\n\n";
                string hashFr = Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry.ComputeHash(textFr);
                settleReg.MarkEmbed(fileA, 8, "FR", false, Jellyfin.Plugin.SubdlScribe.Data.SubtitleStatus.Rejected,
                    contentHash: hashFr, reason: Jellyfin.Plugin.SubdlScribe.Data.RejectReason.DuplicateRemote);
                bool noEvidence = settleReg.RecordSkippedContent(fileA, false, 8, "FR", false, true, hashFr,
                    Jellyfin.Plugin.SubdlScribe.Data.RejectReason.DuplicateContent,
                    Array.Empty<SubtitleProvider>());
                var rowFr = settleReg.GetEmbed(fileA, 8);
                Check("ohne Quellenbeleg bleibt die Ablehnung quellenneutral (F-M499/F-M500)",
                    !noEvidence
                    && rowFr?.Status == Jellyfin.Plugin.SubdlScribe.Data.SubtitleStatus.Rejected
                    && rowFr?.Reason == Jellyfin.Plugin.SubdlScribe.Data.RejectReason.DuplicateContent);

                // Das Herkunftstor steht VOR diesem Weg und wird hier nicht weicher. Drei Faelle, wie
                // festgelegt (Operator 15.11.2026: "externe Quellen duerfen zu beiden hochgeladen werden"):
                // unbekannt/extern -> beide, subdl -> nur SubDL, tvsubtitles -> nur tvsubtitles.
                var torUnbekannt = UploadGate.Decide(true, true, origin: null, false, false);
                var markenText = "1\n00:00:20,000 --> 00:00:22,000\nZwei Marken.\n\n";
                string markenHash = Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry.ComputeHash(markenText);
                settleReg.EnsureMedia("eeee5555ffff6666", "tt9999999", "/data/movies2/beide.mkv");
                settleReg.MarkEmbed("eeee5555ffff6666", 0, de, false,
                    Jellyfin.Plugin.SubdlScribe.Data.SubtitleStatus.Uploaded, contentHash: markenHash,
                    subdlUploadId: "sd-1", subdlUploadedAt: DateTime.UtcNow);
                settleReg.MarkEmbed("eeee5555ffff6666", 0, de, false,
                    Jellyfin.Plugin.SubdlScribe.Data.SubtitleStatus.Uploaded, contentHash: markenHash,
                    tvSubtitlesUploadId: "tv-1", tvSubtitlesUploadedAt: DateTime.UtcNow);
                var beideRow = settleReg.GetEmbed("eeee5555ffff6666", 0);
                Check("unbekannte/externe Herkunft: BEIDE Quellen duerfen hochladen, jede mit eigener Marke (F-M487/F-M500)",
                    torUnbekannt.Subdl && torUnbekannt.TvSubtitles && torUnbekannt.SkipReason is null
                    && Jellyfin.Plugin.SubdlScribe.Api.Endpoints.Provenance.MayUploadTo(null, SubtitleProvider.Subdl)
                    && Jellyfin.Plugin.SubdlScribe.Api.Endpoints.Provenance.MayUploadTo(null, SubtitleProvider.TvSubtitles)
                    // beide Marken stehen nebeneinander auf DERSELBEN Zeile — die Kette schreibt je Quelle
                    && beideRow?.SubdlUploadId == "sd-1" && beideRow?.TvSubtitlesUploadId == "tv-1"
                    && settleReg.IsUploadedTo("eeee5555ffff6666", de, false, SubtitleProvider.Subdl)
                    && settleReg.IsUploadedTo("eeee5555ffff6666", de, false, SubtitleProvider.TvSubtitles)
                    && uploadSource.Contains("foreach (var quelle in quellenReihenfolge)", StringComparison.Ordinal),
                    "subdl=" + torUnbekannt.Subdl + " tvst=" + torUnbekannt.TvSubtitles);

                // Gesperrte Herkuenfte: ein Download ist ein Fund, kein eigener Kandidat — er geht NIRGENDS
                // hoch (Operator 15.11.2026: "Herkunft subdl: nie zurueck zu subdl, nie zu tvst").
                string textHer = "1\n00:00:09,000 --> 00:00:11,000\nHerkunft.\n\n";
                string hashHer = Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry.ComputeHash(textHer);
                settleReg.MarkDownloaded(hashHer, fileA, de, false,
                    origin: Jellyfin.Plugin.SubdlScribe.Api.Endpoints.Provenance.Name(SubtitleProvider.Subdl),
                    fileName: "folge.de.srt", path: "/data/movies2/folge.de.srt");
                string? herkunft = settleReg.GetOrigin(hashHer).Origin;
                var torSubdl = UploadGate.Decide(true, true, herkunft, false, false);
                Check("Herkunft=subdl: weder zu SubDL (kein Reup) noch zu tvsubtitles (F-M487 verschaerft)",
                    herkunft == "subdl"
                    && !torSubdl.Subdl && !torSubdl.TvSubtitles
                    && torSubdl.SkipReason == "origin:subdl — downloaded there, never uploaded"
                    && !Jellyfin.Plugin.SubdlScribe.Api.Endpoints.Provenance.MayUploadTo(herkunft, SubtitleProvider.Subdl)
                    && !Jellyfin.Plugin.SubdlScribe.Api.Endpoints.Provenance.MayUploadTo(herkunft, SubtitleProvider.TvSubtitles)
                    && !settleReg.IsContentUploadedFor(hashHer, SubtitleProvider.TvSubtitles)
                    // der Settle-Weg fragt genau dieses Tor, bevor er einen Kandidaten weiterlaufen laesst
                    && uploadSource.Contains("Api.Endpoints.Provenance.MayUploadTo(herkunft, quelle)", StringComparison.Ordinal)
                    && uploadSource.Contains("string? herkunft = Registry.GetOrigin(contentHash).Origin;", StringComparison.Ordinal),
                    "herkunft=" + (herkunft ?? "-") + " subdl=" + torSubdl.Subdl + " tvst=" + torSubdl.TvSubtitles);

                string textHer2 = "1\n00:00:12,000 --> 00:00:14,000\nAndere Herkunft.\n\n";
                string hashHer2 = Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry.ComputeHash(textHer2);
                settleReg.MarkDownloaded(hashHer2, fileA, de, false,
                    origin: Jellyfin.Plugin.SubdlScribe.Api.Endpoints.Provenance.Name(SubtitleProvider.TvSubtitles),
                    fileName: "folge2.de.srt", path: "/data/movies2/folge2.de.srt");
                string? herkunftTv = settleReg.GetOrigin(hashHer2).Origin;
                var torTv = UploadGate.Decide(true, true, herkunftTv, false, false);
                Check("Herkunft=tvsubtitles: weder zu tvsubtitles (kein Reup) noch zu SubDL (F-M487 verschaerft)",
                    herkunftTv == "tvsubtitles"
                    && !torTv.Subdl && !torTv.TvSubtitles
                    && torTv.SkipReason == "origin:tvsubtitles — downloaded there, never uploaded"
                    && !Jellyfin.Plugin.SubdlScribe.Api.Endpoints.Provenance.MayUploadTo(herkunftTv, SubtitleProvider.Subdl)
                    && !Jellyfin.Plugin.SubdlScribe.Api.Endpoints.Provenance.MayUploadTo(herkunftTv, SubtitleProvider.TvSubtitles)
                    && !settleReg.IsContentUploadedFor(hashHer2, SubtitleProvider.Subdl)
                    && !settleReg.IsContentUploadedFor(hashHer2, SubtitleProvider.TvSubtitles),
                    "herkunft=" + (herkunftTv ?? "-") + " subdl=" + torTv.Subdl + " tvst=" + torTv.TvSubtitles);

                // Ein bekannter Fund ist keine Upload-Arbeit mehr: die Spur ist fuer JEDE Quelle erledigt,
                // sonst reihte der Seeder sie in jedem Lauf erneut ein (Operator: nie zurueck, nie hinueber).
                settleReg.MarkEmbed(fileA, 9, "IT", false,
                    Jellyfin.Plugin.SubdlScribe.Data.SubtitleStatus.Observed, contentHash: hashHer);
                string textFrei = "1\n00:00:30,000 --> 00:00:32,000\nOhne Herkunft.\n\n";
                string hashFrei = Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry.ComputeHash(textFrei);
                settleReg.MarkEmbed(fileA, 10, "IT", false,
                    Jellyfin.Plugin.SubdlScribe.Data.SubtitleStatus.Observed, contentHash: hashFrei);
                Check("ein Download ist fuer BEIDE Quellen erledigt — die Spur bleibt nicht ewig offen (F-M500)",
                    settleReg.IsEmbedTerminalFor(fileA, 9, SubtitleProvider.Subdl)
                    && settleReg.IsEmbedTerminalFor(fileA, 9, SubtitleProvider.TvSubtitles)
                    // Negativkontrolle: ohne bekannte Herkunft ist die Spur NICHT terminal — die Regel greift nur dort
                    && !settleReg.IsEmbedTerminalFor(fileA, 10, SubtitleProvider.Subdl)
                    && !settleReg.IsEmbedTerminalFor(fileA, 10, SubtitleProvider.TvSubtitles)
                    && seederSource.Contains("if (registry.GetOrigin(looseHash).Origin is not null)", StringComparison.Ordinal));

                // Die Queue fragt dieselbe Sache je Quelle (F-M500 im Seeder): ein Upload bei der anderen
                // Quelle haelt die zweite nicht mehr aus der Queue.
                Check("die Queue liest \"bekannt\" nicht mehr quellenblind (F-M500)",
                    seederSource.Contains("registry.IsContentUploadedForAnotherSource(contentHashProbe, quelle)", StringComparison.Ordinal)
                    && !seederSource.Contains("if (registry.IsContentKnown(Registry.ContentHashRegistry.ComputeHash(content)))", StringComparison.Ordinal));
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(settleRoot))
                {
                    Directory.Delete(settleRoot, true);
                }
            }
            catch (IOException)
            {
                // liegen gebliebene Datei: kein Grund, die Suite rot zu machen
            }
        }

        // --- Abschnitt 20: die tvst-Seriennummer im Store und die Anlage fehlender Serien (F-M501) ---
        // Operator-Anweisung 15.11.2026: "Tmdb titel fuehrt.", "Tmdb titel fuehrt auch fuer
        // Doppeleintraege.", nachgeschaerft "Doch Aehnlichkeitssuche. Aber gross/Kleinschreibung. And =
        // und. Leerzeichen mehr oder weniger. Aehnlichkeit eng halten". Also: Name normalisieren und
        // dann auf GLEICHHEIT vergleichen — kein Teilstring, kein Aehnlichkeitsmass, kein
        // Artikel-Abschneiden.
        var namen = new Dictionary<int, string>
        {
            [512] = "The Middle",
            [422] = "Malcolm in the Middle",
            [701] = "The Middle Man",
            [700] = "Rick & Morty",
        };
        Check("tvst vergleicht eng: Gross/Kleinschreibung und Leerraum zaehlen nicht (F-M501)",
            TvSubtitlesClient.ResolveShow(namen, "the middle") == 512
            && TvSubtitlesClient.ResolveShow(namen, "  The   Middle ") == 512
            && TvSubtitlesClient.NormalizeShowName("The   Middle") == "the middle",
            "the middle=" + TvSubtitlesClient.ResolveShow(namen, "the middle")
            + " doppelraum=" + TvSubtitlesClient.ResolveShow(namen, "  The   Middle "));

        Check("tvst vergleicht eng: & = and = und (F-M501)",
            TvSubtitlesClient.ResolveShow(namen, "Rick and Morty") == 700
            && TvSubtitlesClient.ResolveShow(namen, "Rick und Morty") == 700
            && TvSubtitlesClient.ResolveShow(namen, "rick&morty") == 700
            // "und" nur als Wort: aus "Rundschau" darf kein "Randschau" werden
            && TvSubtitlesClient.NormalizeShowName("Rundschau") == "rundschau",
            "and=" + TvSubtitlesClient.ResolveShow(namen, "Rick and Morty"));

        Check("tvst vergleicht eng: kein Teilstring, kein Artikel-Abschneiden (F-M501)",
            // "The Middle" findet NICHT "The Middle Man" und NICHT "Malcolm in the Middle"
            TvSubtitlesClient.ResolveShow(namen, "The Middle") == 512
            // die fruehere "enthaelt"-Stufe haette hier 701 geliefert
            && TvSubtitlesClient.ResolveShow(namen, "Middle") is null
            && TvSubtitlesClient.ResolveShow(namen, "Middle Man") is null
            && TvSubtitlesClient.ResolveShow(namen, "the middle man") == 701,
            "Middle=" + (TvSubtitlesClient.ResolveShow(namen, "Middle")?.ToString() ?? "-"));

        // Der TMDb-Serien-Datensatz, auf dem die Aufloesung beruht (Titel, Jahr, Staffelzahl).
        var tmdbInfo = await new TmdbImdbResolver(
                new HttpClient(new JsonHandler(
                    "{\"name\":\"The Middle\",\"original_name\":\"The Middle\"," +
                    "\"first_air_date\":\"2009-09-30\",\"number_of_seasons\":9}")),
                "test-key")
            .SeriesInfoAsync("999001", CancellationToken.None).ConfigureAwait(false);
        Check("der TMDb-Serien-Datensatz traegt Titel, Erstausstrahlungsjahr und Staffelzahl (F-M501)",
            tmdbInfo is { Name: "The Middle", FirstAirYear: 2009, Seasons: 9 },
            tmdbInfo is null ? "kein Datensatz" : tmdbInfo.Name + "/" + tmdbInfo.FirstAirYear + "/" + tmdbInfo.Seasons);

        string tvstRoot = Path.Combine(Path.GetTempPath(), "endpoints-tvst-" + Environment.ProcessId);
        try
        {
            if (Directory.Exists(tvstRoot))
            {
                Directory.Delete(tvstRoot, true);
            }

            Directory.CreateDirectory(tvstRoot);
            using (var tvstDb = new Jellyfin.Plugin.SubdlScribe.Data.SubdlDbContext(Path.Combine(tvstRoot, "db"), null))
            {
                var tvstReg = new Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry(tvstDb, null, null);
                var mitte = new TmdbSeriesInfo("The Middle", "The Middle", 2009, 9);

                // (a) Die gespeicherte Nummer fuehrt: kein Netz, kein Vergleich, keine Anlage.
                tvstReg.EnsureMedia("aaaa000000000001", patch: m => m.TvstShowId = "512");
                var handlerGespeichert = new TvstSiteHandler(512, "The Middle");
                var aufloeserGespeichert = new TvstShowResolver(
                    new TvSubtitlesClient(new HttpClient(handlerGespeichert)));
                var gespeichert = await aufloeserGespeichert
                    .ResolveForMediaAsync(tvstReg, "aaaa000000000001", mitte, dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("eine gespeicherte TvstShowId verhindert Suche UND Anlage (F-M501)",
                    gespeichert.Resolved && gespeichert.ShowId == 512
                    && gespeichert.Status == TvstShowStatus.Stored
                    && handlerGespeichert.Gets == 0 && handlerGespeichert.Posts == 0,
                    "id=" + gespeichert.ShowId + " status=" + gespeichert.Status
                    + " gets=" + handlerGespeichert.Gets + " posts=" + handlerGespeichert.Posts);

                // (b) Ein Treffer in der Liste: die Nummer wird geschrieben und benutzt, ohne Anlage.
                var handlerVorhanden = new TvstSiteHandler(512, "The Middle");
                var aufloeserVorhanden = new TvstShowResolver(
                    new TvSubtitlesClient(new HttpClient(handlerVorhanden)));
                var vorhanden = await aufloeserVorhanden
                    .ResolveForMediaAsync(tvstReg, "aaaa000000000002", mitte, dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("der TMDb-Titel findet die Serie eng verglichen und schreibt die Nummer ins Medium (F-M501)",
                    vorhanden.Resolved && vorhanden.ShowId == 512 && vorhanden.Status == TvstShowStatus.Found
                    && tvstReg.GetMedia("aaaa000000000002")?.TvstShowId == "512"
                    && handlerVorhanden.Posts == 0 && handlerVorhanden.Gets == 1,
                    "status=" + vorhanden.Status + " store=" + (tvstReg.GetMedia("aaaa000000000002")?.TvstShowId ?? "-"));

                // (c) Kein Treffer: genau EINE Anlage, danach wird erneut gesucht und erst dann geschrieben.
                var neu = new TmdbSeriesInfo("Beispielserie Test", "Beispielserie Test", 2026, 3);
                var handlerNeu = new TvstSiteHandler(512, "The Middle") { AddAfterPost = (9001, "Beispielserie Test") };
                var aufloeserNeu = new TvstShowResolver(new TvSubtitlesClient(new HttpClient(handlerNeu)));
                var angelegt = await aufloeserNeu
                    .ResolveForMediaAsync(tvstReg, "aaaa000000000003", neu, dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("ohne Treffer wird genau einmal angelegt und danach erneut gesucht (F-M501)",
                    angelegt.Resolved && angelegt.ShowId == 9001 && angelegt.Status == TvstShowStatus.Created
                    && handlerNeu.Posts == 1 && handlerNeu.Gets == 2
                    && tvstReg.GetMedia("aaaa000000000003")?.TvstShowId == "9001",
                    "status=" + angelegt.Status + " posts=" + handlerNeu.Posts + " gets=" + handlerNeu.Gets);

                Check("die Anlage traegt Name, Jahr und Staffelzahl aus TMDb (F-M501)",
                    handlerNeu.LastBody is string rumpf
                    && rumpf.Contains("name=\"name\"\r\n\r\nBeispielserie Test", StringComparison.Ordinal)
                    && rumpf.Contains("name=\"year_s\"\r\n\r\n2026", StringComparison.Ordinal)
                    && rumpf.Contains("name=\"seasons\"\r\n\r\n3", StringComparison.Ordinal),
                    handlerNeu.LastBody is null ? "kein Rumpf" : "Rumpf ohne die drei Felder");

                // Staffelzahl: die Seite kennt nur 1..22 — 30 wird auf 22 gezogen, "keine Angabe" auf 1.
                var handlerViele = new TvstSiteHandler(512, "The Middle") { AddAfterPost = (9002, "Viele Staffeln") };
                var aufloeserViele = new TvstShowResolver(new TvSubtitlesClient(new HttpClient(handlerViele)));
                await aufloeserViele.ResolveForMediaAsync(
                        tvstReg, "aaaa000000000004", new TmdbSeriesInfo("Viele Staffeln", null, 2001, 30),
                        dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                var handlerOhne = new TvstSiteHandler(512, "The Middle") { AddAfterPost = (9003, "Ohne Angabe") };
                var aufloeserOhne = new TvstShowResolver(new TvSubtitlesClient(new HttpClient(handlerOhne)));
                await aufloeserOhne.ResolveForMediaAsync(
                        tvstReg, "aaaa000000000005", new TmdbSeriesInfo("Ohne Angabe", null, 2001, null),
                        dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("die Staffelzahl wird auf 1..22 begrenzt, und BuildAddShow weist 23 ab (F-M501)",
                    handlerViele.LastBody!.Contains("name=\"seasons\"\r\n\r\n22", StringComparison.Ordinal)
                    && handlerOhne.LastBody!.Contains("name=\"seasons\"\r\n\r\n1", StringComparison.Ordinal)
                    && TvSubtitlesClient.MaxSeason == 22
                    && Throws(() => TvSubtitlesClient.BuildAddShow("XY", 2026, 23))
                    && !Throws(() => TvSubtitlesClient.BuildAddShow("XY", 2026, 22)),
                    "30->" + (handlerViele.LastBody!.Contains("seasons\"\r\n\r\n22", StringComparison.Ordinal) ? "22" : "?"));

                // (d) Die Anlage scheitert: nichts wird geschrieben, die Kette laeuft weiter (Skipped, kein Wurf).
                var handlerKaputt = new TvstSiteHandler(512, "The Middle") { PostStatus = 500 };
                var aufloeserKaputt = new TvstShowResolver(new TvSubtitlesClient(new HttpClient(handlerKaputt)));
                var kaputt = await aufloeserKaputt
                    .ResolveForMediaAsync(
                        tvstReg, "aaaa000000000006", new TmdbSeriesInfo("Beispielserie Test", null, 2026, 3),
                        dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("eine gescheiterte Anlage blockiert die Kette nicht und schreibt nichts (F-M501)",
                    !kaputt.Resolved && kaputt.Status == TvstShowStatus.Unclear
                    && tvstReg.GetMedia("aaaa000000000006")?.TvstShowId is null
                    && handlerKaputt.Posts == 1
                    // die Kette antwortet mit Skipped (die andere Quelle bekommt ihren Versuch), nicht mit Failed
                    && uploadSource.Contains("return UploadResult.Skipped(\"second-source-show-\" + show.Status)", StringComparison.Ordinal),
                    "status=" + kaputt.Status + " posts=" + handlerKaputt.Posts);

                // Anlegen hoechstens einmal je Medium und Lauf: derselbe Fehlschlag wird NICHT wiederholt.
                var zweiterVersuch = await aufloeserKaputt
                    .ResolveForMediaAsync(
                        tvstReg, "aaaa000000000006", new TmdbSeriesInfo("Beispielserie Test", null, 2026, 3),
                        dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("ein Fehlschlag legt nicht bei jeder Folge erneut an (F-M501)",
                    !zweiterVersuch.Resolved
                    && zweiterVersuch.Status == TvstShowStatus.CreateAlreadyTried
                    && handlerKaputt.Posts == 1,
                    "status=" + zweiterVersuch.Status + " posts=" + handlerKaputt.Posts);

                // (e) Trockenlauf: nichts anlegen, nichts schreiben.
                var handlerTrocken = new TvstSiteHandler(512, "The Middle");
                var aufloeserTrocken = new TvstShowResolver(new TvSubtitlesClient(new HttpClient(handlerTrocken)));
                var trocken = await aufloeserTrocken
                    .ResolveForMediaAsync(
                        tvstReg, "aaaa000000000007", new TmdbSeriesInfo("Beispielserie Test", null, 2026, 3),
                        dryRun: true, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("der Trockenlauf legt nichts an und schreibt nichts (F-M501)",
                    !trocken.Resolved && trocken.Status == TvstShowStatus.WouldCreate
                    && handlerTrocken.Posts == 0
                    && tvstReg.GetMedia("aaaa000000000007")?.TvstShowId is null,
                    "status=" + trocken.Status + " posts=" + handlerTrocken.Posts);

                // (f) Unklare Seite: einmal nachfragen, dann NICHT schreiben und tvst ueberspringen.
                var handlerLeer = new TvstSiteHandler(512, "The Middle") { EmptyList = true };
                var aufloeserLeer = new TvstShowResolver(new TvSubtitlesClient(new HttpClient(handlerLeer)));
                var leer = await aufloeserLeer
                    .ResolveForMediaAsync(
                        tvstReg, "aaaa000000000008", new TmdbSeriesInfo("Beispielserie Test", null, 2026, 3),
                        dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("eine unklare Serienliste wird einmal nachgefragt, dann wird nicht geschrieben (F-M501)",
                    !leer.Resolved && leer.Status == TvstShowStatus.Unclear
                    && handlerLeer.Gets == 2 && handlerLeer.Posts == 0
                    && tvstReg.GetMedia("aaaa000000000008")?.TvstShowId is null,
                    "status=" + leer.Status + " gets=" + handlerLeer.Gets);

                // (g) Der Endpunkt mit Nummer im Ticket: die Serienliste wird gar nicht erst geholt.
                var handlerEndpunkt = new TvstSiteHandler(512, "The Middle");
                var endpunkt = new TvSubtitlesUploadEndpoint(
                    new TvSubtitlesClient(new HttpClient(handlerEndpunkt)), dryRun: false);
                var antwort = await endpunkt.SendAsync(
                        new UploadTicket(
                            "Ein Name, den die Seite NICHT fuehrt", "tt1442464", "1422", 2, 24, "de",
                            "folge.srt", Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:03,000\nSatz.\n"),
                            "WEB", "The.Middle.S02E24", false, false, false, TvstShowId: 512),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                // Die EINE Serienliste danach ist die Ruecklesung der eigenen Upload-Id (F-M488) — sie
                // kommt NACH dem Upload. Vor dem Upload wird nicht gesucht: die Nummer steht im Ticket,
                // obwohl der Name im Ticket eine Schreibweise ist, die die Seite nicht fuehrt.
                Check("mit TvstShowId im Ticket sucht der Endpunkt vor dem Upload nicht (F-M501)",
                    antwort.Ok && handlerEndpunkt.Uploads == 1
                    && handlerEndpunkt.Order.FirstOrDefault() == "UPLOAD"
                    && handlerEndpunkt.Order.Count(x => x == "UPLOAD") == 1
                    && handlerEndpunkt.LastUploadBody!.Contains("tvshow\"\r\n\r\n512", StringComparison.Ordinal),
                    "ok=" + antwort.Ok + " folge=" + string.Join(",", handlerEndpunkt.Order));

                // Die Kette selbst: kein Dateiname mehr als Serienname, die Nummer geht ins Ticket.
                Check("die Upload-Kette loest ueber TMDb und den Store auf, nicht ueber den Dateinamen (F-M501)",
                    uploadSource.Contains("var series = await _tmdb.SeriesInfoAsync(tmdbId!, ct)", StringComparison.Ordinal)
                    && uploadSource.Contains(
                        ".ResolveForMediaAsync(Registry, mediaHash, series, _config.DryRun, ct)", StringComparison.Ordinal)
                    && uploadSource.Contains("TvstShowId: show.ShowId", StringComparison.Ordinal)
                    && uploadSource.Contains("_tvstShows.BeginRun()", StringComparison.Ordinal)
                    // der alte Weg (Serienname aus dem Dateinamen) steht im tvst-Zweig nicht mehr
                    && !uploadSource.Contains(
                        "var parsed = Pipeline.MediaNameParser.Parse(Path.GetFileNameWithoutExtension(releaseName))",
                        StringComparison.Ordinal));

            // (h) Die Migration selbst: eine Datei aus Schema 12 (media ohne TvstShowId) wird beim
            // Oeffnen um die Spalte erweitert, der Bestand bleibt lesbar.
            string legacyDir = Path.Combine(tvstRoot, "legacy");
            Directory.CreateDirectory(legacyDir);
            string legacyFile = Path.Combine(legacyDir, "subdl-scribe.sqlite");
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + legacyFile))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE meta (Id TEXT PRIMARY KEY, SchemaVersion INTEGER, PluginVersion TEXT, "
                    + "JellyfinVersion TEXT, Updated TEXT);"
                    + "INSERT INTO meta VALUES ('db', 12, '12.1.12.389', '10.11.0', '2026-11-15');"
                    + "CREATE TABLE media (Id TEXT PRIMARY KEY, Path TEXT, JellyfinItemId TEXT, ImdbId TEXT, "
                    + "TmdbId TEXT, SdId TEXT, IsSeries INTEGER, Season INTEGER, Episode INTEGER, "
                    + "LanguagesAvailable TEXT, SubtitlesUploadedAt TEXT, LastSearchUtc TEXT, "
                    + "LastSearchLanguages TEXT, LastSeen TEXT);"
                    // Jede Spalte, die das Modell NICHT nullen kann, bekommt einen Wert — sonst
                    // scheitert schon das Lesen des Bestands, und die Pruefung misse die Migration.
                    + "INSERT INTO media (Id, Path, JellyfinItemId, ImdbId, TmdbId, SdId, IsSeries, Season, "
                    + "Episode, LanguagesAvailable, SubtitlesUploadedAt, LastSearchUtc, LastSearchLanguages, "
                    + "LastSeen) VALUES ('aaaa000000000009', '/data/serien/folge.mkv', 'jf-1', 'tt1442464', "
                    + "'1422', NULL, 1, 2, 24, 'DE', NULL, NULL, NULL, '2026-11-15T00:00:00');";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using (var legacyDb = new Jellyfin.Plugin.SubdlScribe.Data.SubdlDbContext(legacyDir, null))
            {
                var bestand = legacyDb.Media.FindById("aaaa000000000009");
                // Der Wert wird VOR dem Schreiben festgehalten: EF haelt eine Zeile im Kontext und
                // liefert beim zweiten Lesen DIESELBE Instanz — die nach dem Patch "512" traegt.
                string? vorherTvst = bestand?.TvstShowId;
                legacyDb.EnsureMedia("aaaa000000000009", patch: m => m.TvstShowId = "512");
                var nachher = legacyDb.Media.FindById("aaaa000000000009");
                Check("Schema 12 -> 13: die Spalte wird beim Start nachgezogen, der Bestand bleibt gueltig (F-M501)",
                    bestand is { ImdbId: "tt1442464", TmdbId: "1422" }
                    && vorherTvst is null
                    && nachher?.TvstShowId == "512"
                    && legacyDb.Meta.FindById("db")?.SchemaVersion == 13,
                    "bestand=" + (bestand?.ImdbId ?? "-") + "/" + (bestand?.TmdbId ?? "-")
                    + "/tvst=" + (vorherTvst ?? "<null>")
                    + " nachher=" + (nachher?.TvstShowId ?? "-")
                    + " marker=" + legacyDb.Meta.FindById("db")?.SchemaVersion);
            }
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(tvstRoot))
                {
                    Directory.Delete(tvstRoot, true);
                }
            }
            catch (IOException)
            {
                // liegen gebliebene Datei: kein Grund, die Suite rot zu machen
            }
        }

        Console.WriteLine();
        if (_failures > 0)
        {
            Console.WriteLine($">>> ENDPUNKTE: {_failures} von {_checks} Pruefungen fehlgeschlagen");
            return 1;
        }

        Console.WriteLine($">>> ENDPUNKTE OK: {_checks} Pruefungen bestanden");
        return 0;
    }

    private static PluginConfiguration Conf(Action<PluginConfiguration> mutate)
    {
        var c = new PluginConfiguration();
        c.Username = "u";
        c.Password = "p";
        c.ApiKey = "k";
        c.TmdbApiKey = "t";
        c.SelectedLibraries = new List<string> { "Movies" };
        c.DownloadLanguages = "EN, DE";
        mutate(c);
        return c;
    }

    /// <summary>Liest eine Datei in einem nicht-async Helfer — CA1849 prueft nur den async-Pfad.</summary>
    private static string ReadText(string path) => File.ReadAllText(path);

    /// <summary>Oeffnet einen ZIP-Eintrag im nicht-async Helfer (dieselbe CA1849-Regel).</summary>
    private static System.IO.Stream OpenForRead(this System.IO.Compression.ZipArchiveEntry entry) => entry.Open();

    /// <summary>Schreibt in einen Strom im nicht-async Helfer.</summary>
    private static void WriteAll(System.IO.Stream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);

    /// <summary>F-M501: laeuft der Aufruf in eine Ausnahme? (Fuer die Grenzen von BuildAddShow.)</summary>
    private static bool Throws(Action action)
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

    /// <summary>
    /// F-M501: die Seiten von tvsubtitles.net, die die Aufloesung braucht — die Serienliste unter
    /// <c>/add.html</c> und das Anlege-Formular unter <c>/add-tvshow1.php</c>. Die Liste ist
    /// veraenderlich, damit "erst anlegen, dann erneut suchen" wirklich geprueft wird und nicht nur
    /// behauptet. Die Zaehler sind der Beweis fuer "kein Netzzugriff" bzw. "genau einmal angelegt".
    /// </summary>
    private sealed class TvstSiteHandler : HttpMessageHandler
    {
        private readonly List<(int Id, string Name)> _shows = new();

        public TvstSiteHandler(int id, string name) => _shows.Add((id, name));

        /// <summary>GETs auf die Serienliste.</summary>
        public int Gets { get; private set; }

        /// <summary>POSTs auf das Anlege-Formular.</summary>
        public int Posts { get; private set; }

        /// <summary>POSTs auf das Upload-Formular.</summary>
        public int Uploads { get; private set; }

        /// <summary>Der Rumpf der letzten Anlage.</summary>
        public string? LastBody { get; private set; }

        /// <summary>Der Rumpf des letzten Uploads.</summary>
        public string? LastUploadBody { get; private set; }

        /// <summary>Antwortet die Seite auf die Anlage mit diesem Status?</summary>
        public int PostStatus { get; set; } = 200;

        /// <summary>Wird die Serie nach erfolgreicher Anlage in die Liste aufgenommen?</summary>
        public (int Id, string Name)? AddAfterPost { get; set; }

        /// <summary>Antwortet die Seite ohne Auswahlfeld (unparsbare/leere Liste)?</summary>
        public bool EmptyList { get; set; }

        /// <summary>Die Anfragen in ihrer Reihenfolge — "GET", "POST" (Anlage) oder "UPLOAD".</summary>
        public List<string> Order { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _ = body;

            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("add-tvshow1.php", StringComparison.Ordinal))
            {
                Order.Add("POST");
                Posts++;
                LastBody = body;
                if (PostStatus is < 200 or >= 300)
                {
                    return new HttpResponseMessage((HttpStatusCode)PostStatus);
                }

                if (AddAfterPost is { } add)
                {
                    _shows.Add(add);
                }

                return Html("<html><body>ok</body></html>");
            }

            if (request.Method == HttpMethod.Post)
            {
                Order.Add("UPLOAD");
                Uploads++;
                LastUploadBody = body;
                return Html("<html><body>ok</body></html>");
            }

            Order.Add("GET");
            Gets++;
            if (EmptyList)
            {
                return Html("<html><body><p>kein Formular</p></body></html>");
            }

            var sb = new StringBuilder("<select name=\"tvshow\" size=\"1\">");
            sb.Append("<option value=\"0\">- Select TV-Show -</option>");
            foreach (var (id, name) in _shows)
            {
                sb.Append("<option value=\"").Append(id).Append("\">").Append(name).Append("</option>");
            }

            sb.Append("</select>");
            return Html("<html><body>" + sb + "</body></html>");
        }

        private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html"),
        };
    }

    /// <summary>F-M501: eine feste JSON-Antwort fuer den TMDb-Serien-Datensatz.</summary>
    private sealed class JsonHandler : HttpMessageHandler
    {
        private readonly string _json;

        public JsonHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
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
    /// Serves the captured tvsubtitles pages and a small ZIP, so the tvsubtitles endpoint can be driven
    /// end to end without the live site.
    /// </summary>
    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly string _dir;

        public FixtureHandler(string dir) => _dir = dir;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            var url = request.RequestUri!.ToString();
            var path = url.Replace("https://www.tvsubtitles.net/", string.Empty, StringComparison.Ordinal);

            if (path == "add.html")
            {
                return Html(ReadText(Path.Combine(_dir, "add-tvshow-select.html")));
            }

            if (path.StartsWith("download-", StringComparison.Ordinal))
            {
                // The site answers the download URL with the ZIP in one of its two shapes (F-M482).
                using var buffer = new MemoryStream();
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var entry = archive.CreateEntry("The Middle - 2x24 - Back To Summer.HDTV.FEVER.en.srt");
                    using var stream = entry.OpenForRead();
                    var body = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nhello\n");
                    WriteAll(stream, body);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(buffer.ToArray()),
                };
            }

            var file = path switch
            {
                "tvshow-512.html" => "tvshow-512.html",
                "tvshow-512-2.html" => "tvshow-512-2.html",
                _ when path.StartsWith("episode-", StringComparison.Ordinal) => "episode-31911.html",
                _ when path.StartsWith("subtitle-", StringComparison.Ordinal) => "subtitle-page.html",
                _ => null,
            };

            if (file is null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return Html(ReadText(Path.Combine(_dir, file)));
        }

        private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html"),
        };
    }
}
