using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Data;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Endpoints;

public static class ClipsMutateEndpoints
{
    private const int MaxTitleLength = 255;
    private static readonly string[] AllowedVisibilities = ["public", "unlisted"];
    private static readonly TimeSpan VideoUrlLifetime = TimeSpan.FromHours(1);
    private static readonly string LogCategory = typeof(ClipsMutateEndpoints).FullName!;

    public static IEndpointRouteBuilder MapClipsMutateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/clips").RequireAuthorization();
        group.MapPatch("/{id:guid}", PatchClip);
        group.MapDelete("/{id:guid}", DeleteClip);
        return app;
    }

    private static async Task<IResult> PatchClip(
        Guid id,
        [FromBody] UpdateClipRequest? req,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<MinioOptions> minio,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Results.Unauthorized();
        }

        if (req is null)
        {
            return Results.BadRequest(new { error = "invalid_body" });
        }

        var clip = await db.Clips
            .Include(c => c.User)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (clip is null)
        {
            return Results.NotFound(new { error = "not_found" });
        }

        if (clip.UserId != userId)
        {
            return Results.Forbid();
        }

        if (req.Title is not null)
        {
            var trimmed = req.Title.Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxTitleLength)
            {
                return Results.BadRequest(new { error = "invalid_title" });
            }
            clip.Title = trimmed;
        }

        if (req.Description is not null)
        {
            clip.Description = req.Description;
        }

        if (req.Visibility is not null)
        {
            if (!AllowedVisibilities.Contains(req.Visibility))
            {
                return Results.BadRequest(new { error = "invalid_visibility" });
            }
            clip.Visibility = req.Visibility;
        }

        if (req.GameId is not null)
        {
            var gameExists = await db.Games.AnyAsync(g => g.Id == req.GameId.Value, ct);
            if (!gameExists)
            {
                return Results.BadRequest(new { error = "invalid_game" });
            }
            clip.GameId = req.GameId;
        }

        clip.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var expiresAt = DateTimeOffset.UtcNow.Add(VideoUrlLifetime);
        var videoUrl = storage.GetPresignedGetUrl(minio.Value.ClipsBucket, clip.VideoKey, VideoUrlLifetime);
        var likedByMe = await db.Likes.AsNoTracking()
            .AnyAsync(l => l.ClipId == clip.Id && l.UserId == userId, ct);

        return Results.Ok(clip.ToDetail(videoUrl, expiresAt, likedByMe));
    }

    private static async Task<IResult> DeleteClip(
        Guid id,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<MinioOptions> minio,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Results.Unauthorized();
        }

        var clip = await db.Clips.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (clip is null)
        {
            return Results.NotFound(new { error = "not_found" });
        }

        if (clip.UserId != userId)
        {
            return Results.Forbid();
        }

        var videoKey = clip.VideoKey;
        var thumbnailKey = clip.ThumbnailKey;

        db.Clips.Remove(clip);
        await db.SaveChangesAsync(ct);

        // S3 cleanup is best-effort: the DB row is already gone, so a cleanup failure must not
        // surface as 500 (that would mislead the client into retrying a non-existent row).
        // Orphaned S3 objects are cheap; a future reaper can sweep them.
        try
        {
            await storage.DeleteObjectAsync(minio.Value.ClipsBucket, videoKey, ct);
            if (!string.IsNullOrEmpty(thumbnailKey))
            {
                await storage.DeleteObjectAsync(minio.Value.ThumbnailsBucket, thumbnailKey, ct);
            }
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(LogCategory).LogWarning(
                ex,
                "Failed to delete S3 objects for clip {ClipId} (video={VideoKey}, thumb={ThumbKey})",
                id, videoKey, thumbnailKey);
        }

        return Results.NoContent();
    }

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        userId = default;
        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out userId);
    }
}
