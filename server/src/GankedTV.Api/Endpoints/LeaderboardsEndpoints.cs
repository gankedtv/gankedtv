using System.Security.Claims;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Contracts.Games;
using GankedTV.Api.Contracts.Leaderboards;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.Caching;
using GankedTV.Api.Services.Feeds;
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
        ISignedUrlCache signedUrls,
        IOptions<S3Options> s3,
        IFeedCache feedCache,
        CancellationToken ct)
    {
        // Default to "week" when the caller omits window — the standalone /leaderboards
        // landing page renders week-first; explicit-but-unknown values still 400 so a UI
        // typo (`?window=mounth`) surfaces immediately instead of being silently coerced.
        if (!LeaderboardWindow.TryParseRequest(
                window, clipsLimit, DefaultClipsLimit, MaxClipsLimit,
                out var windowKey, out var since, out var clipsCap))
        {
            return ProblemResults.BadRequest("invalid_window");
        }

        var gamesCap = Math.Clamp(gamesLimit ?? DefaultGamesLimit, 1, MaxGamesLimit);

        // Cache the anonymous shape (LikedByMe=false on every entry); the per-caller stamp
        // happens post-cache via StampLikedByMeOnEntriesAsync, the same pattern trending uses.
        // TTL-only invalidation matches trending: likes are too high-frequency to bust the
        // cache on each one, and a window-bounded board is inherently approximate.
        var cached = await feedCache.GetOrCreateLeaderboardAsync(
            $"lb:global:{windowKey}:{clipsCap}:{gamesCap}",
            async c =>
            {
                var clipsBase = db.Clips.AsNoTracking()
                    .WherePublicReady();
                var topClips = await BuildAnonymousEntriesAsync(clipsBase, since, clipsCap, db, signedUrls, s3, c);
                var topGames = await BuildTopGamesAsync(since, gamesCap, db, c);
                return new GlobalLeaderboardResponse(windowKey, topClips, topGames);
            },
            ct);

        var stampedClips = await StampLikedByMeOnEntriesAsync(cached.TopClips, principal, db, ct);
        return Results.Ok(cached with { TopClips = stampedClips });
    }

    // Shared helper: turn a pre-filtered Clip IQueryable into a ranked list of leaderboard
    // entries scoped to a window. Owns the windowed-count query, deterministic tiebreak,
    // hydration with feed includes, and the rank-numbering pass.
    //
    // Anonymous half (no LikedByMe stamp) — the per-caller stamp is added post-cache by
    // <see cref="StampLikedByMeOnEntriesAsync"/>, so this output is safe to share via the
    // leaderboard cache. Tie-breaking goes (likes desc, clip.created_at desc, clip.id asc):
    // newer clips with equal likes outrank older ones (rewards momentum); equal createdAt
    // falls back to a total ordering on Guid so the ranking is stable across requests.
    internal static async Task<List<LeaderboardEntry>> BuildAnonymousEntriesAsync(
        IQueryable<Clip> baseQuery,
        DateTimeOffset since,
        int limit,
        GankedTvDbContext db,
        ISignedUrlCache signedUrls,
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

        // Hydrate ranked IDs through the shared builder with the belt-and-braces re-filter
        // turned on: a leaderboard surfacing a clip that flipped to private/unlisted between
        // candidate fetch and hydration is more user-visible than the same race on trending,
        // which self-heals via TTL.
        var topIds = ranked.Select(r => r.ClipId).ToList();
        var hydratedOrdered = await RankedFeedBuilder.HydrateOrderedAsync(
            topIds, db, reapplyPublicReadyFilter: true, ct);

        var feedItems = await ClipsReadEndpoints.ProjectAnonymousFeedItemsAsync(
            hydratedOrdered, signedUrls, s3, ct);

        // hydratedOrdered/feedItems are in `ranked` order with dropped IDs already removed,
        // so we walk a parallel index into the windowed-like counts kept on `ranked`.
        var windowLikesById = ranked.ToDictionary(r => r.ClipId, r => r.WindowLikes);
        var entries = new List<LeaderboardEntry>(feedItems.Count);
        for (var i = 0; i < feedItems.Count; i++)
        {
            var item = feedItems[i];
            entries.Add(item.ToEntry(i + 1, windowLikesById[item.Id]));
        }
        return entries;
    }

    // Re-stamp LikedByMe on the inner ClipFeedItem of each cached leaderboard entry, the
    // same way trending re-stamps cached anonymous feed items. Preserves rank + windowLikes
    // by zipping the stamped clips back into a fresh entry list.
    internal static async Task<List<LeaderboardEntry>> StampLikedByMeOnEntriesAsync(
        IReadOnlyList<LeaderboardEntry> anonymousEntries,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        if (anonymousEntries.Count == 0) return [];
        var clips = anonymousEntries.Select(e => e.Clip).ToList();
        var stamped = await ClipsReadEndpoints.ApplyLikedByMeAsync(clips, principal, db, ct);
        return [.. anonymousEntries.Zip(stamped, (entry, clip) => entry with { Clip = clip })];
    }

    // Top games for a window: sum windowed likes across each game's public+ready clips.
    // Skips games with zero likes in the window so the response only ranks games that
    // actually have activity. ClipCount counts public+ready clips for the whole catalog
    // (not just clips with likes in the window) so the number matches what GameView's
    // header shows — otherwise the same game would display two different counts.
    //
    // Ordering is hybrid: SQL applies the cheap part of the sort (likes desc, clip-count
    // desc) and trims to a bounded candidate set, then the in-memory pass appends the
    // Ordinal name tiebreak for determinism. Without this, ranking the whole catalog meant
    // materialising every game with ≥1 windowed like just to sort + Take(limit) in memory.
    private static async Task<List<TopGameEntry>> BuildTopGamesAsync(
        DateTimeOffset since,
        int limit,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        // 4× headroom absorbs any plausible tie cluster on (likes, clipCount) at the cut.
        // EF requires Take to be a constant or a captured variable for the parameter.
        var candidateCap = limit * 4;
        var rows = await db.Games.AsNoTracking()
            .Select(g => new
            {
                Game = g,
                WindowLikes = db.Likes.Count(l =>
                    l.CreatedAt >= since
                    && l.Clip.GameId == g.Id
                    && l.Clip.Visibility == ClipVisibilities.Public
                    && l.Clip.Status == ClipStatuses.Ready),
                ClipCount = db.Clips.Count(c =>
                    c.GameId == g.Id && c.Visibility == ClipVisibilities.Public && c.Status == ClipStatuses.Ready),
            })
            .Where(x => x.WindowLikes > 0)
            .OrderByDescending(x => x.WindowLikes)
            .ThenByDescending(x => x.ClipCount)
            .Take(candidateCap)
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
