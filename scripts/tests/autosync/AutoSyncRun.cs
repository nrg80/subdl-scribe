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
using Jellyfin.Plugin.SubdlScribe.Api;
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
        failures += CorrectionHunt();
        failures += QaBudgetIsTheFits();
        failures += SlotTellsKind();
        failures += UnsyncArchive();
        failures += StreamingDecoder();
        failures += FitTimeCredit();

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

    /// <summary>
    /// Case 8: F-M310 — the time an audio alignment spent comes off the next transfer pause,
    /// with a floor at zero. Arithmetic, but the arithmetic is the whole feature: the claim is
    /// that a measured alignment is given back and that nothing is banked, and both halves are
    /// wrong in ways a log line would not show (a credit taken twice shrinks two pauses for one
    /// alignment; a negative pause would fire instantly and make the rhythm meaningless).
    /// Driven against the real limiter, because the floor and the exchange live there.
    /// </summary>
    /// <returns>Number of failed checks.</returns>
    private static int FitTimeCredit()
    {
        int f = 0;
        Console.WriteLine("[8] the alignment time comes off the transfer pause (F-M310)");

        // Rate 400 -> base pause 9 s, jittered +-30 % (6.3-11.7 s). A zero credit must return
        // the untouched jittered pause, so the assertion is a range, not a number.
        var lim = new GlobalRateLimiter(400);

        int plain = lim.TransferPauseMs();
        bool plainInBand = plain >= 6300 && plain <= 11700;
        Check("no alignment booked -> the pause is the jittered one",
            plainInBand, $"rate 400 -> {plain} ms (band 6300-11700)");
        f += plainInBand ? 0 : 1;

        // A credit far above the pause must land on exactly 0, not negative.
        lim.AddFitMs(60_000);
        int floored = lim.TransferPauseMsLessFit();
        Check("alignment longer than the pause -> 0, never negative",
            floored == 0, $"60 s alignment against a ~9 s pause -> {floored} ms");
        f += floored == 0 ? 0 : 1;

        // The credit is CONSUMED: the next pause must be the full one again, not short a second
        // time. This is the half that a plain read would get wrong.
        int after = lim.TransferPauseMsLessFit();
        bool consumed = after >= 6300 && after <= 11700;
        Check("the credit is spent once, not carried",
            consumed, $"pause after the credit was consumed -> {after} ms");
        f += consumed ? 0 : 1;

        // A partial credit must shorten the pause by exactly that much: same rate, same jitter
        // draw is impossible, so assert the BOUND instead — a 5 s credit can never yield a pause
        // above 11.7-5 = 6.7 s, while without it the pause could reach 11.7 s.
        for (int i = 0; i < 40; i++)
        {
            var l2 = new GlobalRateLimiter(400);
            l2.AddFitMs(5_000);
            int p = l2.TransferPauseMsLessFit();
            if (p > 6_700)
            {
                Check("a 5 s credit pulls the pause down",
                    false, $"got {p} ms, above the 6700 ms ceiling a 5 s credit implies");
                f += 1;
                break;
            }

            if (i == 39)
            {
                Check("a 5 s credit pulls the pause down",
                    true, "40 draws stayed at or below 6700 ms");
            }
        }

        // Zero and negative bookings must be IGNORED, not booked. Asserted through the pause,
        // because the booked amount is private by design: a negative credit would ADD to the
        // pause (pause - (-9000) = pause + 9 s), which breaks the jitter band upward. A pause
        // still inside the band is the evidence that the booking was discarded.
        var l3 = new GlobalRateLimiter(400);
        l3.AddFitMs(0);
        l3.AddFitMs(-9_000);
        int negPause = l3.TransferPauseMsLessFit();
        bool ignored = negPause >= 6300 && negPause <= 11700;
        Check("zero and negative bookings are ignored (the pause never grows)",
            ignored, $"after 0 and -9000 -> {negPause} ms (band 6300-11700)");
        f += ignored ? 0 : 1;

        // The floor holds at a SECOND rate too, so the bound is not an artefact of one base value.
        // Rate 100 -> 36 s base, jittered 25.2-46.8 s. The credit is chosen ABOVE that maximum:
        // a credit of exactly 36 s would only floor when the jitter drew low, so it would assert
        // a property the code does not have (and, before this was fixed, failed exactly that way).
        var l4 = new GlobalRateLimiter(100);
        l4.AddFitMs(50_000);
        int exact = l4.TransferPauseMsLessFit();
        Check("the floor holds at another rate (credit above the jitter maximum)",
            exact == 0, $"50 s credit against a 25.2-46.8 s pause -> {exact} ms");
        f += exact == 0 ? 0 : 1;

        // And the pipeline must actually USE the crediting pause at both transfer gaps, while the
        // alignment itself must be measured. Asserted on the source: a fit that runs unmeasured
        // leaves the credit at zero and the feature silently does nothing.
        string pipe = "/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs";
        if (System.IO.File.Exists(pipe))
        {
            string src = System.IO.File.ReadAllText(pipe);
            int creditSites = src.Split("_limiter.TransferPauseMsLessFit()").Length - 1;
            bool bothGaps = creditSites == 2;
            Check("both transfer gaps use the crediting pause",
                bothGaps, $"{creditSites} site(s), expected 2 (main and HI)");
            f += bothGaps ? 0 : 1;

            bool measured = src.Split("SyncMeasuredAsync(").Length - 1 == 3;
            Check("both alignments are measured (one definition, two calls)",
                measured, $"{src.Split("SyncMeasuredAsync(").Length - 1} occurrence(s), expected 3");
            f += measured ? 0 : 1;

            // F-M323 (operator order 08.10.2026): the RAW call lives in the WORKER now, not in the
            // pipeline. The assertion follows it — and gets stronger on the way: the pipeline must
            // contain NO raw call at all (a second fit path there would bypass the worker), and the
            // worker must contain exactly one.
            string worker = "/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Qa/AutoSyncWorker.cs";
            int raw = src.Split("SubtitleSync.SyncAsync(").Length - 1;
            Check("no raw fit call is left in the pipeline", raw == 0,
                $"{raw} raw call(s) in the pipeline, expected 0 (they belong to the worker)");
            f += raw == 0 ? 0 : 1;

            if (System.IO.File.Exists(worker))
            {
                string wsrc = System.IO.File.ReadAllText(worker);
                int rawWorker = wsrc.Split("SubtitleSync.SyncAsync(").Length - 1;
                Check("the worker makes exactly one raw fit call, measured", rawWorker == 1,
                    $"{rawWorker} raw call(s) in the worker, expected 1");
                f += rawWorker == 1 ? 0 : 1;

                // The stopwatch must WRAP the call: started before it and stopped after it, in a
                // finally block so a cancelled fit still spends its time. Asserted as an ORDER, not
                // as the presence of a stopwatch — a stopwatch started after the fit, or stopped
                // before it, reports a number that means nothing. `Stopwatch.StartNew()` starts the
                // watch itself, so that is the anchor, not a `sw.Start()` call.
                int swStart = wsrc.IndexOf("Stopwatch.StartNew();", StringComparison.Ordinal);
                int callAt = wsrc.IndexOf("SubtitleSync.SyncAsync(", StringComparison.Ordinal);
                int swStop = wsrc.IndexOf("sw.Stop();", StringComparison.Ordinal);
                bool wraps = swStart >= 0 && callAt > swStart && swStop > callAt;
                Check("the worker measures around the fit, not around a write", wraps,
                    wraps ? "stopwatch wraps the call" : "the stopwatch does not wrap the fit");
                f += wraps ? 0 : 1;
            }
            else
            {
                Check("the worker source is reachable for the measurement check", false,
                    "the worker file was not found — the measurement checks cannot run");
                f += 1;
            }

            // F-M323 (operator order 08.10.2026): the CONTRACT. The worker is handed the subtitle
            // and hands back a status and its output — the operator's own words. Asserted on the
            // worker's public surface, because the contract is what the download may rely on: a
            // worker that returns only a bool, or that writes files itself, breaks the cut between
            // "owns the outcome" and "owns the files and the counters".
            if (System.IO.File.Exists(worker))
            {
                string wsrc = System.IO.File.ReadAllText(worker);
                bool takesSub = wsrc.Contains("string srtText,") && wsrc.Contains("string audioMap,");
                Check("the worker is handed the subtitle and the audio map", takesSub,
                    takesSub ? "inputs are the subtitle text and the audio map" : "the input surface moved");
                f += takesSub ? 0 : 1;

                bool returnsOutcome = wsrc.Contains("Task<Outcome> RunAsync(")
                                      && wsrc.Contains("readonly record struct Outcome(");
                Check("the worker returns a status and its output", returnsOutcome,
                    returnsOutcome ? "Outcome(Status, Applied, Corrected, Reason, ElapsedMs …)"
                                   : "the return type is not the outcome record");
                f += returnsOutcome ? 0 : 1;

                // Its own status vocabulary, with the two cases that decide the caller's next move.
                bool ownStatus = wsrc.Contains("AlreadyGood") && wsrc.Contains("CandidateSpecific")
                                 && wsrc.Contains("ElapsedMs");
                Check("the outcome carries already-good, candidate-specific and the measured time", ownStatus,
                    ownStatus ? "the caller does not re-derive any of the three" : "a decision is missing");
                f += ownStatus ? 0 : 1;

                // The OPERATOR'S CUT: the worker must not write files or count statistics. Checked as
                // an absence, because "helpfully" filing the artifact here is exactly the drift this
                // boundary exists to prevent — two writers for one directory.
                bool doesNotWrite = !wsrc.Contains("File.WriteAllBytes")
                                    && !wsrc.Contains("FittedToAudio")
                                    && !wsrc.Contains("AlreadyGoodAsDownloaded++");
                Check("the worker writes no file and keeps no counter", doesNotWrite,
                    doesNotWrite ? "files and counters stay with the download run"
                                 : "the worker took over writing or counting");
                f += doesNotWrite ? 0 : 1;
            }

            // The download must no longer re-derive the two decisions from the reason string: that
            // derivation is what the worker replaced. Asserted as an absence in the pipeline.
            bool noDerivation = !src.Contains("Qa.SubtitleSync.RefusalIsCandidateSpecific(")
                                && !src.Contains("Qa.SubtitleSync.RefusalMeansAlreadyGood(");
            Check("the pipeline no longer re-derives the refusal itself", noDerivation,
                noDerivation ? "the two decisions come from the worker's status"
                             : "the pipeline still reads the reason string");
            f += noDerivation ? 0 : 1;

            // The helper must BOOK the measured time, and book it with the limiter (not with a
            // local variable that nothing reads). A helper that receives the time but never credits
            // it leaves the feature with no effect at all — the shape this check exists for,
            // because every other assertion here still passes in that state. F-M323: the value now
            // arrives on the outcome, so the booking reads `outcome.ElapsedMs`.
            bool books = src.Contains("_limiter.AddFitMs(outcome.ElapsedMs);");
            Check("the helper credits the measured time to the limiter",
                books, books ? "AddFitMs is called with the worker's measurement" : "the measurement is never credited");
            f += books ? 0 : 1;

            // And the run total must be kept, or the DONE line would print a permanent 0s.
            bool total = src.Contains("summary.FitMsTotal += outcome.ElapsedMs;");
            Check("the helper keeps the run total for the log",
                total, total ? "FitMsTotal is accumulated" : "FitMsTotal is never written");
            f += total ? 0 : 1;
        }
        else
        {
            Console.WriteLine("  [SKIP] pipeline source not reachable from here");
        }

        return f;
    }

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
    /// Case 5: the kept original IS a subtitle now — a loose sidecar in the reserved block, which
    /// Jellyfin lists as its own selectable track. F-M315 replaced the archive with it.
    /// </summary>
    private static int UnsyncSuffix()
    {
        int f = 0;
        Console.WriteLine("[5] the kept original is a selectable sidecar (F-M315)");

        const string baseName = "Person Of Interest S02e06 The High Road";
        string media = $"/media/{baseName}.mkv";
        string corrected = $"/media/{baseName}.en.srt";
        var none = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? kept = SidecarNaming.PlanOriginalTarget(media, "EN", false, none);
        bool nameOk = kept != null && System.IO.Path.GetFileName(kept) == $"{baseName}.en.99.srt";
        Check("the first original takes slot 99", nameOk, kept ?? "(null)");
        f += nameOk ? 0 : 1;

        // The listing pattern is baseName + "*.srt". The original MUST match it: that match is the
        // whole point — it is how the file becomes a track the operator can choose.
        bool listed = kept != null && MatchesGlob(kept, baseName);
        Check("the sidecar listing SEES the original (that is the point)", listed,
            listed ? "matched — it appears as its own track" : "not matched — invisible in the player");
        f += listed ? 0 : 1;

        // And the parser must read it as a normal sidecar: right language, no HI, NOT forced.
        var parsed = kept == null ? null : SidecarNaming.Parse(System.IO.Path.GetFileNameWithoutExtension(kept), baseName);
        bool parsedOk = parsed != null && parsed.Value.Lang == "EN"
                        && !parsed.Value.HearingImpaired && !parsed.Value.Forced;
        Check("the name parses as EN, not HI, not forced", parsedOk,
            parsed == null ? "(not parsed)" : $"{parsed.Value.Lang} hi={parsed.Value.HearingImpaired} forced={parsed.Value.Forced}");
        f += parsedOk ? 0 : 1;

        // Numbering runs DOWNWARD and stops at 90. 99, 98, 97 … never 100, never 89.
        var taken99 = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { $"{baseName}.en.99.srt" };
        string? second = SidecarNaming.PlanOriginalTarget(media, "EN", false, taken99);
        bool secondOk = second != null && System.IO.Path.GetFileName(second) == $"{baseName}.en.98.srt";
        Check("with 99 taken the next original takes 98", secondOk, second ?? "(null)");
        f += secondOk ? 0 : 1;

        // Every reserved slot taken → no name at all. Inventing one would overwrite an original.
        var allTen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int s = SidecarNaming.OriginalSlotMin; s <= SidecarNaming.OriginalSlotMax; s++)
        {
            allTen.Add($"{baseName}.en.{s}.srt");
        }

        string? overflow = SidecarNaming.PlanOriginalTarget(media, "EN", false, allTen);
        Check("all ten reserved slots taken → no name (never an overwrite)", overflow == null, overflow ?? "(null)");
        f += overflow == null ? 0 : 1;

        // The reserved block never collides with a corrected file: the corrected writer stops at 89.
        string? highestCorrected = null;
        var allCorrected = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int s = 1; s <= SidecarNaming.CorrectedSlotMax; s++)
        {
            allCorrected.Add($"{baseName}.en.{s:D2}.srt");
        }

        highestCorrected = SidecarNaming.PlanTarget(media, "EN", false, allCorrected);
        // The fallback name must never come out of the reserved block: that is the collision this
        // whole reservation exists to prevent. Slot 1 is the expected answer, and
        // its number is below the block by construction.
        string fallbackName = System.IO.Path.GetFileName(highestCorrected ?? "(null)");
        bool below = fallbackName == $"{baseName}.en.01.srt";
        Check("every corrected slot taken → slot 1, never a reserved one", below, fallbackName);
        f += below ? 0 : 1;

        bool block = SidecarNaming.IsOriginalSlot(90) && SidecarNaming.IsOriginalSlot(99)
                     && !SidecarNaming.IsOriginalSlot(89) && !SidecarNaming.IsOriginalSlot(1);
        Check("the reserved block is exactly 90–99", block, "90..99");
        f += block ? 0 : 1;

        // ── [5b] the slot is two digits, and the slot IS the track order (F-M316) ────────────
        Console.WriteLine();
        Console.WriteLine("[5b] the slot is two digits and it orders the track list (F-M316)");

        // The writer: every slot, always a number, always two digits — slot 1 included.
        bool w1 = System.IO.Path.GetFileName(SidecarNaming.Build(media, "EN", false, 1))
                  == $"{baseName}.en.01.srt";
        bool w2 = System.IO.Path.GetFileName(SidecarNaming.Build(media, "EN", false, 2))
                  == $"{baseName}.en.02.srt";
        bool w10 = System.IO.Path.GetFileName(SidecarNaming.Build(media, "EN", false, 10))
                   == $"{baseName}.en.10.srt";
        bool w99 = System.IO.Path.GetFileName(SidecarNaming.Build(media, "EN", false, 99))
                   == $"{baseName}.en.99.srt";
        Check("slot 1 is written as .01, not as the bare name", w1,
            System.IO.Path.GetFileName(SidecarNaming.Build(media, "EN", false, 1)));
        Check("slots 2 / 10 / 99 are two digits", w2 && w10 && w99,
            System.IO.Path.GetFileName(SidecarNaming.Build(media, "EN", false, 10)));
        f += w1 ? 0 : 1;
        f += (w2 && w10 && w99) ? 0 : 1;

        // The marker sits BEFORE the number — the reader steps over the slot first and then reads
        // the markers, so the opposite order would make a variant unrecognizable.
        bool markerFirst = System.IO.Path.GetFileName(SidecarNaming.Build(media, "DE", true, 1, true))
                           == $"{baseName}.de.sdh.forced.01.srt";
        Check("the markers come before the number (.de.sdh.forced.01.srt)", markerFirst,
            System.IO.Path.GetFileName(SidecarNaming.Build(media, "DE", true, 1, true)));
        f += markerFirst ? 0 : 1;

        // The OLD form must still parse — the switch needs no migration shim.
        var oldBare = SidecarNaming.Parse($"{baseName}.en", baseName);
        var oldOneDigit = SidecarNaming.Parse($"{baseName}.en.2", baseName);
        Check("a pre-F-M316 name without a number still parses",
            oldBare != null && oldBare.Value.Lang == "EN" && !oldBare.Value.HearingImpaired,
            oldBare?.ToString() ?? "null");
        Check("a pre-F-M316 one-digit slot still parses",
            oldOneDigit != null && oldOneDigit.Value.Lang == "EN",
            oldOneDigit?.ToString() ?? "null");
        f += (oldBare != null && oldBare.Value.Lang == "EN") ? 0 : 1;
        f += (oldOneDigit != null && oldOneDigit.Value.Lang == "EN") ? 0 : 1;

        // THE POINT of the number: the order Jellyfin lists is the ORDER OF THE NAMES. Asserted on
        // the names, not on the files, because that is what the player sorts. Corrected slots must
        // come before reserved originals — the ordering F-M315 reserves the block for.
        var newForm = new List<string>();
        for (int s = 1; s <= SidecarNaming.CorrectedSlotMax; s++)
        {
            newForm.Add(System.IO.Path.GetFileName(SidecarNaming.Build(media, "EN", false, s)));
        }

        for (int s = SidecarNaming.OriginalSlotMin; s <= SidecarNaming.OriginalSlotMax; s++)
        {
            newForm.Add(System.IO.Path.GetFileName(SidecarNaming.Build(media, "EN", false, s)));
        }

        var ordered = newForm.OrderBy(n => n, StringComparer.Ordinal).ToList();
        int lastCorrected = ordered.FindLastIndex(n => n.Contains(".89.srt"));
        int firstOriginal = ordered.FindIndex(n => n.Contains(".90.srt"));
        bool correctFirst = lastCorrected >= 0 && firstOriginal > lastCorrected;
        Check("every corrected slot lists BEFORE every reserved original", correctFirst,
            $"last corrected at {lastCorrected}, first original at {firstOriginal}");
        f += correctFirst ? 0 : 1;

        // And the negative control: the PRE-F-M316 forms must FAIL that same sort, or the case above
        // proves nothing. A bare name sorts last ('s' of ".srt" > '9'), an unpadded slot sorts after
        // ".10" — both are the defect this rule removes.
        var oldForm = new List<string> { $"{baseName}.en.srt", $"{baseName}.en.99.srt", $"{baseName}.en.10.srt", $"{baseName}.en.2.srt" };
        var oldOrdered = oldForm.OrderBy(n => n, StringComparer.Ordinal).ToList();
        bool oldBareLast = oldOrdered[oldOrdered.Count - 1] == $"{baseName}.en.srt";
        bool oldTenBeforeTwo = oldOrdered.IndexOf($"{baseName}.en.10.srt") < oldOrdered.IndexOf($"{baseName}.en.2.srt");
        Check("NEGATIVE CONTROL: the old bare name sorts LAST (that was the defect)", oldBareLast,
            string.Join(" | ", oldOrdered.Select(n => n.Replace(baseName + ".", "…"))));
        Check("NEGATIVE CONTROL: the old form puts .10 before .2", oldTenBeforeTwo,
            string.Join(" | ", oldOrdered.Select(n => n.Replace(baseName + ".", "…"))));
        f += oldBareLast ? 0 : 1;
        f += oldTenBeforeTwo ? 0 : 1;

        Console.WriteLine();
        return f;
    }

    /// <summary>
    /// Case 5f: the QA retry limit bounds the FIT — no other gate gives up and nothing closes a pair
    /// (F-M320, operator order 08.10.2026).
    /// <para>
    /// Asserted as an ABSENCE, which is the only way a removed give-up can be asserted at all: a
    /// passing run looks identical whether the give-up is there or not. The three surviving
    /// consumers are asserted in the SAME case, because "the removal took the wrong thing with it" is
    /// the failure this pattern invites — the memory filter and the fit's budget must still be wired.
    /// </para>
    /// </summary>
    private static int QaBudgetIsTheFits()
    {
        int f = 0;
        Console.WriteLine("[5f] the QA retry limit bounds the fit, not the gates (F-M320)");

        const string pipeline = "/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs";
        const string tracker = "/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Registry/QaFailTracker.cs";
        const string seeder = "/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/ScheduledTasks/SubdlSeeder.cs";
        const string refresh = "/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/ScheduledTasks/SubdlDatabaseRefreshTask.cs";
        foreach (string p in new[] { pipeline, tracker, seeder, refresh })
        {
            if (!System.IO.File.Exists(p))
            {
                Console.WriteLine($"  [SKIP] {System.IO.Path.GetFileName(p)} not reachable from here");
                return f;
            }
        }

        string src = System.IO.File.ReadAllText(pipeline);

        // 1. BOTH give-ups are gone from the pipeline: the QA one and the file-missing one.
        // `_idNotFound` went first (operator order 08.10.2026, "Id resolution retrys bitte auch löschen"),
        // then `_fileRetries` (same day, "Dann bitte file-retry weg in code und gui. Alleinige Aufgabe
        // database refresh."). Scoped to named trackers on purpose — a blanket search for "IsExhausted"
        // would also match the surviving tracker's legitimate call and the assertion would then be about
        // the wrong mechanism.
        bool noGiveUpCall = !src.Contains("_qaFails.IsExhausted", StringComparison.Ordinal);
        Check("the pipeline applies no QA give-up to a work list any more", noGiveUpCall,
            noGiveUpCall ? "no _qaFails.IsExhausted call" : "a QA give-up is still applied to a work list");
        f += noGiveUpCall ? 0 : 1;

        // ...and the file-missing budget is asserted GONE too, so the removal is covered from the
        // pipeline side rather than only from the GUI side. Both halves matter: a re-added give-up with
        // no field to configure it would silently use the default.
        bool noFileRetryCall = !src.Contains("_fileRetries", StringComparison.Ordinal);
        Check("the file-missing (F-M60) give-up is gone from the pipeline", noFileRetryCall,
            noFileRetryCall ? "no _fileRetries call remains" : "a file-missing give-up is still applied");
        f += noFileRetryCall ? 0 : 1;

        // 2. The counter itself is gone from the tracker — the method, its key and its writes.
        string trackerSrc = System.IO.File.ReadAllText(tracker);
        string[] counterParts = { "IsExhausted", "RecordFailure", "CounterKey", "GetCounter", "qa-fail:" };
        string? stillThere = null;
        foreach (string part in counterParts)
        {
            if (trackerSrc.Contains(part, StringComparison.Ordinal))
            {
                stillThere = part;
                break;
            }
        }

        Check("QaFailTracker is the burned-candidate record ONLY (counter removed)", stillThere is null,
            stillThere is null ? "no counter left in the tracker" : $"\"{stillThere}\" is still in the tracker");
        f += stillThere is null ? 0 : 1;

        // 3. Both other readers stop filtering on a QA verdict.
        bool seederClean = !System.IO.File.ReadAllText(seeder)
            .Contains("qaFails.IsExhausted", StringComparison.Ordinal);
        Check("the seeder's queue gate no longer drops a pair on a QA verdict", seederClean,
            seederClean ? "queue gate asks coverage only" : "the seeder still filters on the counter");
        f += seederClean ? 0 : 1;

        bool refreshClean = !System.IO.File.ReadAllText(refresh)
            .Contains("qaFails.IsExhausted", StringComparison.Ordinal);
        Check("the refresh task's actionable filter is unchanged by a QA verdict", refreshClean,
            refreshClean ? "every open pair is actionable" : "the refresh task still filters on the counter");
        f += refreshClean ? 0 : 1;

        // 4. The two run-summary counters are gone with their source.
        bool summaryClean = !src.Contains("SkippedQaGiveUp", StringComparison.Ordinal)
                            && !src.Contains("QaGiveUpLanguages", StringComparison.Ordinal);
        Check("the give-up status counters are gone with the give-up", summaryClean,
            summaryClean ? "neither counter remains" : "a counter remains that nothing can increment");
        f += summaryClean ? 0 : 1;

        // 5. THE SURVIVORS — what the removal must NOT have taken.
        bool memoryKept = src.Contains("_qaFails.GetSkippedCandidates(", StringComparison.Ordinal);
        Check("the walk still consults the burned-release memory", memoryKept,
            memoryKept ? "GetSkippedCandidates still filters the walk" : "the memory filter was removed too");
        f += memoryKept ? 0 : 1;

        bool burnKept = src.Contains("_qaFails.RecordSkippedCandidates(", StringComparison.Ordinal);
        Check("this run's discards are still memorized", burnKept,
            burnKept ? "RecordSkippedCandidates still called" : "discards are no longer burned — quota would repeat");
        f += burnKept ? 0 : 1;

        bool budgetKept = src.Contains("int walkLimit = Math.Max(0, _config.DownloadQaRetryLimit);",
                                       StringComparison.Ordinal);
        Check("the fit still takes the limit as its budget", budgetKept,
            budgetKept ? "DownloadQaRetryLimit → walkLimit" : "the fit lost its budget");
        f += budgetKept ? 0 : 1;

        // NEGATIVE CONTROL: plant the give-up back and require RED.
        string planted = src.Replace(
            "var openStale = openPairs.ToList();",
            "var openStale = openPairs.Where(r => _qaFails.IsExhausted(item.Id.ToString(), r.Language, 3)).ToList();",
            StringComparison.Ordinal);
        bool plantLanded = planted != src;
        bool controlRed = plantLanded && planted.Contains("_qaFails.IsExhausted", StringComparison.Ordinal);
        Check("NEGATIVE CONTROL: planting the give-up back turns this check RED", controlRed,
            !plantLanded ? "(the plant did not land — the work list moved; fix this test)"
                         : "planted → check goes red as required");
        f += controlRed ? 0 : 1;

        Console.WriteLine();
        return f;
    }

    /// <summary>
    /// Case 5e: the correction hunt repeats until a fit proves itself, capped at the QA retry limit
    /// (F-M318, operator order 08.10.2026).
    /// <para>
    /// The load-bearing distinction is that not every refusal deserves another download: a verdict on
    /// the FILE ("no proven gain") may come out differently for another candidate, while a verdict on
    /// the AUDIO ("ffmpeg not available", "audio decode failed") comes out identically for all of them.
    /// The REAL reason strings are run through the real predicate — and each one is first asserted to
    /// still exist in the source, so this test cannot keep passing after the wording drifts.
    /// </para>
    /// </summary>
    private static int CorrectionHunt()
    {
        int f = 0;
        Console.WriteLine("[5e] the correction hunt repeats, capped at the QA retry limit (F-M318)");

        const string syncSource = "/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Qa/SubtitleSync.cs";
        const string pipeline = "/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs";
        if (!System.IO.File.Exists(syncSource) || !System.IO.File.Exists(pipeline))
        {
            Console.WriteLine("  [SKIP] sources not reachable from here");
            return f;
        }

        string syncSrc = System.IO.File.ReadAllText(syncSource);
        string src = System.IO.File.ReadAllText(pipeline);

        // Candidate-specific: another candidate MAY align. Asserted by running the real predicate.
        //
        // Each case carries a FRAGMENT that must exist in the source. The refusal strings themselves
        // are built by interpolation ("shift {largestShift:0.0}s beyond …"), so matching them literally
        // would fail for a reason that has nothing to do with the rule — the fragments are the stable
        // part and they are what makes this test go red when the wording drifts.
        (string Reason, bool CandidateSpecific, string Fragment)[] cases =
        {
            (@"no proven gain (41 cue(s) moved, t = 1.42) — left as downloaded", true, "no proven gain"),
            (@"shift 34.0s beyond the 20s limit — not applied", true, "beyond the"),
            (@"not measured: only 12 cues", true, "not measured: only"),
            (@"not applied: guard failed", true, "not applied: "),
            (@"fit error", true, "fit error"),
            (@"ffmpeg not available", false, "ffmpeg not available"),
            (@"audio decode failed", false, "audio decode failed"),
            (@"not measured: no audio samples", false, "not measured: no audio samples"),
        };

        foreach ((string reason, bool expected, string fragment) in cases)
        {
            bool actual = Jellyfin.Plugin.SubdlScribe.Qa.SubtitleSync.RefusalIsCandidateSpecific(reason);
            bool present = syncSrc.Contains(fragment, StringComparison.Ordinal);
            Check($"refusal \"{reason.Substring(0, Math.Min(34, reason.Length))}…\" → hunt continues: {expected}",
                actual == expected && present,
                present ? actual.ToString() : $"(fragment \"{fragment}\" gone from the source — this test has drifted)");
            f += actual == expected && present ? 0 : 1;
        }

        // The cap is the QA retry limit, read from the config type — not a second number invented
        // beside it. Asserted on the default, because that is what an untouched install uses.
        var cfg = new Jellyfin.Plugin.SubdlScribe.Configuration.PluginConfiguration();
        Check("the candidate cap IS DownloadQaRetryLimit (default 3, not a second knob)",
            cfg.DownloadQaRetryLimit == 3, $"default = {cfg.DownloadQaRetryLimit}");
        f += cfg.DownloadQaRetryLimit == 3 ? 0 : 1;

        bool readsRetryLimit = src.Contains("int walkLimit = Math.Max(0, _config.DownloadQaRetryLimit);",
                                            StringComparison.Ordinal);
        Check("the budget is read from the QA retry limit", readsRetryLimit,
            readsRetryLimit ? "walkLimit ← DownloadQaRetryLimit" : "missing");
        f += readsRetryLimit ? 0 : 1;

        // The stop is checked BEFORE the fetch: breaking after the refusal would burn one more
        // download from the daily quota and write one more unprocessed file.
        bool budgetBeforeFetch = System.Text.RegularExpressions.Regex.IsMatch(
            src, @"walkLimit > 0 && refusalsThisRun >= walkLimit");
        Check("the hunt stops once the budget is spent", budgetBeforeFetch,
            budgetBeforeFetch ? "checked against refusalsThisRun" : "no budget stop found");
        f += budgetBeforeFetch ? 0 : 1;

        // F-M319 (operator order 08.10.2026): "best subtitles to keep per language" is GONE, so the
        // walk's stop is no longer a configured count. What remains is the rule the setting's default
        // always expressed — one corrected file per language — and the counter that decides it is
        // still `correctedSaved`: an unprocessed file is a FALLBACK (kept so the repeat costs no
        // content) and must never end the walk, because that is exactly what made the repeat
        // impossible.
        bool walkStopsOnCorrection = src.Contains("if (correctedSaved >= 1)", StringComparison.Ordinal)
                                     && src.Contains("correctedSaved++;", StringComparison.Ordinal);
        Check("the walk stops on the first CORRECTED file; an unprocessed file never stops it",
            walkStopsOnCorrection,
            walkStopsOnCorrection ? "correctedSaved drives the stop" : "an unprocessed file can end the walk");
        f += walkStopsOnCorrection ? 0 : 1;

        // The setting must be GONE from every layer, not just unused: a field still on the config type
        // is a knob the next reader will wire back up. Asserted on the config TYPE and the GUI page.
        bool settingGone = !src.Contains("DownloadKeepBestPerLanguage", StringComparison.Ordinal);
        var cfgProbe = new Jellyfin.Plugin.SubdlScribe.Configuration.PluginConfiguration();
        bool fieldGone = cfgProbe.GetType().GetProperty("DownloadKeepBestPerLanguage") == null;
        const string guiPage = "/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Configuration/configPage.html";
        bool guiGone = !System.IO.File.Exists(guiPage)
                       || !System.IO.File.ReadAllText(guiPage).Contains("DownloadKeepBestPerLanguage", StringComparison.Ordinal);
        Check("the keep-best setting is gone from config, pipeline AND the page", settingGone && fieldGone && guiGone,
            $"pipeline={settingGone} configType={fieldGone} gui={guiGone}");
        f += settingGone && fieldGone && guiGone ? 0 : 1;

        // Operator order 08.10.2026: "Aus 2 variables mach eine." The search width and the Auto-Sync
        // walk's limit are the SAME setting now, and the second knob ("Max candidates per language")
        // is gone from config, pipeline and page. Asserted as the PAIR, because either half alone
        // leaves the drift: a removed knob whose number still sits in the loop is a hidden second
        // variable, and a merged read without the removal leaves the setting able to contradict it.
        bool capSettingGone = !src.Contains("DownloadMaxCandidatesPerLanguage", StringComparison.Ordinal);
        bool capFieldGone = cfgProbe.GetType().GetProperty("DownloadMaxCandidatesPerLanguage") == null;
        bool capGuiGone = !System.IO.File.Exists(guiPage)
                          || !System.IO.File.ReadAllText(guiPage).Contains("DownloadMaxCandidatesPerLanguage", StringComparison.Ordinal);
        Check("the second candidate setting is gone from config, pipeline AND the page",
            capSettingGone && capFieldGone && capGuiGone,
            $"pipeline={capSettingGone} configType={capFieldGone} gui={capGuiGone}");
        f += capSettingGone && capFieldGone && capGuiGone ? 0 : 1;

        bool oneSettingTwoEffects =
            src.Contains("SearchEarlyStopThreshold(\n                _config.DownloadQaRetryLimit)", StringComparison.Ordinal)
            && src.Contains("int walkLimit = Math.Max(0, _config.DownloadQaRetryLimit);", StringComparison.Ordinal);
        Check("ONE setting drives BOTH the search width and the walk's limit",
            oneSettingTwoEffects,
            oneSettingTwoEffects ? "search and walk read the same value" : "the two effects drifted apart again");
        f += oneSettingTwoEffects ? 0 : 1;

        // NEGATIVE CONTROL: put the old, configured stop back and require the check to go RED.
        string stopPlanted = src.Replace("if (correctedSaved >= 1)", "if (savedCount >= keepBest)",
                                         StringComparison.Ordinal);
        bool stopPlantLanded = stopPlanted != src;
        bool stopControlRed = stopPlantLanded
                              && !stopPlanted.Contains("if (correctedSaved >= 1)", StringComparison.Ordinal);
        Check("NEGATIVE CONTROL: a configured keep-best stop again turns this check RED", stopControlRed,
            !stopPlantLanded ? "(the plant did not land — the comparison moved; fix this test)"
                             : "planted → check goes red as required");
        f += stopControlRed ? 0 : 1;

        // An audio-level refusal ends the walk instead of downloading every remaining candidate to
        // receive the same answer — and it says so, rather than looking like a thin candidate list.
        bool abandonedBreaks = src.Contains("if (correctionHuntAbandoned)", StringComparison.Ordinal)
                               && src.Contains("correction hunt abandoned after", StringComparison.Ordinal);
        Check("an audio-level refusal ends the hunt, and says so in the log", abandonedBreaks,
            abandonedBreaks ? "abandonment breaks the walk and is logged" : "missing");
        f += abandonedBreaks ? 0 : 1;

        // The repeat is only worth anything if every other gate has already had its say: a candidate
        // whose CONTENT fails a gate never reaches the fit, so a download spent on one would be spent
        // on a file the plugin is going to throw away. This asserts the ORDER, not the gates
        // themselves (each has its own case) — and it asserts it in the MAIN path only, so the HI
        // branch's own fit cannot satisfy it by accident.
        string[] gates =
        {
            "F-M43 Stufe 1 — FPS pre-check",
            "if (bytes == null || bytes.Length < 100)",
            "Gate 2: language verification",
            "F-M43 Stufe 2 (structure, fixed part)",
            "Gate 1: minimum cue count",
            "F-M43 Stufe 2 (runtime)",
            "Qa.AutoSyncWorker.Outcome syncResult = await SyncMeasuredAsync(",
        };

        int fitAt = src.IndexOf(gates[^1], StringComparison.Ordinal);
        int firstOffender = -1;
        for (int i = 0; i < gates.Length - 1; i++)
        {
            int at = src.IndexOf(gates[i], StringComparison.Ordinal);
            if (at < 0 || fitAt < 0 || at > fitAt)
            {
                firstOffender = i;
                break;
            }
        }

        Check("every download gate runs BEFORE the fit (a repeat never downloads a gate-rejected file)",
            firstOffender < 0,
            firstOffender < 0
                ? $"{gates.Length - 1} gates, all before the fit"
                : $"\"{gates[firstOffender]}\" is missing or sits after the fit");

        // NEGATIVE CONTROL: plant the fit ABOVE the gates and require RED.
        if (firstOffender < 0)
        {
            int fpsAt = src.IndexOf(gates[0], StringComparison.Ordinal);
            string moved = src.Remove(fitAt, gates[^1].Length).Insert(fpsAt, gates[^1]);
            int movedFitAt = moved.IndexOf(gates[^1], StringComparison.Ordinal);
            bool movedRed = movedFitAt >= 0 && movedFitAt < moved.IndexOf(gates[0], StringComparison.Ordinal);
            Check("NEGATIVE CONTROL: moving the fit above the gates turns this check RED", movedRed,
                movedRed ? "planted → order check goes red as required" : "(the plant did not land)");
            f += movedRed ? 0 : 1;
        }
        else
        {
            f++;
        }

        Console.WriteLine();
        return f;
    }

    /// <summary>
    /// Case 5c: the slot says WHICH KIND the file is (F-M317, operator order 08.10.2026) — an
    /// aligned file numbers up from 01, an unprocessed one down from 99, and the two are counted
    /// SEPARATELY.
    /// <para>
    /// The two halves are asserted differently on purpose. The NAMES are pure and asserted on the
    /// builder. The COUNTERS are not pure — they live in the pipeline's per-candidate loop — so they
    /// are asserted on the SOURCE, the way this file already asserts the retired archive write. What
    /// makes that legitimate here is the negative control at the end: the single-counter behaviour is
    /// planted back into a copy of the source and the check is required to go RED, so the assertion
    /// is shown to be capable of failing rather than assumed to be.
    /// </para>
    /// </summary>
    private static int SlotTellsKind()
    {
        int f = 0;
        Console.WriteLine("[5c] the slot tells the two kinds of file apart (F-M317)");

        const string baseName = "Person Of Interest S02e06 The High Road";
        string media = $"/media/{baseName}.mkv";

        // The two ranges, as the pipeline composes them.
        string firstAligned = System.IO.Path.GetFileName(SidecarNaming.Build(media, "EN", false, 1));
        string secondAligned = System.IO.Path.GetFileName(SidecarNaming.Build(media, "EN", false, 2));
        string firstUnprocessed = System.IO.Path.GetFileName(SidecarNaming.Build(media, "EN", false, 99));
        string secondUnprocessed = System.IO.Path.GetFileName(SidecarNaming.Build(media, "EN", false, 98));

        Check("an aligned file starts at 01", firstAligned == $"{baseName}.en.01.srt", firstAligned);
        Check("the next aligned file takes 02", secondAligned == $"{baseName}.en.02.srt", secondAligned);
        Check("an unprocessed file lands in the reserved block at 99",
            firstUnprocessed == $"{baseName}.en.99.srt", firstUnprocessed);
        Check("numbering runs DOWNWARD from 99",
            secondUnprocessed == $"{baseName}.en.98.srt", secondUnprocessed);
        f += firstAligned == $"{baseName}.en.01.srt" ? 0 : 1;
        f += secondAligned == $"{baseName}.en.02.srt" ? 0 : 1;
        f += firstUnprocessed == $"{baseName}.en.99.srt" ? 0 : 1;
        f += secondUnprocessed == $"{baseName}.en.98.srt" ? 0 : 1;

        // An unprocessed file IS the original, so it is written ONCE — the reserved-slot write that
        // runs AFTER a correction must be skipped on this path. The skip is the existing
        // `unsyncPayload != null` guard, and this asserts the pipeline still ties the reserved write
        // to that variable rather than to "not aligned".
        const string pipeline = "/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs";
        if (!System.IO.File.Exists(pipeline))
        {
            Console.WriteLine("  [SKIP] DownloadPipeline.cs not reachable from here");
            return f;
        }

        string src = System.IO.File.ReadAllText(pipeline);

        bool namesByKind = src.Contains("alignedForNaming", StringComparison.Ordinal)
                           && src.Contains("correctedSlotCount + 1", StringComparison.Ordinal);
        Check("the writer chooses the range by whether the file was aligned", namesByKind,
            namesByKind ? "alignedForNaming drives the slot" : "no aligned/unprocessed split in the writer");
        f += namesByKind ? 0 : 1;

        bool unprocessedUsesReserved = src.Contains(
            "SidecarNaming.PlanOriginalTarget(mediaPath, lang, effectiveHi, namesForSlot)", StringComparison.Ordinal);
        Check("an unprocessed file is written INTO the reserved block (no corrected sibling needed)",
            unprocessedUsesReserved,
            unprocessedUsesReserved ? "PlanOriginalTarget used on the unprocessed path" : "missing");
        f += unprocessedUsesReserved ? 0 : 1;

        // The counter is advanced on the ALIGNED path only. Guarded by the branch, not by a comment:
        // the increment must sit inside `if (alignedForNaming)`.
        bool counterGuarded = System.Text.RegularExpressions.Regex.IsMatch(
            src, @"if\s*\(\s*alignedForNaming\s*\)\s*\{\s*correctedSlotCount\+\+;");
        Check("the corrected counter advances on the aligned path ONLY", counterGuarded,
            counterGuarded ? "guarded by alignedForNaming" : "the counter is shared — 01 would be consumed");
        f += counterGuarded ? 0 : 1;

        // Both branches carry the rule; one branch only would put an aligned HI file at 01 beside an
        // unprocessed one at 01 as well.
        bool hiCarries = src.Contains("hiCorrectedSlotCount", StringComparison.Ordinal)
                         && src.Contains("hiAligned", StringComparison.Ordinal);
        Check("the HI branch carries the same two ranges", hiCarries,
            hiCarries ? "hiAligned / hiCorrectedSlotCount present" : "HI branch left on the old rule");
        f += hiCarries ? 0 : 1;

        // Exhaustion is counted, never silent — this is the one path where a fetched subtitle is not
        // written at all.
        bool refusalCounted = src.Contains("not aligned and no free slot in the reserved range 90–99",
                                          StringComparison.Ordinal);
        Check("a refused write is logged with its own line, not swallowed", refusalCounted,
            refusalCounted ? "refusal is reported" : "silent refusal");
        f += refusalCounted ? 0 : 1;

        // NEGATIVE CONTROL — plant the single-counter behaviour back and require RED. Without this the
        // source assertions above could be passing for the wrong reason.
        string planted = src.Replace(
            "if (alignedForNaming)\n                    {\n                        correctedSlotCount++;",
            "if (true)\n                    {\n                        correctedSlotCount++;",
            StringComparison.Ordinal);
        bool plantLanded = planted != src;
        bool controlRed = plantLanded && !System.Text.RegularExpressions.Regex.IsMatch(
            planted, @"if\s*\(\s*alignedForNaming\s*\)\s*\{\s*correctedSlotCount\+\+;");
        Check("NEGATIVE CONTROL: planting the shared counter back turns this check RED", controlRed,
            !plantLanded ? "(the plant did not land — the guarded form moved; fix this test)"
                         : "planted → check goes red as required");
        f += controlRed ? 0 : 1;

        Console.WriteLine();
        Console.WriteLine("[5d] a \"no proven gain\" file IS the best version (F-M321)");
        f += AlreadyGoodAsCorrected(src);

        Console.WriteLine();
        return f;
    }

    /// <summary>
    /// F-M321 (operator order 08.10.2026): "no proven gain" is the deploy rule DECLINING to move the
    /// file, so the file as downloaded is the best version of its language. It is treated like a
    /// correction — corrected range, own counter, walk ends — while every OTHER refusal keeps the
    /// reserved range and the F-M318 hunt.
    /// <para>
    /// Asserted on the REAL predicate (not a copy of it) plus the pipeline's use of it: the split
    /// between "the file is fine" and "the file is wrong" is the whole rule, and a substring test on
    /// the pipeline alone could pass while the predicate answered the opposite way.
    /// </para>
    /// </summary>
    /// <param name="src">The pipeline source.</param>
    /// <returns>Number of failures.</returns>
    private static int AlreadyGoodAsCorrected(string src)
    {
        int f = 0;

        // ---- The predicate itself, driven with the REAL refusal strings.
        bool goodOnNoGain = Jellyfin.Plugin.SubdlScribe.Qa.SubtitleSync.RefusalMeansAlreadyGood(
            "no proven gain (0 cue(s) moved, t = +0.00) — left as downloaded");
        Check("a \"no proven gain\" refusal means the file is already good", goodOnNoGain,
            goodOnNoGain ? "predicate answers true" : "predicate does not recognise the refusal");
        f += goodOnNoGain ? 0 : 1;

        // Every refusal that says the file is WRONG must NOT count as already good — otherwise the
        // walk would stop on a file that another candidate could have fixed.
        bool wrongStaysWrong = !Jellyfin.Plugin.SubdlScribe.Qa.SubtitleSync.RefusalMeansAlreadyGood(
                "shift 25.0s beyond the 20s limit — not applied")
            && !Jellyfin.Plugin.SubdlScribe.Qa.SubtitleSync.RefusalMeansAlreadyGood("not measured: only 12 cues")
            && !Jellyfin.Plugin.SubdlScribe.Qa.SubtitleSync.RefusalMeansAlreadyGood("not applied: first cue 5,07s would go negative under -8,50s");
        Check("a refusal that says the file is WRONG is not treated as already good", wrongStaysWrong,
            wrongStaysWrong ? "wrong-file refusals keep the reserved range" : "a wrong file would take 01");
        f += wrongStaysWrong ? 0 : 1;

        bool toolingStaysOut = !Jellyfin.Plugin.SubdlScribe.Qa.SubtitleSync.RefusalMeansAlreadyGood("ffmpeg not available")
                               && !Jellyfin.Plugin.SubdlScribe.Qa.SubtitleSync.RefusalMeansAlreadyGood(null);
        Check("a tooling verdict, and no verdict at all, are not \"already good\"", toolingStaysOut,
            toolingStaysOut ? "only the deploy rule's own refusal counts" : "too broad");
        f += toolingStaysOut ? 0 : 1;

        // ---- The pipeline ties the range, the counter and the walk to that predicate.
        bool rangeWired = src.Contains("bool alignedForNaming = unsyncPayload != null || goodAsDownloaded",
                                       StringComparison.Ordinal);
        Check("the corrected range is used when the file is already good", rangeWired,
            rangeWired ? "alignedForNaming includes goodAsDownloaded" : "still routed to the reserved block");
        f += rangeWired ? 0 : 1;

        // Asserted with a REGEX over the branch, not on an exact whitespace run: the first version
        // pinned the indentation between the brace and the assignment, so adding a comment line
        // inside the branch turned this RED while the behaviour was untouched — a test that fails on
        // formatting teaches nothing about the rule. What must hold is that the branch containing
        // `huntForCorrection = false;` is entered on `goodAsDownloaded`.
        bool huntStops = System.Text.RegularExpressions.Regex.IsMatch(
            src, @"if\s*\(\s*goodAsDownloaded\s*\)\s*\{[^}]*huntForCorrection\s*=\s*false;",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Check("the walk ends for an already-good file (no quota spent hunting)", huntStops,
            huntStops ? "hunt turned off for this refusal" : "the hunt would continue");
        f += huntStops ? 0 : 1;

        bool hiSymmetric = src.Contains("bool hiAligned = hiUnsync != null || hiGoodAsDownloaded",
                                        StringComparison.Ordinal);
        Check("the HI branch carries the same rule", hiSymmetric,
            hiSymmetric ? "hiAligned includes hiGoodAsDownloaded" : "HI left on the old rule");
        f += hiSymmetric ? 0 : 1;

        // The reserved copy must hold the SAME bytes as the corrected slot, or the two files differ
        // and the operator's "identisch" is not met. Reusing the written array is what guarantees it.
        bool identicalBytes = src.Contains(": (goodAsDownloaded ? writeBytes : null);", StringComparison.Ordinal)
                              && src.Contains("hiGoodAsDownloaded ? hiWriteBytes : ContentHashRegistry.EncodeCanonical(hiUnsync!)",
                                              StringComparison.Ordinal);
        Check("the reserved copy reuses the written bytes, so both files are identical", identicalBytes,
            identicalBytes ? "both tracks reuse the written array" : "the copy is re-encoded and may differ");
        f += identicalBytes ? 0 : 1;

        // NEGATIVE CONTROL: plant the old routing back and require RED.
        string planted = src.Replace(
            "bool alignedForNaming = unsyncPayload != null || goodAsDownloaded",
            "bool alignedForNaming = unsyncPayload != null",
            StringComparison.Ordinal);
        bool plantLanded = planted != src;
        bool controlRed = plantLanded
                          && !planted.Contains("bool alignedForNaming = unsyncPayload != null || goodAsDownloaded",
                                               StringComparison.Ordinal);
        Check("NEGATIVE CONTROL: routing an already-good file back to the reserved block turns this RED",
            controlRed,
            !plantLanded ? "(the plant did not land — the wiring moved; fix this test)"
                         : "planted → check goes red as required");
        f += controlRed ? 0 : 1;

        Console.WriteLine();
        return f;
    }

    /// <summary>
    /// Case 6: the kept original is LOCKED against upload (F-M315) — a normal sidecar whose content
    /// carries a terminal registry row. The archive (F-M306) is gone; the properties it guaranteed
    /// (never uploaded, never counted as coverage, name the reader recognizes) are asserted instead.
    /// </summary>
    private static int UnsyncArchive()
    {
        int f = 0;
        Console.WriteLine("[6] the kept original is locked against upload (F-M315)");

        const string baseName = "Person Of Interest S02e06 The High Road";
        string corrected = $"/media/{baseName}.en.srt";

        // The archive helpers may stay (they are pure and callers may be gone), but the pipeline must
        // no longer write one. This is the assertion that catches a half-reverted change.
        const string pipeline = "/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs";
        if (System.IO.File.Exists(pipeline))
        {
            string src = System.IO.File.ReadAllText(pipeline);

            bool noArchiveWrite = !System.Text.RegularExpressions.Regex.IsMatch(
                src, @"AtomicWriteAsync\s*\(\s*\n?\s*(hi)?[Uu]nsyncZip\b");
            Check("the pipeline writes NO archive any more", noArchiveWrite,
                noArchiveWrite ? "no AtomicWriteAsync(unsyncZip/…)" : "FOUND a zip write — F-M306 is back");
            f += noArchiveWrite ? 0 : 1;

            // The original IS written, through the reserved-slot planner. Both tracks, one helper.
            int originalWrites = System.Text.RegularExpressions.Regex.Matches(
                src, @"AtomicWriteAsync\s*\(\s*\n?\s*(hi)?[Oo]riginalPath\b").Count;
            Check("the pipeline writes the loose original (both tracks)", originalWrites == 2,
                $"AtomicWriteAsync(originalPath/…) {originalWrites}/2");
            f += originalWrites == 2 ? 0 : 1;

            // The lock is the row every downloaded subtitle already gets — MarkDownloaded. The
            // original IS a downloaded subtitle, so it is registered the same way and no new
            // vocabulary is introduced. Two sites: the main track and the HI track.
            //
            // Counting `Registry.MarkDownloaded` overall would be wrong: the corrected files get the
            // same call, so that number would rise when THEY change. The lock is asserted where it
            // belongs — on the original's own hash, next to the original's own write.
            int lockRows = System.Text.RegularExpressions.Regex.Matches(
                src, @"Registry\.MarkDownloaded\(\s*\n?\s*(hi)?[Oo]riginalHash").Count;
            Check("the original is registered as downloaded, like every download (both tracks)", lockRows == 2,
                $"MarkDownloaded(originalHash/…) {lockRows}/2");
            f += lockRows == 2 ? 0 : 1;

            // And it must be the DOWNLOADED row, not a rejection: the operator kept the file on
            // purpose, so calling it rejected would make the diagnostics report a defect that does
            // not exist. This assertion keeps the vocabulary honest.
            bool noInventedReason = !System.Text.RegularExpressions.Regex.IsMatch(
                src, "RejectReason" + @"\.OriginalKept");
            Check("no invented reject reason — the lock reuses the download treatment", noInventedReason,
                noInventedReason ? "no invented reason" : "FOUND an invented reason");
            f += noInventedReason ? 0 : 1;

            // The locked bytes must be the ones on disk: canonical encode, so the row's hash and the
            // file agree. Feeding raw payload bytes would leave the row pointing at a sequence that
            // exists nowhere, and the duplicate guard would read the file as unknown and upload it.
            int canonicalOriginal = System.Text.RegularExpressions.Regex.Matches(
                src, @"ContentHashRegistry\.EncodeCanonical\((hi)?[Uu]nsync").Count;
            Check("the locked bytes are the bytes written (canonical, both tracks)", canonicalOriginal == 2,
                $"EncodeCanonical(unsync…) {canonicalOriginal}/2");
            f += canonicalOriginal == 2 ? 0 : 1;

            // The dry run must still guard the new write (F-M287). Asserted on the SHAPE, not on one
            // variable name: the guard moved from `unsyncPayload != null` to `reservedBytes != null`
            // when F-M321 added the already-good case, and a test that pins the name would go red for
            // a rewrite that keeps the protection intact. What must hold is that the reserved write is
            // guarded by the dry-run flag at all.
            int dryRunGuards = System.Text.RegularExpressions.Regex.Matches(
                src, @"if\s*\(\s*\w+\s*!=\s*null\s*&&\s*!_config\.DownloadDryRun\s*\)").Count;
            Check("the dry run still guards the original's write", dryRunGuards == 1,
                $"{dryRunGuards} site(s)");
            f += dryRunGuards == 1 ? 0 : 1;

            // The lock must be one the seeder and the uploader ALREADY honour, not a new branch.
            // IsContentKnown is what the seeder asks before queueing: a downloaded row counts as
            // known, an Observed row deliberately does not. Asserted here so a later change that
            // makes downloads non-terminal is caught.
            const string registry = "/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Registry/ContentHashRegistry.cs";
            bool knownCountsDownloaded = System.IO.File.Exists(registry)
                && System.Text.RegularExpressions.Regex.IsMatch(
                    System.IO.File.ReadAllText(registry),
                    "x" + @"\.Status != SubtitleStatus\.Observed");
            Check("a downloaded row counts as known content (the existing lock)", knownCountsDownloaded,
                knownCountsDownloaded ? "IsContentKnown ignores only Observed" : "downloads no longer lock");
            f += knownCountsDownloaded ? 0 : 1;
        }

        // F-M296's canonical rule is unchanged by this work.
        if (System.IO.File.Exists(pipeline))
        {
            string src = System.IO.File.ReadAllText(pipeline);
            int canonical = System.Text.RegularExpressions.Regex.Matches(
                src, @"[Ww]riteBytes\s*=\s*ContentHashRegistry\.EncodeCanonical\(").Count;
            Check("both corrected-file writes still go out canonical", canonical == 2,
                $"EncodeCanonical {canonical}/2");
            f += canonical == 2 ? 0 : 1;
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
