
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
Check("postprocessing is manual on a Manual cadence", WorkerOffState.IsManualOnly(PP, Mutate(c => c.UploadPostprocessInterval = UpdateInterval.Manual)));
Check("a manual worker is NEVER reported as off (manual runs still work)",
    !WorkerOffState.Applies(DB, Mutate(c => c.PruneMode = PruneMode.Manual))
    && !WorkerOffState.Applies(OS, Mutate(c => c.OshashRefresh = OshashRefreshMode.Manual)));
Check("postprocessing: upload off outranks a manual cadence (off, not manual)",
    WorkerOffState.Applies(PP, Mutate(c => { c.UploadEnabled = false; c.UploadPostprocessInterval = UpdateInterval.Manual; })));

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
Check("the off wording is the three operator phrases",
    quota.Contains("\"download off\"") && quota.Contains("\"upload off\"") && quota.Contains("\"upload postprocessing off\"")
    && quota.Contains("\"upload postprocessing manual\"") && quota.Contains("\"database refresh manual\"") && quota.Contains("\"oshash refresh manual\""));
Check("the notes survive the early return when no fire is pending",
    System.Text.RegularExpressions.Regex.Matches(quota, "notes = offEntries").Count >= 2);
Check("the off notes are appended AFTER the deferral notes", page.IndexOf("localLines.push(t)") > page.IndexOf("var localLines = deferred.map"));
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
