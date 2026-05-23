using System.Diagnostics;
using GankedTV.Api.Clips;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Services.Tags;
using GankedTV.Api.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Clips;

public sealed class ClipUploadService : IClipUploadService
{
    private static readonly TimeSpan UploadUrlExpiry = TimeSpan.FromMinutes(15);

    private readonly GankedTvDbContext _db;
    private readonly IObjectStorageService _storage;
    private readonly ITagsResolver _tagsResolver;
    private readonly ClipValidationOptions _validation;
    private readonly S3Options _s3;
    private readonly TimeProvider _clock;

    public ClipUploadService(
        GankedTvDbContext db,
        IObjectStorageService storage,
        ITagsResolver tagsResolver,
        IOptions<ClipValidationOptions> validation,
        IOptions<S3Options> s3,
        TimeProvider clock)
    {
        _db = db;
        _storage = storage;
        _tagsResolver = tagsResolver;
        _validation = validation.Value;
        _s3 = s3.Value;
        _clock = clock;
    }

    // Shared content-type used both for signing the presigned PUT and for validating
    // the uploaded object — keeps the two sides from drifting.
    private string PrimaryContentType =>
        _validation.AllowedContentTypes.FirstOrDefault() ?? "video/mp4";

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

        string? gameSlug = null;
        if (input.GameId is { } gameId)
        {
            gameSlug = await _db.Games.AsNoTracking()
                .Where(g => g.Id == gameId)
                .Select(g => g.Slug)
                .FirstOrDefaultAsync(ct);
            if (gameSlug is null)
            {
                return ClipResult<CreateClipResult>.Fail(ClipUploadError.InvalidGame);
            }
        }

        // Resolve tags BEFORE the clip is staged so a tag validation failure doesn't
        // require rolling back a partial Clip insert. ResolveAsync flushes any newly
        // created tag rows; rolling back the clip insert via SaveChangesAsync below
        // leaves those Tag rows intact but unreferenced — harmless and reachable for
        // the next clip that uses the same slug (the whole point of get-or-create).
        var requestedTags = input.Tags ?? [];
        var tagsResult = await _tagsResolver.ResolveAsync(requestedTags, ct);
        if (!tagsResult.IsSuccess)
        {
            return ClipResult<CreateClipResult>.Fail(MapTagsError(tagsResult.Error!.Value));
        }

        var id = Guid.NewGuid();
        var now = _clock.GetUtcNow();
        var shareCode = await ShareCodeGenerator.GenerateUniqueAsync(_db.Clips, ct);
        var clip = new Clip
        {
            Id = id,
            UserId = userId,
            GameId = input.GameId,
            Title = title,
            Description = string.IsNullOrEmpty(input.Description) ? null : input.Description,
            // Namespace by user id (immutable — username can change via PATCH /auth/me)
            // and by game slug so listing the bucket groups one user's clips per title.
            // No-game uploads omit the slug segment (no `null/` placeholder).
            VideoKey = BuildVideoKey(userId, id, gameSlug),
            ShareCode = shareCode,
            Status = ClipStatuses.Draft,
            Visibility = visibility,
            CreatedAt = now,
            UpdatedAt = now,
        };
        // Same diff-and-attach as PATCH — the clip starts with an empty ClipTags
        // collection, so SetClipTags reduces to "add all resolved tags".
        _tagsResolver.SetClipTags(clip, tagsResult.Tags);

        _db.Clips.Add(clip);
        await _db.SaveChangesAsync(ct);

        return ClipResult<CreateClipResult>.Ok(new CreateClipResult(id));
    }

    // Exhaustive over the defined TagsResolveError cases. The throwing default arm
    // satisfies CS8524 (which can't prove an int-backed enum has no unnamed values)
    // while still failing loudly if a future enum case is added without updating this
    // map — preferable to a silent wrong mapping.
    internal static ClipUploadError MapTagsError(TagsResolveError error) => error switch
    {
        TagsResolveError.TooManyTags => ClipUploadError.TooManyTags,
        TagsResolveError.InvalidTag => ClipUploadError.InvalidTag,
        _ => throw new UnreachableException($"Unmapped TagsResolveError: {error}"),
    };

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

        var contentType = PrimaryContentType;
        var url = _storage.GetPresignedPutUrl(
            _s3.ClipsBucket,
            clip.VideoKey,
            contentType,
            UploadUrlExpiry);
        var expiresAt = _clock.GetUtcNow().Add(UploadUrlExpiry);

        return ClipResult<UploadUrlResult>.Ok(new UploadUrlResult(url, expiresAt, contentType));
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

        var meta = await _storage.GetObjectMetadataAsync(_s3.ClipsBucket, clip.VideoKey, ct);
        if (meta is null || meta.SizeBytes <= 0)
        {
            // Treat a zero-byte object the same as missing — MinIO can accept an empty PUT,
            // and a zero-length clip has no meaningful content to serve later.
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
        // the status check above and both flipping the row to processing.
        // Status transitions to Processing — the media-job worker picks the row up,
        // extracts the thumbnail, and flips it to Ready (or Failed after max attempts).
        var now = _clock.GetUtcNow();
        var rowsAffected = await _db.Clips
            .Where(c => c.Id == clipId && c.Status == ClipStatuses.Draft)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(c => c.Status, ClipStatuses.Processing)
                    .SetProperty(c => c.FileSizeBytes, meta.SizeBytes)
                    .SetProperty(c => c.UpdatedAt, now),
                ct);

        if (rowsAffected == 0)
        {
            return ClipResult<CompleteClipResult>.Fail(ClipUploadError.InvalidState);
        }

        return ClipResult<CompleteClipResult>.Ok(new CompleteClipResult(clipId, meta.SizeBytes));
    }

    internal static string BuildVideoKey(Guid userId, Guid clipId, string? gameSlug) =>
        gameSlug is { Length: > 0 }
            ? $"{userId}/{gameSlug}/{clipId}.mp4"
            : $"{userId}/{clipId}.mp4";

    // Mirrors BuildVideoKey so the thumbnail and video for a given clip live at parallel
    // paths in their respective buckets — keeps manual inspection / GDPR purges simple.
    internal static string BuildThumbnailKey(Guid userId, Guid clipId, string? gameSlug) =>
        gameSlug is { Length: > 0 }
            ? $"{userId}/{gameSlug}/{clipId}.jpg"
            : $"{userId}/{clipId}.jpg";

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
