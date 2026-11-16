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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// F-M528: tolerant number reads for SubDL's JSON, and a note whenever tolerance was needed.
/// <para>
/// Background: on 16.11.2026 two download cycles died inside <c>SubtitleCandidate.FromJson</c> with
/// <c>FormatException: One of the identified items was in an invalid format.</c> — that is what
/// <see cref="JsonElement.GetInt32"/> throws on a JSON number that carries a fraction (e.g.
/// <c>1.5</c>) or on a value outside the Int32 range, and the exception took the WHOLE run down
/// instead of one hit. The trigger comes from SubDL's payload, but the tolerance is our job: a
/// foreign field we cannot read must cost one candidate, never the cycle.
/// </para>
/// <para>
/// The same read also notes WHAT arrived, because "our parser was intolerant" and "the field came
/// back as a fraction" are different facts and only the second one is reportable to SubDL.
/// </para>
/// </summary>
internal static class JsonAnomalies
{
    /// <summary>Per-field notes of the running search: at most one line per distinct shape.</summary>
    private static readonly ConcurrentDictionary<string, string> Notes = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads a property as an integer, accepting every shape SubDL has actually sent: a whole number,
    /// a number with a fraction (truncated), a number too large for Int32 (clamped) and a numeric
    /// string. Anything else is 0 — never an exception.
    /// </summary>
    /// <param name="el">JSON element to read from.</param>
    /// <param name="name">Property name (used for the note).</param>
    /// <returns>The value, or 0 when absent or unusable.</returns>
    public static int GetInt(JsonElement el, string name)
    {
        return el.TryGetProperty(name, out var v) ? ToInt(v, name) : 0;
    }

    /// <summary>
    /// Reads one JSON value as an integer without ever throwing.
    /// </summary>
    /// <param name="v">The value.</param>
    /// <param name="name">Field name for the note; null or empty when the caller does not know it.</param>
    /// <returns>The value, or 0 when unusable.</returns>
    public static int ToInt(JsonElement v, string? name = null)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                if (v.TryGetInt32(out var i))
                {
                    return i;
                }

                if (v.TryGetDouble(out var d))
                {
                    // A fraction or an out-of-range number: readable, but not what an int field
                    // usually looks like. Clamp instead of throwing.
                    var clamped = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, Math.Truncate(d)));
                    Note(name, v.GetRawText(), JsonValueKind.Number, clamped.ToString(CultureInfo.InvariantCulture));
                    return clamped;
                }

                Note(name, v.GetRawText(), JsonValueKind.Number, "0");
                return 0;

            case JsonValueKind.String:
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)
                    && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                if (!string.IsNullOrWhiteSpace(s)
                    && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ds))
                {
                    var clampedS = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, Math.Truncate(ds)));
                    Note(name, v.GetRawText(), JsonValueKind.String, clampedS.ToString(CultureInfo.InvariantCulture));
                    return clampedS;
                }

                Note(name, v.GetRawText(), JsonValueKind.String, "0");
                return 0;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return 0;

            default:
                Note(name, v.GetRawText(), v.ValueKind, "0");
                return 0;
        }
    }

    /// <summary>
    /// Takes every note collected since the last call and clears them. The caller logs the result —
    /// at most one line per cycle, and only when something actually needed tolerance.
    /// </summary>
    /// <returns>One readable line per distinct shape, e.g. <c>"season" = 1.5 (Number) → 1</c>.</returns>
    public static IReadOnlyList<string> Take()
    {
        if (Notes.IsEmpty)
        {
            return Array.Empty<string>();
        }

        var lines = Notes.Values.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Notes.Clear();
        return lines;
    }

    private static void Note(string? name, string raw, JsonValueKind kind, string readAs)
    {
        var field = string.IsNullOrWhiteSpace(name) ? "(unnamed field)" : $"\"{name}\"";
        var shown = raw.Length > 40 ? raw[..40] + "…" : raw;
        Notes.TryAdd($"{field}|{shown}|{kind}", $"{field} = {shown} ({kind}) → read as {readAs}");
    }
}
