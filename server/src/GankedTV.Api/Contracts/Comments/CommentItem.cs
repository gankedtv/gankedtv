using GankedTV.Api.Contracts.Clips;

namespace GankedTV.Api.Contracts.Comments;

/// <summary>
/// A single comment. For soft-deleted comments <see cref="Body"/> is <c>null</c> and
/// <see cref="Deleted"/> is <c>true</c> so the UI can render a <c>[deleted]</c> placeholder
/// while keeping the thread structure intact. <see cref="Replies"/> carries the first page of
/// inline replies for a top-level comment (empty for replies themselves);
/// <see cref="RepliesNextCursor"/> is the cursor to fetch the next page via
/// <c>GET /comments/{id}/replies</c>, or <c>null</c> when the inline preview is exhaustive.
/// </summary>
public sealed record CommentItem(
    Guid Id,
    string? Body,
    AuthorSummary Author,
    Guid? ParentId,
    DateTimeOffset CreatedAt,
    int ReplyCount,
    IReadOnlyList<CommentItem> Replies,
    string? RepliesNextCursor,
    bool Deleted,
    int LikeCount,
    bool LikedByMe);
