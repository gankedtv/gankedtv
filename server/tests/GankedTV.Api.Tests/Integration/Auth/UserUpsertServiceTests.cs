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
                new OAuthUserInfo("d-42", "zoe@example.com", "Zoe", null, EmailVerified: true));
        }

        user.DiscordId.Should().Be("d-42");
        user.Username.Should().Be("zoe");
        user.Email.Should().Be("zoe@example.com");

        await using var verify = _fx.CreateContext();
        (await verify.Users.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UpsertFromOAuthAsync_ExistingDiscordIdWithNoAvatar_UpdatesEmailAndAvatar()
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
                new OAuthUserInfo("d-42", "new@example.com", "Zoe", "http://avatar.png", EmailVerified: true));
        }

        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync(u => u.Id == id);
        user.Email.Should().Be("new@example.com");
        user.AvatarUrl.Should().Be("http://avatar.png");
    }

    [Fact]
    public async Task UpsertFromOAuthAsync_ExistingDiscordIdWithCustomAvatar_KeepsUserAvatar()
    {
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        Guid id;
        await using (var db = _fx.CreateContext())
        {
            // User has set a custom avatar via PATCH /me previously.
            var existing = new User
            {
                Username = "zoe",
                Email = "zoe@example.com",
                AvatarUrl = "https://custom.example/zoe.png",
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
                DiscordOAuthProvider.ProviderName,
                new OAuthUserInfo("d-42", "zoe@example.com", "Zoe", "https://cdn.discordapp.com/avatars/d-42/hash.png"));
        }

        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync(u => u.Id == id);
        user.AvatarUrl.Should().Be("https://custom.example/zoe.png");
    }

    [Fact]
    public async Task UpsertFromOAuthAsync_UnverifiedEmail_DoesNotLinkToExistingUser()
    {
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var db = _fx.CreateContext())
        {
            db.Users.Add(new User
            {
                Username = "legit",
                Email = "claimed@example.com",
                DiscordId = "d-legit",
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        User newUser;
        await using (var db = _fx.CreateContext())
        {
            newUser = await new UserUpsertService(db).UpsertFromOAuthAsync(
                GoogleOAuthProvider.ProviderName,
                new OAuthUserInfo("g-attacker", "claimed@example.com", "Attacker", null, EmailVerified: false));
        }

        // Must NOT link — an unverified email on a new Google account would otherwise take
        // over the original Discord user's account.
        newUser.GoogleId.Should().Be("g-attacker");
        newUser.Username.Should().NotBe("legit");
        // Unverified emails are also not persisted on new users, to prevent future link-by-email.
        newUser.Email.Should().BeNull();

        await using var verify = _fx.CreateContext();
        (await verify.Users.CountAsync()).Should().Be(2);
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
                new OAuthUserInfo("g-1", "zoe@example.com", "Zoe Google", null, EmailVerified: true));
        }

        await using var verify = _fx.CreateContext();
        (await verify.Users.CountAsync()).Should().Be(1);
        var user = await verify.Users.SingleAsync(u => u.Id == id);
        user.GoogleId.Should().Be("g-1");
        user.DiscordId.Should().Be("d-42");
    }

    [Fact]
    public async Task UpsertFromOAuthAsync_ExistingUserUnverifiedEmail_DoesNotOverwriteEmail()
    {
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        Guid id;
        await using (var db = _fx.CreateContext())
        {
            db.Users.Add(new User
            {
                Username = "pat",
                Email = "pat@example.com",
                DiscordId = "d-99",
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
            id = (await db.Users.SingleAsync()).Id;
        }

        await using (var db = _fx.CreateContext())
        {
            await new UserUpsertService(db).UpsertFromOAuthAsync(
                DiscordOAuthProvider.ProviderName,
                new OAuthUserInfo("d-99", "attacker@evil.com", "Pat", null, EmailVerified: false));
        }

        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync(u => u.Id == id);
        user.Email.Should().Be("pat@example.com");
    }

    [Fact]
    public async Task UpsertFromOAuthAsync_UnknownProvider_Throws()
    {
        await _fx.ResetAsync();
        await using var db = _fx.CreateContext();
        var svc = new UserUpsertService(db);

        var act = async () => await svc.UpsertFromOAuthAsync(
            "unknown-provider",
            new OAuthUserInfo("x", "x@example.com", "x", null, true));

        // The switch default in UpsertFromOAuthAsync is a guard for a caller that adds a new
        // provider name without wiring it into the lookup — surface it as a programmer error
        // rather than silently creating an orphaned row.
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*unknown-provider*");
    }

    [Fact]
    public async Task UpsertFromOAuthAsync_ExistingDiscordUserWithAvatar_KeepsEmailUpdatesOnly()
    {
        // Existing user already has a custom avatar; provider re-asserts an avatar hash. The
        // avatar branch must NOT be taken (user's explicit PATCH /me choice wins), but the
        // verified-email update branch should still fire when the provider email changes.
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        Guid id;
        await using (var db = _fx.CreateContext())
        {
            db.Users.Add(new User
            {
                Username = "alex",
                Email = "old@example.com",
                AvatarUrl = "https://custom.example/alex.png",
                DiscordId = "d-alex",
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
            id = (await db.Users.SingleAsync()).Id;
        }

        await using (var db = _fx.CreateContext())
        {
            await new UserUpsertService(db).UpsertFromOAuthAsync(
                DiscordOAuthProvider.ProviderName,
                new OAuthUserInfo("d-alex", "new@example.com", "Alex", "https://cdn.discord.com/new.png", EmailVerified: true));
        }

        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync(u => u.Id == id);
        user.Email.Should().Be("new@example.com");
        user.AvatarUrl.Should().Be("https://custom.example/alex.png");
    }

    [Fact]
    public async Task UpsertFromOAuthAsync_LinkByEmail_FillsInMissingAvatarOnExisting()
    {
        // Existing user has no avatar; link-by-email path should set it from the provider.
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        Guid id;
        await using (var db = _fx.CreateContext())
        {
            db.Users.Add(new User
            {
                Username = "emily",
                Email = "emily@example.com",
                DiscordId = "d-emily",
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
            id = (await db.Users.SingleAsync()).Id;
        }

        await using (var db = _fx.CreateContext())
        {
            await new UserUpsertService(db).UpsertFromOAuthAsync(
                GoogleOAuthProvider.ProviderName,
                new OAuthUserInfo("g-emily", "emily@example.com", "Emily", "https://cdn.google/emily.png", EmailVerified: true));
        }

        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync(u => u.Id == id);
        user.GoogleId.Should().Be("g-emily");
        user.AvatarUrl.Should().Be("https://cdn.google/emily.png");
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
