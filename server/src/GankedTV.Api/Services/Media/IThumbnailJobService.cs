namespace GankedTV.Api.Services.Media;

public interface IThumbnailJobService
{
    // Runs the full thumbnail pipeline for one claimed clip: download via presign,
    // ffprobe for metadata, ffmpeg single-frame extraction, upload to the thumbnails
    // bucket. Returns the metadata to persist; does NOT mutate the clip row itself
    // (the caller decides whether to MarkReady or MarkFailed based on the outcome).
    Task<FinalizedMediaJob> ExtractAsync(
        ClaimedMediaJob job,
        string? gameSlug,
        CancellationToken ct);
}
