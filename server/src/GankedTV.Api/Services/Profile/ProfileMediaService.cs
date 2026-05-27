using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Profile;

public sealed class ProfileMediaService : IProfileMediaService
{
    private static readonly TimeSpan UploadUrlExpiry = TimeSpan.FromMinutes(15);

    private readonly GankedTvDbContext _db;
    private readonly IObjectStorageService _storage;
    private readonly ProfileMediaOptions _options;
    private readonly S3Options _s3;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProfileMediaService> _logger;

    public ProfileMediaService(
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<ProfileMediaOptions> options,
        IOptions<S3Options> s3,
        TimeProvider clock,
        ILogger<ProfileMediaService> logger)
    {
        _db = db;
        _storage = storage;
        _options = options.Value;
        _s3 = s3.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ProfileMediaResult<ProfileMediaUploadUrlResult>> GetUploadUrlAsync(
        Guid userId,
        ProfileMediaKind kind,
        string? contentType,
        CancellationToken ct)
    {
        var normalized = NormalizeContentType(contentType);
        if (normalized is null)
        {
            return ProfileMediaResult<ProfileMediaUploadUrlResult>.Fail(ProfileMediaError.UnsupportedContentType);
        }

        var exists = await _db.Users.AsNoTracking().AnyAsync(u => u.Id == userId, ct);
        if (!exists)
        {
            return ProfileMediaResult<ProfileMediaUploadUrlResult>.Fail(ProfileMediaError.NotFound);
        }

        var key = BuildObjectKey(userId, kind, normalized);
        var url = _storage.GetPresignedPutUrl(_s3.AvatarsBucket, key, normalized, UploadUrlExpiry);
        var expiresAt = _clock.GetUtcNow().Add(UploadUrlExpiry);

        return ProfileMediaResult<ProfileMediaUploadUrlResult>.Ok(
            new ProfileMediaUploadUrlResult(url, expiresAt, normalized, key));
    }

    public async Task<ProfileMediaResult<ProfileMediaCompleteResult>> CompleteAsync(
        Guid userId,
        ProfileMediaKind kind,
        string? objectKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(objectKey) || !IsOwnedByUser(objectKey, userId, kind))
        {
            // Reject anything that's not under {userId}/{kind}- so a user can't claim another
            // user's just-uploaded object as their own avatar.
            return ProfileMediaResult<ProfileMediaCompleteResult>.Fail(ProfileMediaError.InvalidObjectKey);
        }

        var user = await _db.Users.FindAsync([userId], ct);
        if (user is null)
        {
            return ProfileMediaResult<ProfileMediaCompleteResult>.Fail(ProfileMediaError.NotFound);
        }

        // A presigned PUT can't enforce size or MIME, so by the time we reject here the object
        // already exists in the bucket. The avatars bucket has no lifecycle expiry — without
        // an inline cleanup a client could repeatedly PUT oversized/wrong-type files (each
        // under a unique ULID key) and orphan them forever.
        var meta = await _storage.GetObjectMetadataAsync(_s3.AvatarsBucket, objectKey, ct);
        if (meta is null || meta.SizeBytes <= 0)
        {
            if (meta is not null)
            {
                await TryDeleteObjectAsync(objectKey, ct);
            }
            return ProfileMediaResult<ProfileMediaCompleteResult>.Fail(ProfileMediaError.ObjectNotUploaded);
        }

        var maxBytes = kind == ProfileMediaKind.Avatar ? _options.MaxAvatarBytes : _options.MaxBannerBytes;
        if (meta.SizeBytes > maxBytes)
        {
            await TryDeleteObjectAsync(objectKey, ct);
            return ProfileMediaResult<ProfileMediaCompleteResult>.Fail(ProfileMediaError.FileTooLarge);
        }

        if (NormalizeContentType(meta.ContentType) is null)
        {
            await TryDeleteObjectAsync(objectKey, ct);
            return ProfileMediaResult<ProfileMediaCompleteResult>.Fail(ProfileMediaError.UnsupportedContentType);
        }

        var publicUrl = S3PublicUrls.BuildUrl(_s3, _s3.AvatarsBucket, objectKey);
        var previousKey = kind == ProfileMediaKind.Avatar ? user.AvatarObjectKey : user.BannerObjectKey;

        if (kind == ProfileMediaKind.Avatar)
        {
            user.AvatarUrl = publicUrl;
            user.AvatarObjectKey = objectKey;
            user.AvatarSource = "upload";
        }
        else
        {
            user.BannerUrl = publicUrl;
            user.BannerObjectKey = objectKey;
        }
        user.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        // Best-effort cleanup of the previous object — a failure here only leaves an orphan
        // that costs storage, never breaks correctness, so we swallow exceptions.
        if (!string.IsNullOrEmpty(previousKey) && previousKey != objectKey)
        {
            await TryDeleteObjectAsync(previousKey, ct);
        }

        return ProfileMediaResult<ProfileMediaCompleteResult>.Ok(
            new ProfileMediaCompleteResult(publicUrl, objectKey, kind == ProfileMediaKind.Avatar ? user.AvatarSource : null));
    }

