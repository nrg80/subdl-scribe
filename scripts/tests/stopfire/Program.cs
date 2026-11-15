
using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.SubdlScribe.ScheduledTasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

// T157 / F-M351 (order 18.10.2026): a GUI stop cancels the pending fire of the direction it names.
// Driven on the REAL coordinator: arm both directions, cancel one, prove the other survives.
string dir = Path.Combine(Path.GetTempPath(), "stopfire-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
var coord = new SubdlSchedulerCoordinator(null, new TestPaths(dir), NullLogger<SubdlSchedulerCoordinator>.Instance);

int f = 0;
var now = DateTime.UtcNow;

coord.ScheduleRecoveryFireAt(upload: false, now.AddMinutes(10));
coord.ScheduleRecoveryFireAt(upload: true, now.AddMinutes(12));

var armed = coord.GetPendingFires();
Check("both directions armed", armed.ContainsKey("SubdlSyncDownloadTask") && armed.ContainsKey("SubdlSyncUploadTask"),
    string.Join(", ", armed.Keys));

bool hadUpload = coord.CancelPendingFire(upload: true, reason: "stop marker upload");
Check("cancel(upload) reports that a fire was pending", hadUpload, hadUpload.ToString());

var after = coord.GetPendingFires();
Check("upload fire is GONE after the stop", !after.ContainsKey("SubdlSyncUploadTask"), "upload present: " + after.ContainsKey("SubdlSyncUploadTask"));
Check("download fire SURVIVES — a stop names one direction", after.ContainsKey("SubdlSyncDownloadTask"), "download present: " + after.ContainsKey("SubdlSyncDownloadTask"));

bool again = coord.CancelPendingFire(upload: true, reason: "stop marker upload");
Check("cancelling twice is harmless and reports nothing pending", !again, again.ToString());

bool hadDown = coord.CancelPendingFire(upload: false, reason: "stop marker download");
Check("cancel(download) removes the last fire", hadDown && coord.GetPendingFires().Count == 0,
    $"had={hadDown}, left={coord.GetPendingFires().Count}");

// Persisted: a restart must not resurrect a fire the operator cancelled.
string file = Path.Combine(dir, "recovery-fires.json");
string payload = File.Exists(file) ? File.ReadAllText(file) : "<missing>";
Check("the cancellation is PERSISTED (a restart cannot resurrect it)",
    payload.Contains("\"upload\":\"\"") && payload.Contains("\"download\":\"\""), payload);

// Source contract: the log line must print UTC without converting — the first live run claimed "UTC" on a
// local time (02:30 for a fire armed at 00:30Z), which reads as a wrong deferral moment.
string src = File.ReadAllText(Path.Combine("/opt/data/subdl-scribe", "Jellyfin.Plugin.SubdlSync", "ScheduledTasks", "SubdlSchedulerCoordinator.cs"));
int at = src.IndexOf("pending fire for", StringComparison.Ordinal);
string block = at >= 0
    ? src.Substring(Math.Max(0, at - 500), Math.Min(900, src.Length - Math.Max(0, at - 500)))
    : string.Empty;
bool claimsUtc = block.Contains("UTC cancelled", StringComparison.Ordinal);
bool converts = block.Contains("ConvertTimeFromUtc", StringComparison.Ordinal);
Check("the cancel log prints UTC and does not convert to local behind the label", claimsUtc && !converts,
    $"claimsUtc={claimsUtc}, converts={converts}");

// --- F-M353: the cancelled fire must also reach the worker row -------------------------------
var dlRow = SubdlSchedulerCoordinator.CancelledRow(upload: false);
var ulRow = SubdlSchedulerCoordinator.CancelledRow(upload: true);
Check("the download cancellation writes the DOWNLOAD row", dlRow.Key == "SubdlSyncDownloadTask" && dlRow.Name == "Download", dlRow.Key + "/" + dlRow.Name);
Check("the upload cancellation writes the UPLOAD row", ulRow.Key == "SubdlSyncUploadTask" && ulRow.Name == "Upload", ulRow.Key + "/" + ulRow.Name);
Check("the two rows are distinct — a stop names one direction", dlRow.Key != ulRow.Key && dlRow.Name != ulRow.Name, dlRow.Key + "/" + ulRow.Key);
Check("the detail says the fire was cancelled by the operator, not that a run failed",
    dlRow.Detail == ulRow.Detail && dlRow.Detail.Contains("cancelled") && dlRow.Detail.Contains("operator"), dlRow.Detail);

string cancels = src.Substring(Math.Max(0, src.IndexOf("public bool CancelPendingFire", StringComparison.Ordinal)),
    Math.Min(4200, src.Length - Math.Max(0, src.IndexOf("public bool CancelPendingFire", StringComparison.Ordinal))));
Check("the row is written for a cancellation and NOT with a 'defer' outcome",
    cancels.Contains("CancelledRow(upload)", StringComparison.Ordinal)
    && !cancels.Contains("Outcome.Deferred, row.Detail", StringComparison.Ordinal), "cancelled-row write");
Check("the row write happens only AFTER the fire was proven pending (the early return stays)",
    cancels.IndexOf("if (!had)", StringComparison.Ordinal) < cancels.IndexOf("CancelledRow(upload)", StringComparison.Ordinal), "order vs early return");
Check("a failing row write cannot make the stop fail", cancels.Contains("catch (Exception", StringComparison.Ordinal), "try/catch");
Check("the cancellation goes through MarkCancelled, NOT through Finish (which would keep the old start)",
    cancels.Contains("MarkCancelled(row.Key, row.Name, row.Detail)", StringComparison.Ordinal)
    && !cancels.Contains("Outcome.Cancelled, row.Detail", StringComparison.Ordinal), "MarkCancelled");
string reg = File.ReadAllText(Path.Combine("/opt/data/subdl-scribe", "Jellyfin.Plugin.SubdlSync", "Registry", "WorkerRunRegistry.cs"));
int mc = reg.IndexOf("public void MarkCancelled", StringComparison.Ordinal);
string mcBody = mc < 0 ? "" : reg.Substring(mc, Math.Min(1200, reg.Length - mc));
Check("MarkCancelled stamps BOTH times with now — a cancellation has a moment, not a run",
    mcBody.Contains("row.Started = now", StringComparison.Ordinal) && mcBody.Contains("row.Ended = now", StringComparison.Ordinal)
    && mcBody.Contains("var now = DateTime.UtcNow;", StringComparison.Ordinal), "start=end=now");
Check("MarkCancelled writes Outcome.Cancelled itself", mcBody.Contains("row.Outcome = Outcome.Cancelled", StringComparison.Ordinal), "outcome");
Check("MarkCancelled cannot break the stop", mcBody.Contains("catch (Exception", StringComparison.Ordinal), "try/catch");
string page = File.ReadAllText(Path.Combine("/opt/data/subdl-scribe", "Jellyfin.Plugin.SubdlSync", "Configuration", "configPage.html"));
Check("the operator's colour decision is pinned: cancel is ALWAYS grey #767676 (the state's colour, not the situation's)",
    page.Contains("case 'cancelled': return { color: '#767676', text: 'cancel' };", StringComparison.Ordinal)
    && !page.Contains("case 'cancelled': return { color: '#ffc107'", StringComparison.Ordinal), "cancel colour");
Check("cancel and skip share the grey, so only defer is left yellow",
    page.Contains("case 'cancelled': return { color: '#767676', text: 'cancel' };", StringComparison.Ordinal)
    && page.Contains("case 'skipped': return { color: '#767676', text: 'skip' };", StringComparison.Ordinal)
    && page.Contains("case 'deferred': return { color: '#ffc107', text: 'defer' };", StringComparison.Ordinal), "grey pairing");
Check("the row's time column reads Started (that is why the cancellation must stamp it)",
    page.Contains("var when = w.Started ? subdlFmtDateTime(w.Started)", StringComparison.Ordinal), "when=Started");
Check("the row is written AFTER the cancellation is persisted",
    cancels.IndexOf("PersistRecoveryFires()", StringComparison.Ordinal) < cancels.IndexOf("CancelledRow(upload)", StringComparison.Ordinal), "order vs persist");

// --- F-M475 (operator finding 15.11.2026): a library-scan deferral is a SCHEDULED FIRE ----------
// "defer um 15 min taucht nirgendwo auf, stattdessen defer auf den alten anchor". What the user saw:
// the page paired a FRESH attempt with the PREVIOUS run's cause, because the reason came from the
// worker row while the moment came from the fire, and the 15-minute wait was an in-process timer that
// re-ran the ARRIVAL cycle (measured live: fired 03:53:10 into "event — no arrivals collected — no
// run" while the deferred download never resumed).
string dir2 = Path.Combine(Path.GetTempPath(), "scandefer-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir2);
var c2 = new SubdlSchedulerCoordinator(null, new TestPaths(dir2), NullLogger<SubdlSchedulerCoordinator>.Instance);
var t0 = DateTime.UtcNow;

c2.ScheduleRecoveryFireAt(upload: false, t0.AddMinutes(180));
Check("a later fire is pending before the scan defer", c2.GetPendingFireUtc(upload: false) > t0.AddMinutes(170),
    c2.GetPendingFireUtc(upload: false)?.ToString("O") ?? "none");

bool armedScan = c2.ScheduleScanDeferFire(upload: false, t0.AddMinutes(15), 15);
Check("the scan defer arms although a fire was pending — the sooner attempt wins",
    armedScan && c2.GetPendingFireUtc(upload: false) <= t0.AddMinutes(16),
    $"armed={armedScan}, fire={c2.GetPendingFireUtc(upload: false):O}");
Check("the scan defer shows up where the status line reads its moments (GetPendingFires)",
    c2.GetPendingFires().ContainsKey("SubdlSyncDownloadTask"), string.Join(",", c2.GetPendingFires().Keys));
Check("the reason travels WITH the moment — not the previous run's cause",
    c2.PendingFireReason(upload: false) == "library scan running — retry in 15 min",
    c2.PendingFireReason(upload: false) ?? "null");
Check("the reason is stored as a token (no comma, no colon — the fire file is split on those)",
    c2.GetPendingFireWhy(upload: false) == "scan15", c2.GetPendingFireWhy(upload: false) ?? "null");

bool armedLater = c2.ScheduleScanDeferFire(upload: false, t0.AddMinutes(45), 45);
Check("a scan defer never pushes an earlier attempt back", !armedLater
    && c2.PendingFireReason(upload: false) == "library scan running — retry in 15 min",
    $"armed={armedLater}, reason={c2.PendingFireReason(upload: false)}");
Check("a download-only defer leaves the upload direction alone",
    !c2.GetPendingFires().ContainsKey("SubdlSyncUploadTask"), string.Join(",", c2.GetPendingFires().Keys));

var c3 = new SubdlSchedulerCoordinator(null, new TestPaths(dir2), NullLogger<SubdlSchedulerCoordinator>.Instance);
Check("moment AND reason survive a restart (the timer did not)",
    c3.GetPendingFires().ContainsKey("SubdlSyncDownloadTask")
    && c3.PendingFireReason(upload: false) == "library scan running — retry in 15 min",
    c3.PendingFireReason(upload: false) ?? "null");

c3.ScheduleRecoveryFireAt(upload: true, DateTime.UtcNow.AddMinutes(30));
Check("a respaced fire names itself and does not borrow the quota wording",
    c3.PendingFireReason(upload: true) == "rate limit or lock busy — rescheduled",
    c3.PendingFireReason(upload: true) ?? "null");

string disp = File.ReadAllText(Path.Combine("/opt/data/subdl-scribe", "Jellyfin.Plugin.SubdlSync", "ScheduledTasks", "SubdlEventDispatcher.cs"));
Check("the scan defer no longer runs an in-process timer", !disp.Contains("_scanDeferTimer", StringComparison.Ordinal), "timer still present");
int td = disp.IndexOf("private bool TryDeferForLibraryScan", StringComparison.Ordinal);
string deferBody = td < 0 ? string.Empty : disp.Substring(td, Math.Min(4000, disp.Length - td));
Check("the deferral arms the direction's own scheduled fire", deferBody.Contains("ScheduleScanDeferFire", StringComparison.Ordinal), "arming call");
Check("each direction is armed only when the cycle asked for it",
    deferBody.Contains("dir != CycleDirection.UploadOnly", StringComparison.Ordinal)
    && deferBody.Contains("dir != CycleDirection.DownloadOnly", StringComparison.Ordinal), "direction guards");
string quotaSvc = File.ReadAllText(Path.Combine("/opt/data/subdl-scribe", "Jellyfin.Plugin.SubdlSync", "Api", "SubdlQuotaService.cs"));
Check("the status line takes the reason from the pending fire, not from the worker row",
    quotaSvc.Contains("fireReason ?? row!.Detail", StringComparison.Ordinal), "row detail still wins");
Check("the reason token is persisted next to the moment",
    src.Contains("downloadWhy", StringComparison.Ordinal) && src.Contains("uploadWhy", StringComparison.Ordinal), "persist keys");
Check("the fire log names the actual reason instead of claiming a quota reset",
    src.Contains("Recovery fire: download — {Reason}", StringComparison.Ordinal), "log text");
// Bench 15.11.2026: the fire armed at 04:02:31 and fired at 04:07:35, and the log said "retry scheduled
// (reason not recorded)" — the reason had been fetched through the STATUS accessor, which answers
// "nothing pending" for an elapsed deadline (F-M368). A consumed fire must still be able to say why.
Check("a fire that is being consumed reads its reason WITHOUT the elapsed-deadline guard",
    src.Contains("ReasonText(\"download\", _recoveryFireDownloadWhy)", StringComparison.Ordinal)
    && src.Contains("ReasonText(\"upload\", _recoveryFireUploadWhy)", StringComparison.Ordinal), "consumption reads the token");
Check("the status accessor still refuses an elapsed deadline (F-M368 stays intact)",
    src.Contains("if (!when.HasValue || DateTime.UtcNow >= when.Value)", StringComparison.Ordinal), "elapsed guard");

// --- F-M475: the SAME rule for the maintenance slots -------------------------------------------
// Operator order 15.11.2026: "das sollte längst so sein". Database refresh, OSHash refresh and the
// upload postprocessing defer for one cause — a busy run lock — and they wore the previous run's text
// beside a fresh moment just like the direction rows did.
string dir3 = Path.Combine(Path.GetTempPath(), "maintfire-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir3);
var c4 = new SubdlSchedulerCoordinator(null, new TestPaths(dir3), NullLogger<SubdlSchedulerCoordinator>.Instance);
Check("nothing pending, no reason — for every maintenance slot",
    c4.PendingMaintenanceReason("refresh") == null
    && c4.PendingMaintenanceReason("oshash") == null
    && c4.PendingMaintenanceReason("postprocess") == null, "one of them already had a note");

c4.ScheduleRefreshFire(DateTime.UtcNow.AddMinutes(180));
c4.ScheduleOshashFire(DateTime.UtcNow.AddMinutes(180));
c4.SchedulePostprocessFire(DateTime.UtcNow.AddMinutes(180));
Check("a deferred database refresh names its cause",
    c4.PendingMaintenanceReason("refresh") == "run lock busy — rescheduled", c4.PendingMaintenanceReason("refresh") ?? "null");
Check("a deferred oshash refresh names its cause",
    c4.PendingMaintenanceReason("oshash") == "run lock busy — rescheduled", c4.PendingMaintenanceReason("oshash") ?? "null");
Check("a deferred postprocessing order names its cause",
    c4.PendingMaintenanceReason("postprocess") == "run lock busy — rescheduled", c4.PendingMaintenanceReason("postprocess") ?? "null");

c4.ArmUploadPostprocessingAt(DateTime.UtcNow.AddMinutes(200), "upload run finished");
Check("a normal arming clears the old note — a fresh order carries no lock-busy text",
    c4.PendingMaintenanceReason("postprocess") == null, c4.PendingMaintenanceReason("postprocess") ?? "null");

// The loader is fed a state file directly: the writer runs once per tick, so a round trip through it
// would make this check depend on the tick clock rather than on the code under test.
var statePath = Path.Combine(dir3, "scheduler-state.json");
File.WriteAllText(
    statePath,
    "{\"refresh\":\"\",\"oshash\":\"\",\"postprocess\":\"\",\"fired\":\"\",\"catchup\":false,"
    + "\"refreshDefer\":\"" + DateTime.UtcNow.AddMinutes(120).ToString("O") + "\","
    + "\"oshashDefer\":\"" + DateTime.UtcNow.AddMinutes(120).ToString("O") + "\","
    + "\"postprocessDefer\":\"\",\"refetchDefer\":\"\","
    + "\"refreshWhy\":\"lockbusy\",\"oshashWhy\":\"lockbusy\",\"postprocessWhy\":\"\"}");
var c5 = new SubdlSchedulerCoordinator(null, new TestPaths(dir3), NullLogger<SubdlSchedulerCoordinator>.Instance);
Check("the maintenance reasons survive a restart with their moments",
    c5.PendingMaintenanceReason("refresh") == "run lock busy — rescheduled"
    && c5.PendingMaintenanceReason("oshash") == "run lock busy — rescheduled",
    $"{c5.PendingMaintenanceReason("refresh") ?? "null"} / {c5.PendingMaintenanceReason("oshash") ?? "null"}");
Check("a token whose moment did not come back is not shown",
    c5.PendingMaintenanceReason("postprocess") == null, c5.PendingMaintenanceReason("postprocess") ?? "null");

c5.ScheduleRefreshFire(DateTime.UtcNow.AddMinutes(-5));
Check("an elapsed maintenance moment has no reason (F-M368 stays intact)",
    c5.PendingMaintenanceReason("refresh") == null, c5.PendingMaintenanceReason("refresh") ?? "null");
Check("the three maintenance workers are mapped to their slots",
    quotaSvc.Contains("SubdlSyncDatabaseRefreshTask", StringComparison.Ordinal)
    && quotaSvc.Contains("SubdlSyncOshashRefreshTask", StringComparison.Ordinal)
    && quotaSvc.Contains("SubDLPostprocessTask", StringComparison.Ordinal), "mapping missing");
Check("the maintenance tokens are persisted next to their moments",
    src.Contains("refreshWhy", StringComparison.Ordinal) && src.Contains("oshashWhy", StringComparison.Ordinal)
    && src.Contains("postprocessWhy", StringComparison.Ordinal), "persist keys");

Console.WriteLine();
Console.WriteLine(f == 0 ? ">>> STOPFIRE OK" : $">>> STOPFIRE {f} FAILURE(S)");
return f == 0 ? 0 : 1;

void Check(string label, bool ok, string detail)
{
    Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + label + (ok ? "" : " — " + detail));
    if (!ok) f++;
}

sealed class TestPaths : IApplicationPaths
{
    public TestPaths(string dir) { ConfigurationDirectoryPath = dir; DataPath = dir; }

    public string ProgramDataPath => Path.GetTempPath();
    public string ProgramSystemPath => Path.GetTempPath();
    public string DataPath { get; }
    public string ImageCachePath => Path.GetTempPath();
    public string PluginsPath => Path.GetTempPath();
    public string PluginConfigurationsPath => ConfigurationDirectoryPath;
    public string LogDirectoryPath => Path.GetTempPath();
    public string ConfigurationDirectoryPath { get; }
    public string SystemConfigurationFilePath => Path.Combine(ConfigurationDirectoryPath, "system.xml");
    public string CachePath => Path.GetTempPath();
    public string TempDirectory => Path.GetTempPath();
    public string VirtualDataPath => Path.GetTempPath();
    public string TrickplayPath => Path.GetTempPath();
    public string WebPath => Path.GetTempPath();
    public string BackupPath => Path.GetTempPath();
    public void MakeSanityCheckOrThrow() { }
    public void CreateAndCheckMarker(string path, string markerName, bool recursive = false) { }
}
