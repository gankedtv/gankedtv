namespace GankedTV.Api.Services.Clips;

public interface IClipUploadService
{
    Task<ClipResult<CreateClipResult>> CreateAsync(Guid userId, CreateClipInput input, CancellationToken ct);
    Task<ClipResult<UploadUrlResult>> GetUploadUrlAsync(Guid userId, Guid clipId, CancellationToken ct);
    Task<ClipResult<CompleteClipResult>> CompleteAsync(Guid userId, Guid clipId, ClipTrimInput? trim, CancellationToken ct);
}

// Trim range in seconds into the uploaded file, requested by the web trimmer at complete
// time. Values are user input — CompleteAsync validates shape, the thumbnail stage clamps
// against the real probed duration.
public sealed record ClipTrimInput(double StartSecs, double EndSecs);

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
    InvalidTrim,
    // Trim needs the compress stage; rejected when TranscodeEnabled=false (raw uploads
    // are stored as-is and would silently ignore the cut).
    TrimUnavailable,
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
