using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("Postgres")]
public class DevAuthEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;

    public DevAuthEndpointsTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync()
    {
        _factory = new AuthApiFactory(_fx.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task DevToken_CreatesUserAndReturnsJwt()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsJsonAsync("/dev/token", new { username = "alice" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("username").GetString().Should().Be("alice");
        body.GetProperty("userId").GetGuid().Should().NotBeEmpty();

        await using var db = _fx.CreateContext();
        var user = await db.Users.SingleAsync(u => u.Username == "alice");
        user.Email.Should().Be("alice@dev.local");
    }

    [Fact]
    public async Task DevToken_ReusesExistingUserByUsername()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var first = await client.PostAsJsonAsync("/dev/token", new { username = "bob" });
        var second = await client.PostAsJsonAsync("/dev/token", new { username = "bob" });

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        firstBody.GetProperty("userId").GetGuid()
            .Should().Be(secondBody.GetProperty("userId").GetGuid());

        await using var db = _fx.CreateContext();
        var count = await db.Users.CountAsync(u => u.Username == "bob");
        count.Should().Be(1);
    }

    [Fact]
    public async Task DevToken_DefaultsUsernameWhenOmitted()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsJsonAsync("/dev/token", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("username").GetString().Should().Be("dev-user");
    }

    [Fact]
    public async Task DevToken_NotMappedInProduction()
    {
        await using var prodFactory = new AuthApiFactory(_fx.ConnectionString, environment: "Production");
        using var client = prodFactory.CreateClient();

        var resp = await client.PostAsJsonAsync("/dev/token", new { username = "prod" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DevToken_IssuesJwtAcceptedByMeEndpoint()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var token = (await (await client.PostAsJsonAsync("/dev/token", new { username = "carol" }))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var meResp = await client.GetAsync("/me");

        meResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await meResp.Content.ReadAsStringAsync()).Should().Contain("carol");
    }
}
