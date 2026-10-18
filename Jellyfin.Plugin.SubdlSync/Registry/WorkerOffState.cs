using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Configuration;

namespace Jellyfin.Plugin.SubdlScribe.Registry;

/// <summary>
/// F-M352 (operator order 19.10.2026): decides whether a worker is switched OFF — the note under the
/// Workers list says "off" for it (operator: the lamps stay as they are, the off state is noted in the
/// worker section as the last entry).
/// <para>
/// The conditions are not invented here — each one is a hard refusal that already exists in the code,
/// so the lamp states what the run would actually do:
/// </para>
/// <list type="bullet">
/// <item>Download: <c>DownloadEnabled</c> false (SubdlDownloadTask), empty <c>SelectedLibraries</c>
/// (F-M213, hard stop for both directions), an empty target-language list (DownloadPipeline
/// "No target languages configured"), or a missing credential (DownloadPipeline/MissingCredentials).</item>
/// <item>Upload: <c>UploadEnabled</c> false (SubdlUploadTask), empty <c>SelectedLibraries</c>, or a
/// missing credential. The upload has no language list, so it has no language condition.</item>
/// <item>Upload postprocessing: <c>UploadEnabled</c> false or the cadence on Never/Manual/OnArrival —
/// exactly the gate the scheduler uses before it arms the anchor (F-M291). The library selection is
/// deliberately NOT a condition: the postprocessing reads <c>/user/mySubtitles</c> and never touches
/// the library, so libraries do not stop it.</item>
/// </list>
/// <para>
/// The lamps and every stored outcome are left untouched: this class only feeds the trailing "off"
/// notes, so no run is ever hidden by a switch (operator finding: a greyed row made a just-started
/// manual postprocessing run read "off").
/// </para>
/// </summary>
public static class WorkerOffState
{
    /// <summary>
    /// True when the given worker cannot start under the given configuration.
    /// </summary>
    /// <param name="workerKey">The worker's row key.</param>
    /// <param name="config">The live plugin configuration; null means "no statement possible" — a null
    /// configuration must never paint a lamp, so every key answers false.</param>
    /// <returns>True when the row belongs to a switched-off worker.</returns>
    public static bool Applies(string? workerKey, PluginConfiguration? config)
    {
        if (string.IsNullOrEmpty(workerKey) || config == null)
        {
            return false;
        }

        if (string.Equals(workerKey, WorkerRunRegistry.DownloadWorkerKey, StringComparison.Ordinal))
        {
            return !config.DownloadEnabled
                || NoLibraries(config)
                || NoTargetLanguages(config)
                || MissingCredential(config);
        }

        if (string.Equals(workerKey, WorkerRunRegistry.UploadWorkerKey, StringComparison.Ordinal))
        {
            return !config.UploadEnabled
                || NoLibraries(config)
                || MissingCredential(config);
        }

        if (string.Equals(workerKey, PostprocessWorkerKey, StringComparison.Ordinal))
        {
            return !config.UploadEnabled
                || config.UploadPostprocessInterval is UpdateInterval.Never or UpdateInterval.Manual or UpdateInterval.OnArrival;
        }

        return false;
    }

    /// <summary>The postprocessing task's worker key (SubdlPostprocessTask.Key).</summary>
    public const string PostprocessWorkerKey = "SubDLPostprocessTask";

    /// <summary>An empty library selection is a hard stop for both pipelines (F-M213).</summary>
    private static bool NoLibraries(PluginConfiguration config)
        => config.SelectedLibraries == null || config.SelectedLibraries.Count == 0;

    /// <summary>
    /// The download's target languages, parsed exactly as DownloadPipeline.TargetLanguages parses them:
    /// split on the comma, drop empty entries, trim, upper-case, deduplicate. An empty result is the
    /// pipeline's "No target languages configured" refusal.
    /// </summary>
    private static bool NoTargetLanguages(PluginConfiguration config)
        => ParseTargetLanguages(config.DownloadLanguages).Count == 0;

    /// <summary>Parses the configured language list the way the download pipeline does.</summary>
    /// <param name="downloadLanguages">The raw configuration value.</param>
    /// <returns>The effective target languages.</returns>
    public static List<string> ParseTargetLanguages(string? downloadLanguages)
        => (downloadLanguages ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.ToUpperInvariant())
            .Distinct()
            .ToList();

    /// <summary>A missing credential refuses both pipelines up front (F-M19/F-M203).</summary>
    private static bool MissingCredential(PluginConfiguration config)
        => config.MissingCredentials().Count > 0;
}
