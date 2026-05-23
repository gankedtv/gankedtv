using GankedTV.Api.Contracts.Clips;

namespace GankedTV.Api.Contracts.Notifications;

public sealed record NotificationItem(
    Guid Id,
    string Type,
    AuthorSummary Actor,
    ClipSummary? Clip,
    CommentPreview? Comment,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);
