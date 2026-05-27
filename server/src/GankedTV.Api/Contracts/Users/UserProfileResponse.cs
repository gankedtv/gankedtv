using GankedTV.Api.Contracts.Clips;

namespace GankedTV.Api.Contracts.Users;

public sealed record UserProfileResponse(
    Guid Id,
    string Username,
    string? Bio,
    string? AvatarUrl,
    string? BannerUrl,
    string? AccentColor,
    SocialLinksDto? SocialLinks,
    DateTimeOffset CreatedAt,
    int FollowerCount,
    int FollowingCount,
    bool? FollowedByMe,
    IReadOnlyList<ClipFeedItem> Clips);
