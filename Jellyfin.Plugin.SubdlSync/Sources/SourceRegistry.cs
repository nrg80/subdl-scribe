// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SubScribe.Sources;

/// <summary>
/// F-M625: was eine Quelle kann. Der Ablauf fragt die Fähigkeit ab — er vergleicht keine Quellennamen.
/// </summary>
[Flags]
public enum SourceCapabilities
{
    /// <summary>Die Quelle kann gar nichts (nur ein Platzhalter).</summary>
    None = 0,

    /// <summary>Die Quelle lässt sich nach Untertiteln durchsuchen und liefert Treffer.</summary>
    Search = 1,

    /// <summary>Die Quelle gibt einen gefundenen Untertitel heraus.</summary>
    Fetch = 2,

    /// <summary>Die Quelle nimmt eigene Untertitel entgegen.</summary>
    Upload = 4,

    /// <summary>Die Quelle lässt einen fehlenden Film oder eine fehlende Serie anlegen.</summary>
    CreateTitle = 8,

    /// <summary>Die Quelle führt ein Kontingent und antwortet mit Erschöpfung.</summary>
    Quota = 16,

    /// <summary>Der Mindestumfang einer angebundenen Quelle (F-M630).</summary>
    Minimum = Search | Fetch,
}

/// <summary>
/// F-M626: eine Quelle ist ein Eintrag in dieser Liste, kein Zweig im Ablauf.
/// <para>
/// Die Reihenfolge der Liste ist die Reihenfolge, in der gefragt wird; die Vorrangstellung selbst bleibt
/// eine Einstellung (F-M627) und wird hier nur abgebildet. Eine neue Quelle anbinden heißt: einen
/// Eintrag hinzufügen — kein Schema, kein Zweig.
/// </para>
/// </summary>
public static class SourceRegistry
{
    private static readonly object Gate = new();

    private static readonly List<SourceDescriptor> Registriert = new();

    static SourceRegistry()
    {
        Register(new SourceDescriptor("subdl", SourceCapabilities.Search | SourceCapabilities.Fetch
            | SourceCapabilities.Upload | SourceCapabilities.Quota));
        Register(new SourceDescriptor("tvsubtitles", SourceCapabilities.Search | SourceCapabilities.Fetch
            | SourceCapabilities.Upload));
        Register(new SourceDescriptor("moviesubtitles", SourceCapabilities.Search | SourceCapabilities.Fetch
            | SourceCapabilities.Upload | SourceCapabilities.CreateTitle));
    }

    /// <summary>Alle angebundenen Quellen in Frage-Reihenfolge.</summary>
    /// <returns>The registered sources.</returns>
    public static IReadOnlyList<SourceDescriptor> All()
    {
        lock (Gate)
        {
            return Registriert.ToList();
        }
    }

    /// <summary>Die Schlüssel aller angebundenen Quellen.</summary>
    /// <returns>The registered source keys, in order.</returns>
    public static IReadOnlyList<string> Keys()
        => All().Select(s => s.Key).ToList();

    /// <summary>Schlägt eine Quelle nach.</summary>
    /// <param name="key">Source key.</param>
    /// <returns>The descriptor, or null when the source is not attached.</returns>
    public static SourceDescriptor? ByKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        lock (Gate)
        {
            return Registriert.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// F-M625/F-M630: kann diese Quelle diese Aufgabe? Unbekannte Quellen können nichts, und die
    /// Mindestaufgaben gelten als Fähigkeit nur, wenn die Quelle sie ausdrücklich meldet.
    /// </summary>
    /// <param name="key">Source key.</param>
    /// <param name="capability">The capability asked for.</param>
    /// <returns>True when the source declares that capability.</returns>
    public static bool Can(string? key, SourceCapabilities capability)
    {
        SourceDescriptor? quelle = ByKey(key);
        return quelle != null && (quelle.Capabilities & capability) == capability;
    }

    /// <summary>
    /// Die Quellen, die eine Aufgabe können — in Frage-Reihenfolge. Das ist die Liste, die der Ablauf
    /// abarbeitet, statt je Quelle einen Zweig zu haben (F-M626).
    /// </summary>
    /// <param name="capability">The capability asked for.</param>
    /// <returns>The source keys that declare it.</returns>
    public static IReadOnlyList<string> For(SourceCapabilities capability)
        => All().Where(s => (s.Capabilities & capability) == capability).Select(s => s.Key).ToList();

    /// <summary>
    /// Trägt eine Quelle ein. Ein zweiter Eintrag unter demselben Schlüssel ersetzt den ersten — die
    /// Registrierung bleibt damit die eine Wahrheit über die angebundenen Quellen.
    /// </summary>
    /// <param name="descriptor">The source.</param>
    public static void Register(SourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (Gate)
        {
            Registriert.RemoveAll(s => string.Equals(s.Key, descriptor.Key, StringComparison.Ordinal));
            Registriert.Add(descriptor);
        }
    }
}

/// <summary>
/// Eine angebundene Quelle: ihr Schlüssel und was sie kann (F-M624/F-M625).
/// </summary>
/// <param name="Key">The source key, as it is written into the marks.</param>
/// <param name="Capabilities">What this source can do.</param>
public sealed record SourceDescriptor(string Key, SourceCapabilities Capabilities);
