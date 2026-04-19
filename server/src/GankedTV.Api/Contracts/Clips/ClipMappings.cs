using GankedTV.Api.Data.Entities;

namespace GankedTV.Api.Contracts.Clips;

public static class ClipMappings
{
    public static AuthorSummary ToAuthorSummary(this User user) =>
        new(user.Id, user.Username, user.AvatarUrl);

    public static ClipFeedItem ToFeedItem(this Clip clip, bool likedByMe) =>
        new(
            clip.Id,
            clip.Title,
            clip.Description,
            clip.ThumbnailKey,
            clip.DurationSecs,
            clip.ViewCount,
            clip.LikeCount,
            clip.CreatedAt,
            clip.User.ToAuthorSummary(),
            likedByMe);

    public static ClipDetailResponse ToDetail(
        this Clip clip,
        string videoUrl,
        DateTimeOffset videoUrlExpiresAt,
        bool likedByMe) =>
        new(
            clip.Id,
            clip.Title,
            clip.Description,
            videoUrl,
            videoUrlExpiresAt,
            clip.ThumbnailKey,
            clip.DurationSecs,
            clip.Width,
            clip.Height,
            clip.ViewCount,
            clip.LikeCount,
            clip.CreatedAt,
            clip.User.ToAuthorSummary(),
            likedByMe);
}
