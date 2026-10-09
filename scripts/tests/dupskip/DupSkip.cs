// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// T113 (F-M298): ein duplicate-content-Skip darf eine bereits vorhandene uploaded-Marke NICHT
// ueberschreiben.
//
// Nachgestellt wird der Prod-Fall vom 06.10.2026 (Lanterns S01E08 "Dirt and Stars"):
//   1. Der Upload laeuft und die Sprache landet als "uploaded" in der Registry.
//   2. Ein spaeterer Seeder-Lauf schreibt Sprach-Tags in den Container; die Datei bekommt einen
//      NEUEN Media-Hash, und ReplaceMediaIdentity traegt die Zeilen auf die neue Identitaet um.
//   3. Der naechste Upload-Lauf sieht denselben Inhalt, IsContentKnown() meldet "bekannt" (eine
//      rejected-Zeile zaehlt dort genauso wie eine uploaded-Zeile) und der Skip schrieb "rejected"
//      ueber die mitgenommene uploaded-Marke. Ergebnis auf prod: 39x rejected, 0x uploaded — die
//      Datei galt als unerledigt und wurde in jedem weiteren Lauf neu extrahiert und neu abgelehnt.
//
// Der Test faehrt die ECHTE Entscheidung (ContentHashRegistry.RecordSkippedContent), nicht eine
// Kopie davon. Zwei Faelle:
//   A) Inhalt ist nachweislich oben  -> die Marke bleibt "uploaded", nichts wird gezaehlt.
//   B) Inhalt ist NICHT oben (nur eine rejected-Zeile) -> die Ablehnung wird geschrieben wie bisher.
//
// Fall B ist die Absicherung: ohne ihn wuerde ein Fix, der einfach jeden duplicate-content-Skip
// als uploaded verbucht, hier ebenfalls "OK" melden — und luegen.
using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Data;
using Jellyfin.Plugin.SubdlScribe.Registry;

internal static class DupSkip
{
    private static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "dupskip");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);

        using var db = new SubdlDbContext(Path.Combine(root, "db"), null);
        var reg = new ContentHashRegistry(db, null, null);

        const string oldHash = "e621029b4734e366";
        const string newHash = "6c0dd8e8143a2e52";
        const string lang = "EN";
        string text = "\uFEFF1\r\n00:00:01,000 --> 00:00:03,000\r\nHello there.\r\n\r\n";
        string contentHash = ContentHashRegistry.ComputeHash(text);

        // ---- Zustand vor dem Tag-Schreiben: die Sprache ist oben und als uploaded vermerkt.
        reg.EnsureMedia(oldHash, "tt1234567", "/data/movies2/Lanterns/S01/Lanterns.S01E08.mkv");
        reg.MarkEmbed(oldHash, 0, lang, false, SubtitleStatus.Uploaded, contentHash: contentHash);
        Console.WriteLine("Schritt 1  alt=" + oldHash + " uploaded-Zeilen=" + UploadedRows(db, contentHash));

        int moved = reg.ReplaceMediaIdentity(oldHash, newHash);
        Console.WriteLine("Schritt 2  identity moved, " + moved + " Zeile(n) umgetragen -> " + newHash);
        Console.WriteLine("           uploaded-Zeilen unter neuer Identitaet=" + UploadedRows(db, contentHash));

        // ---- Schritt 3a: der naechste Lauf sieht denselben Inhalt und ueberspringt ihn.
        // IsContentKnown() ist hier true — eine rejected-Zeile zaehlte genauso, deshalb feuert der Skip
        // auch dann, wenn der Inhalt laengst oben ist. Genau das war die Falle.
        Console.WriteLine();
        Console.WriteLine("Schritt 3  Inhalt bekannt (IsContentKnown) = " + reg.IsContentKnown(contentHash));
        bool settled = reg.RecordSkippedContent(
            newHash, false, 0, lang, false, false, contentHash, RejectReason.DuplicateContent);

        string statusAfter = reg.GetEmbed(newHash, 0)?.Status ?? "<keine Zeile>";
        Console.WriteLine("           RecordSkippedContent -> settled=" + settled + ", Status danach=" + statusAfter);
        Console.WriteLine("           uploaded-Zeilen=" + UploadedRows(db, contentHash));

        bool caseA = settled
            && statusAfter == SubtitleStatus.Uploaded
            && UploadedRows(db, contentHash) == 1;

        // ---- Fall B: Inhalt ist NICHT oben. Nur eine rejected-Zeile existiert (fremde Ablehnung,
        // z.B. duplicate-remote). Hier muss die Ablehnung stehen bleiben — sonst behauptet der Fix
        // einen Upload, den es nie gab.
        string otherText = "1\n00:00:05,000 --> 00:00:07,000\nSome other line.\n\n";
        string otherHash = ContentHashRegistry.ComputeHash(otherText);
        reg.MarkEmbed(newHash, 3, "DE", false, SubtitleStatus.Rejected,
            contentHash: otherHash, reason: RejectReason.DuplicateRemote);

        bool otherSettled = reg.RecordSkippedContent(
            newHash, false, 4, "DE", false, false, otherHash, RejectReason.DuplicateContent);

        string otherStatus = reg.GetEmbed(newHash, 4)?.Status ?? "<keine Zeile>";
        string otherReason = reg.GetEmbed(newHash, 4)?.Reason ?? "<kein Grund>";
        Console.WriteLine();
        Console.WriteLine("Fall B     Inhalt NICHT oben: settled=" + otherSettled + ", Status=" + otherStatus + ", Grund=" + otherReason);
        bool caseB = !otherSettled && otherStatus == SubtitleStatus.Rejected
            && otherReason == RejectReason.DuplicateContent;

        Console.WriteLine();
        Console.WriteLine("Fall A (Inhalt oben -> uploaded bleibt): " + (caseA ? "OK" : "FEHLER"));
        Console.WriteLine("Fall B (Inhalt nicht oben -> rejected):  " + (caseB ? "OK" : "FEHLER"));
        Console.WriteLine();
        Console.WriteLine(caseA && caseB
            ? ">>> OK: ein duplicate-content-Skip loescht keine uploaded-Marke, erfindet aber auch keine"
            : ">>> FEHLER: F-M298 nicht erfuellt");
        return caseA && caseB ? 0 : 1;
    }

    /// <summary>Zaehlt die uploaded-Zeilen zu diesem Inhalt im ganzen Bestand.</summary>
    private static int UploadedRows(SubdlDbContext db, string contentHash)
    {
        int n = db.Embeds.FindAll().Count(x => x.ContentHash == contentHash && x.Status == SubtitleStatus.Uploaded);
        return n + db.Sidecars.FindAll().Count(x => x.ContentHash == contentHash && x.Status == SubtitleStatus.Uploaded);
    }
}
