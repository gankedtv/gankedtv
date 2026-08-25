using System.ComponentModel.DataAnnotations;

namespace GankedTV.Api.Contracts.Clips;

// Body for POST /clips/{id}/trim — a re-cut of an already-published clip. Both offsets are
// seconds into the CURRENT master (the video the owner just scrubbed), not the raw upload,
// which the compress stage deleted at publish time. Range validation lives in ClipEditService.
//
// Kept as its own required-both-offsets shape after /edit generalized it, so shipped web and
// rewynd builds keep working unchanged. New callers should use EditClipRequest.
public sealed record TrimClipRequest(
    [property: Required] double? TrimStartSeconds,
    [property: Required] double? TrimEndSeconds);

// Body for POST /clips/{id}/edit — a re-cut and/or re-crop of a published clip. Every field is
// optional but at least one operation is required (an empty body would burn a re-encode for no
// change). Both operations ride the SAME compress re-encode, so combining them costs one
// generation of quality loss rather than two.
//
// Crop offsets are NORMALIZED 0..1 fractions of the CURRENT master's frame, never pixels: the
// master is rescaled by the height cap on every edit generation, so a pixel rect would mean
// something different after each one.
public sealed record EditClipRequest(
    double? TrimStartSeconds,
    double? TrimEndSeconds,
    double? CropX,
    double? CropY,
    double? CropWidth,
    double? CropHeight);

// The clip has re-entered the pipeline. Clients poll GET /clips/{id}/status until it flips back
// to 'ready' (or 'failed') — same contract as POST /clips/import. Shared by /trim and /edit so
// the forwarder's response shape is identical to what it always was.
public sealed record TrimClipResponse(Guid Id, string Status);
