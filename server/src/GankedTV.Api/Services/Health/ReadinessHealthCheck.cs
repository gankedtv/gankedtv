using GankedTV.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GankedTV.Api.Services.Health;

/// <summary>
/// Readiness probe backing <c>GET /health/ready</c>. The instance is only "ready" once the
/// database is reachable AND all EF migrations have been applied — a fresh prod DB that has
/// not yet self-migrated (see <see cref="DatabaseMigrator"/>) reports Unhealthy so an
/// orchestrator / deploy smoke-test holds traffic until the schema exists. Liveness
/// (<c>/health/live</c>) is intentionally dependency-free and handled by an empty predicate.
/// </summary>
public sealed class ReadinessHealthCheck(GankedTvDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("Database is not reachable.");
            }

            var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
            var pendingList = pending as IReadOnlyList<string> ?? pending.ToList();
            if (pendingList.Count > 0)
            {
                return HealthCheckResult.Unhealthy(
                    $"Database is reachable but {pendingList.Count} migration(s) are pending.");
            }

            return HealthCheckResult.Healthy("Database reachable and migrations applied.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Readiness check failed.", ex);
        }
    }
}
