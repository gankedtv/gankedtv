using System.IdentityModel.Tokens.Jwt;
using System.Linq.Expressions;
using System.Net;
using System.Security.Claims;
using GankedTV.Api.Auth;
using GankedTV.Api.Clips;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Pagination;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.Caching;
using GankedTV.Api.Services.Media;
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
    // Trending is a single ranked page (no keyset pagination) sized for the discovery UI
    // top-10 with headroom. Capping here keeps the in-memory scoring step bounded.
    internal const int TrendingMaxLimit = 50;
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
        // Owner-scoped status probe. Lets the upload/import UI poll for status transitions
        // (importing → processing → transcoding → ready/failed) without needing the full
        // detail payload, which only exists once status='ready'.
        group.MapGet("/{id:guid}/status", GetStatus).RequireAuthorization();
        // Anonymous like GetDetail, but each call does a DB lookup + S3 HEAD (+ enqueue on
        // miss), so it rides the same per-IP view rate limit to bound abuse.
        group.MapGet("/{id:guid}/stream", GetStream)
            .RequireRateLimiting(ClipsRateLimiting.ClipsViewPolicy);
        app.MapGet("/c/{code:length(6,12)}", GetByShareCode);
        return app;
    }

    // Returns the wizard's polling payload for the requesting user's own clip. Carries the
    // clip's status, share code, and (when failed) the structured failure reason + the
    // observed duration + the configured cap, so the front-end can show "your clip is X
    // seconds; max is Y" instead of a generic error.
    private static async Task<IResult> GetStatus(
        Guid id,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IOptions<GankedTV.Api.Validation.ClipValidationOptions> validationOptions,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        var cap = validationOptions.Value.MaxClipDurationSecs;
        var row = await db.Clips.AsNoTracking()
            .Where(c => c.Id == id && c.UserId == userId)
            .Select(c => new ClipStatusResponse(c.Id, c.Status, c.ShareCode, c.FailureReason, c.DurationSecs, cap))
            .SingleOrDefaultAsync(ct);

        return row is null ? ProblemResults.NotFound("not_found") : Results.Ok(row);
    }

    // Just-in-time H.264 stream for devices that can't decode a clip's stored master (e.g.
    // AV1). Cache hit → 200 with the public master-playlist URL; miss → enqueue a JIT build
    // and return 202 (client polls); a failed build → 503.
    private static async Task<IResult> GetStream(
        Guid id,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        IClipStreamJobStore streamJobs,
        CancellationToken ct)
    {
        // Same visibility rule as the detail endpoint: any ready clip is reachable by link.
        var clip = await db.Clips.AsNoTracking()
            .Where(c => c.Id == id && c.Status == "ready")
            .Select(c => new { c.VideoCodec })
            .SingleOrDefaultAsync(ct);
        if (clip is null)
            return ProblemResults.NotFound("not_found");

        // H.264 masters play directly in every browser — the detail contract tells clients to
        // use videoUrl. Refuse JIT here so /stream can't be abused to pile on avoidable
        // transcode load for clips that never need it.
        if (string.Equals(clip.VideoCodec, "h264", StringComparison.Ordinal))
            return ProblemResults.BadRequest("stream_not_required");

        var masterKey = $"{JitLadderService.BuildCachePrefix(id)}/master.m3u8";
        var cached = await storage.GetObjectMetadataAsync(s3.Value.StreamCacheBucket, masterKey, ct);
        if (cached is not null)
        {
            var url = S3PublicUrls.BuildUrl(s3.Value, s3.Value.StreamCacheBucket, masterKey);
            return Results.Ok(new StreamResponse(url, "ready"));
        }

        // Enqueue first: this inserts a pending job, or recovers a stale 'failed' row (past the
        // retry cooldown) back to pending so a transient GPU outage doesn't permanently block
        // the clip. A still-fresh 'failed' row is left untouched and surfaced as 503 below.
        await streamJobs.EnqueueAsync(id, ct);
        if (await streamJobs.GetStatusAsync(id, ct) == ClipStreamJobStatuses.Failed)
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "stream_unavailable",
                detail: "Just-in-time transcode failed for this clip.");

        return Results.Json(new StreamResponse(null, "pending"), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> GetFeed(
        string? cursor,
        int? limit,
        string? source,
        string? sort,
        string? window,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        IFeedCache feedCache,
        CancellationToken ct)
    {
        var baseQuery = db.Clips.AsNoTracking()
            .Where(c => c.Visibility == "public" && c.Status == "ready");

        // `source` is treated leniently: only the literal "following" switches behaviour;
        // anything else (null, "public", garbage) falls through to the global feed. Matches
        // the same forgive-and-fall-back spirit as the cursor decoder.
        var isFollowing = string.Equals(source, "following", StringComparison.OrdinalIgnoreCase);
        if (isFollowing)
        {
            if (!TryGetUserId(principal, out var me))
            {
                return ProblemResults.Unauthorized("unauthorized");
            }

            baseQuery = baseQuery.Where(c =>
                db.Follows.Any(f => f.FollowerId == me && f.FolloweeId == c.UserId));
        }

        // Symmetric with `window`: null/empty falls through to the default (latest), but an
        // explicit non-null value outside the known set is a 400 to surface client typos
        // (`?sort=trendng`) instead of silently serving latest under a different label.
        if (!string.IsNullOrEmpty(sort)
            && !string.Equals(sort, "latest", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(sort, "trending", StringComparison.OrdinalIgnoreCase))
        {
            return ProblemResults.BadRequest("invalid_sort");
        }

        if (string.Equals(sort, "trending", StringComparison.OrdinalIgnoreCase))
        {
            // `window` is required for trending — unlike `source`, an unknown value is a
            // 400 rather than silent fall-back because trending is meaningless without a
            // window and we'd rather surface the typo than guess.
            if (!TryParseTrendingWindow(window, out var since))
            {
                return ProblemResults.BadRequest("invalid_window");
            }

            // The personalised "following + trending" combination filters baseQuery per user,
            // so it must never hit the shared cache; only the global trending feed is cached.
            if (isFollowing)
            {
                var personalised = await BuildTrendingFeedAsync(baseQuery, since, limit, principal, db, storage, s3, ct);
                return Results.Ok(personalised);
            }

            var trendingLimit = Math.Clamp(limit ?? FeedDefaultLimit, 1, TrendingMaxLimit);
            var cachedTrending = await feedCache.GetOrCreateTrendingAsync(
                $"feed:trending:{window}:{trendingLimit}",
                c => new ValueTask<CachedFeedPage>(
                    BuildAnonymousTrendingFeedAsync(baseQuery, since, limit, db, storage, s3, c)),
                ct);
            var trendingItems = await ApplyLikedByMeAsync(cachedTrending.Items, principal, db, ct);
            return Results.Ok(new ClipFeedResponse(trendingItems, cachedTrending.NextCursor));
        }

        // Cache only the global latest first page (no cursor, not personalised). Cursor pages
        // and following feeds bypass the cache and query Postgres directly.
        if (cursor is null && !isFollowing)
        {
            var feedLimit = Math.Clamp(limit ?? FeedDefaultLimit, 1, FeedMaxLimit);
            var cached = await feedCache.GetOrCreateFeedAsync(
                $"feed:latest:{feedLimit}",
                c => new ValueTask<CachedFeedPage>(
                    BuildAnonymousFeedPageAsync(baseQuery, null, limit, storage, s3, c)),
                ct);
            var items = await ApplyLikedByMeAsync(cached.Items, principal, db, ct);
            return Results.Ok(new ClipFeedResponse(items, cached.NextCursor));
        }

        var latest = await BuildFeedPageAsync(baseQuery, cursor, limit, principal, db, storage, s3, ct);
        return Results.Ok(latest);
    }

    // Per-issue: only 24h and 7d are supported in v1. Other window strings (or null) are 400 —
    // the web client sends the value verbatim, so a 400 surfaces a UI bug instead of returning
    // an arbitrary fallback ranking.
    internal static bool TryParseTrendingWindow(string? window, out DateTimeOffset since)
    {
        var now = DateTimeOffset.UtcNow;
        switch (window)
        {
            case "24h":
                since = now.AddHours(-24);
                return true;
            case "7d":
                since = now.AddDays(-7);
                return true;
            default:
                since = default;
                return false;
        }
    }

    // Time-weighted ranked feed. Score = (likes_in_window * 3 + views_in_window) / pow(hours+2, 1.5).
    //
    // SQL fetches: clip + engagement counts in the window, pre-filtered to clips with ANY
    // engagement in the window (so we don't return a count tuple for every dormant clip).
    // Scoring + ordering happen in-memory afterwards — Postgres' EF translation doesn't have a
    // clean `pow` over interval-hours and the candidate set is bounded by the engagement filter.
    //
    // Revisit if active-clips per window climbs past ~10k (early-stage assumption: << that).
    internal static async Task<ClipFeedResponse> BuildTrendingFeedAsync(
        IQueryable<Clip> baseQuery,
        DateTimeOffset since,
        int? limit,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        var page = await BuildAnonymousTrendingFeedAsync(baseQuery, since, limit, db, storage, s3, ct);
        var items = await ApplyLikedByMeAsync(page.Items, principal, db, ct);
        return new ClipFeedResponse(items, page.NextCursor);
    }

    // Caller-independent half of BuildTrendingFeedAsync — the scoring + rehydration + anonymous
    // projection that FeedCache stores. The window-relative ranking is inherently approximate and
    // self-healing, so caching it behind a short TTL (rather than invalidating on every like/view)
    // is sufficient: a cache entry just freezes `since`/scores for one TTL.
    internal static async Task<CachedFeedPage> BuildAnonymousTrendingFeedAsync(
        IQueryable<Clip> baseQuery,
        DateTimeOffset since,
        int? limit,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        var clampedLimit = Math.Clamp(limit ?? FeedDefaultLimit, 1, TrendingMaxLimit);

        var candidates = await baseQuery
            .Where(c => db.Likes.Any(l => l.ClipId == c.Id && l.CreatedAt > since)
                     || db.ClipViews.Any(v => v.ClipId == c.Id && v.CreatedAt > since))
            .Select(c => new
            {
                Clip = c,
                LikesInWindow = db.Likes.Count(l => l.ClipId == c.Id && l.CreatedAt > since),
                ViewsInWindow = db.ClipViews.Count(v => v.ClipId == c.Id && v.CreatedAt > since),
            })
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var topIds = candidates
            .Select(r => new
            {
                r.Clip,
                Score = (r.LikesInWindow * 3 + r.ViewsInWindow)
                    / Math.Pow(Math.Max(0, (now - r.Clip.CreatedAt).TotalHours) + 2, 1.5),
            })
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Clip.CreatedAt)
            .Take(clampedLimit)
            .Select(r => r.Clip.Id)
            .ToList();

        if (topIds.Count == 0)
        {
            return new CachedFeedPage([], NextCursor: null);
        }

        // Re-hydrate with feed Includes (the candidate Select dropped them) and preserve
        // ranking order. EF's Contains() generates IN (...) which doesn't preserve order, so
        // we re-sort in C# after fetch using the topIds index.
        //
        // Visibility/status filters from `baseQuery` aren't reapplied here because `topIds` was
        // already derived from the filtered candidate set. The micro-race window — a clip
        // flipping to unlisted or back to processing between scoring and rehydration — could
        // surface one stale row in a trending response; accepted as bounded and self-healing
        // on the next request. A clip *deleted* between scoring and rehydration just drops
        // out of the result (TryGetValue skip) rather than 500ing.
        var ordered = await db.Clips.AsNoTracking()
            .Where(c => topIds.Contains(c.Id))
            .IncludeFeedRelations()
            .ToListAsync(ct);

        var byId = ordered.ToDictionary(c => c.Id);
        var ranked = new List<Clip>(topIds.Count);
        foreach (var id in topIds)
        {
            if (byId.TryGetValue(id, out var clip))
            {
                ranked.Add(clip);
            }
        }

        var items = ProjectAnonymousFeedItems(ranked, storage, s3);
        return new CachedFeedPage(items, NextCursor: null);
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
        var page = await BuildAnonymousFeedPageAsync(baseQuery, cursor, limit, storage, s3, ct);
        var items = await ApplyLikedByMeAsync(page.Items, principal, db, ct);
        return new ClipFeedResponse(items, page.NextCursor);
    }

    // Caller-independent half of BuildFeedPageAsync: runs the keyset query + ordering + includes
    // + thumbnail signing into an anonymous page (no likedByMe). This is what FeedCache stores,
    // so the cached entry never holds personalised data. No ClaimsPrincipal/DbContext needed
    // for the likes lookup — that happens after the cache, per caller, in ApplyLikedByMeAsync.
    internal static async Task<CachedFeedPage> BuildAnonymousFeedPageAsync(
        IQueryable<Clip> baseQuery,
        string? cursor,
        int? limit,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        var clampedLimit = Math.Clamp(limit ?? FeedDefaultLimit, 1, FeedMaxLimit);

        // Invalid cursor values silently fall back to "no cursor" rather than 400-ing; the
        // client's next-page fetch shouldn't be broken by a corrupted query string.
        var hasCursor = KeysetCursor.TryParse(cursor, out var cursorCreatedAt, out var cursorId);

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

        var items = ProjectAnonymousFeedItems(page, storage, s3);
        var nextCursor = hasMore ? KeysetCursor.Build(page[^1].CreatedAt, page[^1].Id) : null;

        return new CachedFeedPage(items, nextCursor);
    }

    // Shared DTO projector for any pre-ordered, pre-included Clip list. Splits out the
    // likedByMe lookup + thumbnail signing so SearchEndpoints can reuse them on a ranked
    // (non-paginated, non-cursor) result set without duplicating the logic.
    internal static async Task<List<ClipFeedItem>> ProjectFeedItemsAsync(
        IReadOnlyList<Clip> clips,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        var anonymous = ProjectAnonymousFeedItems(clips, storage, s3);
        return await ApplyLikedByMeAsync(anonymous, principal, db, ct);
    }

    // Caller-independent projection: thumbnail signing only, LikedByMe left false. This is the
    // shape the feed cache stores — never personalised — so one user's likes can't leak to
    // another via a shared cache entry. likedByMe is re-stamped per request by ApplyLikedByMeAsync.
    internal static List<ClipFeedItem> ProjectAnonymousFeedItems(
        IReadOnlyList<Clip> clips,
        IObjectStorageService storage,
        IOptions<S3Options> s3)
    {
        if (clips.Count == 0)
        {
            return [];
        }

        var thumbnailsBucket = s3.Value.ThumbnailsBucket;
        return [.. clips.Select(c => c.ToFeedItem(
            BuildThumbnailUrl(storage, thumbnailsBucket, c.ThumbnailKey),
            likedByMe: false))];
    }

    // Re-stamps the per-caller LikedByMe flag onto an anonymous (possibly cached) item list.
    // Anonymous callers and callers with no likes among these clips get the list back unchanged
    // (items already carry LikedByMe=false), so the only cost is one indexed Likes lookup.
    internal static async Task<List<ClipFeedItem>> ApplyLikedByMeAsync(
        IReadOnlyList<ClipFeedItem> items,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return [.. items];
        }

        var likedIds = await LoadLikedClipIdsAsync(db, principal, items.Select(i => i.Id), ct);
        if (likedIds.Count == 0)
        {
            return [.. items];
        }

        return [.. items.Select(i => likedIds.Contains(i.Id) ? i with { LikedByMe = true } : i)];
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

        // videoUrl presigns the stored master (the compressed file). The web player decides
        // from VideoCodec whether to play it directly or request a JIT H.264 stream.
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
