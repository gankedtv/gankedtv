using GankedTV.Api.Contracts.Clips;

namespace GankedTV.Api.Contracts.Users;

public sealed record UserProfileResponse(
    Guid Id,
    string Username,
    string? Bio,
    string? AvatarUrl,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ClipFeedItem> Clips);
