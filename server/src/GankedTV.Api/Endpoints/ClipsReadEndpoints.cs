using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Data;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Endpoints;

public static class ClipsReadEndpoints
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;
    private static readonly TimeSpan VideoUrlLifetime = TimeSpan.FromHours(1);

    public static IEndpointRouteBuilder MapClipsReadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/clips");
        group.MapGet("/feed", GetFeed);
        group.MapGet("/{id:guid}", GetDetail);
        return app;
    }

    private static async Task<IResult> GetFeed(
        string? cursor,
        int? limit,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        var clampedLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        // Invalid cursor values silently fall back to "no cursor" rather than 400-ing; the
        // client's next-page fetch shouldn't be broken by a corrupted query string.
        DateTimeOffset? cursorValue = null;
        if (!string.IsNullOrWhiteSpace(cursor)
            && DateTimeOffset.TryParse(cursor, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
        {
            cursorValue = parsed;
        }

        var query = db.Clips.AsNoTracking()
            .Where(c => c.Visibility == "public" && c.Status == "ready");
        if (cursorValue is DateTimeOffset cv)
        {
            query = query.Where(c => c.CreatedAt < cv);
        }

        // Fetch limit+1 so we can detect whether another page exists without a second round trip.
        var rows = await query
            .OrderByDescending(c => c.CreatedAt)
            .Include(c => c.User)
            .Take(clampedLimit + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > clampedLimit;
        var page = hasMore ? rows.GetRange(0, clampedLimit) : rows;

        var likedIds = await LoadLikedClipIdsAsync(db, principal, page.Select(c => c.Id), ct);

        var items = page.Select(c => c.ToFeedItem(likedIds.Contains(c.Id))).ToList();
        var nextCursor = hasMore ? page[^1].CreatedAt.ToString("O", CultureInfo.InvariantCulture) : null;

        return Results.Ok(new ClipFeedResponse(items, nextCursor));
    }

    private static async Task<IResult> GetDetail(
        Guid id,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<MinioOptions> minio,
        CancellationToken ct)
    {
        var clip = await db.Clips.AsNoTracking()
            .Include(c => c.User)
            .FirstOrDefaultAsync(
                c => c.Id == id && c.Visibility == "public" && c.Status == "ready",
                ct);

        if (clip is null)
        {
            return Results.NotFound(new { error = "not_found" });
        }

        var expiresAt = DateTimeOffset.UtcNow.Add(VideoUrlLifetime);
        var videoUrl = storage.GetPresignedGetUrl(minio.Value.ClipsBucket, clip.VideoKey, VideoUrlLifetime);

        var likedByMe = false;
        if (TryGetUserId(principal, out var userId))
        {
            likedByMe = await db.Likes.AsNoTracking()
                .AnyAsync(l => l.ClipId == clip.Id && l.UserId == userId, ct);
        }

        return Results.Ok(clip.ToDetail(videoUrl, expiresAt, likedByMe));
    }

    internal static async Task<HashSet<Guid>> LoadLikedClipIdsAsync(
        GankedTvDbContext db,
        ClaimsPrincipal principal,
        IEnumerable<Guid> clipIds,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return [];
        }

        var ids = clipIds as IReadOnlyCollection<Guid> ?? clipIds.ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var liked = await db.Likes.AsNoTracking()
            .Where(l => l.UserId == userId && ids.Contains(l.ClipId))
            .Select(l => l.ClipId)
            .ToListAsync(ct);

        return [.. liked];
    }

    internal static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        userId = default;
        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out userId);
    }
}
