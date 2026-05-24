using GankedTV.Api.Contracts.Games;
using GankedTV.Api.Contracts.Tags;
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

    public static ImportClipResponse ToImportClipResponse(this ImportClipResult result) =>
        new(result.ClipId, result.Status);

    public static PreviewImportResponse ToPreviewResponse(this ImportClipPreviewResult result) =>
        new(result.Title, result.DurationSecs, result.Width, result.Height, result.ThumbnailUrl, result.MaxClipDurationSecs);

    public static ClipFeedItem ToFeedItem(this Clip clip, string thumbnailUrl, bool likedByMe) =>
        new(
            clip.Id,
            clip.ShareCode,
            clip.Title,
            clip.Description,
            thumbnailUrl,
            clip.DurationSecs,
            clip.ViewCount,
            clip.LikeCount,
            clip.CreatedAt,
            clip.User.ToAuthorSummary(),
            clip.Game?.ToGameSummary(),
            clip.ToTagSummaries(),
            likedByMe);

    public static ClipDetailResponse ToDetail(
        this Clip clip,
        string videoUrl,
        DateTimeOffset videoUrlExpiresAt,
        string thumbnailUrl,
        bool likedByMe) =>
        new(
            clip.Id,
            clip.ShareCode,
            clip.Title,
            clip.Description,
            videoUrl,
            videoUrlExpiresAt,
            clip.VideoCodec,
            thumbnailUrl,
            clip.DurationSecs,
            clip.Width,
            clip.Height,
            clip.ViewCount,
            clip.LikeCount,
            clip.CreatedAt,
            clip.User.ToAuthorSummary(),
            clip.Game?.ToGameSummary(),
            clip.ToTagSummaries(),
            likedByMe,
            clip.Visibility,
            clip.ImportSourceUrl);

    // Tag projection used by both ToFeedItem and ToDetail. Sorted by slug so cards
    // render deterministically regardless of clip_tags insertion order. Callers must
    // Include(c => c.ClipTags).ThenInclude(ct => ct.Tag) on the entity load — an
    // un-included collection silently maps to an empty list, hiding bugs.
    private static IReadOnlyList<TagSummary> ToTagSummaries(this Clip clip) =>
        clip.ClipTags
            .Where(ct => ct.Tag is not null)
            .OrderBy(ct => ct.Tag.Slug, StringComparer.Ordinal)
            .Select(ct => ct.Tag.ToSummary())
            .ToList();
}
