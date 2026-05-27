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
    /// <summary>Env-var name that gates startup migration. Set to a truthy value to enable.</summary>
    public const string EnableEnvVar = "RUN_MIGRATIONS_ON_STARTUP";

    // Deliberately more lenient than the bool.TryParse-based MEDIA_* toggles in Program.cs:
    // K8s ConfigMaps and many CI systems use "1"/"yes"/"on" by convention, and the failure mode
    // of a silently-skipped migration (a fresh prod DB that boots but never migrates, leaving
    // /health/ready red with a "migrations pending" message) is confusing to debug. Accepting
    // the common truthy spellings here avoids that foot-gun.
    private static readonly HashSet<string> TruthyValues =
        new(StringComparer.OrdinalIgnoreCase) { "true", "1", "yes", "on" };

    public static bool IsEnabled(string? envValue) =>
        envValue is not null && TruthyValues.Contains(envValue.Trim());

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
