using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using GankedTV.Api.Auth.Jwt;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("Postgres")]
public class MeEndpointsSmokeTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;

    public MeEndpointsSmokeTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync()
    {
        _factory = new AuthApiFactory(_fx.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    private async Task<(Guid userId, string token, string username)> SeedUserAndIssueTokenAsync(string username = "smoketest")
    {
        var now = DateTimeOffset.UtcNow;
        Guid id;
        await using (var db = _fx.CreateContext())
        {
            var user = new User
            {
                Username = username,
                Email = $"{username}@example.com",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            id = user.Id;
        }

        using var scope = _factory!.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<IJwtService>();
        var token = jwt.Issue(new User { Id = id, Username = username, Email = $"{username}@example.com" });
        return (id, token, username);
    }

    private HttpClient ClientWithBearer(string token)
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Get_NoBearer_Returns401()
    {
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/me");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_WithValidBearer_Returns200AndUser()
    {
        await _fx.ResetAsync();
        var (_, token, username) = await SeedUserAndIssueTokenAsync();

        using var client = ClientWithBearer(token);
        var resp = await client.GetAsync("/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain(username);
    }

    [Fact]
    public async Task Patch_UpdateBio_Returns200()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync();

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/me", new { bio = "Built different. Dies first." });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("Built different. Dies first.");
    }

    [Fact]
    public async Task Patch_ChangeUsername_Returns200()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync("original");

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/me", new { username = "renamed" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("renamed");
    }

    [Fact]
    public async Task Patch_UsernameCollision_Returns409()
    {
        await _fx.ResetAsync();

        var now = DateTimeOffset.UtcNow;
        await using (var db = _fx.CreateContext())
        {
            db.Users.Add(new User { Username = "taken", CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        var (_, token, _) = await SeedUserAndIssueTokenAsync("myself");

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/me", new { username = "taken" });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Patch_InvalidAvatarUrl_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync();

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/me", new { avatarUrl = "not a url" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_AvatarUrlWithCredentials_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync();

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/me", new { avatarUrl = "https://user:pass@example.com/a.png" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_AvatarUrlWithFragment_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync();

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/me", new { avatarUrl = "https://example.com/a.png#payload" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_AvatarUrlTooLong_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync();

        var longPath = new string('a', 2100);
        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/me", new { avatarUrl = $"https://example.com/{longPath}.png" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_NoChange_DoesNotBumpUpdatedAt()
    {
        await _fx.ResetAsync();
        var (userId, token, _) = await SeedUserAndIssueTokenAsync("stable");

        DateTimeOffset originalUpdatedAt;
        await using (var db = _fx.CreateContext())
        {
            originalUpdatedAt = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId)).UpdatedAt;
        }

        // Give the clock a moment to advance so we'd detect any spurious write.
        await Task.Delay(20);

        using var client = ClientWithBearer(token);
        // Re-send the same bio — after my no-op short-circuit, no mutation should occur.
        var resp = await client.PatchAsJsonAsync("/me", new { bio = (string?)null });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db2 = _fx.CreateContext();
        var after = await db2.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        after.UpdatedAt.Should().Be(originalUpdatedAt);
    }

    [Fact]
    public async Task Patch_BioTooLong_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync();

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/me", new { bio = new string('a', 501) });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_WhitespaceUsername_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token, original) = await SeedUserAndIssueTokenAsync("keeper");

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/me", new { username = "   " });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using var db = _fx.CreateContext();
        var after = await db.Users.AsNoTracking().SingleAsync();
        after.Username.Should().Be(original);
    }

    [Fact]
    public async Task Patch_PunctuationOnlyUsername_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token, original) = await SeedUserAndIssueTokenAsync("holder");

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/me", new { username = "!!!" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using var db = _fx.CreateContext();
        var after = await db.Users.AsNoTracking().SingleAsync();
        after.Username.Should().Be(original);
    }

    [Fact]
    public async Task Patch_MixedCaseWithSpaces_PersistsSlugifiedForm()
    {
        await _fx.ResetAsync();
        var (userId, token, _) = await SeedUserAndIssueTokenAsync("starter");

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/me", new { username = " My Mixed-Case Name " });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = _fx.CreateContext();
        var after = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        after.Username.Should().Be("my-mixed-case-name");
    }

    [Fact]
    public async Task Get_TokenSubjectDoesNotExist_Returns401()
    {
        await _fx.ResetAsync();
        var (userId, token, _) = await SeedUserAndIssueTokenAsync("ghost");

        // Simulate the user being deleted after we issued the JWT.
        await using (var db = _fx.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            db.Users.Remove(user);
            await db.SaveChangesAsync();
        }

        using var client = ClientWithBearer(token);
        var resp = await client.GetAsync("/me");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
