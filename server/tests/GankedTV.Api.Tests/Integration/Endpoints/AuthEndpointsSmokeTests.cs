using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Auth.Tokens;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("PostgresAuth")]
public class AuthEndpointsSmokeTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;

    public AuthEndpointsSmokeTests(PostgresFixture fx) => _fx = fx;

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
    public async Task Start_UnknownProvider_Returns404()
    {
        using var client = _factory!.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var resp = await client.GetAsync("/auth/twitter/start");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListProviders_ReturnsConfiguredProviders()
    {
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/auth/providers");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var providers = body.GetProperty("providers").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        providers.Should().BeEquivalentTo(new[] { "discord", "google" });
    }

    [Fact]
    public async Task Refresh_UnknownToken_Returns401()
    {
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsJsonAsync("/auth/refresh", new { refresh = "never-issued" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_EmptyBody_Returns400()
    {
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsJsonAsync("/auth/refresh", new { refresh = "" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Start_ValidProvider_RedirectsAndSetsCookie()
    {
        using var client = _factory!.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var resp = await client.GetAsync("/auth/discord/start?returnTo=/feed");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().StartWith("https://discord.com/oauth2/authorize?");
        resp.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        string.Join(';', cookies!).Should().Contain("gtv_oauth_state=");
    }

    [Fact]
    public async Task Refresh_ValidToken_Returns200WithNewPair()
    {
        await _fx.ResetAsync();
        var original = await SeedUserAndIssueRefreshAsync();

        using var client = _factory!.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/refresh", new { refresh = original });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
        var newRefresh = body.RootElement.GetProperty("refresh").GetString();
        newRefresh.Should().NotBeNullOrEmpty().And.NotBe(original);
        body.RootElement.GetProperty("expiresIn").GetInt32().Should().Be(15 * 60);
    }

    [Fact]
    public async Task Refresh_BannedUser_Returns403_AndDoesNotInsertSuccessor()
    {
        // Endpoint-level smoke: ensure the BannedAccountException thrown by RotateAsync
        // surfaces as 403 account_banned, mirroring the login response, and that the SPA
        // can tell the difference between an invalid refresh (401) and a disabled account.
        await _fx.ResetAsync();
        var original = await SeedUserAndIssueRefreshAsync();

        // Ban the user we just issued the refresh for.
        await using (var db = _fx.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Username == "refreshuser");
            user.BannedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/refresh", new { refresh = original });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The endpoint refused to mint a successor; refresh_tokens still has the original
        // (now revoked) row but no new ones — write amplification is bounded.
        await using var verify = _fx.CreateContext();
        var rows = await verify.RefreshTokens.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_ReuseOldToken_Returns401()
    {
        await _fx.ResetAsync();
        var original = await SeedUserAndIssueRefreshAsync();

        using var client = _factory!.CreateClient();
        var first = await client.PostAsJsonAsync("/auth/refresh", new { refresh = original });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.PostAsJsonAsync("/auth/refresh", new { refresh = original });
        second.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<string> SeedUserAndIssueRefreshAsync()
    {
        var now = DateTimeOffset.UtcNow;
        Guid userId;
        await using (var db = _fx.CreateContext())
        {
            var user = new User
            {
                Username = "refreshuser",
                Email = "refresh@example.com",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        using var scope = _factory!.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        return await svc.IssueAsync(userId);
    }
}
