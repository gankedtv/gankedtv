using System.Security.Claims;
using GankedTV.Api.Auth;
using GankedTV.Api.Contracts.Presence;
using GankedTV.Api.Contracts.Users;
using GankedTV.Api.Data;
using GankedTV.Api.Services.Presence;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Endpoints;

public static class PresenceEndpoints
{
    public static IEndpointRouteBuilder MapPresenceEndpoints(this IEndpointRouteBuilder app)
    {
        // Open (no .RequireAuthorization()): anonymous visitors count too. The GET records the
        // caller then reads the summary, so a client's poll doubles as its heartbeat — no separate
        // heartbeat endpoint. Decision: polling (a 30–60s nav poll), not SSE. Per-IP rate limited
        // because it's an anonymous write (see PresenceRateLimiting).
        app.MapGet("/presence/summary", GetSummary)
            .RequireRateLimiting(PresenceRateLimiting.PresencePolicy);
        return app;
    }

    private static async Task<IResult> GetSummary(
        HttpContext http,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        PresenceTracker tracker,
        IOptions<PresenceOptions> options,
        CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            // Disabled → 503 (matches the media-import "feature off" convention). Clients treat any
            // non-2xx as "absent" and render nothing, per the missing-data policy.
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var viewerKey = ResolveViewerKey(principal, http);
        await tracker.RecordAsync(viewerKey, ct);
        var online = await tracker.CountOnlineAsync(ct);

        IReadOnlyList<UserSummary> followsOnline = [];
        var followsOnlineCount = 0;
        if (principal.TryGetUserId(out var me))
        {
            (followsOnline, followsOnlineCount) =
                await GetFollowsOnlineAsync(db, tracker, me, options.Value.FollowsOnlineCap, ct);
        }

        return Results.Ok(new PresenceSummaryResponse(online, followsOnline, followsOnlineCount));
    }

    private static async Task<(IReadOnlyList<UserSummary> Page, int Total)> GetFollowsOnlineAsync(
        GankedTvDbContext db, PresenceTracker tracker, Guid me, int cap, CancellationToken ct)
    {
        var followeeIds = await db.Follows
            .Where(f => f.FollowerId == me)
            .Select(f => f.FolloweeId)
            .ToListAsync(ct);
        if (followeeIds.Count == 0)
        {
            return ([], 0);
        }

        var keys = followeeIds.Select(UserKey).ToList();
        var onlineKeys = await tracker.GetOnlineSubsetAsync(keys, ct);
        if (onlineKeys.Count == 0)
        {
            return ([], 0);
        }

        // Total before the cap so clients can render an honest "+N more" overflow.
        var onlineIds = followeeIds
            .Where(id => onlineKeys.Contains(UserKey(id)))
            .ToList();
        var pageIds = onlineIds.Take(cap).ToList();

        var page = await db.Users
            .Where(u => pageIds.Contains(u.Id))
            .Select(u => new UserSummary(u.Id, u.Username, u.AvatarUrl))
            .ToListAsync(ct);
        return (page, onlineIds.Count);
    }

    private static string ResolveViewerKey(ClaimsPrincipal principal, HttpContext http)
    {
        if (principal.TryGetUserId(out var userId))
        {
            return UserKey(userId);
        }

        // Optional client-supplied id: the (future) web client sends a stable per-browser GUID as
        // ?cid=, which is immune to the proxy-IP collapse below. Length-capped so a hostile client
        // can't inflate the count with unbounded distinct keys.
        var cid = http.Request.Query["cid"].ToString();
        if (!string.IsNullOrEmpty(cid) && cid.Length <= 64)
        {
            return $"a:{cid}";
        }

        // Fallback. NOTE: with no UseForwardedHeaders wired, behind a reverse proxy every anonymous
        // visitor collapses to the proxy IP and under-counts. The cid path is the accurate route.
        return $"ip:{http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }

    private static string UserKey(Guid userId) => $"u:{userId}";
}
