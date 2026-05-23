using System.IdentityModel.Tokens.Jwt;
using System.Linq.Expressions;
using System.Net;
using System.Security.Claims;
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
    // Feed-prefixed because BuildFeedPageAsync is called from GamesEndpoints too;
    // a generic DefaultLimit/MaxLimit would collide with the same-named (different
    // value) constants there. Internal so the helper is callable cross-class.
    internal const int FeedDefaultLimit = 20;
    internal const int FeedMaxLimit = 100;
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
        string? source,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        var baseQuery = db.Clips.AsNoTracking()
            .Where(c => c.Visibility == "public" && c.Status == "ready");

        // `source` is treated leniently: only the literal "following" switches behaviour;
        // anything else (null, "public", garbage) falls through to the global feed. Matches
        // the same forgive-and-fall-back spirit as the cursor decoder.
        if (string.Equals(source, "following", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryGetUserId(principal, out var me))
            {
                return ProblemResults.Unauthorized("unauthorized");
            }

            baseQuery = baseQuery.Where(c =>
                db.Follows.Any(f => f.FollowerId == me && f.FolloweeId == c.UserId));
        }

        var response = await BuildFeedPageAsync(baseQuery, cursor, limit, principal, db, storage, s3, ct);
        return Results.Ok(response);
    }

    // Shared cursor-paginated feed builder. Callers pass a pre-filtered IQueryable<Clip>
    // (e.g. global feed, per-game feed) and this owns ordering, keyset cursoring,
    // includes, likedByMe lookup, thumbnail signing, and nextCursor minting.
    internal static async Task<ClipFeedResponse> BuildFeedPageAsync(
        IQueryable<Clip> baseQuery,
        string? cursor,
        int? limit,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        var clampedLimit = Math.Clamp(limit ?? FeedDefaultLimit, 1, FeedMaxLimit);

        // Invalid cursor values silently fall back to "no cursor" rather than 400-ing; the
        // client's next-page fetch shouldn't be broken by a corrupted query string.
        var hasCursor = FeedCursor.TryParse(cursor, out var cursorCreatedAt, out var cursorId);

        var query = baseQuery;
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
            .IncludeFeedRelations()
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
        var nextCursor = hasMore ? FeedCursor.Build(page[^1].CreatedAt, page[^1].Id) : null;

        return new ClipFeedResponse(items, nextCursor);
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
            .IncludeFeedRelations()
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
