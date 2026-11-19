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
using System.Text.Json;

namespace Jellyfin.Plugin.SubScribe.Api;

/// <summary>
/// F-M530: the documented shape of every field SubDL answers, checked against what actually arrived.
/// <para>
/// The operator's rule (17.11.2026): every metadata field must be robust against a wrong entry —
/// "Meldung und droppen, kein run fail". Robustness here means two things, and this class is the
/// second half of it: the READERS never throw (<see cref="JsonAnomalies"/>, <see cref="JsonFields"/>),
/// and this table decides what counts as a wrong entry. A field that arrives in a shape its own
/// documentation does not allow is named, its subtitle is dropped, and the run keeps going.
/// </para>
/// <para>
/// The table follows https://subdl.com/api-doc, not our convenience: <c>framerate</c> is a CODE
/// (integer), <c>fps</c> is the human-readable rate and may be text, <c>hi</c>/<c>full_season</c> are
/// booleans. Reading <c>fps</c> as text is therefore NOT a finding even though the doc prints it
/// unquoted — dropping a quarter of all hits over a value we read correctly would be the wrong
/// answer to a documentation detail.
/// </para>
/// </summary>
public static class ScribeFieldSchema
{
    private enum Shape
    {
        /// <summary>JSON string.</summary>
        Text,

        /// <summary>Integer number (no fraction).</summary>
        Whole,

        /// <summary>JSON boolean.</summary>
        Flag,

        /// <summary>Number or numeric text — the documented alternates.</summary>
        NumberOrText
    }

    /// <summary>Fields of one subtitle entry, with the shape the doc gives them.</summary>
    private static readonly (string Name, Shape Shape)[] EntryFields =
    {
        ("season", Shape.Whole),
        ("episode", Shape.Whole),
        ("episode_from", Shape.Whole),
        ("episode_end", Shape.Whole),
        ("framerate", Shape.Whole),
        ("download_count", Shape.Whole),
        ("hi", Shape.Flag),
        ("full_season", Shape.Flag),
        ("followed", Shape.Flag),
        ("muted", Shape.Flag),
        ("fps", Shape.NumberOrText),
        ("match_score", Shape.NumberOrText),
        ("release_name", Shape.Text),
        ("language", Shape.Text),
        ("name", Shape.Text),
        ("author", Shape.Text),
        ("url", Shape.Text),
        ("subtitlePage", Shape.Text)
    };

    /// <summary>Fields of one file inside a pack (<c>unpack_files[]</c>).</summary>
    private static readonly (string Name, Shape Shape)[] PackFileFields =
    {
        ("season", Shape.Whole),
        ("episode", Shape.Whole),
        ("size", Shape.Whole),
        ("hi", Shape.Flag),
        ("file_n_id", Shape.Text),
        ("name", Shape.Text),
        ("release_name", Shape.Text),
        ("language", Shape.Text),
        ("format", Shape.Text),
        ("md5", Shape.Text),
        ("url", Shape.Text)
    };

    /// <summary>
    /// Reads the identity of an entry — needed so a finding can name WHICH subtitle carries the wrong
    /// value. Read directly off the JSON, so it works even when the entry is dropped.
    /// </summary>
    /// <param name="el">The entry.</param>
    /// <returns>Entry id, release name and language, each empty when absent.</returns>
    public static (string SubId, string Name, string Language, string NId) Identity(JsonElement el)
    {
        string id = string.Empty;
        if (el.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
        {
            string url = u.GetString() ?? string.Empty;
            var segs = url.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length >= 2 && segs[^2] == "subtitle")
            {
                string file = segs[^1];
                int dot = file.IndexOf('.', StringComparison.Ordinal);
                id = dot > 0 ? file[..dot] : file;
            }
        }

        string name = Text(el, "release_name");
        string lang = Text(el, "language").ToUpperInvariant();
        string nId = string.Empty;
        if (el.TryGetProperty("subtitlePage", out var sp) && sp.ValueKind == JsonValueKind.String)
        {
            string page = sp.GetString() ?? string.Empty;
            int slash = page.LastIndexOf('/');
            if (slash >= 0 && slash + 1 < page.Length)
            {
                nId = page[(slash + 1)..];
            }
        }

        return (id, name, lang, nId);
    }

