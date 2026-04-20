using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Data.Entities;

namespace GankedTV.Api.Contracts.Users;

public static class UserMappings
{
    public static UserProfileResponse ToProfile(this User user, IReadOnlyList<ClipFeedItem> clips) =>
        new(user.Id, user.Username, user.Bio, user.AvatarUrl, user.CreatedAt, clips);

    public static MeResponse ToMe(this User user) =>
        new(user.Id, user.Username, user.Email, user.Bio, user.AvatarUrl, user.CreatedAt);
}
