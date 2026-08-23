namespace GankedTV.Api.Data.Entities;

public class Comment
{
    public Guid Id { get; set; }
    public Guid ClipId { get; set; }
    public Guid UserId { get; set; }

    // null = top-level comment; set = reply. Threads are flat two-level: a reply's parent is
    // always a top-level comment (ParentId.ParentId is null), enforced at the endpoint layer.
    public Guid? ParentId { get; set; }
    public required string Body { get; set; }

    // Denormalised like Clip.LikeCount: the read path renders whole threads plus inline reply
    // previews, so a per-row COUNT would grow with the page.
    public int LikeCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Soft-delete: preserves thread structure so replies don't collapse. A deleted comment
    // renders as a `[deleted]` placeholder with Body surfaced as null in the API.
    public DateTimeOffset? DeletedAt { get; set; }

    public Clip Clip { get; set; } = null!;
    public User User { get; set; } = null!;
    public Comment? Parent { get; set; }
    public ICollection<Comment> Replies { get; set; } = [];
    public ICollection<CommentLike> Likes { get; set; } = [];
}
