using FluentAssertions;
using GankedTV.Api.Auth.ApiKeys;
using GankedTV.Api.Auth.Devices;
using GankedTV.Api.Auth.Tokens;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Tests.Integration.Auth;

[Collection("PostgresAuth")]
public class DeviceAuthorizationServiceTests
{
    private readonly PostgresFixture _fx;

    public DeviceAuthorizationServiceTests(PostgresFixture fx) => _fx = fx;

    private DeviceAuthorizationService NewService(GankedTV.Api.Data.GankedTvDbContext db, TimeProvider clock) =>
        new(db, new ApiKeyService(db, clock), clock);

    private async Task<Guid> SeedUserAsync(string username = "approver")
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        var user = new User { Username = username, Email = $"{username}@example.com", CreatedAt = now, UpdatedAt = now };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task StartAsync_PersistsHashNotRaw_ReturnsPrefixedDeviceCodeAndUserCode()
    {
        await _fx.ResetAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        DeviceStartResult result;
        await using (var db = _fx.CreateContext())
        {
            result = await NewService(db, clock).StartAsync("rewynd");
        }

        result.DeviceCode.Should().StartWith(DeviceAuthorizationService.DeviceCodePrefix);
        result.UserCode.Should().HaveLength(8);
        result.IntervalSeconds.Should().Be(DeviceAuthorizationService.IntervalSeconds);

        await using var verify = _fx.CreateContext();
        var row = await verify.DeviceAuthorizations.AsNoTracking().SingleAsync();
        row.DeviceCodeHash.Should().Be(OpaqueToken.Hash(result.DeviceCode));
        row.DeviceCodeHash.Should().NotBe(result.DeviceCode);
        row.Status.Should().Be(DeviceAuthorizationStatuses.Pending);
        row.ClientName.Should().Be("rewynd");
        row.UserId.Should().BeNull();
    }

    [Fact]
    public async Task PollAsync_Pending_ThenSlowDownWithinInterval_ThenPendingAfter()
    {
        await _fx.ResetAsync();
        var t0 = DateTimeOffset.UtcNow;
        var clock = new FakeClock(t0);

        string deviceCode;
        await using (var db = _fx.CreateContext())
        {
            deviceCode = (await NewService(db, clock).StartAsync(null)).DeviceCode;
        }

        // First poll → pending.
        await using (var db = _fx.CreateContext())
        {
            (await NewService(db, clock).PollAsync(deviceCode)).Status.Should().Be(DevicePollStatus.Pending);
        }
        // Immediate re-poll (within the 5s interval) → slow_down.
        await using (var db = _fx.CreateContext())
        {
            (await NewService(db, clock).PollAsync(deviceCode)).Status.Should().Be(DevicePollStatus.SlowDown);
        }
        // After the interval → pending again.
        clock.Set(t0.AddSeconds(DeviceAuthorizationService.IntervalSeconds + 1));
        await using (var db = _fx.CreateContext())
        {
            (await NewService(db, clock).PollAsync(deviceCode)).Status.Should().Be(DevicePollStatus.Pending);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-device-code")]
    [InlineData("dvc_unknownbutwellformed")]
    public async Task PollAsync_UnknownOrMalformed_ReturnsExpired(string deviceCode)
    {
        await _fx.ResetAsync();
        await using var db = _fx.CreateContext();
        (await NewService(db, new FakeClock(DateTimeOffset.UtcNow)).PollAsync(deviceCode)).Status
            .Should().Be(DevicePollStatus.Expired);
    }

    [Fact]
    public async Task PollAsync_AfterExpiry_ReturnsExpired()
    {
        await _fx.ResetAsync();
        var t0 = DateTimeOffset.UtcNow;
        var clock = new FakeClock(t0);

        string deviceCode;
        await using (var db = _fx.CreateContext())
        {
            deviceCode = (await NewService(db, clock).StartAsync(null)).DeviceCode;
        }

        clock.Set(t0 + DeviceAuthorizationService.Lifetime + TimeSpan.FromSeconds(1));
        await using var db2 = _fx.CreateContext();
        (await NewService(db2, clock).PollAsync(deviceCode)).Status.Should().Be(DevicePollStatus.Expired);
    }

    [Fact]
    public async Task ApproveThenPoll_MintsKeyForApprover_AndConsumesTheRow()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        DeviceStartResult start;
        await using (var db = _fx.CreateContext())
        {
            start = await NewService(db, clock).StartAsync("rewynd");
        }

        await using (var db = _fx.CreateContext())
        {
            (await NewService(db, clock).ApproveAsync(userId, DeviceAuthorizationService.FormatUserCode(start.UserCode)))
                .Should().Be(DeviceDecisionOutcome.Ok);
        }

        DevicePollResult poll;
        await using (var db = _fx.CreateContext())
        {
            poll = await NewService(db, clock).PollAsync(start.DeviceCode);
        }

        poll.Status.Should().Be(DevicePollStatus.Approved);
        poll.ApiKey.Should().StartWith(ApiKeyService.KeyPrefix);

        await using var verify = _fx.CreateContext();
        // Device row consumed (single-use).
        (await verify.DeviceAuthorizations.CountAsync()).Should().Be(0);
        // A key was minted for the approver and it authenticates.
        var key = await verify.ApiKeys.AsNoTracking().SingleAsync();
        key.UserId.Should().Be(userId);
        key.Name.Should().Be("rewynd");
        (await new ApiKeyService(verify, clock).AuthenticateAsync(poll.ApiKey!))!.Id.Should().Be(userId);
    }

    [Fact]
    public async Task ApproveThenPoll_AtKeyCap_ReturnsTooManyKeys_AndKeepsTheApprovedRow()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        // Fill the approver's key quota so the mint on exchange fails.
        await using (var seed = _fx.CreateContext())
        {
            for (var i = 0; i < ApiKeyService.MaxActiveKeysPerUser; i++)
            {
                seed.ApiKeys.Add(new ApiKey
                {
                    UserId = userId,
                    KeyHash = OpaqueToken.Hash($"cap-{i}"),
                    KeyPrefix = $"gtv_cap{i}",
                    CreatedAt = clock.GetUtcNow(),
                });
            }
            await seed.SaveChangesAsync();
        }

        DeviceStartResult start;
        await using (var db = _fx.CreateContext())
        {
            start = await NewService(db, clock).StartAsync("rewynd");
        }
        await using (var db = _fx.CreateContext())
        {
            await NewService(db, clock).ApproveAsync(userId, start.UserCode);
        }

        DevicePollResult poll;
        await using (var db = _fx.CreateContext())
        {
            poll = await NewService(db, clock).PollAsync(start.DeviceCode);
        }

        poll.Status.Should().Be(DevicePollStatus.TooManyKeys);
        // The approved row must survive so a re-poll (after the user frees a slot) can recover —
        // and so the client keeps seeing too_many_keys rather than expired_token.
        await using var verify = _fx.CreateContext();
        var row = await verify.DeviceAuthorizations.AsNoTracking().SingleAsync();
        row.Status.Should().Be(DeviceAuthorizationStatuses.Approved);
    }

