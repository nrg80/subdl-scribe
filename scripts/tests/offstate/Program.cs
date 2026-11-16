
using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Api.Endpoints;
using Jellyfin.Plugin.SubdlScribe.Configuration;
using Jellyfin.Plugin.SubdlScribe.Registry;

// T158 / F-M352 (operator order 19.10.2026): a worker that cannot start reads "off".
// The conditions are the code's own hard refusals, so they are driven through the REAL gate.
string root = "/opt/data/subdl-scribe";
int f = 0;

const string DL = WorkerRunRegistry.DownloadWorkerKey;   // SubdlSyncDownloadTask
const string UL = WorkerRunRegistry.UploadWorkerKey;     // SubdlSyncUploadTask
const string PP = WorkerOffState.PostprocessWorkerKey;   // SubDLPostprocessTask

static PluginConfiguration Ready()
{
    var c = new PluginConfiguration();
    c.Username = "u"; c.Password = "p"; c.ApiKey = "k"; c.TmdbApiKey = "t";
    c.DownloadEnabled = true;
    c.UploadEnabled = true;
    c.SelectedLibraries = new System.Collections.Generic.List<string> { "Movies" };
    c.DownloadLanguages = "EN, DE";
    return c;
}

// --- the happy case paints nothing grey -------------------------------------
var ok = Ready();
Check("ready download is NOT off", !WorkerOffState.Applies(DL, ok));
Check("ready upload is NOT off", !WorkerOffState.Applies(UL, ok));
Check("ready postprocessing is NOT off", !WorkerOffState.Applies(PP, ok));

// --- download: four independent hard stops ----------------------------------
// F-M507: „alles aus" verlangt auch den Schalter der dritten Quelle — sie ist im ungesetzten Fall an.
Check("download off when the toggle is off", Off(DL, c =>
{
    c.DownloadEnabled = false;
    c.TvSubtitlesDownloadEnabled = false;
    c.MovieSubtitlesDownloadEnabled = false;
}));
Check("download off when no library is selected", Off(DL, c => c.SelectedLibraries.Clear()));
Check("download off when the language list is empty", Off(DL, c => c.DownloadLanguages = "  , ,"));
Check("download off when the MANDATORY credential is missing — and NOT for a missing SubDL key, because tvsubtitles still runs (F-M497)",
    Off(DL, c => c.TmdbApiKey = "") && !Off(DL, c => c.ApiKey = " "));

// --- upload: three hard stops, and NO language condition --------------------
Check("upload off when the toggle is off", Off(UL, c => c.UploadEnabled = false));
Check("upload off when no library is selected", Off(UL, c => c.SelectedLibraries.Clear()));
Check("upload off when a credential is missing", Off(UL, c => c.TmdbApiKey = ""));
Check("upload IGNORES the download language list — it has none", !WorkerOffState.Applies(UL, Mutate(c => c.DownloadLanguages = "")));

// --- postprocessing: bound to upload, not to libraries ----------------------
Check("postprocessing off when upload is off", Off(PP, c => c.UploadEnabled = false));
// F-M445: there is no cadence left — the worker is off exactly when the upload direction is off.
Check("postprocessing is never off while upload runs", !Off(PP, _ => { }));
Check("postprocessing stays ON when no library is selected — it reads /user/mySubtitles",
    !WorkerOffState.Applies(PP, Mutate(c => c.SelectedLibraries.Clear())));

// --- F-M477: die Schalter sind je Anbieter und Richtung getrennt -----------------
// Der Alt-Wert antwortet nur, solange der Anbieterschalter ungesetzt (null) ist. Genau diese
// Reihenfolge macht die Umstellung ohne Migration richtig: eine Datei von vor der Trennung
// bedeutet weiter, was sie bedeutete.
Check("SubDL-Download folgt dem Anbieterschalter, wenn er gesetzt ist",
    !Mutate(c => { c.DownloadEnabled = true; c.SubdlDownloadEnabled = false; }).IsDownloadEnabled(SubtitleProvider.Subdl));
