namespace GankedTV.Api.Configuration;

public static class CorsOriginsParser
{
    /// <summary>
    /// True when <paramref name="origin"/> is a well-formed http(s) URL targeting the
    /// local machine — <c>localhost</c>, <c>127.0.0.1</c>, or the IPv6 loopback
    /// <c>[::1]</c>. Used by the dev-mode CORS predicate to auto-allow any local web
    /// origin so worktrees and one-off VITE_PORT overrides don't require keeping
    /// WEB_ORIGIN in sync. Production stays strict — this predicate is only consulted
    /// when ASPNETCORE_ENVIRONMENT=Development.
    /// </summary>
    public static bool IsLocalhostOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host == "127.0.0.1"
            || uri.Host == "[::1]";
    }

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
