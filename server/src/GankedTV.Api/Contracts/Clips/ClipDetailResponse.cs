using GankedTV.Api.Contracts.Games;
using GankedTV.Api.Contracts.Tags;

namespace GankedTV.Api.Contracts.Clips;

public sealed record ClipDetailResponse(
    Guid Id,
    string ShareCode,
    string Title,
    string? Description,
    string VideoUrl,
    DateTimeOffset VideoUrlExpiresAt,
    // Presigned GET URL for the thumbnail JPEG (Plyr poster). Always set on Ready
    // clips — the worker is the only path to Ready and never marks a clip Ready
    // without a thumbnail key.
    string ThumbnailUrl,
    short? DurationSecs,
    short? Width,
    short? Height,
    int ViewCount,
    int LikeCount,
    DateTimeOffset CreatedAt,
    AuthorSummary Author,
    GameSummary? Game,
    IReadOnlyList<TagSummary> Tags,
    bool LikedByMe,
    string Visibility);