Check("SubDL-Download bleibt an, auch wenn der Alt-Wert aus ist — der neue Schalter gewinnt",
    Mutate(c => { c.DownloadEnabled = false; c.SubdlDownloadEnabled = true; }).IsDownloadEnabled(SubtitleProvider.Subdl));
Check("der Alt-Wert antwortet, solange der Anbieterschalter ungesetzt ist",
    !Mutate(c => { c.DownloadEnabled = false; c.SubdlDownloadEnabled = null; }).IsDownloadEnabled(SubtitleProvider.Subdl)
    && Mutate(c => { c.DownloadEnabled = true; c.SubdlDownloadEnabled = null; }).IsDownloadEnabled(SubtitleProvider.Subdl));
Check("SubDL-Upload folgt dem Anbieterschalter, wenn er gesetzt ist",
    !Mutate(c => { c.UploadEnabled = true; c.SubdlUploadEnabled = false; }).IsUploadEnabled(SubtitleProvider.Subdl));
Check("der Alt-Wert des Uploads antwortet, solange der Anbieterschalter ungesetzt ist",
    !Mutate(c => { c.UploadEnabled = false; c.SubdlUploadEnabled = null; }).IsUploadEnabled(SubtitleProvider.Subdl));
// F-M478: tvsubtitles.net hat Schalter, aber keinen Anbieter — beide Richtungen sind aus,
// egal was in der Konfiguration steht, und der Grund ist benannt statt still.
// F-M485: die zweite Quelle ist eingebunden — ihr Schalter wirkt jetzt, und zwar je Richtung.
Check("der tvsubtitles-Schalter schaltet beide Richtungen einzeln",
    Mutate(c => { c.TvSubtitlesDownloadEnabled = true; c.TvSubtitlesUploadEnabled = false; }).IsDownloadEnabled(SubtitleProvider.TvSubtitles)
    && !Mutate(c => { c.TvSubtitlesDownloadEnabled = true; c.TvSubtitlesUploadEnabled = false; }).IsUploadEnabled(SubtitleProvider.TvSubtitles)
    && Mutate(c => { c.TvSubtitlesDownloadEnabled = false; c.TvSubtitlesUploadEnabled = true; }).IsUploadEnabled(SubtitleProvider.TvSubtitles)
    && !Mutate(c => { c.TvSubtitlesDownloadEnabled = false; c.TvSubtitlesUploadEnabled = true; }).IsDownloadEnabled(SubtitleProvider.TvSubtitles));
Check("tvsubtitles nennt seine Grenzen, SubDL hat keine zu nennen",
    PluginConfiguration.MissingProviderReason(SubtitleProvider.TvSubtitles) is { Length: > 20 }
    && PluginConfiguration.MissingProviderReason(SubtitleProvider.Subdl) is null);
Check("beide Quellen sind Anbieter (F-M485: tvsubtitles ist eingebunden)",
    PluginConfiguration.HasProvider(SubtitleProvider.TvSubtitles)
    && PluginConfiguration.HasProvider(SubtitleProvider.Subdl));
// F-M503 (Nutzerentscheid 15.11.2026: "tvst download als download default aktivieren sie, upload
// default deaktiviert"): die beiden tvst-Richtungen fallen im null-Fall GEGENLAEUFIG aus. Der
// Download ist default AN, der Upload bleibt default AUS, und ein explizit gesetztes false muss
// weiterhin AUS heissen — der Default antwortet nur, solange nichts geschrieben wurde.
Check("ohne gesetzten Schalter ist der tvst-DOWNLOAD an, auch wenn der Alt-Wert aus ist",
    Mutate(c => { c.DownloadEnabled = false; c.UploadEnabled = false; }).IsDownloadEnabled(SubtitleProvider.TvSubtitles),
    "F-M503: null → Download AN");
Check("ein explizit ausgeschalteter tvst-Download bleibt aus",
    !Mutate(c => { c.DownloadEnabled = true; c.UploadEnabled = true; c.TvSubtitlesDownloadEnabled = false; }).IsDownloadEnabled(SubtitleProvider.TvSubtitles),
    "F-M503: false → Download AUS, der Default ueberschreibt keine Entscheidung");
