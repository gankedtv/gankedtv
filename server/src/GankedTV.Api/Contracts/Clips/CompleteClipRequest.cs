namespace GankedTV.Api.Contracts.Clips;

// Optional body for POST /clips/{id}/complete. Every field is optional and the body may be
// omitted entirely (rewynd, API scripts) = publish the whole frame, whole clip. Both trim
// pairs set = cut to [start, end]; all four crop fields set = crop to that rect. Both ride
// the SAME single compress re-encode, so asking for both costs one encode, not two.
//
// Crop offsets are NORMALIZED 0..1 fractions of the uploaded frame, never pixels — the
// request is recorded before anything has been probed. Range validation lives in
// ClipUploadService.CompleteAsync and ClipCropValidation.
public sealed record CompleteClipRequest(
    double? TrimStartSeconds,
    double? TrimEndSeconds,
    double? CropX,
    double? CropY,
    double? CropWidth,
    double? CropHeight);
