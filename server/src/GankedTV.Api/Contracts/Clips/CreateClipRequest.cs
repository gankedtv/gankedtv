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
    string? Visibility);
