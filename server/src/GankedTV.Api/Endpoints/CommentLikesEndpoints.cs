using System.Security.Claims;
using GankedTV.Api.Auth;
using GankedTV.Api.Clips;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Data;
using GankedTV.Api.Notifications;
using GankedTV.Api.Problems;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Endpoints;

/// <summary>
/// Likes on comments and replies. A near-mechanical sibling of <see cref="LikesEndpoints"/> —
/// same transaction shape, same idempotent insert, same clamped decrement — with the visibility
/// gate reached through the comment's clip instead of the clip directly.
/// </summary>
public static class CommentLikesEndpoints
{
    public static IEndpointRouteBuilder MapCommentLikesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/comments")
            .RequireAuthorization()
            .RequireRateLimiting(ClipsRateLimiting.ClipsWritePolicy);
        group.MapPost("/{id:guid}/like", LikeComment);
        group.MapDelete("/{id:guid}/like", UnlikeComment);
        return app;
    }

    private static async Task<IResult> LikeComment(
        Guid id,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        INotificationService notifications,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var target = await LoadLikeableAsync(db, id, userId, ct);
        if (target is null)
        {
            return ProblemResults.NotFound("not_found");
        }

        // Atomic idempotent insert, mirroring clip likes: ON CONFLICT DO NOTHING collapses a
        // double-click (or two concurrent requests from the same user) into a 0-row insert, so
        // the counter only moves when the row was actually new.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO comment_likes (user_id, comment_id) VALUES ({userId}, {id}) ON CONFLICT DO NOTHING",
            ct);

        if (inserted == 1)
        {
            await db.Comments.Where(c => c.Id == id)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(c => c.LikeCount, c => c.LikeCount + 1),
                    ct);

            // ClipId rides along so the notification row deep-links to the clip; the service
            // drops self-likes, and the surrounding transaction means a notification failure
            // rolls the like row back too.
            await notifications.RecordAsync(
                target.AuthorId, userId, NotificationTypes.CommentLike, target.ClipId, id, ct);
        }

        var count = await CurrentCountAsync(db, id, ct);
        await tx.CommitAsync(ct);

        return Results.Ok(new LikeResponse(count, true));
    }

    private static async Task<IResult> UnlikeComment(
        Guid id,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var target = await LoadLikeableAsync(db, id, userId, ct);
        if (target is null)
        {
            return ProblemResults.NotFound("not_found");
        }

        // Set-based delete so concurrent unlikes from the same user resolve to one 1-row and one
        // 0-row result instead of a DbUpdateConcurrencyException.
        var deleted = await db.CommentLikes
            .Where(l => l.UserId == userId && l.CommentId == id)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
        {
            // The `LikeCount > 0` guard is the ≥ 0 clamp: a counter already at zero (data drift,
            // a manually inserted row) must not go negative.
            await db.Comments.Where(c => c.Id == id && c.LikeCount > 0)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(c => c.LikeCount, c => c.LikeCount - 1),
                    ct);
        }

        var count = await CurrentCountAsync(db, id, ct);
        await tx.CommitAsync(ct);

        return Results.Ok(new LikeResponse(count, false));
    }

    /// <summary>
    /// The comment's clip and author, or null when the caller must not be able to like it: the
    /// comment is gone, soft-deleted (it renders as <c>[deleted]</c> — there is nothing to like),
    /// or sits on a clip that is private or hidden from this viewer.
    /// </summary>
    private static async Task<LikeTarget?> LoadLikeableAsync(
        GankedTvDbContext db,
        Guid commentId,
        Guid userId,
        CancellationToken ct)
    {
        var target = await db.Comments.AsNoTracking()
            .Where(c => c.Id == commentId && c.DeletedAt == null)
            .Select(c => new LikeTarget(c.ClipId, c.UserId))
            .FirstOrDefaultAsync(ct);
        if (target is null)
        {
            return null;
        }

        // Visibility is a second query rather than a subquery so it can go through the shared
        // WhereVisibleTo — the one place the private/hidden rule is written down.
        var visible = await db.Clips.AsNoTracking()
            .Where(c => c.Id == target.ClipId)
            .WhereVisibleTo(userId)
            .AnyAsync(ct);

        return visible ? target : null;
    }

    private static Task<int> CurrentCountAsync(GankedTvDbContext db, Guid id, CancellationToken ct) =>
        db.Comments.AsNoTracking().Where(c => c.Id == id).Select(c => c.LikeCount).FirstAsync(ct);

    private sealed record LikeTarget(Guid ClipId, Guid AuthorId);
}
