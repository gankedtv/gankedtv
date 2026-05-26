namespace GankedTV.Api.Services.Profile;

public interface IProfileMediaService
{
    Task<ProfileMediaResult<ProfileMediaUploadUrlResult>> GetUploadUrlAsync(
        Guid userId,
        ProfileMediaKind kind,
        string? contentType,
        CancellationToken ct);

    Task<ProfileMediaResult<ProfileMediaCompleteResult>> CompleteAsync(
        Guid userId,
        ProfileMediaKind kind,
        string? objectKey,
        CancellationToken ct);

    Task<ProfileMediaResult<ProfileMediaDeleteResult>> DeleteAsync(
        Guid userId,
        ProfileMediaKind kind,
        CancellationToken ct);
}
