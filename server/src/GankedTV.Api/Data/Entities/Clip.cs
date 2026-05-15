namespace GankedTV.Api.Data.Entities;

public class Clip
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public int? GameId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public required string VideoKey { get; set; }
    public string? ThumbnailKey { get; set; }
    public required string ShareCode { get; set; }
    public short? DurationSecs { get; set; }
    public short? Width { get; set; }
    public short? Height { get; set; }
    public long? FileSizeBytes { get; set; }
    public int ViewCount { get; set; }
    public int LikeCount { get; set; }
    public string Status { get; set; } = "processing";
    public string Visibility { get; set; } = "public";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Lease columns for the media-job worker. ProcessingStartedAt is set when the worker
    // claims a row and cleared on success; ProcessingAttempts increments on each claim and
    // gates the row out of the queue once it exceeds the configured maximum.
    public DateTimeOffset? ProcessingStartedAt { get; set; }
    public int ProcessingAttempts { get; set; }

    public User User { get; set; } = null!;
    public Game? Game { get; set; }
    public ICollection<Like> Likes { get; set; } = [];
}
