using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Data.Entities;

namespace GankedTV.Api.Contracts.Notifications;

public static class NotificationMappings
{
    /// <summary>
    /// Maps a notification entity to its API shape. Callers must <c>.Include(n =&gt; n.Actor)</c>
    /// and (where present) <c>.Include(n =&gt; n.Clip)</c> / <c>.Include(n =&gt; n.Comment)</c>
    /// — an un-included nav silently surfaces as <c>null</c>, masking bugs.
    /// </summary>
    public static NotificationItem ToItem(this Notification n) =>
        new(
            n.Id,
            n.Type,
            n.Actor.ToAuthorSummary(),
            n.Clip is null ? null : new ClipSummary(n.Clip.Id, n.Clip.ShareCode, n.Clip.Title),
            n.Comment is null
                ? null
                : new CommentPreview(n.Comment.Id, n.Comment.DeletedAt is null ? n.Comment.Body : null),
            n.CreatedAt,
            n.ReadAt);
}
