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
    // Codec of the stored master ("av1" / "h264" / null). The web player uses it to decide
    // whether to play VideoUrl directly or request a just-in-time H.264 stream
    // (GET /clips/{id}/stream) for devices that can't decode the master.
    string? VideoCodec,
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
    string Visibility,
    // Source URL when this clip was ingested via POST /clips/import (Medal.tv / YouTube).
    // Null for direct uploads. The web detail view renders a "From {host}" attribution
    // badge linking back to the original — credit + reduces friction over reuploads.
    string? ImportSourceUrl);
