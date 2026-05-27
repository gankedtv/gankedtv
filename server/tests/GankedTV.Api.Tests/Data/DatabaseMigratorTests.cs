using FluentAssertions;
using GankedTV.Api.Data;
using GankedTV.Api.Services.Health;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace GankedTV.Api.Tests.Data;

// Own container (not the shared, already-migrated PostgresFixture) so we can exercise the
// "applies pending migrations" branch against a fresh DB and then the "already up to date"
// no-op branch on the second call.
public class DatabaseMigratorTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("gankedtv_migrate_test")
        .WithUsername("gankedtv")
        .WithPassword("gankedtv_test")
        .Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS pgcrypto;";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private GankedTvDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GankedTvDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new GankedTvDbContext(options);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData(" true ", true)] // tolerate surrounding whitespace from env/ConfigMap values
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsEnabled_AcceptsCommonTruthyValues(string? value, bool expected)
    {
        DatabaseMigrator.IsEnabled(value).Should().Be(expected);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_FreshDb_AppliesThenSecondCallIsNoOp()
    {
        await using var db = CreateContext();

        (await db.Database.GetAppliedMigrationsAsync()).Should().BeEmpty();

        await DatabaseMigrator.ApplyMigrationsAsync(db, NullLogger.Instance);

        (await db.Database.GetAppliedMigrationsAsync()).Should().NotBeEmpty();
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();

        // Second call: nothing pending → no-op branch, must not throw.
        await DatabaseMigrator.ApplyMigrationsAsync(db, NullLogger.Instance);
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    // Exercises ReadinessHealthCheck against this owned container: Unhealthy while migrations
    // are pending, Healthy once applied. (The shared fixture is always migrated, so the
    // "pending" branch can only be covered with a fresh DB.)
    [Fact]
    public async Task Readiness_ReportsUnhealthyUntilMigrated_ThenHealthy()
    {
        await using var db = CreateContext();
        var check = new ReadinessHealthCheck(db);
        var context = new HealthCheckContext();

        var beforeMigrate = await check.CheckHealthAsync(context);
        beforeMigrate.Status.Should().Be(HealthStatus.Unhealthy);
        beforeMigrate.Description.Should().Contain("pending");

        await DatabaseMigrator.ApplyMigrationsAsync(db, NullLogger.Instance);

        var afterMigrate = await check.CheckHealthAsync(context);
        afterMigrate.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Readiness_ReportsUnhealthy_WhenDatabaseUnreachable()
    {
        var options = new DbContextOptionsBuilder<GankedTvDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=1;Command Timeout=1")
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new GankedTvDbContext(options);

        var result = await new ReadinessHealthCheck(db).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("not reachable");
    }
}
