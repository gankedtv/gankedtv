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
    /// to fetch the next page of replies, and <paramref name="likedByMe"/>. The defaults mean a
    /// forgotten call site ships <c>likedByMe: false</c> instead of failing the build, so each
    /// one has a test.
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
