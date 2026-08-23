using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Data.Entities;

namespace GankedTV.Api.Contracts.Comments;

public static class CommentMappings
{
    private static readonly IReadOnlyList<CommentItem> NoReplies = [];

    /// <summary>
    /// Maps a comment to its API shape. Soft-deleted comments surface a <c>null</c> body and
    /// <c>Deleted = true</c>. The caller supplies <paramref name="replyCount"/>, the inline
    /// <paramref name="replies"/> page, and <paramref name="repliesNextCursor"/> — the cursor
    /// to fetch the next page of replies (or <c>null</c> when the preview is exhaustive), and
    /// <paramref name="likedByMe"/>. All default to empty/null/false for a reply or a freshly-
    /// created comment. The defaults exist so a new call site compiles — which also means a
    /// forgotten one silently ships <c>likedByMe: false</c> rather than failing the build;
    /// every call site is covered by a test for that reason.
    /// </summary>
    public static CommentItem ToItem(
        this Comment comment,
        int replyCount = 0,
        IReadOnlyList<CommentItem>? replies = null,
        string? repliesNextCursor = null,
        bool likedByMe = false) =>
        new(
            comment.Id,
            comment.DeletedAt is null ? comment.Body : null,
            comment.User.ToAuthorSummary(),
            comment.ParentId,
            comment.CreatedAt,
            replyCount,
            replies ?? NoReplies,
            repliesNextCursor,
            comment.DeletedAt is not null,
            comment.LikeCount,
            likedByMe);
}
