// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// F-M452 (operator order 09.11.2026): "ok ist erst wenn eine Version vorliegt die die
// Signifikanz bestanden hat". A file from the reserved ORIGINAL block (51–99) is the untouched
// download kept beside a refused alignment — it exists because the file proved wrong, so it must
// not close the language pair. The pair stays open, the walk keeps its N attempts, and a later
// candidate still has the chance to become slot 01.
//
// The checks drive the REAL coverage reader against real files in a temp directory. A rule about
// what a file on disk means cannot be verified by reading source text: the reader has to be asked.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Registry;

namespace CoverageRun;

internal static class Program
{
    private static int _checks;
    private static int _failed;

    private static void Check(bool ok, string text)
    {
        _checks++;
        if (!ok)
        {
            _failed++;
            Console.WriteLine("  [FAIL] " + text);
        }
        else
        {
            Console.WriteLine("  OK   " + text);
        }
    }

    private static int Main(string[] args)
    {
        string dir = Path.Combine(Path.GetTempPath(), "subdl-coverage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string media = Path.Combine(dir, "The_Rainmaker_S02E03-HDTV-X264-NGP.mp4");
            File.WriteAllBytes(media, Array.Empty<byte>());

            var en = new SubtitleRef("EN", hearingImpaired: false);
            var enHi = new SubtitleRef("EN", hearingImpaired: true);
            var de = new SubtitleRef("DE", hearingImpaired: false);

            List<SubtitleRef> Open(params SubtitleRef[] required)
                => SubtitleCoverage.Read(registry: null, media).Open(required);

            // The fit OFF: nothing is ever aligned, so every file lands in the reserved block and
            // the first download has to close the pair — otherwise no pair would ever close.
            List<SubtitleRef> OpenWithoutFit(params SubtitleRef[] required)
                => SubtitleCoverage.Read(registry: null, media, countOriginals: true).Open(required);

            // 1. Nothing on disk: everything is open.
            Check(Open(en, de).Count == 2, "an empty directory proves nothing — both pairs are open");

            // 2. The reserved original alone does NOT close the pair.
            File.WriteAllText(SidecarNaming.Build(media, "EN", hearingImpaired: false, slot: 51), "1\n00:00:01,000 --> 00:00:02,000\nx\n");
            Check(Open(en).Count == 1, "only .en.51.srt exists → EN stays open (an original is no proof)");

            // 3. The hearing-impaired original alone closes neither EN nor EN (HI).
            File.WriteAllText(SidecarNaming.Build(media, "EN", hearingImpaired: true, slot: 51), "1\n00:00:01,000 --> 00:00:02,000\nx\n");
            Check(Open(en, enHi).Count == 2, "only the two 51 originals exist → EN and EN (HI) stay open");

            // 4. A corrected slot closes the pair — that is the version the alignment proved.
            string corrected = SidecarNaming.Build(media, "EN", hearingImpaired: false, slot: 1);
            File.WriteAllText(corrected, "1\n00:00:01,000 --> 00:00:02,000\nx\n");
            Check(Open(en).Count == 0, "with .en.01.srt beside it the pair is closed — a proven version counts");
            Check(Open(enHi).Count == 1,
                "the variant stays open: .en.01 is plain text, and the only HI file is a 51 original");

            // 5. Deleting the corrected file re-opens the pair although the original is still there.
            File.Delete(corrected);
            Check(Open(en).Count == 1, "deleting the 01 re-opens the pair even though the 51 is still on disk");

            // 6. A name without a slot (written before F-M316) is in neither block and still counts.
            File.WriteAllText(Path.Combine(dir, "The_Rainmaker_S02E03-HDTV-X264-NGP.de.srt"), "1\n00:00:01,000 --> 00:00:02,000\nx\n");
            Check(Open(de).Count == 0, "an unnumbered .de.srt still proves DE — the rule only touches the reserved block");

            // 6b. With the audio fit off, the reserved block DOES close the pair.
            Check(OpenWithoutFit(en).Count == 0,
                "fit off: the 51 download closes EN — there is no alignment that could prove anything");
            Check(OpenWithoutFit(enHi).Count == 0,
                "fit off: the .sdh.51 closes the variant as well");

            // 7. The reader's slot parser is the one the writer writes.
            Check(SidecarNaming.SlotOf("Film.de.51", "Film") == 51
                  && SidecarNaming.SlotOf("Film.de.sdh.99", "Film") == 99
                  && SidecarNaming.SlotOf("Film.de.01", "Film") == 1,
                "SlotOf reads the slot the builder writes, marker or not");
            Check(SidecarNaming.SlotOf("Film.de", "Film") == 0
                  && SidecarNaming.SlotOf("Film", "Film") == 0,
                "a name without a slot reads as 0 — it belongs to neither block");
            Check(SidecarNaming.IsOriginalSlot(51) && SidecarNaming.IsOriginalSlot(99)
                  && !SidecarNaming.IsOriginalSlot(50) && !SidecarNaming.IsOriginalSlot(1),
                "the reserved block is 51–99, exactly as documented");

            Console.WriteLine($"coverage: {_checks} checks, {(_failed == 0 ? "all green" : _failed + " failed")}.");
            return _failed == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
