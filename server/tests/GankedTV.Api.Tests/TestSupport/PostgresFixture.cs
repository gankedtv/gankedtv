using GankedTV.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;

namespace GankedTV.Api.Tests.TestSupport;

/// <summary>
/// One Postgres container for the whole test process; one database per fixture instance.
/// xUnit creates a fixture instance per collection, so collections run against isolated
/// databases and can execute in parallel (see xunit.runner.json). The first instance starts
/// the container and migrates a template database once; every instance then clones it via
/// <c>CREATE DATABASE ... TEMPLATE</c>, which copies the migrated schema plus the seeded
/// games rows near-instantly instead of re-running EF migrations per collection.
///
/// Parallel-collections env contract: AuthApiFactory writes process-global env vars on every
/// host boot, which is only safe because every factory writes the same constant values.
/// Tests that need to vary process env (CORS_ORIGINS, ADMIN_EMAILS, ...) must all live in
/// the same collection (PostgresServices) so they serialize with each other.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string TemplateDb = "gankedtv_template";

    private static readonly PostgreSqlContainer Container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("gankedtv_test")
        .WithUsername("gankedtv")
        .WithPassword("gankedtv_test")
        // Headroom for parallel collections: each one runs its own WebApplicationFactory
        // host with its own Npgsql pool, plus respawner/test connections.
        .WithCommand("-c", "max_connections=300")
        .Build();

    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static bool _templateReady;
    private static int _dbCounter;

    private Respawner? _respawner;

    /// <summary>Connection string for this fixture instance's own cloned database.</summary>
    public string ConnectionString { get; private set; } = null!;

    /// <summary>
    /// Constant across all fixture instances — safe for AuthApiFactory's process-global
    /// DATABASE_URL. The real per-collection database flows through the factory's
    /// DbContextOptions override, never through this value.
    /// </summary>
    public static string AdminConnectionString => Container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await InitLock.WaitAsync();
        try
        {
            if (!_templateReady)
            {
                await Container.StartAsync();
                await ExecuteAdminAsync($"CREATE DATABASE {TemplateDb};");

                // Migrate the template on a non-pooled connection so no idle pooled
                // connection blocks cloning (CREATE DATABASE ... TEMPLATE requires the
                // source to have zero active connections).
                var templateConn = BuildConnectionString(TemplateDb, pooling: false);
                await using (var conn = new NpgsqlConnection(templateConn))
                {
                    await conn.OpenAsync();
                    // pgcrypto provides gen_random_uuid(); extensions are per-database and
                    // ride along into every clone.
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS pgcrypto;";
                    await cmd.ExecuteNonQueryAsync();
                }

                var options = new DbContextOptionsBuilder<GankedTvDbContext>()
                    .UseNpgsql(templateConn)
                    .UseSnakeCaseNamingConvention()
                    .Options;
                await using (var db = new GankedTvDbContext(options))
                {
                    await db.Database.MigrateAsync();
                }

                // Hard guarantee the template stays clonable.
                await ExecuteAdminAsync($"ALTER DATABASE {TemplateDb} WITH ALLOW_CONNECTIONS false;");
                _templateReady = true;
            }

            // Clone creation stays under the lock: concurrent CREATE DATABASE commands
            // against the same template conflict on the template lock.
            var dbName = $"gankedtv_test_{Interlocked.Increment(ref _dbCounter)}";
            await ExecuteAdminAsync($"CREATE DATABASE {dbName} TEMPLATE {TemplateDb};");
            ConnectionString = BuildConnectionString(dbName, pooling: true);
        }
        finally
        {
            InitLock.Release();
        }

        await using var resetConn = new NpgsqlConnection(ConnectionString);
        await resetConn.OpenAsync();
        _respawner = await Respawner.CreateAsync(resetConn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = new[] { "public" },
            TablesToIgnore = new Respawn.Graph.Table[]
            {
                new("__EFMigrationsHistory"),
                new("games"),
            },
        });
    }

    public async Task ResetAsync()
    {
        if (_respawner is null)
        {
            throw new InvalidOperationException("Respawner not initialised.");
        }
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await _respawner.ResetAsync(conn);
    }

    public GankedTvDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GankedTvDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new GankedTvDbContext(options);
    }

    public async Task DisposeAsync()
    {
        // Drop this instance's pool; the static container is never disposed here — xUnit v2
        // has no assembly-level async fixture, so Testcontainers' reaper (Ryuk) removes it
        // when the test process exits.
        if (ConnectionString is not null)
        {
            NpgsqlConnection.ClearPool(new NpgsqlConnection(ConnectionString));
        }
        await Task.CompletedTask;
    }

    private static string BuildConnectionString(string database, bool pooling) =>
        new NpgsqlConnectionStringBuilder(Container.GetConnectionString())
        {
            Database = database,
            Pooling = pooling,
        }.ToString();

    private static async Task ExecuteAdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
