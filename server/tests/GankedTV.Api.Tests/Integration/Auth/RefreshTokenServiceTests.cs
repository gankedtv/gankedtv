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
        rows[0].FamilyId.Should().NotBe(Guid.Empty);
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
    public async Task RotateAsync_ExpiredToken_DoesNotTouchLiveFamilySiblings()
    {
        // An expired-but-never-revoked token presented for rotation must not trigger family
        // revocation — it's not a theft signal, just an unlucky user with a stale token.
        // Live siblings (e.g. another tab that is still within TTL) must remain usable.
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("mona");

        var family = Guid.NewGuid();
        const string expiredRaw = "manual-expired-token";
        string liveRaw;
        await using (var db = _fx.CreateContext())
        {
            db.RefreshTokens.Add(new RefreshToken
            {
                UserId = userId,
                TokenHash = RefreshTokenService.Hash(expiredRaw),
                FamilyId = family,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            liveRaw = await new RefreshTokenService(db, DefaultOpts()).IssueAsync(userId);
        }
        // Force the live token into the same family as the expired one.
        await using (var db = _fx.CreateContext())
        {
            await db.RefreshTokens
                .Where(t => t.TokenHash == RefreshTokenService.Hash(liveRaw))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.FamilyId, family));
        }

        await using (var db = _fx.CreateContext())
        {
            var act = () => new RefreshTokenService(db, DefaultOpts()).RotateAsync(expiredRaw);
            await act.Should().ThrowAsync<InvalidRefreshTokenException>();
        }

        // Sibling must still be rotatable.
        await using var rotateDb = _fx.CreateContext();
        var rotated = await new RefreshTokenService(rotateDb, DefaultOpts()).RotateAsync(liveRaw);
        rotated.NewRawToken.Should().NotBeNullOrEmpty();
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
    public async Task RevokeAsync_UnknownToken_NoOp()
    {
        // RevokeAsync is called eagerly (e.g. sign-out with stored raw token that might not
        // exist any more). It must not throw when the token was never issued.
        await _fx.ResetAsync();

        await using var db = _fx.CreateContext();
        var act = () => new RefreshTokenService(db, DefaultOpts()).RevokeAsync("never-issued");

        await act.Should().NotThrowAsync();
        (await db.RefreshTokens.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task RevokeAsync_AlreadyRevoked_KeepsOriginalTimestamp()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("greta");

        string raw;
        await using (var db = _fx.CreateContext())
        {
            raw = await new RefreshTokenService(db, DefaultOpts()).IssueAsync(userId);
        }
        await using (var db = _fx.CreateContext())
        {
            await new RefreshTokenService(db, DefaultOpts()).RevokeAsync(raw);
        }

        DateTimeOffset? firstRevokedAt;
        await using (var db = _fx.CreateContext())
        {
            firstRevokedAt = (await db.RefreshTokens.AsNoTracking().SingleAsync()).RevokedAt;
        }

        await Task.Delay(20);

        await using (var db = _fx.CreateContext())
        {
            await new RefreshTokenService(db, DefaultOpts()).RevokeAsync(raw);
        }

        await using var verify = _fx.CreateContext();
        var row = await verify.RefreshTokens.AsNoTracking().SingleAsync();
        row.RevokedAt.Should().Be(firstRevokedAt);
    }

    [Fact]
    public async Task RotateAsync_CopiesFamilyIdFromParent()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("hugo");

        string raw;
        await using (var db = _fx.CreateContext())
        {
            raw = await new RefreshTokenService(db, DefaultOpts()).IssueAsync(userId);
        }

        Guid parentFamily;
        await using (var db = _fx.CreateContext())
        {
            parentFamily = (await db.RefreshTokens.AsNoTracking().SingleAsync()).FamilyId;
        }

        RotateResult result;
        await using (var db = _fx.CreateContext())
        {
            result = await new RefreshTokenService(db, DefaultOpts()).RotateAsync(raw);
        }

        await using var verify = _fx.CreateContext();
        var families = await verify.RefreshTokens.AsNoTracking().Select(t => t.FamilyId).Distinct().ToListAsync();
        families.Should().ContainSingle().Which.Should().Be(parentFamily);
    }

    [Fact]
    public async Task RotateAsync_RotationChain_SharesOneFamily()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("ivan");

        string current;
        await using (var db = _fx.CreateContext())
        {
            current = await new RefreshTokenService(db, DefaultOpts()).IssueAsync(userId);
        }

        for (var i = 0; i < 3; i++)
        {
            await using var db = _fx.CreateContext();
            current = (await new RefreshTokenService(db, DefaultOpts()).RotateAsync(current)).NewRawToken;
        }

        await using var verify = _fx.CreateContext();
        var rows = await verify.RefreshTokens.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(4);
        rows.Select(r => r.FamilyId).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task RotateAsync_ReplayOfRevokedToken_RevokesEntireFamily()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("judy");
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        string tokenA;
        await using (var db = _fx.CreateContext())
        {
            tokenA = await new RefreshTokenService(db, DefaultOpts(), clock: clock).IssueAsync(userId);
        }

        // Legitimate rotation: A -> B. A is now revoked.
        string tokenB;
        await using (var db = _fx.CreateContext())
        {
            tokenB = (await new RefreshTokenService(db, DefaultOpts(), clock: clock).RotateAsync(tokenA)).NewRawToken;
        }

        // Advance past the replay grace window so the next presentation of A is treated as theft.
        clock.Set(clock.GetUtcNow().AddMinutes(5));

        await using (var rotateDb = _fx.CreateContext())
        {
            var act = () => new RefreshTokenService(rotateDb, DefaultOpts(), clock: clock).RotateAsync(tokenA);
            await act.Should().ThrowAsync<InvalidRefreshTokenException>();
        }

        await using var verify = _fx.CreateContext();
        var rows = await verify.RefreshTokens.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.RevokedAt != null);

        // Token B (the previously-live successor) cannot be rotated either.
        await using var rotateB = _fx.CreateContext();
        var rotateActB = () => new RefreshTokenService(rotateB, DefaultOpts(), clock: clock).RotateAsync(tokenB);
        await rotateActB.Should().ThrowAsync<InvalidRefreshTokenException>();
    }

    [Fact]
    public async Task RotateAsync_ConcurrentRotationDoesNotTriggerFamilyRevoke()
    {
        // The CAS-loser path is reachable in legitimate flows (multiple tabs racing to refresh).
        // Fresh revocations within the grace window must NOT trigger family revoke, otherwise
        // the user's just-issued successor token would be killed on every concurrent refresh.
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("liam");

        string tokenA;
        await using (var db = _fx.CreateContext())
        {
            tokenA = await new RefreshTokenService(db, DefaultOpts()).IssueAsync(userId);
        }

        // Legitimate rotation A -> B.
        await using (var db = _fx.CreateContext())
        {
            await new RefreshTokenService(db, DefaultOpts()).RotateAsync(tokenA);
        }

        // Immediately replay A — should throw (token revoked) but leave B live.
        await using (var db = _fx.CreateContext())
        {
            var act = () => new RefreshTokenService(db, DefaultOpts()).RotateAsync(tokenA);
            await act.Should().ThrowAsync<InvalidRefreshTokenException>();
        }

        await using var verify = _fx.CreateContext();
        var liveRows = await verify.RefreshTokens.AsNoTracking().Where(t => t.RevokedAt == null).ToListAsync();
        liveRows.Should().ContainSingle("the freshly-issued successor must remain live during a concurrent rotation race");
    }

    [Fact]
    public async Task RotateAsync_ReplayDoesNotTouchOtherFamilies()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("kim");
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        // Two independent login sessions = two families.
        string tokenF1, tokenF2;
        await using (var db = _fx.CreateContext())
        {
            tokenF1 = await new RefreshTokenService(db, DefaultOpts(), clock: clock).IssueAsync(userId);
        }
        await using (var db = _fx.CreateContext())
        {
            tokenF2 = await new RefreshTokenService(db, DefaultOpts(), clock: clock).IssueAsync(userId);
        }

        // Rotate F1 to revoke its only token, then replay it past the grace window.
        await using (var db = _fx.CreateContext())
        {
            await new RefreshTokenService(db, DefaultOpts(), clock: clock).RotateAsync(tokenF1);
        }
        clock.Set(clock.GetUtcNow().AddMinutes(5));
        await using (var db = _fx.CreateContext())
        {
            var act = () => new RefreshTokenService(db, DefaultOpts(), clock: clock).RotateAsync(tokenF1);
            await act.Should().ThrowAsync<InvalidRefreshTokenException>();
        }

        // F2 must still be live and rotatable.
        await using var rotateF2 = _fx.CreateContext();
        var rotated = await new RefreshTokenService(rotateF2, DefaultOpts(), clock: clock).RotateAsync(tokenF2);
        rotated.NewRawToken.Should().NotBeNullOrEmpty();
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
