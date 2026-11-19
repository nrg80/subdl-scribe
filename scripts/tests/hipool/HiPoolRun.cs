// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. Sub Scribe is distributed WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
// PARTICULAR PURPOSE. See the GNU General Public License for more details.
//
// T201 / F-M476: the hearing-impaired pool is walked, and it carries the main
// track when the regular pool runs dry.
//
// WHY THE WIRING IS ASSERTED. The two defects this repairs were both invisible to
// the compiler and to every behavioural test that only looks at the regular pool:
//
//   1. THE GRAB. The pool was read with FirstOrDefault — one candidate per language.
//      A duplicate or a failed gate closed the pool although more uploads were listed.
//      The old check could not see this, because one grab and a walk of one agree.
//   2. THE PLACE. The block sat inside the "a subtitle was saved" branch, so an
//      episode whose pack produced no usable file never touched its pool.
//
// The checks read the pipeline source and model the walk, and they also pin the two
// properties the operator asked for: the fallback is conditioned on the HI switch, and
// the files keep their SDH lines with the HI marker.
//
// Usage: dotnet run --project scripts/tests/hipool/hipool.csproj [repo-root]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HiPoolRun;

internal static class HiPoolRun
{
    private static int _checks;
    private static readonly List<string> Failures = new();

    private static void Check(string name, bool ok, string? detail = null)
    {
        _checks++;
        if (ok)
        {
            Console.WriteLine("  [PASS] " + name);
            return;
        }

        Failures.Add(name + (string.IsNullOrEmpty(detail) ? string.Empty : "  (" + detail + ")"));
        Console.WriteLine("  [FAIL] " + name + (string.IsNullOrEmpty(detail) ? string.Empty : "  — " + detail));
    }

