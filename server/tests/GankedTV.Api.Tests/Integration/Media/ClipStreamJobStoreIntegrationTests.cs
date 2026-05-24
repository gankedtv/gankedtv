using FluentAssertions;
using GankedTV.Api.Clips;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Tests.Integration.Media;

[Collection("Postgres")]
public class ClipStreamJobStoreIntegrationTests
{
    private readonly PostgresFixture _fx;

    public ClipStreamJobStoreIntegrationTests(PostgresFixture fx) => _fx = fx;

    private GankedTvDbContext NewContext() => _fx.CreateContext();
    private ClipStreamJobStore NewStore(DateTimeOffset now, GankedTvDbContext db) => new(db, new FakeClock(now));

    private async Task<Guid> SeedUserAsync(string username)
    {
        await using var db = NewContext();
        var u = new User
        {
            Username = username,
            Email = $"{username}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u.Id;
    }

    private async Task<Guid> SeedClipAsync(Guid userId, string status, short? height = 720)
    {
        await using var db = NewContext();
        var clip = new Clip
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = "t",
            VideoKey = $"{userId}/clip.cmp.mp4",
            ShareCode = ShareCodeGenerator.Next(),
            Status = status,
            Height = height,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Clips.Add(clip);
        await db.SaveChangesAsync();
        return clip.Id;
    }

    [Fact]
    public async Task EnqueueAsync_InsertsPendingOnce_SecondIsNoOp()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("alice");
        var clipId = await SeedClipAsync(userId, ClipStatuses.Ready);

        await using var db = NewContext();
        var store = NewStore(DateTimeOffset.UtcNow, db);

        await store.EnqueueAsync(clipId, CancellationToken.None);
        await store.EnqueueAsync(clipId, CancellationToken.None);

        await using var verify = NewContext();
        var rows = await verify.ClipStreamJobs.AsNoTracking().Where(j => j.ClipId == clipId).ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Status.Should().Be(ClipStreamJobStatuses.Pending);
    }

    [Fact]
    public async Task EnqueueAsync_ResetsStaleFailedRowToPending_LeavesFreshFailedRow()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("rex");
        var staleClip = await SeedClipAsync(userId, ClipStatuses.Ready);
        var freshClip = await SeedClipAsync(userId, ClipStatuses.Ready);
        var now = DateTimeOffset.UtcNow;

        await using (var seed = NewContext())
        {
            // Stale failed (10min ago, past the 5min cooldown) + fresh failed (1min ago).
            seed.ClipStreamJobs.Add(new ClipStreamJob
            {
                ClipId = staleClip,
                Status = ClipStreamJobStatuses.Failed,
                ProcessingAttempts = 3,
                CreatedAt = now.AddMinutes(-20),
                UpdatedAt = now.AddMinutes(-10),
            });
            seed.ClipStreamJobs.Add(new ClipStreamJob
            {
                ClipId = freshClip,
                Status = ClipStreamJobStatuses.Failed,
                ProcessingAttempts = 3,
                CreatedAt = now.AddMinutes(-1),
                UpdatedAt = now.AddMinutes(-1),
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            var store = NewStore(now, db);
            await store.EnqueueAsync(staleClip, CancellationToken.None);
            await store.EnqueueAsync(freshClip, CancellationToken.None);
        }

        await using var verify = NewContext();
        var stale = await verify.ClipStreamJobs.AsNoTracking().SingleAsync(j => j.ClipId == staleClip);
        stale.Status.Should().Be(ClipStreamJobStatuses.Pending); // recovered
        stale.ProcessingAttempts.Should().Be(0);
        var fresh = await verify.ClipStreamJobs.AsNoTracking().SingleAsync(j => j.ClipId == freshClip);
        fresh.Status.Should().Be(ClipStreamJobStatuses.Failed); // within cooldown — untouched
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsStatusOrNull()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("bob");
        var clipId = await SeedClipAsync(userId, ClipStatuses.Ready);

        await using var db = NewContext();
        var store = NewStore(DateTimeOffset.UtcNow, db);

        (await store.GetStatusAsync(clipId, CancellationToken.None)).Should().BeNull();
        await store.EnqueueAsync(clipId, CancellationToken.None);
        (await store.GetStatusAsync(clipId, CancellationToken.None)).Should().Be(ClipStreamJobStatuses.Pending);
    }

    [Fact]
    public async Task ClaimNextAsync_ClaimsPendingForReadyClip_CarriesKeyAndHeight()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("carol");
        var clipId = await SeedClipAsync(userId, ClipStatuses.Ready, height: 1080);
        var now = DateTimeOffset.UtcNow;

        await using (var seed = NewContext())
        {
            await NewStore(now, seed).EnqueueAsync(clipId, CancellationToken.None);
        }

        await using var db = NewContext();
        var store = NewStore(now, db);

        var claimed = await store.ClaimNextAsync(TimeSpan.FromMinutes(5), 3, CancellationToken.None);

        claimed.Should().NotBeNull();
        claimed!.ClipId.Should().Be(clipId);
        claimed.VideoKey.Should().Be($"{userId}/clip.cmp.mp4");
        claimed.SourceHeight.Should().Be(1080);
        claimed.AttemptNumber.Should().Be(1);
    }

