using System.Net;
using FluentAssertions;
using GankedTV.Api.Tests.TestSupport;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("Postgres")]
public class ProductionEnvironmentTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;

    public ProductionEnvironmentTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync()
    {
        _factory = new AuthApiFactory(_fx.ConnectionString, environment: "Production");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Production_AppBoots_DevEndpointsNotMapped()
    {
        // Covers the !IsDevelopment branches in Program.cs: /dev/token is only mapped in
        // Development, so hitting it in Production must 404. Also incidentally exercises the
        // "skip .env load" and "skip OpenAPI" branches.
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsync("/dev/token", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Production_OpenApiNotMapped()
    {
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/openapi/v1.json");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Production_HealthLive_ReturnsOk()
    {
        // Liveness has no dependency checks, so it's 200 as soon as the process is up.
        // Also proves the Production secret validation passed (the app booted at all).
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/health/live");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Production_HealthReady_ReturnsOk_WhenDbMigrated()
    {
        // The PostgresFixture migrates the test DB, so readiness reports healthy.
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/health/ready");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
