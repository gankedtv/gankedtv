using System.ComponentModel.DataAnnotations;
using GankedTV.Api.Validation;

namespace GankedTV.Api.Contracts.Clips;

public sealed record CreateClipRequest(
    [property: Required]
    [property: StringLength(ClipValidationLimits.MaxTitleLength, MinimumLength = 1)]
    string? Title,
    [property: StringLength(ClipValidationLimits.MaxDescriptionLength)]
    string? Description,
    int? GameId,
    string? Visibility,
    // Optional. Omitted = no tags; otherwise each entry is normalized + get-or-created
    // server-side. Validation (max 5, char/length rules) lives in TagsResolver so POST
    // and PATCH share one code path. Null is treated identically to an empty list.
    List<string>? Tags);
