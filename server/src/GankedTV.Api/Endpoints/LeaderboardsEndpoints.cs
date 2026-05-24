using System.Security.Claims;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Contracts.Games;
using GankedTV.Api.Contracts.Leaderboards;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Endpoints;

public static class LeaderboardsEndpoints
{
    internal const int DefaultClipsLimit = 10;
    internal const int MaxClipsLimit = 50;
    internal const int DefaultGamesLimit = 10;
    internal const int MaxGamesLimit = 25;

    public static IEndpointRouteBuilder MapLeaderboardsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/leaderboards", GetGlobal);
        return app;
    }

    private static async Task<IResult> GetGlobal(
        string? window,
        int? clipsLimit,
        int? gamesLimit,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        // Default to "week" when the caller omits window — the standalone /leaderboards
        // landing page renders week-first; explicit-but-unknown values still 400 so a UI
        // typo (`?window=mounth`) surfaces immediately instead of being silently coerced.
        var windowKey = window ?? LeaderboardWindow.Default;
        if (!LeaderboardWindow.TryParse(windowKey, out var since))
        {
            return ProblemResults.BadRequest("invalid_window");
        }

        var clipsCap = Math.Clamp(clipsLimit ?? DefaultClipsLimit, 1, MaxClipsLimit);
        var gamesCap = Math.Clamp(gamesLimit ?? DefaultGamesLimit, 1, MaxGamesLimit);

        var clipsBase = db.Clips.AsNoTracking()
            .Where(c => c.Visibility == "public" && c.Status == "ready");
        var topClips = await BuildEntriesAsync(clipsBase, since, clipsCap, principal, db, storage, s3, ct);

        var topGames = await BuildTopGamesAsync(since, gamesCap, db, ct);

        return Results.Ok(new GlobalLeaderboardResponse(windowKey, topClips, topGames));
    }

    // Shared helper: turn a pre-filtered Clip IQueryable into a ranked list of leaderboard
    // entries scoped to a window. Owns the windowed-count query, deterministic tiebreak,
    // hydration with feed includes, and the rank-numbering pass.
    //
    // Tie-breaking goes (likes desc, clip.created_at desc, clip.id asc): newer clips with
    // equal likes outrank older ones (rewards momentum); equal createdAt falls back to a
    // total ordering on Guid so the ranking is stable across requests.
    internal static async Task<List<LeaderboardEntry>> BuildEntriesAsync(
        IQueryable<Clip> baseQuery,
        DateTimeOffset since,
        int limit,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        // Pre-filter to clips with ANY like in the window so the COUNT subquery only fires
        // for candidates that could plausibly rank. Same shape as BuildTrendingFeedAsync —
        // bounded candidate set, then sort in memory because EF + Postgres can't reliably
        // order on a correlated COUNT alongside the tiebreak chain we want.
        var candidates = await baseQuery
            .Where(c => db.Likes.Any(l => l.ClipId == c.Id && l.CreatedAt >= since))
            .Select(c => new
            {
                ClipId = c.Id,
                c.CreatedAt,
                WindowLikes = db.Likes.Count(l => l.ClipId == c.Id && l.CreatedAt >= since),
            })
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return [];
        }

        var ranked = candidates
            .OrderByDescending(x => x.WindowLikes)
            .ThenByDescending(x => x.CreatedAt)
            .ThenBy(x => x.ClipId)
            .Take(limit)
            .ToList();

        var topIds = ranked.Select(r => r.ClipId).ToList();
        var hydrated = await db.Clips.AsNoTracking()
            .Where(c => topIds.Contains(c.Id))
            .IncludeFeedRelations()
            .ToListAsync(ct);
        var byId = hydrated.ToDictionary(c => c.Id);

        var feedItems = await ClipsReadEndpoints.ProjectFeedItemsAsync(
            [.. ranked.Where(r => byId.ContainsKey(r.ClipId)).Select(r => byId[r.ClipId])],
            principal, db, storage, s3, ct);

        // Walk ranked + feedItems in lockstep: feedItems was built from ranked-ordered
        // clips, so index i corresponds to the same clip in both lists. A clip dropped
        // between candidate fetch and hydrate (rare but possible mid-delete) just falls
        // out of both lists together.
        var entries = new List<LeaderboardEntry>(feedItems.Count);
        var rank = 0;
        var feedIndex = 0;
        foreach (var r in ranked)
        {
            if (!byId.ContainsKey(r.ClipId)) continue;
            rank++;
            entries.Add(feedItems[feedIndex].ToEntry(rank, r.WindowLikes));
            feedIndex++;
        }
        return entries;
    }

    // Top games for a window: sum windowed likes across each game's public+ready clips.
    // Skips games with zero likes in the window so the response only ranks games that
    // actually have activity. ClipCount counts public+ready clips for the whole catalog
    // (not just clips with likes in the window) so the number matches what GameView's
    // header shows — otherwise the same game would display two different counts.
    private static async Task<List<TopGameEntry>> BuildTopGamesAsync(
        DateTimeOffset since,
        int limit,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        var rows = await db.Games.AsNoTracking()
            .Select(g => new
            {
                Game = g,
                WindowLikes = db.Likes.Count(l =>
                    l.CreatedAt >= since
                    && l.Clip.GameId == g.Id
                    && l.Clip.Visibility == "public"
                    && l.Clip.Status == "ready"),
                ClipCount = db.Clips.Count(c =>
                    c.GameId == g.Id && c.Visibility == "public" && c.Status == "ready"),
            })
            .Where(x => x.WindowLikes > 0)
            .ToListAsync(ct);

        return [.. rows
            .OrderByDescending(x => x.WindowLikes)
            .ThenByDescending(x => x.ClipCount)
            .ThenBy(x => x.Game.Name, StringComparer.Ordinal)
            .Take(limit)
            .Select((x, i) => new TopGameEntry(
                i + 1,
                x.WindowLikes,
                x.ClipCount,
                x.Game.ToGameSummary(),
                x.Game.CoverUrl))];
    }
}
