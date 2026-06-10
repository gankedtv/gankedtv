using System.Linq.Expressions;

namespace GankedTV.Api.Pagination;

/// <summary>
/// Shared boilerplate for <see cref="KeysetCursor"/>-based pagination: the composite
/// <c>(CreatedAt, Id)</c> keyset predicate and the fetch-one-extra page slicing. The id is a
/// selector expression because not every feed keysets on a primary key (follow lists keyset
/// on the projected side of the row). Includes, ordering, and projection stay at the call
/// site — each endpoint keeps its own query shape.
/// </summary>
public static class KeysetPagination
{
    /// <summary>Descending feeds: rows strictly older than the cursor position.</summary>
    public static IQueryable<T> WhereKeysetBefore<T>(
        this IQueryable<T> source,
        Expression<Func<T, DateTimeOffset>> createdAt,
        Expression<Func<T, Guid>> id,
        DateTimeOffset cursorCreatedAt,
        Guid cursorId) =>
        source.Where(BuildPredicate(createdAt, id, cursorCreatedAt, cursorId, before: true));

    /// <summary>Ascending feeds (chronological reply threads): rows strictly newer.</summary>
    public static IQueryable<T> WhereKeysetAfter<T>(
        this IQueryable<T> source,
        Expression<Func<T, DateTimeOffset>> createdAt,
        Expression<Func<T, Guid>> id,
        DateTimeOffset cursorCreatedAt,
        Guid cursorId) =>
        source.Where(BuildPredicate(createdAt, id, cursorCreatedAt, cursorId, before: false));

    /// <summary>
    /// Slices a <c>Take(limit + 1)</c> result into the page plus the next-cursor token
    /// (null when this was the last page).
    /// </summary>
    public static (IReadOnlyList<T> Page, string? NextCursor) TakePage<T>(
        List<T> rows,
        int limit,
        Func<T, DateTimeOffset> createdAt,
        Func<T, Guid> id)
    {
        if (rows.Count <= limit)
        {
            return (rows, null);
        }

        var page = rows.GetRange(0, limit);
        var last = page[^1];
        return (page, KeysetCursor.Build(createdAt(last), id(last)));
    }

    // Cursor values live in a captured holder and enter the tree as member accesses (not
    // Expression.Constant) so EF funcletizes them into SQL parameters — keeping the query
    // plan cacheable across cursor values, exactly like a closure in an inline lambda.
    private sealed record CursorValues(DateTimeOffset CreatedAt, Guid Id);

    private static Expression<Func<T, bool>> BuildPredicate<T>(
        Expression<Func<T, DateTimeOffset>> createdAt,
        Expression<Func<T, Guid>> id,
        DateTimeOffset cursorCreatedAt,
        Guid cursorId,
        bool before)
    {
        var parameter = Expression.Parameter(typeof(T), "row");
        var createdBody = ReplaceParameter(createdAt.Body, createdAt.Parameters[0], parameter);
        var idBody = ReplaceParameter(id.Body, id.Parameters[0], parameter);

        var cursor = Expression.Constant(new CursorValues(cursorCreatedAt, cursorId));
        var cursorCreated = Expression.Property(cursor, nameof(CursorValues.CreatedAt));
        var cursorIdValue = Expression.Property(cursor, nameof(CursorValues.Id));

        var compareTo = Expression.Call(
            idBody, typeof(Guid).GetMethod(nameof(Guid.CompareTo), [typeof(Guid)])!, cursorIdValue);
        var zero = Expression.Constant(0);

        var createdComparison = before
            ? Expression.LessThan(createdBody, cursorCreated)
            : Expression.GreaterThan(createdBody, cursorCreated);
        var idComparison = before
            ? Expression.LessThan(compareTo, zero)
            : Expression.GreaterThan(compareTo, zero);

        var tieBreak = Expression.AndAlso(Expression.Equal(createdBody, cursorCreated), idComparison);
        return Expression.Lambda<Func<T, bool>>(
            Expression.OrElse(createdComparison, tieBreak), parameter);
    }

    private static Expression ReplaceParameter(Expression body, ParameterExpression from, ParameterExpression to) =>
        new ParameterReplacer(from, to).Visit(body);

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == from ? to : base.VisitParameter(node);
    }
}