Check("ohne gesetzten Schalter bleibt der tvst-UPLOAD aus, auch wenn der Alt-Wert an ist",
    !Mutate(c => { c.DownloadEnabled = true; c.UploadEnabled = true; }).IsUploadEnabled(SubtitleProvider.TvSubtitles),
    "F-M503: null → Upload AUS (unveraendert)");

// F-M507 (Nutzerauftrag 15.11.2026: „Dritte quelle. Nur das series halt nach tv show und movies nach
// moviesubtitels gehen."): die dritte Quelle hat eigene Schalter, und sie fallen wie tvst aus —
// Download im ungesetzten Fall AN, Upload AUS. Ein gespeichertes false bleibt AUS; der Alt-Schalter
// (DownloadEnabled/UploadEnabled) gehoert SubDL und antwortet hier NICHT.
Check("ohne gesetzten Schalter ist der moviesubtitles-DOWNLOAD an, der Upload aus",
    Mutate(c => { c.DownloadEnabled = true; c.UploadEnabled = true; }).IsDownloadEnabled(SubtitleProvider.MovieSubtitles)
    && !Mutate(c => { c.DownloadEnabled = true; c.UploadEnabled = true; }).IsUploadEnabled(SubtitleProvider.MovieSubtitles),
    "F-M507: null → Download AN, Upload AUS");
Check("ein explizit ausgeschalteter moviesubtitles-Download bleibt aus",
    !Mutate(c => { c.DownloadEnabled = true; c.MovieSubtitlesDownloadEnabled = false; }).IsDownloadEnabled(SubtitleProvider.MovieSubtitles)
    && Mutate(c => { c.DownloadEnabled = false; c.MovieSubtitlesDownloadEnabled = true; }).IsDownloadEnabled(SubtitleProvider.MovieSubtitles),
    "F-M507: false → AUS, true schlaegt den Alt-Schalter");
Check("der Alt-Schalter allein schaltet moviesubtitles NICHT ab",
    Mutate(c => { c.DownloadEnabled = false; c.UploadEnabled = false; }).IsDownloadEnabled(SubtitleProvider.MovieSubtitles)
    && Mutate(c => { c.UploadEnabled = true; c.MovieSubtitlesUploadEnabled = true; }).IsUploadEnabled(SubtitleProvider.MovieSubtitles),
    "F-M507: der Alt-Wert gehoert SubDL");
// F-M508 (Etappe 2b): der Upload-Weg der dritten Quelle EXISTIERT jetzt (Endpunkt verdrahtet), aber er
// ist gesperrt: der Schalter faellt im ungesetzten Fall aus, und ein gespeichertes true ist die einzige
// Art, ihn scharf zu machen — bis die Sprachabbildung der Seite belegt ist, bleibt er aus.
Check("die dritte Quelle hat einen Upload-Weg, aber ihr Schalter ist ohne Entscheidung aus",
    PluginConfiguration.HasProvider(SubtitleProvider.MovieSubtitles)
    && SubtitleEndpointCatalog.HasUploadPath(SubtitleProvider.MovieSubtitles)
    && !Mutate(c => { c.UploadEnabled = true; c.DownloadEnabled = true; }).IsUploadEnabled(SubtitleProvider.MovieSubtitles)
    && Mutate(c => { c.MovieSubtitlesUploadEnabled = true; }).IsUploadEnabled(SubtitleProvider.MovieSubtitles),
    "F-M508: der Weg ist gebaut, der Schalter bleibt aus, bis die Sprachabbildung belegt ist");

