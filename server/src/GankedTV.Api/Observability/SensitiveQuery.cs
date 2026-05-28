namespace GankedTV.Api.Observability;

/// <summary>
/// Drops credential-bearing query params from a captured request URL. The API serves OAuth callbacks
/// (<c>?code=…&amp;state=…</c>) whose query string the SDK captures regardless of SendDefaultPii, so
/// the auth code would otherwise reach GlitchTip. Same key list as the web client.
/// </summary>
internal static class SensitiveQuery
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "token",
        "refresh",
        "code",
        "state",
        "access_token",
        "id_token",
    };

    /// <summary>Drops sensitive pairs, keeping order and benign params. Handles a leading '?'.</summary>
    public static string? Redact(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString))
        {
            return queryString;
        }

        var hasLeadingQuestion = queryString[0] == '?';
        var raw = hasLeadingQuestion ? queryString[1..] : queryString;
        if (raw.Length == 0)
        {
            return queryString;
        }

        var kept = new List<string>();
        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = eq >= 0 ? pair[..eq] : pair;
            // Keys are URL-encoded on the wire; decode before matching our (unencoded) list.
            if (!SensitiveKeys.Contains(Uri.UnescapeDataString(key)))
            {
                kept.Add(pair);
            }
        }

        if (kept.Count == 0)
        {
            return "";
        }

        var rebuilt = string.Join('&', kept);
        return hasLeadingQuestion ? "?" + rebuilt : rebuilt;
    }
}
