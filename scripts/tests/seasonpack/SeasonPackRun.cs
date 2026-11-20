// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. Sub Scribe is distributed WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR
// PURPOSE. See the GNU General Public License for more details.
//
// T163 / F-M372: a season goes up as ONE pack — but only when the season is complete and
// every episode passed the gates the single path applies.
//
// THE RULES THE OPERATOR SET (01.11.2026):
//
//   completeness  "ist die season vollzählig" — asked at IMDb, because the claim
//                 is_full_season=true is about the season that AIRED, not about the folder.
//                 Eight of ten episodes is a complete folder and an incomplete season.
//   one bad cue   a season whose episode failed qa-invalid-srt stays single, as does an
//                 incomplete season. The pack never absorbs a refusal.
//   one source    lang, hi, quality, framerate and releases are single fields on SubDL's
//                 side, so mixed sources cannot share a container.
//   the name      {imdb}.S{ss}.{md5 of the zip}.{LANG}.zip — the container's own hash, built
//                 byte-stable, otherwise the same season hashes differently every run.
//
// Usage: dotnet build Jellyfin.Plugin.SubdlSync -c Release
//        dotnet run --project scripts/tests/seasonpack/seasonpack.csproj -- [repo-root]

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Pipeline;
using Microsoft.Extensions.Logging;

namespace SeasonPackRun;

internal static class Program
{
    private static int _checks;
    private static readonly List<string> Failures = new();

    private static void Check(bool ok, string what, string? detail = null)
    {
        _checks++;
        if (!ok)
        {
            Failures.Add(what);
            Console.WriteLine("  FAIL  " + what + (string.IsNullOrEmpty(detail) ? string.Empty : "  [" + detail + "]"));
        }
    }

    private static string Srt(int episode) =>
        string.Join("\n\n", Enumerable.Range(1, 30).Select(i =>
            $"{i}\n00:00:{i:00},000 --> 00:00:{i:00},800\nEpisode {episode} line {i}\n"));

    /// <summary>One pack member, built without a library: the coordinator only needs name and numbers.</summary>
    private static SeasonPackCoordinator.Candidate Candidate(int episode) => new(
        new MediaBrowser.Controller.Entities.TV.Episode { Name = $"Episode {episode}" },
        $"item-{episode}",
        "0123456789abcdef0123456789abcdef",
        "tt1",
        null,
        1,
        episode,
        "EN",
        false,
        false,
        0,
        Srt(episode),
        $"Show.S01E{episode:00}.1080p.WEB.x264-NTb",
        "web",
        "23.976",
        1920,
        1080,
        "h264");

    /// <summary>
    /// The pipeline's filler, replaced: it answers with a candidate or refuses, and it keeps the order
    /// it was asked in so the harness can assert that only the missing episode is asked for.
    /// </summary>
    private sealed class FakeFiller : IPackFiller
    {
        private readonly HashSet<int> _refuse;
        private readonly int _answer;

        public FakeFiller(IEnumerable<int> refuse, int answer)
        {
            _refuse = refuse.ToHashSet();
            _answer = answer;
        }

        public List<int> Calls { get; } = new();

        /// <summary>F-M433: what the files of a season say they carry.</summary>
        public HashSet<(string Language, bool HearingImpaired)> InFiles { get; } = new()
        {
            ("EN", false), ("DE", false),
        };

        public bool FileCarriesLanguage(string imdbId, int season, int episode, string lang, bool hearingImpaired)
            => InFiles.Contains((lang, hearingImpaired));

        /// <summary>F-M442: what the library says about quality and frame rate, per episode.</summary>
        public Func<int, (string? Quality, string? Framerate)> Source { get; set; }
            = _ => ("1080p.WEB-DL", "23,976");

        public (string? Quality, string? Framerate) SourceFacts(string imdbId, int season, int episode)
            => Source(episode);

        public IReadOnlyCollection<(string Language, bool HearingImpaired)> FileLanguages(string imdbId, int season)
            => InFiles;

        /// <summary>F-M600: Folgen, von denen der Store schon weiss, dass sie nicht beitreten koennen.</summary>
        public HashSet<int> CannotJoin { get; } = new();

        public bool EpisodeCannotJoin(string imdbId, int season, int episode, string lang, bool hearingImpaired, out string reason)
        {
            reason = CannotJoin.Contains(episode) ? "every track of this language is already refused in the store (1)" : string.Empty;
            return CannotJoin.Contains(episode);
        }

        public Task<SeasonPackCoordinator.Candidate?> FillAsync(
            string imdbId, int season, int episode, string lang, bool hearingImpaired, CancellationToken ct)
        {
            Calls.Add(episode);
            if (_refuse.Contains(episode))
            {
                return Task.FromResult<SeasonPackCoordinator.Candidate?>(null);
            }

            return Task.FromResult<SeasonPackCoordinator.Candidate?>(Candidate(_answer > 0 ? _answer : episode));
        }
    }

    /// <summary>Captures the Normal lines the coordinator writes, so the harness can read them.</summary>
    private sealed class ListLogger : ILogger
    {
        private readonly List<string> _lines;

        public ListLogger(List<string> lines) => _lines = lines;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _lines.Add(formatter(state, exception));
    }

    /// <summary>
    /// A coordinator in dry-run mode: it walks the whole pack decision and stops before the transfer, so
    /// no HTTP client is used and the registry is never asked to write.
    /// </summary>
    private static SeasonPackCoordinator NewCoordinator(
        List<string> log,
        IReadOnlyDictionary<(string ImdbId, int Season), IReadOnlyList<int>> complete,
        ISet<(string ImdbId, int Season)> packable,
        IPackFiller filler,
        Jellyfin.Plugin.SubScribe.Configuration.PluginConfiguration config,
        Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry? registry = null)
        => new(
            new Jellyfin.Plugin.SubScribe.Api.ScribeApiClient(new System.Net.Http.HttpClient()),
            registry!,
            new ListLogger(log),
            config,
            complete,
            packable,
            (_, _) => { },
            _ => { },
            _ => { },
            filler);

