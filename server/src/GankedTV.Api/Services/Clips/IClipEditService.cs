namespace GankedTV.Api.Services.Clips;

public interface IClipEditService
{
    // Re-cuts and/or re-crops a published clip: records the new operations and pushes the row
    // back to the start of the media pipeline. Returns null on success, or the reason the
    // request was rejected.
    Task<ClipEditError?> EditAsync(Guid userId, Guid clipId, ClipEdits edits, CancellationToken ct);
}

public enum ClipEditError
{
    NotFound,
    Forbidden,
    // Moderation takedown — same rule as PATCH /clips/{id}: a hidden clip is not the owner's
    // to reshape while it is under review.
    Moderated,
    // Only 'ready' clips can be edited; anything else is already moving through the pipeline.
    InvalidState,
    InvalidTrim,
    // Re-cutting runs through the compress stage, so it is unavailable when transcoding is off.
    TrimUnavailable,
    InvalidCrop,
    // Crop needs the compress stage too, plus its own MediaJobs.CropEnabled kill switch.
    CropUnavailable,
    // The body asked for nothing at all. Requeuing a clip through a full re-encode to apply
    // no change would burn a generation of quality for free.
    NoOperations,
}
