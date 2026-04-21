using System.ComponentModel.DataAnnotations;
using GankedTV.Api.Validation;

namespace GankedTV.Api.Contracts.Clips;

public sealed record UpdateClipRequest(
    [property: StringLength(ClipValidationLimits.MaxTitleLength)]
    string? Title,
    [property: StringLength(ClipValidationLimits.MaxDescriptionLength)]
    string? Description,
    int? GameId,
    string? Visibility);
