using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Data;

/// <summary>
/// Applies pending EF Core migrations at boot. Wired in <c>Program.cs</c> behind the
/// <c>RUN_MIGRATIONS_ON_STARTUP</c> env flag (default off) so a fresh production DB
/// self-migrates before the API serves its first request — locally migrations stay manual
/// (<c>make migrate</c>). Idempotent: a re-run with no pending migrations is a no-op.
/// </summary>
public static class DatabaseMigrator
{
    /// <summary>Env-var name that gates startup migration. Set to <c>"true"</c> to enable.</summary>
    public const string EnableEnvVar = "RUN_MIGRATIONS_ON_STARTUP";

    public static bool IsEnabled(string? envValue) =>
        string.Equals(envValue, "true", StringComparison.OrdinalIgnoreCase);

    public static async Task ApplyMigrationsAsync(
        GankedTvDbContext db,
        ILogger logger,
        CancellationToken ct = default)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Startup migrations: database already up to date.");
            return;
        }

        logger.LogInformation(
            "Startup migrations: applying {Count} pending migration(s): {Migrations}",
            pending.Count,
            string.Join(", ", pending));
        await db.Database.MigrateAsync(ct);
        logger.LogInformation("Startup migrations: applied {Count} migration(s).", pending.Count);
    }
}
