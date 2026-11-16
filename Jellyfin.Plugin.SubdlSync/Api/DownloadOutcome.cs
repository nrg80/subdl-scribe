namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// The verdict of ONE download attempt: the status, the byte count and the error name of that
/// attempt, together with the body it produced. Every fetch returns one of these, so no consumer
/// reads state that a later call may already have overwritten.
/// <para>
/// Measured 11.11.2026, why this type exists: the client carried the verdict in three mutable
/// properties that every attempt overwrote. Four separate defects came out of that one shape —
/// a reject quoting the previous candidate's 200 (F-M456), a route that recorded nothing at all
/// (F-M461), a finding that died because a later candidate succeeded (F-M462), and an empty answer
/// that was believed twice (F-M455). The verdict belongs to the call that produced it.
/// </para>
/// </summary>
public sealed record DownloadOutcome
{
    /// <summary>HTTP status of this attempt; 0 means no request was made.</summary>
    public required int Status { get; init; }

    /// <summary>Bytes the attempt carried (archive size, not the unpacked member).</summary>
    public int Bytes { get; init; }

    /// <summary>SubDL error name of this attempt, if it answered with one.</summary>
    public string? Error { get; init; }

    /// <summary>Which route produced this: v2-zip, v2-file, v1, or a cache hit.</summary>
    public required string Route { get; init; }

    /// <summary>True when the body came from the archive cache and cost no quota unit.</summary>
    public bool ServedFromCache { get; init; }

    /// <summary>The usable payload, or null when the attempt produced nothing usable.</summary>
    public byte[]? Content { get; init; }

    /// <summary>Entry name inside the archive that was handed back, when there was one.</summary>
    public string? Member { get; init; }

    /// <summary>True when the attempt delivered something usable.</summary>
    public bool HasContent => Content is { Length: > 0 };

    /// <summary>A genuine empty answer: a real 200 that carried nothing.</summary>
    public bool GenuineEmpty => Status == 200 && Bytes == 0 && string.IsNullOrEmpty(Error);

    /// <summary>True when the candidate was dropped before any request was made.</summary>
    public bool NoRequest => Status == 0;

    /// <summary>The reason as it appears in a log line or a finding.</summary>
    public string Label => Status == 0
        ? "no request made"
        : "HTTP " + Status + (string.IsNullOrEmpty(Error) ? string.Empty : " " + Error);

    /// <summary>An attempt that never happened.</summary>
    /// <param name="route">Route name to record.</param>
    /// <returns>The outcome.</returns>
    public static DownloadOutcome None(string route) => new() { Status = 0, Route = route };
}
