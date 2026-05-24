using NpgsqlTypes;

namespace GankedTV.Api.Data.Entities;

public class Clip
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public int? GameId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    // Key of the playable master in the private clips bucket. Initially the raw upload;
    // after the compress stage runs it points at the compressed master (and the original
    // object is deleted), so there is exactly one video file per clip on disk.
    public required string VideoKey { get; set; }
    public string? ThumbnailKey { get; set; }
    // Codec of the stored master ("av1", "h264", …). Lets the web player decide whether to
    // play VideoKey directly or fall back to a just-in-time H.264 stream. Null until the
    // compress stage has run.
    public string? VideoCodec { get; set; }
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

    // Set on rows ingested via POST /clips/import — preserves the original Medal.tv /
    // YouTube URL the fetcher pulled from. Stays populated after the clip reaches 'ready'
    // (cheap audit trail + lets us short-circuit a re-import of the same URL).
    public string? ImportSourceUrl { get; set; }

    // Machine-readable code set by the worker when status flips to 'failed'. Lets the web
    // wizard surface a specific message ("clip too long") instead of a generic "failed".
    // Null while in flight or once the clip lands 'ready'. Short codes — never raw stderr.
    public string? FailureReason { get; set; }

    // Postgres-managed `tsvector` (GENERATED ALWAYS AS … STORED). Populated by the DB from
    // title (weight A) + description (weight B); never set by application code. Used by
    // SearchEndpoints for full-text matching/ranking against `plainto_tsquery('simple', q)`.
    public NpgsqlTsVector SearchVector { get; private set; } = null!;

    public User User { get; set; } = null!;
    public Game? Game { get; set; }
    public ICollection<Like> Likes { get; set; } = [];
    public ICollection<ClipTag> ClipTags { get; set; } = [];
}
