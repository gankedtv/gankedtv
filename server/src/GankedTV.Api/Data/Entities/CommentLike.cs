namespace GankedTV.Api.Data.Entities;

public class CommentLike
{
    public Guid UserId { get; set; }
    public Guid CommentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public User User { get; set; } = null!;
    public Comment Comment { get; set; } = null!;
}
