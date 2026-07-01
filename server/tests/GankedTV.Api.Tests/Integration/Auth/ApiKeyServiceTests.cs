using FluentAssertions;
using GankedTV.Api.Auth.ApiKeys;
using GankedTV.Api.Auth.Tokens;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Tests.Integration.Auth;

[Collection("PostgresAuth")]
public class ApiKeyServiceTests
{
    private readonly PostgresFixture _fx;

    public ApiKeyServiceTests(PostgresFixture fx) => _fx = fx;

    private async Task<Guid> SeedUserAsync(string username = "keyowner")
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
    public async Task CreateAsync_PersistsHashNotRaw_AndReturnsPrefixedRaw()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();

        ApiKeyCreateResult result;
        await using (var db = _fx.CreateContext())
        {
            result = await new ApiKeyService(db).CreateAsync(userId, "rewynd", null);
        }

        result.IsSuccess.Should().BeTrue();
        result.RawKey.Should().StartWith(ApiKeyService.KeyPrefix);

        await using var verify = _fx.CreateContext();
        var row = await verify.ApiKeys.AsNoTracking().SingleAsync();
        row.KeyHash.Should().Be(OpaqueToken.Hash(result.RawKey!));
        row.KeyHash.Should().NotBe(result.RawKey);
        row.Name.Should().Be("rewynd");
        // The stored prefix is a non-secret leading fragment of the raw key.
        result.RawKey!.Should().StartWith(row.KeyPrefix);
        row.LastUsedAt.Should().BeNull();
        row.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_BlankName_StoredAsNull()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();

        await using (var db = _fx.CreateContext())
        {
            await new ApiKeyService(db).CreateAsync(userId, "   ", null);
        }

        await using var verify = _fx.CreateContext();
        (await verify.ApiKeys.AsNoTracking().SingleAsync()).Name.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_AtMaxActiveKeys_FailsTooManyKeys()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();

