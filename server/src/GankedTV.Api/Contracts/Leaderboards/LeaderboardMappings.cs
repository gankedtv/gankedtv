using GankedTV.Api.Contracts.Clips;

namespace GankedTV.Api.Contracts.Leaderboards;

public static class LeaderboardMappings
{
    // `rank` and `windowLikes` are computed by the caller (post-query) because they're
    // a function of result position, not a property of the clip itself. The ClipFeedItem
    // is reused verbatim so the web layer can render the same ClipCard component used
    // on every other feed.
    public static LeaderboardEntry ToEntry(this ClipFeedItem clip, int rank, int windowLikes) =>
        new(rank, windowLikes, clip);
}
