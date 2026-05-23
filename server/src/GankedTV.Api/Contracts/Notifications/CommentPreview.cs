namespace GankedTV.Api.Contracts.Notifications;

// Body is nullable to mirror CommentItem — a soft-deleted comment surfaces as null so the UI
// can render a `[deleted]` placeholder without losing the row's anchor.
public sealed record CommentPreview(Guid Id, string? Body);
