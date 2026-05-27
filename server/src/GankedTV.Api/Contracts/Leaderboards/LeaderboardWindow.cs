namespace GankedTV.Api.Contracts.Leaderboards;

// Leaderboards intentionally use coarser windows than trending (week/month/all vs 24h/7d):
// "most liked this hour" is too noisy to rank, and "all-time" is a meaningful question for
// boards but never for trending. Kept separate from TryParseTrendingWindow so the two
// endpoints can evolve their window vocabularies independently.
public static class LeaderboardWindow
{
    public const string Week = "week";
    public const string Month = "month";
    public const string All = "all";
    public const string Default = Week;

    // `since` for `all` is DateTimeOffset.MinValue so the same `l.CreatedAt >= since`
    // filter covers every window with no branching at the query layer. Note the inclusive
    // `>=`: trending uses strict `>` (see TryParseTrendingWindow) — inconsequential at
    // second precision, but called out so a future reader copy-pasting between the two
    // endpoints doesn't assume the boundary semantics are interchangeable.
    public static bool TryParse(string? window, out DateTimeOffset since)
    {
        var now = DateTimeOffset.UtcNow;
        switch (window)
        {
            case Week:
                since = now.AddDays(-7);
                return true;
            case Month:
                // Deliberately a rolling 30 days, not a calendar month — the web label
                // ("This Month") is a simplification. A clip liked 31 days ago drops out
                // of the window even mid-calendar-month. Renaming the key to `30d` would
                // be more honest, but `month` matches the user-facing label.
                since = now.AddDays(-30);
                return true;
            case All:
                since = DateTimeOffset.MinValue;
                return true;
            default:
                since = default;
                return false;
        }
    }

    // Bundles the window-default-and-parse + limit-clamp that every leaderboard handler
    // does up-front. `windowKey` is the resolved string (defaulted when null) so callers
    // can echo it back in the response payload; `since` is the parsed cutoff; `clampedLimit`
    // is the input limit clamped to [1, maxLimit] with the default applied when null.
    // Returns false only when an explicit, non-null window string doesn't match a known
    // value — the caller should turn that into a 400.
    public static bool TryParseRequest(
        string? window,
        int? limit,
        int defaultLimit,
        int maxLimit,
        out string windowKey,
        out DateTimeOffset since,
        out int clampedLimit)
    {
        windowKey = window ?? Default;
        clampedLimit = Math.Clamp(limit ?? defaultLimit, 1, maxLimit);
        return TryParse(windowKey, out since);
    }
}
