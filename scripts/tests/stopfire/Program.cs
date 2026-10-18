
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
