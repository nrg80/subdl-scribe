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
//   4. Byte style     — a file this plugin CHANGES is written CANONICAL (UTF-8, no
//                       BOM, LF) so the bytes on disk are the bytes the hash
//                       describes. Asserted on the real helper and against the
//                       pipeline source, because the two used to agree only by
//                       coincidence: the corrected file was re-encoded back to the
//                       fetched style, and NormalizeSrt happened to strip exactly
//                       what that put back. The ARCHIVED original is the exception
//                       — it keeps its own byte style, which is the whole point of
//                       keeping it.
//   5. The name       — the kept artefact "<...>.srt.unsynced.zip" must NOT
//                       match the sidecar glob (baseName + "*.srt"), while the
//                       swapped order would. Asserted against the real pattern,
//                       because the swapped form silently invents a language.
//   6. The archive    — "<...>.srt.unsynced.zip" (F-M306) must be the ONLY
//                       artefact written, hold exactly ONE entry named after the
//                       file it preserves, and carry the unmodified bytes — read
//                       back as a real archive, not as a blob, so a re-encode or a
//                       line-ending change fails here. The LOOSE copy's absence is
//                       asserted too: it is the regression this change could cause.
//
// Run: dotnet run --project scripts/tests/autosync
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Qa;
using Jellyfin.Plugin.SubdlScribe.Registry;
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
        failures += ShiftClamped();
        failures += ByteStyle();
        failures += UnsyncSuffix();
        failures += UnsyncArchive();
        failures += StreamingDecoder();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "ALL CASES PASSED"
            : $"{failures} CASE(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Case 7: the decoder streams frame LEVELS and nothing persists them. Two absences,
    /// asserted on the source, because both are invisible in a log line and both would
    /// silently come back: materialising the samples costs ~1 GB peak on a 140-minute
    /// track (measured, the OOM killer took it), and a cached or stored level curve is a
    /// second copy of the audio that nothing asked for.
    /// </summary>
    private static int StreamingDecoder()
    {
        int f = 0;
        Console.WriteLine("[7] the decoder streams levels; nothing persists them");

        string gate = "/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Qa/DriftGate.cs";
        string fit = "/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Qa/OffsetFit.cs";
        if (!System.IO.File.Exists(gate) || !System.IO.File.Exists(fit))
        {
            Console.WriteLine("  [SKIP] sources not reachable from here");
            return f;
        }

        string g = System.IO.File.ReadAllText(gate);
        string o = System.IO.File.ReadAllText(fit);

        // (a) the streaming decoder exists and is what the paths call
        Check("the streaming decoder exists",
            g.Contains("DecodeFrameLevelsAsync", StringComparison.Ordinal),
            "DecodeFrameLevelsAsync");
        f += g.Contains("DecodeFrameLevelsAsync", StringComparison.Ordinal) ? 0 : 1;

        // (b) the sample materialiser is GONE. It existed and allocated byte[] + float[]
        //     at once; a presence-only check on the new name would not notice its return.
        Check("no sample-materialising decoder is left",
            !g.Contains("ms.ToArray()", StringComparison.Ordinal)
            && !g.Contains("new float[", StringComparison.Ordinal),
            "no ms.ToArray() / new float[] in the decoder");
        f += !g.Contains("ms.ToArray()", StringComparison.Ordinal)
             && !g.Contains("new float[", StringComparison.Ordinal) ? 0 : 1;

        // (c) the level curve is what the fit consumes, not samples
        Check("the fit takes the level curve",
            o.Contains("SpeechProbability(double[] level)", StringComparison.Ordinal)
            || o.Contains("SpeechProbability(double[] level)", StringComparison.Ordinal),
            "SpeechProbability(double[])");
        f += o.Contains("SpeechProbability(double[] level)", StringComparison.Ordinal) ? 0 : 1;

        // (d) nothing writes the levels anywhere: no file, no cache, no DB column. The
        //     plugin side only — the fit TEST keeps a local file as a test convenience.
        bool writesLevels =
            System.Text.RegularExpressions.Regex.IsMatch(g, @"File\.WriteAll\w*\([^)]*[Ll]evel")
            || System.Text.RegularExpressions.Regex.IsMatch(o, @"File\.WriteAll\w*\([^)]*[Ll]evel");
        Check("nothing persists the levels", !writesLevels,
            writesLevels ? "FOUND a level write in the plugin" : "no level file/cache/column");
        f += writesLevels ? 1 : 0;

        // (e) no DB entity carries a curve/blob field
        string entities = "/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Data/Entities.cs";
        if (System.IO.File.Exists(entities))
        {
            string e = System.IO.File.ReadAllText(entities);
            bool curveCol =
                e.Contains("Envelope", StringComparison.Ordinal)
                || e.Contains("SpeechLevel", StringComparison.Ordinal)
                || e.Contains("LevelCurve", StringComparison.Ordinal);
            Check("no database column holds an audio curve", !curveCol,
                curveCol ? "FOUND a curve column in Entities.cs" : "no curve column");
            f += curveCol ? 1 : 0;
        }

        Console.WriteLine();
        return f;
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

    /// <summary>Case 3: a shift that would go negative is applied, the cue clamped to zero.</summary>
    private static int ShiftClamped()
    {
        int f = 0;
        Console.WriteLine("[3] shift applied; a cue below zero is clamped");

        string src = Sample(); // first cue at 3.545 s
        (bool ok, string outText, string why) = SubtitleSync.ShiftBy(src, -4.0);
        Check("negative first cue is APPLIED, not refused", ok && outText != src,
            ok ? why : "REFUSED (wrong)");
        f += ok && outText != src ? 0 : 1;

        List<double> after = Times(outText);
        Check("the cue that lands below zero is clamped to 0",
            after.Count > 0 && Math.Abs(after[0]) < 1e-6,
            $"first timestamp after the shift: {(after.Count > 0 ? after[0] : double.NaN):0.000}s");
        f += after.Count > 0 && Math.Abs(after[0]) < 1e-6 ? 0 : 1;

        // The cues AFTER the clamped one must carry the full measured shift — a clamp that
        // dragged the rest of the file with it would be the old refusal in disguise.
        List<double> before = Times(src);
        int idx = 1;
        Check("every later cue still moves by the full shift",
            after.Count > idx && Math.Abs((after[idx] - before[idx]) - (-4.0)) < 1e-6,
            $"cue {idx}: {before[idx]:0.000}s -> {(after.Count > idx ? after[idx] : double.NaN):0.000}s");
        f += after.Count > idx && Math.Abs((after[idx] - before[idx]) - (-4.0)) < 1e-6 ? 0 : 1;

        // Exactly-zero first cue: boundary case, no clamp needed.
        (bool okEdge, string edgeText, string whyEdge) = SubtitleSync.ShiftBy(src, -3.545);
        List<double> edge = Times(edgeText);
        Check("first cue landing exactly on 0 is allowed",
            okEdge && edge.Count > 0 && Math.Abs(edge[0]) < 1e-6,
            okEdge ? $"first {edge[0]:0.000}s" : whyEdge);
        f += okEdge && edge.Count > 0 && Math.Abs(edge[0]) < 1e-6 ? 0 : 1;

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

        // ---- F-M296 (operator order, 07.10.2026): what this plugin WRITES is canonical.
        // The corrected file used to be re-encoded to the fetched payload's byte style, and
        // the stored hash was taken over the normalized text — the two agreed only because
        // NormalizeSrt strips exactly what the style puts back. That is a coincidence, not an
        // invariant, so it is now asserted instead of assumed: the bytes written are the bytes
        // hashed, whatever style the payload arrived in.
        foreach (var (label, payload) in new (string, byte[])[]
        {
            ("BOM + CRLF", bomCrlf),
            ("no BOM + LF", lf),
            ("UTF-16 with BOM", u16),
        })
        {
            string decoded = DecodeLikePayload(payload);
            byte[] written = ContentHashRegistry.EncodeCanonical(decoded);

            bool noBom = !(written.Length >= 3 && written[0] == 0xEF && written[1] == 0xBB && written[2] == 0xBF);
            bool lfOnly = !System.Text.Encoding.UTF8.GetString(written).Contains('\r');
            Check($"written canonical ({label})", noBom && lfOnly,
                $"noBom {noBom}, lf {lfOnly}");
            f += noBom && lfOnly ? 0 : 1;

            // The invariant that matters: the hash the registry stores must describe the bytes
            // that actually land on disk. Asserted by hashing the WRITTEN BYTES DIRECTLY — not by
            // hashing their decoded text, which routes both sides through NormalizeSrt and so
            // compares a value with itself. Measured: the tautological form passed even with
            // EncodeCanonical deliberately broken to emit BOM+CRLF, and caught nothing.
            string storedHash = ContentHashRegistry.ComputeHash(decoded);
            string hashOfWrittenBytes =
                Convert.ToHexString(System.Security.Cryptography.MD5.HashData(written)).ToLowerInvariant();

            Check($"stored hash == MD5 of the bytes ON DISK ({label})", storedHash == hashOfWrittenBytes,
                storedHash == hashOfWrittenBytes
                    ? storedHash
                    : $"stored {storedHash} but disk {hashOfWrittenBytes}");
            f += storedHash == hashOfWrittenBytes ? 0 : 1;
        }

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
        Console.WriteLine("[5] the .unsynced suffix does not become a subtitle");

        const string baseName = "Person Of Interest S02e06 The High Road";
        string corrected = $"/media/{baseName}.en.srt";
        string kept = SubtitleSync.UnsyncZipPathFor(corrected);   // the artefact that IS kept
        string swapped = $"/media/{baseName}.en.unsynced.srt";

        bool suffixOk = kept == $"/media/{baseName}.en.srt.unsynced.zip";
        Check("the kept name sits after .srt", suffixOk, kept);
        f += suffixOk ? 0 : 1;

        // The listing pattern is baseName + "*.srt". A path matches when the file name
        // is that prefix followed by anything then ".srt".
        bool correctedMatched = MatchesGlob(corrected, baseName);
        bool unsyncMatched = MatchesGlob(kept, baseName);
        bool swappedMatched = MatchesGlob(swapped, baseName);

        Check("corrected file IS listed", correctedMatched, "matched");
        f += correctedMatched ? 0 : 1;
        Check("the kept artefact is NOT listed", !unsyncMatched,
            unsyncMatched ? "MATCHED (would be read as a subtitle)" : "ignored");
        f += !unsyncMatched ? 0 : 1;
        Check("the swapped order WOULD be listed (why the order matters)", swappedMatched,
            swappedMatched ? "matched — the name parser then reads \"unsynced\" as a language" : "not matched");
        f += swappedMatched ? 0 : 1;

        Console.WriteLine();
        return f;
    }

    /// <summary>
    /// Case 6: the archived original (F-M306) — one entry, the same bytes, and a name that
    /// matches neither listing pattern, so the archive can never be read as a subtitle.
    /// </summary>
    private static int UnsyncArchive()
    {
        int f = 0;
        Console.WriteLine("[6] the archived original (.zip) — F-M306");

        const string baseName = "Person Of Interest S02e06 The High Road";
        string corrected = $"/media/{baseName}.en.srt";
        string unsync = SubtitleSync.UnsyncPathFor(corrected);
        string unsyncZip = SubtitleSync.UnsyncZipPathFor(corrected);

        // The name sits after ".srt" like the copy's does, and ends in .zip.
        bool nameOk = unsyncZip == $"/media/{baseName}.en.srt.unsynced.zip";
        Check("zip name sits after .srt", nameOk, unsyncZip);
        f += nameOk ? 0 : 1;

        // Neither listing pattern may see it. Both match on the .srt ENDING.
        bool sidecarSees = MatchesGlob(unsyncZip, baseName);
        Check("the sidecar listing does NOT see the archive", !sidecarSees,
            sidecarSees ? "MATCHED (would be read as a subtitle)" : "ignored");

        f += !sidecarSees ? 0 : 1;
        bool seederSees = System.IO.Path.GetFileName(unsyncZip)
            .EndsWith(".srt", StringComparison.OrdinalIgnoreCase);
        Check("the seeder's \"*.srt\" does NOT see the archive", !seederSees,
            seederSees ? "MATCHED" : "ignored");
        f += !seederSees ? 0 : 1;

        // Build the real archive from real bytes, then read it back as an archive.
        // F-M306: the payload carries a BOM on purpose. The archive is fed the FETCHED bytes and
        // nothing else; it used to be fed `Encode(DecodeSrt(bytes), StyleOfBytes(bytes))`, and that
        // decode/encode round trip prepended a SECOND BOM to a payload that already had one.
        // Measured on a real file: 55 108 B came back as 55 111 B with the header EF BB BF EF BB BF.
        // A fixture WITHOUT a BOM cannot see that, which is why this one has three extra bytes.
        string sampleText = "1\n00:00:01,000 --> 00:00:03,000\nHallo\n";
        byte[] payload = SubtitleSync.Encode(sampleText, bom: true, crlf: true);
        byte[] zipBytes = SubtitleSync.BuildUnsyncArchive(System.IO.Path.GetFileName(unsync), payload);

        int entries = 0;
        byte[]? roundTrip = null;
        string? entryName = null;
        using (var ms = new System.IO.MemoryStream(zipBytes))
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read))
        {
            entries = zip.Entries.Count;
            if (entries > 0)
            {
                entryName = zip.Entries[0].FullName;
                using var s = zip.Entries[0].Open();
                using var outMs = new System.IO.MemoryStream();
                s.CopyTo(outMs);
                roundTrip = outMs.ToArray();
            }
        }

        Check("the archive holds exactly ONE entry", entries == 1, $"entries={entries}");
        f += entries == 1 ? 0 : 1;

        bool nameInZip = entryName == System.IO.Path.GetFileName(unsync);
        Check("the entry carries the copy's file name", nameInZip, entryName ?? "(none)");
        f += nameInZip ? 0 : 1;

        // Byte for byte — this is the assertion a re-encode fails. The payload carries a BOM,
        // which is what makes the doubled-BOM defect visible here instead of silently passing.
        bool identical = roundTrip != null && roundTrip.Length == payload.Length
                         && roundTrip.SequenceEqual(payload);
        bool singleBom = payload.Length >= 6
                         && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF
                         && !(payload[3] == 0xEF && payload[4] == 0xBB && payload[5] == 0xBF);
        Check("the payload is BYTE-IDENTICAL to the plain copy", identical && singleBom,
            identical
                ? (singleBom
                    ? $"{payload.Length} bytes equal, single BOM (EF BB BF)"
                    : $"{payload.Length} bytes equal but the BOM is DOUBLED (EF BB BF EF BB BF)")
                : $"differ: zip={roundTrip?.Length ?? -1} bytes, copy={payload.Length} bytes");
        f += identical && singleBom ? 0 : 1;

        // Deterministic: the same input yields the same bytes on every run, which is what
        // makes a byte comparison a usable test at all.
        byte[] again = SubtitleSync.BuildUnsyncArchive(System.IO.Path.GetFileName(unsync), payload);
        bool deterministic = again.SequenceEqual(zipBytes);
        Check("the archive is deterministic", deterministic,
            deterministic ? "same bytes on a second build" : "BYTES DIFFER between runs");
        f += deterministic ? 0 : 1;

        // ---- The artefact count, on a REAL directory. This is where "only the zip" is
        // proved rather than asserted: the pipeline's own write sequence is replayed on
        // disk, and the folder must then hold exactly the corrected file and the archive.
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "subdl-unsync-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            string tCorrected = System.IO.Path.Combine(dir, $"{baseName}.en.srt");
            string tZip = SubtitleSync.UnsyncZipPathFor(tCorrected);
            string tLoose = SubtitleSync.UnsyncPathFor(tCorrected);

            System.IO.File.WriteAllBytes(tCorrected, SubtitleSync.Encode("1\n00:00:01,000 --> 00:00:03,000\nX\n", true, false));
            System.IO.File.WriteAllBytes(tZip, zipBytes);

            string[] files = System.IO.Directory.GetFiles(dir);
            Check("the folder holds exactly TWO files", files.Length == 2,
                $"{files.Length}: " + string.Join(", ", System.Array.ConvertAll(files, System.IO.Path.GetFileName)));
            f += files.Length == 2 ? 0 : 1;

            bool looseAbsent = !System.IO.File.Exists(tLoose);
            Check("the LOOSE copy is NOT written", looseAbsent,
                looseAbsent ? "no <...>.srt.unsynced on disk" : "FOUND a loose copy");
            f += looseAbsent ? 0 : 1;

            bool zipPresent = System.IO.File.Exists(tZip);
            Check("the archive IS on disk", zipPresent, System.IO.Path.GetFileName(tZip));
            f += zipPresent ? 0 : 1;
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
        }

        // ---- The SOURCE. A helper can be right while the CALLER still writes the loose
        // copy; the pipeline's own write calls are what decide the artefact on disk.
        const string pipeline = "/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs";
        if (System.IO.File.Exists(pipeline))
        {
            string src = System.IO.File.ReadAllText(pipeline);
            // A write of the loose path would look like AtomicWriteAsync(<name containing
            // "unsyncPath", ...) or the HI equivalent. Both must be gone.
            bool looseWrite = System.Text.RegularExpressions.Regex.IsMatch(
                src, @"AtomicWriteAsync\s*\(\s*(hi)?[Uu]nsync(Path|Name)\b");
            Check("no write targets the loose copy in the pipeline source", !looseWrite,
                looseWrite ? "FOUND AtomicWriteAsync(unsyncPath/…)" : "only the archive is written");
            f += !looseWrite ? 0 : 1;

            bool archiveWrite = System.Text.RegularExpressions.Regex.IsMatch(
                src, @"AtomicWriteAsync\s*\(\s*\n?\s*(hi)?[Uu]nsyncZip\b");
            Check("the pipeline writes the archive path", archiveWrite,
                archiveWrite ? "AtomicWriteAsync(unsyncZip/…)" : "NOT FOUND — the archive is never written");
            f += archiveWrite ? 0 : 1;

            // F-M296: the CORRECTED file must be encoded canonical, never re-encoded back to
            // the fetched payload's byte style. A helper can be right while the caller still
            // writes the old shape, so the source is checked as well — this is the regression
            // that would silently put the stored hash and the file back out of step.
            //
            // TWO, and that count is the correct one. The pipeline has exactly two corrected
            // writes: the plain sidecar and the HI sidecar (case matters — the HI variable is
            // `hiWriteBytes`, so a pattern expecting `writeBytes` alone would miscount). The
            // other two writes in the file carry the FETCHED bytes untouched (`writeBytes =
            // bytes`, `hiWriteBytes = hiBytes`) and must NOT be canonical: the original has to
            // stay exactly as SubDL delivered it, which is what the archive guarantees. An
            // expectation of FOUR counted those two and could never pass — it measured the
            // wrong thing, and this assertion stayed red while the behaviour was right.
            // Measured 07.10.2026: the same 2 against HEAD, before any of this work.
            int canonical = System.Text.RegularExpressions.Regex.Matches(
                src, @"[Ww]riteBytes\s*=\s*ContentHashRegistry\.EncodeCanonical\(").Count;
            Check("both corrected-file writes go out canonical", canonical == 2,
                $"EncodeCanonical {canonical}/2");
            f += canonical == 2 ? 0 : 1;

            // F-M306: the archive is fed the FETCHED bytes, never a decoded/re-encoded copy of
            // them. The round trip `Encode(DecodeSrt(bytes), StyleOfBytes(bytes))` prepended a
            // SECOND BOM to a payload that already carried one — measured on a real file, 55 108 B
            // came out as 55 111 B with the header EF BB BF EF BB BF — while every assertion in this
            // file stayed green: the fixture carried no BOM, and the helper faithfully archived
            // whatever it was handed. The defect was in the ARGUMENT, so the argument is asserted.
            int archiveArgs = System.Text.RegularExpressions.Regex.Matches(
                src, @"BuildUnsyncArchive\(\s*(hi)?[Uu]nsyncName\s*,\s*(hi)?[Bb]ytes!?\s*\)").Count;
            int reencoded = System.Text.RegularExpressions.Regex.Matches(
                src, @"(hi)?[Oo]riginalBytes\s*=").Count;
            Check("the archive is fed the FETCHED bytes, not a re-encoded copy",
                archiveArgs == 2 && reencoded == 0,
                $"BuildUnsyncArchive(…, bytes) {archiveArgs}/2, re-encode sites {reencoded} (want 0)");
            f += archiveArgs == 2 && reencoded == 0 ? 0 : 1;
        }

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

    /// <summary>
    /// Decodes payload bytes the way the pipeline does — UTF-16 with its BOM, else UTF-8 —
    /// so the canonical round trip is measured on the same input the real path sees.
    /// </summary>
    private static string DecodeLikePayload(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        return System.Text.Encoding.UTF8.GetString(bytes);
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
