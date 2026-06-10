using System.Security.Claims;
using GankedTV.Api.Auth;
using GankedTV.Api.Contracts.Users;
using GankedTV.Api.Data;
using GankedTV.Api.Notifications;
using GankedTV.Api.Pagination;
using GankedTV.Api.Problems;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Endpoints;

public static class FollowsEndpoints
{
    private const int FollowListDefaultLimit = 20;
    private const int FollowListMaxLimit = 100;

    public static IEndpointRouteBuilder MapFollowsEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/users").RequireAuthorization();
        auth.MapPost("/{username}/follow", Follow);
        auth.MapDelete("/{username}/follow", Unfollow);

        var open = app.MapGroup("/users");
        open.MapGet("/{username}/followers", ListFollowers);
        open.MapGet("/{username}/following", ListFollowing);
        return app;
    }

    private static async Task<IResult> Follow(
        string username,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        INotificationService notifications,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var followerId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        var target = await UsersEndpoints.FindByUsernameAsync(db, username, ct);
        if (target is null)
        {
            return ProblemResults.NotFound("not_found");
        }

        if (target.Id == followerId)
        {
            return ProblemResults.BadRequest("self_follow", "Cannot follow yourself.");
        }

        // Wrap insert + RecordAsync so a notification failure rolls the follow back —
        // INotificationService promises to enlist in the caller's transaction, and there's
        // no retry path (dedup is `inserted == 1`), so an event without its notification
        // would be lost forever.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // ON CONFLICT DO NOTHING collapses both sequential double-clicks and concurrent
        // requests from the same follower into a single row — same pattern as Like.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO follows (follower_id, followee_id) VALUES ({followerId}, {target.Id}) ON CONFLICT DO NOTHING",
            ct);

        if (inserted == 1)
        {
            // Notify the followee. Re-following after an unfollow still produces a new
            // notification — mirrors the like / re-like semantics elsewhere.
            await notifications.RecordAsync(
                target.Id, followerId, NotificationTypes.Follow, null, null, ct);
        }

        await tx.CommitAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> Unfollow(
        string username,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var followerId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        var target = await UsersEndpoints.FindByUsernameAsync(db, username, ct);
        if (target is null)
        {
            return ProblemResults.NotFound("not_found");
        }

        // Idempotent: a DELETE that matches zero rows still returns 204. No transactional
        // counter to maintain (counts are computed on read), so a single set-based DELETE
        // suffices.
        await db.Follows
            .Where(f => f.FollowerId == followerId && f.FolloweeId == target.Id)
            .ExecuteDeleteAsync(ct);

        return Results.NoContent();
    }

    // ListFollowers and ListFollowing are intentionally near-duplicates rather than
    // a shared helper parameterised over which side of the row to project: the two
    // queries differ in which column they filter on and which navigation they
    // Include, and folding those behind expression parameters would obscure what
    // each endpoint actually emits. Only the keyset predicate/page slicing is
    // shared (KeysetPagination), which takes the keyset Guid as a selector.

    private static async Task<IResult> ListFollowers(
        string username, string? cursor, int? limit,
        GankedTvDbContext db, CancellationToken ct)
    {
        var target = await UsersEndpoints.FindByUsernameAsync(db, username, ct);
        if (target is null) return ProblemResults.NotFound("not_found");

        var clampedLimit = Math.Clamp(limit ?? FollowListDefaultLimit, 1, FollowListMaxLimit);
        var hasCursor = KeysetCursor.TryParse(cursor, out var cAt, out var cId);

        // Followers of target: rows where FolloweeId == target.Id; pagination keyset is
        // (CreatedAt, FollowerId) — the "other side" of the row, matching what the
        // projected UserSummary identifies.
        var query = db.Follows.AsNoTracking().Where(f => f.FolloweeId == target.Id);
        if (hasCursor)
        {
            query = query.WhereKeysetBefore(f => f.CreatedAt, f => f.FollowerId, cAt, cId);
        }

        var rows = await query
            .OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.FollowerId)
            .Include(f => f.Follower)
            .Take(clampedLimit + 1)
            .ToListAsync(ct);

        var (page, nextCursor) = KeysetPagination.TakePage(rows, clampedLimit, f => f.CreatedAt, f => f.FollowerId);
        var items = page
            .Select(f => new UserSummary(f.Follower.Id, f.Follower.Username, f.Follower.AvatarUrl))
            .ToList();

        return Results.Ok(new UserSummaryPage(items, nextCursor));
    }

    private static async Task<IResult> ListFollowing(
        string username, string? cursor, int? limit,
        GankedTvDbContext db, CancellationToken ct)
    {
        var target = await UsersEndpoints.FindByUsernameAsync(db, username, ct);
        if (target is null) return ProblemResults.NotFound("not_found");

        var clampedLimit = Math.Clamp(limit ?? FollowListDefaultLimit, 1, FollowListMaxLimit);
        var hasCursor = KeysetCursor.TryParse(cursor, out var cAt, out var cId);

        var query = db.Follows.AsNoTracking().Where(f => f.FollowerId == target.Id);
        if (hasCursor)
        {
            query = query.WhereKeysetBefore(f => f.CreatedAt, f => f.FolloweeId, cAt, cId);
        }

        var rows = await query
            .OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.FolloweeId)
            .Include(f => f.Followee)
            .Take(clampedLimit + 1)
            .ToListAsync(ct);

        var (page, nextCursor) = KeysetPagination.TakePage(rows, clampedLimit, f => f.CreatedAt, f => f.FolloweeId);
        var items = page
            .Select(f => new UserSummary(f.Followee.Id, f.Followee.Username, f.Followee.AvatarUrl))
            .ToList();

        return Results.Ok(new UserSummaryPage(items, nextCursor));
    }
}
