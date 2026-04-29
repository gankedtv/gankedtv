using FluentAssertions;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Tests.Integration.Media;

[Collection("Postgres")]
public class ClipMediaJobStoreIntegrationTests
{
    private readonly PostgresFixture _fx;

    public ClipMediaJobStoreIntegrationTests(PostgresFixture fx) => _fx = fx;

    private GankedTvDbContext NewContext() => _fx.CreateContext();

    private ClipMediaJobStore NewStore(DateTimeOffset now, GankedTvDbContext db) =>
        new(db, new FakeClock(now));

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

    private async Task<Guid> SeedClipAsync(
        Guid userId,
        string status,
        DateTimeOffset updatedAt,
        DateTimeOffset? processingStartedAt = null,
        int processingAttempts = 0,
        string? thumbnailKey = null,
        int? gameId = null)
    {
        await using var db = NewContext();
        var clip = new Clip
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = "t",
            VideoKey = $"{userId}/v.mp4",
            Status = status,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
            ProcessingStartedAt = processingStartedAt,
            ProcessingAttempts = processingAttempts,
            ThumbnailKey = thumbnailKey,
            GameId = gameId,
        };
        db.Clips.Add(clip);
        await db.SaveChangesAsync();
        return clip.Id;
    }

    [Fact]
    public async Task ClaimNextAsync_NoCandidates_ReturnsNull()
    {
        await _fx.ResetAsync();
        await using var db = NewContext();
        var store = NewStore(DateTimeOffset.UtcNow, db);

        var result = await store.ClaimNextAsync(TimeSpan.FromMinutes(5), maxAttempts: 3, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ClaimNextAsync_OnlyDraftClipsExist_ReturnsNull()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("alice");
        await SeedClipAsync(userId, ClipStatuses.Draft, DateTimeOffset.UtcNow);

        await using var db = NewContext();
        var store = NewStore(DateTimeOffset.UtcNow, db);

        var result = await store.ClaimNextAsync(TimeSpan.FromMinutes(5), 3, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ClaimNextAsync_PicksOldestProcessingClip_AndIncrementsAttempt()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("bob");
        var now = DateTimeOffset.UtcNow;

        var newer = await SeedClipAsync(userId, ClipStatuses.Processing, now.AddSeconds(-1));
        var older = await SeedClipAsync(userId, ClipStatuses.Processing, now.AddMinutes(-5));

        await using var db = NewContext();
        var store = NewStore(now, db);

        var result = await store.ClaimNextAsync(TimeSpan.FromMinutes(5), 3, CancellationToken.None);

        result.Should().NotBeNull();
        result!.ClipId.Should().Be(older);
        result.AttemptNumber.Should().Be(1);

        // Older row's lease was bumped; younger row remains untouched.
        await using var verify = NewContext();
        var olderRow = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == older);
        olderRow.ProcessingStartedAt.Should().NotBeNull();
        olderRow.ProcessingAttempts.Should().Be(1);
        var newerRow = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == newer);
        newerRow.ProcessingStartedAt.Should().BeNull();
        newerRow.ProcessingAttempts.Should().Be(0);
    }

    [Fact]
    public async Task ClaimNextAsync_SkipsClipWithThumbnailAlreadySet()
    {
        // Defensive: a clip in 'processing' that already has a thumbnail_key shouldn't
        // be re-processed. (Should not occur normally — MarkReady flips status to ready —
        // but the predicate is cheap and protects against stuck states.)
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("carol");
        var now = DateTimeOffset.UtcNow;

        await SeedClipAsync(userId, ClipStatuses.Processing, now, thumbnailKey: "already.jpg");

        await using var db = NewContext();
        var store = NewStore(now, db);

        var result = await store.ClaimNextAsync(TimeSpan.FromMinutes(5), 3, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ClaimNextAsync_SkipsClipPastMaxAttempts()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("dan");
        var now = DateTimeOffset.UtcNow;

        await SeedClipAsync(userId, ClipStatuses.Processing, now, processingAttempts: 3);

        await using var db = NewContext();
        var store = NewStore(now, db);

        var result = await store.ClaimNextAsync(TimeSpan.FromMinutes(5), maxAttempts: 3, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ClaimNextAsync_SkipsClipWithFreshLease()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("eve");
        var now = DateTimeOffset.UtcNow;

        // Lease taken 30s ago, with a 5-minute lease window — still considered held.
        await SeedClipAsync(userId, ClipStatuses.Processing, now,
            processingStartedAt: now.AddSeconds(-30),
            processingAttempts: 1);

        await using var db = NewContext();
        var store = NewStore(now, db);

        var result = await store.ClaimNextAsync(TimeSpan.FromMinutes(5), 3, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ClaimNextAsync_PicksClipWithExpiredLease_AndBumpsAttempt()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("frank");
        var now = DateTimeOffset.UtcNow;

        var clipId = await SeedClipAsync(userId, ClipStatuses.Processing, now,
            processingStartedAt: now.AddMinutes(-10),
            processingAttempts: 1);

        await using var db = NewContext();
        var store = NewStore(now, db);

        var result = await store.ClaimNextAsync(TimeSpan.FromMinutes(5), 3, CancellationToken.None);

        result.Should().NotBeNull();
        result!.ClipId.Should().Be(clipId);
        result.AttemptNumber.Should().Be(2);
    }

    [Fact]
    public async Task ClaimNextAsync_TwoConcurrentClaims_EachGetsDifferentRow()
    {
        // Validates the FOR UPDATE SKIP LOCKED behavior end-to-end. Spin two stores in
        // parallel; with two queued rows and two concurrent claims, neither should be
        // null and they must point at different clip ids.
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("greg");
        var now = DateTimeOffset.UtcNow;
        await SeedClipAsync(userId, ClipStatuses.Processing, now.AddMinutes(-2));
        await SeedClipAsync(userId, ClipStatuses.Processing, now.AddMinutes(-1));

        // Each claimer needs its own DbContext instance — sharing one would serialize the
        // two FOR UPDATE statements through a single connection and defeat the test.
        await using var db1 = NewContext();
        await using var db2 = NewContext();
        var store1 = NewStore(now, db1);
        var store2 = NewStore(now, db2);

        var task1 = store1.ClaimNextAsync(TimeSpan.FromMinutes(5), 3, CancellationToken.None);
        var task2 = store2.ClaimNextAsync(TimeSpan.FromMinutes(5), 3, CancellationToken.None);
        var results = await Task.WhenAll(task1, task2);

        results[0].Should().NotBeNull();
        results[1].Should().NotBeNull();
        results[0]!.ClipId.Should().NotBe(results[1]!.ClipId);
    }

    [Fact]
    public async Task GetGameSlugAsync_ReturnsSlugForKnownId_NullForUnknown_NullForNullId()
    {
        await _fx.ResetAsync();
        await using var db = NewContext();
        var store = NewStore(DateTimeOffset.UtcNow, db);

        // The seeded games table includes 'valorant' at id=2.
        var slug = await store.GetGameSlugAsync(2, CancellationToken.None);
        slug.Should().Be("valorant");

        (await store.GetGameSlugAsync(999_999, CancellationToken.None)).Should().BeNull();
        (await store.GetGameSlugAsync(null, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task MarkReadyAsync_FlipsStatusAndPersistsMetadata()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("hank");
        var now = DateTimeOffset.UtcNow;
        var clipId = await SeedClipAsync(userId, ClipStatuses.Processing, now,
            processingStartedAt: now.AddSeconds(-2),
            processingAttempts: 1);

        await using var db = NewContext();
        var store = NewStore(now, db);

        await store.MarkReadyAsync(clipId,
            new FinalizedMediaJob("k.jpg", DurationSecs: 12, Width: 1920, Height: 1080),
            CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Ready);
        clip.ThumbnailKey.Should().Be("k.jpg");
        clip.DurationSecs.Should().Be(12);
        clip.Width.Should().Be(1920);
        clip.Height.Should().Be(1080);
        clip.ProcessingStartedAt.Should().BeNull();
    }

    [Fact]
    public async Task MarkReadyAsync_NoOpsWhenStatusAlreadyFailed()
    {
        // The status guard means a row that was already marked failed by a parallel
        // worker doesn't get resurrected by a late MarkReady.
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("ivy");
        var now = DateTimeOffset.UtcNow;
        var clipId = await SeedClipAsync(userId, ClipStatuses.Failed, now);

        await using var db = NewContext();
        var store = NewStore(now, db);

        await store.MarkReadyAsync(clipId,
            new FinalizedMediaJob("k.jpg", 1, 1, 1),
            CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Failed);
        clip.ThumbnailKey.Should().BeNull();
    }

    [Fact]
    public async Task MarkFailedAsync_FlipsStatusAndClearsLease()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("jane");
        var now = DateTimeOffset.UtcNow;
        var clipId = await SeedClipAsync(userId, ClipStatuses.Processing, now,
            processingStartedAt: now,
            processingAttempts: 3);

        await using var db = NewContext();
        var store = NewStore(now, db);

        await store.MarkFailedAsync(clipId, CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Failed);
        clip.ProcessingStartedAt.Should().BeNull();
        // ProcessingAttempts is preserved so audit/forensics can see how many tries it took.
        clip.ProcessingAttempts.Should().Be(3);
    }

    [Fact]
    public async Task ReleaseLeaseAsync_ClearsProcessingStartedAtButPreservesStatus()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("kate");
        var now = DateTimeOffset.UtcNow;
        var clipId = await SeedClipAsync(userId, ClipStatuses.Processing, now,
            processingStartedAt: now,
            processingAttempts: 1);

        await using var db = NewContext();
        var store = NewStore(now, db);

        await store.ReleaseLeaseAsync(clipId, CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Processing);
        clip.ProcessingStartedAt.Should().BeNull();
        clip.ProcessingAttempts.Should().Be(1);
    }
}
