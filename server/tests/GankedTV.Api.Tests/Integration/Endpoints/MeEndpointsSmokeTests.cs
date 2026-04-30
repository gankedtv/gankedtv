using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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

        var resp = await client.GetAsync("/auth/me");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_WithValidBearer_Returns200AndUser()
    {
        await _fx.ResetAsync();
        var (_, token, username) = await SeedUserAndIssueTokenAsync();

        using var client = ClientWithBearer(token);
        var resp = await client.GetAsync("/auth/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain(username);
    }

    [Fact]
    public async Task Get_WithPopulatedUser_ReturnsAllMeResponseFields()
    {
        await _fx.ResetAsync();

        const string username = "fulluser";
        const string email = "fulluser@example.com";
        const string bio = "Built different. Dies first.";
        const string avatarUrl = "https://example.com/avatars/fulluser.png";
        var createdAt = DateTimeOffset.UtcNow.AddDays(-3);

        Guid userId;
        await using (var db = _fx.CreateContext())
        {
            var user = new User
            {
                Username = username,
                Email = email,
                Bio = bio,
                AvatarUrl = avatarUrl,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        using var scope = _factory!.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<IJwtService>();
        var token = jwt.Issue(new User { Id = userId, Username = username, Email = email });

        using var client = ClientWithBearer(token);
        var resp = await client.GetAsync("/auth/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(userId);
        body.GetProperty("username").GetString().Should().Be(username);
        body.GetProperty("email").GetString().Should().Be(email);
        body.GetProperty("bio").GetString().Should().Be(bio);
        body.GetProperty("avatarUrl").GetString().Should().Be(avatarUrl);
        // Postgres timestamptz stores microsecond precision; .NET DateTimeOffset has 100ns ticks,
        // so a round-trip rounds by up to 1 microsecond. 1ms tolerance comfortably absorbs that.
        body.GetProperty("createdAt").GetDateTimeOffset()
            .Should().BeCloseTo(createdAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Get_WithSparseUser_ReturnsNullsForBioAndAvatar()
    {
        await _fx.ResetAsync();
        var (userId, token, username) = await SeedUserAndIssueTokenAsync("sparse");

        using var client = ClientWithBearer(token);
        var resp = await client.GetAsync("/auth/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(userId);
        body.GetProperty("username").GetString().Should().Be(username);
        body.GetProperty("email").GetString().Should().Be($"{username}@example.com");
        body.GetProperty("bio").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("avatarUrl").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("createdAt").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task Patch_UpdateBio_Returns200()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync();

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/auth/me", new { bio = "Built different. Dies first." });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("Built different. Dies first.");
    }

    [Fact]
    public async Task Patch_ChangeUsername_Returns200()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync("original");

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/auth/me", new { username = "renamed" });

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
        var resp = await client.PatchAsJsonAsync("/auth/me", new { username = "taken" });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Patch_InvalidAvatarUrl_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync();

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/auth/me", new { avatarUrl = "not a url" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_AvatarUrlWithCredentials_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync();

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/auth/me", new { avatarUrl = "https://user:pass@example.com/a.png" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_AvatarUrlWithFragment_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync();

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/auth/me", new { avatarUrl = "https://example.com/a.png#payload" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_AvatarUrlTooLong_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync();

        var longPath = new string('a', 2100);
        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/auth/me", new { avatarUrl = $"https://example.com/{longPath}.png" });

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
        var resp = await client.PatchAsJsonAsync("/auth/me", new { bio = (string?)null });

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
        var resp = await client.PatchAsJsonAsync("/auth/me", new { bio = new string('a', 501) });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_WhitespaceUsername_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token, original) = await SeedUserAndIssueTokenAsync("keeper");

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/auth/me", new { username = "   " });

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
        var resp = await client.PatchAsJsonAsync("/auth/me", new { username = "!!!" });

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
        var resp = await client.PatchAsJsonAsync("/auth/me", new { username = " My Mixed-Case Name " });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = _fx.CreateContext();
        var after = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        after.Username.Should().Be("my-mixed-case-name");
    }

    [Fact]
    public async Task Patch_TokenSubjectDoesNotExist_Returns401()
    {
        await _fx.ResetAsync();
        var (userId, token, _) = await SeedUserAndIssueTokenAsync("willvanish");

        await using (var db = _fx.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            db.Users.Remove(user);
            await db.SaveChangesAsync();
        }

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/auth/me", new { bio = "anything" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Patch_EmptyBio_ClearsBio()
    {
        await _fx.ResetAsync();
        var (userId, token, _) = await SeedUserAndIssueTokenAsync("bioed");
        await using (var db = _fx.CreateContext())
        {
            var u = await db.Users.SingleAsync(x => x.Id == userId);
            u.Bio = "prior text";
            await db.SaveChangesAsync();
        }

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/auth/me", new { bio = "" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db2 = _fx.CreateContext();
        var after = await db2.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        after.Bio.Should().BeNull();
    }

    [Fact]
    public async Task Patch_SetAndClearAvatarUrl()
    {
        await _fx.ResetAsync();
        var (userId, token, _) = await SeedUserAndIssueTokenAsync("avataruser");

        using var client = ClientWithBearer(token);
        var set = await client.PatchAsJsonAsync("/auth/me", new { avatarUrl = "https://cdn.example.com/a.png" });
        set.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = _fx.CreateContext())
        {
            var u = await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId);
            u.AvatarUrl.Should().Be("https://cdn.example.com/a.png");
        }

        // An empty string is the canonical "remove avatar" signal — ValidateAvatarUrl maps "" to (ok, null).
        var cleared = await client.PatchAsJsonAsync("/auth/me", new { avatarUrl = "" });
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verify = _fx.CreateContext();
        var final = await verify.Users.AsNoTracking().SingleAsync(x => x.Id == userId);
        final.AvatarUrl.Should().BeNull();
    }

    [Fact]
    public async Task Patch_AvatarUrlUnchanged_DoesNotBumpUpdatedAt()
    {
        await _fx.ResetAsync();
        var (userId, token, _) = await SeedUserAndIssueTokenAsync("stableavatar");
        const string avatar = "https://cdn.example.com/x.png";

        DateTimeOffset before;
        await using (var db = _fx.CreateContext())
        {
            var u = await db.Users.SingleAsync(x => x.Id == userId);
            u.AvatarUrl = avatar;
            await db.SaveChangesAsync();
            before = u.UpdatedAt;
        }

        await Task.Delay(20);

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/auth/me", new { avatarUrl = avatar });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verify = _fx.CreateContext();
        var final = await verify.Users.AsNoTracking().SingleAsync(x => x.Id == userId);
        final.UpdatedAt.Should().Be(before);
    }

    [Fact]
    public async Task Patch_FallbackToPlayerUsername_Allowed()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync("claimer");

        using var client = ClientWithBearer(token);
        // Literal "player" is explicitly allowed even though it's the generator's fallback
        // value — otherwise nobody could ever take the obvious name.
        var resp = await client.PatchAsJsonAsync("/auth/me", new { username = "player" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("player");
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
        var resp = await client.GetAsync("/auth/me");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
