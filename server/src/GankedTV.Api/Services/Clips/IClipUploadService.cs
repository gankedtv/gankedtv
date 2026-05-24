namespace GankedTV.Api.Services.Clips;

public interface IClipUploadService
{
    Task<ClipResult<CreateClipResult>> CreateAsync(Guid userId, CreateClipInput input, CancellationToken ct);
    Task<ClipResult<UploadUrlResult>> GetUploadUrlAsync(Guid userId, Guid clipId, CancellationToken ct);
    Task<ClipResult<CompleteClipResult>> CompleteAsync(Guid userId, Guid clipId, CancellationToken ct);
}

public sealed record CreateClipInput(
    string? Title,
    string? Description,
    int? GameId,
    string? Visibility,
    IReadOnlyList<string>? Tags);

public sealed record CreateClipResult(Guid ClipId);
public sealed record UploadUrlResult(string Url, DateTimeOffset ExpiresAt, string ContentType);
public sealed record CompleteClipResult(Guid ClipId, long FileSizeBytes);

public enum ClipUploadError
{
    InvalidTitle,
    InvalidDescription,
    InvalidVisibility,
    InvalidGame,
    NotFound,
    InvalidState,
    ObjectNotUploaded,
    FileTooLarge,
    UnsupportedContentType,
    TooManyTags,
    InvalidTag,
    // URL-import only (issue #106). Live in the same enum so the import endpoint can reuse
    // the existing MapError table (visibility/game/tag errors are shared with /clips create).
    InvalidUrl,
    UnsupportedHost,
    ImportDisabled,
    SourceUnavailable,
    FetchFailed,
}

public readonly record struct ClipResult<T>(T? Value, ClipUploadError? Error)
{
    public bool IsSuccess => Error is null;

    public static ClipResult<T> Ok(T value) => new(value, null);
    public static ClipResult<T> Fail(ClipUploadError error) => new(default, error);
}
