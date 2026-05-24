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
    // filter covers every window with no branching at the query layer.
    public static bool TryParse(string? window, out DateTimeOffset since)
    {
        var now = DateTimeOffset.UtcNow;
        switch (window)
        {
            case Week:
                since = now.AddDays(-7);
                return true;
            case Month:
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
}
