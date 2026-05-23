using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GankedTV.Api.Clips;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GankedTV.Api.Endpoints;

public static class ClipsViewEndpoints
{
    // Window inside which repeat POST /view from the same (clip, viewer) is collapsed to
    // a single counted view. Sliding so a sustained-watch session doesn't suddenly count
    // a second view 30 minutes after the first frame plays.
    internal static readonly TimeSpan ViewDedupWindow = TimeSpan.FromMinutes(30);

    public static IEndpointRouteBuilder MapClipsViewEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous-friendly: no .RequireAuthorization(). Rate limit is per-IP so a single
        // host can't run a view-pump regardless of whether they're logged in.
        var group = app.MapGroup("/clips")
            .RequireRateLimiting(ClipsRateLimiting.ClipsViewPolicy);
        group.MapPost("/{id:guid}/view", RecordView);
        return app;
    }

    private static async Task<IResult> RecordView(
        Guid id,
        HttpContext http,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IMemoryCache cache,
        CancellationToken ct)
    {
        // Issue spec: every outcome (success, dedup, not-found, non-public) returns 204 as a
        // silent no-op. Clients fire-and-forget — they neither need nor act on the result.
        var viewerKey = ResolveViewerKey(principal, http);
        var cacheKey = $"view:{id}:{viewerKey}";
        if (cache.TryGetValue(cacheKey, out _))
        {
            return Results.NoContent();
        }

        // Wrap both writes in one tx so the denormalised `clips.view_count` counter and the
        // `clip_views` event row can't drift on a partial failure — every counted view has
        // exactly one event row backing it (and vice versa).
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var rows = await db.Clips
            .Where(c => c.Id == id && c.Visibility == "public" && c.Status == "ready")
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ViewCount, c => c.ViewCount + 1), ct);

        if (rows == 0)
        {
            // Clip is missing, draft, processing, or unlisted — silently no-op.
            return Results.NoContent();
        }

        db.ClipViews.Add(new ClipView { ClipId = id, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Set AFTER the write commits so a transient DB failure doesn't poison the cache and
        // silently swallow the next 30 min of legitimate views — the common failure case we
        // care about. Cost of this ordering: under truly concurrent pings from the same
        // (clip, viewer) arriving within milliseconds, both can pass TryGetValue above before
        // either commits, producing an at-least-once over-count. Bounded by the per-IP rate
        // limit (20/min) and per-(clip, viewer) cache key — acceptable for an engagement
        // metric. If view_count ever needs to be exact, move to a DB-level unique constraint
        // on (clip_id, viewer_key, time-bucket) instead of cache-before-commit.
        cache.Set(cacheKey, true, new MemoryCacheEntryOptions
        {
            SlidingExpiration = ViewDedupWindow,
        });

        return Results.NoContent();
    }

    // Auth wins over IP: the same user moving between networks (mobile → wifi) is still the
    // same viewer for dedup purposes, and shared NAT shouldn't make two logged-in viewers
    // collapse into one.
    private static string ResolveViewerKey(ClaimsPrincipal principal, HttpContext http)
    {
        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(sub))
        {
            return $"u:{sub}";
        }

        return $"ip:{http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }
}
