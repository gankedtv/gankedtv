using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.Maintenance;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Endpoints;

public static class ClipsMutateEndpoints
{
    private static readonly string[] AllowedVisibilities = ["public", "unlisted"];
    private static readonly TimeSpan VideoUrlLifetime = TimeSpan.FromHours(1);
    private static readonly string LogCategory = typeof(ClipsMutateEndpoints).FullName!;

    public static IEndpointRouteBuilder MapClipsMutateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/clips").RequireAuthorization();
        group.MapPatch("/{id:guid}", PatchClip).WithValidation<UpdateClipRequest>();
        group.MapDelete("/{id:guid}", DeleteClip);
        return app;
    }

    private static async Task<IResult> PatchClip(
        Guid id,
        // Nullable so a literal JSON `null` body reaches the ValidationEndpointFilter, which
        // shapes it into the same ValidationProblemDetails response as a missing field rather
        // than surfacing as a framework-generated 400 that bypasses our filter.
        [FromBody] UpdateClipRequest? req,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<MinioOptions> minio,
        IOptions<ClipValidationOptions> validation,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        // Defensive: the WithValidation<T> filter already returns 400 for null bodies, so this
        // path is unreachable at runtime. Kept so future removals of the filter (or test
        // harnesses that bypass it) fail closed rather than NRE — same envelope as the filter.
        if (req is null)
        {
            return ProblemResults.InvalidBody();
        }

        var clip = await db.Clips
            .Include(c => c.User)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (clip is null)
        {
            return ProblemResults.NotFound("not_found");
        }

        if (clip.UserId != userId)
        {
            return ProblemResults.Forbidden("forbidden");
        }

        // Only Ready clips are PATCH-able. Non-Ready (draft/processing/failed) rows have
        // no thumbnail and ClipDetailResponse's contract requires a non-null ThumbnailUrl;
        // also matches GET /clips/{id} which already filters to Ready, so the response
        // shape is consistent across read/edit.
        if (clip.Status != ClipStatuses.Ready)
        {
            return ProblemResults.Conflict("invalid_state");
        }

        var limits = validation.Value;

        if (req.Title is not null)
        {
            var trimmed = req.Title.Trim();
            if (trimmed.Length == 0 || trimmed.Length > limits.MaxTitleLength)
            {
                return ProblemResults.BadRequest("invalid_title");
            }
            clip.Title = trimmed;
        }

        if (req.Description is not null)
        {
            // Mirrors the upload-side cap in ClipUploadService; keeping them aligned through the
            // shared ClipValidationOptions prevents PATCH from becoming a backdoor around the
            // storage/readability limits CREATE enforces.
            if (req.Description.Length > limits.MaxDescriptionLength)
            {
                return ProblemResults.BadRequest("invalid_description");
            }
            clip.Description = req.Description;
        }

        if (req.Visibility is not null)
        {
            if (!AllowedVisibilities.Contains(req.Visibility))
            {
                return ProblemResults.BadRequest("invalid_visibility");
            }
            clip.Visibility = req.Visibility;
        }

        if (req.GameId is not null)
        {
            var gameExists = await db.Games.AnyAsync(g => g.Id == req.GameId.Value, ct);
            if (!gameExists)
            {
                return ProblemResults.BadRequest("invalid_game");
            }
            clip.GameId = req.GameId;
        }

        clip.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var expiresAt = DateTimeOffset.UtcNow.Add(VideoUrlLifetime);
        var videoUrl = storage.GetPresignedGetUrl(minio.Value.ClipsBucket, clip.VideoKey, VideoUrlLifetime);
        var thumbnailUrl = ClipsReadEndpoints.BuildThumbnailUrl(
            storage, minio.Value.ThumbnailsBucket, clip.ThumbnailKey);
        var likedByMe = await db.Likes.AsNoTracking()
            .AnyAsync(l => l.ClipId == clip.Id && l.UserId == userId, ct);

        return Results.Ok(clip.ToDetail(videoUrl, expiresAt, thumbnailUrl, likedByMe));
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
            return ProblemResults.Unauthorized("unauthorized");
        }

        var clip = await db.Clips.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (clip is null)
        {
            return ProblemResults.NotFound("not_found");
        }

        if (clip.UserId != userId)
        {
            return ProblemResults.Forbidden("forbidden");
        }

        db.Clips.Remove(clip);
        await db.SaveChangesAsync(ct);

        // S3 cleanup is best-effort: the DB row is already gone, so a cleanup failure must not
        // surface as 500 (that would mislead the client into retrying a non-existent row).
        await ClipBlobCleanup.TryDeleteAsync(
            storage,
            minio.Value,
            clip,
            loggerFactory.CreateLogger(LogCategory),
            ct);

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
