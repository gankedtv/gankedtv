using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Igdb;

/// <summary>
/// Periodically re-syncs the games catalog from IGDB (new popular games, changed cover art,
/// renamed auto-imported games) by running <see cref="IGameCatalogImporter"/> on a timer.
/// Opt-in (<see cref="IgdbOptions.SyncEnabled"/>) and a no-op without credentials. Mirrors the
/// MaintenanceHostedService pattern: immediate run on startup, then every <c>SyncInterval</c>.
/// </summary>
public sealed class IgdbSyncHostedService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<IgdbOptions> options,
    ILogger<IgdbSyncHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var snapshot = options.CurrentValue;
        if (!snapshot.SyncEnabled)
        {
            logger.LogInformation("IGDB sync hosted service disabled by configuration; exiting.");
            return;
        }
        if (!snapshot.IsConfigured)
        {
            logger.LogInformation("IGDB sync hosted service idle: IGDB credentials not configured.");
            return;
        }

        // Interval is captured at startup and frozen for the life of the process (a change
        // requires a restart) — same contract as MaintenanceHostedService.SweepInterval.
        using var timer = new PeriodicTimer(snapshot.SyncInterval);
        logger.LogInformation("IGDB sync hosted service started (interval={Interval}).", snapshot.SyncInterval);

        try
        {
            // Run once on startup, then on each tick.
            do
            {
                await RunSyncAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutdown — not an error.
        }
    }

    private async Task RunSyncAsync(CancellationToken ct)
    {
        // Own scope per sweep so a half-failed sync can't leave a dirty change tracker behind.
        try
        {
            using var scope = scopeFactory.CreateScope();
            var importer = scope.ServiceProvider.GetRequiredService<IGameCatalogImporter>();
            await importer.RunAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "IGDB catalog sync failed");
        }
    }
}
