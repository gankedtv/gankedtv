namespace GankedTV.Api.Notifications;

/// <summary>
/// Records an in-app notification for the recipient. Implementations are expected to use the
/// caller's <c>GankedTvDbContext</c> so the insert joins any open transaction (e.g. the
/// like / unlike counter update). Self-actions are dropped silently — every call site is free
/// to invoke this without first checking <c>recipient != actor</c>.
/// </summary>
public interface INotificationService
{
    Task RecordAsync(
        Guid recipientId,
        Guid actorId,
        string type,
        Guid? clipId,
        Guid? commentId,
        CancellationToken ct);
}
