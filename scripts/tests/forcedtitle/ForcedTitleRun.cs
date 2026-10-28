// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. SubDL Scribe is distributed WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR
// PURPOSE. See the GNU General Public License for more details.
//
// T159 / F-M367: a forced track whose FLAG is unset but whose TITLE says "Forced"
// must not make its language present.
//
// FOUR THINGS ARE ASSERTED, all executed against the shipped code:
//
//   1. THE TITLE IS A SOURCE. `IsForcedStream` must answer true for a track titled
//      "Forced" with `IsForced` false — the measured case (`The.Deputy.2026…mkv`, `eng`
//      titled `Forced`, `DISPOSITION:forced=0`) that ran as "only the variant is open:
//      EN (HI), DE (HI)" on 24.10. and 25.10.2026 and was never searched for.
//   2. WORD BOUNDARY, NOT SUBSTRING. `enforced`, `unforced` and a plain title are NOT
//      forced markers — the same discipline `cc` gets in the HI predicate, where `Occitan`
//      must not read as hearing-impaired.
//   3. THE CHAIN FOLLOWS. `IsDialogueStream` must reject the track, `EmbeddedPresentLanguages`
//      must not list its language, and `SubtitleCoverage` + `SubtitleRef.Required` must leave
//      the language OPEN so the download pipeline searches for it. A green predicate with a
//      language still reported as present would be the bug, not the fix.
//   4. THE WIRING IS ON THE SOURCE. The predicate must read `Title` and carry the word-boundary
//      regex; the old single expression `stream.IsForced == true` must be gone.
//
// Usage: dotnet publish Jellyfin.Plugin.SubdlSync -c Release -o out
//        dotnet run --project scripts/tests/forcedtitle/forcedtitle.csproj [repo-root]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Registry;
using MediaBrowser.Model.Entities;

namespace ForcedTitleRun;

internal static class Program
{
    private static int _checks;
    private static readonly List<string> Failures = new();

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (!ok)
        {
            Failures.Add(what);
            Console.WriteLine("  FAIL  " + what);
        }
    }

    private static MediaStream Sub(string language, string? title, bool forced = false, bool text = true)
        => new()
        {
            Type = MediaStreamType.Subtitle,
            Codec = text ? "subrip" : "pgssub",   // IsTextSubtitleStream is derived from the codec
            Language = language,
            Title = title,
            IsForced = forced,
        };

    private static int Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";

        // ── 1. The measured case ────────────────────────────────────────────────
        Console.WriteLine("1. Title is a source (The Deputy, forced=0)");
        var deputyEn = Sub("eng", "Forced", forced: false);
        var deputyDe = Sub("ger", null, forced: false);
        Check(SidecarNaming.IsForcedStream(deputyEn), "eng titled 'Forced' with forced=0 is forced");
        Check(!SidecarNaming.IsForcedStream(deputyDe), "ger without title/flag is not forced");
        Check(!SidecarNaming.IsDialogueStream(deputyEn), "IsDialogueStream rejects the forced-titled track");
        Check(SidecarNaming.IsDialogueStream(deputyDe), "IsDialogueStream keeps the plain ger track");

        // ── 2. Word boundary, not substring ─────────────────────────────────────
        Console.WriteLine("2. Word boundary");
        foreach (var t in new[] { "Forced", "forced", "FORCED", "Forced (SDH)", "Eng forced", "English - Forced" })
        {
            Check(SidecarNaming.IsForcedStream(Sub("eng", t, forced: false)), $"title '{t}' reads as forced");
        }

        foreach (var t in new[] { "enforced", "unforced", "full", "English", "SDH", "" })
        {
            Check(!SidecarNaming.IsForcedStream(Sub("eng", t, forced: false)), $"title '{t}' is NOT a forced marker");
        }

        Check(!SidecarNaming.IsForcedStream(Sub("eng", null, forced: false)), "no title, flag false → not forced");
        Check(SidecarNaming.IsForcedStream(Sub("eng", null, forced: true)), "flag true, no title → forced");
        Check(!SidecarNaming.IsForcedStream(null!), "null stream → not forced");
        Check(!SidecarNaming.IsDialogueStream(Sub("eng", null, text: false)), "bitmap track is no dialogue either (F-M257)");

        // ── 3. The chain follows ────────────────────────────────────────────────
        Console.WriteLine("3. Language presence and coverage");
        var streams = new List<MediaStream> { deputyEn, deputyDe };
        var present = SidecarNaming.EmbeddedPresentLanguages(streams);
        Check(!present.Contains("eng") && !present.Contains("EN"), "EN is NOT present from the forced-titled track");
        Check(present.Contains("ger") || present.Contains("DE"), "DE stays present from the plain ger track");

        var coverage = SubtitleCoverage.Read(null, "/kein/solcher/pfad/The.Deputy.mkv", present);
        var open = coverage.Open(SubtitleRef.Required(new[] { "EN", "DE" }, false));
        Check(open.Any(r => r.Language == "EN"), "EN is OPEN — the download pipeline will search for it");
        Check(!open.Any(r => r.Language == "DE"), "DE is closed — nothing to fetch");

        var forcedObservation = SubtitleCoverage.FromPairs(new[] { new SubtitleRef("EN", false, true) });
        Check(!forcedObservation.Covers(new SubtitleRef("EN", false)), "a forced datum never covers the regular (F-M284)");

        // ── 4. Wiring on the source ──────────────────────────────────────────────
        Console.WriteLine("4. Source contract");
        var src = Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Registry/SidecarNaming.cs");
        var text2 = File.ReadAllText(src);
        var start = text2.IndexOf("public static bool IsForcedStream", StringComparison.Ordinal);
        Check(start >= 0, "IsForcedStream exists");
        var body = start < 0 ? string.Empty : text2.Substring(start, Math.Min(1200, text2.Length - start));
        Check(body.Contains("stream.Title", StringComparison.Ordinal), "the predicate reads the track title");
        Check(body.Contains("(?<![a-z])forced(?![a-z])", StringComparison.Ordinal),
            "the marker is matched on a word boundary");
        Check(!text2.Contains("=> stream != null && stream.IsForced == true;", StringComparison.Ordinal),
            "the old flag-only expression is gone");

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? $"forcedtitle: {_checks} checks, all green."
            : $"forcedtitle: {_checks} checks, {Failures.Count} FAILED.");
        return Failures.Count == 0 ? 0 : 1;
    }
}
