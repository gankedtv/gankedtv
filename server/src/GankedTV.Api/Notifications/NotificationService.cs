using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;

namespace GankedTV.Api.Notifications;

public class NotificationService(GankedTvDbContext db) : INotificationService
{
    public async Task RecordAsync(
        Guid recipientId,
        Guid actorId,
        string type,
        Guid? clipId,
        Guid? commentId,
        CancellationToken ct)
    {
        // Self-actions never produce a notification. The DB also has a CHECK constraint to
        // guard against a buggy call site bypassing this filter.
        if (recipientId == actorId)
        {
            return;
        }

        db.Notifications.Add(new Notification
        {
            RecipientId = recipientId,
            ActorId = actorId,
            Type = type,
            ClipId = clipId,
            CommentId = commentId,
        });
        await db.SaveChangesAsync(ct);
    }
}
