// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// F-M296 test driver: the two halves of the auto-sync that do NOT need a media
// file or an audio decode — the audio-track choice and the constant shift.
//
// WHY THESE TWO ARE TESTED WITHOUT AUDIO
//
// The audio decode is the expensive and environment-dependent part (ffmpeg,
// codecs). Everything that decides WHAT gets written is pure arithmetic and must
// therefore be testable exactly. The failure that matters most here is not a
// wrong number but a silently broken FILE, so the shift tests assert the
// integrity properties, not only the times:
//
//   1. Track choice   — the priority rule, including the 2-vs-3-letter trap that
//                       once produced "no track matches: 252 of 304" as a pure
//                       artefact. A rule that never matches anything looks
//                       perfectly plausible in a log line.
//   2. Shift applied  — a planted constant shift must come back exactly, the text
//                       must be untouched, cue count unchanged, order unchanged.
//   3. Shift refused  — a shift that would push the first cue below zero is
//                       REFUSED, never clamped (clamping one cue would change its
//                       relation to its neighbour while the rest of the file
//                       still moves).
//   4. Byte style     — the corrected file must carry the same BOM/CRLF shape as
//                       the payload it came from.
//   5. The suffix     — "<...>.srt.unsynchronized" must NOT match the sidecar
//                       glob (baseName + "*.srt"), while the swapped order would.
//                       This is asserted against the real pattern, because the
//                       swapped form silently invents a language.
//
// Run: dotnet run --project scripts/tests/autosync
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Qa;
using MediaBrowser.Model.Entities;

namespace AutoSyncTest;

