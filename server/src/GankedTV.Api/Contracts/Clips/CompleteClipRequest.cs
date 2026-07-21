namespace GankedTV.Api.Contracts.Clips;

// Optional body for POST /clips/{id}/complete. Both fields set = trim the clip to
// [start, end] during compression; body omitted (rewynd, API scripts) = keep the whole
// clip. Range validation lives in ClipUploadService.CompleteAsync.
public sealed record CompleteClipRequest(
    double? TrimStartSeconds,
    double? TrimEndSeconds);
