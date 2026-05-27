using System.Security.Claims;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Contracts.Games;
using GankedTV.Api.Contracts.Leaderboards;
using GankedTV.Api.Data;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.Caching;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Endpoints;

public static class GamesEndpoints
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    public static IEndpointRouteBuilder MapGamesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/games");
        group.MapGet("/", GetGames);
        group.MapGet("/{slug}", GetBySlug);
        group.MapGet("/{slug}/clips", GetClipsForGame);
        group.MapGet("/{slug}/leaderboard", GetLeaderboardForGame);
        return app;
    }

    private static async Task<IResult> GetGames(
        string? search,
        int? limit,
        bool? hasClips,
        GankedTvDbContext db,
        IGamesCache gamesCache,
        CancellationToken ct)
    {
        var clampedLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        // Only the browse list (no search) is cached — search strings are high-cardinality and
        // the upload picker's queries don't repeat, so caching them would just churn keys.
        if (!string.IsNullOrWhiteSpace(search))
        {
            return Results.Ok(await QueryGamesAsync(db, hasClips, search, clampedLimit, ct));
        }

        var key = $"games:list:hasClips={hasClips == true}:{clampedLimit}";
        var cached = await gamesCache.GetOrCreateListAsync(
            key,
            async c => await QueryGamesAsync(db, hasClips, search: null, clampedLimit, c),
            ct);
        return Results.Ok(cached);
    }

    private static async Task<IReadOnlyList<GameListItem>> QueryGamesAsync(
        GankedTvDbContext db, bool? hasClips, string? search, int clampedLimit, CancellationToken ct)
    {
        var query = db.Games.AsNoTracking().AsQueryable();

        // The games *page* (GamesView) passes hasClips=true so it only lists games people can
        // actually watch; the upload picker (GameSelector) omits it to search the full catalog.
        if (hasClips == true)
        {
            query = query.Where(g => db.Clips.Any(c =>
                c.GameId == g.Id && c.Visibility == "public" && c.Status == "ready"));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Trim, then escape LIKE metacharacters so a user typing "100%" doesn't
            // turn into a wildcard match. Backslash is escaped first to avoid
            // double-escaping the escapes themselves.
            var trimmed = search.Trim()
                .Replace(@"\", @"\\")
                .Replace("%", @"\%")
                .Replace("_", @"\_");
            var pattern = $"%{trimmed}%";
            query = query.Where(g =>
                EF.Functions.ILike(g.Name, pattern, @"\")
                || EF.Functions.ILike(g.Slug, pattern, @"\"));
        }

        return await query
            .OrderBy(g => g.Name)
            .Take(clampedLimit)
            .Select(g => new GameListItem(g.Id, g.Name, g.Slug, g.Tag, g.CoverUrl))
            .ToListAsync(ct);
    }

    private static async Task<IResult> GetBySlug(
        string slug,
        GankedTvDbContext db,
        IGamesCache gamesCache,
        CancellationToken ct)
    {
        var detail = await gamesCache.GetOrCreateDetailAsync(
            $"games:detail:{slug}",
            async c => await QueryGameDetailAsync(db, slug, c),
            ct);

        return detail is null
            ? ProblemResults.NotFound("not_found")
            : Results.Ok(detail);
    }

    private static async Task<GameDetail?> QueryGameDetailAsync(
        GankedTvDbContext db, string slug, CancellationToken ct)
    {
        // One round-trip: project the entity together with a correlated COUNT scoped
        // to the same visibility/status filter the clips list uses, so the header
        // count matches the number of clips a user can actually scroll through.
        var row = await db.Games.AsNoTracking()
            .Where(g => g.Slug == slug)
            .Select(g => new
            {
                Game = g,
                ClipCount = db.Clips.Count(c =>
                    c.GameId == g.Id && c.Visibility == "public" && c.Status == "ready"),
            })
            .FirstOrDefaultAsync(ct);

        return row?.Game.ToDetail(row.ClipCount);
    }

    private static async Task<IResult> GetClipsForGame(
        string slug,
        string? cursor,
        int? limit,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        IFeedCache feedCache,
        CancellationToken ct)
    {
        // Distinguish "no such game" (404) from "game exists but has no clips" (200, empty page)
        // so the client can pick the right empty state.
        var gameId = await db.Games.AsNoTracking()
            .Where(g => g.Slug == slug)
            .Select(g => (int?)g.Id)
            .FirstOrDefaultAsync(ct);

        if (gameId is null)
            return ProblemResults.NotFound("not_found");

        var baseQuery = db.Clips.AsNoTracking()
            .Where(c => c.GameId == gameId && c.Visibility == "public" && c.Status == "ready");

        // Cache only the first page per game (no cursor). Cursor pages bypass the cache.
        if (cursor is null)
        {
            var feedLimit = Math.Clamp(limit ?? ClipsReadEndpoints.FeedDefaultLimit, 1, ClipsReadEndpoints.FeedMaxLimit);
            var cached = await feedCache.GetOrCreateFeedAsync(
                $"feed:game:{slug}:{feedLimit}",
                c => new ValueTask<CachedFeedPage>(
                    ClipsReadEndpoints.BuildAnonymousFeedPageAsync(baseQuery, null, limit, storage, s3, c)),
                ct);
            var items = await ClipsReadEndpoints.ApplyLikedByMeAsync(cached.Items, principal, db, ct);
            return Results.Ok(new ClipFeedResponse(items, cached.NextCursor));
        }

        var response = await ClipsReadEndpoints.BuildFeedPageAsync(
            baseQuery, cursor, limit, principal, db, storage, s3, ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetLeaderboardForGame(
        string slug,
        string? window,
        int? limit,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        IFeedCache feedCache,
        CancellationToken ct)
    {
        if (!LeaderboardWindow.TryParseRequest(
                window, limit,
                LeaderboardsEndpoints.DefaultClipsLimit, LeaderboardsEndpoints.MaxClipsLimit,
                out var windowKey, out var since, out var cap))
        {
            return ProblemResults.BadRequest("invalid_window");
        }

        // Resolve game first so "no such game" stays a 404 even when there are no likes in
        // the window — otherwise an unknown slug would silently return an empty board.
        // Inline the GameSummary columns so EF projects rather than materializing the full
        // entity (extension-method calls inside Select can't be translated).
        var game = await db.Games.AsNoTracking()
            .Where(g => g.Slug == slug)
            .Select(g => new { g.Id, Summary = new GameSummary(g.Id, g.Name, g.Slug, g.Tag) })
            .FirstOrDefaultAsync(ct);

        if (game is null)
            return ProblemResults.NotFound("not_found");

        // Cache the anonymous entry list keyed by slug+window+cap; stamp LikedByMe post-cache.
        // Same TTL-only contract as the global board and trending — likes don't bust the cache.
        var gameId = game.Id;
        var anonymousEntries = await feedCache.GetOrCreateLeaderboardAsync(
            $"lb:game:{slug}:{windowKey}:{cap}",
            c =>
            {
                var baseQuery = db.Clips.AsNoTracking()
                    .Where(cl => cl.GameId == gameId && cl.Visibility == "public" && cl.Status == "ready");
                return new ValueTask<List<LeaderboardEntry>>(
                    LeaderboardsEndpoints.BuildAnonymousEntriesAsync(baseQuery, since, cap, db, storage, s3, c));
            },
            ct);

        var stamped = await LeaderboardsEndpoints.StampLikedByMeOnEntriesAsync(
            anonymousEntries, principal, db, ct);
        return Results.Ok(new GameLeaderboardResponse(windowKey, game.Summary, stamped));
    }
}
