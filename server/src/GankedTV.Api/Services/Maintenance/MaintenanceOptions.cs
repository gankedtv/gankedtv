namespace GankedTV.Api.Services.Maintenance;

public sealed class MaintenanceOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan ClipStaleThreshold { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan RefreshTokenRetention { get; set; } = TimeSpan.FromDays(30);

    // How long a clip may sit in 'failed' before the sweep purges it (DB row + S3 blobs), measured
    // from updated_at (when it entered 'failed'). ALL failure reasons are purged, so this doubles as
    // a recovery deadline: a retryable clip must be requeued (POST /admin/clips/media/requeue) within
    // this window or it is deleted. There is no separate on/off toggle — raise it very high to
    // effectively disable the failed-clip sweep.
    public TimeSpan FailedClipRetention { get; set; } = TimeSpan.FromDays(3);

    // Hard cap on rows a clip sweep loads in one tick. Protects memory if a backlog accumulates
    // (e.g. the sweep silently fails for days, then the bug is fixed). The remainder is picked up
    // on the next tick. Shared by the orphaned-draft and failed-clip sweeps.
    public int ClipBatchSize { get; set; } = 1000;
}