// F-M509 (Operator-Klarstellung 15.11.2026: „Seperate into subdl and tvst. Tvst is splitted into tvst
// and moviesub itself"): die Reihenfolge ordnet ZWEI Quellen — SubDL und die tvst-Familie —, und die
// Familie loest nach Medienart auf ihre Adresse auf. Deshalb hat die Einstellung zwei Werte, und in
// JEDER Medienart stehen genau zwei Quellen in der Frage-Reihenfolge.
Check("die Reihenfolge hat zwei Werte und ordnet die Familie, nicht ihre Adressen",
    Enum.GetValues<PluginConfiguration.DownloadSourceOrder>().Length == 2
    && SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: true)
        .SequenceEqual(new[] { SubtitleProvider.TvSubtitles, SubtitleProvider.Subdl })
    && SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: false)
        .SequenceEqual(new[] { SubtitleProvider.MovieSubtitles, SubtitleProvider.Subdl }),
    "F-M509: tvst zuerst = die Adresse der Medienart zuerst, danach SubDL");

Check("die Familie ist EINE Quelle mit zwei Adressen, und die Medienart waehlt die Adresse",
    SourceOrder.FamilyPage(isSeries: true) == SubtitleProvider.TvSubtitles
    && SourceOrder.FamilyPage(isSeries: false) == SubtitleProvider.MovieSubtitles
    && SourceOrder.InFamily(SubtitleProvider.TvSubtitles)
    && SourceOrder.InFamily(SubtitleProvider.MovieSubtitles)
    && !SourceOrder.InFamily(SubtitleProvider.Subdl)
    && SourceOrder.FamilyFirst(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst)
    && !SourceOrder.FamilyFirst(PluginConfiguration.DownloadSourceOrder.SubdlFirst),
    "F-M509: zwei Adressen, eine Quelle");

// --- the lamps are NOT repainted (operator: note it, do not grey the row) ---
var gate = Mutate(c => c.UploadEnabled = false);
Check("the lamp override is gone from the registry", typeof(WorkerRunRegistry.Outcome).GetField("Off") == null);
Check("no PaintsOff decision remains", typeof(WorkerOffState).GetMethod("PaintsOff") == null);

// --- a runnable direction whose refetch rhythm is Manual ---------------------
Check("download is not manual on the default rhythm", !WorkerOffState.IsManualOnly(DL, Mutate(c => { })));
Check("download manual when the refetch rhythm is Manual",
    WorkerOffState.IsManualOnly(DL, Mutate(c => c.RefetchInterval = UpdateInterval.Manual))
    && WorkerOffState.IsManualOnly(UL, Mutate(c => c.RefetchInterval = UpdateInterval.Manual)));
Check("download manual on the legacy UpdateInterval.Never",
    WorkerOffState.IsManualOnly(DL, Mutate(c => c.RefetchInterval = UpdateInterval.Never)));
Check("OnArrival is NOT manual - arrivals start the pipeline",
    !WorkerOffState.IsManualOnly(DL, Mutate(c => c.RefetchInterval = UpdateInterval.OnArrival))
    && !WorkerOffState.IsManualOnly(UL, Mutate(c => c.RefetchInterval = UpdateInterval.OnArrival)));
Check("a scheduled rhythm is NOT manual",
    !WorkerOffState.IsManualOnly(DL, Mutate(c => c.RefetchInterval = UpdateInterval.Daily))
    && !WorkerOffState.IsManualOnly(UL, Mutate(c => c.RefetchInterval = UpdateInterval.Monthly)));
Check("manual needs a runnable direction - upload off is off, not manual",
    WorkerOffState.Applies(UL, Mutate(c => { c.UploadEnabled = false; c.RefetchInterval = UpdateInterval.Manual; })));
Check("download manual and off are mutually exclusive by construction",
    WorkerOffState.Applies(DL, Mutate(c => { c.DownloadEnabled = false; c.TvSubtitlesDownloadEnabled = false; c.MovieSubtitlesDownloadEnabled = false; c.RefetchInterval = UpdateInterval.Manual; }))
    && WorkerOffState.IsManualOnly(DL, Mutate(c => { c.DownloadEnabled = false; c.TvSubtitlesDownloadEnabled = false; c.MovieSubtitlesDownloadEnabled = false; c.RefetchInterval = UpdateInterval.Manual; })));
