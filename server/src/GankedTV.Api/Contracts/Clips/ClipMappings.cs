using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Clips;

namespace GankedTV.Api.Contracts.Clips;

public static class ClipMappings
{
    public static AuthorSummary ToAuthorSummary(this User user) =>
        new(user.Id, user.Username, user.AvatarUrl);

    public static CreateClipResponse ToCreateClipResponse(this CreateClipResult result) =>
        new(result.ClipId);

    public static UploadUrlResponse ToUploadUrlResponse(this UploadUrlResult result) =>
        new(result.Url, result.ExpiresAt, result.ContentType);

    public static CompleteClipResponse ToCompleteClipResponse(this CompleteClipResult result) =>
        new(result.ClipId, result.FileSizeBytes);

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
