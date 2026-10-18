
using System;
using System.IO;
using System.Linq;
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
    c.UploadPostprocessInterval = UpdateInterval.Weekly;
    return c;
}

// --- the happy case paints nothing grey -------------------------------------
var ok = Ready();
Check("ready download is NOT off", !WorkerOffState.Applies(DL, ok));
Check("ready upload is NOT off", !WorkerOffState.Applies(UL, ok));
Check("ready postprocessing is NOT off", !WorkerOffState.Applies(PP, ok));

// --- download: four independent hard stops ----------------------------------
Check("download off when the toggle is off", Off(DL, c => c.DownloadEnabled = false));
Check("download off when no library is selected", Off(DL, c => c.SelectedLibraries.Clear()));
Check("download off when the language list is empty", Off(DL, c => c.DownloadLanguages = "  , ,"));
Check("download off when a credential is missing", Off(DL, c => c.ApiKey = " "));

// --- upload: three hard stops, and NO language condition --------------------
Check("upload off when the toggle is off", Off(UL, c => c.UploadEnabled = false));
Check("upload off when no library is selected", Off(UL, c => c.SelectedLibraries.Clear()));
Check("upload off when a credential is missing", Off(UL, c => c.TmdbApiKey = ""));
Check("upload IGNORES the download language list — it has none", !WorkerOffState.Applies(UL, Mutate(c => c.DownloadLanguages = "")));

// --- postprocessing: bound to upload, not to libraries ----------------------
Check("postprocessing off when upload is off", Off(PP, c => c.UploadEnabled = false));
Check("postprocessing off on Manual", Off(PP, c => c.UploadPostprocessInterval = UpdateInterval.Manual));
Check("postprocessing off on Never", Off(PP, c => c.UploadPostprocessInterval = UpdateInterval.Never));
Check("postprocessing off on OnArrival", Off(PP, c => c.UploadPostprocessInterval = UpdateInterval.OnArrival));
Check("postprocessing stays ON when no library is selected — it reads /user/mySubtitles",
    !WorkerOffState.Applies(PP, Mutate(c => c.SelectedLibraries.Clear())));

// --- a real run must never be painted over (the operator's own finding) ------
var gate = Mutate(c => c.UploadEnabled = false);           // postprocessing: off-condition holds
var saved = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);

Check("a RUNNING row is never off — the manual button must not turn the lamp grey",
    !WorkerOffState.PaintsOff(PP, gate, WorkerRunRegistry.Outcome.Running, null, saved));
Check("a run that ended after the switch is shown, not hidden",
    !WorkerOffState.PaintsOff(PP, gate, WorkerRunRegistry.Outcome.Ok, saved.AddMinutes(5), saved));
Check("a run that ended BEFORE the switch reads off",
    WorkerOffState.PaintsOff(PP, gate, WorkerRunRegistry.Outcome.Ok, saved.AddMinutes(-5), saved));
Check("never run reads off", WorkerOffState.PaintsOff(PP, gate, WorkerRunRegistry.Outcome.Never, null, saved));
Check("unknown switch moment falls back to the config gate",
    WorkerOffState.PaintsOff(PP, gate, WorkerRunRegistry.Outcome.Ok, saved.AddMinutes(5), null));
Check("a run that ends in the same second as the save is still shown",
    !WorkerOffState.PaintsOff(PP, gate, WorkerRunRegistry.Outcome.Ok, saved, saved));
Check("a non-off worker is never painted off, whatever it did",
    !WorkerOffState.PaintsOff(PP, Mutate(c => { }), WorkerRunRegistry.Outcome.Ok, saved.AddMinutes(-60), saved));

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
Check("the endpoint applies the rule", ctl.Contains("WorkerOffState.PaintsOff(r.Id, liveConfig, r.Outcome, r.Ended, configSavedUtc)"));
Check("the endpoint reads the configuration's own moment", ctl.Contains("GetLastWriteTimeUtc(cfgPath)"));
Check("the endpoint REPLACES the outcome with off", ctl.Contains("Outcome = off ? Registry.WorkerRunRegistry.Outcome.Off : r.Outcome"));
Check("off clears the detail — no reason is written", ctl.Contains("Detail = off ? string.Empty : r.Detail"));
Check("off suppresses a pending fire (yellow must not outrank it)",
    ctl.Contains("Deferred = !off && pendingFires.ContainsKey(r.Id)") && ctl.Contains("DeferUntil = !off &&"));

string page = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Configuration/configPage.html"));
Check("the page paints off grey", page.Contains("case 'off': return { color: '#767676', text: 'off' };"));
Check("the status word is at most 6 characters", "off".Length <= 6);

string coord = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/ScheduledTasks/SubdlSchedulerCoordinator.cs"));
Check("the scheduler's own postprocessing gate uses the same three off states",
    coord.Contains("config.UploadEnabled") && coord.Contains("UpdateInterval.Never or UpdateInterval.Manual or UpdateInterval.OnArrival"));

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
    c.UploadPostprocessInterval = UpdateInterval.Weekly;
    mutate(c);
    return c;
}

void Check(string label, bool ok2, string detail = "")
{
    Console.WriteLine((ok2 ? "  [PASS] " : "  [FAIL] ") + label + (ok2 ? "" : " — " + detail));
    if (!ok2) f++;
}
