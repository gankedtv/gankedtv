using System.Security.Claims;
using GankedTV.Api.Auth;
using GankedTV.Api.Clips;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Notifications;
using GankedTV.Api.Problems;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Endpoints;

public static class LikesEndpoints
{
    public static IEndpointRouteBuilder MapLikesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/clips")
            .RequireAuthorization()
            .RequireRateLimiting(ClipsRateLimiting.ClipsWritePolicy);
        group.MapPost("/{id:guid}/like", LikeClip);
        group.MapDelete("/{id:guid}/like", UnlikeClip);
        return app;
    }

    private static async Task<IResult> LikeClip(
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

        // Private and hidden clips look nonexistent to everyone but the owner.
        var clip = await db.Clips.AsNoTracking()
            .Where(c => c.Id == id)
            .WhereVisibleTo(userId)
            .Select(c => new { c.UserId })
            .FirstOrDefaultAsync(ct);
        if (clip is null)
        {
            return ProblemResults.NotFound("not_found");
        }
        var clipOwnerId = clip.UserId;

        // Atomic idempotent insert: ON CONFLICT DO NOTHING collapses a duplicate like (sequential
        // double-click OR two concurrent requests from the same user) into a 0-row insert, so we
        // only bump the counter when the row was actually new. `created_at` falls back to the
        // column's DEFAULT now() from the migration.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO likes (user_id, clip_id) VALUES ({userId}, {id}) ON CONFLICT DO NOTHING",
            ct);

        if (inserted == 1)
        {
            await db.Clips.Where(c => c.Id == id)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(c => c.LikeCount, c => c.LikeCount + 1),
                    ct);

            // Record only on the first like (re-likes after an unlike still register because the
            // row was reinserted). The service drops self-likes; the surrounding transaction
            // means a notification failure rolls back the like row too.
            await notifications.RecordAsync(
                clipOwnerId, userId, NotificationTypes.Like, id, null, ct);
        }

        var count = await db.Clips.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => c.LikeCount)
            .FirstAsync(ct);

        await tx.CommitAsync(ct);

        return Results.Ok(new LikeResponse(count, true));
    }

    private static async Task<IResult> UnlikeClip(
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

        // Mirrors LikeClip's gate: a stranger's stale like on a now-private or now-hidden
        // clip stays put until the clip becomes visible to them again.
        var clipVisible = await db.Clips.AsNoTracking()
            .Where(c => c.Id == id)
            .WhereVisibleTo(userId)
            .AnyAsync(ct);
        if (!clipVisible)
        {
            return ProblemResults.NotFound("not_found");
        }

        // Set-based delete: a single SQL DELETE that returns the row count. Under concurrent
        // unlikes from the same user, only one request sees deleted==1, the other sees 0 —
        // neither throws DbUpdateConcurrencyException the way FirstOrDefaultAsync+Remove would.
        var deleted = await db.Likes
            .Where(l => l.UserId == userId && l.ClipId == id)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
        {
            // `LikeCount > 0` guard provides the ≥ 0 clamp required by the acceptance
            // criteria: if the counter is already 0 (data drift, manual row insert, etc.)
            // the decrement is a no-op rather than introducing a negative count.
            await db.Clips.Where(c => c.Id == id && c.LikeCount > 0)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(c => c.LikeCount, c => c.LikeCount - 1),
                    ct);
        }

        var count = await db.Clips.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => c.LikeCount)
            .FirstAsync(ct);

        await tx.CommitAsync(ct);

        return Results.Ok(new LikeResponse(count, false));
    }
}
