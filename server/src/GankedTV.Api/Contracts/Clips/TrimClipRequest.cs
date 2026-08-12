using System.ComponentModel.DataAnnotations;

namespace GankedTV.Api.Contracts.Clips;

// Body for POST /clips/{id}/trim — a re-cut of an already-published clip. Both offsets are
// seconds into the CURRENT master (the video the owner just scrubbed), not the raw upload,
// which the compress stage deleted at publish time. Range validation lives in ClipTrimService.
public sealed record TrimClipRequest(
    [property: Required] double? TrimStartSeconds,
    [property: Required] double? TrimEndSeconds);

// The clip has re-entered the pipeline. Clients poll GET /clips/{id}/status until it flips back
// to 'ready' (or 'failed') — same contract as POST /clips/import.
public sealed record TrimClipResponse(Guid Id, string Status);
