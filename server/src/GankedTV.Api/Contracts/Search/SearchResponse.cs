using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Contracts.Games;

namespace GankedTV.Api.Contracts.Search;

// Reuses ClipFeedItem and GameListItem so the navbar dropdown / search view can share
// the same render components as the feed and games pages. No new field shapes.
public sealed record SearchResponse(
    IReadOnlyList<ClipFeedItem> Clips,
    IReadOnlyList<GameListItem> Games);
