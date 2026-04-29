using System.Buffers.Text;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Data;
using GankedTV.Api.Problems;
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
        var hasCursor = TryParseCursor(cursor, out var cursorCreatedAt, out var cursorId);

        var query = db.Clips.AsNoTracking()
            .Where(c => c.Visibility == "public" && c.Status == "ready");
        if (hasCursor)
        {
            // Composite (CreatedAt, Id) keyset: two clips sharing the same created_at (bulk imports,
            // seed scripts, same-microsecond uploads) would otherwise cause the second one to be
            // skipped with a strict `CreatedAt < @cursor` filter.
            query = query.Where(c =>
                c.CreatedAt < cursorCreatedAt
                || (c.CreatedAt == cursorCreatedAt && c.Id.CompareTo(cursorId) < 0));
        }

        // Fetch limit+1 so we can detect whether another page exists without a second round trip.
        var rows = await query
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Include(c => c.User)
            .Include(c => c.Game)
            .Take(clampedLimit + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > clampedLimit;
        var page = hasMore ? rows.GetRange(0, clampedLimit) : rows;

        var likedIds = await LoadLikedClipIdsAsync(db, principal, page.Select(c => c.Id), ct);

        var items = page.Select(c => c.ToFeedItem(likedIds.Contains(c.Id))).ToList();
        var nextCursor = hasMore ? BuildCursor(page[^1].CreatedAt, page[^1].Id) : null;

        return Results.Ok(new ClipFeedResponse(items, nextCursor));
    }

    private const char CursorSeparator = '_';

    // Cursor is Base64Url-encoded so the raw token is safe to drop into a query string without
    // client-side escaping. DateTimeOffset.ToString("O") includes `+` (which URL decoders turn
    // into space) and `:` — encoding keeps the token opaque and URL-transport-safe.
    private static string BuildCursor(DateTimeOffset createdAt, Guid id)
    {
        var payload = $"{createdAt.ToString("O", CultureInfo.InvariantCulture)}{CursorSeparator}{id:D}";
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
    }

    private static bool TryParseCursor(string? raw, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(raw);
        }
        catch (FormatException)
        {
            return false;
        }

        var decoded = Encoding.UTF8.GetString(bytes);
        var sep = decoded.IndexOf(CursorSeparator);
        if (sep <= 0 || sep == decoded.Length - 1) return false;

        return DateTimeOffset.TryParse(
                decoded[..sep], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out createdAt)
            && Guid.TryParse(decoded[(sep + 1)..], out id);
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
            .Include(c => c.Game)
            .FirstOrDefaultAsync(
                c => c.Id == id && c.Visibility == "public" && c.Status == "ready",
                ct);

        if (clip is null)
        {
            return ProblemResults.NotFound("not_found");
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
