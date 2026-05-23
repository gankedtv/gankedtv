namespace GankedTV.Api.Contracts.Comments;

public sealed record CommentListResponse(IReadOnlyList<CommentItem> Items, string? NextCursor);
