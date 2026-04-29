using GankedTV.Api.Contracts.Games;

namespace GankedTV.Api.Contracts.Clips;

public sealed record ClipDetailResponse(
    Guid Id,
    string Title,
    string? Description,
    string VideoUrl,
    DateTimeOffset VideoUrlExpiresAt,
    string? ThumbnailKey,
    short? DurationSecs,
    short? Width,
    short? Height,
    int ViewCount,
    int LikeCount,
    DateTimeOffset CreatedAt,
    AuthorSummary Author,
    GameSummary? Game,
    bool LikedByMe);