    /// <summary>
    /// Names every field of one entry — and of every file inside its pack — that arrived in a shape
    /// the documentation does not allow. Absent and null fields are not findings: SubDL omits what
    /// does not apply, and "not there" is not "wrong".
    /// </summary>
    /// <param name="el">The entry as answered.</param>
    /// <returns>Findings, empty when the shape is as documented.</returns>
    public static IReadOnlyList<FieldShapeFinding> Check(JsonElement el)
    {
        var findings = new List<FieldShapeFinding>();
        CheckFields(el, string.Empty, EntryFields, findings);

        if (el.TryGetProperty("unpack_files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind == JsonValueKind.Object)
                {
                    CheckFields(file, $"unpack_files[{i}].", PackFileFields, findings);
                }
                else if (file.ValueKind != JsonValueKind.Null)
                {
                    findings.Add(new FieldShapeFinding(
                        $"unpack_files[{i}]", "unpack_files", Raw(file), Kind(file), "an object"));
                }

                i++;
            }
        }
        else if (el.TryGetProperty("unpack_files", out var bad) && bad.ValueKind != JsonValueKind.Null)
        {
            findings.Add(new FieldShapeFinding(
                "unpack_files", "unpack_files", Raw(bad), Kind(bad), "an array"));
        }

        return findings;
    }


    /// <summary>Fields of the film block (<c>results[0]</c>).</summary>
    private static readonly (string Name, Shape Shape)[] FilmFields =
    {
        ("imdb_id", Shape.Text),
        ("tmdb_id", Shape.NumberOrText)
    };

    /// <summary>Fields of the quota answer (<c>usage.search</c> / <c>usage.downloads</c>).</summary>
    private static readonly (string Name, Shape Shape)[] QuotaFields =
    {
        ("used", Shape.Whole),
        ("limit", Shape.Whole),
        ("remaining", Shape.Whole),
        ("reset_at", Shape.Text)
    };

    /// <summary>Fields of one entry in the own-subtitle list (<c>/user/mySubtitles</c>).</summary>
    private static readonly (string Name, Shape Shape)[] OwnEntryFields =
    {
        ("id", Shape.Whole),
        ("subtitles_uploaded_id", Shape.Whole),
        ("tmdb_id", Shape.NumberOrText),
        ("n_id", Shape.Text),
        ("name", Shape.Text),
        ("language", Shape.Text),
        ("lang", Shape.Text),
        ("imdb_id", Shape.Text)
    };

    /// <summary>Fields of the upload answer (<c>subtitle</c> block).</summary>
    private static readonly (string Name, Shape Shape)[] UploadAnswerFields =
    {
        ("sd_id", Shape.Text),
        ("subtitle_id", Shape.Whole)
    };

    /// <summary>
    /// F-M532: where a finding goes. The client installs a logger here, so the parsers of the OTHER
    /// answers — which own no logger (the quota snapshot is a plain model) — report through the same
    /// door as the search answer.
    /// </summary>
    public static Action<FieldShapeFinding, string>? OnMismatch { get; set; }

    /// <summary>Checks the film block <c>results[0]</c>.</summary>
    /// <param name="el">The film entry.</param>
    /// <returns>Findings.</returns>
    public static IReadOnlyList<FieldShapeFinding> CheckFilm(JsonElement el) => CheckTable(el, string.Empty, FilmFields);

    /// <summary>Checks the quota answer, including its two nested counter blocks.</summary>
    /// <param name="el">The answer root.</param>
    /// <returns>Findings.</returns>
    public static IReadOnlyList<FieldShapeFinding> CheckQuota(JsonElement el)
    {
        var findings = new List<FieldShapeFinding>();
        var usage = el.TryGetProperty("usage", out var us) && us.ValueKind == JsonValueKind.Object ? us : el;
        foreach (string block in new[] { "search", "downloads" })
        {
            if (usage.TryGetProperty(block, out var b) && b.ValueKind == JsonValueKind.Object)
            {
                CheckFields(b, block + ".", QuotaFields, findings);
            }
        }

        return findings;
    }

    /// <summary>Checks one entry of the own-subtitle list.</summary>
    /// <param name="el">The entry.</param>
    /// <returns>Findings.</returns>
    public static IReadOnlyList<FieldShapeFinding> CheckOwnEntry(JsonElement el)
    {
        var findings = new List<FieldShapeFinding>();
        if (el.TryGetProperty("subtitle", out var sub) && sub.ValueKind == JsonValueKind.Object)
        {
            CheckFields(sub, "subtitle.", OwnEntryFields, findings);
        }

        return findings;
    }

