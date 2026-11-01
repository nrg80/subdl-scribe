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
// T164 / F-M375: a subtitle whose content speaks another language than its
// stream tag claims is CORRECTED, not thrown away.
//
// WHAT IS ASSERTED, against the shipped code:
//
//   1. THE VERDICT. `QaGates.CorrectedLanguage` answers the detected language
//      when the tag lies, and null when the tag holds — the caller has no way to
//      tell "no correction needed" from "no confident verdict", and neither may
//      produce an upload under a guessed language.
//   2. THE FAMILY STAYS. A tag that names a relative (Norwegian written form,
//      Czech vs. Slovak, the Serbo-Croatian block) is not a lie; it is not
//      corrected either, because the label the operator saw is the honest one.
//   3. NO GUESSING. Text without a confident verdict corrects nothing.
//   4. THE GATE IS THE SWITCH. The pipeline only asks for a correction while
//      `QaVerifyLanguage` is on, and it writes no language mismatch reject any
//      more — the old reject code stays readable for rows from older runs, but
//      it is no longer produced.
//   5. THE CORRECTION FOLLOWS. The corrected language replaces the tag in the
//      candidate the rest of the run sees, so the upload name, the SubDL
//      language and the season pack all carry the language the text is in.
//   6. THE GUI SAYS SO. The checkbox description starts with "Corrects" and names
//      the detected language as the one uploaded under.
//
// Usage: dotnet run --project scripts/tests/langcorrect/langcorrect.csproj [repo-root]

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SubdlScribe.Qa;

namespace LangCorrectRun;

internal static class LangCorrectRun
{
    private static int _checks;
    private static readonly List<string> Failures = new();

    private static void Check(bool ok, string what)
    {
        _checks++;
        Console.WriteLine((ok ? "  OK   " : "  FAIL ") + what);
        if (!ok)
        {
            Failures.Add(what);
        }
    }

    private static string Flatten(string s) => Regex.Replace(s, "\\s+", " ");

    private static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";
        Console.WriteLine("T164 / F-M375 — a lying language tag is corrected, not rejected");
        Console.WriteLine();

        // Realistic bodies: detectability needs whole sentences, not cue stubs.
        string french = Cues(
            "Précédemment dans Foundation…",
            "On nous a attaqués et nous avons perdu la moitié de la flotte.",
            "Que ressens-tu maintenant que tu es enfin libre ?",
            "Je ne sais pas si je peux encore lui faire confiance après tout ça.");
        string german = Cues(
            "Bisher bei Foundation: das Imperium zerfällt, und niemand hält es auf.",
            "Wir müssen den Sprung wagen, sonst sterben wir hier alle.",
            "Ich habe dir vertraut, und du hast mich belogen, vor allen anderen.",
            "Die Nachricht kam aus Trantor, aber der Absender war längst tot.");
        string english = Cues(
            "Previously on Foundation, the Empire was already falling apart.",
            "We have to make the jump, or we will all die here tonight.",
            "I trusted you, and you lied to me in front of everyone else.",
            "The message came from Trantor, but the sender had been dead for years.");
        string nynorsk = Cues(
            "Tidlegare i Foundation gjekk imperiet i oppløysing utan at nokon stoppa det.",
            "Vi må ta spranget, elles døyr vi alle her i natt, sa ho til dei.",
            "Eg stolte på deg, og du logn for meg framfor alle dei andre i salen.");
        string noVerdict = Cues("1234", "5678", "9012");

        // ── 1. the verdict ──────────────────────────────────────────────────────
        Console.WriteLine("1. The verdict");
        Check(QaGates.CorrectedLanguage("DE", french) == "FR",
            "a German tag on French text corrects to FR");
        Check(QaGates.CorrectedLanguage("FR", german) == "DE",
            "a French tag on German text corrects to DE");
        Check(QaGates.CorrectedLanguage("EN", english) == null,
            "a tag that holds corrects nothing");
        Check(QaGates.CorrectedLanguage("DE", german) == null,
            "the measured case: a correct German track keeps its DE");

        // ── 2. the family stays ──────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("2. A relative is not a lie");
        Check(QaGates.CorrectedLanguage("NO", nynorsk) == null,
            "a Norwegian tag on Nynorsk text is not corrected");
        Check(QaGates.CorrectedLanguage("NO", Cues(
                "Tidligere i Foundation falt imperiet sammen uten at noen stoppet det her.",
                "Vi må ta spranget, ellers dør vi alle her i natt, sa hun til dem.")) == null,
            "a Norwegian tag on Bokmål text is not corrected");

        // ── 3. no guessing ──────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("3. No confident verdict, no correction");
        Check(QaGates.CorrectedLanguage("DE", noVerdict) == null, "numbers only correct nothing");
        Check(QaGates.CorrectedLanguage("DE", string.Empty) == null, "empty text corrects nothing");

        // ── 4. the gate is the switch ───────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("4. The checkbox decides, and no mismatch reject is written");
        string pipeline = Flatten(File.ReadAllText(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs")));
        Check(pipeline.Contains("if (_config.QaVerifyLanguage) { correctedLang = Qa.QaGates.CorrectedLanguage(lang, srtContent);"),
            "the correction only runs behind the QaVerifyLanguage checkbox");
        Check(!pipeline.Contains("return $\"qa-lang-mismatch (tag {lang}, content {detected})\";"),
            "the language mismatch is no longer a reject");
        Check(pipeline.Contains("qa-lang-mismatch"),
            "rows from older runs stay readable (the reject code survives in the explainer)");
        Check(pipeline.Contains("out string? correctedLang"),
            "the precheck hands the corrected language back to its caller");

        // ── 5. the correction follows ────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("5. The corrected language travels on");
        Check(pipeline.Contains("qaSurvivors.Add((cand.IsLoose, cand.Stream, cand.SubPos, cand.LoosePath, correctedLang,"),
            "the candidate the run keeps carries the corrected language");
        Check(pipeline.Contains("lang = correctedLang ?? lang;"),
            "the retry path corrects before it names the upload");
        Check(pipeline.Contains("[SubDL] CORRECT {File} [{Tag}]"),
            "a correction is logged with the tag that lied");

        // ── 6. the GUI says so ───────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("6. The GUI says so");
        string page = Flatten(File.ReadAllText(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Configuration/configPage.html")));
        string uploadGate = page.Substring(
            page.IndexOf("id=\"QaVerifyLanguage\"", StringComparison.Ordinal),
            Math.Min(600, page.Length - page.IndexOf("id=\"QaVerifyLanguage\"", StringComparison.Ordinal)));
        Check(uploadGate.Contains("Corrects a subtitle whose content speaks another language than its tag claims"),
            "the upload description says what it corrects");
        Check(uploadGate.Contains("uploaded under the detected language"),
            "… and where it goes: under the detected language");
        Check(uploadGate.Contains("Default: on."), "… and its default");
        Check(page.Contains("Rejects uploads with wrong language tag. Default: on."),
            "the download gate keeps its reject wording — it saves files, it does not upload them");

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? $"langcorrect: {_checks} checks, all green."
            : $"langcorrect: {_checks} checks, {Failures.Count} FAILED.");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static string Cues(params string[] lines)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            int start = 1 + (i * 5);
            sb.Append(i + 1).Append('\n')
              .Append("00:00:").Append(start.ToString("00", System.Globalization.CultureInfo.InvariantCulture))
              .Append(",000 --> 00:00:").Append((start + 4).ToString("00", System.Globalization.CultureInfo.InvariantCulture))
              .Append(",000\n")
              .Append(lines[i]).Append("\n\n");
        }

        return sb.ToString();
    }
}
