using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Data;

/// <summary>
/// Holds DB-backed background work until the database has no pending EF migrations, so a host that
/// doesn't migrate never queries a schema older than its code.
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
    public async Task<IReadOnlyList<string>> GetPendingAsync(CancellationToken ct)
    {
        // Without a history table EF logs the failed history SELECT at Error level on every call,
        // which Sentry would turn into an event per poll. IHistoryRepository.ExistsAsync can't
        // guard it: on Npgsql it reports true for a database that has no history table.
        var hasHistory = await db.Database
            .SqlQueryRaw<bool>("""SELECT to_regclass('"__EFMigrationsHistory"') IS NOT NULL AS "Value" """)
            .SingleAsync(ct);
        if (!hasHistory)
        {
            return db.Database.GetMigrations().ToList();
        }
        return (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
    }
}

public sealed class SchemaReadyGate : ISchemaReadyGate, IDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SchemaReadyGate> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _disposed = new();
    private readonly object _lock = new();
    // One poll loop shared by every waiter: they all wait on the same schema, so N workers must not
    // mean N queries per interval (or N copies of the warning).
    private Task? _poll;
    private int _isDisposed;

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

    public Task WaitUntilReadyAsync(CancellationToken ct)
    {
        Task poll;
        lock (_lock)
        {
            _poll ??= Task.Run(() => PollUntilReadyAsync(_disposed.Token));
            poll = _poll;
        }
        return poll.IsCompletedSuccessfully ? Task.CompletedTask : poll.WaitAsync(ct);
    }

    private async Task PollUntilReadyAsync(CancellationToken ct)
    {
        var announced = false;
        while (true)
        {
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
                    if (announced)
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

            if (!announced)
            {
                announced = true;
                _logger.LogWarning(error,
                    "Background work paused until the database schema is current ({Reason}); rechecking every {Interval}.",
                    reason, _pollInterval);
            }

            await Task.Delay(_pollInterval, ct);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1) return;
        _disposed.Cancel();
        _disposed.Dispose();
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
