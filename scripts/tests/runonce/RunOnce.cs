// T82 (F-M264): laeuft das Sprachcode-Gate pro Datei nur EINMAL?
//
// Nachgestellt wird der Prod-Fall vom 30.09.2026: ein Arrival durchlaeuft im selben Zyklus
// ZWEI Seeds (download, dann upload). Der zweite Seed fragt Jellyfin nach den Streams, und
// Jellyfin liefert seine GECACHTE Liste — die den gerade geschriebenen Tag noch nicht kennt.
//
// Der Test reicht dem zweiten Seed absichtlich dieselbe (alte) Streamliste wie dem ersten.
// Vorher: zweiter Schreibvorgang, Datei = neuer Hash. Nachher: 0 geschrieben, Datei unveraendert.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubdlScribe.Configuration;
using Jellyfin.Plugin.SubdlScribe.Data;
using Jellyfin.Plugin.SubdlScribe.Pipeline;
using Jellyfin.Plugin.SubdlScribe.Registry;
using Jellyfin.Plugin.SubdlScribe.ScheduledTasks;

internal static class RunOnce
{
    private static int Main(string[] args)
    {
        string media = args[0], root = args[1], ffmpeg = args[2];
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        string work = Path.Combine(root, "item.mkv");
        BuildProbe(work, ffmpeg);   // eigene Probe: 2 grosse Spuren + 1 zu kleine

        using var db = new SubdlDbContext(Path.Combine(root, "db"), null);
        var reg = new ContentHashRegistry(db, null, null);
        var cfg = new PluginConfiguration { LogMode = LogLevelMode.Verbose, AllocateMissingLanguageCodes = true };
        var log = new RecLogger();

        // Der Stand VOR dem ersten Lauf: genau das, was Jellyfin beim Import gelesen hat
        // (beide Spuren ohne Sprach-Tag). Diese Liste wird fuer BEIDE Seeds benutzt.
        var jellyfinCachedStreams = Probe(work);
        Console.WriteLine("Jellyfins (gecachte) Streams vor dem Lauf: "
            + string.Join(", ", jellyfinCachedStreams.Select((s, i) => "s" + i + "=" + (s.Language ?? "<none>"))));

        string h0 = reg.GetMediaHash(work);
        var st0 = Stat(work);
        reg.EnsureMedia(h0, "aa11bb22cc33dd44aa11bb22cc33dd44", work);
        Console.WriteLine("Start: hash=" + h0 + " size=" + st0.Size + " mtime=" + st0.MTime);

        // ---- Seed 1 (download): darf schreiben ----
        var gate1 = new LanguageTagGate(log, cfg, reg);
        var r1 = gate1.RunAsync(work, jellyfinCachedStreams, ffmpeg, CancellationToken.None).GetAwaiter().GetResult();
        string h1 = r1.NewHash ?? reg.GetMediaHash(work);
        // Wie der Seeder: erst den Identitaets-Umzug (der ALLE Zeilen mitzieht, auch die
        // Versuchs-Vermerke), dann die aufgeloesten Spuren unter dem neuen Hash festhalten.
        if (r1.OldHash != null && r1.NewHash != null) reg.ReplaceMediaIdentity(r1.OldHash, r1.NewHash);
        reg.EnsureMedia(h1, "aa11bb22cc33dd44aa11bb22cc33dd44", work);
        foreach (var (pos, lang, hi) in r1.Tracks) reg.ObserveEmbed(h1, pos, lang, hi);
        var st1 = Stat(work);
        Console.WriteLine();
        Console.WriteLine("SEED 1 (download): " + r1.Written + " geschrieben, " + r1.Tracks.Count + " Spuren erkannt");
        Console.WriteLine("  hash " + h0 + " -> " + h1 + " | size=" + st1.Size + " mtime=" + st1.MTime);
        var tags1 = Probe(work);
        Console.WriteLine("  Tags in der Datei jetzt: "
            + string.Join(", ", tags1.Select((s, i) => "s" + i + "=" + (s.Language ?? "<none>"))));

        // ---- Seed 2 (upload): Jellyfin liefert WEITER die alte Liste ----
        int logMark = log.Lines.Count;
        var gate2 = new LanguageTagGate(log, cfg, reg);
        var r2 = gate2.RunAsync(work, jellyfinCachedStreams, ffmpeg, CancellationToken.None).GetAwaiter().GetResult();
        string h2 = r2.NewHash ?? reg.GetMediaHash(work);
        var st2 = Stat(work);
        Console.WriteLine();
        Console.WriteLine("SEED 2 (upload, alte Streamliste): " + r2.Written + " geschrieben, " + r2.Tracks.Count + " Spuren erkannt");
        Console.WriteLine("  hash " + h1 + " -> " + h2 + " | size=" + st2.Size + " mtime=" + st2.MTime);

        int writesInPass2 = log.Lines.Skip(logMark).Count(x => x.Contains("language code(s) written into the container"));
        bool fileUntouched = st1.Size == st2.Size && st1.MTime == st2.MTime && h1 == h2;
        bool ok = r1.Written > 0 && r2.Written == 0 && fileUntouched;

        Console.WriteLine();
        Console.WriteLine("Schreib-Zeilen im 2. Lauf: " + writesInPass2 + " (erwartet 0)");
        Console.WriteLine(ok
            ? ">>> T82 OK: Datei wurde genau EINMAL geschrieben; der zweite Seed hat nichts mehr getan"
            : ">>> T82 FEHLER: erwartet 1 Schreibvorgang, gesehen " + (r1.Written > 0 ? "2" : r1.Written));

        // ---- F-M266: eine Spur, die die Erkennung NICHT aufloesen kann ----
        // Der Fall aus der Bibliothek: ein Track, dessen Sprache der Klassifizierer nicht
        // bestimmen kann. Der Tag fehlt weiter, die billige Vorpruefung im Seeder sagt also bei
        // JEDEM Scan "es gibt eine untagged Spur" — und ohne Marker wird die Datei jedes Mal
        // wieder geoeffnet. Erwartet: Lauf 1 versucht es und merkt es sich, Lauf 2 oeffnet die
        // Datei gar nicht mehr.
        Console.WriteLine();
        Console.WriteLine("=== F-M266: unaufloesbare Spur ===");
        string work2 = Path.Combine(root, "unresolvable.mkv");
        BuildProbe(work2, ffmpeg);

        string u0 = reg.GetMediaHash(work2);
        var ust0 = Stat(work2);
        reg.EnsureMedia(u0, "cc33dd44ee55ff66cc33dd44ee55ff66", work2);
        var uStreams = Probe(work2);

        var ugate1 = new LanguageTagGate(log, cfg, reg);
        var ur1 = ugate1.RunAsync(work2, uStreams, ffmpeg, CancellationToken.None).GetAwaiter().GetResult();
        int uAttempts1 = log.Lines.Count(x => x.Contains("left unresolved"));
        Console.WriteLine("Lauf 1: " + ur1.Written + " geschrieben, " + uAttempts1 + " Spur(en) nicht aufloesbar");

        // Der Seeder zieht die Zeilen auf den neuen Hash — die Vermerke muessen mitwandern.
        // Reihenfolge wie in SubdlSeeder.AllocateMissingLanguageCodes: erst der Umzug, DANN den
        // aktuellen Hash lesen. Andersherum haelt uCur den ALTEN Hash, die Marker suchen am neuen
        // und der Test scheitert an seinem eigenen Aufbau statt am Code.
        if (ur1.OldHash != null && ur1.NewHash != null) reg.ReplaceMediaIdentity(ur1.OldHash, ur1.NewHash);
        string uCur = reg.GetMediaHash(work2);
        foreach (var (pos, lang, hi) in ur1.Tracks) reg.ObserveEmbed(uCur, pos, lang, hi);

        var marks = reg.DetectedPositions(uCur);
        Console.WriteLine("Marker in der DB: " + marks.Count + " Position(en) [" + string.Join(",", marks) + "]");
        var rows = reg.GetEmbeds(uCur);
        foreach (var r in rows)
        {
            Console.WriteLine("  pos " + r.SubPos + ": outcome=" + (r.DetectionOutcome ?? "-")
                + " at=" + (r.DetectionAttemptedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-")
                + " status=" + r.Status + " lang='" + r.Language + "'");
        }

        int logMark2 = log.Lines.Count;
        var ust1 = Stat(work2);
        var ugate2 = new LanguageTagGate(log, cfg, reg);
        var ur2 = ugate2.RunAsync(work2, uStreams, ffmpeg, CancellationToken.None).GetAwaiter().GetResult();
        var ust2 = Stat(work2);
        bool uSkippedLine = log.Lines.Skip(logMark2).Any(x => x.Contains("already ran through language detection"));
        bool uNoProbe = !log.Lines.Skip(logMark2).Any(x => x.Contains("left unresolved"))
                        && !log.Lines.Skip(logMark2).Any(x => x.Contains("untagged track resolved"));
        bool uUntouched = ust1.Size == ust2.Size && ust1.MTime == ust2.MTime;

        Console.WriteLine("Lauf 2: Skip-Zeile=" + uSkippedLine + ", keine Erkennung mehr=" + uNoProbe
            + ", Datei unveraendert=" + uUntouched);
        bool uOk = uAttempts1 > 0 && marks.Count > 0 && rows.Any(r => r.DetectionAttemptedAt != null)
                   && uSkippedLine && uNoProbe && uUntouched;
        Console.WriteLine(uOk
            ? ">>> F-M266 OK: Lauf 1 merkt den Versuch mit Datum, Lauf 2 oeffnet die Datei nicht mehr"
            : ">>> F-M266 FEHLER: der zweite Lauf hat die Datei wieder geoeffnet");

        // ---- Der Refresh-Eingriff: loescht ForgetStaleEmbeds VERDICT-Zeilen? ----
        // Die Frage dahinter: eine Spur ohne zuordenbare Sprache (untagged) taucht in Jellyfins
        // Streamliste NICHT auf — EmbeddedTracks laesst sie heraus. ForgetStaleEmbeds vergleicht
        // aber genau gegen diese Liste, also sieht JEDE Zeile fuer so eine Spur wie "verwaist"
        // aus. Geprueft wird, ob der Sweep eine bereits gefaellte Verdict-Zeile wegwirft.
        Console.WriteLine();
        Console.WriteLine("=== Refresh-Eingriff (ForgetStaleEmbeds) ===");

        // (a) Vorhandene Verdict-Klasse: der Uploader schreibt bei und-too-small Language="UN".
        string vHash = reg.GetMediaHash(work2);
        reg.MarkEmbed(vHash, 2, "UN", false, SubtitleStatus.Rejected,
            contentHash: "deadbeefdeadbeef", reason: RejectReason.UndTooSmall);
        bool before1 = reg.GetEmbed(vHash, 2) != null;

        // (b) Unsere neue Versuchs-Zeile (Sprache ebenfalls unbekannt).
        bool before2 = reg.GetEmbed(vHash, 2)?.DetectionAttemptedAt != null;

        var currentTracks = SidecarNaming.EmbeddedTracks(Probe(work2));
        Console.WriteLine("  Jellyfin sieht " + currentTracks.Count + " zuordenbare Spur(en): "
            + string.Join(", ", currentTracks.Select(t => "pos" + t.SubPos + "=" + t.Lang)));

        int swept = reg.ForgetStaleEmbeds(vHash, currentTracks);
        var after = reg.GetEmbed(vHash, 2);
        Console.WriteLine("  Sweep hat " + swept + " Zeile(n) entfernt");

        bool verdictSurvived = after != null && after.Status == SubtitleStatus.Rejected
                               && after.Reason == RejectReason.UndTooSmall;
        bool markerSurvived = after != null && after.DetectionAttemptedAt != null;

        Console.WriteLine("  Verdict-Zeile (rejected/und-too-small) ueberlebt: " + verdictSurvived);
        Console.WriteLine("  Versuchs-Vermerk (DetectionAttemptedAt) ueberlebt  : " + markerSurvived);
        Console.WriteLine(verdictSurvived && markerSurvived
            ? ">>> OK: Zeilen ohne zuordenbare Sprache ueberstehen den Sweep"
            : ">>> FEHLER: der Refresh hat eine Verdict-Zeile geloescht (before=" + before1 + "/" + before2 + ")");

        // ---- F-M267/F-M268: Ampel-Kriterien fuer den Warte-Task ----
        // Ein einziger Grundsatz (Nutzer-Freigabe 30.09.2026):
        //   gruen  = die Arbeit lief und endete ohne Ausnahme
        //   gelb   = die Arbeit fand nicht statt, nichts ist kaputt (Quota, Lock, User-Stop)
        //   rot    = etwas ist kaputt (Ausnahme, gescheiterter Cycle)
        //   grau   = bewusst nicht gelaufen (disabled), noch nie gelaufen, oder Scan ohne Aenderung
        Console.WriteLine();
        Console.WriteLine("=== F-M267/F-M268: Ampel-Kriterien ===");

        var cases = new (string Name, bool Finished, string Seed, string SeedDetail,
                         string Dir, string DirDetail, string WantOutcome)[]
        {
            // gruen: Scan lief, Richtung ohne besonderes Schicksal
            ("scan ok", true, WorkerRunRegistry.Outcome.Ok, "8 upload, 0 download queued", null, null,
             WorkerRunRegistry.Outcome.Ok),
            // gruen: Pre-check hat nichts gefunden -> grau, NICHT gruen
            ("scan skipped", true, WorkerRunRegistry.Outcome.Skipped, "no changes — scan skipped", null, null,
             WorkerRunRegistry.Outcome.Skipped),
            // gelb: Quota (Prod 30.09.2026 18:51, 2 von 1144 Items)
            ("quota", true, WorkerRunRegistry.Outcome.Ok, "0 upload, 1138 download queued",
             WorkerRunRegistry.Outcome.Deferred, "download quota/rate limit — rescheduled",
             WorkerRunRegistry.Outcome.Deferred),
            // gelb: Seeder selbst verschoben (Lock busy), Richtung hat kein eigenes Schicksal
            ("seeder deferred", true, WorkerRunRegistry.Outcome.Deferred, "run lock busy — rescheduled", null, null,
             WorkerRunRegistry.Outcome.Deferred),
            // gelb: User-Stop
            ("user stop", true, WorkerRunRegistry.Outcome.Ok, "0 upload, 5 download queued",
             WorkerRunRegistry.Outcome.Cancelled, "user stop", WorkerRunRegistry.Outcome.Cancelled),
            // rot: gescheiterter Cycle (Prod 17:22:39) — muss rot sein, nicht gruen
            ("cycle failed", true, WorkerRunRegistry.Outcome.Ok, "0 upload, 3 download queued",
             WorkerRunRegistry.Outcome.Failed, "The CancellationToken was cancelled", WorkerRunRegistry.Outcome.Failed),
            // rot: der Scan selbst ist geworfen
            ("scan failed", true, WorkerRunRegistry.Outcome.Failed, "ffprobe not found", null, null,
             WorkerRunRegistry.Outcome.Failed),
            // rot schlaegt gelb: ein gescheiterter Cycle mit vorheriger Quota bleibt rot
            ("failed beats quota", true, WorkerRunRegistry.Outcome.Ok, "0 upload, 3 download queued",
             WorkerRunRegistry.Outcome.Failed, "boom", WorkerRunRegistry.Outcome.Failed),
            // Cap abgelaufen: laeuft noch
            ("cap expired", false, null, null, null, null, WorkerRunRegistry.Outcome.Running),
        };

        bool lightsOk = true;
        foreach (var c in cases)
        {
            var (got, det) = WorkerRunRegistry.DescribeCycle(
                c.Finished, c.Seed, c.SeedDetail, c.Dir, c.DirDetail);
            bool pass = got == c.WantOutcome && !string.IsNullOrWhiteSpace(det);
            if (!pass) { lightsOk = false; }
            Console.WriteLine(string.Format(
                "  {0,-18} -> {1,-9} {2}  \"{3}\"",
                c.Name, got, pass ? "OK " : "FEHLER", det));
        }
        Console.WriteLine(lightsOk
            ? ">>> F-M268 OK: gruen/gelb/rot/grau nach dem vereinbarten Grundsatz"
            : ">>> F-M268 FEHLER: mindestens eine Ampel stimmt nicht");

        bool seederListed = WorkerRunRegistry.KnownWorkers.Any(w => w.Key == WorkerRunRegistry.SeederKey);
        Console.WriteLine("  Seeder in KnownWorkers: " + seederListed);

        // ---- F-M269: die Detailzeile des Refresh nennt den Rebuild ----
        // Nutzer-Entscheidung 30.09.2026: die Kompaktierung wird nicht mehr geloest, der Rebuild ist
        // die akzeptierte Antwort. Die Zeile sagte aber "ok" mit einem Text, der wie ein sauberer
        // Lauf las — der Rueckfall war unsichtbar.
        Console.WriteLine();
        Console.WriteLine("=== F-M269: Refresh-Detailzeile ===");

        string dClean = SubdlDatabaseRefreshTask.BuildRefreshDetail(
            null, 33, 10, 0, 0, 450, null);
        string dRebuilt = SubdlDatabaseRefreshTask.BuildRefreshDetail(
            null, 33, 10, 0, 0, 450, "compaction failed — rebuilt from own rows (12419072 -> 8249344 bytes)");
        string dNothing = SubdlDatabaseRefreshTask.BuildRefreshDetail(
            null, 0, 0, 0, 0, 0, null);
        string dFailed = SubdlDatabaseRefreshTask.BuildRefreshDetail(
            new InvalidOperationException("boom"), 0, 0, 0, 0, 0, null);

        Console.WriteLine("  sauber       : \"" + dClean + "\"");
        Console.WriteLine("  Rebuild      : \"" + dRebuilt + "\"");
        Console.WriteLine("  nichts zu tun: \"" + dNothing + "\"");
        Console.WriteLine("  Ausnahme     : \"" + dFailed + "\"");

        bool detailOk = dClean == "removed 10 dead state, forgot 0 stale row(s), dropped 450 mark(s)"
                        && dRebuilt.Contains("rebuilt from own rows") && dRebuilt.Contains("12419072")
                        && dRebuilt.StartsWith("removed 10 dead state")
                        && dNothing == "nothing to change"
                        && dFailed == "boom";
        // Teilbedingungen einzeln ausgeben, damit ein Fehlschlag benennbar ist
        bool c1 = dClean == "removed 10 dead state, forgot 0 stale row(s), dropped 450 mark(s)";
        bool c2 = dRebuilt.Contains("rebuilt from own rows");
        bool c3 = dRebuilt.Contains("12419072");
        bool c4 = dNothing == "nothing to change";
        bool c5 = dFailed == "boom";
        Console.WriteLine("  Teilbedingungen: clean=" + c1 + " containsRebuild=" + c2
            + " containsSize=" + c3 + " nothing=" + c4 + " exception=" + c5);
        Console.WriteLine(detailOk
            ? ">>> F-M269 OK: der Rebuild-Rueckfall steht in der Zeile, die Zahlen bleiben"
            : ">>> F-M269 FEHLER: die Detailzeile verschweigt den Rebuild oder verliert die Zahlen");

        bool allOk = ok && uOk && verdictSurvived && markerSurvived && lightsOk && seederListed && detailOk;
        Console.WriteLine();
        Console.WriteLine("--- Logzeilen (Normal) ---");
        foreach (var l in log.Lines) Console.WriteLine("  " + l);
        return allOk ? 0 : 1;
    }

    // ---- Hilfen ----

    private static (long Size, long MTime) Stat(string p)
    {
        var fi = new FileInfo(p);
        return (fi.Length, fi.LastWriteTimeUtc.Ticks);
    }

    /// <summary>Liest die Subtitle-Spuren wie Jellyfin sie sieht — Sprach-Tag aus dem Container.</summary>
    private static List<MediaBrowser.Model.Entities.MediaStream> Probe(string media)
    {
        var psi = new System.Diagnostics.ProcessStartInfo { FileName = "/usr/bin/ffprobe", RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-v", "error", "-select_streams", "s", "-show_entries", "stream=index:stream_tags=language", "-of", "default=nw=1", media })
            psi.ArgumentList.Add(a);
        var pr = System.Diagnostics.Process.Start(psi);
        string raw = pr.StandardOutput.ReadToEnd();
        pr.WaitForExit();

        var outl = new List<MediaBrowser.Model.Entities.MediaStream>();
        foreach (var lineRaw in raw.Split('\n'))
        {
            var line = lineRaw.Trim();
            if (line.StartsWith("index=", StringComparison.Ordinal))
            {
                outl.Add(new MediaBrowser.Model.Entities.MediaStream
                {
                    Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle,
                    Codec = "subrip",
                    Language = null
                });
            }
            else if (line.StartsWith("TAG:language=", StringComparison.Ordinal) && outl.Count > 0)
            {
                outl[outl.Count - 1].Language = line.Substring(13).Trim();
            }
        }
        return outl;
    }


    /// <summary>
    /// Baut die Probe-Datei fuer beide Tests: DREI Text-Spuren ohne Sprach-Tag.
    /// <list type="number">
    /// <item>s0 — ~9 KB englischer Text (wird erkannt und getaggt: der T82-Fall);</item>
    /// <item>s1 — ~9 KB deutscher Text (dito);</item>
    /// <item>s2 — ~600 Bytes (unter der 2-KB-Grenze, also NICHT aufloesbar: der F-M266-Fall).</item>
    /// </list>
    /// Der dritte Track ist der Punkt: er bleibt nach jedem Lauf untagged, die billige
    /// Vorpruefung des Seeders meldet ihn also bei JEDEM Scan als Arbeit, und ohne den
    /// Versuchs-Vermerk in der Datenbank wird die Datei jedes Mal wieder geoeffnet.
    /// </summary>
    private static void BuildProbe(string target, string ffmpeg)
    {
        string en = Path.ChangeExtension(target, ".en.srt");
        string de = Path.ChangeExtension(target, ".de.srt");
        string tiny = Path.ChangeExtension(target, ".tiny.srt");

        // Genug Text fuer die Erkennung, klar in der jeweiligen Sprache.
        File.WriteAllText(en, Cues(new[]
        {
            "The quick brown fox jumps over the lazy dog every single morning.",
            "She said that we should meet at the station before the train leaves.",
            "I do not think this is going to work the way you hope it will.",
            "There is nothing left to say about the matter, so let us go home.",
            "He walked through the door and never looked back at the burning house."
        }, 9000));

        File.WriteAllText(de, Cues(new[]
        {
            "Der schnelle braune Fuchs springt jeden einzelnen Morgen ueber den Hund.",
            "Sie sagte, dass wir uns am Bahnhof treffen sollten, bevor der Zug faehrt.",
            "Ich glaube nicht, dass das so funktionieren wird, wie du es erhoffst.",
            "Es gibt nichts mehr zu sagen in dieser Angelegenheit, also gehen wir heim.",
            "Er ging durch die Tuer und sah nie wieder zurueck auf das brennende Haus."
        }, 9000));

        // Zu wenig Text fuer ein Urteil — genau die Klasse, die dauerhaft untagged bleibt.
        File.WriteAllText(tiny, Cues(new[] { "Hello there." }, 1500));  // < 2048 Zeichen: bleibt untagged

        var psi = new System.Diagnostics.ProcessStartInfo { FileName = "/usr/bin/ffmpeg", RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[]
        {
            "-y", "-v", "error",
            "-f", "lavfi", "-i", "color=c=black:s=320x240:d=1",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
            "-i", en, "-i", de, "-i", tiny,
            "-map", "0:v", "-map", "1:a", "-map", "2:s", "-map", "3:s", "-map", "4:s",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-c:s", "srt",
            target
        })
        {
            psi.ArgumentList.Add(a);
        }

        var pr = System.Diagnostics.Process.Start(psi);
        pr.StandardError.ReadToEnd();
        pr.WaitForExit();
        File.Delete(en); File.Delete(de); File.Delete(tiny);
    }

    /// <summary>Baut SRT-Cues aus den gegebenen Saetzen, so oft wiederholt bis die Groesse passt.</summary>
    private static string Cues(string[] sentences, int targetChars)
    {
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (sb.Length < targetChars)
        {
            i++;
            int start = i * 5, end = start + 4;
            sb.Append(i).Append('\n');
            sb.Append("00:").Append((start / 60).ToString("00")).Append(':').Append((start % 60).ToString("00")).Append(",000 --> ");
            sb.Append("00:").Append((end / 60).ToString("00")).Append(':').Append((end % 60).ToString("00")).Append(",000\n");
            sb.Append(sentences[i % sentences.Length]).Append("\n\n");
        }
        return sb.ToString();
    }

    private sealed class RecLogger : ILogger
    {
        public List<string> Lines { get; } = new();
        public IDisposable BeginScope<TState>(TState state) => new Nop();
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (formatter != null) Lines.Add("[" + logLevel + "] " + formatter(state, exception));
        }
        private sealed class Nop : IDisposable { public void Dispose() { } }
    }
}
