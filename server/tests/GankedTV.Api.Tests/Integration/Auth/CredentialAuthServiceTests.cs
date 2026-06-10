using FluentAssertions;
using GankedTV.Api.Auth;
using GankedTV.Api.Auth.Passwords;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Tests.Integration.Auth;

// Direct service-level tests for branches the endpoint can't reach in practice
// (DataAnnotations [EmailAddress] intercepts malformed emails before the handler
// runs, but the service must still handle them defensively for unit consumers).
[Collection("PostgresAuth")]
public class CredentialAuthServiceTests
{
    private readonly PostgresFixture _fx;

    public CredentialAuthServiceTests(PostgresFixture fx) => _fx = fx;

    private CredentialAuthService NewService(GankedTV.Api.Data.GankedTvDbContext db) =>
        new(db, new Argon2idPasswordHasher(), TimeProvider.System);

    [Fact]
    public async Task TryRegisterAsync_WithExplicitClock_StampsCreatedAtFromClock()
    {
        await _fx.ResetAsync();
        await using var db = _fx.CreateContext();
        var clock = new FakeClock(new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero));
        var svc = new CredentialAuthService(db, new Argon2idPasswordHasher(), clock);

        var result = await svc.TryRegisterAsync("clock@example.com", "clockuser", "long-fine-password");

        ((RegisterResult.SuccessResult)result).User.CreatedAt.Should().Be(clock.GetUtcNow());
    }

    [Fact]
    public async Task TryRegisterAsync_HappyPath_ReturnsSuccessWithUser()
    {
        await _fx.ResetAsync();
        await using var db = _fx.CreateContext();
        var svc = NewService(db);

        var result = await svc.TryRegisterAsync("alice@example.com", "alice", "long-fine-password");

        result.Should().BeOfType<RegisterResult.SuccessResult>();
        var user = ((RegisterResult.SuccessResult)result).User;
        user.Email.Should().Be("alice@example.com");
        user.Username.Should().Be("alice");
        user.PasswordHash.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("@example.com")]
    public async Task TryRegisterAsync_BadEmail_ReturnsInvalidEmail(string email)
    {
        await _fx.ResetAsync();
        await using var db = _fx.CreateContext();
        var svc = NewService(db);

        var result = await svc.TryRegisterAsync(email, "user", "long-fine-password");

        result.Should().BeOfType<RegisterResult.InvalidEmailResult>();
    }

    [Fact]
    public async Task TryRegisterAsync_WeakPassword_ReturnsInvalidPasswordWithError()
    {
        await _fx.ResetAsync();
        await using var db = _fx.CreateContext();
        var svc = NewService(db);

        var result = await svc.TryRegisterAsync("e@example.com", "user", "tiny");

        result.Should().BeOfType<RegisterResult.InvalidPasswordResult>();
        ((RegisterResult.InvalidPasswordResult)result).Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TryRegisterAsync_NormalisesEmailToLowercase()
    {
        await _fx.ResetAsync();
        await using var db = _fx.CreateContext();
        var svc = NewService(db);

        await svc.TryRegisterAsync("Alice@Example.COM", "alice", "long-fine-password");

        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync();
        user.Email.Should().Be("alice@example.com");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public async Task TryLoginAsync_BadEmailFormat_ReturnsNullAfterDummyVerify(string email)
    {
        // Important behaviour: bad-format emails still pay the Argon2 cost (constant-time
        // equivalent) so attackers can't probe address shape via response timing.
        await _fx.ResetAsync();
        await using var db = _fx.CreateContext();
        var svc = NewService(db);

        var user = await svc.TryLoginAsync(email, "any-password");

        user.Should().BeNull();
    }

    [Fact]
    public async Task TryLoginAsync_UnknownEmail_ReturnsNull()
    {
        await _fx.ResetAsync();
        await using var db = _fx.CreateContext();
        var svc = NewService(db);

        var user = await svc.TryLoginAsync("nobody@example.com", "any-password-12345");

        user.Should().BeNull();
    }

    [Fact]
    public async Task TryLoginAsync_OAuthOnlyUser_ReturnsNull()
    {
        await _fx.ResetAsync();
        await using (var db = _fx.CreateContext())
        {
            db.Users.Add(new User
            {
                Username = "oauthonly",
                Email = "oauthonly@example.com",
                DiscordId = "d-oauth-only",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using var queryDb = _fx.CreateContext();
        var svc = NewService(queryDb);

        var user = await svc.TryLoginAsync("oauthonly@example.com", "anything-12345");

        user.Should().BeNull();
    }

    [Fact]
    public async Task SetPasswordAsync_UserNotFound_ReturnsUserNotFound()
    {
        await _fx.ResetAsync();
        await using var db = _fx.CreateContext();
        var svc = NewService(db);

        var result = await svc.SetPasswordAsync(Guid.NewGuid(), null, "fresh-strong-password");

        result.Should().BeOfType<SetPasswordResult.UserNotFoundResult>();
    }

    [Fact]
    public async Task SetPasswordAsync_OAuthOnlyUser_DoesNotRequireCurrentPassword()
    {
        await _fx.ResetAsync();
        Guid userId;
        await using (var db = _fx.CreateContext())
        {
            var user = new User
            {
                Username = "merger",
                Email = "merger@example.com",
                DiscordId = "d-merger",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        await using var ctx = _fx.CreateContext();
        var svc = NewService(ctx);

        var result = await svc.SetPasswordAsync(userId, currentPassword: null, "new-strong-password");

        result.Should().BeOfType<SetPasswordResult.OkResult>();

        await using var verify = _fx.CreateContext();
        var after = await verify.Users.SingleAsync();
        after.PasswordHash.Should().NotBeNullOrEmpty();
        after.PasswordAlgo.Should().Be("argon2id");
    }

    [Fact]
    public async Task SetPasswordAsync_WithExistingPassword_RejectsMissingCurrent()
    {
        await _fx.ResetAsync();
        var hasher = new Argon2idPasswordHasher();
        Guid userId;
        await using (var db = _fx.CreateContext())
        {
            var user = new User
            {
                Username = "rotator",
                Email = "rotator@example.com",
                PasswordHash = hasher.Hash("original-strong-password"),
                PasswordAlgo = "argon2id",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        await using var ctx = _fx.CreateContext();
        var svc = NewService(ctx);

        // No currentPassword supplied → 400-equivalent.
        var result = await svc.SetPasswordAsync(userId, currentPassword: null, "new-strong-password");

        result.Should().BeOfType<SetPasswordResult.WrongCurrentPasswordResult>();
    }

    [Fact]
    public async Task SetPasswordAsync_WeakNewPassword_ReturnsInvalidPasswordWithError()
    {
        await _fx.ResetAsync();
        Guid userId;
        await using (var db = _fx.CreateContext())
        {
            var user = new User
            {
                Username = "weakset",
                Email = "weakset@example.com",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        await using var ctx = _fx.CreateContext();
        var svc = NewService(ctx);

        var result = await svc.SetPasswordAsync(userId, null, "tiny");

        result.Should().BeOfType<SetPasswordResult.InvalidPasswordResult>();
        ((SetPasswordResult.InvalidPasswordResult)result).Error.Should().NotBeNullOrEmpty();
    }
}