    /// <summary>
    /// F-M647: sammeln und senden sind zwei Schritte — der Test geht denselben Weg wie die Pipeline,
    /// damit die Zeilen (Füllen, DRY-RUN, Absage) dort entstehen, wo der Lauf sie schreibt.
    /// </summary>
    private static void FillAndSend(SeasonPackCoordinator coordinator, IReadOnlyList<string>? sources = null)
    {
        var plan = coordinator
            .FillAsync("tt1", 1, "EN", false, sources, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (plan != null)
        {
            coordinator.SendAsync(plan, Jellyfin.Plugin.SubScribe.Data.PackEntity.SubdlSource, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
    }

    private static int Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";

        // ── 1. What counts as aired ─────────────────────────────────────────────
        Console.WriteLine("1. Aired episode numbers");
        var got = SeasonPack.AiredNumbers(new[] { 0, 3, 1, 2, 10, 2 });
        Check(got.SequenceEqual(new[] { 1, 2, 3, 10 }), "zero is dropped, rest sorted and deduplicated");
        Check(SeasonPack.AiredNumbers(Array.Empty<int>()).Count == 0, "no numbers, no season");
        // Measured shape at IMDb: Game of Thrones S01 reports eleven entries — ten episodes
        // plus the unaired original pilot at E0.
        var thrones = Enumerable.Range(0, 11).ToList();
        Check(SeasonPack.AiredNumbers(thrones).Count == 10, "eleven IMDb entries with an E0 are ten episodes");

        // ── 2. Is the season complete ───────────────────────────────────────────
        Console.WriteLine("2. Completeness");
        var aired8 = Enumerable.Range(1, 8).ToList();
        Check(SeasonPack.IsComplete(aired8, aired8), "eight of eight is complete");
        Check(!SeasonPack.IsComplete(aired8, Enumerable.Range(1, 7)), "seven of eight is not");
        Check(!SeasonPack.IsComplete(aired8, Enumerable.Range(1, 9)), "nine of eight is not");
        Check(!SeasonPack.IsComplete(aired8, new[] { 1, 2, 3, 4, 5, 6, 7, 9 }), "a gap at the end is not");
        Check(!SeasonPack.IsComplete(aired8, new[] { 1, 2, 3, 4, 5, 6, 7 }), "a missing episode is not");
        Check(!SeasonPack.IsComplete(Array.Empty<int>(), Array.Empty<int>()), "an unanswered season is never complete");
        Check(SeasonPack.IsComplete(new[] { 0, 1, 2, 3 }, new[] { 1, 2, 3 }), "an E0 on the aired side does not demand an episode");
        Check(SeasonPack.IsComplete(new[] { 1, 2, 3 }, new[] { 0, 1, 2, 3 }), "an E0 in the library does not block the pack");
        Check(SeasonPack.IsComplete(new[] { 5 }, new[] { 5 }), "a one-episode season can be complete");

        // ── 3. The name carries the pack's identity ─────────────────────────────
        Console.WriteLine("3. Pack name");
        var hashes8 = Enumerable.Range(1, 8)
            .Select(e => Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry.ComputeHash(Srt(e))).ToList();
        string packHash = SeasonPack.PackHash(hashes8);
        Check(packHash.Length == 32, "the pack hash is an MD5");
        Check(SeasonPack.PackHash(hashes8) == packHash, "the same season hashes the same");
        Check(SeasonPack.PackHash(hashes8.AsEnumerable().Reverse()) != packHash, "the episode order is part of the identity");
        var corrected = hashes8.ToList();
        corrected[2] = Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry.ComputeHash(Srt(3) + "\n99\n00:01:00,000 --> 00:01:01,000\nchanged\n");
        Check(SeasonPack.PackHash(corrected) != packHash, "a corrected episode is a different pack");

        string name = SeasonPack.Name("tt0944947", 1, packHash, "DE");
        Check(name == $"tt0944947.S01.{packHash}.DE", "the name is {imdb}.S{ss}.{pack hash}.{LANG}");
        Check(!name.EndsWith(".zip", StringComparison.Ordinal), "the .zip is SubDL's to append — the name field becomes the zip name");
        Check(SeasonPack.Name("tt0944947", 12, "abc", "EN") == "tt0944947.S12.abc.EN", "the season is two digits, the language keeps its case");

        // ── 4. What SubDL documents about a packed upload ───────────────────────
        Console.WriteLine("4. Documented upload shape");
        var api = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/ScribeApiClient.cs"));
        Check(api.Contains("JsonSerializer.Serialize(fileNIds)", StringComparison.Ordinal), "step 3 sends the ARRAY of file ids");
        Check(api.Contains("[\"is_full_season\"] = \"true\"", StringComparison.Ordinal), "the pack claims a full season");
        Check(api.Contains("[\"is_full_season\"] = \"false\"", StringComparison.Ordinal), "a single upload still claims nothing");
        Check(api.Contains("[\"name\"] = packName", StringComparison.Ordinal), "the name field carries the pack name SubDL zips under");
        // F-M377 (measured 01.11.2026): ef/ee are the reason our packs were filed as episode 1 with
        // full_season=false. The file set is the range — the fields must NOT travel.
        Check(api.IndexOf("[\"ef\"] = episodeFrom", StringComparison.Ordinal) < 0
              && api.IndexOf("[\"ee\"] = episodeTo", StringComparison.Ordinal) < 0,
            "a pack carries no ef/ee — SubDL files it as episode 1 when they are present");
        Check(api.Contains("F-M377"), "… and the measurement is recorded at the call site");
        Check(api.Contains("UploadSingleFileAsync", StringComparison.Ordinal), "one file per uploadSingleSubtitle call");
        Check(api.Contains("pack-file-refused", StringComparison.Ordinal), "a refused file is recognised as such");
        Check(api.Contains("JsonSerializer.Serialize(releaseNames)", StringComparison.Ordinal),
            "the pack carries every episode's release name — the field is an array");

        // ── 5. The inner names are exactly the single upload's ──────────────────
        Console.WriteLine("5. Inner file names");
        string hash = "0123456789abcdef0123456789abcdef";
        Check(SeasonPack.NeutralFileName(true, "tt0944947", 1, 3, "DE", hash) == $"tt0944947.S01E03.{hash}.DE.srt", "series form carries SxxEyy");
        Check(SeasonPack.NeutralFileName(false, "tt0944947", 0, 0, "DE", hash) == $"tt0944947.{hash}.DE.srt", "movie form carries no episode");
        // F-M379 (operator decision 01.11.2026): the SDH variant has to be readable in the NAME. The
        // `hi` field SubDL carries ends where the name begins — the zip, the member file and the entry
        // title show nothing but the name, so two English packs were indistinguishable but for the hash.
        Check(SeasonPack.Name("tt0944947", 1, packHash, "EN", true) == $"tt0944947.S01.{packHash}.EN.SDH",
            "an SDH pack states the variant in its name");
        Check(SeasonPack.NeutralFileName(true, "tt0944947", 1, 3, "EN", hash, true) == $"tt0944947.S01E03.{hash}.EN.SDH.srt",
            "an SDH single upload states it as well");
        Check(SeasonPack.MemberFileName(1, 1, "The Emperor's Peace", "EN", true) == "S01E01 - The Emperor's Peace.en.SDH.srt",
            "a pack member names the variant too");
        Check(SeasonPack.NeutralFileName(false, "tt0944947", 0, 0, "EN", hash, true) == $"tt0944947.{hash}.EN.SDH.srt",
            "the movie form states it as well");
        Check(SeasonPack.HiMarker(false) == string.Empty
              && SeasonPack.Name("tt0944947", 1, packHash, "EN") == $"tt0944947.S01.{packHash}.EN"
              && SeasonPack.MemberFileName(1, 1, "The Emperor's Peace", "EN") == "S01E01 - The Emperor's Peace.en.srt",
            "the regular track keeps its old name unchanged");

        // ── 6. The rules in the code, not only in the arithmetic ────────────────
        Console.WriteLine("6. Rules wired");
        var config = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Configuration/PluginConfiguration.cs"));
        Check(config.Contains("public bool UploadSeasonPacks { get; set; } = true;", StringComparison.Ordinal),
            "the switch exists and is on by default");

        var page = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Configuration/configPage.html"));
        Check(page.Contains("id=\"UploadSeasonPacks\"", StringComparison.Ordinal), "the switch is a field in the upload section");
        Check(page.Contains("config.UploadSeasonPacks = document.querySelector", StringComparison.Ordinal)
              && page.Contains("document.querySelector('#UploadSeasonPacks').checked = config.UploadSeasonPacks", StringComparison.Ordinal),
            "the config page reads and writes it");

        Check(api.Contains("public async Task<UploadResult> UploadSeasonPackAsync", StringComparison.Ordinal), "the pack has its own upload call");

        var coordinator = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/SeasonPackCoordinator.cs"));
        Check(coordinator.Contains("do not share one source", StringComparison.Ordinal), "mixed sources never share a container");
        Check(coordinator.Contains("season pack skipped — {Imdb} S{Season} [{Lang}]: {Have} of {Want} episode(s)", StringComparison.Ordinal),
            "an incomplete buffer says so and falls back to singles");
        Check(coordinator.Contains("pack-file-refused", StringComparison.Ordinal), "a refused container falls back to singles, not to loss");
        Check(!coordinator.Contains("subdlId: packId", StringComparison.Ordinal),
            "F-M585: a pack no longer writes its id onto the episode rows — the pack row is the pack's only home");
        Check(!coordinator.Contains("c.ReleaseName).Distinct().Count() == 1", StringComparison.Ordinal),
            "differing release names do not block a pack — only quality and frame rate do");
        Check(coordinator.Contains("PackReleases(ordered, key)", StringComparison.Ordinal),
            "a pack builds its release list through the season label");
        Check(coordinator.Contains("SeasonPack.PackRelease(", StringComparison.Ordinal),
            "the pack's one release entry is built from the season form of the episode releases");
        Check(coordinator.Contains("return new List<string>\n        {\n            SeasonPack.PackRelease(")
              || coordinator.Contains("SeasonPack.PackRelease("),
            "exactly one release entry goes to SubDL");
        Check(!coordinator.Contains("ordered.Select(c => c.ReleaseName).Distinct().ToList()", StringComparison.Ordinal),
            "the episode release names are never handed to SubDL as the pack's release list");

        // F-M374: SubDL titles a full-season upload after releases[0] and ignores `name`
        // (measured 01.11.2026 against /user/mySubtitles), so the first entry has to name the season.
        Check(SeasonPack.ReleaseLabel("Lanterns", "tt26545992", 1, "EN", false) == "Lanterns S01 - complete season [EN]",
            "the label names series, season and language");
        Check(SeasonPack.ReleaseLabel(null, "tt26545992", 3, "EN", true) == "tt26545992 S03 - complete season (SDH) [EN]",
            "without a series name the IMDb id carries the label and SDH is marked");
        Check(SeasonPack.ReleaseLabel("  ", "tt26545992", 2, "FR", false) == "tt26545992 S02 - complete season [FR]",
            "a blank series name falls back to the IMDb id");

        // F-M374: one release entry per pack — other uploaders name a season release there, and
        // this plugin mixes nothing: differing groups name no release at all.
        var foundation = SeasonPack.PackRelease(
            new[]
            {
                "Foundation.S01E01.1080p.WEB.H264-NHTFS",
                "Foundation.S01E02.1080p.WEB.H264-NHTFS",
                "Foundation.S01E03.1080p.WEB.H264-NHTFS",
            },
            "Foundation", "tt0804484", 1, "EN", false);
        Check(foundation == "Foundation.S01.1080p.WEB.H264-NHTFS",
            "episodes from one release give one season release name");
        Check(!foundation.Contains("E01", StringComparison.Ordinal), "the episode marker never survives");
        var mixed = SeasonPack.PackRelease(
            new[]
            {
                "Foundation.S02E01.1080p.WEB.H264-NHTFS",
                "Foundation.S02E02.1080p.WEB.H264-SuccessfulCrab",
                "Foundation.S02E03.1080p.WEB.H264-NHTFS",
            },
            "Foundation", "tt0804484", 2, "EN", false);
        Check(mixed == "Foundation S02 - complete season [EN]",
            "two groups never get merged into one release name — the label stands in");
        var dashed = SeasonPack.PackRelease(
            new[] { "Lanterns - S01E01 - Test", "Lanterns - S01E08 - Test" },
            "Lanterns", "tt26545992", 1, "EN", false);
        Check(dashed == "Lanterns - S01 - Test", "the marker is cut out, not replaced by junk");
        // F-M380 (measured 01.11.2026, Dexter: Resurrection): the WIDTH decides the resolution token.
        // 1920x800 is a cropped 1080p encode and was named 720p by the height-only rule.
        Check(SeasonPack.MediaRelease("Dexter: Resurrection", "tt33043892", 1, "web", 1920, 800, "hevc")
              == "Dexter.Resurrection.S01.1080p.WEB.x265",
            "a cropped 1080p encode is 1080p, and the colon of the series name does not survive");
        Check(SeasonPack.MediaRelease("Foundation", "tt0804484", 1, "web", null, 800, "h264")
              == "Foundation.S01.720p.WEB.x264", "without a width the height stays the fallback");
        Check(SeasonPack.MediaRelease("Dexter: Resurrection", "tt33043892", 1, "web", 1920, 800, "hevc", "Rapta")
              == "Dexter.Resurrection.S01.1080p.WEB.x265-Rapta",
            "a group the file name states is appended — never invented");

        // The group is read off the file name, and only when that name states one.
        Check(SeasonPack.ReleaseGroup("Dexter.Resurrection.S01.1080p.WEBRip.AAC5.1.10bits.x265-Rapta") == "Rapta",
            "the last dash segment of a release name is the group");
        Check(SeasonPack.ReleaseGroup("Dexter - Resurrection - S01E01 - A Beating Heart...") == null,
            "an episode title after the last dash is no group");
        Check(SeasonPack.ReleaseGroup("Show.S01.1080p.WEB.x265-AAC5.1") == null,
            "a technical token with a dot is no group");
        // F-M430 (operator order 03.11.2026): the four spellings found in the library — and the ones
        // that must stay null because the name states no group.
        Check(SeasonPack.ReleaseGroup("Toy.Story.1995.1080p.BRrip.x264.YIFY") == "YIFY",
            "F-M430: a dot-separated scene group is read (x264.YIFY)");
        Check(SeasonPack.ReleaseGroup("Too Good to be True 2024 S01E02 1080p WEB-DL HEVC x265 BONE") == "BONE",
            "F-M430: a space-separated group is read (x265 BONE)");
        Check(SeasonPack.ReleaseGroup("Mayor of Kingstown (2021) - S04E07 - My Way (1080p AMZN WEB-DL x265 Ghost)") == "Ghost",
            "F-M430: a group inside a trailing parenthesis is read");
        Check(SeasonPack.ReleaseGroup("Cunk.on.Britain.s01e01.[MPup]") == "MPup",
            "F-M430: a bracketed group is read");
        Check(SeasonPack.ReleaseGroup("Unicorn.Academy.S03E01.1080p.WEB.h264-DOLORES[EZTVx.to]") == "DOLORES",
            "F-M430: a site tag behind the group does not hide it");
        Check(SeasonPack.ReleaseGroup("The Middle.S01E05.MULTI.1080p.WEB-DL.H265-FTMVHD") == "FTMVHD",
            "F-M505: the group is sent to tvst, not the file name — the name is what the site truncated");
        Check(SeasonPack.ReleaseGroup("A.Bona.Fide.Killer.S01E12.(THENKIRI.COM)") == null,
            "F-M430: a long site watermark is not a group");
        Check(SeasonPack.ReleaseGroup("Toy.Story.4.2019.1080p.BluRay.x264-[YTS.LT]") == "YTS"
              && SeasonPack.ReleaseGroup("Forgotten.Island.2026.1080p.WEBRip.x264.AAC5.1-[YTS.GG - YTS.BZ]") == "YTS",
            "F-M430: a short brand behind a domain inside the bracket is the group");
        Check(SeasonPack.ReleaseGroup("Pressure.2026.1080p.BluRay.x264-ZoroSenpai_EniaHD") == "ZoroSenpai",
            "F-M430: an uploader pair separated by an underscore yields the group");
        Check(SeasonPack.ReleaseGroup("The Odyssey 2026.720p.WEB-DL.AAC.x264-rothebehap.mp4") == "rothebehap",
            "F-M430: a second extension left in the name does not hide the group");
        Check(SeasonPack.ReleaseGroup("The Thundermans - S02E12 - Meet the Evilmans") == null,
            "F-M430: a dash segment with spaces is a title, never a group");
        Check(SeasonPack.ReleaseGroup("For All Mankind S03E04") == null
              && SeasonPack.ReleaseGroup("The Thundermans - S02E12 - Meet the Evilmans") == null,
            "F-M430: an episode title behind an episode tag stays null — nothing is invented");
        Check(SeasonPack.ReleaseGroup(null) == null && SeasonPack.ReleaseGroup("") == null,
            "no name, no group");

        // F-M505 (measured 15.11.2026 on tvsubtitles.net, first Middle season, 193 entries read): the
        // Release column of the page carries the GROUP — 2HD (28x), REWARD (24x), FQM (14x), FEVER,
        // POW4, CTU, LOL, DIMENSION, 91 empty, not one entry with a dot. Our uploads carried the media
        // file name instead, and the page cut it at the dash: "The Middle.S01E21.MULTI.1080p.WEB-DL"
        // stood there and the group (FTMVHD) was gone. This is the real library name of that episode.
        Check(SeasonPack.ReleaseGroup("The Middle.S01E01.MULTI.1080p.WEB-DL.H265-FTMVHD") == "FTMVHD",
            "F-M505: the group is read off the real library name (…WEB-DL.H265-FTMVHD → FTMVHD) — the page's release column carries the group, not the file name");

        Check(SeasonPack.PackRelease(new[] { "Reacher.3x04.1080p.WEB" }, "Reacher", "tt9288030", 3, "EN", false)
              == "Reacher.S03.1080p.WEB", "1x05 style markers reduce to the season too");
        Check(SeasonPack.PackRelease(new[] { "Folge eins", "Folge zwei" }, "Lanterns", "tt26545992", 1, "EN", false)
              == "Lanterns S01 - complete season [EN]",
            "a release name without a marker cannot be reduced and the label stands in");
        Check(SeasonPack.PackRelease(Array.Empty<string>(), "Lanterns", "tt26545992", 1, "EN", true)
              == "Lanterns S01 - complete season (SDH) [EN]", "an empty list still yields a title");

        // F-M376: when no name carries a release, the file's own facts name the season.
        // F-M378: SubDL reads s/e from each member's own name — a season in the member name turns the
        // pack into an episode range.
        Check(SeasonPack.MemberFileName(1, 1, "The Emperor's Peace", "EN") == "S01E01 - The Emperor's Peace.en.srt",
            "a member names its season and episode — SubDL reads both from that name");
        Check(SeasonPack.MemberFileName(2, 7, "Mysteries and Martyrs", "DE") == "S02E07 - Mysteries and Martyrs.de.srt",
            "season and episode follow the pack's own season");
        Check(SeasonPack.MemberFileName(3, 7, null, "FR") == "S03E07.fr.srt", "a missing title leaves number and episode");
        Check(SeasonPack.MemberFileName(1, 2, "Bad/Name: Here?", "ES") == "S01E02 - Bad Name Here.es.srt",
            "file-name characters are stripped, the language is lower case");

        Check(SeasonPack.MediaRelease("Foundation", "tt0804484", 1, "web", 1920, 1080, "h264")
              == "Foundation.S01.1080p.WEB.x264", "resolution, source and codec from the file name the season");
        Check(SeasonPack.MediaRelease("Dark Matter", "tt1", 2, "bluray", 3840, 2160, "hevc")
              == "Dark.Matter.S02.2160p.BluRay.x265", "spaces and codec names are release-shaped");
        Check(SeasonPack.MediaRelease("Foundation", "tt0804484", 1, "web", null, null, null) == "Foundation.S01.WEB",
            "a file without a video stream still names its source");
        Check(SeasonPack.MediaRelease("Foundation", "tt0804484", 1, null, null, null, null) == null,
            "no facts at all is no release");
        Check(SeasonPack.MediaRelease(null, "tt0804484", 3, "hdtv", 1280, 720, "av1") == "tt0804484.S03.720p.HDTV.AV1",
            "an unknown series name is replaced by the IMDb id");
        Check(SeasonPack.PackRelease(
                new[] { "S01E01 - The Emperor's Peace", "S01E02 - Preparing to Live" },
                "Foundation", "tt0804484", 1, "EN", false,
                SeasonPack.MediaRelease("Foundation", "tt0804484", 1, "web", 1920, 1080, "h264"))
              == "Foundation.S01.1080p.WEB.x264",
            "names without a release fall through to the built release");
        Check(SeasonPack.PackRelease(
                new[] { "Foundation.S01E01.1080p.WEB.x264-NTb", "Foundation.S01E02.1080p.WEB.x264-NTb" },
                "Foundation", "tt0804484", 1, "EN", false,
                "Foundation.S01.1080p.WEB.x264")
              == "Foundation.S01.1080p.WEB.x264-NTb",
            "a release the names carry still wins over the built one");
        Check(SeasonPack.PackRelease(
                new[] { "S01E01 - One", "S01E02 - Two" },
                "Foundation", "tt0804484", 1, "FR", false, null)
              == "Foundation S01 - complete season [FR]",
            "no names, no facts — the neutral label stands in");
        Check(coordinator.Contains("DRY-RUN would upload season pack", StringComparison.Ordinal), "a dry run names the pack it would send and sends nothing");
        // F-M414: the id comes from mySubtitles right after the upload; the pack name is only the
        // last resort when the account's own list has nothing matching.
        Check(coordinator.Contains("ResolveOwnPackIdAsync", StringComparison.Ordinal)
              && coordinator.Contains("?? plan.Name", StringComparison.Ordinal),
            "the pack takes its id from mySubtitles and keeps the name only as the last resort");

        var pipeline = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));

        // F-M505 (operator 15.11.2026: "Release on tvst is not perfect"): the tvst ticket carries the
        // GROUP, never the media file name. The page cuts the release at the dash, so a file name
        // arrives there as "The Middle.S01E21.MULTI.1080p.WEB-DL" and the group is lost; an empty
        // field is allowed and is the majority on the page, hence the `?? string.Empty`. Bound as
        // source text, so a later edit cannot quietly put the file name back.
        {
            // F-M643: seit dem Staffel-Pack der zweiten Quelle gibt es ZWEI tvst-Tickets in der Datei.
            // Der Anker ist deshalb die Einzel-Upload-Methode — geprueft wird ihr Ticket, nicht das
            // des Behaelters (der traegt den Pack-Namen, nicht die Gruppe).
            int einzelAt = pipeline.IndexOf("private async Task<UploadResult> SendToTvSubtitlesAsync(", StringComparison.Ordinal);
            int ticketAt = einzelAt < 0
                ? -1
                : pipeline.IndexOf("var ticket = new Api.Endpoints.UploadTicket(", einzelAt, StringComparison.Ordinal);
            int ticketEnd = ticketAt < 0
                ? -1
                : pipeline.IndexOf("TvstShowId: show.ShowId", ticketAt, StringComparison.Ordinal);
            // F-M572: das Ticket endet nicht mehr an "TvstShowId: show.ShowId);" — der Folgentitel steht
            // jetzt als letztes Feld dahinter. Der Anker ist deshalb der Feldname ohne Klammer.
            string ticket = ticketAt < 0 || ticketEnd < 0
                ? string.Empty
                : pipeline[ticketAt..(ticketEnd + "TvstShowId: show.ShowId".Length)];
            Check(ticket.Length > 0
                  && ticket.Contains("SeasonPack.ReleaseGroup(releaseName) ?? string.Empty", StringComparison.Ordinal)
                  && !ticket.Contains("GetFileNameWithoutExtension", StringComparison.Ordinal),
                "F-M505: the tvst ticket's release field is the GROUP (`SeasonPack.ReleaseGroup(releaseName) ?? string.Empty`) and never the media file name — the page cuts a name at the dash (\"The Middle.S01E21.MULTI.1080p.WEB-DL\") and the group would be lost, an empty field being allowed and the majority on the page");
        }
        Check(pipeline.Contains("&& !cand.IsLoose && !cand.Forced", StringComparison.Ordinal), "no pack for a loose sidecar or a forced track");
        Check(pipeline.Contains("await packCoordinator.FlushAsync(runCt)", StringComparison.Ordinal), "the run closes its buffers before the summary");
        Check(pipeline.Contains("SeasonPack.IsComplete(aired, entry.Value)", StringComparison.Ordinal), "the pipeline asks the completeness rule");
        Check(pipeline.Contains("SeasonEpisodeIndex.AiredAsync", StringComparison.Ordinal), "and gets the answered season from IMDb");

        // F-M373: a pack needs the WHOLE season in this run. Episodes that are already up
        // individually cannot join, so a season that is only partly queued is worked as singles
        // right away — and the seasons that can still pack are closed in one go, one after another.
        var queuedItems = new List<SeasonPack.RunItem>
        {
            new("e5", "tt1", 1, 5),
            new("e1", "tt1", 1, 1),
            new("m1", null, -1, -1),
            new("e9", "tt2", 2, 3),
            new("e3", "tt1", 1, 3),
        };
        var order = SeasonPack.ClosureOrder(queuedItems, new HashSet<(string, int)> { ("tt1", 1) });
        Check(order.Take(3).SequenceEqual(new[] { "e1", "e3", "e5" }),
            "a packable season runs first and in episode order, so it closes in one go");
        Check(order.Skip(3).SequenceEqual(new[] { "m1", "e9" }),
            "everything outside the plan keeps the order the queue gave it");
        Check(SeasonPack.ClosureOrder(queuedItems, new HashSet<(string, int)>())
                .SequenceEqual(queuedItems.Select(i => i.Id)),
            "with nothing packable the run order is untouched");
        Check(!SeasonPack.IsComplete(new[] { 1, 2, 3 }, new[] { 1, 3 }),
            "a season that is only partly queued is not the season that aired");
        Check(coordinator.Contains("&& _packable.Contains((imdbId!, season))", StringComparison.Ordinal),
            "buffering still asks the plan, which now holds every complete season");
        // F-M420 (operator order 02.11.2026): "refetch und manual: erst einzelupload, dann
        // staffelpack". No season is buffered any more — the packable set stays EMPTY, every
        // position goes up the single way, and the container is planned at the END of the run from
        // the store (which by then holds this run's own uploads).
        Check(pipeline.Contains("var packable = new HashSet<(string ImdbId, int Season)>();", StringComparison.Ordinal),
            "nothing is held back for a pack: the packable set is empty and every position goes single first");
        Check(!pipeline.Contains("items = OrderForClosure(items, packable);", StringComparison.Ordinal),
            "the run no longer re-orders the queue around a pack");
        Check(pipeline.Contains("Erst packbildung dann Einzeluploads", StringComparison.Ordinal),
            "F-M429: the pack phase runs before the single uploads, stated where it is enforced");
        Check(pipeline.Contains("PlanPacksFromStore(completeSeasons, sources: slotQuellen)", StringComparison.Ordinal),
            "and the plan is made from the queue, before any single went up — for every source of the slot (F-M647)");
        Check(pipeline.Contains("brought all {Aired} episode(s)", StringComparison.Ordinal)
              && pipeline.Contains("no queue of this run was a whole season (F-M424/F-M425)", StringComparison.Ordinal),
            "every run packs only when its own queued episodes are the whole season (F-M425)");
        Check(pipeline.Contains("the rest is filled from what is already uploaded", StringComparison.Ordinal),
            "a partly queued season says that the gap is filled, not that it stays single");
        Check(pipeline.Split("await packCoordinator.AddAsync(").Length - 1 >= 2,
            "the dry run hands its candidates to the same buffer as the real run");
        Check(pipeline.Contains("a dry run shows the PACK it would send", StringComparison.Ordinal),
            "a dry run names the pack instead of eight singles");


        var index = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/SeasonEpisodeIndex.cs"));
        Check(index.Contains("caching.graphql.imdb.com", StringComparison.Ordinal), "IMDb is asked first");
        Check(index.Contains("/tv/{tmdbId}/season/{season}", StringComparison.Ordinal), "TMDB is the fallback");

        // ── 7. F-M389: a pack completed from what is already uploaded ───────────
        // Operator order 02.11.2026, revised the same day: a season may be FILLED with episodes whose
        // subtitle is already up — and a season whose episodes are ALL up is a pack of its own.
        Console.WriteLine("7. Filling a pack");
        var airedEight = Enumerable.Range(1, 8).ToList();
        var sevenOfEight = Enumerable.Range(1, 7).ToList();
        Check(SeasonPack.Uncovered(airedEight, sevenOfEight).SequenceEqual(new[] { 8 }),
            "seven of eight leaves exactly the eighth to fill");
        Check(SeasonPack.Uncovered(airedEight, Array.Empty<int>()).Count == 8,
            "with nothing queued the whole season comes from the filler");
        Check(SeasonPack.Uncovered(airedEight, airedEight).Count == 0, "a full buffer needs nothing filled");
        Check(SeasonPack.Uncovered(airedEight, new[] { 1, 2, 3, 5, 6, 7, 9 }).SequenceEqual(new[] { 4, 8 }),
            "a gap in the middle is filled per episode, and an episode that never aired is no gap");

        var filledSeason = sevenOfEight.Concat(SeasonPack.Uncovered(airedEight, sevenOfEight)).ToList();
        Check(SeasonPack.IsComplete(airedEight, filledSeason), "seven plus the filled eighth is the season");
        Check(!SeasonPack.IsComplete(airedEight, sevenOfEight), "without the fill the season stays single");
        Check(SeasonPack.PackHash(filledSeason.Select(e => Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry.ComputeHash(Srt(e))))
              == SeasonPack.PackHash(Enumerable.Range(1, 8).Select(e => Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry.ComputeHash(Srt(e)))),
            "a filled pack is the same container as a fully collected one — same members, same hash");

        // The rules in the code: a member may only come from a subtitle that passed the gates AND is
        // known to the registry; nothing is uploaded when one episode cannot join.
        int fillFrom = pipeline.IndexOf("public async Task<SeasonPackCoordinator.Candidate?> FillAsync", StringComparison.Ordinal);
        int fillTo = pipeline.IndexOf("private BaseItem? FindSeasonEpisode", StringComparison.Ordinal);
        string fill = fillFrom >= 0 && fillTo > fillFrom ? pipeline[fillFrom..fillTo] : string.Empty;
        Check(fill.Length > 0, "the pipeline is the pack filler");
        Check(fill.Contains("ContentHashRegistry.NormalizeSrt(extracted)", StringComparison.Ordinal),
            "the candidate is canonicalised before it is gated and hashed");
        Check(fill.Contains("GateReject(srt, lang, runtimeMs", StringComparison.Ordinal),
            "the same gates as the single path run on the filler");
        Check(fill.Contains("Registry.IsContentKnown(contentHash)", StringComparison.Ordinal),
            "… and only content the registry knows is accepted — that is the 'already uploaded' proof");
        Check(fill.IndexOf("GateReject(srt", StringComparison.Ordinal) < fill.IndexOf("Registry.IsContentKnown(contentHash)", StringComparison.Ordinal),
            "the gates run BEFORE the content is trusted");
        Check(fill.Split("return null;").Length - 1 >= 5, "every refusal leaves the pack without that member");
        Check(fill.Contains("the text speaks ", StringComparison.Ordinal),
            "a lying language tag cannot drag a second language into the container");

        Check(coordinator.Contains("IPackFiller? filler = null", StringComparison.Ordinal),
            "the filler is optional — without it the old behaviour is untouched");
        Check(coordinator.Contains("SeasonPack.Uncovered(aired, buffer.Select(c => c.Episode))", StringComparison.Ordinal),
            "an incomplete buffer asks which episode it lacks");
        Check(coordinator.Contains("public async Task<PackPlan?> FillAsync(", StringComparison.Ordinal)
              && coordinator.Contains("public async Task<PackSend> SendAsync(PackPlan plan, string source, CancellationToken ct)", StringComparison.Ordinal),
            "F-M647: the collector hands back one plan, and every source sends from it — collect and send are two steps");
        // F-M647: der Füll-Pfad hat nichts nachzureichen — was er tragen würde, steht schon oben.
        // Deshalb sammelt FillAsync nur und sendet nicht selbst; die Einzel (UploadSinglesAsync) ruft
        // allein der Puffer-Pfad, und SendAsync meldet den Fall nur als SinglesFallback.
        int fillNurStart = coordinator.IndexOf("public async Task<PackPlan?> FillAsync(", StringComparison.Ordinal);
        int fillNurEnde = coordinator.IndexOf("public async Task<PackSend> SendAsync(", StringComparison.Ordinal);
        string fillNur = fillNurStart > 0 && fillNurEnde > fillNurStart ? coordinator[fillNurStart..fillNurEnde] : string.Empty;
        Check(fillNur.Length > 0
              && !fillNur.Contains("UploadSinglesAsync(", StringComparison.Ordinal)
              && !fillNur.Contains("UploadAsync(", StringComparison.Ordinal),
            "a fill-only season re-sends nothing when the container is refused");
        Check(pipeline.Contains("FillAsync(packImdb, packSeason, packLang, packHi, slotQuellen, runCt)", StringComparison.Ordinal),
            "F-M429/F-M647: the packs go up first, before the item loop and before the buffers are flushed — and ONE collection feeds every source");
        Check(pipeline.Contains("_packAbsorbed.Add((packImdb, packSeason, packLang, packHi))", StringComparison.Ordinal),
            "the pairs a pack carried are remembered for the single phase");
        Check(pipeline.Contains("carried by the pack built in this run and sent as a single beside it (F-M585, the pack row lists this episode", StringComparison.Ordinal),
            "F-M585: a covered position is NOT skipped — it goes up as a single beside the pack");

        // ── 8. The coordinator itself, on fakes ─────────────────────────────────
        // No network, no Jellyfin, no database: the dry run of a pack stops before the upload, and the
        // registry is never touched on that path.
        Console.WriteLine("8. Coordinator on fakes");
        var dryConfig = new Jellyfin.Plugin.SubScribe.Configuration.PluginConfiguration { DryRun = true };
        var complete = new Dictionary<(string ImdbId, int Season), IReadOnlyList<int>> { [("tt1", 1)] = airedEight };
        var packable = new HashSet<(string ImdbId, int Season)> { ("tt1", 1) };

        var fillerOne = new FakeFiller(refuse: Array.Empty<int>(), answer: 8);
        var logOne = new List<string>();
        var coordinatorOne = NewCoordinator(logOne, complete, packable, fillerOne, dryConfig);
        foreach (int e in sevenOfEight)
        {
            coordinatorOne.AddAsync(Candidate(e), CancellationToken.None).GetAwaiter().GetResult();
        }

        coordinatorOne.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        Check(fillerOne.Calls.SequenceEqual(new[] { 8 }), "only the missing episode is asked for, in episode order");
        Check(logOne.Any(l => l.Contains("season pack filled", StringComparison.Ordinal)
                              && l.Contains("1 episode(s) from subtitles already uploaded, 7 from this run", StringComparison.Ordinal)),
            "one line says how many members came from the filler");
        Check(logOne.Any(l => l.Contains("DRY-RUN would upload season pack", StringComparison.Ordinal)
                              && l.Contains("8 episode(s)", StringComparison.Ordinal)
                              && l.Contains("Ef/Ee 1-8", StringComparison.Ordinal)),
            "seven collected plus one filled is a pack of eight");
        Check(!logOne.Any(l => l.Contains("season pack skipped", StringComparison.Ordinal)),
            "the skip line is not reached when the fill succeeds");

        var fillerAll = new FakeFiller(refuse: Array.Empty<int>(), answer: -1);
        var logAll = new List<string>();
        var coordinatorAll = NewCoordinator(logAll, complete, packable, fillerAll, dryConfig);
        FillAndSend(coordinatorAll);
        Check(fillerAll.Calls.Count == 8, "a season with nothing new asks the filler for every aired episode");
        Check(logAll.Any(l => l.Contains("season pack filled", StringComparison.Ordinal)
                              && l.Contains("8 episode(s) from subtitles already uploaded, 0 from this run", StringComparison.Ordinal)),
            "the fill-only pack is made of uploaded content only");
        Check(logAll.Any(l => l.Contains("DRY-RUN would upload season pack", StringComparison.Ordinal)
                              && l.Contains("8 episode(s)", StringComparison.Ordinal)),
            "and it is sent as one pack of eight");

        var fillerHole = new FakeFiller(refuse: new[] { 5 }, answer: -1);
        var logHole = new List<string>();
        var coordinatorHole = NewCoordinator(logHole, complete, packable, fillerHole, dryConfig);
        FillAndSend(coordinatorHole);
        Check(logHole.Any(l => l.Contains("episode 5 cannot join, and ", StringComparison.Ordinal)),
            "one episode that cannot join stops the pack and names itself");
        Check(!logHole.Any(l => l.Contains("DRY-RUN would upload season pack", StringComparison.Ordinal)),
            "no partial pack is ever sent");

        // ── F-M442: gemischte Quellen fallen VOR der ersten Extraktion auf ──────
        var fillerMixed = new FakeFiller(refuse: Array.Empty<int>(), answer: -1)
        {
            Source = episode => episode == 4 ? ("ATVP.WEB-DL", "23,809") : ("1080p.WEB-DL", "23,976"),
        };
        var logMixed = new List<string>();
        var coordinatorMixed = NewCoordinator(logMixed, complete, packable, fillerMixed, dryConfig);
        FillAndSend(coordinatorMixed);
        Check(fillerMixed.Calls.Count == 0,
            "F-M442: a mixed-source season is refused before the first extraction");
        Check(logMixed.Any(l => l.Contains("skipped before any extraction", StringComparison.Ordinal)
                                && l.Contains("S01E04", StringComparison.Ordinal)),
            "F-M442: the line names the episode that carries another source");
        Check(logMixed.Any(l => l.Contains("go up as singles after this pass", StringComparison.Ordinal)
                                || l.Contains("already up as singles", StringComparison.Ordinal)),
            "F-M442: the skip line states what really happens to the episodes instead of claiming they are up");

        // Gleichmaessige Quellen kommen weiterhin bis zum Fill durch.
        var fillerEven = new FakeFiller(refuse: Array.Empty<int>(), answer: -1);
        var logEven = new List<string>();
        var coordinatorEven = NewCoordinator(logEven, complete, packable, fillerEven, dryConfig);
        FillAndSend(coordinatorEven);
        Check(fillerEven.Calls.Count == 8, "F-M442: one quality and one frame rate across the season still packs");

        // ── F-M407: TMDb zuerst ─────────────────────────────────────────────────
        var seasonIndexSrc = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/SeasonEpisodeIndex.cs"));
        Check(seasonIndexSrc.IndexOf("FromTmdbAsync(tmdbId, tmdbApiKey, season, logger, ct)", StringComparison.Ordinal)
              < seasonIndexSrc.IndexOf("FromImdbAsync(imdbId, season, logger, ct)", StringComparison.Ordinal),
              "F-M407: TMDb is asked before IMDb");

        // ── F-M408: Vollstaendigkeit je Release-Gruppe ──────────────────────────
        var uploadSrc = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        Check(uploadSrc.Contains("SeasonPack.ReleaseGroup(") && uploadSrc.Contains("completeGroup"),
              "F-M408: the season is completed per release group");
        Check(uploadSrc.Contains("no release group covers the season"),
              "F-M408: a season split across groups says so and stays single");

        // ── F-M432: die Id aus der VERÖFFENTLICHUNGSliste lesen ─────────────────
        // Wortlaut einer echten Antwort von /user/myPublishedSubtitles (03.11.2026).
        const string published = """
        {"movie":{"name":"Stuart Fails to Save the Universe","year":2026,"type":"tv","sd_id":12716930},
         "subtitle":{"id":3755405,"sd_id":12716930,"n_id":"asBtVj74El","name":"Stuart.Fails.to.Save.the.Universe.S01.1080p.WEB.x265-ELiTE",
         "file_name":"Stuart.Fails.to.Save.the.Universe.S01.1080p.WEB.x265-ELiTE.zip","lang":"English","createdAt":"2026-11-02T19:22:07.000Z",
         "status":"downloaded","season":1,"s":1,"episode":0,"is_full_season":true}}
        """;
        using (var doc = System.Text.Json.JsonDocument.Parse(published))
        {
            var entry = Jellyfin.Plugin.SubScribe.Api.OwnSubtitleEntry.FromJson(doc.RootElement);
            Check(entry != null && entry!.UploadId == 3755405,
                "F-M432: the published list yields the upload id a delete needs");
            Check(entry!.Name.Contains("ELiTE", StringComparison.Ordinal) && entry.Language == "English",
                "F-M432: release name and language are read from the published entry");
            Check(entry.Season == 1 && entry.Episode == 0,
                "F-M432: season and episode come from the published entry — 0 for a movie");
            Check(entry.CreatedAt.Year == 2026,
                "F-M432: the camelCase createdAt of the published list is understood");
        }

        // ── F-M435: die EINTRAGS-Id, nicht die Upload-Id ────────────────────────
        // Wortlaut einer echten Antwort von /user/myPublishedSubtitles (03.11.2026): derselbe
        // Eintrag trägt zwei Nummernkreise — `id` ist die Eintrags-Id (die der Löschaufruf
        // erwartet), `subtitles_uploaded_id` die interne Upload-Id.
        const string publishedWithBoth = """
        {"movie":{"name":"Crystal Lake","year":2026,"type":"tv","sd_id":1300275},
         "subtitle":{"id":3758706,"sd_id":1300275,"subtitles_uploaded_id":380828,"n_id":"xP0ZaRg3nR",
         "name":"Crystal.Lake.S01.1080p.WEB.x265-NeoNoir","file_name":"Crystal.Lake.S01.1080p.WEB.x265-NeoNoir.zip",
         "lang":"English","createdAt":"2026-11-03T21:14:06.000Z","status":"downloaded","season":1,"s":1,"episode":0,
         "ef":1,"ee":8,"is_full_season":null}}
        """;
        using (var doc = System.Text.Json.JsonDocument.Parse(publishedWithBoth))
        {
            var entry = Jellyfin.Plugin.SubScribe.Api.OwnSubtitleEntry.FromJson(doc.RootElement);
            Check(entry != null && entry!.EntryId == 3758706,
                "F-M435: the published entry carries its own entry id");
            Check(entry!.UploadId == 380828,
                "F-M435: the internal upload id is read from subtitles_uploaded_id, not from id");
            Check(entry.HandleId == 3758706,
                "F-M435: the handle to store and delete with is the ENTRY id");
        }

        // Eine offene Einreichung hat noch keine Eintrags-Id — dort bleibt die Upload-Id der Griff.
        const string pending = """
        {"subtitle":{"id":380865,"name":"Foundation.S02.1080p.WEB.AV1","lang":"Malay","created_at":"2026-11-03T20:10:00.000Z"}}
        """;
        using (var doc = System.Text.Json.JsonDocument.Parse(pending))
        {
            var entry = Jellyfin.Plugin.SubScribe.Api.OwnSubtitleEntry.FromJson(doc.RootElement);
            Check(entry != null && entry!.EntryId == 380865 && entry.UploadId == 380865,
                "F-M435: a submission reports its upload id as id — both fields agree");
            Check(entry!.HandleId == 380865,
                "F-M435: without a published entry the upload id stays the handle");
        }

        Check(uploadSrc.Contains("publishedEntry.EntryId") && uploadSrc.Contains("registry.RemapSubdlId("),
            "F-M435: postprocessing moves stored handles to the published entry id");
        var registrySrc = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Registry/ContentHashRegistry.cs"));
        Check(registrySrc.Contains("public int RemapSubdlId(string uploadId, string entryId)"),
            "F-M435: the registry can rewrite a stored handle");
        Check(registrySrc.Contains("x.PackId, uploadId") && registrySrc.Contains("x.SubdlId, uploadId"),
            "F-M435: subtitle rows AND pack rows are rewritten");
        var coordinatorSrc = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/SeasonPackCoordinator.cs"));
        Check(coordinatorSrc.Contains("pack entry id {Id} taken from myPublishedSubtitles"),
            "F-M435: a pack stores the entry id when the published list names it");
        Check(uploadSrc.Contains("entry id {Id} taken from myPublishedSubtitles"),
            "F-M435: a single and a movie store the entry id the same way");

        // ── F-M442/F-M443/F-M444: die drei Vorabprüfungen und die zweite Fassung ──
        Check(uploadSrc.Contains("public (string? Quality, string? Framerate) SourceFacts("),
            "F-M442: quality and frame rate are readable without extracting anything");
        Check(uploadSrc.Contains("SpurIstGesperrt(fillMediaHash, position, lang, hearingImpaired, isSeries: true)"),
            "F-M443/F-M584: the fill asks the reject store before it extracts — and only for the source that owns the verdict");
        Check(uploadSrc.Contains("every track of this language is already refused in the store, nothing is extracted"),
            "F-M443: a language whose tracks are all refused costs no extraction at all");
        Check(uploadSrc.Contains("FindSeasonEpisodeItems(") && uploadSrc.Contains("trying the next file of this episode"),
            "F-M444: a second file of the same episode gets an attempt of its own");
        Check(coordinatorSrc.Contains("go up as singles after this pass"),
            "F-M442: the skip messages carry the real state of the season, not a guess");

        // ── F-M441: der Fill arbeitet über Dateien im Lauf-Ordner, nicht über den Speicher ────────
        Check(uploadSrc.Contains("_workFiles.TryRead(imdbId, season, episode, lang, hearingImpaired, position, out string? onDisk)"),
            "F-M441: the fill reads a text this run already extracted off the disk");
        Check(uploadSrc.Contains("_workFiles.Write(imdbId, season, episode, lang, hearingImpaired, pair.Key, pair.Value!)"),
            "F-M441: an extracted text is written to the run's working directory");
        Check(!uploadSrc.Contains("_fillTextCache") && !uploadSrc.Contains("_ownTexts"),
            "F-M441: no in-memory text cache and no persistent store");
        Check(!uploadSrc.Contains("Enumerable.Range(0, subtitleStreams.Count)"),
            "F-M441: the fill never extracts every subtitle track of the container");
        Check(uploadSrc.Contains("Api.RunWorkFiles.PurgeStale(null, TimeSpan.FromDays(2))"),
            "F-M441: the start of a run sweeps what a killed run left behind");
        Check(uploadSrc.Contains("_workFiles.Purge()"),
            "F-M441: the run removes its own working files when it ends");
        Check(!File.Exists(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/OwnTextStore.cs")),
            "F-M441: there is no persistent text store in the tree");
        var coordinatorSrc2 = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/SeasonPackCoordinator.cs"));
        Check(!coordinatorSrc2.Contains("_ownTexts"),
            "F-M441: the pack coordinator carries no store either");
        Check(!coordinatorSrc2.Contains("DeletePublishedSubtitleAsync(single.Id"),
            "F-M585 replaces F-M391: no single is deleted before the pack any more — SubDL allows both");
        Check(coordinatorSrc2.Contains("stay up as singles beside the pack", StringComparison.Ordinal)
                && coordinatorSrc2.Contains("CoveredByPack", StringComparison.Ordinal) == false,
            "F-M585: the pack names the singles that stay beside it instead of deleting them");

        // F-M467 (measured 11.11.2026 on VisionQuest tt23112594): the pack phase reads a season as
        // children of the folder it did not work this run — there Episode.Series is NULL, and every
        // per-file language check compared against a null id. The series is resolved BY ID now, and
        // the real helper is asked here with an episode that carries nothing but its series id.
        var idsOf = typeof(UploadPipeline).GetMethod("SeriesIdsOf",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Check(idsOf != null, "F-M467: the series-id helper exists");

        var loadedSeries = new MediaBrowser.Controller.Entities.TV.Series();
        loadedSeries.ProviderIds["Imdb"] = "tt23112594";
        loadedSeries.ProviderIds["Tmdb"] = "213375";
        Guid seriesId = Guid.NewGuid();

        var bare = new MediaBrowser.Controller.Entities.TV.Episode { SeriesId = seriesId };
        Func<Guid, MediaBrowser.Controller.Entities.BaseItem?> lookup = id => id == seriesId ? loadedSeries : null;
        var resolved = ((string? ImdbId, string? TmdbId))idsOf!.Invoke(null, new object?[] { bare, lookup })!;
        Check(resolved.ImdbId == "tt23112594" && resolved.TmdbId == "213375",
            "F-M467: an episode without a loaded series still resolves the SERIES ids — through its series id");

        var unresolved = ((string? ImdbId, string? TmdbId))idsOf!.Invoke(null, new object?[] { bare, null })!;
        Check(unresolved.ImdbId == null,
            "F-M467: without a lookup nothing is invented — a null stays a null");

        string uploadSrc2 = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Pipeline", "UploadPipeline.cs"));
        Check(uploadSrc2.Contains("series = ep.Series;", StringComparison.Ordinal)
              && uploadSrc2.IndexOf("series = ep.Series;", StringComparison.Ordinal)
                 < uploadSrc2.IndexOf("byId(ep.SeriesId)", StringComparison.Ordinal),
            "F-M467: a loaded series is used as it is — the lookup is the second witness, not the first");
        Check(uploadSrc2.Contains("var (seriesImdb, seriesTmdb) = SeriesIdsOf(ep, id => _libraryManager.GetItemById(id));", StringComparison.Ordinal),
            "F-M467: every id resolution goes through the one helper — the pack phase and the single path agree");

        // F-M468 (measured 11.11.2026, same season): the stream list is asked BY ID. A folder child has
        // no media sources, so its own list is empty — every language then answered "the file does not
        // carry it" although the files carried 25.
        Check(uploadSrc2.Contains("_mediaSourceManager.GetMediaStreams(item.Id)", StringComparison.Ordinal),
            "F-M468: the per-file language check asks the media source manager by id, not the item object");
        Check(uploadSrc2.IndexOf("_mediaSourceManager.GetMediaStreams(item.Id)", StringComparison.Ordinal)
                 < uploadSrc2.IndexOf("?? item.GetMediaStreams())", StringComparison.Ordinal),
            "F-M468: the object's own list is only the fallback for a manager that has nothing");

        // F-M469 (operator 11.11.2026, "aber wir fassen nur denselben release zusammen"): a pack carries
        // ONE release. The real helper is asked with the measured situation — eight episodes over three
        // encodes (NeoNoir 4, MeGusta 2, two renamed files without a release).
        var aired = new[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var split = new (string Group, IEnumerable<int> Episodes)[]
        {
            ("NeoNoir", new[] { 1, 2, 7, 8 }),
            ("MeGusta", new[] { 5, 6 }),
            (SeasonPack.UnknownGroup, new[] { 3, 4 }),
        };
        Check(SeasonPack.GroupCoveringSeason(aired, split) == null,
            "F-M469: a season split over three releases has no covering group — no pack");

        var whole = new (string Group, IEnumerable<int> Episodes)[]
        {
            ("NeoNoir", new[] { 1, 2, 7, 8 }),
            ("MeGusta", new[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
        };
        Check(SeasonPack.GroupCoveringSeason(aired, whole) == "MeGusta",
            "F-M469: the group that supplies every aired episode wins");

        var nameless = new (string Group, IEnumerable<int> Episodes)[]
        {
            (SeasonPack.UnknownGroup, new[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
        };
        Check(SeasonPack.GroupCoveringSeason(aired, nameless) == SeasonPack.UnknownGroup,
            "F-M469: files that state no release are their own group and never win over a named one");

        string uploadSrc3 = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Pipeline", "UploadPipeline.cs"));
        Check(uploadSrc3.Contains("if (SeasonPack.IsComplete(aired, entry.Value) && completeGroup != null)", StringComparison.Ordinal),
            "F-M469: being complete on disk is no longer enough — the group has to cover the season too");
        Check(uploadSrc3.Contains("a pack carries one release, stays single", StringComparison.Ordinal),
            "F-M469: the refusal says why it refused");

        // F-M472 (operator order 12.11.2026): the DOWNLOAD side gets the counterpart. An episode that
        // came out of a season pack hands the rest of the SAME release to the run loop, which works
        // them right after it. Only the same release counts, and completeness of the season is
        // explicitly NOT required this time.
        string dlPipeline = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Pipeline", "DownloadPipeline.cs"));
        Check(dlPipeline.Contains("RememberPackCover(item, season, episode, cand);", StringComparison.Ordinal),
            "F-M472: a save out of a pack records the cover for the run loop");
        Check(!dlPipeline.Contains("member.ReleaseName.Trim(), cand.ReleaseName.Trim()", StringComparison.Ordinal),
            "F-M472: no name comparison against the pack — the entry that matched IS the release");
        int handAt = dlPipeline.IndexOf("if (_packCover is { } cover)", StringComparison.Ordinal);
        string handBody = handAt < 0 ? string.Empty : dlPipeline[handAt..Math.Min(dlPipeline.Length, handAt + 2600)];
        Check(handAt > 0 && !handBody.Contains("GetItemList", StringComparison.Ordinal)
              && !handBody.Contains("InternalItemsQuery", StringComparison.Ordinal),
            "F-M472: only the run's own queue is re-ordered — the library is not asked for episodes");
        Check(dlPipeline.Contains("sibling.ParentIndexNumber == cover.Season", StringComparison.Ordinal),
            "F-M472: only the season the pack covers is pulled forward");
        Check(dlPipeline.Contains("items.InsertRange(pos + 1, pulled);", StringComparison.Ordinal),
            "F-M472: the other episodes are inserted right behind the one that was served");
        int coverAt = dlPipeline.IndexOf("private void RememberPackCover", StringComparison.Ordinal);
        string coverBody = coverAt < 0 ? string.Empty : dlPipeline[coverAt..Math.Min(dlPipeline.Length, coverAt + 3200)];
        Check(coverAt > 0 && !coverBody.Contains("IsComplete", StringComparison.Ordinal)
              && !coverBody.Contains("aired", StringComparison.Ordinal),
            "F-M472: no completeness check — whatever the release covers is served");
        Check(coverAt > 0 && !coverBody.Contains("_packCover = null;\n        if (cand.UnpackFiles.Count < 2)", StringComparison.Ordinal),
            "F-M472: a save from a single-file follow-up does not wipe the pack cover");
        // F-M474 (operator decision 13.11.2026): the ranking pays attention to the PRICE — nothing
        // that costs a download is taken while something free is on offer — and, at the same price,
        // a season pack beats a single because one fetch serves the whole season. The cache question
        // is asked by ENTRY ID, the address the download actually uses.
        int rankAt = dlPipeline.IndexOf("private long CandidateRank", StringComparison.Ordinal);
        string rankBody = rankAt < 0 ? string.Empty : dlPipeline[rankAt..Math.Min(dlPipeline.Length, rankAt + 2200)];
        Check(rankAt > 0 && !dlPipeline.Contains("private long PackPreference", StringComparison.Ordinal),
            "F-M474: the cost rank replaced the old pack-only preference");
        Check(rankBody.Contains("? (cached ? 4 : 2)", StringComparison.Ordinal)
              && rankBody.Contains("return cached ? 3 : 1;", StringComparison.Ordinal),
            "F-M474: four ranks — free/pack, free/single, paid/pack, paid/single");
        Check(rankBody.Contains("FindUnpackFile(season, episode) != null", StringComparison.Ordinal),
            "F-M474: a pack without this episode keeps rank 0 — it could not serve it at all");
        Check(dlPipeline.Contains("_api.IsEntryArchiveCached(c.NId)", StringComparison.Ordinal)
              && !dlPipeline.Contains("_api.IsArchiveCached(c.Url)", StringComparison.Ordinal),
            "F-M474: the cache is asked by entry id, the address the download uses");
        Check(dlPipeline.Split(".OrderByDescending(c => CandidateRank(c, season, episode))").Length - 1 >= 2
              && dlPipeline.Contains("Pack: CandidateRank(c, season, episode)", StringComparison.Ordinal),
            "F-M474: the same rank orders the main pick and both HI picks");
        Check(dlPipeline.Contains("for (int pos = 0; pos < items.Count; pos++)", StringComparison.Ordinal),
            "F-M472: the run loop is an index loop, because the hand-over inserts while it walks");
        Check(dlPipeline.Contains("covers {Season}x{First}-{Last}", StringComparison.Ordinal),
            "F-M472: the run says which stretch of the season it moved forward");

        // ---- F-M528: eine fremde Zahl kostet einen Treffer, nie den Lauf ------------------------
        // Befund 16.11.2026: `FormatException` aus SubtitleCandidate.FromJson beendete zwei
        // Download-Zyklen. GetInt32 wirft bei einem Zahlwert mit Nachkomma oder ausserhalb des
        // Int32-Bereichs. Geprueft wird jetzt, dass solche Werte GELESEN werden.
        string looseJson = """
        { "url": "/subtitle/1-2.zip", "release_name": "X", "language": "de",
          "season": 1.7, "episode": "3", "episode_from": 99999999999, "framerate": 1e3,
          "download_count": { "n": 5 } }
        """;
        using (var doc = System.Text.Json.JsonDocument.Parse(looseJson))
        {
            Jellyfin.Plugin.SubScribe.Api.SubtitleCandidate? cand = null;
            bool threw = false;
            try
            {
                cand = Jellyfin.Plugin.SubScribe.Api.SubtitleCandidate.FromJson(doc.RootElement);
            }
            catch (FormatException)
            {
                threw = true;
            }

            Check(!threw, "F-M528: a hit with foreign number shapes no longer throws");
            Check(cand != null && cand!.Season == 1,
                "F-M528: a number with a fraction (1.7) is truncated, not thrown away");
            Check(cand != null && cand!.Episode == 3,
                "F-M528: a number sent as a string (\"3\") is understood");
            Check(cand != null && cand!.EpisodeFrom == int.MaxValue,
                "F-M528: a number beyond the Int32 range is clamped instead of throwing");
            Check(cand != null && cand!.Framerate == 1000,
                "F-M528: exponent notation (1e3) is read");
            Check(cand != null && cand!.DownloadCount == 0,
                "F-M528: an object where a number belongs reads as 0, and the hit survives");
        }

        string subdlClient = File.ReadAllText(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Api", "ScribeApiClient.cs"));
        Check(subdlClient.Contains("JsonAnomalies.GetInt(el, name)", StringComparison.Ordinal),
            "F-M528: the shared int reader goes through the tolerant one");
        Check(subdlClient.Contains("catch (FormatException)", StringComparison.Ordinal)
              && subdlClient.Contains("JsonAnomalies.Take()", StringComparison.Ordinal),
            "F-M528: one bad hit is skipped AND the foreign value is written to the log");
        Check(!subdlClient.Contains("v.GetInt32();\n        }", StringComparison.Ordinal),
            "F-M528: no int-only read of an API payload is left in the search path");
        // F-M529: der Befund wird nicht gemeldet, sondern der Treffer faellt — und wird gezaehlt.
        Check(subdlClient.Contains("LastRejectedByFormat++", StringComparison.Ordinal),
            "F-M529: a hit whose field shape is not as documented is rejected");
        Check(subdlClient.Contains("LastJsonMismatches = befund", StringComparison.Ordinal),
            "F-M529: the rejected hits keep their sub and attribute for the log");
        string dlSource = File.ReadAllText(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Pipeline", "DownloadPipeline.cs"));
        Check(dlSource.Contains("summary.RejectedCandidates += _api.LastRejectedByFormat", StringComparison.Ordinal),
            "F-M529: the count reaches the run's closing line");

        // ---- F-M530: jedes Metadatenfeld robust, Meldung + droppen, kein Run-Fail ----------------
        // Operator-Anweisung 17.11.2026: „in jedem Metadatenfeld Robustheit gegen Falscheintrag.
        // Meldung und droppen, kein run fail." Geprueft wird die Tabelle, die das entscheidet.
        string sauber = """
        { "url": "/subtitle/1-2.zip", "release_name": "X", "language": "de", "season": 1, "episode": 2,
          "framerate": 2, "fps": "23.976", "hi": true, "full_season": false, "download_count": 12,
          "unpack_files": [ { "file_n_id": "abc", "season": 1, "episode": 2, "hi": false, "size": 100 } ] }
        """;
        using (var doc = System.Text.Json.JsonDocument.Parse(sauber))
        {
            var findings = Jellyfin.Plugin.SubScribe.Api.ScribeFieldSchema.Check(doc.RootElement);
            Check(findings.Count == 0,
                "F-M530: a documented entry — including numeric fps as TEXT — is not a finding");
            var ident = Jellyfin.Plugin.SubScribe.Api.ScribeFieldSchema.Identity(doc.RootElement);
            Check(ident.SubId == "1-2" && ident.Name == "X" && ident.Language == "DE",
                "F-M530: the finding can name its subtitle (id, release name, language)");
        }

        string falsch = """
        { "url": "/subtitle/9-9.zip", "release_name": "Y", "language": "de",
          "season": "3", "framerate": 23.98, "hi": "true", "author": ["a"],
          "unpack_files": [ { "file_n_id": "abc", "season": 1.5 } ] }
        """;
        using (var doc = System.Text.Json.JsonDocument.Parse(falsch))
        {
            var findings = Jellyfin.Plugin.SubScribe.Api.ScribeFieldSchema.Check(doc.RootElement);
            Check(findings.Count == 5,
                "F-M530: five wrong shapes are named (got " + findings.Count + ")");
            Check(findings.Any(f => f.Path == "season"),
                "F-M530: a number sent as text is a finding");
            Check(findings.Any(f => f.Path == "framerate"),
                "F-M530: a fractional framerate is a finding");
            Check(findings.Any(f => f.Path == "hi"),
                "F-M530: a flag sent as text is a finding");
            Check(findings.Any(f => f.Path == "author"),
                "F-M530: an array where a string belongs is a finding");
            Check(findings.Any(f => f.Path == "unpack_files[0].season"),
                "F-M530: the pack's own files are checked too, with their position named");

        }

        using (var doc = System.Text.Json.JsonDocument.Parse("{ \"unpack_files\": \"kaputt\" }"))
        {
            var findings = Jellyfin.Plugin.SubScribe.Api.ScribeFieldSchema.Check(doc.RootElement);
            Check(findings.Count == 1 && findings[0].Path == "unpack_files",
                "F-M530: a string where the pack array belongs is a finding, not a crash");
        }

        // ---- F-M531: die Meldung sagt nur, was verkehrt ist ------------------------------------
        var probe = new Jellyfin.Plugin.SubScribe.Api.FieldShapeFinding(
            "framerate", "framerate", "23.98", "Number", "a whole number");
        var text = Jellyfin.Plugin.SubScribe.Pipeline.MetadataMismatchReports.Build(
            probe, "3676577-8593549", "The.Odyssey.2026.720p.WEBRip", "en", "12.1.12.413", "konto");
        Check(text != null && text!.Category == Jellyfin.Plugin.SubScribe.Pipeline.BrokenSubtitleReports.CategoryMetadata,
            "F-M531: a format mismatch is filed as wrong release info");
        var zeilen = (text?.Comment ?? string.Empty).Split('\n');
        Check(zeilen.Length == 5 && zeilen[0] == "Metadata format mismatch.",
            "F-M531: the report opens with the finding and carries nothing else");
        Check(zeilen[1] == "Attribute: framerate (documented: a whole number, received: 23.98)",
            "F-M531: the attribute line names documented and received value");
        Check(zeilen[2] == "Subtitle: 3676577-8593549 ('The.Odyssey.2026.720p.WEBRip', EN)",
            "F-M531: the subtitle line names the affected sub");
        Check((text?.Comment ?? string.Empty).Contains("Automated report by Sub Scribe", StringComparison.Ordinal)
              && !(text?.Comment ?? string.Empty).Contains("because", StringComparison.OrdinalIgnoreCase)
              && !(text?.Comment ?? string.Empty).Contains("please", StringComparison.OrdinalIgnoreCase),
            "F-M531: attribution, and no story around it");

        // ---- F-M532: auch die anderen SubDL-Antworten werden geprueft ---------------------------
        using (var doc = System.Text.Json.JsonDocument.Parse(
            "{ \"imdb_id\": 123, \"tmdb_id\": 1368337 }"))
        {
            var f = Jellyfin.Plugin.SubScribe.Api.ScribeFieldSchema.CheckFilm(doc.RootElement);
            Check(f.Count == 1 && f[0].Path == "imdb_id",
                "F-M532: the film block is checked (a numeric imdb_id is a finding)");
        }

        using (var doc = System.Text.Json.JsonDocument.Parse(
            "{ \"usage\": { \"search\": { \"used\": 5, \"limit\": \"100\" }, \"downloads\": { \"used\": 1.5 } } }"))
        {
            var f = Jellyfin.Plugin.SubScribe.Api.ScribeFieldSchema.CheckQuota(doc.RootElement);
            Check(f.Count == 2
                  && f.Any(x => x.Path == "search.limit")
                  && f.Any(x => x.Path == "downloads.used"),
                "F-M532: the quota answer is checked, nested blocks named (got " + f.Count + ")");
        }

        using (var doc = System.Text.Json.JsonDocument.Parse(
            "{ \"subtitle\": { \"id\": \"3755405\", \"n_id\": \"abc\" } }"))
        {
            var f = Jellyfin.Plugin.SubScribe.Api.ScribeFieldSchema.CheckOwnEntry(doc.RootElement);
            Check(f.Count == 1 && f[0].Path == "subtitle.id",
                "F-M532: an own-list entry is checked (an id sent as text is a finding)");
        }

        using (var doc = System.Text.Json.JsonDocument.Parse(
            "{ \"subtitle\": { \"sd_id\": \"x\", \"subtitle_id\": \"9\" } }"))
        {
            var f = Jellyfin.Plugin.SubScribe.Api.ScribeFieldSchema.CheckUploadAnswer(doc.RootElement);
            Check(f.Count == 1 && f[0].Path == "subtitle.subtitle_id",
                "F-M532: the upload answer is checked too");
        }

        var gesammelt = new List<string>();
        Jellyfin.Plugin.SubScribe.Api.ScribeFieldSchema.OnMismatch = (f, ctx) => gesammelt.Add(ctx + ":" + f.Path);
        using (var doc = System.Text.Json.JsonDocument.Parse("{ \"imdb_id\": 7 }"))
        {
            bool gemeldet = Jellyfin.Plugin.SubScribe.Api.ScribeFieldSchema.Emit(
                Jellyfin.Plugin.SubScribe.Api.ScribeFieldSchema.CheckFilm(doc.RootElement), "search answer, film block");
            Check(gemeldet && gesammelt.Count == 1 && gesammelt[0] == "search answer, film block:imdb_id",
                "F-M532: a finding from another answer reaches the same reporter");
        }

        Jellyfin.Plugin.SubScribe.Api.ScribeFieldSchema.OnMismatch = null;

        // ── F-M643: der Staffel-Pack der ZWEITEN Quelle ─────────────────────────────────────────────
        // Der Sammler ist derselbe, der Versand nicht: tvsubtitles.net nimmt EINEN Zip in den
        // Staffel-Slot (episode=0), SubDL baut seinen Container aus Einzeldateien. Geprueft wird der
        // Behälter, den der Koordinator wirklich baut (ueber den echten Zip-Leser der Seite), und die
        // Verdrahtung im Lauf.
        Console.WriteLine("9. F-M643: a season pack for the second source");
        {
            var tvstCoord = File.ReadAllText(
                Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/SeasonPackCoordinator.cs"));
            var tvstPipe = File.ReadAllText(
                Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));

            Check(tvstCoord.Contains("public Func<TvPackTransfer, CancellationToken, Task<UploadResult>>? TvSubtitlesSender", StringComparison.Ordinal)
                  && tvstCoord.Contains("public sealed record TvPackTransfer(", StringComparison.Ordinal),
                "F-M643: the collector hands the container to a sender for the second source — the collection itself is shared");

            // Der echte Behälter: der Koordinator baut ihn in einer privaten Hilfsmethode, die Seite
            // liest die Folgen aus den Eintragsnamen. Beides wird hier wirklich ausgefuehrt.
            var zipMacher = typeof(SeasonPackCoordinator).GetMethod(
                "BuildZip",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Check(zipMacher != null, "F-M643: the container is built in one place");

            var mitglieder = new List<(string FileName, string Srt)>
            {
                ("tt1.S01E01.aaaa1111.EN.srt", "1\r\n00:00:01,000 --> 00:00:02,000\r\nA\r\n"),
                ("tt1.S01E02.bbbb2222.EN.srt", "1\r\n00:00:01,000 --> 00:00:02,000\r\nB\r\n"),
                ("tt1.S01E03.cccc3333.EN.SDH.srt", "1\r\n00:00:01,000 --> 00:00:02,000\r\nC\r\n"),
            };
            byte[] zip = (byte[])zipMacher!.Invoke(null, new object[] { mitglieder })!;
            var folgen = Jellyfin.Plugin.SubScribe.Api.TvSubtitlesClient.EpisodesInZip(zip);
            Check(zip.Length > 0 && folgen.SequenceEqual(new[] { 1, 2, 3 }),
                "F-M643: the entries are the episode file names, and the site's own reader finds every episode in them ("
                + string.Join(",", folgen) + ")");

            using (var gelesen = new System.IO.Compression.ZipArchive(new System.IO.MemoryStream(zip)))
            {
                Check(gelesen.Entries.Count == 3
                      && gelesen.Entries.All(e => e.FullName.EndsWith(".srt", StringComparison.Ordinal))
                      && gelesen.Entries.Select(e => e.FullName).SequenceEqual(mitglieder.Select(m => m.FileName)),
                    "F-M643: one entry per episode, named exactly like the single upload's file — nothing else rides along");
            }

            // Die Folgen-Dateinamen kommen aus derselben Funktion wie beim Einzel-Upload — mit Staffel,
            // weil die SEITE den Namen liest (SubDL-Mitglieder tragen ausdruecklich keine Staffel).
            Check(tvstCoord.Contains("var subdlFiles = ordered", StringComparison.Ordinal)
                  && tvstCoord.Contains("SeasonPack.MemberFileName(", StringComparison.Ordinal)
                  && tvstCoord.Contains("var tvstFiles = ordered", StringComparison.Ordinal)
                  && tvstCoord.Contains("SeasonPack.NeutralFileName(", StringComparison.Ordinal),
                "F-M643/F-M647: the plan carries BOTH namings — the tvst entries with the season, the SubDL members without it");

            // Der Versand: Ticket mit SeasonPack=true in den Staffel-Slot, Schalter der GUI, Quelle tvst.
            int packVersand = tvstPipe.IndexOf("private async Task<UploadResult> SendPackToTvSubtitlesAsync(", StringComparison.Ordinal);
            int packEnde = packVersand < 0 ? -1 : tvstPipe.IndexOf("private async Task<UploadResult> SendToTvSubtitlesAsync(", packVersand, StringComparison.Ordinal);
            string sender = packVersand < 0 || packEnde < 0 ? string.Empty : tvstPipe[packVersand..packEnde];
            Check(sender.Contains("SeasonPack: true", StringComparison.Ordinal)
                  && sender.Contains("TvstShowId: show.ShowId", StringComparison.Ordinal)
                  && sender.Contains("seasonPacks: _config.UploadSeasonPacks", StringComparison.Ordinal)
                  && sender.Contains("registry: Registry", StringComparison.Ordinal),
                "F-M643: the container goes as a pack ticket into the season slot, with the GUI's pack switch and the store");
            Check(sender.Contains("uebergabe.Hi", StringComparison.Ordinal)
                  && sender.Contains("DoubleEpisode: false", StringComparison.Ordinal),
                "F-M643: hi describes the WHOLE container — the collector builds one container per (language, variant), never a mixture");

            // F-M647: EIN Slot je Paar. Bis 12.2.0.8 standen hier zwei Phasen — erst
            // `PlanPacksFromStore(completeSeasons)`, danach ein zweiter Block mit
            // `PlanPacksFromStore(completeSeasons, source: TvSubtitlesSource)` und einer zweiten
            // `FillOnlyAsync`-Schleife. Jetzt gibt es EINE Paar-Schleife, EINEN Sammler und EINEN Slot.
            int tvstSlot = tvstPipe.IndexOf("var slotQuellen = new List<string> { PackEntity.SubdlSource };", StringComparison.Ordinal);
            Check(tvstSlot > 0
                  && tvstPipe.IndexOf("else if (!SourceAllowed(Configuration.SubtitleProvider.TvSubtitles))", tvstSlot, StringComparison.Ordinal) > tvstSlot
                  && tvstPipe.IndexOf("slotQuellen.Add(PackEntity.TvSubtitlesSource);", tvstSlot, StringComparison.Ordinal) > tvstSlot,
                "F-M643/F-M647: the second source joins the SAME slot, only when the run is a full run and the source may upload");

            int paarSchleife = tvstPipe.IndexOf("foreach (var (packImdb, packSeason, packLang, packHi) in packPaare)", StringComparison.Ordinal);
            int sammler = paarSchleife < 0 ? -1 : tvstPipe.IndexOf("FillAsync(packImdb, packSeason, packLang, packHi, slotQuellen, runCt)", paarSchleife, StringComparison.Ordinal);
            Check(paarSchleife > 0 && sammler > paarSchleife
                  && tvstPipe.IndexOf("PlanPacksFromStore(completeSeasons, sources: slotQuellen)", tvstSlot, StringComparison.Ordinal) > tvstSlot,
                "F-M647: ONE loop over the pairs, ONE collection per pair, and the plan asks every source of the slot");

            int whenAll = sammler < 0 ? -1 : tvstPipe.IndexOf("await Task.WhenAll(packLaeufe).ConfigureAwait(false);", sammler, StringComparison.Ordinal);
            Check(whenAll > sammler
                  && tvstPipe.IndexOf("return await packCoordinator.SendAsync(plan, quelle, runCt).ConfigureAwait(false);", sammler, StringComparison.Ordinal) > sammler
                  && tvstPipe.IndexOf("Random.Shared.Next(1, packVersatzMs + 1)", sammler, StringComparison.Ordinal) > sammler,
                "F-M647: both containers leave in the SAME slot, simultaneously (Task.WhenAll) and with the 0-5 % offset of F-M587");

            Check(!tvstPipe.Contains("FillOnlyAsync", StringComparison.Ordinal)
                  && tvstPipe.Split("PlanPacksFromStore(completeSeasons").Length - 1 == 1,
                "F-M647: the second pack phase is gone — no second plan, no second collector loop, no two sources in sequence");

            Check(tvstPipe.Contains("packCoordinator.TvSubtitlesSender = SendPackToTvSubtitlesAsync;", StringComparison.Ordinal)
                  && tvstPipe.Contains("geplantProStaffel[(packImdb, packSeason)]", StringComparison.Ordinal),
                "F-M643: the run wires the sender, and a tvst pair counts as the season's one line (F-M640)");
        }

        // ── 10. F-M647/F-M648/F-M649: EIN Slot je Paar, beide Quellen ------------------------------
        Console.WriteLine("10. F-M647: one slot per pair, both sources");
        {
            var quellen = new[] { "subdl", "tvsubtitles" };

            // Der Sammler laeuft EINMAL, auch wenn zwei Quellen im Slot stehen: acht Folgen, nicht
            // sechzehn — das ist der Unterschied zu den zwei Phasen von 12.2.0.8.
            var einmalFiller = new FakeFiller(refuse: Array.Empty<int>(), answer: -1);
            var einmalLog = new List<string>();
            var einmal = NewCoordinator(einmalLog, complete, packable, einmalFiller, dryConfig);
            var plan = einmal.FillAsync("tt1", 1, "EN", false, quellen, CancellationToken.None).GetAwaiter().GetResult();
            Check(plan != null && einmalFiller.Calls.Count == 8,
                "F-M647: the members are collected ONCE for both sources (8 episodes, not 16)");
            var zipNamen = new List<string>();
            if (plan?.TvstZip != null)
            {
                using var gelesen = new ZipArchive(new System.IO.MemoryStream(plan.TvstZip));
                zipNamen.AddRange(gelesen.Entries.Select(e => e.FullName));
            }

            Check(plan != null && plan!.SubdlFiles.Count == 8 && plan.TvstFiles.Count == 8
                  && plan.SubdlFiles[0].FileName != plan.TvstFiles[0].FileName
                  && zipNamen.SequenceEqual(plan.TvstFiles.Select(f => f.FileName)),
                "F-M648: ONE plan carries both namings AND the finished zip — both containers are built from the same members, before the slot");

            var fuerSubdl = einmal.SendAsync(plan!, Jellyfin.Plugin.SubScribe.Data.PackEntity.SubdlSource, CancellationToken.None).GetAwaiter().GetResult();
            var fuerTvst = einmal.SendAsync(plan!, Jellyfin.Plugin.SubScribe.Data.PackEntity.TvSubtitlesSource, CancellationToken.None).GetAwaiter().GetResult();
            Check(fuerSubdl.Outcome == SeasonPackCoordinator.PackSendOutcome.WouldSend
                  && fuerTvst.Outcome == SeasonPackCoordinator.PackSendOutcome.WouldSend
                  && einmalFiller.Calls.Count == 8,
                "F-M647: each source gets its own verdict from the SAME plan — sending does not collect again");

            // Ein Inhaltsurteil faellt EINMAL je Paar: gemischte Quellen, zwei Quellen im Slot, eine
            // Zeile — nicht zwei.
            var gemischtFiller = new FakeFiller(refuse: Array.Empty<int>(), answer: -1)
            {
                Source = episode => episode == 4 ? ("ATVP.WEB-DL", "23,809") : ("1080p.WEB-DL", "23,976"),
            };
            var gemischtLog = new List<string>();
            var gemischt = NewCoordinator(gemischtLog, complete, packable, gemischtFiller, dryConfig);
            var gemischtPlan = gemischt.FillAsync("tt1", 1, "EN", false, quellen, CancellationToken.None).GetAwaiter().GetResult();
            Check(gemischtPlan == null
                  && gemischtLog.Count(l => l.Contains("skipped before any extraction", StringComparison.Ordinal)) == 1,
                "F-M648: a content verdict is made ONCE per pair for both sources — one line, not one per source");

            // F-M649: das Stundenkontingent ist kein Urteil.
            var koord = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/SeasonPackCoordinator.cs"));
            int kontingent = koord.IndexOf("if (result.Verdict == UploadVerdict.NoVerdict)", StringComparison.Ordinal);
            int kontingentEnde = koord.IndexOf("if (!result.Ok && result.SourceAccepted)", StringComparison.Ordinal);
            string kontingentZweig = kontingent >= 0 && kontingentEnde > kontingent ? koord[kontingent..kontingentEnde] : string.Empty;
            int dublettenZweig = koord.IndexOf("\"duplicate-content\", StringComparison.Ordinal)", StringComparison.Ordinal);
            Check(kontingentZweig.Length > 0 && dublettenZweig > kontingent
                  && kontingentZweig.Contains("upload-hourly-cap", StringComparison.Ordinal)
                  && kontingentZweig.Contains("hourly budget spent", StringComparison.Ordinal)
                  && kontingentZweig.Contains("PackSendOutcome.LeftOpen", StringComparison.Ordinal)
                  && !kontingentZweig.Contains("Refuse(", StringComparison.Ordinal)
                  && !kontingentZweig.Contains("RecordPackRefusal", StringComparison.Ordinal)
                  && !kontingentZweig.Contains("_countFailed", StringComparison.Ordinal),
                "F-M649: the hourly cap writes NO verdict and no refusal — the pair stays open and is not counted as failed");

            // Die eine Zeile je Paar: beide Gruende in EINER Zeile, auf der Normal-Stufe.
            var pipeQuelle = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
            int teileAt = pipeQuelle.IndexOf("string.Join(\"; \", teile)", StringComparison.Ordinal);
            Check(teileAt > 0
                  && pipeQuelle.Contains("nothing to build — {Parts}", StringComparison.Ordinal)
                  && pipeQuelle.Contains("no container, no verdict — {Parts}", StringComparison.Ordinal)
                  && pipeQuelle.Contains("the next run takes it again (F-M649)", StringComparison.Ordinal),
                "F-M648/F-M649: BOTH reasons of a pair share ONE Normal line — and the quota case says the pair stays open");

            Check(pipeQuelle.Split("LogUtil.Normal(", StringSplitOptions.None).Length > 0
                  && pipeQuelle.IndexOf("packErgebnisse.Any(e => e.Delivered)", StringComparison.Ordinal)
                     < pipeQuelle.IndexOf("string.Join(\"; \", teile)", StringComparison.Ordinal),
                "F-M647: the slot answers from the OUTCOMES of both sends, not from a store row afterwards");
        }

        // ── 11. F-M650: eine stehende Ablehnung schliesst das Paar VOR dem Sammeln ──────────────────
        // Bis 12.2.0.9 wurde fuer ein abgelehntes Paar trotzdem jede Folge entpackt, nur um am Ende
        // „fingerprint unchanged, nothing is re-sent" zu melden: der Fingerabdruck einer Absage laesst
        // sich erst nach dem Sammeln vergleichen. Die Signatur der Staffel steht vorher fest — hier wird
        // mit einem ECHTEN Store geprueft, dass sie das Sammeln wirklich verhindert.
        Console.WriteLine("11. F-M650: a standing refusal closes the pair before the collection");
        {
            string store = Path.Combine(
                Path.GetTempPath(), "seasonpack-f650-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(store);
            try
            {
                const string Grund = "the source already holds this container (duplicate)";
                var f650Complete = new Dictionary<(string ImdbId, int Season), IReadOnlyList<int>>
                {
                    [("tt650", 2)] = Enumerable.Range(1, 8).ToList(),
                };
                var f650Packable = new HashSet<(string ImdbId, int Season)> { ("tt650", 2) };

                using (var db = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(store, null))
                {
                    var reg = new Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry(db);

                    // (a) Die Absage traegt die Signatur, auf der sie beruht.
                    reg.RecordPackRefusal(
                        "tt650", 2, "EN", false, Grund, "fingerprint-of-the-pack",
                        Jellyfin.Plugin.SubScribe.Data.PackEntity.SubdlSource, signature: "sig-A");
                    var zeile = reg.GetPack("tt650", 2, "EN", false, Jellyfin.Plugin.SubScribe.Data.PackEntity.SubdlSource);
                    Check(zeile?.RefusedSignature == "sig-A",
                        "F-M650: a refusal carries the season signature it is based on",
                        zeile?.RefusedSignature ?? "(keine Zeile)");

                    // Die Frage selbst: gleiche Signatur = ja, andere oder unbekannte = nein.
                    Check(reg.PackRefusalStands("tt650", 2, "EN", false, Jellyfin.Plugin.SubScribe.Data.PackEntity.SubdlSource, "sig-A")
                          && !reg.PackRefusalStands("tt650", 2, "EN", false, Jellyfin.Plugin.SubScribe.Data.PackEntity.SubdlSource, "sig-B")
                          && !reg.PackRefusalStands("tt650", 2, "EN", false, Jellyfin.Plugin.SubScribe.Data.PackEntity.SubdlSource, null),
                        "F-M650: the refusal stands on the SAME signature only — a changed or unknown one does not");

                    // (b) Gleiche Signatur: KEIN Sammelaufruf, genau EINE Zeile mit beiden Angaben.
                    var frageFiller = new FakeFiller(refuse: Array.Empty<int>(), answer: -1);
                    var frageLog = new List<string>();
                    var frage = NewCoordinator(frageLog, f650Complete, f650Packable, frageFiller, dryConfig, reg);
                    frage.SeasonSignature = (_, _) => "sig-A";
                    var fragePlan = frage
                        .FillAsync("tt650", 2, "EN", false, new[] { Jellyfin.Plugin.SubScribe.Data.PackEntity.SubdlSource }, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    Check(fragePlan == null && frageFiller.Calls.Count == 0,
                        "F-M650: a refusal with an unchanged signature extracts NOTHING — the filler is never asked (0 calls)",
                        frageFiller.Calls.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

                    var f650Zeilen = frageLog.Where(l => l.Contains("F-M650", StringComparison.Ordinal)).ToList();
                    Check(f650Zeilen.Count == 1
                          && f650Zeilen[0].Contains("no source left open", StringComparison.Ordinal)
                          && f650Zeilen[0].Contains("subdl refused", StringComparison.Ordinal)
                          && f650Zeilen[0].Contains("duplicate upload", StringComparison.Ordinal)
                          && f650Zeilen[0].Contains("signature unchanged", StringComparison.Ordinal)
                          && f650Zeilen[0].Contains("nothing is extracted", StringComparison.Ordinal),
                        "F-M650: ONE line names the pair and the source with its reason, date and the unchanged signature",
                        f650Zeilen.Count == 1 ? f650Zeilen[0] : f650Zeilen.Count + " lines");
                    Check(frageLog.Count(l => l.Contains("season pack", StringComparison.Ordinal)) == 1
                          && string.IsNullOrEmpty(frage.LastSkipReason),
                        "F-M650: the pair reports itself ONCE — the line is already written, the pipeline adds none (F-M648)");

                    // (c) Andere Signatur (neue Folge / neue Datei): es wird wieder gesammelt.
                    var neuFiller = new FakeFiller(refuse: Array.Empty<int>(), answer: -1);
                    var neuLog = new List<string>();
                    var neu = NewCoordinator(neuLog, f650Complete, f650Packable, neuFiller, dryConfig, reg);
                    neu.SeasonSignature = (_, _) => "sig-B";
                    var neuPlan = neu
                        .FillAsync("tt650", 2, "EN", false, new[] { Jellyfin.Plugin.SubScribe.Data.PackEntity.SubdlSource }, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    Check(neuPlan != null && neuFiller.Calls.Count == 8,
                        "F-M650: a CHANGED signature is collected again — all 8 episodes are asked for",
                        neuFiller.Calls.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

                    // (d) Der GUI-Reset (F-M620) raeumt die Signatur mit weg — sonst bliebe der Pack fuer
                    // immer geschlossen.
                    reg.EnsureMedia("ffffffffffffffffffffffffffffffff", patch: m =>
                    {
                        m.ImdbId = "tt650";
                        m.Season = 2;
                        m.IsSeries = true;
                    });
                    int geoeffnet = reg.ReopenPackRefusals("ffffffffffffffffffffffffffffffff");
                    Check(geoeffnet >= 1
                          && reg.GetPack("tt650", 2, "EN", false, Jellyfin.Plugin.SubScribe.Data.PackEntity.SubdlSource)?.RefusedSignature == null
                          && !reg.PackRefusalStands("tt650", 2, "EN", false, Jellyfin.Plugin.SubScribe.Data.PackEntity.SubdlSource, "sig-A"),
                        "F-M650: the GUI reset clears the signature with the refusal — the pack is open again",
                        geoeffnet.ToString(System.Globalization.CultureInfo.InvariantCulture));

                    // (e) Inhalt gleich, aber die Quelle hat den Behälter inzwischen: HasPack gewinnt.
                    reg.RecordPackRefusal(
                        "tt650", 2, "EN", false, Grund, "fingerprint-of-the-pack",
                        Jellyfin.Plugin.SubScribe.Data.PackEntity.SubdlSource, signature: "sig-A");
                    reg.RecordPack(
                        "tt650", 2, "EN", false, "Show.S02.1080p", "tt650.S02.x.EN",
                        new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, null, "fingerprint-of-the-pack");
                    var hatFiller = new FakeFiller(refuse: Array.Empty<int>(), answer: -1);
                    var hatLog = new List<string>();
                    var hat = NewCoordinator(hatLog, f650Complete, f650Packable, hatFiller, dryConfig, reg);
                    hat.SeasonSignature = (_, _) => "sig-A";
                    var hatPlan = hat
                        .FillAsync("tt650", 2, "EN", false, new[] { Jellyfin.Plugin.SubScribe.Data.PackEntity.SubdlSource }, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    Check(hatPlan == null
                          && hatFiller.Calls.Count == 0
                          && hatLog.Count(l => l.Contains("F-M647", StringComparison.Ordinal)) == 1
                          && hatLog.All(l => !l.Contains("F-M650", StringComparison.Ordinal)),
                        "F-M650: a container that is up wins over the refusal — the HasPack gate answers, once (F-M647)");

                // Die Verdrahtung: ohne die Signatur am Aufruf bliebe `RefusedSignature` leer und das Tor
                // koennte ein Paar nie schliessen — der Test oben ruft den Store direkt, dieser hier
                // prueft den Weg, den der Koordinator wirklich geht.
                var koord650 = File.ReadAllText(
                    Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/SeasonPackCoordinator.cs"));
                int ruf = koord650.IndexOf("_registry.RecordPackRefusal(", StringComparison.Ordinal);
                string rufStelle = ruf < 0 ? string.Empty : koord650[ruf..Math.Min(koord650.Length, ruf + 400)];
                Check(ruf > 0 && rufStelle.Contains("signature: SeasonSignature?.Invoke", StringComparison.Ordinal),
                    "F-M650: the refusal call hands the season signature over — without it the gate could never close a pair");
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(store, true);
                }
                catch (IOException)
                {
                    // Ein liegengebliebener Probe-Store ist kein Pruefergebnis.
                }
            }
        }

        Console.WriteLine(Failures.Count == 0
            ? $"seasonpack: {_checks} checks, all green."
            : $"seasonpack: {_checks} checks, {Failures.Count} failed.");
        return Failures.Count == 0 ? 0 : 1;
    }
}