// F-M503: der Alt-Schalter allein schaltet die Richtung NICHT mehr ab — der tvst-Download ist im
// null-Fall an. Genau diese Folge wird hier festgehalten, damit sie nicht als Regression auftaucht.
Check("Download alt aus + tvst ungesetzt = Richtung bleibt an (F-M503)",
    !WorkerOffState.Applies(DL, Mutate(c => c.DownloadEnabled = false)),
    "der tvst-Download default traegt die Richtung");

// --- the maintenance workers say MANUAL, not off -----------------------------
const string DB = WorkerOffState.DatabaseWorkerKey;
const string OS = WorkerOffState.OshashWorkerKey;

Check("database refresh is not manual by default", !WorkerOffState.IsManualOnly(DB, Mutate(c => { })));
Check("database refresh manual on PruneMode.Manual", WorkerOffState.IsManualOnly(DB, Mutate(c => c.PruneMode = PruneMode.Manual)));
Check("database refresh manual on the legacy PruneMode.Never", WorkerOffState.IsManualOnly(DB, Mutate(c => c.PruneMode = PruneMode.Never)));
Check("database refresh NOT manual on Weekly/Monthly/Yearly",
    !WorkerOffState.IsManualOnly(DB, Mutate(c => c.PruneMode = PruneMode.Weekly))
    && !WorkerOffState.IsManualOnly(DB, Mutate(c => c.PruneMode = PruneMode.Monthly))
    && !WorkerOffState.IsManualOnly(DB, Mutate(c => c.PruneMode = PruneMode.Yearly)));
Check("oshash refresh manual on OshashRefreshMode.Manual", WorkerOffState.IsManualOnly(OS, Mutate(c => c.OshashRefresh = OshashRefreshMode.Manual)));
Check("oshash refresh manual on the legacy OshashRefreshMode.Never", WorkerOffState.IsManualOnly(OS, Mutate(c => c.OshashRefresh = OshashRefreshMode.Never)));
Check("oshash refresh NOT manual on Monthly", !WorkerOffState.IsManualOnly(OS, Mutate(c => c.OshashRefresh = OshashRefreshMode.Monthly)));
// F-M445: no cadence means nothing to call "manual" either — the order comes from the upload run.
Check("postprocessing is never reported as manual", !WorkerOffState.IsManualOnly(PP, Mutate(_ => { })));
Check("postprocessing is never reported off while upload is on", !WorkerOffState.Applies(PP, Mutate(_ => { })));
Check("a manual worker is NEVER reported as off (manual runs still work)",
    !WorkerOffState.Applies(DB, Mutate(c => c.PruneMode = PruneMode.Manual))
    && !WorkerOffState.Applies(OS, Mutate(c => c.OshashRefresh = OshashRefreshMode.Manual)));
Check("postprocessing is off in the gate when upload is off (F-M445: the switch is all that is left)",
    WorkerOffState.Applies(PP, Mutate(c => c.UploadEnabled = false)));

// --- the language parse matches the download pipeline's ---------------------
var parsed = WorkerOffState.ParseTargetLanguages(" en, de ,EN,,");
Check("the language parse trims, upper-cases and dedupes", parsed.Count == 2 && parsed.Contains("EN") && parsed.Contains("DE"),
    string.Join("/", parsed));
Check("a blank language value yields no languages", WorkerOffState.ParseTargetLanguages(" \t ").Count == 0);
Check("a null language value yields no languages", WorkerOffState.ParseTargetLanguages(null).Count == 0);

// --- a null configuration never paints a lamp -------------------------------
Check("no configuration: no statement, no off", !WorkerOffState.Applies(DL, null) && !WorkerOffState.Applies(PP, null));

// --- the other workers keep their palette ----------------------------------
foreach (var other in new[] { "SubDLSeeder", "SubdlAutoSyncTask", "SubdlDatabaseRefreshTask", "SubdlOshashRefreshTask" })
{
    Check($"'{other}' is not covered by the off rule", !WorkerOffState.Applies(other, Mutate(c => { c.DownloadEnabled = false; c.UploadEnabled = false; c.SelectedLibraries.Clear(); })));
}

