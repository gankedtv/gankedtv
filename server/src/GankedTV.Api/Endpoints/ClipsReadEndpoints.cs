using System.Buffers.Text;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Linq.Expressions;
using System.Net;
using System.Security.Claims;
using System.Text;
using GankedTV.Api.Auth;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
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
    // Thumbnail URLs ride the same 1-hour signed window as video URLs — keeping the
    // two lifetimes aligned means a feed page that's still fresh enough to play the
    // video still has working poster images.
    private static readonly TimeSpan ThumbnailUrlLifetime = TimeSpan.FromHours(1);

    public static IEndpointRouteBuilder MapClipsReadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/clips");
        group.MapGet("/feed", GetFeed);
        group.MapGet("/{id:guid}", GetDetail);
        app.MapGet("/c/{code:length(6,12)}", GetByShareCode);
        return app;
    }

    private static async Task<IResult> GetFeed(
        string? cursor,
        int? limit,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
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

        var thumbnailsBucket = s3.Value.ThumbnailsBucket;
        var items = page
            .Select(c => c.ToFeedItem(
                BuildThumbnailUrl(storage, thumbnailsBucket, c.ThumbnailKey),
                likedIds.Contains(c.Id)))
            .ToList();
        var nextCursor = hasMore ? BuildCursor(page[^1].CreatedAt, page[^1].Id) : null;

        return Results.Ok(new ClipFeedResponse(items, nextCursor));
    }

    // Public Ready clips always have a thumbnail (the worker is the only path to Ready
    // and never marks Ready without writing ThumbnailKey first). Caller is expected to
    // pass non-null; passing null indicates a corrupted row and we fail loudly.
    internal static string BuildThumbnailUrl(
        IObjectStorageService storage, string bucket, string? thumbnailKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(thumbnailKey);
        return storage.GetPresignedGetUrl(bucket, thumbnailKey, ThumbnailUrlLifetime);
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

    private static Task<IResult> GetDetail(
        Guid id,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct) =>
        ResolveClipByPredicateAsync(
            c => c.Id == id && c.Status == "ready",
            principal, db, storage, s3, ct);

    private static async Task<IResult> GetByShareCode(
        string code,
        HttpRequest request,
        IOptions<OAuthOptions> oauthOptions,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        var result = await LoadClipWithUrlsAsync(
            c => c.ShareCode == code && c.Status == "ready",
            db, storage, s3, ct);

        if (result is null)
            return ProblemResults.NotFound("not_found");

        var (clip, videoUrl, thumbnailUrl) = result.Value;
        var userAgent = request.Headers.UserAgent.ToString();
        var webOrigin = oauthOptions.Value.WebOrigin.TrimEnd('/');

        // Crawler UA wins over Accept negotiation: a bot advertising application/json
        // (rare but possible) still needs the OG HTML so previews render.
        if (IsCrawler(userAgent))
            return Results.Content(BuildOgHtml(clip, videoUrl, thumbnailUrl, webOrigin), "text/html; charset=utf-8");

        if (request.Headers.Accept.ToString().Contains("application/json"))
            return await BuildDetailResultAsync(clip, videoUrl, thumbnailUrl, principal, db, ct);

        return Results.Redirect($"{webOrigin}/c/{code}", permanent: false);
    }

    private static async Task<IResult> BuildDetailResultAsync(
        Clip clip,
        string videoUrl,
        string thumbnailUrl,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        var likedByMe = false;
        if (TryGetUserId(principal, out var userId))
        {
            likedByMe = await db.Likes.AsNoTracking()
                .AnyAsync(l => l.ClipId == clip.Id && l.UserId == userId, ct);
        }
        var expiresAt = DateTimeOffset.UtcNow.Add(VideoUrlLifetime);
        return Results.Ok(clip.ToDetail(videoUrl, expiresAt, thumbnailUrl, likedByMe));
    }

    // Unlisted clips are accessible to anyone with the link or share code — only the
    // feed is gated to public-only. Visibility is enforced at the listing layer, not
    // the detail layer.
    private static async Task<(Clip clip, string videoUrl, string thumbnailUrl)?> LoadClipWithUrlsAsync(
        Expression<Func<Clip, bool>> predicate,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        var clip = await db.Clips.AsNoTracking()
            .Include(c => c.User)
            .Include(c => c.Game)
            .FirstOrDefaultAsync(predicate, ct);

        if (clip is null)
            return null;

        var videoUrl = storage.GetPresignedGetUrl(s3.Value.ClipsBucket, clip.VideoKey, VideoUrlLifetime);
        var thumbnailUrl = BuildThumbnailUrl(storage, s3.Value.ThumbnailsBucket, clip.ThumbnailKey);

        return (clip, videoUrl, thumbnailUrl);
    }

    private static async Task<IResult> ResolveClipByPredicateAsync(
        Expression<Func<Clip, bool>> predicate,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        var result = await LoadClipWithUrlsAsync(predicate, db, storage, s3, ct);

        if (result is null)
        {
            return ProblemResults.NotFound("not_found");
        }

        var (clip, videoUrl, thumbnailUrl) = result.Value;
        return await BuildDetailResultAsync(clip, videoUrl, thumbnailUrl, principal, db, ct);
    }

    private static readonly string[] CrawlerSubstrings =
    [
        "Discordbot", "Twitterbot", "facebookexternalhit", "Slackbot",
        "LinkedInBot", "TelegramBot", "WhatsApp", "redditbot"
    ];

    private static bool IsCrawler(string userAgent) =>
        CrawlerSubstrings.Any(s => userAgent.Contains(s, StringComparison.OrdinalIgnoreCase));

    private static string BuildOgHtml(Clip clip, string videoUrl, string thumbnailUrl, string webOrigin)
    {
        var title = WebUtility.HtmlEncode(clip.Title);
        // IsNullOrWhiteSpace, not just null: an empty Description still emits an empty
        // <meta og:description> which crawlers (notably Slack) render as a blank line.
        var desc = !string.IsNullOrWhiteSpace(clip.Description) ? WebUtility.HtmlEncode(clip.Description) : null;
        // webOrigin is the public, config-driven origin (not request.Scheme/Host, which
        // behind a reverse proxy without UseForwardedHeaders surfaces the internal URL).
        var canonicalUrl = WebUtility.HtmlEncode($"{webOrigin}/c/{clip.ShareCode}");
        var encodedVideoUrl = WebUtility.HtmlEncode(videoUrl);
        var encodedThumbnailUrl = WebUtility.HtmlEncode(thumbnailUrl);
        // width/height fallback: Discord/Twitter require numeric values
        var width = clip.Width?.ToString() ?? "1280";
        var height = clip.Height?.ToString() ?? "720";
        // video/mp4 is hardcoded — it's the only content type allowed by ClipValidationOptions

        // Twitter card: summary_large_image rather than `player`. `player` requires an
        // HTTPS iframe HTML page (not a raw mp4) plus Twitter app approval; pointing
        // it at the presigned mp4 falls back to summary anyway. summary_large_image
        // renders the thumbnail + title/description directly.
        return $"""
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8" />
            <title>{title}</title>
            <meta property="og:type" content="video.other" />
            <meta property="og:url" content="{canonicalUrl}" />
            <meta property="og:title" content="{title}" />
            {(desc is not null ? $"""<meta property="og:description" content="{desc}" />""" : "")}
            <meta property="og:image" content="{encodedThumbnailUrl}" />
            <meta property="og:video" content="{encodedVideoUrl}" />
            <meta property="og:video:secure_url" content="{encodedVideoUrl}" />
            <meta property="og:video:type" content="video/mp4" />
            <meta property="og:video:width" content="{width}" />
            <meta property="og:video:height" content="{height}" />
            <meta name="twitter:card" content="summary_large_image" />
            <meta name="twitter:title" content="{title}" />
            {(desc is not null ? $"""<meta name="twitter:description" content="{desc}" />""" : "")}
            <meta name="twitter:image" content="{encodedThumbnailUrl}" />
            </head>
            <body></body>
            </html>
            """;
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
