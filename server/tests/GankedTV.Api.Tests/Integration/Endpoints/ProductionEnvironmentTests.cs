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
}
