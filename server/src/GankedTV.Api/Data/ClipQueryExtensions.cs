using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Data;

public static class ClipQueryExtensions
{
    /// <summary>
    /// Eager-load every relation a <see cref="Clip"/> projection (feed item or detail)
    /// touches: author, game, and the tag join. Centralised so additions stay in one
    /// place — <see cref="Contracts.Clips.ClipMappings.ToFeedItem"/> and
    /// <see cref="Contracts.Clips.ClipMappings.ToDetail"/> both silently fall back to
    /// an empty collection if these Includes are missing, hiding the bug.
    /// </summary>
    public static IQueryable<Clip> IncludeFeedRelations(this IQueryable<Clip> query) =>
        query
            .Include(c => c.User)
            .Include(c => c.Game)
            .Include(c => c.ClipTags).ThenInclude(ct => ct.Tag);

    /// <summary>
    /// The canonical "publicly watchable" predicate shared by every feed, search, and
    /// leaderboard query. Only works on a top-level clip query — predicates nested inside
    /// projection expression trees must inline the same two comparisons (EF cannot
    /// translate method calls there).
    /// </summary>
    public static IQueryable<Clip> WherePublicReady(this IQueryable<Clip> query) =>
        query.Where(c => c.Visibility == ClipVisibilities.Public && c.Status == ClipStatuses.Ready);
}
