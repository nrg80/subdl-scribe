using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Configuration;

namespace Jellyfin.Plugin.SubdlScribe.Registry;

/// <summary>
/// F-M352 (operator order 19.10.2026): decides which worker rows are painted GREY as <c>off</c>.
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
/// Off outranks every stored outcome. A row that ran green yesterday must not claim health while the
/// switch is off, and it also outranks a pending fire (F-M292/F-M294): a fire that was armed before
/// the switch went off cannot produce work either.
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

    /// <summary>
    /// F-M352: the full decision behind the grey lamp — the configuration gate PLUS two guards that keep
    /// the lamp from hiding real work.
    /// <para>
    /// Measured by the operator on the day the rule shipped: pressing the postprocessing button in the
    /// dashboard made the row read "off" INSTANTLY, although a run had just started — the postprocessing
    /// task deliberately has no upload gate (F-M291, the manual button must always run), so "off" was
    /// stating a capability while the work was happening. Two guards follow from that:
    /// </para>
    /// <list type="number">
    /// <item>A row that is RUNNING is never "off".</item>
    /// <item>A row whose run ended AT OR AFTER the configuration was last saved is never "off" either:
    /// that run belongs to the current setting, so it is the honest answer. Only a row that has not run
    /// since the switch went off (or has never run) reads "off". The comparison is inclusive because a
    /// run can finish in the same second the operator saves the page.</item>
    /// </list>
    /// </summary>
    /// <param name="workerKey">The worker's row key.</param>
    /// <param name="config">The live configuration.</param>
    /// <param name="outcome">The row's stored outcome.</param>
    /// <param name="endedUtc">The row's end, in UTC (null while running or never run).</param>
    /// <param name="configSavedUtc">When the configuration file was last written, in UTC (null when unknown).</param>
    /// <returns>True when the row must be painted "off".</returns>
    public static bool PaintsOff(
        string? workerKey,
        PluginConfiguration? config,
        string? outcome,
        DateTime? endedUtc,
        DateTime? configSavedUtc)
    {
        if (!Applies(workerKey, config))
        {
            return false;
        }

        if (string.Equals(outcome, WorkerRunRegistry.Outcome.Running, StringComparison.Ordinal))
        {
            return false;
        }

        if (endedUtc.HasValue && configSavedUtc.HasValue
            && endedUtc.Value.ToUniversalTime() >= configSavedUtc.Value.ToUniversalTime())
        {
            return false;
        }

        return true;
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
