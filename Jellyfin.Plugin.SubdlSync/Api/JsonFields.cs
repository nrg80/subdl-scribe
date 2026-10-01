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
using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// Readers for the fields SubDL sends.
/// <para>
/// These exist because the same field is not always JSON-typed the same way across endpoints.
/// Measured live (28.09.2026, 37 search hits across 4 queries): <c>hi</c> and <c>full_season</c> arrive
/// as JSON booleans, while the numeric fields (<c>season</c>, <c>episode</c>, <c>framerate</c>) arrive as
/// numbers. A reader that insists on ONE kind silently yields the default for every other kind, and
/// because the default of a flag is "off", the failure looks like "the feature does nothing" rather
/// than like an error. That is exactly how <c>hi</c> was lost: it was read with an int-only reader, so
/// every candidate came back as "not hearing impaired".
/// </para>
/// <para>
/// The rule for this plugin: read what the API actually sends, accept the documented alternates, and
/// keep ONE implementation here instead of a copy per class.
/// </para>
/// </summary>
internal static class JsonFields
{
    /// <summary>
    /// Reads a flag. Accepts the JSON booleans as well as the 0/1 form some endpoints use, because
    /// the same logical flag is not uniformly typed across SubDL's endpoints.
    /// </summary>
    /// <param name="el">JSON element to read from.</param>
    /// <param name="name">Property name.</param>
    /// <returns>The flag, or false when the property is absent or unusable.</returns>
    public static bool GetBool(JsonElement el, string name)
    {
        return el.TryGetProperty(name, out var v) && ToBool(v);
    }

    /// <summary>
    /// Converts one JSON value to a flag: booleans as sent, numbers by "non-zero", strings by the
    /// usual literals ("true"/"false", "1"/"0"). Anything else is false rather than an exception —
    /// a missing flag must never abort a download run.
    /// </summary>
    /// <param name="v">JSON value.</param>
    /// <returns>The flag.</returns>
    public static bool ToBool(JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                return v.TryGetInt64(out var n) && n != 0;
            case JsonValueKind.String:
                var s = v.GetString();
                if (string.IsNullOrWhiteSpace(s))
                {
                    return false;
                }

                s = s.Trim();
                return s.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("yes", StringComparison.OrdinalIgnoreCase)
                    || (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0);
            default:
                return false;
        }
    }

    /// <summary>
    /// Reads a number that the API may send as a JSON number OR as a numeric string. Live example:
    /// <c>fps</c> comes as the string "23.976" on some entries and is absent on others, so an
    /// int-only reader silently returned "unknown" for every value that was actually present.
    /// </summary>
    /// <param name="el">JSON element to read from.</param>
    /// <param name="name">Property name.</param>
    /// <returns>The number, or 0 when absent or unusable.</returns>
    public static double GetDouble(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
        {
            return 0.0;
        }

        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                return v.TryGetDouble(out var d) ? d : 0.0;
            case JsonValueKind.String:
                var s = v.GetString();
                return !string.IsNullOrWhiteSpace(s)
                    && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
                        ? p
                        : 0.0;
            default:
                return 0.0;
        }
    }
}
