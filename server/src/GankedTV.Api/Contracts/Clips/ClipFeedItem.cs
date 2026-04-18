namespace GankedTV.Api.Contracts.Clips;

public sealed record ClipFeedItem(
    Guid Id,
    string Title,
    string? Description,
    string? ThumbnailKey,
    short? DurationSecs,
    int ViewCount,
    int LikeCount,
    DateTimeOffset CreatedAt,
    AuthorSummary Author,
    bool LikedByMe);
