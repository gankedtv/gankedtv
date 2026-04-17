using GankedTV.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;

namespace GankedTV.Api.Tests.TestSupport;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("gankedtv_test")
        .WithUsername("gankedtv")
        .WithPassword("gankedtv_test")
        .Build();

    private Respawner? _respawner;

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // pgcrypto provides gen_random_uuid(); also available in postgres >=13 via pg_catalog,
        // but enabling the extension ensures parity with migrations that reference it.
        await using (var conn = new NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS pgcrypto;";
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var resetConn = new NpgsqlConnection(ConnectionString))
        {
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

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}
