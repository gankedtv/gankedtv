using GankedTV.Api.Auth;
using GankedTV.Api.Contracts.Notifications;
using GankedTV.Api.Data;
using GankedTV.Api.Pagination;
using GankedTV.Api.Problems;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Endpoints;

public static class NotificationsEndpoints
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/me/notifications").RequireAuthorization();
        group.MapGet("/", ListNotifications);
        group.MapGet("/unread-count", UnreadCount);
        group.MapPost("/read", MarkAllRead);
        group.MapPost("/{id:guid}/read", MarkOneRead);
        return app;
    }

    private static async Task<IResult> ListNotifications(
        string? cursor,
        int? limit,
        System.Security.Claims.ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        var clampedLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var hasCursor = KeysetCursor.TryParse(cursor, out var cursorCreatedAt, out var cursorId);

        var query = db.Notifications.AsNoTracking()
            .Where(n => n.RecipientId == userId);

        if (hasCursor)
        {
            query = query.WhereKeysetBefore(n => n.CreatedAt, n => n.Id, cursorCreatedAt, cursorId);
        }

        // Include the actor (always present), and the clip / comment when set so the dropdown
        // can label the row without a follow-up request. An un-included nav silently surfaces as
        // null in NotificationMappings.ToItem, so the Includes here matter for correctness.
        var rows = await query
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Include(n => n.Actor)
            .Include(n => n.Clip)
            .Include(n => n.Comment)
            .Take(clampedLimit + 1)
            .ToListAsync(ct);

        var (page, nextCursor) = KeysetPagination.TakePage(rows, clampedLimit, n => n.CreatedAt, n => n.Id);

        var items = page.Select(n => n.ToItem()).ToList();
        return Results.Ok(new NotificationListResponse(items, nextCursor));
    }

    private static async Task<IResult> UnreadCount(
        System.Security.Claims.ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        // The partial index idx_notifications_unread covers this — it's the hot-path probe
        // the web client polls every 30s, so it must stay O(unread rows for this user).
        var count = await db.Notifications
            .Where(n => n.RecipientId == userId && n.ReadAt == null)
            .CountAsync(ct);

        return Results.Ok(new UnreadCountResponse(count));
    }

    private static async Task<IResult> MarkAllRead(
        System.Security.Claims.ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        var now = DateTimeOffset.UtcNow;
        var marked = await db.Notifications
            .Where(n => n.RecipientId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);

        return Results.Ok(new MarkAllReadResponse(marked));
    }

    private static async Task<IResult> MarkOneRead(
        Guid id,
        System.Security.Claims.ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        var now = DateTimeOffset.UtcNow;
        // Filter on RecipientId so a caller can't reveal or mutate someone else's row. The
        // ReadAt == null guard preserves the original read timestamp when the row is already
        // read — important if read_at is ever used for analytics. On zero rows we cheaply
        // distinguish "already read" (204, idempotent) from "missing / not yours" (404).
        var updated = await db.Notifications
            .Where(n => n.Id == id && n.RecipientId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);

        if (updated == 0)
        {
            var existsForCaller = await db.Notifications
                .AnyAsync(n => n.Id == id && n.RecipientId == userId, ct);
            return existsForCaller ? Results.NoContent() : ProblemResults.NotFound("not_found");
        }

        return Results.NoContent();
    }
}
