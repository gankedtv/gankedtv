using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Data.Entities;

namespace GankedTV.Api.Contracts.Users;

public static class UserMappings
{
    public static UserProfileResponse ToProfile(
        this User user,
        IReadOnlyList<ClipFeedItem> clips,
        int followerCount,
        int followingCount,
        bool? followedByMe) =>
        new(user.Id, user.Username, user.Bio, user.AvatarUrl, user.CreatedAt,
            followerCount, followingCount, followedByMe, clips);

    public static MeResponse ToMe(this User user) =>
        new(user.Id, user.Username, user.Email, user.Bio, user.AvatarUrl, user.CreatedAt,
            HasPassword: !string.IsNullOrEmpty(user.PasswordHash));
}
