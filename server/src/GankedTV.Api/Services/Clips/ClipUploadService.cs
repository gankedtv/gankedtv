using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Clips;

public sealed class ClipUploadService : IClipUploadService
{
    private static readonly TimeSpan UploadUrlExpiry = TimeSpan.FromMinutes(15);

    private readonly GankedTvDbContext _db;
    private readonly IObjectStorageService _storage;
    private readonly ClipValidationOptions _validation;
    private readonly MinioOptions _minio;
    private readonly TimeProvider _clock;

    public ClipUploadService(
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<ClipValidationOptions> validation,
        IOptions<MinioOptions> minio,
        TimeProvider clock)
    {
        _db = db;
        _storage = storage;
        _validation = validation.Value;
        _minio = minio.Value;
        _clock = clock;
    }

    public async Task<ClipResult<CreateClipResult>> CreateAsync(
        Guid userId,
        CreateClipInput input,
        CancellationToken ct)
    {
        var title = input.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > _validation.MaxTitleLength)
        {
            return ClipResult<CreateClipResult>.Fail(ClipUploadError.InvalidTitle);
        }

        if (input.Description is { Length: var descLen } && descLen > _validation.MaxDescriptionLength)
        {
            return ClipResult<CreateClipResult>.Fail(ClipUploadError.InvalidDescription);
        }

        var rawVisibility = input.Visibility ?? ClipVisibilities.Public;
        if (!ClipVisibilities.IsValid(rawVisibility))
        {
            return ClipResult<CreateClipResult>.Fail(ClipUploadError.InvalidVisibility);
        }
        var visibility = ClipVisibilities.Normalize(rawVisibility);

        var id = Guid.NewGuid();
        var now = _clock.GetUtcNow();
        var clip = new Clip
        {
            Id = id,
            UserId = userId,
            GameId = input.GameId,
            Title = title,
            Description = string.IsNullOrEmpty(input.Description) ? null : input.Description,
            VideoKey = $"clips/{id}.mp4",
            Status = ClipStatuses.Draft,
            Visibility = visibility,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Clips.Add(clip);
        await _db.SaveChangesAsync(ct);

        return ClipResult<CreateClipResult>.Ok(new CreateClipResult(id));
    }

    public async Task<ClipResult<UploadUrlResult>> GetUploadUrlAsync(
        Guid userId,
        Guid clipId,
        CancellationToken ct)
    {
        var clip = await _db.Clips
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == clipId && c.UserId == userId, ct);

        if (clip is null)
        {
            return ClipResult<UploadUrlResult>.Fail(ClipUploadError.NotFound);
        }

        if (clip.Status != ClipStatuses.Draft)
        {
            return ClipResult<UploadUrlResult>.Fail(ClipUploadError.InvalidState);
        }

        var url = _storage.GetPresignedPutUrl(
            _minio.ClipsBucket,
            clip.VideoKey,
            "video/mp4",
            UploadUrlExpiry);
        var expiresAt = _clock.GetUtcNow().Add(UploadUrlExpiry);

        return ClipResult<UploadUrlResult>.Ok(new UploadUrlResult(url, expiresAt));
    }

    public async Task<ClipResult<CompleteClipResult>> CompleteAsync(
        Guid userId,
        Guid clipId,
        CancellationToken ct)
    {
        // AsNoTracking: the final mutation goes through ExecuteUpdateAsync, which bypasses
        // the change tracker. Keeping the entity untracked avoids a wasted tracker entry.
        var clip = await _db.Clips
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == clipId && c.UserId == userId, ct);

        if (clip is null)
        {
            return ClipResult<CompleteClipResult>.Fail(ClipUploadError.NotFound);
        }

        if (clip.Status != ClipStatuses.Draft)
        {
            return ClipResult<CompleteClipResult>.Fail(ClipUploadError.InvalidState);
        }

        var meta = await _storage.GetObjectMetadataAsync(_minio.ClipsBucket, clip.VideoKey, ct);
        if (meta is null)
        {
            return ClipResult<CompleteClipResult>.Fail(ClipUploadError.ObjectNotUploaded);
        }

        if (meta.SizeBytes > _validation.MaxUploadSizeBytes)
        {
            return ClipResult<CompleteClipResult>.Fail(ClipUploadError.FileTooLarge);
        }

        if (!IsAllowedContentType(meta.ContentType))
        {
            return ClipResult<CompleteClipResult>.Fail(ClipUploadError.UnsupportedContentType);
        }

        // Conditional atomic update guards against two concurrent completes racing past
        // the status check above and both flipping the row to ready.
        var now = _clock.GetUtcNow();
        var rowsAffected = await _db.Clips
            .Where(c => c.Id == clipId && c.Status == ClipStatuses.Draft)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(c => c.Status, ClipStatuses.Ready)
                    .SetProperty(c => c.FileSizeBytes, meta.SizeBytes)
                    .SetProperty(c => c.UpdatedAt, now),
                ct);

        if (rowsAffected == 0)
        {
            return ClipResult<CompleteClipResult>.Fail(ClipUploadError.InvalidState);
        }

        return ClipResult<CompleteClipResult>.Ok(new CompleteClipResult(clipId, meta.SizeBytes));
    }

    private bool IsAllowedContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var semicolon = contentType.IndexOf(';');
        var mediaType = (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim();
        foreach (var allowed in _validation.AllowedContentTypes)
        {
            if (string.Equals(allowed, mediaType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
