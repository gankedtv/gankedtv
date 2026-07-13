using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Contracts.Games;
using GankedTV.Api.Contracts.Users;

namespace GankedTV.Api.Contracts.Search;

// Reuses ClipFeedItem, GameListItem, and UserSummary so the navbar dropdown / search view
// can share the same render components as the feed, games, and follows pages. No new
// field shapes.
public sealed record SearchResponse(
    IReadOnlyList<ClipFeedItem> Clips,
    IReadOnlyList<GameListItem> Games,
    IReadOnlyList<UserSummary> Users);
