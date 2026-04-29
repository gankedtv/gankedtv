namespace GankedTV.Api.Services.Maintenance;

public sealed class MaintenanceOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan ClipStaleThreshold { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan RefreshTokenRetention { get; set; } = TimeSpan.FromDays(30);
    // Hard cap on rows the orphan-clip sweep loads in one tick. Protects memory if a
    // backlog accumulates (e.g. the sweep silently fails for days, then the bug is fixed).
    // The remainder is picked up on the next tick.
    public int ClipBatchSize { get; set; } = 1000;
}
