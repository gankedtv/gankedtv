using System.Net;
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
        var (id, token) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);
        return (id, token, username);
    }

    private HttpClient ClientWithBearer(string token) =>
        AuthTestHelpers.CreateBearerClient(_factory!, token);

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
        // OAuth-only / no-credential user — hasPassword is false. Pinned here so the web
        // SettingsPasswordView's "Set password" vs "Change password" copy stays correct.
        body.GetProperty("hasPassword").GetBoolean().Should().BeFalse();
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
    public async Task Patch_AvatarUrl_FieldIgnoredSilently()
    {
        // PATCH /auth/me no longer accepts avatarUrl directly — uploads go through the
        // ProfileMedia endpoints, and OAuth refresh handles the provider-driven path. An
        // external script that still sends the field must not be persisted, but the request
        // is otherwise valid so it still 200s.
        await _fx.ResetAsync();
        var (userId, token, _) = await SeedUserAndIssueTokenAsync("legacyclient");

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/auth/me",
            new { avatarUrl = "https://attacker.example.com/evil.png" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = _fx.CreateContext();
        var user = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        user.AvatarUrl.Should().BeNull();
    }

    [Fact]
    public async Task Patch_AccentColor_RoundTrips()
    {
        await _fx.ResetAsync();
        var (userId, token, _) = await SeedUserAndIssueTokenAsync("accent");

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/auth/me", new { accentColor = "#6D28D9" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = _fx.CreateContext();
        var user = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        user.AccentColor.Should().Be("#6D28D9");
    }

    [Fact]
    public async Task Patch_AccentColor_InvalidHex_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        // Word colors (no #), short hex, non-hex chars all rejected by the regex.
        var resps = await Task.WhenAll(
            client.PatchAsJsonAsync("/auth/me", new { accentColor = "red" }),
            client.PatchAsJsonAsync("/auth/me", new { accentColor = "#fff" }),
            client.PatchAsJsonAsync("/auth/me", new { accentColor = "#ZZZZZZ" }));

        foreach (var r in resps)
        {
            r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task Patch_AccentColor_EmptyClears()
    {
        await _fx.ResetAsync();
        var (userId, token, _) = await SeedUserAndIssueTokenAsync("clearaccent");
        await using (var db = _fx.CreateContext())
        {
            var u = await db.Users.SingleAsync(x => x.Id == userId);
            u.AccentColor = "#00FF00";
            await db.SaveChangesAsync();
        }

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/auth/me", new { accentColor = "" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verify = _fx.CreateContext();
        var user = await verify.Users.AsNoTracking().SingleAsync(x => x.Id == userId);
        user.AccentColor.Should().BeNull();
    }

    [Fact]
    public async Task Patch_SocialLinks_RoundTripsAndStripsAtPrefix()
    {
        await _fx.ResetAsync();
        var (userId, token, _) = await SeedUserAndIssueTokenAsync("socials");
        using var client = ClientWithBearer(token);

        var resp = await client.PatchAsJsonAsync("/auth/me", new
        {
            socialLinks = new { twitch = "@TwitchUser", youtube = "MyChannel", twitter = "x.handle" },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await resp.Content.ReadAsStringAsync();
        // Pin the serialized shape so the SPA can rely on the lower-camel keys.
        raw.Should().Contain("\"socialLinks\":{");
        raw.Should().Contain("\"twitch\":\"TwitchUser\"");
        raw.Should().Contain("\"youtube\":\"MyChannel\"");
        raw.Should().Contain("\"twitter\":\"x.handle\"");

        await using var verify = _fx.CreateContext();
        var user = await verify.Users.AsNoTracking().SingleAsync(x => x.Id == userId);
        user.SocialLinks.Should().NotBeNull();
        // Leading @ stripped on the way in.
        user.SocialLinks!.Twitch.Should().Be("TwitchUser");
        user.SocialLinks.YouTube.Should().Be("MyChannel");
        user.SocialLinks.Twitter.Should().Be("x.handle");
    }

    [Fact]
    public async Task Patch_SocialLinks_DisallowedChar_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        // Spaces, slashes, and other punctuation aren't allowed by SocialHandleRegex.
        var resp = await client.PatchAsJsonAsync("/auth/me",
            new { socialLinks = new { twitch = "bad handle" } });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_SocialLinks_TooLong_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PatchAsJsonAsync("/auth/me",
            new { socialLinks = new { twitch = new string('a', 33) } });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_SocialLinks_AllEmpty_CollapsesToNull()
    {
        await _fx.ResetAsync();
        var (userId, token, _) = await SeedUserAndIssueTokenAsync("collapse");
        await using (var db = _fx.CreateContext())
        {
            var u = await db.Users.SingleAsync(x => x.Id == userId);
            u.SocialLinks = new GankedTV.Api.Data.Entities.SocialLinks { Twitch = "prior" };
            await db.SaveChangesAsync();
        }

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/auth/me",
            new { socialLinks = new { twitch = "", youtube = "", twitter = "" } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = _fx.CreateContext();
        var user = await verify.Users.AsNoTracking().SingleAsync(x => x.Id == userId);
        user.SocialLinks.Should().BeNull();
    }

    [Fact]
    public async Task Patch_AccentColor_SameValueIsNoOp()
    {
        // Repeat-with-same-value branch — the handler skips the assignment + UpdatedAt bump.
        await _fx.ResetAsync();
        var (userId, token, _) = await SeedUserAndIssueTokenAsync("samenoop");
        await using (var db = _fx.CreateContext())
        {
            var u = await db.Users.SingleAsync(x => x.Id == userId);
            u.AccentColor = "#00FFAA";
            await db.SaveChangesAsync();
        }
        DateTimeOffset before;
        await using (var db = _fx.CreateContext())
        {
            before = (await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId)).UpdatedAt;
        }
        await Task.Delay(20);

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/auth/me", new { accentColor = "#00FFAA" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = _fx.CreateContext();
        var user = await verify.Users.AsNoTracking().SingleAsync(x => x.Id == userId);
        user.UpdatedAt.Should().Be(before);
    }

    [Fact]
    public async Task Patch_SocialLinks_YoutubeInvalid_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/auth/me",
            new { socialLinks = new { youtube = "bad space" } });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_SocialLinks_TwitterInvalid_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token, _) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/auth/me",
            new { socialLinks = new { twitter = "bad/slash" } });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_SocialLinks_RepeatSame_IsNoOp()
    {
        await _fx.ResetAsync();
        var (userId, token, _) = await SeedUserAndIssueTokenAsync("noop2");
        await using (var db = _fx.CreateContext())
        {
            var u = await db.Users.SingleAsync(x => x.Id == userId);
            u.SocialLinks = new SocialLinks { Twitch = "same" };
            await db.SaveChangesAsync();
        }
        DateTimeOffset before;
        await using (var db = _fx.CreateContext())
        {
            before = (await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId)).UpdatedAt;
        }
        await Task.Delay(20);

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync("/auth/me",
            new { socialLinks = new { twitch = "same" } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = _fx.CreateContext();
        var user = await verify.Users.AsNoTracking().SingleAsync(x => x.Id == userId);
        user.UpdatedAt.Should().Be(before);
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
