using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Endpoints;
using GankedTV.Api.Pagination;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Services.Feeds;

internal sealed record ForYouPage(IReadOnlyList<Clip> Clips, string? NextCursor);

/// <summary>
/// Builds the personalized For You feed: the public+ready feed re-ordered into relevance tiers
/// (0 = followed author, 1 = liked game, 2 = everything else), newest-first within each tier.
/// Owns tier construction, the cross-tier page fill, and the tiered cursor. Projection
/// (thumbnail signing + likedByMe) stays at the call site, so no personalized data is built
/// here — mirroring <see cref="RankedFeedBuilder"/>'s split of concerns.
/// </summary>
internal static class ForYouFeedBuilder
{
    /// <summary>
    /// Returns <c>null</c> when the caller has no follows AND no liked games — the endpoint then
    /// serves the shared latest path (cold-start), so a signal-less user gets results identical
    /// to Latest (including the cached first page).
    /// </summary>
    internal static async Task<ForYouPage?> BuildPageAsync(
        GankedTvDbContext db,
        Guid me,
        int? gameId,
        string? cursor,
        int? limit,
        CancellationToken ct)
    {
        var followedAuthorIds = await db.Follows.AsNoTracking()
            .Where(f => f.FollowerId == me)
            .Select(f => f.FolloweeId)
            .ToListAsync(ct);

        // A game is "liked" if it appears on >=1 clip the user has liked (there is no game-follow
        // table). The liked clip's own visibility is irrelevant — a like is a like.
        var likedGameIds = await db.Clips.AsNoTracking()
            .Where(c => c.GameId != null && db.Likes.Any(l => l.UserId == me && l.ClipId == c.Id))
            .Select(c => c.GameId!.Value)
            .Distinct()
            .ToListAsync(ct);

        if (followedAuthorIds.Count == 0 && likedGameIds.Count == 0)
        {
            return null;
        }

        var clampedLimit = Math.Clamp(
            limit ?? ClipsReadEndpoints.FeedDefaultLimit, 1, ClipsReadEndpoints.FeedMaxLimit);

        // Every public+ready clip lands in exactly one tier (highest wins), so tiers never
        // overlap and no cross-tier dedup pass is needed. `Contains` translates to SQL `IN (...)`
        // — acceptable at current scale; a heavy-follow user could move to correlated EXISTS
        // later without changing the contract (mirrors the trending feed's ~10k revisit note).
        IQueryable<Clip> TierQuery(int tier)
        {
            var q = db.Clips.AsNoTracking().WherePublicReady();
            // The Home game pills narrow every tier to one game, so the personalised ranking
            // still applies but only within that game (followed authors first, then liked-game
            // and backfill of the same game). Mirrors the latest path's gameId filter.
            if (gameId is int gid)
            {
                q = q.Where(c => c.GameId == gid);
            }
            return tier switch
            {
                0 => q.Where(c => followedAuthorIds.Contains(c.UserId)),
                1 => q.Where(c => c.GameId != null
                                  && likedGameIds.Contains(c.GameId.Value)
                                  && !followedAuthorIds.Contains(c.UserId)),
                _ => q.Where(c => !followedAuthorIds.Contains(c.UserId)
                                  && (c.GameId == null || !likedGameIds.Contains(c.GameId.Value))),
            };
        }

        var hasCursor = TieredKeysetCursor.TryParse(cursor, out var startTier, out var cursorCreatedAt, out var cursorId);
        // Clamp defends against a parseable-but-out-of-range tier; a missing/corrupt cursor already
        // yields startTier=0 from TryParse.
        startTier = Math.Clamp(startTier, 0, 2);

        // Fetch one extra across the walked tiers to detect whether a further page exists.
        var need = clampedLimit + 1;
        var collected = new List<(Clip Clip, int Tier)>(need);

        for (var tier = startTier; tier <= 2 && collected.Count < need; tier++)
        {
            var q = TierQuery(tier);
            // Keyset applies ONLY on the starting tier: lower tiers were fully drained on earlier
            // pages; higher tiers start from their newest row.
            if (hasCursor && tier == startTier)
            {
                q = q.WhereKeysetBefore(c => c.CreatedAt, c => c.Id, cursorCreatedAt, cursorId);
            }

            var rows = await q
                .OrderByDescending(c => c.CreatedAt)
                .ThenByDescending(c => c.Id)
                .IncludeFeedRelations()
                .Take(need - collected.Count)
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                collected.Add((row, tier));
            }
        }

        var hasMore = collected.Count > clampedLimit;
        var pageRows = hasMore ? collected.GetRange(0, clampedLimit) : collected;
        var clips = pageRows.Select(r => r.Clip).ToList();

        // The row's tier is recorded during the fill, so the cursor pins the correct tier even
        // when the page ends exactly on a tier boundary.
        string? nextCursor = null;
        if (hasMore)
        {
            var last = pageRows[^1];
            nextCursor = TieredKeysetCursor.Build(last.Tier, last.Clip.CreatedAt, last.Clip.Id);
        }

        return new ForYouPage(clips, nextCursor);
    }
}