    [Fact]
    public async Task PollAsync_AfterSuccessfulExchange_IsSingleUse()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        DeviceStartResult start;
        await using (var db = _fx.CreateContext())
        {
            start = await NewService(db, clock).StartAsync("rewynd");
        }
        await using (var db = _fx.CreateContext())
        {
            await NewService(db, clock).ApproveAsync(userId, start.UserCode);
        }
        await using (var db = _fx.CreateContext())
        {
            (await NewService(db, clock).PollAsync(start.DeviceCode)).Status.Should().Be(DevicePollStatus.Approved);
        }

        // The device code is consumed on first successful exchange; a replay yields expired, and
        // only one key was ever minted.
        await using (var db = _fx.CreateContext())
        {
            (await NewService(db, clock).PollAsync(start.DeviceCode)).Status.Should().Be(DevicePollStatus.Expired);
        }
        await using var verify = _fx.CreateContext();
        (await verify.ApiKeys.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task PollAsync_Denied_ReturnsDenied()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        DeviceStartResult start;
        await using (var db = _fx.CreateContext())
        {
            start = await NewService(db, clock).StartAsync(null);
        }
        await using (var db = _fx.CreateContext())
        {
            await NewService(db, clock).DenyAsync(userId, start.UserCode);
        }
        await using var db2 = _fx.CreateContext();
        (await NewService(db2, clock).PollAsync(start.DeviceCode)).Status.Should().Be(DevicePollStatus.Denied);
    }

    [Fact]
    public async Task ApproveAsync_UnknownCode_NotFound()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        await using var db = _fx.CreateContext();
        (await NewService(db, new FakeClock(DateTimeOffset.UtcNow)).ApproveAsync(userId, "ZZZZ-ZZZZ"))
            .Should().Be(DeviceDecisionOutcome.NotFound);
    }

    [Fact]
    public async Task ApproveAsync_AlreadyDecided_Conflict()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        DeviceStartResult start;
        await using (var db = _fx.CreateContext())
        {
            start = await NewService(db, clock).StartAsync(null);
        }
        await using (var db = _fx.CreateContext())
        {
            var svc = NewService(db, clock);
            (await svc.ApproveAsync(userId, start.UserCode)).Should().Be(DeviceDecisionOutcome.Ok);
        }
        await using var db2 = _fx.CreateContext();
        (await NewService(db2, clock).DenyAsync(userId, start.UserCode)).Should().Be(DeviceDecisionOutcome.AlreadyDecided);
    }

    [Fact]
    public async Task LookupByUserCode_ReturnsClientNameAndStatus_NormalizingInput()
    {
        await _fx.ResetAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        DeviceStartResult start;
        await using (var db = _fx.CreateContext())
        {
            start = await NewService(db, clock).StartAsync("rewynd");
        }

        await using var db2 = _fx.CreateContext();
        // Lower-case + dashed input must still resolve (normalization).
        var lookup = await NewService(db2, clock)
            .LookupByUserCodeAsync(DeviceAuthorizationService.FormatUserCode(start.UserCode).ToLowerInvariant());

        lookup.Should().NotBeNull();
        lookup!.ClientName.Should().Be("rewynd");
        lookup.Status.Should().Be(DeviceAuthorizationStatuses.Pending);
    }
}
