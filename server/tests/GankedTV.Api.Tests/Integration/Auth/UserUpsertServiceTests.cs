using FluentAssertions;
using GankedTV.Api.Auth;
using GankedTV.Api.Auth.Providers;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Tests.Integration.Auth;

[Collection("Postgres")]
public class UserUpsertServiceTests
{
    private readonly PostgresFixture _fx;

    public UserUpsertServiceTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task UpsertFromOAuthAsync_NewDiscordUser_CreatesRowWithDiscordId()
    {
        await _fx.ResetAsync();

        User user;
        await using (var db = _fx.CreateContext())
        {
            var svc = new UserUpsertService(db);
            user = await svc.UpsertFromOAuthAsync(
                DiscordOAuthProvider.ProviderName,
                new OAuthUserInfo("d-42", "zoe@example.com", "Zoe", null));
        }

        user.DiscordId.Should().Be("d-42");
        user.Username.Should().Be("zoe");
        user.Email.Should().Be("zoe@example.com");

        await using var verify = _fx.CreateContext();
        (await verify.Users.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UpsertFromOAuthAsync_ExistingDiscordId_UpdatesEmailAndAvatar()
    {
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        Guid id;
        await using (var db = _fx.CreateContext())
        {
            var existing = new User
            {
                Username = "zoe",
                Email = "old@example.com",
                DiscordId = "d-42",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(existing);
            await db.SaveChangesAsync();
            id = existing.Id;
        }

        await using (var db = _fx.CreateContext())
        {
            var svc = new UserUpsertService(db);
            await svc.UpsertFromOAuthAsync(
                DiscordOAuthProvider.ProviderName,
                new OAuthUserInfo("d-42", "new@example.com", "Zoe", "http://avatar.png"));
        }

        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync(u => u.Id == id);
        user.Email.Should().Be("new@example.com");
        user.AvatarUrl.Should().Be("http://avatar.png");
    }

    [Fact]
    public async Task UpsertFromOAuthAsync_GoogleSignInWithMatchingEmail_LinksGoogleIdToExistingUser()
    {
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        Guid id;
        await using (var db = _fx.CreateContext())
        {
            var existing = new User
            {
                Username = "zoe",
                Email = "zoe@example.com",
                DiscordId = "d-42",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(existing);
            await db.SaveChangesAsync();
            id = existing.Id;
        }

        await using (var db = _fx.CreateContext())
        {
            await new UserUpsertService(db).UpsertFromOAuthAsync(
                GoogleOAuthProvider.ProviderName,
                new OAuthUserInfo("g-1", "zoe@example.com", "Zoe Google", null));
        }

        await using var verify = _fx.CreateContext();
        (await verify.Users.CountAsync()).Should().Be(1);
        var user = await verify.Users.SingleAsync(u => u.Id == id);
        user.GoogleId.Should().Be("g-1");
        user.DiscordId.Should().Be("d-42");
    }

    [Fact]
    public async Task UpsertFromOAuthAsync_UsernameCollision_AppendsSuffix()
    {
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var db = _fx.CreateContext())
        {
            db.Users.Add(new User { Username = "zoe", CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        User user;
        await using (var db = _fx.CreateContext())
        {
            user = await new UserUpsertService(db).UpsertFromOAuthAsync(
                DiscordOAuthProvider.ProviderName,
                new OAuthUserInfo("d-99", "zoe2@example.com", "Zoe", null));
        }

        user.Username.Should().MatchRegex("^zoe-[0-9a-f]{4}$");
    }
}
