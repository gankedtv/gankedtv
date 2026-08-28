using System.Security.Claims;
using GankedTV.Api.Auth;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Contracts.Games;
using GankedTV.Api.Contracts.Leaderboards;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Data;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.Caching;
using GankedTV.Api.Services.Igdb;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Endpoints;

public static class GamesEndpoints
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;
    private const int HotDefaultLimit = 8;
    private const int HotMaxLimit = 20;

    // Longest game name in IGDB is well under this; the cap bounds what an unbounded search term
    // can do downstream (memo keys, IGDB query size).
    private const int MaxSearchLength = 100;

    // Fixed 7-day window: hot games deliberately don't follow the trending page's window
    // toggle — game hotness moves slower than clip hotness.
    private static readonly TimeSpan HotWindow = TimeSpan.FromDays(7);

    public static IEndpointRouteBuilder MapGamesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/games");
        group.MapGet("/", GetGames).RequireRateLimiting(GamesRateLimiting.GamesSearchPolicy);
        // Any literal added here must also go in GameCatalogImporter.ReservedSlugs, or an
        // imported game whose slug matches it would shadow the route.
        group.MapGet("/hot", GetHotGames);
        group.MapGet("/{slug}", GetBySlug);
        group.MapGet("/{slug}/clips", GetClipsForGame);
        group.MapGet("/{slug}/leaderboard", GetLeaderboardForGame);
        return app;
    }

    private static async Task<IResult> GetGames(
        string? search,
        int? limit,
        bool? hasClips,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IGamesCache gamesCache,
        IGameSearchImportService searchImport,
        CancellationToken ct)
    {
        var clampedLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        if (search is { Length: > MaxSearchLength })
        {
            return ProblemResults.BadRequest("invalid_search");
        }

        // Only the browse list (no search) is cached — search strings are high-cardinality and
        // the upload picker's queries don't repeat, so caching them would just churn keys.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var results = await QueryGamesAsync(db, hasClips, search, clampedLimit, ct);

            // Local miss → pull matches from IGDB into the catalog and retry once, so
            // long-tail games outside the popularity import become pickable on first search.
            // Authenticated-only: the pickers behind it all require auth, and it keeps
            // anonymous unique-term enumeration from minting catalog rows and IGDB calls.
            // Skipped under hasClips (a just-imported game has no clips; the retry can't win).
            if (results.Count == 0
                && hasClips != true
                && principal.TryGetUserId(out _)
                && await searchImport.TryImportMatchesAsync(search, ct))
            {
                results = await QueryGamesAsync(db, hasClips, search, clampedLimit, ct);
            }

            return Results.Ok(results);
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
                c.GameId == g.Id && c.Visibility == ClipVisibilities.Public && c.Status == ClipStatuses.Ready));
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

        // Two rows can legitimately share a display name, and an unordered tie makes
        // `Take(limit)` non-deterministic across requests.
        return await query
            .OrderBy(g => g.Name)
            .ThenBy(g => g.Id)
            .Take(clampedLimit)
            .Select(g => new GameListItem(g.Id, g.Name, g.Slug, g.Tag, g.CoverUrl))
            .ToListAsync(ct);
    }

    private static async Task<IResult> GetHotGames(
        int? limit,
        GankedTvDbContext db,
        IGamesCache gamesCache,
        CancellationToken ct)
    {
        var clampedLimit = Math.Clamp(limit ?? HotDefaultLimit, 1, HotMaxLimit);
        var items = await gamesCache.GetOrCreateListAsync(
            $"games:hot:{clampedLimit}",
            async c => await QueryHotGamesAsync(db, clampedLimit, c),
            ct);
        return Results.Ok(items);
    }

    private static async Task<IReadOnlyList<GameListItem>> QueryHotGamesAsync(
        GankedTvDbContext db, int clampedLimit, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - HotWindow;

        // Same signal and weighting as the trending feed (likes ×3 + views in window),
        // aggregated up to the game. No per-clip age decay: the window already bounds
        // recency, and a game aggregates many clips so decay adds little.
        var likesByGame = await db.Likes.AsNoTracking()
            .Where(l => l.CreatedAt >= since)
            .Join(db.Clips.WherePublicReady().Where(c => c.GameId != null),
                l => l.ClipId, c => c.Id, (l, c) => c.GameId!.Value)
            .GroupBy(id => id)
            .Select(g => new { GameId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var viewsByGame = await db.ClipViews.AsNoTracking()
            .Where(v => v.CreatedAt >= since)
            .Join(db.Clips.WherePublicReady().Where(c => c.GameId != null),
                v => v.ClipId, c => c.Id, (v, c) => c.GameId!.Value)
            .GroupBy(id => id)
            .Select(g => new { GameId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var scores = new Dictionary<int, long>();
        foreach (var l in likesByGame)
        {
            scores[l.GameId] = scores.GetValueOrDefault(l.GameId) + l.Count * 3L;
        }
        foreach (var v in viewsByGame)
        {
            scores[v.GameId] = scores.GetValueOrDefault(v.GameId) + v.Count;
        }

        var hotIds = scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Take(clampedLimit)
            .Select(kv => kv.Key)
            .ToList();

        var hotById = await db.Games.AsNoTracking()
            .Where(g => hotIds.Contains(g.Id))
            .Select(g => new GameListItem(g.Id, g.Name, g.Slug, g.Tag, g.CoverUrl))
            .ToDictionaryAsync(g => g.Id, ct);
        var items = hotIds
            .Where(hotById.ContainsKey)
            .Select(id => hotById[id])
            .ToList();

        if (items.Count < clampedLimit)
        {
            // Backfill with the most-clipped games so the rail stays full on quiet weeks. Grouped
            // over clips rather than a correlated count per game: the catalog grows with on-demand
            // imports, but only games that have clips can ever rank.
            var chosen = items.Select(i => i.Id).ToHashSet();
            var backfill = await db.Clips.AsNoTracking().WherePublicReady()
                .Where(c => c.GameId != null && !chosen.Contains(c.GameId.Value))
                .GroupBy(c => c.GameId!.Value)
                .Select(g => new { GameId = g.Key, ClipCount = g.Count() })
                .Join(db.Games.AsNoTracking(), x => x.GameId, g => g.Id, (x, g) => new
                {
                    x.ClipCount,
                    g.Id,
                    g.Name,
                    g.Slug,
                    g.Tag,
                    g.CoverUrl,
                })
                .OrderByDescending(x => x.ClipCount)
                .ThenBy(x => x.Name)
                .Take(clampedLimit - items.Count)
                .ToListAsync(ct);
            items.AddRange(backfill.Select(x => new GameListItem(x.Id, x.Name, x.Slug, x.Tag, x.CoverUrl)));
        }

        return items;
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
                    c.GameId == g.Id && c.Visibility == ClipVisibilities.Public && c.Status == ClipStatuses.Ready),
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
        IOptions<S3Options> s3,
        IFeedCache feedCache,
        ISignedUrlCache signedUrls,
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
            .Where(c => c.GameId == gameId).WherePublicReady();

        // Cache only the first page per game (no cursor). Cursor pages bypass the cache.
        if (cursor is null)
        {
            var feedLimit = Math.Clamp(limit ?? ClipsReadEndpoints.FeedDefaultLimit, 1, ClipsReadEndpoints.FeedMaxLimit);
            var cached = await feedCache.GetOrCreateFeedAsync(
                $"feed:game:{slug}:{feedLimit}",
                c => new ValueTask<CachedFeedPage>(
                    ClipsReadEndpoints.BuildAnonymousFeedPageAsync(baseQuery, null, limit, signedUrls, s3, c)),
                ct);
            var items = await ClipsReadEndpoints.ApplyLikedByMeAsync(cached.Items, principal, db, ct);
            return Results.Ok(new ClipFeedResponse(items, cached.NextCursor));
        }

        var response = await ClipsReadEndpoints.BuildFeedPageAsync(
            baseQuery, cursor, limit, principal, db, signedUrls, s3, ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetLeaderboardForGame(
        string slug,
        string? window,
        int? limit,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        ISignedUrlCache signedUrls,
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
                    .Where(cl => cl.GameId == gameId).WherePublicReady();
                return new ValueTask<List<LeaderboardEntry>>(
                    LeaderboardsEndpoints.BuildAnonymousEntriesAsync(baseQuery, since, cap, db, signedUrls, s3, c));
            },
            ct);

        var stamped = await LeaderboardsEndpoints.StampLikedByMeOnEntriesAsync(
            anonymousEntries, principal, db, ct);
        return Results.Ok(new GameLeaderboardResponse(windowKey, game.Summary, stamped));
    }
}
