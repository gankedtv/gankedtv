namespace GankedTV.Api.Contracts.Clips;

public sealed record ClipFeedResponse(IReadOnlyList<ClipFeedItem> Items, string? NextCursor);
