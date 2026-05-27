using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Services.Feeds;

/// <summary>
/// Shared post-candidate pipeline for ranked clip feeds (leaderboards + trending). Both
/// callers share the same shape: materialise a candidate set, rank in memory, hydrate the
/// top IDs with feed includes, preserve the rank order. The differences (candidate query,
/// scoring expression, projection) stay at the call site where they're visible.
/// </summary>
internal static class RankedFeedBuilder
{
    /// <summary>
    /// Hydrates a pre-ranked list of clip IDs with feed includes, preserving the input
    /// order and dropping any IDs that don't come back (deleted mid-flight, or filtered
    /// out by the optional re-applied predicate).
    /// </summary>
    /// <param name="orderedIds">Clip IDs in rank order — the result follows this order.</param>
    /// <param name="reapplyPublicReadyFilter">
    /// True for leaderboards: re-checks <c>visibility='public' AND status='ready'</c> at
    /// hydrate time as a belt-and-braces guard against the micro-race between candidate
    /// selection and hydration (a leaderboard surfacing a private clip is more confusing
    /// than a trending page doing the same — trending self-heals on the next request via
    /// its short TTL, so it opts out).
    /// </param>
    public static async Task<List<Clip>> HydrateOrderedAsync(
        IReadOnlyList<Guid> orderedIds,
        GankedTvDbContext db,
        bool reapplyPublicReadyFilter,
        CancellationToken ct)
    {
        if (orderedIds.Count == 0) return [];

        IQueryable<Clip> query = db.Clips.AsNoTracking().Where(c => orderedIds.Contains(c.Id));
        if (reapplyPublicReadyFilter)
        {
            query = query.Where(c => c.Visibility == "public" && c.Status == "ready");
        }
        var hydrated = await query.IncludeFeedRelations().ToListAsync(ct);

        // EF's IN(...) doesn't preserve input order, so re-sort by walking orderedIds.
        // TryGetValue silently skips IDs that lost their row mid-flight.
        var byId = hydrated.ToDictionary(c => c.Id);
        var ordered = new List<Clip>(orderedIds.Count);
        foreach (var id in orderedIds)
        {
            if (byId.TryGetValue(id, out var clip)) ordered.Add(clip);
        }
        return ordered;
    }
}
