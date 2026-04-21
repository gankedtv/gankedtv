namespace GankedTV.Api.Configuration;

public static class CorsOriginsParser
{
    /// <summary>
    /// Parses a comma-separated <c>CORS_ORIGINS</c> value and guarantees that
    /// <paramref name="alwaysInclude"/> (typically <c>WEB_ORIGIN</c>) is in the result,
    /// since OAuth callback redirects land on it and the browser's subsequent XHR back
    /// to the API must pass CORS regardless of the operator's list.
    /// </summary>
    public static string[] Parse(string? raw, string alwaysInclude)
    {
        if (string.IsNullOrWhiteSpace(alwaysInclude))
        {
            throw new ArgumentException("alwaysInclude origin must be a non-empty string.", nameof(alwaysInclude));
        }

        var parts = string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Preserve order (caller-specified first, then the always-include), de-dupe by ordinal
        // string equality. Case-sensitive match is fine — WithOrigins treats origins as opaque
        // strings and browsers send the scheme+host+port verbatim.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(parts.Length + 1);
        foreach (var part in parts)
        {
            if (seen.Add(part)) result.Add(part);
        }
        if (seen.Add(alwaysInclude)) result.Add(alwaysInclude);

        return [.. result];
    }
}
