using System.Security.Claims;
using GankedTV.Api.Auth;
using GankedTV.Api.Clips;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.Caching;
using GankedTV.Api.Services.Maintenance;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Services.Tags;
using GankedTV.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Endpoints;

public static class ClipsMutateEndpoints
{
    private static readonly TimeSpan VideoUrlLifetime = TimeSpan.FromHours(1);
    private static readonly string LogCategory = typeof(ClipsMutateEndpoints).FullName!;

    public static IEndpointRouteBuilder MapClipsMutateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/clips")
            .RequireAuthorization()
            .RequireRateLimiting(ClipsRateLimiting.ClipsWritePolicy);
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
        ITagsResolver tagsResolver,
        IOptions<S3Options> s3,
        IOptions<ClipValidationOptions> validation,
        IFeedCache feedCache,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
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
            .IncludeFeedRelations()
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
            // Same gate + normalization as create/import: user-settable values only,
            // so "hidden" (moderation-owned) can never arrive through PATCH.
            if (!ClipVisibilities.IsValid(req.Visibility))
            {
                return ProblemResults.BadRequest("invalid_visibility");
            }
            clip.Visibility = ClipVisibilities.Normalize(req.Visibility);
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

        // PATCH semantics: null Tags = "field omitted, leave alone"; any non-null list
        // (including empty) = "replace with this set". The resolver handles normalize +
        // dedupe + max-5 + get-or-create; SetClipTags applies the diff against the loaded
        // ClipTags collection (already Include'd above).
        if (req.Tags is not null)
        {
            var tagsResult = await tagsResolver.ResolveAsync(req.Tags, ct);
            if (!tagsResult.IsSuccess)
            {
                return ProblemResults.BadRequest(TagsResolveProblemCodes.ToCode(tagsResult.Error!.Value));
            }
            tagsResolver.SetClipTags(clip, tagsResult.Tags);
        }

        clip.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // An edit can change anything shown in a feed item (title, thumbnail, game, tags) or move
        // the clip in/out of the public feed (visibility), so drop the cached pages.
        await InvalidateFeedsBestEffortAsync(feedCache, loggerFactory, ct);

        var expiresAt = DateTimeOffset.UtcNow.Add(VideoUrlLifetime);
        var videoUrl = storage.GetPresignedGetUrl(s3.Value.ClipsBucket, clip.VideoKey, VideoUrlLifetime);
        var thumbnailUrl = ClipsReadEndpoints.BuildThumbnailUrl(
            storage, s3.Value.ThumbnailsBucket, clip.ThumbnailKey);
        var likedByMe = await db.Likes.AsNoTracking()
            .AnyAsync(l => l.ClipId == clip.Id && l.UserId == userId, ct);

        return Results.Ok(clip.ToDetail(videoUrl, expiresAt, thumbnailUrl, likedByMe));
    }

    private static async Task<IResult> DeleteClip(
        Guid id,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        ILoggerFactory loggerFactory,
        IFeedCache feedCache,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
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

        // A deleted clip may have been on a cached feed page; drop them so it stops being served.
        await InvalidateFeedsBestEffortAsync(feedCache, loggerFactory, ct);

        // S3 cleanup is best-effort: the DB row is already gone, so a cleanup failure must not
        // surface as 500 (that would mislead the client into retrying a non-existent row).
        await ClipBlobCleanup.TryDeleteAsync(
            storage,
            s3.Value,
            clip,
            loggerFactory.CreateLogger(LogCategory),
            ct);

        return Results.NoContent();
    }

    // The DB write has already committed by the time we invalidate, so a cache failure (e.g. Redis
    // down) must not turn a successful mutation into a 500. Swallow + log; the short TTL self-heals
    // if invalidation is missed. Mirrors the best-effort pattern in MediaJobHostedService.
    private static async Task InvalidateFeedsBestEffortAsync(
        IFeedCache feedCache, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        try
        {
            await feedCache.InvalidateFeedsAsync(ct);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(LogCategory).LogWarning(ex,
                "Feed cache invalidation failed after a clip mutation; entries will expire via TTL.");
        }
    }
}
