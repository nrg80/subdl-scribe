// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Configuration;
using Jellyfin.Plugin.SubdlScribe.Pipeline;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe.ScheduledTasks;

/// <summary>
/// F-M176: scheduled cleanup of rejected SubDL dashboard entries. Runs independently
/// on its own diced anchor. The pipeline method itself holds the global run lock.
/// </summary>
public class SubdlPostprocessTask : IScheduledTask
{
    private readonly ILogger<SubdlPostprocessTask> _logger;

    public SubdlPostprocessTask(ILogger<SubdlPostprocessTask> logger)
    {
        _logger = logger;
    }

    public string Name => "SubDL Postprocessing";

    public string Key => "SubDLPostprocessTask";

    public string Description => "Cleans up rejected SubDL dashboard entries (max 5 pages per run).";

    public string Category => "SubDL Scribe";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        LogUtil.Normal(_logger, "[SubDL] Scheduled postprocessing task triggered.");
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            _logger.LogError("[SubDL] Plugin instance missing — cannot run.");
            return;
        }

        plugin.WorkerRuns.Start("SubDLPostprocessTask", Name);

        try
        {
            await UploadPipeline.RunUploadPostprocessingAsync().ConfigureAwait(false);
            // The pipeline keeps its own rich result in memory; this row is the durable record the
            // configuration page reads, and it is the only part that survives a restart.
            plugin.WorkerRuns.Finish("SubDLPostprocessTask", Name, Registry.WorkerRunRegistry.Outcome.Ok,
                UploadPipeline.PostprocessingLastResult);
        }
        catch (OperationCanceledException)
        {
            plugin.WorkerRuns.Finish("SubDLPostprocessTask", Name, Registry.WorkerRunRegistry.Outcome.Cancelled, "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            plugin.WorkerRuns.Finish("SubDLPostprocessTask", Name, Registry.WorkerRunRegistry.Outcome.Failed, ex.Message);
            throw;
        }
    }
}
