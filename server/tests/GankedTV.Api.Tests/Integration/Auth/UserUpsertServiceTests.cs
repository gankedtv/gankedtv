using FluentAssertions;
using GankedTV.Api.Auth;
using GankedTV.Api.Auth.Providers;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Tests.Integration.Auth;

[Collection("PostgresAuth")]
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
    public async Task UpsertFromOAuthAsync_ExistingDiscordIdWithUploadedAvatar_KeepsUserAvatar()
    {
        // The "upload" source is the only one we treat as a manual override that the OAuth
        // login MUST NOT clobber. A legacy NULL source is treated as OAuth-sourced (because
        // pre-migration all avatars came from OAuth); see UpsertFromOAuthAsync_LegacyNullSource_*
        // for that branch.
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        Guid id;
        await using (var db = _fx.CreateContext())
        {
            var existing = new User
            {
                Username = "zoe",
                Email = "zoe@example.com",
                AvatarUrl = "https://custom.example/zoe.png",
                AvatarSource = "upload",
                AvatarObjectKey = "zoe/avatar-abc.png",
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
        user.AvatarSource.Should().Be("upload");
        // Even when the active avatar is left alone, we still stash the latest provider URL
        // so DELETE /auth/me/avatar can restore it without waiting for the next login.
        user.OAuthAvatarUrl.Should().Be("https://cdn.discordapp.com/avatars/d-42/hash.png");
        user.OAuthAvatarSource.Should().Be("oauth:discord");
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
    public async Task UpsertFromOAuthAsync_UploadAvatar_KeepsAvatarUpdatesEmailOnly()
    {
        // Existing user has an uploaded avatar; provider re-asserts a different URL. Email
        // update fires normally; avatar is preserved (upload source is sacred).
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
                AvatarSource = "upload",
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
    public async Task UpsertFromOAuthAsync_AgainstExistingPasswordOnlyUser_LinksProviderIdWithoutDroppingPassword()
    {
        // Regression test for the OAuth→password-account merge direction (issue #62 spec).
        // The OAuth provider asserts a verified email matching an existing password-only
        // account: the provider id should attach to the existing row, and the password
        // hash must NOT be wiped. Without email verification on the password side, this
        // is the only direction where an automatic merge is safe.
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        Guid id;
        const string PreservedHash = "$argon2id$v=19$m=19456,t=2,p=1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        await using (var db = _fx.CreateContext())
        {
            db.Users.Add(new User
            {
                Username = "merge-target",
                Email = "merge@example.com",
                PasswordHash = PreservedHash,
                PasswordAlgo = "argon2id",
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
                new OAuthUserInfo("d-merge", "merge@example.com", "Whatever", null, EmailVerified: true));
        }

        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync(u => u.Id == id);
        user.DiscordId.Should().Be("d-merge");
        user.PasswordHash.Should().Be(PreservedHash);
        user.PasswordAlgo.Should().Be("argon2id");
    }

    [Fact]
    public async Task UpsertFromOAuthAsync_NewDiscordUser_SetsAvatarSourceAndOAuthStash()
    {
        // First-signup path: the freshly-created row should carry both the active AvatarSource
        // and the OAuth stash columns so the "Reset to OAuth avatar" UI works immediately.
        await _fx.ResetAsync();
        await using (var db = _fx.CreateContext())
        {
            await new UserUpsertService(db).UpsertFromOAuthAsync(
                DiscordOAuthProvider.ProviderName,
                new OAuthUserInfo("d-7", "u@example.com", "u", "https://cdn.discord/u.png", EmailVerified: true));
        }

        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync();
        user.AvatarUrl.Should().Be("https://cdn.discord/u.png");
        user.AvatarSource.Should().Be("oauth:discord");
        user.OAuthAvatarUrl.Should().Be("https://cdn.discord/u.png");
        user.OAuthAvatarSource.Should().Be("oauth:discord");
    }

    [Fact]
    public async Task UpsertFromOAuthAsync_LegacyNullSource_RefreshesAvatarOnLogin()
    {
        // The bug fix: pre-migration rows have AvatarSource = NULL. The provider rotating its
        // CDN hash leaves the old URL 404'ing; on next login we must refresh and classify the
        // source so subsequent rotations work too.
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        Guid id;
        await using (var db = _fx.CreateContext())
        {
            db.Users.Add(new User
            {
                Username = "legacy",
                DiscordId = "d-legacy",
                AvatarUrl = "https://cdn.discordapp.com/avatars/d-legacy/OLDHASH.png",
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
                new OAuthUserInfo("d-legacy", null, "legacy",
                    "https://cdn.discordapp.com/avatars/d-legacy/NEWHASH.png"));
        }

        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync(u => u.Id == id);
        user.AvatarUrl.Should().Be("https://cdn.discordapp.com/avatars/d-legacy/NEWHASH.png");
        user.AvatarSource.Should().Be("oauth:discord");
    }

    [Fact]
    public async Task UpsertFromOAuthAsync_LegacyNullSource_UnchangedUrl_ClassifiesSource()
    {
        // Legacy row whose AvatarUrl already matches the provider's current URL: nothing
        // to refresh, but the NULL source still needs to be stamped so subsequent CDN
        // rotations take the refresh branch instead of stalling on the null-source check.
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        const string url = "https://cdn.discordapp.com/avatars/d-legacy2/SAMEHASH.png";
        Guid id;
        await using (var db = _fx.CreateContext())
        {
            db.Users.Add(new User
            {
                Username = "legacy2",
                DiscordId = "d-legacy2",
                AvatarUrl = url,
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
                new OAuthUserInfo("d-legacy2", null, "legacy2", url));
        }

        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync(u => u.Id == id);
        user.AvatarUrl.Should().Be(url);
        user.AvatarSource.Should().Be("oauth:discord");
    }

    [Fact]
    public async Task UpsertFromOAuthAsync_SourceMatchesProvider_RefreshesAvatar()
    {
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        Guid id;
        await using (var db = _fx.CreateContext())
        {
            db.Users.Add(new User
            {
                Username = "rot",
                DiscordId = "d-rot",
                AvatarUrl = "https://cdn.discordapp.com/avatars/d-rot/OLD.png",
                AvatarSource = "oauth:discord",
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
                new OAuthUserInfo("d-rot", null, "rot",
                    "https://cdn.discordapp.com/avatars/d-rot/NEW.png"));
        }

        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync(u => u.Id == id);
        user.AvatarUrl.Should().Be("https://cdn.discordapp.com/avatars/d-rot/NEW.png");
    }

    [Fact]
    public async Task UpsertFromOAuthAsync_DifferentProvider_DoesNotStompAvatar_ButStashesOAuth()
    {
        // User's active avatar is sourced from Discord. They log in with Google. The active
        // avatar must NOT change, but the Google URL is stashed so a user who later prefers it
        // can still use it.
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        Guid id;
        await using (var db = _fx.CreateContext())
        {
            db.Users.Add(new User
            {
                Username = "dual",
                Email = "dual@example.com",
                DiscordId = "d-dual",
                GoogleId = "g-dual",
                AvatarUrl = "https://cdn.discordapp.com/avatars/d-dual/X.png",
                AvatarSource = "oauth:discord",
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
                new OAuthUserInfo("g-dual", "dual@example.com", "dual",
                    "https://lh.googleusercontent.com/dual.png", EmailVerified: true));
        }

        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync(u => u.Id == id);
        user.AvatarUrl.Should().Be("https://cdn.discordapp.com/avatars/d-dual/X.png");
        user.AvatarSource.Should().Be("oauth:discord");
        user.OAuthAvatarUrl.Should().Be("https://lh.googleusercontent.com/dual.png");
        user.OAuthAvatarSource.Should().Be("oauth:google");
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
