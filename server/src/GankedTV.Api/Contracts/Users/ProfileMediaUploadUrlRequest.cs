using System.ComponentModel.DataAnnotations;

namespace GankedTV.Api.Contracts.Users;

public sealed record ProfileMediaUploadUrlRequest(
    [property: Required]
    [property: StringLength(64)]
    string ContentType);

public sealed record ProfileMediaUploadUrlResponse(
    string Url,
    DateTimeOffset ExpiresAt,
    string ContentType,
    string ObjectKey);

public sealed record ProfileMediaCompleteRequest(
    [property: Required]
    [property: StringLength(256)]
    string ObjectKey);

public sealed record ProfileMediaCompleteResponse(
    string Url,
    string ObjectKey,
    string? AvatarSource);

public sealed record ProfileMediaDeleteResponse(
    string? Url,
    string? AvatarSource);
