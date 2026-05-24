namespace GankedTV.Api.Data.Entities;

// Coordinates just-in-time (watch-time) transcoding of a clip's compressed master into a
// cached H.264 HLS ladder for devices that can't decode the master's codec (e.g. AV1).
// One row per clip that currently has (or is having) a cached rendition built. The row is
// the work queue; the actual readiness source-of-truth is whether the master.m3u8 object
// exists in the stream-cache bucket (which auto-expires via a lifecycle rule). A row is
// created on a cache miss and deleted once the rendition is cached.
public class ClipStreamJob
{
    public Guid ClipId { get; set; }

    // pending → claimable; failed → exhausted retries (the endpoint surfaces an error
    // instead of looping). A successful build deletes the row rather than marking it ready.
    public string Status { get; set; } = ClipStreamJobStatuses.Pending;

    // Lease columns, mirroring the clips media-job worker: ProcessingStartedAt is set on
    // claim and cleared on release; ProcessingAttempts gates the row out after MaxAttempts.
    public DateTimeOffset? ProcessingStartedAt { get; set; }
    public int ProcessingAttempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Clip Clip { get; set; } = null!;
}

public static class ClipStreamJobStatuses
{
    public const string Pending = "pending";
    public const string Failed = "failed";
}
