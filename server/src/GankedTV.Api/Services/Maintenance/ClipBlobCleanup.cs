using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;

namespace GankedTV.Api.Services.Maintenance;

public static class ClipBlobCleanup
{
    // Best-effort delete of a clip's video and thumbnail objects. Each delete is wrapped
    // independently so a thumbnail failure cannot suppress the video delete (and vice versa).
    // Callers must already have removed (or be about to remove) the DB row — orphaned blobs
    // are cheap; orphaned rows pointing at missing blobs are not.
    public static async Task TryDeleteAsync(
        IObjectStorageService storage,
        MinioOptions buckets,
        Clip clip,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await storage.DeleteObjectAsync(buckets.ClipsBucket, clip.VideoKey, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to delete S3 video object for clip {ClipId} (video={VideoKey})",
                clip.Id, clip.VideoKey);
        }

        if (string.IsNullOrEmpty(clip.ThumbnailKey))
        {
            return;
        }

        try
        {
            await storage.DeleteObjectAsync(buckets.ThumbnailsBucket, clip.ThumbnailKey, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to delete S3 thumbnail object for clip {ClipId} (thumb={ThumbKey})",
                clip.Id, clip.ThumbnailKey);
        }
    }
}