    /// <summary>Checks the upload answer.</summary>
    /// <param name="el">The answer root.</param>
    /// <returns>Findings.</returns>
    public static IReadOnlyList<FieldShapeFinding> CheckUploadAnswer(JsonElement el)
    {
        var findings = new List<FieldShapeFinding>();
        if (el.TryGetProperty("subtitle", out var sub) && sub.ValueKind == JsonValueKind.Object)
        {
            CheckFields(sub, "subtitle.", UploadAnswerFields, findings);
        }

        return findings;
    }

    /// <summary>
    /// Hands findings to the installed reporter (F-M532). Returns true when something was wrong, so
    /// the caller can drop the value/record it just parsed.
    /// </summary>
    /// <param name="findings">The findings.</param>
    /// <param name="context">Which answer they came from ("quota answer", "own subtitle list", …).</param>
    /// <returns>True when the answer carried a wrong field shape.</returns>
    public static bool Emit(IReadOnlyList<FieldShapeFinding> findings, string context)
    {
        if (findings.Count == 0)
        {
            return false;
        }

        var reporter = OnMismatch;
        if (reporter != null)
        {
            foreach (var f in findings)
            {
                reporter(f, context);
            }
        }

        return true;
    }

    private static IReadOnlyList<FieldShapeFinding> CheckTable(
        JsonElement el, string prefix, (string Name, Shape Shape)[] fields)
    {
        var findings = new List<FieldShapeFinding>();
        CheckFields(el, prefix, fields, findings);
        return findings;
    }

    private static void CheckFields(
        JsonElement el,
        string prefix,
        (string Name, Shape Shape)[] fields,
        List<FieldShapeFinding> findings)
    {
        foreach (var (name, shape) in fields)
        {
            if (!el.TryGetProperty(name, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            if (!Fits(v, shape))
            {
                findings.Add(new FieldShapeFinding(
                    prefix + name, name, Raw(v), Kind(v), Describe(shape)));
            }
        }
    }

    private static bool Fits(JsonElement v, Shape shape) => shape switch
    {
        Shape.Text => v.ValueKind == JsonValueKind.String,
        Shape.Flag => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
        Shape.Whole => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && d == Math.Truncate(d)
                       && d >= int.MinValue && d <= int.MaxValue,
        Shape.NumberOrText => v.ValueKind == JsonValueKind.Number
                              || (v.ValueKind == JsonValueKind.String && double.TryParse(
                                  (v.GetString() ?? string.Empty).Trim(),
                                  System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture,
                                  out _)),
        _ => true
    };

    private static string Describe(Shape shape) => shape switch
    {
        Shape.Text => "a string",
        Shape.Flag => "true/false",
        Shape.Whole => "a whole number",
        Shape.NumberOrText => "a number or a numeric string",
        _ => "a documented shape"
    };

    private static string Kind(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Object => "Object",
        JsonValueKind.Array => "Array",
        JsonValueKind.String => "String",
        JsonValueKind.Number => "Number",
        JsonValueKind.True => "Boolean",
        JsonValueKind.False => "Boolean",
        JsonValueKind.Null => "Null",
        _ => v.ValueKind.ToString()
    };

    private static string Raw(JsonElement v)
    {
        string raw = v.GetRawText();
        return raw.Length > 40 ? raw[..40] + "…" : raw;
    }

    private static string Text(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>
/// F-M531: one field that arrived in a shape its documentation does not allow — kept in parts
/// (attribute, received value, documented shape) so the report can state exactly that, without a
/// story around it.
/// </summary>
/// <param name="Path">Position in the answer, e.g. <c>framerate</c> or <c>unpack_files[0].season</c>.</param>
/// <param name="Attribute">Attribute name as sent.</param>
/// <param name="Received">The value as it arrived (shortened).</param>
/// <param name="Kind">JSON kind that arrived.</param>
/// <param name="Documented">The shape the documentation promises.</param>
public sealed record FieldShapeFinding(
    string Path,
    string Attribute,
    string Received,
    string Kind,
    string Documented);