    [Fact]
    public async Task ClaimNextAsync_ClipNotReady_DropsStaleJob_ReturnsNull()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("dan");
        var clipId = await SeedClipAsync(userId, ClipStatuses.Failed);
        var now = DateTimeOffset.UtcNow;
        await using (var seed = NewContext())
        {
            await NewStore(now, seed).EnqueueAsync(clipId, CancellationToken.None);
        }

        await using var db = NewContext();
        var store = NewStore(now, db);

        var claimed = await store.ClaimNextAsync(TimeSpan.FromMinutes(5), 3, CancellationToken.None);

        claimed.Should().BeNull();
        await using var verify = NewContext();
        (await verify.ClipStreamJobs.AsNoTracking().AnyAsync(j => j.ClipId == clipId)).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_DeletesRow()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("erin");
        var clipId = await SeedClipAsync(userId, ClipStatuses.Ready);
        var now = DateTimeOffset.UtcNow;
        await using (var seed = NewContext())
        {
            await NewStore(now, seed).EnqueueAsync(clipId, CancellationToken.None);
        }

        await using var db = NewContext();
        await NewStore(now, db).CompleteAsync(clipId, CancellationToken.None);

        await using var verify = NewContext();
        (await verify.ClipStreamJobs.AsNoTracking().AnyAsync(j => j.ClipId == clipId)).Should().BeFalse();
    }

    [Fact]
    public async Task MarkFailedAsync_SetsFailed_GuardedByAttempt()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("finn");
        var clipId = await SeedClipAsync(userId, ClipStatuses.Ready);
        var now = DateTimeOffset.UtcNow;
        await using (var seed = NewContext())
        {
            var store0 = NewStore(now, seed);
            await store0.EnqueueAsync(clipId, CancellationToken.None);
        }
        // Claim to bump attempts to 1.
        await using (var c = NewContext())
        {
            await NewStore(now, c).ClaimNextAsync(TimeSpan.FromMinutes(5), 3, CancellationToken.None);
        }

        await using var db = NewContext();
        var store = NewStore(now, db);

        // Stale attempt number → no-op.
        await store.MarkFailedAsync(clipId, expectedAttempt: 99, CancellationToken.None);
        (await store.GetStatusAsync(clipId, CancellationToken.None)).Should().Be(ClipStreamJobStatuses.Pending);

        // Correct attempt → failed.
        await store.MarkFailedAsync(clipId, expectedAttempt: 1, CancellationToken.None);
        await using var verify = NewContext();
        var row = await verify.ClipStreamJobs.AsNoTracking().SingleAsync(j => j.ClipId == clipId);
        row.Status.Should().Be(ClipStreamJobStatuses.Failed);
        row.ProcessingStartedAt.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseLeaseAsync_ClearsLease_KeepsPending()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("gwen");
        var clipId = await SeedClipAsync(userId, ClipStatuses.Ready);
        var now = DateTimeOffset.UtcNow;
        await using (var seed = NewContext())
        {
            await NewStore(now, seed).EnqueueAsync(clipId, CancellationToken.None);
        }
        await using (var c = NewContext())
        {
            await NewStore(now, c).ClaimNextAsync(TimeSpan.FromMinutes(5), 3, CancellationToken.None);
        }

        await using var db = NewContext();
        await NewStore(now, db).ReleaseLeaseAsync(clipId, expectedAttempt: 1, CancellationToken.None);

        await using var verify = NewContext();
        var row = await verify.ClipStreamJobs.AsNoTracking().SingleAsync(j => j.ClipId == clipId);
        row.Status.Should().Be(ClipStreamJobStatuses.Pending);
        row.ProcessingStartedAt.Should().BeNull();
        row.ProcessingAttempts.Should().Be(1);
    }
}
