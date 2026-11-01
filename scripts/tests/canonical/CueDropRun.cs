// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// T164 / F-M382: a cue that is too short to be seen is DROPPED, not used to convict the file.
//
// Measured 01.11.2026 on MobLand S01E02: a doubled "♪ MTV ♪" line at 00:00:03,898 with a 1 ms
// duration made the whole 1040-cue file fail qa-invalid-srt, which cost the ENTIRE season pack —
// 9 of 10 episodes were left, so every episode went up as a single and E02 not at all.
//
// Usage: dotnet run --project canonical.csproj

using System;
using Jellyfin.Plugin.SubdlScribe.Registry;

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
    if (!ok) failures++;
}

Console.WriteLine("1. A 1 ms cue is dropped, the rest renumbered");
string withTiny = "1\n00:00:03,898 --> 00:00:03,899\n♪ MTV ♪\n\n2\n00:00:10,000 --> 00:00:12,000\nHello\n\n3\n00:00:13,000 --> 00:00:15,000\nWorld\n";
string dropped = ContentHashRegistry.NormalizeSrt(withTiny);
Check(!dropped.Contains("MTV"), "the invisible cue is gone");
Check(dropped.Contains("1\n00:00:10,000 --> 00:00:12,000\nHello"), "the first survivor is renumbered to 1");
Check(dropped.Contains("2\n00:00:13,000 --> 00:00:15,000\nWorld"), "and the second to 2");
Check(ContentHashRegistry.NormalizeSrt(dropped) == dropped, "dropping twice changes nothing");

Console.WriteLine("2. A clean file is untouched — byte for byte");
string clean = "1\n00:00:01,000 --> 00:00:02,000\nA\n\n2\n00:00:03,000 --> 00:00:04,000\nB";
Check(ContentHashRegistry.NormalizeSrt(clean) == clean, "no drop, no rewrite, no new hash");

Console.WriteLine("3. The threshold is the visible one");
string atThreshold = "1\n00:00:01,000 --> 00:00:01,100\nEdge\n\n2\n00:00:03,000 --> 00:00:05,000\nB";
string justAbove = "1\n00:00:01,000 --> 00:00:01,101\nEdge\n\n2\n00:00:03,000 --> 00:00:05,000\nB";
Check(!ContentHashRegistry.NormalizeSrt(atThreshold).Contains("Edge"), "100 ms is still invisible");
Check(ContentHashRegistry.NormalizeSrt(justAbove).Contains("Edge"), "101 ms is kept");

Console.WriteLine("4. The hash follows the dropped text");
Check(ContentHashRegistry.ComputeHash(withTiny) == ContentHashRegistry.ComputeHash(dropped),
    "hashing the raw and the dropped form agrees — the payload and the hash describe one text");
Check(ContentHashRegistry.ComputeHash(clean) != ContentHashRegistry.ComputeHash(dropped),
    "different files still hash differently");

Console.WriteLine();
Console.WriteLine(failures == 0 ? "canonical: all green." : $"canonical: {failures} FAILED.");
return failures == 0 ? 0 : 1;
