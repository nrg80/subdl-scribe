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
using Jellyfin.Plugin.SubdlScribe.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe.Pipeline;

/// <summary>
/// The plugin's own log mode decides how much detail its lines carry, and every
/// line says which of the three levels produced it.
/// <para>
/// Why not real levels: Jellyfin's Serilog pipeline filters a record BEFORE the
/// sink sees it, so a line written at Debug never appears while the server sits at
/// its default Information — and Trace ranks below Information, which is why the
/// TMDb traces gated at Verbose reached no log at all (measured 0 [TRC] lines in a
/// full production day). Everything here therefore goes out at INFORMATION, which
/// the server always passes, and the level travels as a text marker instead:
/// <c>[N]</c>, <c>[V]</c> or <c>[D]</c> in front of the message.
/// </para>
/// <para>
/// Normal: lifecycle and summaries. Verbose: per-item work and the TMDb trace.
/// Debug: internals, diagnostics and API traces. Warnings and errors are never
/// gated and need no marker — Jellyfin's own level column already labels them.
/// </para>
/// </summary>
/// F-M224: the plugin's own log mode is the only authority, and every line is written at a level
/// Jellyfin always passes.
public static class LogUtil
{
    /// <summary>Marker for a line shown at every level.</summary>
    // F-M226: the level marker sits in front of the message, because the server column cannot carry it.
    public const string NormalTag = "[N] ";

    /// <summary>Marker for a line shown from Verbose on.</summary>
    public const string VerboseTag = "[V] ";

    /// <summary>Marker for a line shown from Debug on.</summary>
    public const string DebugTag = "[D] ";

    /// <summary>
    /// The effective mode, resolved from the live configuration so helpers in
    /// the registry and data layers need no reference of their own. Falls back
    /// to Normal before the plugin instance exists (e.g. during startup).
    /// </summary>
    private static LogLevelMode Mode =>
        global::Jellyfin.Plugin.SubdlScribe.Plugin.Instance?.Configuration.LogMode
        ?? LogLevelMode.Normal;

    /// <summary>Lifecycle/summary line — shown at every level.</summary>
    /// <param name="logger">Target logger.</param>
    /// <param name="message">Message template.</param>
    /// <param name="args">Template arguments.</param>
#pragma warning disable CA2254 // template comes from the call site by design
    // F-M225: every line belongs to exactly ONE level, and the statistics never depend on
    // logging; internals and diagnostics go to Detail, per-item work to PerItem.
    public static void Normal(ILogger? logger, string message, params object?[] args)
        => logger?.LogInformation(NormalTag + message, args);

    /// <summary>Lifecycle/summary line carrying an exception.</summary>
    /// <param name="logger">Target logger.</param>
    /// <param name="ex">Exception to record.</param>
    /// <param name="message">Message template.</param>
    /// <param name="args">Template arguments.</param>
    public static void Normal(ILogger? logger, Exception ex, string message, params object?[] args)
        => logger?.LogInformation(ex, NormalTag + message, args);

    /// <summary>Per-item line: shown from Verbose on.</summary>
    /// <param name="mode">Effective plugin log mode.</param>
    /// <param name="logger">Target logger.</param>
    /// <param name="message">Message template.</param>
    /// <param name="args">Template arguments.</param>
    public static void PerItem(LogLevelMode mode, ILogger logger, string message, params object?[] args)
    {
        if (mode >= LogLevelMode.Verbose)
        {
            logger.LogInformation(VerboseTag + message, args);
        }
    }

    /// <summary>Per-item line, resolving the mode itself.</summary>
    /// <param name="logger">Target logger.</param>
    /// <param name="message">Message template.</param>
    /// <param name="args">Template arguments.</param>
    public static void PerItem(ILogger? logger, string message, params object?[] args)
        => PerItem(Mode, logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, message, args);

    /// <summary>
    /// Detail line — the successor of the old LogDebug calls: written at
    /// Information so the server level never has to be raised, and shown only
    /// when the plugin's own mode is Debug.
    /// </summary>
    /// <param name="mode">Effective plugin log mode.</param>
    /// <param name="logger">Target logger.</param>
    /// <param name="message">Message template.</param>
    /// <param name="args">Template arguments.</param>
    public static void Detail(LogLevelMode mode, ILogger? logger, string message, params object?[] args)
    {
        if (mode >= LogLevelMode.Debug)
        {
            logger?.LogInformation(DebugTag + message, args);
        }
    }

    /// <summary>Detail line, resolving the mode itself.</summary>
    /// <param name="logger">Target logger.</param>
    /// <param name="message">Message template.</param>
    /// <param name="args">Template arguments.</param>
    public static void Detail(ILogger? logger, string message, params object?[] args)
        => Detail(Mode, logger, message, args);

    /// <summary>Detail line carrying an exception.</summary>
    /// <param name="mode">Effective plugin log mode.</param>
    /// <param name="logger">Target logger.</param>
    /// <param name="ex">Exception to record.</param>
    /// <param name="message">Message template.</param>
    /// <param name="args">Template arguments.</param>
    public static void Detail(LogLevelMode mode, ILogger? logger, Exception ex, string message, params object?[] args)
    {
        if (mode >= LogLevelMode.Debug)
        {
            logger?.LogInformation(ex, DebugTag + message, args);
        }
    }

    /// <summary>Detail line carrying an exception, resolving the mode itself.</summary>
    /// <param name="logger">Target logger.</param>
    /// <param name="ex">Exception to record.</param>
    /// <param name="message">Message template.</param>
    /// <param name="args">Template arguments.</param>
    public static void Detail(ILogger? logger, Exception ex, string message, params object?[] args)
        => Detail(Mode, logger, ex, message, args);

    /// <summary>
    /// Call trace (the old LogTrace path), shown from Verbose on — the level a
    /// user picks precisely to see it.
    /// </summary>
    /// <param name="mode">Effective plugin log mode.</param>
    /// <param name="logger">Target logger.</param>
    /// <param name="message">Message template.</param>
    /// <param name="args">Template arguments.</param>
    public static void Trace(LogLevelMode mode, ILogger? logger, string message, params object?[] args)
    {
        if (mode >= LogLevelMode.Verbose)
        {
            logger?.LogInformation(VerboseTag + message, args);
        }
    }

    /// <summary>Call trace, resolving the mode itself.</summary>
    /// <param name="logger">Target logger.</param>
    /// <param name="message">Message template.</param>
    /// <param name="args">Template arguments.</param>
    public static void Trace(ILogger? logger, string message, params object?[] args)
        => Trace(Mode, logger, message, args);
#pragma warning restore CA2254
}
