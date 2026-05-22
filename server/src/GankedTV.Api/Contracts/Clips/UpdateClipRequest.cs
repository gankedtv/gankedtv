using System.ComponentModel.DataAnnotations;
using GankedTV.Api.Validation;

namespace GankedTV.Api.Contracts.Clips;

public sealed record UpdateClipRequest(
    [property: StringLength(ClipValidationLimits.MaxTitleLength)]
    string? Title,
    [property: StringLength(ClipValidationLimits.MaxDescriptionLength)]
    string? Description,
    int? GameId,
    string? Visibility,
    // PATCH semantics: <c>null</c> = "tags field omitted, leave the existing set alone".
    // A non-null list (including empty) = "replace the current set with this exact list".
    // Caller distinguishes the two states; the JSON binder maps a missing key to null.
    List<string>? Tags);
