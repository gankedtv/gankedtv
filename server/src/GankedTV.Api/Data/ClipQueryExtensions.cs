using System.Linq.Expressions;
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

    /// <summary>
    /// The canonical link-reachability rule for every by-id/share-code read path (detail,
    /// share, stream, comments, likes): public and unlisted clips resolve for anyone,
    /// private and moderator-hidden ones only for their owner. Deliberately says nothing
    /// about <see cref="Clip.Status"/> — call sites that require a ready clip keep that
    /// check themselves.
    /// </summary>
    public static IQueryable<Clip> WhereVisibleTo(this IQueryable<Clip> query, Guid? viewerId) =>
        query.Where(VisibleTo(viewerId));

    /// <summary>
    /// Expression form of <see cref="WhereVisibleTo"/> for queries that reach the clip
    /// through a projection (e.g. <c>comment.Clip</c>) where an extension method cannot
    /// be translated.
    /// </summary>
    public static Expression<Func<Clip, bool>> VisibleTo(Guid? viewerId) =>
        c => (c.Visibility != ClipVisibilities.Private && c.Visibility != ClipVisibilities.Hidden)
            || c.UserId == viewerId;

    /// <summary>
    /// Negation of <see cref="VisibleTo"/>, built from the same expression tree so the two
    /// can never drift. Used by the comments endpoints' existence-oracle probes, which
    /// answer "does this clip exist AND is it hidden from the viewer" in one query.
    /// </summary>
    public static Expression<Func<Clip, bool>> NotVisibleTo(Guid? viewerId)
    {
        var visible = VisibleTo(viewerId);
        return Expression.Lambda<Func<Clip, bool>>(Expression.Not(visible.Body), visible.Parameters);
    }
}