// --- wiring on the source ---------------------------------------------------
string ctl = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/SubdlStatusController.cs"));
Check("the worker endpoint no longer carries an off block of its own", !ctl.Contains("Off = new"));
Check("the endpoint does NOT repaint an outcome", !ctl.Contains("Outcome = off ?"));
Check("the endpoint does NOT hide a pending fire", !ctl.Contains("Deferred = !off"));
Check("the endpoint keeps the stored detail", !ctl.Contains("Detail = off ?"));

string page = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Configuration/configPage.html"));
Check("the off note rides on the DEFERRAL line, not on a line of its own",
    page.Contains("var deferLine = document.querySelector('#SubdlLocalDeferLine')")
    && page.Contains("offNotes.forEach(function (t) { localLines.push(t); })")
    && !page.Contains("SubdlOffLine"));
Check("the page reads the off facts from the same payload as the deferral", page.Contains("(loc && loc.notes)"));

string quota = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/SubdlQuotaService.cs"));
Check("the quota answer carries the notes in its local block",
    quota.Contains("var offEntries = StatusNotes();") && quota.Contains("notes = offEntries"));
Check("the notes list is built from the shared gates",
    quota.Contains("WorkerOffState.Applies") && quota.Contains("WorkerOffState.IsManualOnly") && quota.Contains("StatusNotes()"));
Check("postprocessing has ONE wording - manual, never off", quota.Contains("!config.IsUploadEnabled(SubtitleProvider.Subdl) || manual(postKey, config)"));
Check("the wording is the operator's seven phrases", quota.Contains("\"download off\"") && quota.Contains("\"upload off\"") && quota.Contains("\"download manual\"") && quota.Contains("\"upload manual\"")
    && !quota.Contains("\"upload postprocessing off\"")
    && quota.Contains("\"upload postprocessing manual\"") && quota.Contains("\"database refresh manual\"") && quota.Contains("\"oshash refresh manual\""));
Check("the notes survive the early return when no fire is pending",
    System.Text.RegularExpressions.Regex.Matches(quota, "notes = offEntries").Count >= 2);
Check("off outranks manual per direction (else-if, not two words)",
    System.Text.RegularExpressions.Regex.Matches(quota, "else if \\(manual\\(").Count >= 2);
Check("the off notes are appended AFTER the deferral notes", page.IndexOf("localLines.push(t)") > page.IndexOf("var localLines = deferred.map"));
Check("no off-case was added to the outcome words", !page.Contains("case 'off': return"));

// --- F-M436 (operator order 04.11.2026): "deferred überschreib fail nicht" --------------------
// Ein offener Fire auf einem kaputten Worker darf nicht als bloße Vertagung gelesen werden.
Check("the deferral note carries a BROKEN worker's outcome, not just 'deferred'",
    quota.Contains("Outcome.Failed") && quota.Contains("\"last run failed\""));
Check("the broken worker's own cause travels with it",
    quota.Contains("string.IsNullOrWhiteSpace(row!.Detail) ? string.Empty : \": \" + row.Detail.Trim()"));
// F-M475 (operator finding 15.11.2026): the reason belongs to the PENDING attempt, not to the row.
// A fresh deferral must not wear the previous run's cause — live 15.11.2026: the scan deferral was
// printed as "download quota/rate limit — rescheduled — next attempt 4:24 AM". The row's detail stays
// the fallback for a fire that carries no token (an older state file, or a foreign failure).
Check("a deferred row names the reason of the PENDING attempt and falls back to the row's detail",
    quota.Contains("reason = fireReason ?? row!.Detail ?? string.Empty;"));
Check("a pending attempt without a row still says why it waits",
    quota.Contains("else if (hasFire)") && quota.Contains("reason = fireReason ?? string.Empty;"));
Check("a failure keeps its note WITHOUT a pending fire (no early return)",
    !quota.Contains("if (fires.Count == 0)")
    && quota.Contains("if (!hasFire && !isFailed)"));
