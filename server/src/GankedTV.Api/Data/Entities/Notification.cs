namespace GankedTV.Api.Data.Entities;

public class Notification
{
    public Guid Id { get; set; }
    public Guid RecipientId { get; set; }
    public Guid ActorId { get; set; }

    // 'like' | 'comment' | 'comment_like' | 'follow'. Kept as a short string to mirror
    // Clip.Status / Visibility — an EF enum mapping would add ceremony for a handful of values
    // the UI also treats as strings.
    public required string Type { get; set; }

    // Populated on like, comment and comment_like notifications (so the UI can link to the
    // clip); null on follow.
    public Guid? ClipId { get; set; }

    // Populated on comment and comment_like notifications.
    public Guid? CommentId { get; set; }

    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public User Recipient { get; set; } = null!;
    public User Actor { get; set; } = null!;
    public Clip? Clip { get; set; }
    public Comment? Comment { get; set; }
}
