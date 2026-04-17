using FluentAssertions;
using GankedTV.Api.Auth.Tokens;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Tests.Integration.Auth;

[Collection("Postgres")]
public class RefreshTokenServiceTests
{
    private readonly PostgresFixture _fx;

    public RefreshTokenServiceTests(PostgresFixture fx) => _fx = fx;

    private static IOptions<RefreshTokenOptions> DefaultOpts() =>
        Options.Create(new RefreshTokenOptions { ExpiryDays = 30 });

    private async Task<Guid> SeedUserAsync(string username = "alice")
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        var user = new User
        {
            Username = username,
            Email = $"{username}@example.com",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task IssueAsync_NewUser_PersistsHashNotRaw()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();

        string raw;
        await using (var db = _fx.CreateContext())
        {
            var svc = new RefreshTokenService(db, DefaultOpts());
            raw = await svc.IssueAsync(userId);
        }

        await using var verify = _fx.CreateContext();
        var rows = await verify.RefreshTokens.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].TokenHash.Should().NotBe(raw);
        rows[0].TokenHash.Should().Be(RefreshTokenService.Hash(raw));
        rows[0].RevokedAt.Should().BeNull();
        rows[0].ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddDays(29));
    }

    [Fact]
    public async Task RotateAsync_ValidToken_RevokesOldAndInsertsNew()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("bob");

        string raw;
        await using (var db = _fx.CreateContext())
        {
            raw = await new RefreshTokenService(db, DefaultOpts()).IssueAsync(userId);
        }

        RotateResult result;
        await using (var db = _fx.CreateContext())
        {
            result = await new RefreshTokenService(db, DefaultOpts()).RotateAsync(raw);
        }

        result.User.Id.Should().Be(userId);
        result.NewRawToken.Should().NotBe(raw);

        await using var verify = _fx.CreateContext();
        var rows = await verify.RefreshTokens.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(r => r.TokenHash == RefreshTokenService.Hash(raw)).RevokedAt.Should().NotBeNull();
        rows.Single(r => r.TokenHash == RefreshTokenService.Hash(result.NewRawToken)).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task RotateAsync_AlreadyRevoked_Throws()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("carol");

        string raw;
        await using (var db = _fx.CreateContext())
        {
            raw = await new RefreshTokenService(db, DefaultOpts()).IssueAsync(userId);
        }

        await using (var db = _fx.CreateContext())
        {
            await new RefreshTokenService(db, DefaultOpts()).RevokeAsync(raw);
        }

        await using var rotateDb = _fx.CreateContext();
        var act = () => new RefreshTokenService(rotateDb, DefaultOpts()).RotateAsync(raw);
        await act.Should().ThrowAsync<InvalidRefreshTokenException>();
    }

    [Fact]
    public async Task RotateAsync_Expired_Throws()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("dan");

        var now = DateTimeOffset.UtcNow;
        string raw = "manual-raw-token-value-for-expiry-test";
        await using (var db = _fx.CreateContext())
        {
            db.RefreshTokens.Add(new RefreshToken
            {
                UserId = userId,
                TokenHash = RefreshTokenService.Hash(raw),
                ExpiresAt = now.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }

        await using var rotateDb = _fx.CreateContext();
        var act = () => new RefreshTokenService(rotateDb, DefaultOpts()).RotateAsync(raw);
        await act.Should().ThrowAsync<InvalidRefreshTokenException>();
    }

    [Fact]
    public async Task RotateAsync_Unknown_Throws()
    {
        await _fx.ResetAsync();

        await using var db = _fx.CreateContext();
        var act = () => new RefreshTokenService(db, DefaultOpts()).RotateAsync("never-issued");
        await act.Should().ThrowAsync<InvalidRefreshTokenException>();
    }

    [Fact]
    public async Task RevokeAsync_ValidToken_SetsRevokedAt()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("eve");

        string raw;
        await using (var db = _fx.CreateContext())
        {
            raw = await new RefreshTokenService(db, DefaultOpts()).IssueAsync(userId);
        }

        await using (var db = _fx.CreateContext())
        {
            await new RefreshTokenService(db, DefaultOpts()).RevokeAsync(raw);
        }

        await using var verify = _fx.CreateContext();
        var row = await verify.RefreshTokens.AsNoTracking().SingleAsync();
        row.RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RotateAsync_ConcurrentCallers_OnlyOneSucceeds()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("frank");

        string raw;
        await using (var db = _fx.CreateContext())
        {
            raw = await new RefreshTokenService(db, DefaultOpts()).IssueAsync(userId);
        }

        async Task<bool> TryRotate()
        {
            await using var db = _fx.CreateContext();
            try
            {
                await new RefreshTokenService(db, DefaultOpts()).RotateAsync(raw);
                return true;
            }
            catch (InvalidRefreshTokenException)
            {
                return false;
            }
        }

        var outcomes = await Task.WhenAll(TryRotate(), TryRotate(), TryRotate());

        outcomes.Count(ok => ok).Should().Be(1);
        outcomes.Count(ok => !ok).Should().Be(2);
    }
}
