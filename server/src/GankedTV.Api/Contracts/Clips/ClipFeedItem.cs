using GankedTV.Api.Contracts.Games;

namespace GankedTV.Api.Contracts.Clips;

public sealed record ClipFeedItem(
    Guid Id,
    string Title,
    string? Description,
    // Presigned GET URL for the thumbnail JPEG. Always set on Ready clips — the worker
    // is the only path to Ready and never marks a clip Ready without a thumbnail key.
    // Clients use this directly as an <img src>; the bucket key is not exposed.
    string ThumbnailUrl,
    short? DurationSecs,
    int ViewCount,
    int LikeCount,
    DateTimeOffset CreatedAt,
    AuthorSummary Author,
    GameSummary? Game,
    bool LikedByMe);
