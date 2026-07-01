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
            "Maintenance hosted service started (interval={Interval}, clipThreshold={ClipThreshold}, refreshTokenRetention={Retention}).",
            snapshot.SweepInterval, snapshot.ClipStaleThreshold, snapshot.RefreshTokenRetention);

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
        // Each sweep gets its own scope so a half-failed clip sweep cannot leave a dirty
        // change tracker for the refresh-token sweep that runs after it.
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await SweepOrphanedClipsAsync(scope, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orphaned clip sweep failed");
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            await SweepExpiredRefreshTokensAsync(scope, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh token sweep failed");
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            await SweepExpiredDeviceAuthorizationsAsync(scope, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device authorization sweep failed");
        }
    }

    internal async Task SweepOrphanedClipsAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<GankedTvDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageService>();
        var buckets = _s3.CurrentValue;
        var opts = _options.CurrentValue;
        var threshold = opts.ClipStaleThreshold;
        var cutoff = _clock.GetUtcNow() - threshold;
        // +1 so we can tell whether more remain beyond the cap without a second query.
        var fetchLimit = opts.ClipBatchSize + 1;

        var orphans = await db.Clips
            .Where(c => c.Status == ClipStatuses.Draft && c.CreatedAt < cutoff)
            .OrderBy(c => c.CreatedAt)
            .Take(fetchLimit)
            .ToListAsync(ct);

        if (orphans.Count == 0)
        {
            _logger.LogDebug("No orphaned draft clips older than {Threshold} found.", threshold);
            return;
        }

        var moreRemaining = orphans.Count > opts.ClipBatchSize;
        if (moreRemaining)
        {
            orphans.RemoveAt(orphans.Count - 1);
        }

        // Mark the DB row for deletion first, then attempt the blob cleanup — same row-first
        // ordering as DELETE /clips/{id}. SaveChangesAsync below commits all queued removes
        // in one transaction.
        foreach (var clip in orphans)
        {
            db.Clips.Remove(clip);
            await ClipBlobCleanup.TryDeleteAsync(storage, buckets, clip, _logger, ct);
        }

        await db.SaveChangesAsync(ct);
        if (moreRemaining)
        {
            _logger.LogInformation(
                "Swept {Count} orphaned draft clips (older than {Threshold}); batch cap reached, remainder will be picked up next tick.",
                orphans.Count, threshold);
        }
        else
        {
            _logger.LogInformation(
                "Swept {Count} orphaned draft clips (older than {Threshold}).",
                orphans.Count, threshold);
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
