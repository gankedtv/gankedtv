namespace GankedTV.Api.Data.Entities;

public class Follow
{
    public Guid FollowerId { get; set; }
    public Guid FolloweeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public User Follower { get; set; } = null!;
    public User Followee { get; set; } = null!;
}