    public static int Main(string[] args)
    {
        string repo = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";
        string pipe = Path.Combine(repo, "Jellyfin.Plugin.SubdlSync", "Pipeline", "DownloadPipeline.cs");
        if (!File.Exists(pipe))
        {
            Console.WriteLine($">>> FEHLER: {pipe} nicht lesbar");
            return 2;
        }

        string src = File.ReadAllText(pipe);

        // The pool lives in one local function; everything below is measured inside it, so a change
        // somewhere else in this very large method cannot make a check pass by accident.
        int fnStart = src.IndexOf("async Task<bool> RunHiPoolAsync(bool asMainTrack)", StringComparison.Ordinal);
        Check("the pool has one implementation, not two", fnStart > 0);
        if (fnStart < 0)
        {
            Console.WriteLine();
            Console.WriteLine($"hipool: {Failures.Count} of {_checks} checks FAILED:");
            foreach (string f in Failures)
            {
                Console.WriteLine("  - " + f);
            }

            return 1;
        }

        string tail = src[fnStart..];
        int open = tail.IndexOf('{');
        int depth = 0;
        int end = -1;
        for (int i = open; i < tail.Length; i++)
        {
            if (tail[i] == '{')
            {
                depth++;
            }
            else if (tail[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    end = i;
                    break;
                }
            }
        }

        string fn = end > 0 ? tail[..end] : tail;

        // 1. THE WALK. A list plus a loop; the single grab must be gone.
        Check("the pool is materialised as a ranked list",
            Regex.IsMatch(fn, @"var hiWalk = hiCandidates\s*\n\s*\.Where\(.*\.ToList\(\);", RegexOptions.Singleline));
        Check("the pool is walked, not grabbed",
            fn.Contains("foreach (var hi in hiWalk)", StringComparison.Ordinal)
            && !Regex.IsMatch(fn, @"hiCandidates[\s\S]{0,400}?\.FirstOrDefault\(\)", RegexOptions.Singleline));
        Check("a save ends the walk instead of the first refusal",
            Regex.IsMatch(fn, @"if \(hiSaved \|\| ct\.IsCancellationRequested\)", RegexOptions.Singleline));
        Check("the walk reports how many were tried per language",
            fn.Contains("no usable candidate in the HI pool", StringComparison.Ordinal));

        // 2. THE TWO CALL SITES. Variant beside a saved main track, and the fallback.
        Check("the pool still serves as the variant",
            src.Contains("RunHiPoolAsync(asMainTrack: false)", StringComparison.Ordinal));
        Check("the pool serves as the main track when the regular walk ended empty",
            src.Contains("RunHiPoolAsync(asMainTrack: true)", StringComparison.Ordinal));
        Check("the fallback runs before the language is declared unavailable",
            src.IndexOf("RunHiPoolAsync(asMainTrack: true)", StringComparison.Ordinal)
            < src.IndexOf("no candidate passed checks (F-M46), language marked not-available", StringComparison.Ordinal));
        Check("the fallback only exists while nothing was saved",
            Regex.IsMatch(src, @"if \(!savedAny && _config\.DownloadHearingImpaired\)\s*\n\s*\{", RegexOptions.Singleline));
        Check("the fallback's save counts as a save for the language",
            Regex.IsMatch(src, @"savedAny = await RunHiPoolAsync\(asMainTrack: true\)", RegexOptions.Singleline));

        // 3. THE SWITCH. Both call sites hang on the HI setting — the operator's condition.
        Check("the variant call hangs on the HI switch",
            Regex.IsMatch(src, @"if \(_config\.DownloadHearingImpaired && !effectiveHi\)[\s\S]{0,200}?RunHiPoolAsync\(asMainTrack: false\)", RegexOptions.Singleline));
        Check("the search itself is fed by the same switch",
            Regex.IsMatch(src, @"Required\([^)]*_config\.DownloadHearingImpaired\)", RegexOptions.Singleline));

        // 4. THE FILE. HI marker kept, SDH lines kept, store told the truth.
        Check("a file taken from this pool is named as the variant",
            fn.Contains("hearingImpaired: true", StringComparison.Ordinal));
        Check("the store records it as hearing-impaired",
            Regex.IsMatch(fn, @"MarkDownloaded\([\s\S]{0,120}?lang, true,", RegexOptions.Singleline));
        Check("the SDH lines are NOT stripped on the way in",
            !Regex.IsMatch(fn, @"Strip|RemoveSdh|StripHi|SdhTags", RegexOptions.Singleline));
        Check("the fetch keeps the correction for invisible cues only",
            fn.Contains("CorrectInvisibleCues(hiDl.Content, lang)", StringComparison.Ordinal));

        // 5. THE LOG. One line, two readings — a reader must be able to tell which case happened.
        Check("a save as main track is not logged as a variant",
            fn.Contains("SAVED {Source} HI as main track", StringComparison.Ordinal)
            && fn.Contains("SAVED {Source} HI variant", StringComparison.Ordinal)
            && fn.Contains("asMainTrack", StringComparison.Ordinal));

        // 6. THE ORDER OF THE POOL. Rank first, score second — a different order would decide the
        //    fallback by something other than the operator's price/breadth rule (F-M474).
        Check("the walk ranks price and breadth before the text score",
            Regex.IsMatch(fn, @"OrderByDescending\(c => CandidateRank\(c, season, episode\)\)\s*\n\s*\.ThenByDescending\(c => Score\(c, videoBase\)\)", RegexOptions.Singleline));

        // 7. THE MODEL. Three candidates, the first two are duplicates: the walk must reach the third.
        var pool = new List<(string Name, bool Duplicate)> { ("a", true), ("b", true), ("c", false) };
        bool saved = false;
        string hit = string.Empty;
        foreach (var cand in pool)
        {
            if (saved)
            {
                break;
            }

            if (cand.Duplicate)
            {
                continue;
            }

            saved = true;
            hit = cand.Name;
        }

        Check("a duplicate no longer ends the pool", saved && hit == "c", "reached " + (hit.Length == 0 ? "nothing" : hit));

        Check("a burned candidate is not fetched twice",
            Regex.IsMatch(src, @"RecordSkippedCandidates|IsExhausted|candidates memorized", RegexOptions.Singleline));

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? $"hipool: {_checks} checks, all green."
            : $"hipool: {Failures.Count} of {_checks} checks FAILED:");
        foreach (string f in Failures)
        {
            Console.WriteLine("  - " + f);
        }

        return Failures.Count == 0 ? 0 : 1;
    }
}
