using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Maintenance;

public sealed class MaintenanceHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<MaintenanceOptions> _options;
    private readonly IOptionsMonitor<S3Options> _s3;
    private readonly TimeProvider _clock;
    private readonly ILogger<MaintenanceHostedService> _logger;

    public MaintenanceHostedService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<MaintenanceOptions> options,
        IOptionsMonitor<S3Options> s3,
        TimeProvider clock,
        ILogger<MaintenanceHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _s3 = s3;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var snapshot = _options.CurrentValue;
        if (!snapshot.Enabled)
        {
            _logger.LogInformation("Maintenance hosted service disabled by configuration; exiting.");
            return;
        }

        // SweepInterval is captured at startup and frozen for the life of the process.
        // The other thresholds are read fresh from IOptionsMonitor each tick, so config
        // reload affects them; changing the interval requires a restart.
        using var timer = new PeriodicTimer(snapshot.SweepInterval);
        _logger.LogInformation(
            "Maintenance hosted service started (interval={Interval}, clipThreshold={ClipThreshold}, failedClipRetention={FailedRetention}, refreshTokenRetention={Retention}).",
            snapshot.SweepInterval, snapshot.ClipStaleThreshold, snapshot.FailedClipRetention, snapshot.RefreshTokenRetention);

        try
        {
            // Run an immediate sweep on startup, then on each tick.
            do
            {
                await RunTickAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutdown — not an error.
        }
    }

    private async Task RunTickAsync(CancellationToken ct)
    {
        await RunSweepAsync(SweepOrphanedClipsAsync, "Orphaned clip sweep", ct);
        await RunSweepAsync(SweepFailedClipsAsync, "Failed clip sweep", ct);
        await RunSweepAsync(SweepExpiredRefreshTokensAsync, "Refresh token sweep", ct);
        await RunSweepAsync(SweepExpiredDeviceAuthorizationsAsync, "Device authorization sweep", ct);
    }

    // Each sweep runs in its own scope so a half-failed sweep can't leave a dirty change tracker for
    // the next one, and its failures are isolated — one sweep throwing must not starve the rest.
    // Cooperative cancellation still propagates so shutdown isn't swallowed as a sweep error.
    private async Task RunSweepAsync(Func<IServiceScope, CancellationToken, Task> sweep, string label, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await sweep(scope, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Sweep} failed", label);
        }
    }

    // Never-completed uploads: a draft older than ClipStaleThreshold whose owner abandoned the
    // wizard. Keyed off created_at (a draft's updated_at never moves past creation).
    internal Task SweepOrphanedClipsAsync(IServiceScope scope, CancellationToken ct)
    {
        var cutoff = _clock.GetUtcNow() - _options.CurrentValue.ClipStaleThreshold;
        return PurgeClipBatchAsync(
            scope,
            db => db.Clips
                .Where(c => c.Status == ClipStatuses.Draft && c.CreatedAt < cutoff)
                .OrderBy(c => c.CreatedAt),
            "orphaned draft",
            ct);
    }

    // Dead clips: anything that landed in 'failed' more than FailedClipRetention ago (all failure
    // reasons). Keyed off updated_at — the moment the pipeline flipped it to 'failed'. This window
    // is also the requeue deadline; see MaintenanceOptions.FailedClipRetention.
    internal Task SweepFailedClipsAsync(IServiceScope scope, CancellationToken ct)
    {
        var cutoff = _clock.GetUtcNow() - _options.CurrentValue.FailedClipRetention;
        return PurgeClipBatchAsync(
            scope,
            db => db.Clips
                .Where(c => c.Status == ClipStatuses.Failed && c.UpdatedAt < cutoff)
                .OrderBy(c => c.UpdatedAt),
            "failed",
            ct);
    }

    // Deletes one capped batch of clips — DB row first, then best-effort S3 blob cleanup (video +
    // JIT stream cache + thumbnail), same row-first ordering as DELETE /clips/{id}. The caller
    // supplies an already-filtered+ordered query against the passed-in context so the loaded
    // entities are change-tracked for removal. ClipBatchSize caps the batch; the remainder (if any)
    // is picked up on the next tick.
    private async Task PurgeClipBatchAsync(
        IServiceScope scope,
        Func<GankedTvDbContext, IQueryable<Clip>> buildCandidates,
        string label,
        CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<GankedTvDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageService>();
        var buckets = _s3.CurrentValue;
        var batchSize = _options.CurrentValue.ClipBatchSize;

        // +1 so we can tell whether more remain beyond the cap without a second query.
        var batch = await buildCandidates(db).Take(batchSize + 1).ToListAsync(ct);
        if (batch.Count == 0)
        {
            _logger.LogDebug("No {Label} clips to sweep.", label);
            return;
        }

        var moreRemaining = batch.Count > batchSize;
        if (moreRemaining)
        {
            batch.RemoveAt(batch.Count - 1);
        }

        foreach (var clip in batch)
        {
            db.Clips.Remove(clip);
            await ClipBlobCleanup.TryDeleteAsync(storage, buckets, clip, _logger, ct);
        }

        await db.SaveChangesAsync(ct);
        if (moreRemaining)
        {
            _logger.LogInformation(
                "Swept {Count} {Label} clips; batch cap reached, remainder will be picked up next tick.",
                batch.Count, label);
        }
        else
        {
            _logger.LogInformation("Swept {Count} {Label} clips.", batch.Count, label);
        }
    }

    internal async Task SweepExpiredRefreshTokensAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<GankedTvDbContext>();
        var retention = _options.CurrentValue.RefreshTokenRetention;
        var cutoff = _clock.GetUtcNow() - retention;

        var deleted = await db.RefreshTokens
            .Where(t => t.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(ct);

        if (deleted == 0)
        {
            _logger.LogDebug("No refresh token rows older than {Retention} past expiry.", retention);
            return;
        }

        _logger.LogInformation(
            "Swept {Count} refresh token rows expired more than {Retention} ago.",
            deleted, retention);
    }

    // Device-authorization rows are short-lived (10 min) and single-use; once past expiry they're
    // dead weight (a consumed/approved row is deleted on token exchange). Delete on expiry — no
    // retention window needed, unlike refresh tokens which are kept for audit.
    internal async Task SweepExpiredDeviceAuthorizationsAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<GankedTvDbContext>();
        var now = _clock.GetUtcNow();

        var deleted = await db.DeviceAuthorizations
            .Where(d => d.ExpiresAt < now)
            .ExecuteDeleteAsync(ct);

        if (deleted == 0)
        {
            _logger.LogDebug("No expired device authorization rows found.");
            return;
        }

        _logger.LogInformation("Swept {Count} expired device authorization rows.", deleted);
    }
}
