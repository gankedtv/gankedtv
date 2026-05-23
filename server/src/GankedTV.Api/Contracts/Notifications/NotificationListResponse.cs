namespace GankedTV.Api.Contracts.Notifications;

public sealed record NotificationListResponse(IReadOnlyList<NotificationItem> Items, string? NextCursor);
