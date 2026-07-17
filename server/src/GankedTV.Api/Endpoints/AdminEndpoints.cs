using System.Security.Claims;
using GankedTV.Api.Auth;
using GankedTV.Api.Contracts.Moderation;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.Maintenance;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Services.Moderation;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Endpoints;

// All admin-or-moderator endpoints live under /admin. Routes that require admin specifically
// (ban / unban) call .RequireAuthorization(RolePolicies.Admin) inline; the group-level
// policy is the floor.
public static class AdminEndpoints
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private static readonly string LogCategory = typeof(AdminEndpoints).FullName!;

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin")
            .RequireAuthorization(RolePolicies.Moderator);

        group.MapGet("/reports", ListReports);
        group.MapPost("/reports/{id:guid}/resolve", ResolveReport)
            .WithValidation<ResolveReportRequest>();

        group.MapPost("/clips/{id:guid}/hide", HideClip);
        group.MapPost("/clips/{id:guid}/unhide", UnhideClip);
        group.MapPost("/clips/{id:guid}/game", SetClipGame)
            .WithValidation<SetClipGameRequest>();
        group.MapPost("/clips/media/requeue", RequeueFailedMedia);
        group.MapPost("/comments/{id:guid}/remove", RemoveComment);

        // Ban / unban are admin-only — mods can resolve queues and hide content but can't
        // disable accounts.
        group.MapPost("/users/{id:guid}/ban", BanUser)
            .WithValidation<BanUserRequest>()
            .RequireAuthorization(RolePolicies.Admin);
        group.MapPost("/users/{id:guid}/unban", UnbanUser)
            .RequireAuthorization(RolePolicies.Admin);

        return app;
    }

    private static async Task<IResult> ListReports(
        string? status,
        int? page,
        int? pageSize,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        var normalizedStatus = status ?? ReportStatuses.Open;
        if (!ReportStatuses.IsValid(normalizedStatus))
        {
            return ProblemResults.BadRequest("invalid_status");
        }

        var clampedPage = Math.Max(1, page ?? 1);
        var clampedPageSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        // Compute skip in long arithmetic so a hostile ?page=int.MaxValue request can't
        // wrap around to a negative offset and either crash or silently page from the
        // start. Anything beyond int.MaxValue rows is well past any realistic queue
        // depth — surface as 400 rather than passing a clamped/wrapped value to Skip().
        var skipLong = (long)(clampedPage - 1) * clampedPageSize;
        if (skipLong > int.MaxValue)
        {
            return ProblemResults.BadRequest("invalid_page");
        }
        var skip = (int)skipLong;

        var baseQuery = db.Reports.AsNoTracking()
            .Where(r => r.Status == normalizedStatus);
        var total = await baseQuery.CountAsync(ct);

        // One projection that hydrates the reporter row + the polymorphic target. EF folds
        // this into a single SQL with three LEFT JOINs on the three target tables, so the
        // queue page never fans out into N+1 lookups.
        var items = await baseQuery
            .OrderByDescending(r => r.CreatedAt)
            .Skip(skip)
            .Take(clampedPageSize)
            .Select(r => new ReportListItem(
                r.Id,
                r.TargetType,
                r.TargetId,
                r.Reason,
                r.Note,
                r.Status,
                r.CreatedAt,
                r.ResolvedAt,
                new ReportUserRef(r.Reporter.Id, r.Reporter.Username, r.Reporter.AvatarUrl),
                new ReportTarget(
                    r.TargetType == ReportTargetTypes.Clip
                        ? (from c in db.Clips.AsNoTracking()
                           where c.Id == r.TargetId
                           select new ReportClipTarget(
                               c.Id,
                               c.Title,
                               c.ThumbnailKey,
                               c.Visibility,
                               c.Status,
                               new ReportUserRef(c.User.Id, c.User.Username, c.User.AvatarUrl))).FirstOrDefault()
                        : null,
                    r.TargetType == ReportTargetTypes.Comment
                        ? (from cmt in db.Comments.AsNoTracking()
                           where cmt.Id == r.TargetId
                           select new ReportCommentTarget(
                               cmt.Id,
                               cmt.ClipId,
                               cmt.DeletedAt == null ? cmt.Body : null,
                               cmt.DeletedAt,
                               new ReportUserRef(cmt.User.Id, cmt.User.Username, cmt.User.AvatarUrl))).FirstOrDefault()
                        : null,
                    r.TargetType == ReportTargetTypes.User
                        ? (from u in db.Users.AsNoTracking()
                           where u.Id == r.TargetId
                           select new ReportUserTarget(
                               u.Id,
                               u.Username,
                               u.AvatarUrl,
                               u.BannedAt,
                               u.Role)).FirstOrDefault()
                        : null)))
            .ToListAsync(ct);

        return Results.Ok(new ReportListResponse(items, clampedPage, clampedPageSize, total));
    }

    private static async Task<IResult> ResolveReport(
        Guid id,
        [FromBody] ResolveReportRequest? req,
        ClaimsPrincipal principal,
        IReportService reports,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var modId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }
        if (req?.Outcome is null)
        {
            return ProblemResults.InvalidBody();
        }

        var result = await reports.ResolveAsync(id, modId, req.Outcome, ct);
        if (result.IsSuccess)
        {
            return Results.Ok(new { id = result.Report!.Id, status = result.Report.Status });
        }
        return result.Error switch
        {
            ReportResolveError.NotFound => ProblemResults.NotFound("not_found"),
            ReportResolveError.InvalidOutcome => ProblemResults.BadRequest("invalid_outcome"),
            ReportResolveError.AlreadyResolved => ProblemResults.Conflict("already_resolved"),
            _ => ProblemResults.Internal("unmapped_error"),
        };
    }

    private static async Task<IResult> HideClip(
        Guid id,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IReportService reports,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var modId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }
        var clip = await db.Clips.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (clip is null)
        {
            return ProblemResults.NotFound("not_found");
        }
        if (clip.Visibility != ClipVisibilities.Hidden)
        {
            clip.Visibility = ClipVisibilities.Hidden;
            clip.UpdatedAt = DateTimeOffset.UtcNow;
        }
        // Wrap the mutation + report auto-resolve in one transaction so a partial failure
        // can't leave the queue out of sync with the entity state.
        await SaveAndResolveAsync(db, reports, ReportTargetTypes.Clip, id, modId, ct);

        // Anonymous-read stream-cache has a multi-day TTL, so a hidden clip's JIT HLS stays
        // fetchable by GUID until eviction — purge now (unconditional so a re-hide self-heals a
        // prior failure). None, not ct: the hide already committed, so a disconnect can't abort it.
        await ClipBlobCleanup.TryDeleteStreamCacheAsync(
            storage, s3.Value, id, loggerFactory.CreateLogger(LogCategory), CancellationToken.None);

        return Results.Ok(new { id, visibility = clip.Visibility });
    }

    private static async Task<IResult> UnhideClip(
        Guid id,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        var clip = await db.Clips.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (clip is null)
        {
            return ProblemResults.NotFound("not_found");
        }
        if (clip.Visibility == ClipVisibilities.Hidden)
        {
            clip.Visibility = ClipVisibilities.Public;
            clip.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(new { id, visibility = clip.Visibility });
    }

    // Corrects a clip's game tag from the admin queue when a wrong_game report comes in.
    // Resolves ONLY open wrong_game reports against this clip — abuse reports (spam, hate,
    // etc.) against the same clip stay open so a separate moderation pass can handle them.
    private static async Task<IResult> SetClipGame(
        Guid id,
        [FromBody] SetClipGameRequest? req,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IReportService reports,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var modId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }
        if (req is null)
        {
            return ProblemResults.InvalidBody();
        }

        var clip = await db.Clips.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (clip is null)
        {
            return ProblemResults.NotFound("not_found");
        }

        // Validate the new game exists when non-null. The DB has a FK with Restrict
        // semantics, but the early check gives the SPA a friendlier 400 than a 500 from
        // EF's update.
        if (req.GameId is { } newGameId)
        {
            var gameExists = await db.Games.AsNoTracking().AnyAsync(g => g.Id == newGameId, ct);
            if (!gameExists)
            {
                return ProblemResults.BadRequest("invalid_game");
            }
        }

        if (clip.GameId != req.GameId)
        {
            clip.GameId = req.GameId;
            clip.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // Reason-scoped auto-resolve via the same transaction shape as the other admin
        // actions: SaveChangesAsync (no-op if game was already correct) + close the
        // wrong_game reports + commit.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await reports.ResolveForTargetByReasonAsync(
            ReportTargetTypes.Clip, id, ReportReasons.WrongGame, modId, ct);
        await tx.CommitAsync(ct);

        return Results.Ok(new { id, gameId = clip.GameId });
    }

    // Recovers clips stuck in 'failed' after an infrastructure fault (e.g. the media workers
    // failing TLS verification against storage). Puts them back into the pipeline for another
    // attempt; content rejections (too long / too large) are skipped unless explicitly included.
    private static async Task<IResult> RequeueFailedMedia(
        [FromBody] RequeueFailedMediaRequest? req,
        IClipMediaJobStore store,
        CancellationToken ct)
    {
        var onlyRetryable = req?.IncludeContentFailures != true;
        var requeued = await store.RequeueFailedMediaAsync(req?.ClipId, onlyRetryable, ct);

        // A targeted requeue that matched nothing is a client error, not a silent no-op: the clip
        // doesn't exist, isn't failed, or is a content rejection the caller didn't opt into.
        if (req?.ClipId is not null && requeued == 0)
        {
            return ProblemResults.NotFound("clip_not_requeuable");
        }

        return Results.Ok(new { requeued });
    }

    private static async Task<IResult> RemoveComment(
        Guid id,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IReportService reports,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var modId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }
        var comment = await db.Comments.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (comment is null)
        {
            return ProblemResults.NotFound("not_found");
        }
        if (comment.DeletedAt is null)
        {
            var now = DateTimeOffset.UtcNow;
            comment.DeletedAt = now;
            comment.UpdatedAt = now;
        }
        await SaveAndResolveAsync(db, reports, ReportTargetTypes.Comment, id, modId, ct);
        return Results.Ok(new { id, deleted = true });
    }

    private static async Task<IResult> BanUser(
        Guid id,
        [FromBody] BanUserRequest? req,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IReportService reports,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var modId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }
        if (modId == id)
        {
            // Self-ban would lock the admin out — refuse with a code the SPA can branch on.
            return ProblemResults.BadRequest("self_action");
        }
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return ProblemResults.NotFound("not_found");
        }
        if (user.BannedAt is null)
        {
            user.BannedAt = DateTimeOffset.UtcNow;
            user.BannedReason = string.IsNullOrWhiteSpace(req?.Reason) ? null : req.Reason.Trim();
            user.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await SaveAndResolveAsync(db, reports, ReportTargetTypes.User, id, modId, ct);
        return Results.Ok(new { id, bannedAt = user.BannedAt });
    }

    private static async Task<IResult> UnbanUser(
        Guid id,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return ProblemResults.NotFound("not_found");
        }
        if (user.BannedAt is not null)
        {
            user.BannedAt = null;
            user.BannedReason = null;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(new { id, bannedAt = (DateTimeOffset?)null });
    }

    // Atomic "save the in-memory mutation + close every open report against this target".
    // Used by hide-clip / remove-comment / ban-user so the queue can't end up out of sync
    // with the entity state if the second write fails. SaveChangesAsync is a no-op when
    // the caller didn't mutate (idempotent re-application of an action), in which case
    // this collapses to just the bulk UPDATE inside one transaction.
    private static async Task SaveAndResolveAsync(
        GankedTvDbContext db,
        IReportService reports,
        string targetType,
        Guid targetId,
        Guid modId,
        CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await reports.ResolveForTargetAsync(targetType, targetId, modId, ct);
        await tx.CommitAsync(ct);
    }
}
