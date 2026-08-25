using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Media;

namespace GankedTV.Api.Services.Clips;

public interface IClipUploadService
{
    Task<ClipResult<CreateClipResult>> CreateAsync(Guid userId, CreateClipInput input, CancellationToken ct);
    Task<ClipResult<UploadUrlResult>> GetUploadUrlAsync(Guid userId, Guid clipId, CancellationToken ct);
    Task<ClipResult<CompleteClipResult>> CompleteAsync(Guid userId, Guid clipId, ClipEdits edits, CancellationToken ct);
}

// Trim range in seconds into the uploaded file, requested by the web trimmer at complete
// time. Values are user input — CompleteAsync validates shape, the thumbnail stage clamps
// against the real probed duration.
public sealed record ClipTrimInput(double StartSecs, double EndSecs);

// The edit operations a caller can attach to an upload or a published clip: a cut, a crop, or
// both. Both ride the SAME single compress re-encode, so asking for both costs one generation
// of quality loss rather than two. `None` is the body-less /complete contract rewynd relies on.
public sealed record ClipEdits(ClipTrimInput? Trim = null, CropRect? Crop = null)
{
    public static readonly ClipEdits None = new();

    public bool HasAny => Trim is not null || Crop is not null;
}

// UploadSource is decided by the endpoint from the request's auth scheme (ApiKey → 'api',
// JWT → 'web') — never taken from the request body, so clients can't claim a badge.
public sealed record CreateClipInput(
    string? Title,
    string? Description,
    int? GameId,
    string? Visibility,
    IReadOnlyList<string>? Tags,
    string UploadSource = ClipUploadSources.Web);

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
    InvalidCrop,
    // Crop rides the same compress re-encode as the trim, so it needs TranscodeEnabled too —
    // plus its own MediaJobs.CropEnabled kill switch.
    CropUnavailable,
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
