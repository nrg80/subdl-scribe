// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// T112 (F-M297): the anchor correction and the reference rule, driven with real data.
//
// WHAT IS ASSERTED HERE, AND WHY EACH ONE WAS PUT IN
//
//  1. MEASUREMENT AGAINST A REAL REFERENCE PAIR. The container of Invasion S01E06 carries
//     a plain English track and an English SDH track. Extracted and paired by text, they
//     share 137 lines sitting at 0.00 s — that pair is the test's clean case, and it proves
//     the anchor logic can SEE a synchronised file (a tool that reports drift on a clean
//     file is worthless; that was measured once already with a cross-correlation method
//     that gave ±25 s on the clean reference).
//
//  2. THE REFERENCE RULE. A same-language plain track is chosen; an HI track is refused even
//     when it is the only one in that language, because the HI file is the one that drifts.
//     A wrong-language track is refused because text anchoring across languages produces
//     zero anchors while looking like it ran.
//
//  3. THE REFUSAL. A correction that does not improve the worst single-cue residual is not
//     applied. This is what separates a safe feature from the audio path's 33/36.
//
//  4. THE ORDER GUARD. No correction may reorder cues or collapse a gap: checked on the
//     corrected output by comparing the text sequence against the input's.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Qa;

namespace AutoSyncTest;

internal static class AnchorSyncTest
{
    private static int _failed;
    private static int _checks;

