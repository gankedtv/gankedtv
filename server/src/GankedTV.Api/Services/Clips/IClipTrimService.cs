namespace GankedTV.Api.Services.Clips;

public interface IClipTrimService
{
    // Re-cuts a published clip: records the new range and pushes the row back to the start of
    // the media pipeline. Returns null on success, or the reason the request was rejected.
    Task<ClipTrimError?> TrimAsync(Guid userId, Guid clipId, ClipTrimInput trim, CancellationToken ct);
}

public enum ClipTrimError
{
    NotFound,
    Forbidden,
    // Moderation takedown — same rule as PATCH /clips/{id}: a hidden clip is not the owner's
    // to reshape while it is under review.
    Moderated,
    // Only 'ready' clips can be re-cut; anything else is already moving through the pipeline.
    InvalidState,
    InvalidTrim,
    // Re-cutting runs through the compress stage, so it is unavailable when transcoding is off.
    TrimUnavailable,
}
