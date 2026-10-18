
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
Check("the operator's colour decision is pinned: cancel stays #ffc107, not grey",
    page.Contains("case 'cancelled': return { color: '#ffc107', text: 'cancel' };", StringComparison.Ordinal), "cancel colour");
Check("the row's time column reads Started (that is why the cancellation must stamp it)",
    page.Contains("var when = w.Started ? subdlFmtDateTime(w.Started)", StringComparison.Ordinal), "when=Started");
Check("the row is written AFTER the cancellation is persisted",
    cancels.IndexOf("PersistRecoveryFires()", StringComparison.Ordinal) < cancels.IndexOf("CancelledRow(upload)", StringComparison.Ordinal), "order vs persist");

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
