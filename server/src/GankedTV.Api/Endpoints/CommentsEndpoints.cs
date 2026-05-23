using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GankedTV.Api.Clips;
using GankedTV.Api.Contracts.Comments;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Notifications;
using GankedTV.Api.Pagination;
using GankedTV.Api.Problems;
using GankedTV.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Endpoints;

public static class CommentsEndpoints
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;
    // How many replies ride inline on each top-level comment in the list response; the UI
    // calls GET /comments/{id}/replies to page through the rest ("Show more replies").
    private const int ReplyPreviewCount = 3;

    public static IEndpointRouteBuilder MapCommentsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/clips/{clipId:guid}/comments", ListComments);
        app.MapGet("/comments/{id:guid}/replies", ListReplies);

        app.MapPost("/clips/{clipId:guid}/comments", CreateComment)
            .RequireAuthorization()
            .RequireRateLimiting(ClipsRateLimiting.ClipsWritePolicy)
            .WithValidation<CreateCommentRequest>();

        app.MapDelete("/comments/{id:guid}", DeleteComment)
            .RequireAuthorization()
            .RequireRateLimiting(ClipsRateLimiting.ClipsWritePolicy);

        return app;
    }

    private static async Task<IResult> CreateComment(
        Guid clipId,
        // Nullable so a literal JSON `null` body reaches WithValidation<T>, which shapes it into
        // the same 400 envelope as a missing field rather than a framework-generated 400.
        [FromBody] CreateCommentRequest? req,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        INotificationService notifications,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        // Defensive: WithValidation<T> already 400s a null/empty/whitespace body before we get
        // here ([Required] trims and rejects whitespace; [StringLength] caps at 2000).
        if (req?.Body is null)
        {
            return ProblemResults.InvalidBody();
        }

        var body = req.Body.Trim();

        var clipOwnerId = await db.Clips.AsNoTracking()
            .Where(c => c.Id == clipId)
            .Select(c => (Guid?)c.UserId)
            .FirstOrDefaultAsync(ct);
        if (clipOwnerId is null)
        {
            return ProblemResults.NotFound("not_found");
        }

        if (req.ParentId is { } parentId)
        {
            // The parent must exist, belong to this same clip, and itself be top-level — threads
            // are flat two-level (depth 0 → 1), so replying to a reply is rejected.
            var parent = await db.Comments.AsNoTracking()
                .Where(c => c.Id == parentId)
                .Select(c => new { c.ClipId, c.ParentId })
                .FirstOrDefaultAsync(ct);

            if (parent is null || parent.ClipId != clipId || parent.ParentId is not null)
            {
                return ProblemResults.BadRequest("invalid_parent");
            }
        }

        var comment = new Comment
        {
            ClipId = clipId,
            UserId = userId,
            ParentId = req.ParentId,
            Body = body,
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync(ct);

        // Notify the clip owner — replies-to-replies still notify the clip owner only; notifying
        // the parent commenter is a Phase 4 follow-up. Self-comments are dropped by the service.
        await notifications.RecordAsync(
            clipOwnerId.Value, userId, NotificationTypes.Comment, clipId, comment.Id, ct);

        // Author is needed for the response shape; the authenticated user always exists.
        comment.User = (await db.Users.FindAsync([userId], ct))!;

        return Results.Json(comment.ToItem(), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListComments(
        Guid clipId,
        string? cursor,
        int? limit,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        var clampedLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var hasCursor = KeysetCursor.TryParse(cursor, out var cursorCreatedAt, out var cursorId);

        // Top-level only. A soft-deleted top-level comment is still shown if it has at least one
        // live reply (so the thread doesn't collapse — rendered as `[deleted]`); a deleted
        // comment with no live replies is dropped entirely.
        var query = db.Comments.AsNoTracking()
            .Where(c => c.ClipId == clipId && c.ParentId == null)
            .Where(c => c.DeletedAt == null || c.Replies.Any(r => r.DeletedAt == null));

        if (hasCursor)
        {
            query = query.Where(c =>
                c.CreatedAt < cursorCreatedAt
                || (c.CreatedAt == cursorCreatedAt && c.Id.CompareTo(cursorId) < 0));
        }

        var rows = await query
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Include(c => c.User)
            .Take(clampedLimit + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > clampedLimit;
        var page = hasMore ? rows.GetRange(0, clampedLimit) : rows;

        var repliesByParent = await LoadRepliesForParentsAsync(db, page.Select(c => c.Id), ct);

        var items = page.Select(c =>
        {
            var entry = repliesByParent.GetValueOrDefault(c.Id);
            var previewRows = entry.Preview ?? [];
            var preview = previewRows.Select(r => r.ToItem()).ToList();
            // When the thread has more replies than fit in the inline preview, hand back a
            // cursor anchored on the last preview row. The web client seeds its per-thread
            // cursor from this so "show more replies" doesn't re-fetch the preview rows.
            var repliesNextCursor = entry.Count > previewRows.Count && previewRows.Count > 0
                ? KeysetCursor.Build(previewRows[^1].CreatedAt, previewRows[^1].Id)
                : null;
            return c.ToItem(replyCount: entry.Count, replies: preview, repliesNextCursor: repliesNextCursor);
        }).ToList();

        var nextCursor = hasMore ? KeysetCursor.Build(page[^1].CreatedAt, page[^1].Id) : null;
        return Results.Ok(new CommentListResponse(items, nextCursor));
    }

    private static async Task<IResult> ListReplies(
        Guid id,
        string? cursor,
        int? limit,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        var clampedLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var hasCursor = KeysetCursor.TryParse(cursor, out var cursorCreatedAt, out var cursorId);

        // Replies read oldest-first (chronological thread), so the cursor pages forward in
        // ascending order — the opposite direction from the descending top-level feed.
        var query = db.Comments.AsNoTracking()
            .Where(c => c.ParentId == id && c.DeletedAt == null);

        if (hasCursor)
        {
            query = query.Where(c =>
                c.CreatedAt > cursorCreatedAt
                || (c.CreatedAt == cursorCreatedAt && c.Id.CompareTo(cursorId) > 0));
        }

        var rows = await query
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Include(c => c.User)
            .Take(clampedLimit + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > clampedLimit;
        var page = hasMore ? rows.GetRange(0, clampedLimit) : rows;

        var items = page.Select(c => c.ToItem()).ToList();
        var nextCursor = hasMore ? KeysetCursor.Build(page[^1].CreatedAt, page[^1].Id) : null;
        return Results.Ok(new CommentListResponse(items, nextCursor));
    }

    private static async Task<IResult> DeleteComment(
        Guid id,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        var comment = await db.Comments.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (comment is null)
        {
            return ProblemResults.NotFound("not_found");
        }

        if (comment.UserId != userId)
        {
            return ProblemResults.Forbidden("forbidden");
        }

        // Soft-delete: keep the row so replies stay anchored. Idempotent — deleting an
        // already-deleted comment is a no-op that still returns 204.
        if (comment.DeletedAt is null)
        {
            var now = DateTimeOffset.UtcNow;
            comment.DeletedAt = now;
            comment.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    // For each top-level comment id, returns the total live-reply count plus the oldest few replies
    // for the inline preview — never the full reply set, which is unbounded for a popular comment.
    // Two targeted queries (a grouped count, and a top-N-per-parent slice) keep both bounded
    // regardless of how many replies a thread has accumulated.
    private static async Task<Dictionary<Guid, (int Count, List<Comment> Preview)>> LoadRepliesForParentsAsync(
        GankedTvDbContext db,
        IEnumerable<Guid> parentIds,
        CancellationToken ct)
    {
        var ids = parentIds as IReadOnlyCollection<Guid> ?? parentIds.ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var counts = await db.Comments.AsNoTracking()
            .Where(r => r.ParentId != null && ids.Contains(r.ParentId.Value) && r.DeletedAt == null)
            .GroupBy(r => r.ParentId!.Value)
            .Select(g => new { ParentId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // Top-N-per-parent: keep a reply only if its id is among the oldest ReplyPreviewCount for
        // its parent. The correlated subquery translates to a per-parent lateral slice in Postgres,
        // so we materialize at most ReplyPreviewCount rows per parent instead of every reply.
        var previews = await db.Comments.AsNoTracking()
            .Where(r => r.ParentId != null && ids.Contains(r.ParentId.Value) && r.DeletedAt == null)
            .Where(r => db.Comments
                .Where(x => x.ParentId == r.ParentId && x.DeletedAt == null)
                .OrderBy(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .Select(x => x.Id)
                .Take(ReplyPreviewCount)
                .Contains(r.Id))
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .Include(r => r.User)
            .ToListAsync(ct);

        var previewsByParent = previews
            .GroupBy(r => r.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        return counts.ToDictionary(
            c => c.ParentId,
            c => (c.Count, previewsByParent.GetValueOrDefault(c.ParentId) ?? []));
    }

    // Mirrors the local helper in every other endpoint module (ClipsRead/Mutate, Likes, Auth, Me).
    // Lifting this into a shared auth helper is a worthwhile follow-up across all callers, but
    // this PR keeps the change scoped — comments no longer reach across to ClipsReadEndpoints.
    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        userId = default;
        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out userId);
    }
}