/// <summary>Test driver for <see cref="AudioTrackChoice"/> and <see cref="SubtitleSync"/>.</summary>
public static class AutoSyncRun
{
    /// <summary>Entry point.</summary>
    /// <param name="args">Unused.</param>
    /// <returns>0 when every case passed, 1 otherwise.</returns>
    public static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "real")
        {
            return RealAutoSync.RunAsync(args).GetAwaiter().GetResult();
        }

        Console.WriteLine("=== F-M296 auto-sync — track choice and constant shift ===");
        Console.WriteLine();
        int failures = 0;

        failures += TrackChoice();
        failures += ShiftApplied();
        failures += ShiftRefused();
        failures += ByteStyle();
        failures += UnsyncSuffix();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "ALL CASES PASSED"
            : $"{failures} CASE(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static MediaStream Audio(string? lang) => new()
    {
        Type = MediaStreamType.Audio,
        Language = lang,
        Index = 0
    };

    private static void Check(string label, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}: {detail}");
    }

    /// <summary>Case 1: the priority rule, and the 2-vs-3-letter matching.</summary>
    private static int TrackChoice()
    {
        int f = 0;
        Console.WriteLine("[1] audio track choice");

        // The core claim: 2-letter subtitle code against 3-letter container tags.
        Check("en matches eng", AudioTrackChoice.Canonical("eng") == "en", "eng -> en");
        f += AudioTrackChoice.Canonical("eng") == "en" ? 0 : 1;
        Check("de matches deu/ger",
            AudioTrackChoice.Canonical("deu") == "de" && AudioTrackChoice.Canonical("ger") == "de",
            "deu/ger -> de");
        f += AudioTrackChoice.Canonical("deu") == "de" && AudioTrackChoice.Canonical("ger") == "de" ? 0 : 1;
        Check("zh matches zho/chi",
            AudioTrackChoice.Canonical("zho") == "zh" && AudioTrackChoice.Canonical("chi") == "zh",
            "zho/chi -> zh");
        f += AudioTrackChoice.Canonical("zho") == "zh" && AudioTrackChoice.Canonical("chi") == "zh" ? 0 : 1;
        Check("und is not a language", AudioTrackChoice.Canonical("und") == string.Empty, "und -> (empty)");
        f += AudioTrackChoice.Canonical("und") == string.Empty ? 0 : 1;
        Check("null is not a language", AudioTrackChoice.Canonical(null) == string.Empty, "null -> (empty)");
        f += AudioTrackChoice.Canonical(null) == string.Empty ? 0 : 1;

        // Prio 1: the subtitle's own language wins even when it is not first.
        var itaEng = new List<MediaStream> { Audio("ita"), Audio("eng") };
        var c1 = AudioTrackChoice.Choose(itaEng, "en");
        Check("Prio 1 beats track order", c1.Position == 1 && c1.Priority == 1,
            $"ita,eng with sub=en -> track {c1.Position}, prio {c1.Priority}");
        f += c1.Position == 1 && c1.Priority == 1 ? 0 : 1;

        // Prio 2: no track in the subtitle's language, but English is there.
        var c2 = AudioTrackChoice.Choose(itaEng, "de");
        Check("Prio 2 English fallback", c2.Position == 1 && c2.Priority == 2,
            $"ita,eng with sub=de -> track {c2.Position}, prio {c2.Priority}");
        f += c2.Position == 1 && c2.Priority == 2 ? 0 : 1;

        // Prio 3: neither matches -> the first untagged track.
        var rusRus = new List<MediaStream> { Audio("rus"), Audio("rus"), Audio("rus"), Audio("rus"), Audio("eng") };
        var c3 = AudioTrackChoice.Choose(rusRus, "de");
        Check("Prio 2 over a long Russian run", c3.Position == 4 && c3.Priority == 2,
            $"4x rus + eng, sub=de -> track {c3.Position}, prio {c3.Priority}");
        f += c3.Position == 4 && c3.Priority == 2 ? 0 : 1;

        var untagged = new List<MediaStream> { Audio("und"), Audio("ita") };
        var c4 = AudioTrackChoice.Choose(untagged, "de");
        Check("Prio 3 prefers an untagged track over a foreign one",
            c4.Position == 0 && c4.Priority == 3,
            $"und,ita with sub=de -> track {c4.Position}, prio {c4.Priority}");
        f += c4.Position == 0 && c4.Priority == 3 ? 0 : 1;

        // The common case must be unchanged: a single English track.
        var c5 = AudioTrackChoice.Choose(new List<MediaStream> { Audio("eng") }, "en");
        Check("single English track stays at 0", c5.Position == 0 && c5.Priority == 1,
            $"eng with sub=en -> track {c5.Position}, prio {c5.Priority}");
        f += c5.Position == 0 && c5.Priority == 1 ? 0 : 1;

        // No streams at all: the old behaviour, position 0, and it must say so.
        var c6 = AudioTrackChoice.Choose(null, "en");
        Check("no streams listed -> position 0", c6.Position == 0 && c6.TrackCount == 0,
            $"null -> track {c6.Position}, prio {c6.Priority}");
        f += c6.Position == 0 && c6.TrackCount == 0 ? 0 : 1;

        // Video/subtitle streams must not be mistaken for audio tracks.
        var mixed = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Language = "eng" },
            new() { Type = MediaStreamType.Audio, Language = "ita" },
            new() { Type = MediaStreamType.Subtitle, Language = "eng" },
            new() { Type = MediaStreamType.Audio, Language = "eng" }
        };
        var c7 = AudioTrackChoice.Choose(mixed, "en");
        Check("only audio streams count", c7.Position == 1 && c7.TrackCount == 2,
            $"v,ita,s,eng with sub=en -> audio track {c7.Position} of {c7.TrackCount}");
        f += c7.Position == 1 && c7.TrackCount == 2 ? 0 : 1;

        Console.WriteLine();
        return f;
    }

    private static string Sample() =>
        "1\r\n00:00:03,545 --> 00:00:05,879\r\nYou are being watched.\r\n\r\n"
        + "2\r\n00:00:07,000 --> 00:00:09,500\r\n- Stay where you are!\r\n- Don't move!\r\n\r\n"
        + "3\r\n00:01:00,250 --> 00:01:02,000\r\n[door slams]\r\n\r\n";

    /// <summary>Case 2: the shift is applied exactly and the text survives.</summary>
    private static int ShiftApplied()
    {
        int f = 0;
        Console.WriteLine("[2] constant shift applied");

        string src = Sample();
        foreach (double shift in new[] { -2.0, -0.545, 1.5, 12.0 })
        {
            (bool ok, string outText, string why) = SubtitleSync.ShiftBy(src, shift);
            if (!ok)
            {
                Check($"shift {shift:+0.000;-0.000}s", false, "refused: " + why);
                f++;
                continue;
            }

            // Times must move by exactly the planted amount.
            var before = Times(src);
            var after = Times(outText);
            bool timesOk = before.Count == after.Count
                && before.Zip(after).All(p => Math.Abs((p.Second - p.First) - shift) < 0.002);

            // Text content must be identical, line for line.
            bool textOk = Body(src) == Body(outText);
            bool countOk = CueCount(src) == CueCount(outText);

            Check($"shift {shift:+0.000;-0.000}s",
                timesOk && textOk && countOk,
                $"times {timesOk}, text {textOk}, cues {CueCount(src)}={CueCount(outText)}");
            f += timesOk && textOk && countOk ? 0 : 1;
        }

        // A shift of zero must not produce a different file.
        (bool ok0, string same, _) = SubtitleSync.ShiftBy(src, 0.0);
        Check("zero shift leaves the file as it was", ok0 && same == src,
            ok0 ? "unchanged" : "refused");
        f += ok0 && same == src ? 0 : 1;

        Console.WriteLine();
        return f;
    }

    /// <summary>Case 3: a shift that would go negative is refused, not clamped.</summary>
    private static int ShiftRefused()
    {
        int f = 0;
        Console.WriteLine("[3] shift refused rather than clamped");

        string src = Sample(); // first cue at 3.545 s
        (bool ok, string outText, string why) = SubtitleSync.ShiftBy(src, -4.0);
        Check("negative first cue is refused", !ok && outText == src,
            ok ? "APPLIED (wrong)" : why);
        f += !ok && outText == src ? 0 : 1;

        // Exactly-zero first cue is allowed (boundary, not negative).
        (bool okEdge, _, string whyEdge) = SubtitleSync.ShiftBy(src, -3.545);
        Check("first cue landing exactly on 0 is allowed", okEdge, okEdge ? "applied" : whyEdge);
        f += okEdge ? 0 : 1;

        // Content without timestamps is refused, not passed through.
        (bool okBad, _, string whyBad) = SubtitleSync.ShiftBy("no timestamps here", 1.0);
        Check("content without timestamps is refused", !okBad, whyBad);
        f += !okBad ? 0 : 1;

        // Empty content is refused.
        (bool okEmpty, _, string whyEmpty) = SubtitleSync.ShiftBy(string.Empty, 1.0);
        Check("empty content is refused", !okEmpty, whyEmpty);
        f += !okEmpty ? 0 : 1;

        Console.WriteLine();
        return f;
    }

    /// <summary>Case 4: the byte style of the corrected file matches the payload.</summary>
    private static int ByteStyle()
    {
        int f = 0;
        Console.WriteLine("[4] byte style preserved");

        string text = Sample().Replace("\r\n", "\n", StringComparison.Ordinal);

        byte[] bomCrlf = SubtitleSync.Encode(text, bom: true, crlf: true);
        bool b1 = bomCrlf[0] == 0xEF && bomCrlf[1] == 0xBB && bomCrlf[2] == 0xBF;
        string s1 = System.Text.Encoding.UTF8.GetString(bomCrlf, 3, bomCrlf.Length - 3);
        Check("BOM + CRLF", b1 && s1.Contains("\r\n", StringComparison.Ordinal),
            $"bom {b1}, crlf {s1.Contains("\r\n", StringComparison.Ordinal)}");
        f += b1 && s1.Contains("\r\n", StringComparison.Ordinal) ? 0 : 1;

        byte[] lf = SubtitleSync.Encode(text, bom: false, crlf: false);
        string s2 = System.Text.Encoding.UTF8.GetString(lf);
        bool b2 = !(lf[0] == 0xEF && lf[1] == 0xBB && lf[2] == 0xBF);
        Check("no BOM + LF", b2 && !s2.Contains("\r", StringComparison.Ordinal),
            $"noBom {b2}, lf {!s2.Contains("\r", StringComparison.Ordinal)}");
        f += b2 && !s2.Contains("\r", StringComparison.Ordinal) ? 0 : 1;

        // Style is read BACK from the bytes the pipeline actually holds.
        var (rb, rc) = SubtitleSync.StyleOfBytes(bomCrlf);
        Check("style read back from bytes", rb && rc, $"bom {rb}, crlf {rc}");
        f += rb && rc ? 0 : 1;

        var (rb2, rc2) = SubtitleSync.StyleOfBytes(lf);
        Check("style read back (noBom/LF)", !rb2 && !rc2, $"bom {rb2}, crlf {rc2}");
        f += !rb2 && !rc2 ? 0 : 1;

        // UTF-16 payloads carry their BOM (that IS how the download path detects
        // them), and are re-encoded as UTF-8 with BOM. The BOM must be prepended
        // explicitly: Encoding.Unicode.GetBytes alone writes none — first bytes are
        // 31 00, not FF FE (measured), so building the probe without the preamble
        // would test a payload that does not occur.
        byte[] u16Body = System.Text.Encoding.Unicode.GetBytes(Sample());
        byte[] u16Preamble = System.Text.Encoding.Unicode.GetPreamble();
        byte[] u16 = new byte[u16Preamble.Length + u16Body.Length];
        u16Preamble.CopyTo(u16, 0);
        u16Body.CopyTo(u16, u16Preamble.Length);

        var (rb3, rc3) = SubtitleSync.StyleOfBytes(u16);
        Check("UTF-16 payload (with BOM) counts as BOM and is detected as CRLF",
            rb3 && rc3,
            $"bom {rb3}, crlf {rc3} (preamble {u16[0]:X2} {u16[1]:X2})");
        f += rb3 && rc3 ? 0 : 1;

        Console.WriteLine();
        return f;
    }

    /// <summary>
    /// Case 5: the suffix must sit after ".srt", asserted against the real glob the
    /// sidecar listing uses.
    /// </summary>
    private static int UnsyncSuffix()
    {
        int f = 0;
        Console.WriteLine("[5] the .unsynchronized suffix does not become a subtitle");

        const string baseName = "Person Of Interest S02e06 The High Road";
        string corrected = $"/media/{baseName}.en.srt";
        string unsync = SubtitleSync.UnsyncPathFor(corrected);
        string swapped = $"/media/{baseName}.en.unsynchronized.srt";

        bool suffixOk = unsync == $"/media/{baseName}.en.srt.unsynchronized";
        Check("suffix sits after .srt", suffixOk, unsync);
        f += suffixOk ? 0 : 1;

        // The listing pattern is baseName + "*.srt". A path matches when the file name
        // is that prefix followed by anything then ".srt".
        bool correctedMatched = MatchesGlob(corrected, baseName);
        bool unsyncMatched = MatchesGlob(unsync, baseName);
        bool swappedMatched = MatchesGlob(swapped, baseName);

        Check("corrected file IS listed", correctedMatched, "matched");
        f += correctedMatched ? 0 : 1;
        Check("unsynchronized original is NOT listed", !unsyncMatched,
            unsyncMatched ? "MATCHED (would be read as a subtitle)" : "ignored");
        f += !unsyncMatched ? 0 : 1;
        Check("the swapped order WOULD be listed (why the order matters)", swappedMatched,
            swappedMatched ? "matched — the name parser then reads \"unsynchronized\" as a language" : "not matched");
        f += swappedMatched ? 0 : 1;

        Console.WriteLine();
        return f;
    }

    /// <summary>
    /// The sidecar listing's own pattern: <c>Directory.EnumerateFiles(dir, baseName + "*.srt")</c>.
    /// Emulated as "file name starts with the base name and ends with .srt".
    /// </summary>
    private static bool MatchesGlob(string path, string baseName)
    {
        string name = System.IO.Path.GetFileName(path);
        return name.StartsWith(baseName, StringComparison.Ordinal)
               && name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase);
    }

    private static List<double> Times(string srt)
    {
        var rx = new System.Text.RegularExpressions.Regex(
            @"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})");
        return rx.Matches(srt)
            .Select(m => (int.Parse(m.Groups[1].Value) * 3600.0)
                         + (int.Parse(m.Groups[2].Value) * 60.0)
                         + int.Parse(m.Groups[3].Value)
                         + (int.Parse(m.Groups[4].Value) / 1000.0))
            .ToList();
    }

    private static string Body(string srt)
    {
        var rx = new System.Text.RegularExpressions.Regex(@"-->");
        return string.Join(
            "\n",
            srt.Split('\n')
                .Where(l => !rx.IsMatch(l)
                            && !System.Text.RegularExpressions.Regex.IsMatch(l.Trim(), @"^\d+$"))
                .Select(l => l.Trim()));
    }

    private static int CueCount(string srt)
        => System.Text.RegularExpressions.Regex.Matches(srt, @"-->").Count;
}
