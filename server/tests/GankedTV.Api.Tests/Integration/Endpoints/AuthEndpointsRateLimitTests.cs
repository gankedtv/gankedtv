using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using GankedTV.Api.Auth;
using GankedTV.Api.Contracts.Auth;
using GankedTV.Api.Tests.TestSupport;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("Postgres")]
public class AuthEndpointsRateLimitTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;

    public AuthEndpointsRateLimitTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }
    }

    [Fact]
    public async Task Login_Exceeding5Per60s_Returns429()
    {
        await _fx.ResetAsync();
        Assert.Null(_factory);
        _factory = new AuthApiFactory(_fx.ConnectionString);
        using var client = _factory.CreateClient();

        // Fire one over the per-IP permit limit. The first PermitLimit calls all return
        // 401 (invalid creds); the (PermitLimit+1)th is rejected by the limiter as 429.
        for (var i = 0; i < AuthRateLimiting.CredentialsPermitLimit; i++)
        {
            var ok = await client.PostAsJsonAsync(
                "/auth/login",
                new LoginRequest("nobody@example.com", "wrong-password-12345"));
            ok.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var blocked = await client.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest("nobody@example.com", "wrong-password-12345"));
        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