        await using (var seed = _fx.CreateContext())
        {
            for (var i = 0; i < ApiKeyService.MaxActiveKeysPerUser; i++)
            {
                seed.ApiKeys.Add(new ApiKey
                {
                    UserId = userId,
                    KeyHash = OpaqueToken.Hash($"seed-{i}"),
                    KeyPrefix = $"gtv_seed{i}",
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
            await seed.SaveChangesAsync();
        }

        await using var db = _fx.CreateContext();
        var result = await new ApiKeyService(db).CreateAsync(userId, "one too many", null);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(ApiKeyCreateError.TooManyKeys);
    }

    [Fact]
    public async Task CreateAsync_RevokedKeysDontCountTowardMax()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();

        await using (var seed = _fx.CreateContext())
        {
            for (var i = 0; i < ApiKeyService.MaxActiveKeysPerUser; i++)
            {
                seed.ApiKeys.Add(new ApiKey
                {
                    UserId = userId,
                    KeyHash = OpaqueToken.Hash($"revoked-{i}"),
                    KeyPrefix = $"gtv_rev{i}",
                    CreatedAt = DateTimeOffset.UtcNow,
                    RevokedAt = DateTimeOffset.UtcNow,
                });
            }
            await seed.SaveChangesAsync();
        }

        await using var db = _fx.CreateContext();
        var result = await new ApiKeyService(db).CreateAsync(userId, "fresh", null);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task AuthenticateAsync_ValidKey_ReturnsOwnerAndStampsLastUsed()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("alice");
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        string raw;
        await using (var db = _fx.CreateContext())
        {
            raw = (await new ApiKeyService(db, clock).CreateAsync(userId, null, null)).RawKey!;
        }

        User? user;
        await using (var db = _fx.CreateContext())
        {
            user = await new ApiKeyService(db, clock).AuthenticateAsync(raw);
        }

        user.Should().NotBeNull();
        user!.Id.Should().Be(userId);

        await using var verify = _fx.CreateContext();
        (await verify.ApiKeys.AsNoTracking().SingleAsync()).LastUsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task AuthenticateAsync_LastUsedThrottled_OnlyUpdatesWhenStale()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var t0 = DateTimeOffset.UtcNow;
        var clock = new FakeClock(t0);

        string raw;
        await using (var db = _fx.CreateContext())
        {
            raw = (await new ApiKeyService(db, clock).CreateAsync(userId, null, null)).RawKey!;
        }

        // First auth stamps LastUsedAt = t0.
        await using (var db = _fx.CreateContext())
        {
            await new ApiKeyService(db, clock).AuthenticateAsync(raw);
        }
        DateTimeOffset firstStamp;
        await using (var v = _fx.CreateContext())
        {
            firstStamp = (await v.ApiKeys.AsNoTracking().SingleAsync()).LastUsedAt!.Value;
        }

        // Within the throttle window → no update.
        clock.Set(t0.AddSeconds(30));
        await using (var db = _fx.CreateContext())
        {
            await new ApiKeyService(db, clock).AuthenticateAsync(raw);
        }
        await using (var v = _fx.CreateContext())
        {
            (await v.ApiKeys.AsNoTracking().SingleAsync()).LastUsedAt!.Value
                .Should().BeCloseTo(firstStamp, TimeSpan.FromMilliseconds(1));
        }

        // Past the throttle window → updated.
        clock.Set(t0.AddSeconds(90));
        await using (var db = _fx.CreateContext())
        {
            await new ApiKeyService(db, clock).AuthenticateAsync(raw);
        }
        await using (var v = _fx.CreateContext())
        {
            (await v.ApiKeys.AsNoTracking().SingleAsync()).LastUsedAt!.Value
                .Should().BeCloseTo(t0.AddSeconds(90), TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task AuthenticateAsync_RevokedKey_ReturnsNull()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();

        string raw;
        await using (var db = _fx.CreateContext())
        {
            var svc = new ApiKeyService(db);
            var created = await svc.CreateAsync(userId, null, null);
            raw = created.RawKey!;
            await svc.RevokeAsync(userId, created.Key!.Id);
        }

        await using var db2 = _fx.CreateContext();
        (await new ApiKeyService(db2).AuthenticateAsync(raw)).Should().BeNull();
    }

    [Fact]
    public async Task AuthenticateAsync_ExpiredKey_ReturnsNull()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        string raw;
        await using (var db = _fx.CreateContext())
        {
            raw = (await new ApiKeyService(db, clock)
                .CreateAsync(userId, null, clock.GetUtcNow().AddMinutes(5))).RawKey!;
        }

        clock.Set(clock.GetUtcNow().AddMinutes(10));
        await using var db2 = _fx.CreateContext();
        (await new ApiKeyService(db2, clock).AuthenticateAsync(raw)).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-ganked-key")]
    [InlineData("gtv_unknownbutwellformed")]
    public async Task AuthenticateAsync_InvalidOrUnknown_ReturnsNull(string raw)
    {
        await _fx.ResetAsync();
        await SeedUserAsync();

        await using var db = _fx.CreateContext();
        (await new ApiKeyService(db).AuthenticateAsync(raw)).Should().BeNull();
    }

    [Fact]
    public async Task RevokeAsync_OtherUsersKey_ReturnsFalseAndLeavesItLive()
    {
        await _fx.ResetAsync();
        var ownerId = await SeedUserAsync("owner");
        var attackerId = await SeedUserAsync("attacker");

        Guid keyId;
        await using (var db = _fx.CreateContext())
        {
            keyId = (await new ApiKeyService(db).CreateAsync(ownerId, null, null)).Key!.Id;
        }

        await using var db2 = _fx.CreateContext();
        var revoked = await new ApiKeyService(db2).RevokeAsync(attackerId, keyId);

        revoked.Should().BeFalse();
        await using var v = _fx.CreateContext();
        (await v.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == keyId)).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task RevokeAsync_AlreadyRevoked_ReturnsFalse()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();

        Guid keyId;
        await using (var db = _fx.CreateContext())
        {
            var svc = new ApiKeyService(db);
            keyId = (await svc.CreateAsync(userId, null, null)).Key!.Id;
            (await svc.RevokeAsync(userId, keyId)).Should().BeTrue();
        }

        await using var db2 = _fx.CreateContext();
        (await new ApiKeyService(db2).RevokeAsync(userId, keyId)).Should().BeFalse();
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlyOwnKeys_NewestFirst()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("me");
        var otherId = await SeedUserAsync("other");

        await using (var db = _fx.CreateContext())
        {
            var svc = new ApiKeyService(db);
            await svc.CreateAsync(userId, "first", null);
            await svc.CreateAsync(userId, "second", null);
            await svc.CreateAsync(otherId, "theirs", null);
        }

        await using var db2 = _fx.CreateContext();
        var keys = await new ApiKeyService(db2).ListAsync(userId);

        keys.Should().HaveCount(2);
        keys.Select(k => k.Name).Should().Equal("second", "first");
        keys.Should().OnlyContain(k => k.UserId == userId);
    }
}