Check("a healthy worker with no fire says nothing",
    quota.Contains("if (!hasFire && !isFailed)") && quota.Contains("due = hasFire"));
// Die Regel hängt am Worker, nicht an einer Richtung: Download, Seeder, Auto-Sync und die drei
// Wartungs-Worker laufen durch dieselbe Schleife.
var noteStart = quota.IndexOf("private static object LocalRateLimitState()");
var noteEnd = noteStart < 0 ? -1 : quota.IndexOf("private static", noteStart + 10);
var noteBody = noteEnd > noteStart ? quota.Substring(noteStart, noteEnd - noteStart) : string.Empty;
Check("the failure note runs over EVERY worker — no upload-only special case",
    noteBody.Contains("WorkerRunRegistry.KnownWorkers")
    && !noteBody.Contains("UploadWorkerKey") && !noteBody.Contains("DownloadWorkerKey"));
Check("the note still appends the due moment after the reason",
    page.Contains("return d.name + ': ' + reason + (when ? ' — next attempt ' + when : '');"));
Check("a pending fire turns the LAMP to defer for every outcome",
    page.Contains("if (w.Deferred && wo !== 'run')"));
Check("a failed row is no longer exempt from the deferral lamp",
    !page.Contains("wo !== 'failed' && wo !== 'run'"));

// --- F-M447 (operator order 05.11.2026): "für das upload Postprocessing im worker status bitte
// nicht defer sondern schedule verwenden. Der erste lauf ist planmäßig" -------------------------
// Die Seite setzt fuer eine leere Begruendung das Wort "deferred" ein. Beim Postprocessing ist das
// falsch: jeder beendete Upload-Lauf terminiert den Auftrag genau einmal, und dieser erste Lauf ist
// planmaessig — nichts wurde vertagt. Geprueft wird das Paar: das Wort steht da, UND es haengt am
// Postprocessing-Worker, nicht an allen.
// F-M470 (operator 11.11.2026, "wieso wird postprocessing jetzt 2x gemeldet?"): F-M447 gave the
// postprocessing the word `schedule` on THIS line — but the armed order has its own line on the page
// ("Postprocessing due <when>"), so the same moment stood twice. The word is gone; the worker is
// skipped here entirely unless its run failed, which is the one thing only this line can say.
Check("the postprocessing no longer rides on the deferral line",
    quota.Contains("&& !isFailed)") && quota.Contains("continue;"),
    "the armed order is shown once — by its own line (F-M445)");
Check("the skip is gated on the postprocessing worker and on a healthy run",
    quota.Contains("Jellyfin.Plugin.SubdlScribe.Registry.WorkerOffState.PostprocessWorkerKey, StringComparison.Ordinal)\n                    && !isFailed"),
    "a FAILED postprocessing run still speaks here (F-M436)");
Check("the duplicate word is gone",
    !quota.Contains("reason = \"schedule\";"),
    "one fact, one line");

Console.WriteLine();
Console.WriteLine(f == 0 ? ">>> OFFSTATE OK" : $">>> OFFSTATE {f} FAILURE(S)");
return f == 0 ? 0 : 1;

bool Off(string key, Action<PluginConfiguration> mutate) => WorkerOffState.Applies(key, Mutate(mutate));
static PluginConfiguration Mutate(Action<PluginConfiguration> mutate)
{
    var c = new PluginConfiguration();
    c.Username = "u"; c.Password = "p"; c.ApiKey = "k"; c.TmdbApiKey = "t";
    c.DownloadEnabled = true; c.UploadEnabled = true;
    c.SelectedLibraries = new System.Collections.Generic.List<string> { "Movies" };
    c.DownloadLanguages = "EN, DE";
    mutate(c);
    return c;
}

void Check(string label, bool ok2, string detail = "")
{
    Console.WriteLine((ok2 ? "  [PASS] " : "  [FAIL] ") + label + (ok2 ? "" : " — " + detail));
    if (!ok2) f++;
}