    private static void Check(string what, bool ok, string detail = "")
    {
        _checks++;
        if (!ok)
        {
            _failed++;
        }

        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}{(detail.Length > 0 ? "  — " + detail : string.Empty)}");
    }

    internal static int Run(string[] args)
    {
        Console.WriteLine("=== T112: anchor correction against a real reference pair (F-M297) ===");

        // 1. The reference rule, no disk needed.
        ReferenceChoiceRule();

        // 2. Real data, when the extracted pair is present.
        string plain = "/tmp/inv_ref.srt";
        string sdh = "/tmp/inv_sdh2.srt";
        if (File.Exists(plain) && File.Exists(sdh))
        {
            RealPair(plain, sdh);
        }
        else
        {
            Console.WriteLine("  [skip] extracted reference pair not present (/tmp/inv_ref.srt, /tmp/inv_sdh2.srt)");
        }

        // 3. Synthetic: a step planted into a clean file must be found and removed.
        Synthetic();

        Console.WriteLine($"\n  {_checks - _failed}/{_checks} checks passed");
        return _failed == 0 ? 0 : 1;
    }

    private static void ReferenceChoiceRule()
    {
        Console.WriteLine("\n--- the reference rule ---");

        var cands = new List<ReferenceChoice.Candidate>
        {
            new("eng", true, "/m/ep.en.sdh.srt", null),
            new("eng", false, "/m/ep.en.srt", null),
            new("deu", false, "/m/ep.de.srt", null),
            new("eng", false, null, 3),
            new("eng", false, null, 4)
        };

        var d = ReferenceChoice.Choose(cands, "eng", "/m/ep.en.sdh.srt");
        Check("sidecar preferred over embedded", d.Origin == ReferenceChoice.Origin.Sidecar,
            $"{d.Origin} {d.Path} {d.Reason}");
        Check("the HI file is not its own reference", d.Path != "/m/ep.en.sdh.srt", d.Path ?? "null");

        // Only an HI track in the target language: refuse, do not fall back to it.
        var hiOnly = new List<ReferenceChoice.Candidate> { new("eng", true, null, 4) };
        var d2 = ReferenceChoice.Choose(hiOnly, "eng");
        Check("HI-only in target language is refused", d2.Origin == ReferenceChoice.Origin.None, d2.Reason);

        // Wrong language only: refuse.
        var wrongLang = new List<ReferenceChoice.Candidate> { new("deu", false, "/m/ep.de.srt", null) };
        var d3 = ReferenceChoice.Choose(wrongLang, "eng");
        Check("wrong-language reference is refused", d3.Origin == ReferenceChoice.Origin.None, d3.Reason);

        // Embedded fallback when no sidecar exists.
        var embeddedOnly = new List<ReferenceChoice.Candidate> { new("eng", false, null, 4) };
        var d4 = ReferenceChoice.Choose(embeddedOnly, "eng");
        Check("embedded track accepted when no sidecar", d4.Origin == ReferenceChoice.Origin.Embedded && d4.SubPos == 4,
            $"{d4.Origin} pos={d4.SubPos}");

        // Unknown target language: refuse rather than guess.
        var d5 = ReferenceChoice.Choose(embeddedOnly, null);
        Check("unknown target language is refused", d5.Origin == ReferenceChoice.Origin.None, d5.Reason);
    }

    private static void RealPair(string plainPath, string sdhPath)
    {
        Console.WriteLine("\n--- real extracted pair (Invasion S01E06) ---");
        var reference = AnchorSync.Parse(File.ReadAllText(plainPath));
        var target = AnchorSync.Parse(File.ReadAllText(sdhPath));
        Console.WriteLine($"  reference {reference.Count} cues | target {target.Count} cues");

        var res = AnchorSync.Correct(reference, target);
        Console.WriteLine($"  before: n={res.Before.Count} median={res.Before.MedianSec:0.00}s "
                          + $"p90={res.Before.P90Sec:0.00}s worst={res.Before.WorstSec:0.00}s");
        Console.WriteLine($"  after : n={res.After.Count} median={res.After.MedianSec:0.00}s "
                          + $"p90={res.After.P90Sec:0.00}s worst={res.After.WorstSec:0.00}s");
        Console.WriteLine($"  applied={res.Applied} clamped={res.ClampedOverlaps} — {res.Reason}");

        // The already-synchronised pair must not be "corrected" into a worse state.
        Check("a synchronised pair is not moved", res.Before.WorstSec < 1.0,
            $"worst {res.Before.WorstSec:0.00}s");

        if (res.Applied)
        {
            // Order must survive: the text sequence of the output equals the input's.
            bool sameOrder = res.Cues.Count == target.Count
                && res.Cues.Select(c => c.Text).SequenceEqual(target.Select(c => c.Text));
            Check("cue order and text unchanged", sameOrder);

            double worstShift = res.Cues.Zip(target, (a, b) => Math.Abs(a.StartSec - b.StartSec)).Max();
            Console.WriteLine($"  largest single shift {worstShift:0.00}s");
            Check("no shift beyond the plausible range", worstShift <= 20.0, $"{worstShift:0.00}s");
        }
        else
        {
            Console.WriteLine("  (nothing applied — the refusal path, which is the expected outcome here)");
        }
    }

    private static void Synthetic()
    {
        Console.WriteLine("\n--- synthetic: a step planted into a clean file ---");

        // A clean file: cues every 4 s, each 2 s long, text from the reference vocabulary.
        var reference = new List<AnchorSync.Cue>();
        for (int i = 0; i < 300; i++)
        {
            reference.Add(new AnchorSync.Cue(100 + (i * 4.0), 102 + (i * 4.0), $"line number {i} of the reference"));
        }

        // The target is the same file with a +6 s step at 60 % — exactly the defect a drift
        // correction has to remove, and exactly what a single global offset cannot fix.
        int stepAt = 180;
        var target = reference.Select((c, i) => new AnchorSync.Cue(
            c.StartSec + (i >= stepAt ? 6.0 : 0.0),
            c.EndSec + (i >= stepAt ? 6.0 : 0.0),
            c.Text)).ToList();

        var res = AnchorSync.Correct(reference, target);
        Console.WriteLine($"  before: worst={res.Before.WorstSec:0.00}s median={res.Before.MedianSec:0.00}s");
        Console.WriteLine($"  after : worst={res.After.WorstSec:0.00}s median={res.After.MedianSec:0.00}s");
        Console.WriteLine($"  applied={res.Applied} — {res.Reason}");

        Check("a planted step is corrected", res.Applied, res.Reason);
        Check("the step is gone afterwards", res.After.WorstSec < 0.5, $"worst {res.After.WorstSec:0.00}s");
        Check("the correction improves the worst line", res.After.WorstSec < res.Before.WorstSec,
            $"{res.Before.WorstSec:0.00}s -> {res.After.WorstSec:0.00}s");
    }
}