    public async Task<ProfileMediaResult<ProfileMediaDeleteResult>> DeleteAsync(
        Guid userId,
        ProfileMediaKind kind,
        CancellationToken ct)
    {
        var user = await _db.Users.FindAsync([userId], ct);
        if (user is null)
        {
            return ProfileMediaResult<ProfileMediaDeleteResult>.Fail(ProfileMediaError.NotFound);
        }

        var previousKey = kind == ProfileMediaKind.Avatar ? user.AvatarObjectKey : user.BannerObjectKey;

        if (kind == ProfileMediaKind.Avatar)
        {
            // If we have a stashed OAuth avatar, immediately restore it so the user sees their
            // Discord/Google picture come back without waiting for the next login.
            if (!string.IsNullOrEmpty(user.OAuthAvatarUrl))
            {
                user.AvatarUrl = user.OAuthAvatarUrl;
                user.AvatarSource = user.OAuthAvatarSource;
            }
            else
            {
                user.AvatarUrl = null;
                user.AvatarSource = null;
            }
            user.AvatarObjectKey = null;
        }
        else
        {
            user.BannerUrl = null;
            user.BannerObjectKey = null;
        }
        user.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(previousKey))
        {
            await TryDeleteObjectAsync(previousKey, ct);
        }

        return ProfileMediaResult<ProfileMediaDeleteResult>.Ok(
            new ProfileMediaDeleteResult(
                kind == ProfileMediaKind.Avatar ? user.AvatarUrl : user.BannerUrl,
                null,
                kind == ProfileMediaKind.Avatar ? user.AvatarSource : null));
    }

    private async Task TryDeleteObjectAsync(string key, CancellationToken ct)
    {
        try
        {
            await _storage.DeleteObjectAsync(_s3.AvatarsBucket, key, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete previous profile object '{Key}'.", key);
        }
    }

    private string? NormalizeContentType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var semicolon = raw.IndexOf(';');
        var mediaType = (semicolon >= 0 ? raw[..semicolon] : raw).Trim();
        foreach (var allowed in _options.AllowedContentTypes)
        {
            if (string.Equals(allowed, mediaType, StringComparison.OrdinalIgnoreCase))
            {
                return allowed;
            }
        }
        return null;
    }

    internal static string BuildObjectKey(Guid userId, ProfileMediaKind kind, string contentType)
    {
        // Guid v7 is time-ordered, so listing the bucket per user surfaces newest first and
        // a hex string is URL-safe without further escaping. The extension is informational
        // — the actual served Content-Type comes from S3's metadata on the object.
        var suffix = Guid.CreateVersion7().ToString("N");
        var ext = ExtensionForContentType(contentType);
        var prefix = kind == ProfileMediaKind.Avatar ? "avatar" : "banner";
        return $"{userId}/{prefix}-{suffix}.{ext}";
    }

    private static bool IsOwnedByUser(string objectKey, Guid userId, ProfileMediaKind kind)
    {
        var prefix = kind == ProfileMediaKind.Avatar ? "/avatar-" : "/banner-";
        var expected = $"{userId}{prefix}";
        return objectKey.StartsWith(expected, StringComparison.Ordinal);
    }

    private static string ExtensionForContentType(string contentType) => contentType switch
    {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/webp" => "webp",
        _ => "bin",
    };
}
