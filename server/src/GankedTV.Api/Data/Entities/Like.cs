namespace GankedTV.Api.Data.Entities;

public class Like
{
    public Guid UserId { get; set; }
    public Guid ClipId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public User User { get; set; } = null!;
    public Clip Clip { get; set; } = null!;
}
