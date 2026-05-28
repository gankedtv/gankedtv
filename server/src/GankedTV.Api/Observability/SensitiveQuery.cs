namespace GankedTV.Api.Observability;

/// <summary>
/// Strips credential-bearing params from a request query string before it's attached to a Sentry
/// event. The API serves OAuth callbacks (<c>/auth/{provider}/callback?code=…&amp;state=…</c>), and
/// the SDK captures the query string regardless of <c>SendDefaultPii</c>, so the authorization code
/// would otherwise reach GlitchTip. Mirrors the web client's redaction key list.
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

    /// <summary>
    /// Drops sensitive key/value pairs, preserving order and benign params. Returns the input
    /// unchanged when null/empty, and "" when every pair was sensitive. Handles a leading '?'.
    /// </summary>
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
