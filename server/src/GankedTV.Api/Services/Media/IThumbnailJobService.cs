namespace GankedTV.Api.Services.Media;

// Deterministic content rejection (mirrors ImportSourceRejectedException): the requested
// trim can't be validated against the source, and re-probing the same bytes can't change
// that. The thumbnail worker fails the clip immediately instead of burning MaxAttempts.
public sealed class TrimUnverifiableException(string message) : Exception(message);

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
