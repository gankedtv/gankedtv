using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Data;

/// <summary>
/// Holds DB-backed background work until the database has no pending EF migrations. Only the
/// app-host api migrates (<see cref="DatabaseMigrator"/>); any other host running the same image —
/// the split-deployment GPU encoder — would otherwise start querying a schema that doesn't have the
/// new columns yet whenever it picks up a new image before the app host has migrated. On the
/// migrating host the first check already passes, since migrations run before hosted services start.
/// </summary>
public interface ISchemaReadyGate
{
    Task WaitUntilReadyAsync(CancellationToken ct);
}

public interface IPendingMigrationsProbe
{
    Task<IReadOnlyList<string>> GetPendingAsync(CancellationToken ct);
}

public sealed class EfPendingMigrationsProbe(GankedTvDbContext db) : IPendingMigrationsProbe
{
    public async Task<IReadOnlyList<string>> GetPendingAsync(CancellationToken ct) =>
        (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
}

public sealed class SchemaReadyGate : ISchemaReadyGate
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SchemaReadyGate> _logger;
    private readonly TimeSpan _pollInterval;
    private volatile bool _ready;
    // Shared across callers: every gated service waits on the same schema, so one warning (and
    // one "resuming" line) says it all instead of one per worker.
    private int _waitAnnounced;
    private int _resumeAnnounced;

    public SchemaReadyGate(IServiceScopeFactory scopeFactory, ILogger<SchemaReadyGate> logger)
        : this(scopeFactory, logger, DefaultPollInterval)
    {
    }

    internal SchemaReadyGate(IServiceScopeFactory scopeFactory, ILogger<SchemaReadyGate> logger, TimeSpan pollInterval)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _pollInterval = pollInterval;
    }

    public async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        while (!_ready)
        {
            ct.ThrowIfCancellationRequested();

            string reason;
            Exception? error = null;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var pending = await scope.ServiceProvider
                    .GetRequiredService<IPendingMigrationsProbe>()
                    .GetPendingAsync(ct);
                if (pending.Count == 0)
                {
                    _ready = true;
                    if (Volatile.Read(ref _waitAnnounced) == 1 && Interlocked.Exchange(ref _resumeAnnounced, 1) == 0)
                    {
                        _logger.LogInformation("Database schema is current; resuming background work.");
                    }
                    return;
                }
                reason = $"{pending.Count} pending migration(s): {string.Join(", ", pending)}";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Unreachable DB at boot: wait it out rather than let each worker error per tick.
                reason = "could not check migrations";
                error = ex;
            }

            if (Interlocked.Exchange(ref _waitAnnounced, 1) == 0)
            {
                _logger.LogWarning(error,
                    "Background work paused until the database schema is current ({Reason}); rechecking every {Interval}.",
                    reason, _pollInterval);
            }

            await Task.Delay(_pollInterval, ct);
        }
    }
}

public static class SchemaReadyGateExtensions
{
    /// <summary>
    /// Awaits the registered <see cref="ISchemaReadyGate"/>, or completes immediately when none is
    /// registered (unit tests that build a minimal container for one service).
    /// </summary>
    public static Task WaitForSchemaAsync(this IServiceScopeFactory scopeFactory, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var gate = scope.ServiceProvider.GetService<ISchemaReadyGate>();
        return gate?.WaitUntilReadyAsync(ct) ?? Task.CompletedTask;
    }
}
