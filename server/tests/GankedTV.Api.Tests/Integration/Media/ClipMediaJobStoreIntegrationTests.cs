using FluentAssertions;
using GankedTV.Api.Clips;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Clips;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Tests.Integration.Media;

[Collection("PostgresServices")]
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
        int? gameId = null,
        short? height = null,
        string? failureReason = null,
        string? importSourceUrl = null,
        long? fileSizeBytes = null,
        double? trimStartSecs = null,
        double? trimEndSecs = null,
        DateTimeOffset? editedAt = null,
        int editCount = 0)
    {
        await using var db = NewContext();
        var clip = new Clip
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = "t",
            VideoKey = $"{userId}/v.mp4",
            ShareCode = ShareCodeGenerator.Next(),
            Status = status,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
            ProcessingStartedAt = processingStartedAt,
            ProcessingAttempts = processingAttempts,
            ThumbnailKey = thumbnailKey,
            GameId = gameId,
            Height = height,
            FailureReason = failureReason,
            ImportSourceUrl = importSourceUrl,
            FileSizeBytes = fileSizeBytes,
            TrimStartSecs = trimStartSecs,
            TrimEndSecs = trimEndSecs,
            EditedAt = editedAt,
            EditCount = editCount,
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

        var result = await store.ClaimNextAsync(ClipStatuses.Processing, TimeSpan.FromMinutes(5), maxAttempts: 3, CancellationToken.None);

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

        var result = await store.ClaimNextAsync(ClipStatuses.Processing, TimeSpan.FromMinutes(5), 3, CancellationToken.None);

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

        var result = await store.ClaimNextAsync(ClipStatuses.Processing, TimeSpan.FromMinutes(5), 3, CancellationToken.None);

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
    public async Task ClaimNextAsync_TranscodeStage_OnlyPicksTranscodingClips()
    {
        // The transcode stage claims 'transcoding', not 'processing'. A 'processing' clip
        // (still awaiting its thumbnail) must be invisible to it.
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("carol");
        var now = DateTimeOffset.UtcNow;

        await SeedClipAsync(userId, ClipStatuses.Processing, now.AddMinutes(-1));
        var transcoding = await SeedClipAsync(userId, ClipStatuses.Transcoding, now.AddMinutes(-2));

        await using var db = NewContext();
        var store = NewStore(now, db);

        var result = await store.ClaimNextAsync(ClipStatuses.Transcoding, TimeSpan.FromMinutes(5), 3, CancellationToken.None);

        result.Should().NotBeNull();
        result!.ClipId.Should().Be(transcoding);
    }

    [Fact]
    public async Task ClaimNextAsync_CarriesSourceHeight()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("hugh");
        var now = DateTimeOffset.UtcNow;
        await SeedClipAsync(userId, ClipStatuses.Transcoding, now, height: 720);

        await using var db = NewContext();
        var store = NewStore(now, db);

        var result = await store.ClaimNextAsync(ClipStatuses.Transcoding, TimeSpan.FromMinutes(5), 3, CancellationToken.None);

        result.Should().NotBeNull();
        result!.SourceHeight.Should().Be(720);
    }

    [Fact]
    public async Task ClaimNextAsync_CarriesTrimRange()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("trina");
        var now = DateTimeOffset.UtcNow;
        await SeedClipAsync(userId, ClipStatuses.Transcoding, now, trimStartSecs: 2.5, trimEndSecs: 11.0);

        await using var db = NewContext();
        var store = NewStore(now, db);

        var result = await store.ClaimNextAsync(ClipStatuses.Transcoding, TimeSpan.FromMinutes(5), 3, CancellationToken.None);

        result.Should().NotBeNull();
        result!.TrimStartSecs.Should().Be(2.5);
        result.TrimEndSecs.Should().Be(11.0);
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

        var result = await store.ClaimNextAsync(ClipStatuses.Processing, TimeSpan.FromMinutes(5), maxAttempts: 3, CancellationToken.None);

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

        var result = await store.ClaimNextAsync(ClipStatuses.Processing, TimeSpan.FromMinutes(5), 3, CancellationToken.None);

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

        var result = await store.ClaimNextAsync(ClipStatuses.Processing, TimeSpan.FromMinutes(5), 3, CancellationToken.None);

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

        var task1 = store1.ClaimNextAsync(ClipStatuses.Processing, TimeSpan.FromMinutes(5), 3, CancellationToken.None);
        var task2 = store2.ClaimNextAsync(ClipStatuses.Processing, TimeSpan.FromMinutes(5), 3, CancellationToken.None);
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

        // Resolve the seeded id at runtime — don't pin to a hardcoded value, since
        // seeding order can shift as new games are added.
        var valorantId = await db.Games
            .Where(g => g.Slug == "valorant")
            .Select(g => g.Id)
            .SingleAsync();

        var store = NewStore(DateTimeOffset.UtcNow, db);

        var slug = await store.GetGameSlugAsync(valorantId, CancellationToken.None);
        slug.Should().Be("valorant");

        (await store.GetGameSlugAsync(999_999, CancellationToken.None)).Should().BeNull();
        (await store.GetGameSlugAsync(null, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AdvanceThumbnailAsync_AdvancesToTranscodingAndPersistsMetadata()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("hank");
        var now = DateTimeOffset.UtcNow;
        var clipId = await SeedClipAsync(userId, ClipStatuses.Processing, now,
            processingStartedAt: now.AddSeconds(-2),
            processingAttempts: 1);

        await using var db = NewContext();
        var store = NewStore(now, db);

        await store.AdvanceThumbnailAsync(clipId,
            expectedAttempt: 1,
            new FinalizedMediaJob("k.jpg", DurationSecs: 12, Width: 1920, Height: 1080),
            ClipStatuses.Transcoding,
            CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Transcoding);
        clip.ThumbnailKey.Should().Be("k.jpg");
        clip.DurationSecs.Should().Be(12);
        clip.Width.Should().Be(1920);
        clip.Height.Should().Be(1080);
        clip.ProcessingStartedAt.Should().BeNull();
        // Attempts reset so the compress stage starts with a fresh MaxAttempts budget.
        clip.ProcessingAttempts.Should().Be(0);
    }

    [Fact]
    public async Task AdvanceThumbnailAsync_WritesSanitizedTrim()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("harriet");
        var now = DateTimeOffset.UtcNow;
        // Requested end (99) exceeds the source; the thumbnail stage clamps and writes back.
        var clipId = await SeedClipAsync(userId, ClipStatuses.Processing, now,
            processingAttempts: 1, trimStartSecs: 4.0, trimEndSecs: 99.0);

        await using var db = NewContext();
        var store = NewStore(now, db);

        await store.AdvanceThumbnailAsync(clipId,
            expectedAttempt: 1,
            new FinalizedMediaJob("k.jpg", 6, 1920, 1080, TrimStartSecs: 4.0, TrimEndSecs: 10.0),
            ClipStatuses.Transcoding,
            CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.TrimStartSecs.Should().Be(4.0);
        clip.TrimEndSecs.Should().Be(10.0);
    }

    [Fact]
    public async Task AdvanceThumbnailAsync_DegenerateTrim_ClearsColumns()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("hollis");
        var now = DateTimeOffset.UtcNow;
        var clipId = await SeedClipAsync(userId, ClipStatuses.Processing, now,
            processingAttempts: 1, trimStartSecs: 0.0, trimEndSecs: 0.1);

        await using var db = NewContext();
        var store = NewStore(now, db);

        await store.AdvanceThumbnailAsync(clipId,
            expectedAttempt: 1,
            new FinalizedMediaJob("k.jpg", 5, 1280, 720),
            ClipStatuses.Transcoding,
            CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.TrimStartSecs.Should().BeNull();
        clip.TrimEndSecs.Should().BeNull();
    }

    [Fact]
    public async Task AdvanceThumbnailAsync_ToReady_WhenTranscodeDisabled()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("hilda");
        var now = DateTimeOffset.UtcNow;
        var clipId = await SeedClipAsync(userId, ClipStatuses.Processing, now, processingAttempts: 1);

        await using var db = NewContext();
        var store = NewStore(now, db);

        await store.AdvanceThumbnailAsync(clipId,
            expectedAttempt: 1,
            new FinalizedMediaJob("k.jpg", 5, 1280, 720),
            ClipStatuses.Ready,
            CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Ready);
        clip.ThumbnailKey.Should().Be("k.jpg");
    }

    [Fact]
    public async Task AdvanceThumbnailAsync_NoOpsWhenStatusAlreadyFailed()
    {
        // The status guard means a row that was already marked failed by a parallel
        // worker doesn't get resurrected by a late advance.
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("ivy");
        var now = DateTimeOffset.UtcNow;
        var clipId = await SeedClipAsync(userId, ClipStatuses.Failed, now);

        await using var db = NewContext();
        var store = NewStore(now, db);

        await store.AdvanceThumbnailAsync(clipId,
            expectedAttempt: 0,
            new FinalizedMediaJob("k.jpg", 1, 1, 1),
            ClipStatuses.Transcoding,
            CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Failed);
        clip.ThumbnailKey.Should().BeNull();
    }

    [Fact]
    public async Task AdvanceThumbnailAsync_NoOpsWhenAttemptMismatch()
    {
        // Race regression: this worker's lease elapsed mid-extraction; another worker
        // re-claimed and bumped processing_attempts. The original worker's late advance
        // arrives with the stale attempt number and must NOT clobber the new claim.
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("liam");
        var now = DateTimeOffset.UtcNow;
        var clipId = await SeedClipAsync(userId, ClipStatuses.Processing, now,
            processingStartedAt: now,
            processingAttempts: 2);

        await using var db = NewContext();
        var store = NewStore(now, db);

        // Original worker thinks it owns attempt 1 — but the store is on attempt 2.
        await store.AdvanceThumbnailAsync(clipId,
            expectedAttempt: 1,
            new FinalizedMediaJob("stale.jpg", 99, 99, 99),
            ClipStatuses.Transcoding,
            CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Processing);
        clip.ThumbnailKey.Should().BeNull();
        clip.ProcessingAttempts.Should().Be(2);
        clip.ProcessingStartedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CompleteCompressionAsync_FlipsToReadyAndRepointsVideoKey()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("trent");
        var now = DateTimeOffset.UtcNow;
        var clipId = await SeedClipAsync(userId, ClipStatuses.Transcoding, now,
            processingStartedAt: now.AddSeconds(-2),
            processingAttempts: 1);

        await using var db = NewContext();
        var store = NewStore(now, db);

        await store.CompleteCompressionAsync(clipId, expectedAttempt: 1, "user/clip.cmp.mp4", "av1", CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Ready);
        clip.VideoKey.Should().Be("user/clip.cmp.mp4");
        clip.VideoCodec.Should().Be("av1");
        clip.ProcessingStartedAt.Should().BeNull();
    }

    [Fact]
    public async Task CompleteCompressionAsync_NoOpsWhenNotTranscoding()
    {
        // Status guard: a clip that was already failed (or never reached transcoding) must
        // not be flipped to ready by a late compression completion.
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("tara");
        var now = DateTimeOffset.UtcNow;
        var clipId = await SeedClipAsync(userId, ClipStatuses.Failed, now);

        await using var db = NewContext();
        var store = NewStore(now, db);

        await store.CompleteCompressionAsync(clipId, expectedAttempt: 0, "user/clip.cmp.mp4", "av1", CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Failed);
        clip.VideoCodec.Should().BeNull();
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

        await store.MarkFailedAsync(clipId, expectedAttempt: 3, ClipStatuses.Processing, CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Failed);
        clip.ProcessingStartedAt.Should().BeNull();
        // ProcessingAttempts is preserved so audit/forensics can see how many tries it took.
        clip.ProcessingAttempts.Should().Be(3);
    }

    [Fact]
    public async Task MarkFailedAsync_FailedReCut_RestoresClipToReady()
    {
        // A re-cut that exhausts its retries must not take a published clip dark: its previous
        // master is untouched, so the clip goes back to 'ready' with the pending cut dropped.
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("recut");
        var now = DateTimeOffset.UtcNow;
        var clipId = await SeedClipAsync(userId, ClipStatuses.Transcoding, now,
            processingStartedAt: now,
            processingAttempts: 3,
            thumbnailKey: "thumbs/x.jpg",
            trimStartSecs: 2,
            trimEndSecs: 8,
            editedAt: now,
            editCount: 1);

        await using var db = NewContext();
        var store = NewStore(now, db);

        await store.MarkFailedAsync(clipId, expectedAttempt: 3, ClipStatuses.Transcoding,
            CancellationToken.None, reason: ClipFailureReasons.TranscodeFailed);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Ready);
        clip.FailureReason.Should().BeNull();
        clip.ProcessingStartedAt.Should().BeNull();
        // Range cleared so an admin requeue can't re-apply the cut that just failed.
        clip.TrimStartSecs.Should().BeNull();
        clip.TrimEndSecs.Should().BeNull();
        // First re-cut, so the footage is back to never-edited and the badge must go.
        clip.EditedAt.Should().BeNull();
        // Generation is monotonic — it must not be reused by the next re-cut.
        clip.EditCount.Should().Be(1);
    }

    [Fact]
    public async Task MarkFailedAsync_FailedReCut_KeepsEarlierEditStamp()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("recut2");
        var now = DateTimeOffset.UtcNow;
        var earlierEdit = now.AddDays(-3);
        var clipId = await SeedClipAsync(userId, ClipStatuses.Transcoding, now,
            processingStartedAt: now,
            processingAttempts: 3,
            thumbnailKey: "thumbs/x.jpg",
            editedAt: earlierEdit,
            editCount: 2);

        await using var db = NewContext();
        var store = NewStore(now, db);

        await store.MarkFailedAsync(clipId, expectedAttempt: 3, ClipStatuses.Transcoding, CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Ready);
        // An earlier successful re-cut really did change the footage — that badge stays.
        clip.EditedAt.Should().BeCloseTo(earlierEdit, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task MarkFailedAsync_FirstPublishFailure_StillFails()
    {
        // The rollback keys off EditedAt, which is only ever stamped on a live clip. A clip that
        // never reached 'ready' has no previous master to fall back to and must stay failed.
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("firstpub");
        var now = DateTimeOffset.UtcNow;
        var clipId = await SeedClipAsync(userId, ClipStatuses.Transcoding, now,
            processingStartedAt: now,
            processingAttempts: 3,
            thumbnailKey: "thumbs/x.jpg");

        await using var db = NewContext();
        var store = NewStore(now, db);

        await store.MarkFailedAsync(clipId, expectedAttempt: 3, ClipStatuses.Transcoding,
            CancellationToken.None, reason: ClipFailureReasons.TranscodeFailed);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Failed);
        clip.FailureReason.Should().Be(ClipFailureReasons.TranscodeFailed);
    }

    [Fact]
    public async Task MarkFailedAsync_NoOpsWhenAttemptMismatch()
    {
        // Same race shape as MarkReady: a stale final-attempt failure must not kill a
        // row that another worker has since re-claimed for a fresh attempt.
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("mira");
        var now = DateTimeOffset.UtcNow;
        var clipId = await SeedClipAsync(userId, ClipStatuses.Processing, now,
            processingStartedAt: now,
            processingAttempts: 4);

        await using var db = NewContext();
        var store = NewStore(now, db);

        await store.MarkFailedAsync(clipId, expectedAttempt: 3, ClipStatuses.Processing, CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Processing);
        clip.ProcessingStartedAt.Should().NotBeNull();
        clip.ProcessingAttempts.Should().Be(4);
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

        await store.ReleaseLeaseAsync(clipId, expectedAttempt: 1, ClipStatuses.Processing, CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Processing);
        clip.ProcessingStartedAt.Should().BeNull();
        clip.ProcessingAttempts.Should().Be(1);
    }

    [Fact]
    public async Task ReleaseLeaseAsync_NoOpsWhenAttemptMismatch()
    {
        // The race the review flagged: worker A's transient release must not free a
        // lease that worker B has since acquired (with a higher attempt count) and is
        // currently using.
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("noah");
        var now = DateTimeOffset.UtcNow;
        var bLeaseStart = now.AddSeconds(-5);
        var clipId = await SeedClipAsync(userId, ClipStatuses.Processing, now,
            processingStartedAt: bLeaseStart,
            processingAttempts: 3);

        await using var db = NewContext();
        var store = NewStore(now, db);

        // Worker A wakes up holding stale attempt=2; release must be a no-op.
        await store.ReleaseLeaseAsync(clipId, expectedAttempt: 2, ClipStatuses.Processing, CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        // Postgres `timestamp with time zone` stores microseconds; .NET DateTimeOffset
        // is 100ns ticks. Use a microsecond-tolerance compare so the assertion isn't
        // sensitive to the truncation that happens on roundtrip.
        clip.ProcessingStartedAt.Should().NotBeNull();
        clip.ProcessingStartedAt!.Value.Should().BeCloseTo(bLeaseStart, TimeSpan.FromMicroseconds(1));
        clip.ProcessingAttempts.Should().Be(3);
    }

    // --- Import stage (issue #106) ----------------------------------------------------

    private async Task<Guid> SeedImportingClipAsync(
        Guid userId,
        DateTimeOffset updatedAt,
        string url = "https://medal.tv/clips/x",
        string title = "imported title")
    {
        await using var db = NewContext();
        var clip = new Clip
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = title,
            VideoKey = $"{userId}/v.mp4",
            ShareCode = ShareCodeGenerator.Next(),
            Status = ClipStatuses.Importing,
            ImportSourceUrl = url,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
        };
        db.Clips.Add(clip);
        await db.SaveChangesAsync();
        return clip.Id;
    }

    [Fact]
    public async Task ClaimNextImportAsync_PicksImportingClipAndReturnsUrl()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("oscar");
        var now = DateTimeOffset.UtcNow;
        var clipId = await SeedImportingClipAsync(userId, now, "https://medal.tv/clips/abc");

        await using var db = NewContext();
        var store = NewStore(now, db);

        var result = await store.ClaimNextImportAsync(TimeSpan.FromMinutes(5), 3, CancellationToken.None);

        result.Should().NotBeNull();
        result!.ClipId.Should().Be(clipId);
        result.ImportSourceUrl.Should().Be("https://medal.tv/clips/abc");
        result.AttemptNumber.Should().Be(1);
    }

    [Fact]
    public async Task ClaimNextImportAsync_IgnoresOtherStatuses()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("paula");
        var now = DateTimeOffset.UtcNow;
        // A 'processing' clip must be invisible to the import claim — the partial index is
        // scoped to status='importing'.
        await SeedClipAsync(userId, ClipStatuses.Processing, now);

        await using var db = NewContext();
        var store = NewStore(now, db);
        var result = await store.ClaimNextImportAsync(TimeSpan.FromMinutes(5), 3, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task AdvanceImportAsync_AdvancesToProcessing_AndOverwritesPlaceholderTitle()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("queen");
        var now = DateTimeOffset.UtcNow;
        var clipId = await SeedImportingClipAsync(userId, now, title: ClipImportDefaults.PlaceholderTitle);

        await using var db = NewContext();
        var store = NewStore(now, db);
        // Pretend the worker had bumped attempts via a claim — set the row's attempt to 1
        // so the expectedAttempt guard inside AdvanceImportAsync passes.
        await db.Clips.Where(c => c.Id == clipId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ProcessingAttempts, 1));

        await store.AdvanceImportAsync(
            clipId,
            expectedAttempt: 1,
            fileSizeBytes: 1024,
            extractorTitle: "Extractor Title",
            placeholderTitle: ClipImportDefaults.PlaceholderTitle,
            CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Processing);
        clip.FileSizeBytes.Should().Be(1024);
        clip.Title.Should().Be("Extractor Title");
        clip.ProcessingAttempts.Should().Be(0);
        clip.ProcessingStartedAt.Should().BeNull();
    }

    [Fact]
    public async Task AdvanceImportAsync_KeepsUserSuppliedTitle()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("ralph");
        var now = DateTimeOffset.UtcNow;
        var clipId = await SeedImportingClipAsync(userId, now, title: "My override");

        await using var db = NewContext();
        var store = NewStore(now, db);
        await db.Clips.Where(c => c.Id == clipId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ProcessingAttempts, 1));

        await store.AdvanceImportAsync(
            clipId,
            expectedAttempt: 1,
            fileSizeBytes: 2048,
            extractorTitle: "Extractor Title",
            placeholderTitle: ClipImportDefaults.PlaceholderTitle,
            CancellationToken.None);

        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Processing);
        // User-supplied title is preserved because it didn't match the placeholder.
        clip.Title.Should().Be("My override");
    }

    [Fact]
    public async Task RequeueFailedMediaAsync_FailedWithoutThumbnail_RestartsAtProcessing()
    {
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        var userId = await SeedUserAsync("rq_thumb");
        var clipId = await SeedClipAsync(userId, ClipStatuses.Failed, now,
            processingAttempts: 3, thumbnailKey: null, failureReason: ClipFailureReasons.ThumbnailFailed);

        await using var db = NewContext();
        var count = await NewStore(now, db).RequeueFailedMediaAsync(null, onlyRetryable: true, CancellationToken.None);

        count.Should().Be(1);
        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Processing);
        clip.ProcessingAttempts.Should().Be(0);
        clip.ProcessingStartedAt.Should().BeNull();
        clip.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task RequeueFailedMediaAsync_FailedImportWithoutSource_RestartsAtImporting()
    {
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        var userId = await SeedUserAsync("rq_import");
        // An import that failed before yt-dlp fetched the source: it has an import URL but no
        // downloaded bytes (FileSizeBytes null), so restarting it at 'processing' would just fail
        // again for want of a source. It must go back to 'importing'.
        var clipId = await SeedClipAsync(userId, ClipStatuses.Failed, now,
            processingAttempts: 3, thumbnailKey: null, failureReason: ClipFailureReasons.SourceUnavailable,
            importSourceUrl: "https://medal.tv/clips/abc", fileSizeBytes: null);

        await using var db = NewContext();
        var count = await NewStore(now, db).RequeueFailedMediaAsync(null, onlyRetryable: true, CancellationToken.None);

        count.Should().Be(1);
        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Importing);
        clip.ProcessingAttempts.Should().Be(0);
        clip.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task RequeueFailedMediaAsync_FailedImportWithSource_RestartsAtProcessing()
    {
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        var userId = await SeedUserAsync("rq_import_fetched");
        // An import that already fetched its source (FileSizeBytes set) then failed at the thumbnail
        // stage restarts at 'processing' like any other source-in-hand clip, not 'importing'.
        var clipId = await SeedClipAsync(userId, ClipStatuses.Failed, now,
            processingAttempts: 3, thumbnailKey: null, failureReason: ClipFailureReasons.ThumbnailFailed,
            importSourceUrl: "https://medal.tv/clips/def", fileSizeBytes: 2048);

        await using var db = NewContext();
        var count = await NewStore(now, db).RequeueFailedMediaAsync(null, onlyRetryable: true, CancellationToken.None);

        count.Should().Be(1);
        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Processing);
    }

    [Fact]
    public async Task RequeueFailedMediaAsync_FailedWithThumbnail_RestartsAtTranscoding()
    {
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        var userId = await SeedUserAsync("rq_transcode");
        var clipId = await SeedClipAsync(userId, ClipStatuses.Failed, now,
            processingAttempts: 3, thumbnailKey: "u/c.jpg", failureReason: ClipFailureReasons.TranscodeFailed);

        await using var db = NewContext();
        var count = await NewStore(now, db).RequeueFailedMediaAsync(null, onlyRetryable: true, CancellationToken.None);

        count.Should().Be(1);
        await using var verify = NewContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Transcoding);
        clip.ProcessingAttempts.Should().Be(0);
    }

    [Fact]
    public async Task RequeueFailedMediaAsync_OnlyRetryable_SkipsContentRejections()
    {
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        var userId = await SeedUserAsync("rq_content");
        var infra = await SeedClipAsync(userId, ClipStatuses.Failed, now, failureReason: ClipFailureReasons.SourceUnavailable);
        var tooLong = await SeedClipAsync(userId, ClipStatuses.Failed, now, failureReason: ClipFailureReasons.SourceTooLong);

        await using var db = NewContext();
        var count = await NewStore(now, db).RequeueFailedMediaAsync(null, onlyRetryable: true, CancellationToken.None);

        count.Should().Be(1);
        await using var verify = NewContext();
        (await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == infra)).Status.Should().Be(ClipStatuses.Processing);
        // The content rejection is left failed — a retry can't make an over-long clip acceptable.
        (await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == tooLong)).Status.Should().Be(ClipStatuses.Failed);
    }

    [Fact]
    public async Task RequeueFailedMediaAsync_IncludeContentFailures_RequeuesTooLong()
    {
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        var userId = await SeedUserAsync("rq_include");
        var tooLong = await SeedClipAsync(userId, ClipStatuses.Failed, now, failureReason: ClipFailureReasons.SourceTooLong);

        await using var db = NewContext();
        var count = await NewStore(now, db).RequeueFailedMediaAsync(null, onlyRetryable: false, CancellationToken.None);

        count.Should().Be(1);
        (await NewContext().Clips.AsNoTracking().SingleAsync(c => c.Id == tooLong)).Status.Should().Be(ClipStatuses.Processing);
    }

    [Fact]
    public async Task RequeueFailedMediaAsync_ClipIdFilter_TouchesOnlyThatClip()
    {
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        var userId = await SeedUserAsync("rq_single");
        var target = await SeedClipAsync(userId, ClipStatuses.Failed, now, failureReason: ClipFailureReasons.FetchFailed);
        var other = await SeedClipAsync(userId, ClipStatuses.Failed, now, failureReason: ClipFailureReasons.FetchFailed);

        await using var db = NewContext();
        var count = await NewStore(now, db).RequeueFailedMediaAsync(target, onlyRetryable: true, CancellationToken.None);

        count.Should().Be(1);
        await using var verify = NewContext();
        (await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == target)).Status.Should().Be(ClipStatuses.Processing);
        (await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == other)).Status.Should().Be(ClipStatuses.Failed);
    }

    [Fact]
    public async Task RequeueFailedMediaAsync_LeavesNonFailedClipsAlone()
    {
        await _fx.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        var userId = await SeedUserAsync("rq_ready");
        var ready = await SeedClipAsync(userId, ClipStatuses.Ready, now);

        await using var db = NewContext();
        var count = await NewStore(now, db).RequeueFailedMediaAsync(null, onlyRetryable: true, CancellationToken.None);

        count.Should().Be(0);
        (await NewContext().Clips.AsNoTracking().SingleAsync(c => c.Id == ready)).Status.Should().Be(ClipStatuses.Ready);
    }
}
