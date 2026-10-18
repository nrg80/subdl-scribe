
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

// --- the lamps are NOT repainted (operator: note it, do not grey the row) ---
var gate = Mutate(c => c.UploadEnabled = false);
Check("the lamp override is gone from the registry", typeof(WorkerRunRegistry.Outcome).GetField("Off") == null);
Check("no PaintsOff decision remains", typeof(WorkerOffState).GetMethod("PaintsOff") == null);

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
Check("the endpoint reports the three off states", ctl.Contains("Download = Registry.WorkerOffState.Applies(Registry.WorkerRunRegistry.DownloadWorkerKey")
    && ctl.Contains("Upload = Registry.WorkerOffState.Applies(Registry.WorkerRunRegistry.UploadWorkerKey")
    && ctl.Contains("Postprocessing = Registry.WorkerOffState.Applies(Registry.WorkerOffState.PostprocessWorkerKey"));
Check("the endpoint does NOT repaint an outcome", !ctl.Contains("Outcome = off ?"));
Check("the endpoint does NOT hide a pending fire", !ctl.Contains("Deferred = !off"));
Check("the endpoint keeps the stored detail", !ctl.Contains("Detail = off ?"));

string page = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Configuration/configPage.html"));
Check("the page draws a note per off state", page.Contains("['download off', offs.Download]")
    && page.Contains("['upload off', offs.Upload]")
    && page.Contains("['upload postprocessing off', offs.Postprocessing]"));
Check("the notes are appended AFTER the worker rows (last entries)", page.IndexOf("offNotes.forEach", page.IndexOf("workers.forEach")) > 0);
Check("the note text ends in off and carries NO second off in the result cell",
    page.Contains("<span class=\"subdl-label\" style=\"color:#767676;\">' + n[0] + '</span>")
    && page.Contains("<span class=\"subdl-outcome\"></span>")
    && !page.Contains("<span class=\"subdl-outcome\" style=\"color:#767676;\">off</span>"));
Check("no off-case was added to the outcome words", !page.Contains("case 'off': return"));

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
