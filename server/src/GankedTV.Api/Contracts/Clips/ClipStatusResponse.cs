namespace GankedTV.Api.Contracts.Clips;

// Lightweight projection used by GET /clips/{id}/status — enough for the wizard to poll
// for transitions (importing → processing → transcoding → ready/failed) and redirect via
// share code once the clip is feed-visible. On failure, FailureReason carries one of the
// ClipFailureReasons.* codes and the optional Duration / Limit fields let the web layer
// render specific copy ("your clip is X seconds; limit is Y").
public sealed record ClipStatusResponse(
    Guid Id,
    string Status,
    string ShareCode,
    string? FailureReason,
    short? DurationSecs,
    int? MaxClipDurationSecs);
