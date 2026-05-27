namespace GankedTV.Api.Services.Profile;

public enum ProfileMediaError
{
    NotFound,
    UnsupportedContentType,
    ObjectNotUploaded,
    FileTooLarge,
    InvalidObjectKey,
}

public enum ProfileMediaKind
{
    Avatar,
    Banner,
}

public sealed record ProfileMediaResult<T>(bool IsSuccess, T? Value, ProfileMediaError? Error)
{
    public static ProfileMediaResult<T> Ok(T value) => new(true, value, null);
    public static ProfileMediaResult<T> Fail(ProfileMediaError error) => new(false, default, error);
}

public sealed record ProfileMediaUploadUrlResult(string Url, DateTimeOffset ExpiresAt, string ContentType, string ObjectKey);

public sealed record ProfileMediaCompleteResult(string Url, string ObjectKey, string? AvatarSource);

public sealed record ProfileMediaDeleteResult(string? Url, string? ObjectKey, string? AvatarSource);
