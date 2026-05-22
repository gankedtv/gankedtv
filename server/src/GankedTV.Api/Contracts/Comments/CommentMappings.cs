using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Data.Entities;

namespace GankedTV.Api.Contracts.Comments;

public static class CommentMappings
{
    private static readonly IReadOnlyList<CommentItem> NoReplies = [];

    /// <summary>
    /// Maps a comment to its API shape. Soft-deleted comments surface a <c>null</c> body and
    /// <c>Deleted = true</c>. The caller supplies <paramref name="replyCount"/> and the
    /// inline <paramref name="replies"/> page (both default to empty for a reply or a
    /// freshly-created comment).
    /// </summary>
    public static CommentItem ToItem(
        this Comment comment,
        int replyCount = 0,
        IReadOnlyList<CommentItem>? replies = null) =>
        new(
            comment.Id,
            comment.DeletedAt is null ? comment.Body : null,
            comment.User.ToAuthorSummary(),
            comment.ParentId,
            comment.CreatedAt,
            replyCount,
            replies ?? NoReplies,
            comment.DeletedAt is not null);
}
