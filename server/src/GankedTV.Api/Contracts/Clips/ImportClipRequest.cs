using System.ComponentModel.DataAnnotations;
using GankedTV.Api.Validation;

namespace GankedTV.Api.Contracts.Clips;

// Wire shape for POST /clips/import. Url is required; Title/Description/GameId/Visibility/Tags
// are optional — when omitted, sensible defaults apply (title = placeholder filled later by
// the extractor, visibility = public, no game, no tags).
public sealed record ImportClipRequest(
    [property: Required]
    [property: Url]
    [property: StringLength(2048, MinimumLength = 1)]
    string? Url,
    [property: StringLength(ClipValidationLimits.MaxTitleLength)]
    string? Title,
    [property: StringLength(ClipValidationLimits.MaxDescriptionLength)]
    string? Description,
    int? GameId,
    string? Visibility,
    List<string>? Tags);

public sealed record ImportClipResponse(Guid Id, string Status);

// Wire shape for POST /clips/import/preview — same allow-list + URL validation as a full
// submit, but no clip row is created. Returns title + duration so the wizard can gate
// "Continue" before the user fills in step 2.
public sealed record PreviewImportRequest(
    [property: System.ComponentModel.DataAnnotations.Required]
    [property: System.ComponentModel.DataAnnotations.Url]
    [property: System.ComponentModel.DataAnnotations.StringLength(2048, MinimumLength = 1)]
    string? Url);

public sealed record PreviewImportResponse(
    string? Title,
    int? DurationSecs,
    int? Width,
    int? Height,
    string? ThumbnailUrl,
    int MaxClipDurationSecs);
