using FluentAssertions;
using GankedTV.Api.Clips;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Moderation;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Tests.Services;

[Collection("Postgres")]
public class ReportServiceTests
{
    private readonly PostgresFixture _fx;

    public ReportServiceTests(PostgresFixture fx) => _fx = fx;

    private async Task<(Guid reporter, Guid owner)> SeedTwoUsersAsync()
    {
        await using var db = _fx.CreateContext();
        var reporter = new User
        {
            Username = "reporter",
            Email = "r@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var owner = new User
        {
            Username = "owner",
            Email = "o@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.AddRange(reporter, owner);
        await db.SaveChangesAsync();
        return (reporter.Id, owner.Id);
    }

    private async Task<Guid> SeedClipAsync(Guid ownerId)
    {
        await using var db = _fx.CreateContext();
        var clip = new Clip
        {
            Id = Guid.NewGuid(),
            UserId = ownerId,
            Title = "target",
            VideoKey = $"clips/{ownerId}.mp4",
            ThumbnailKey = "thumbs/x.jpg",
            ShareCode = ShareCodeGenerator.Next(),
            Status = "ready",
            Visibility = "public",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Clips.Add(clip);
        await db.SaveChangesAsync();
        return clip.Id;
    }

    [Fact]
    public async Task CreateAsync_InvalidReason_ReturnsFailure()
    {
        await _fx.ResetAsync();
        var (reporter, owner) = await SeedTwoUsersAsync();
        var clipId = await SeedClipAsync(owner);

        await using var db = _fx.CreateContext();
        var svc = new ReportService(db, TimeProvider.System);

        var result = await svc.CreateAsync(reporter, ReportTargetTypes.Clip, clipId, "bogus", null, default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(ReportCreateError.InvalidReason);
    }

    [Fact]
    public async Task CreateAsync_OtherWithoutNote_ReturnsNoteRequired()
    {
        await _fx.ResetAsync();
        var (reporter, owner) = await SeedTwoUsersAsync();
        var clipId = await SeedClipAsync(owner);

        await using var db = _fx.CreateContext();
        var svc = new ReportService(db, TimeProvider.System);

        var result = await svc.CreateAsync(reporter, ReportTargetTypes.Clip, clipId,
            ReportReasons.Other, null, default);

        result.Error.Should().Be(ReportCreateError.NoteRequired);
    }

    [Fact]
    public async Task ResolveForTargetAsync_BulkClosesOpenReports()
    {
        await _fx.ResetAsync();
        var (reporter1, owner) = await SeedTwoUsersAsync();
        var clipId = await SeedClipAsync(owner);

        // Seed a second reporter so we can stack two open reports against the same target.
        Guid reporter2;
        await using (var db = _fx.CreateContext())
        {
            var r2 = new User
            {
                Username = "rep2",
                Email = "r2@example.com",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(r2);
            await db.SaveChangesAsync();
            reporter2 = r2.Id;
        }

        await using (var db = _fx.CreateContext())
        {
            var svc = new ReportService(db, TimeProvider.System);
            (await svc.CreateAsync(reporter1, ReportTargetTypes.Clip, clipId, "spam", null, default)).IsSuccess.Should().BeTrue();
            (await svc.CreateAsync(reporter2, ReportTargetTypes.Clip, clipId, "spam", null, default)).IsSuccess.Should().BeTrue();
        }

        await using (var db = _fx.CreateContext())
        {
            var svc = new ReportService(db, TimeProvider.System);
            var closed = await svc.ResolveForTargetAsync(ReportTargetTypes.Clip, clipId, owner, default);
            closed.Should().Be(2);
        }

        await using var verify = _fx.CreateContext();
        (await verify.Reports.CountAsync(r => r.Status == ReportStatuses.Resolved)).Should().Be(2);
    }

    [Fact]
    public async Task ResolveAsync_InvalidOutcome_ReturnsFailure()
    {
        await _fx.ResetAsync();
        var (reporter, owner) = await SeedTwoUsersAsync();
        var clipId = await SeedClipAsync(owner);

        Guid reportId;
        await using (var db = _fx.CreateContext())
        {
            var svc = new ReportService(db, TimeProvider.System);
            var created = await svc.CreateAsync(reporter, ReportTargetTypes.Clip, clipId, "spam", null, default);
            created.IsSuccess.Should().BeTrue();
            reportId = created.ReportId!.Value;
        }

        await using var verifyDb = _fx.CreateContext();
        var svc2 = new ReportService(verifyDb, TimeProvider.System);
        var result = await svc2.ResolveAsync(reportId, owner, "bogus", default);

        result.Error.Should().Be(ReportResolveError.InvalidOutcome);
    }

    [Fact]
    public async Task CreateAsync_InvalidTargetType_ReturnsFailure()
    {
        await _fx.ResetAsync();
        await using var db = _fx.CreateContext();
        var svc = new ReportService(db, TimeProvider.System);

        var result = await svc.CreateAsync(Guid.NewGuid(), "bogus", Guid.NewGuid(), "spam", null, default);

        result.Error.Should().Be(ReportCreateError.InvalidTargetType);
    }

    [Fact]
    public async Task CreateAsync_TargetUserNotFound_ReturnsFailure()
    {
        await _fx.ResetAsync();
        var (reporter, _) = await SeedTwoUsersAsync();
        await using var db = _fx.CreateContext();
        var svc = new ReportService(db, TimeProvider.System);

        var result = await svc.CreateAsync(reporter, ReportTargetTypes.User, Guid.NewGuid(),
            "harassment", null, default);

        result.Error.Should().Be(ReportCreateError.TargetNotFound);
    }

    [Fact]
    public async Task CreateAsync_CommentTargetMissing_ReturnsTargetNotFound()
    {
        await _fx.ResetAsync();
        var (reporter, _) = await SeedTwoUsersAsync();
        await using var db = _fx.CreateContext();
        var svc = new ReportService(db, TimeProvider.System);

        var result = await svc.CreateAsync(reporter, ReportTargetTypes.Comment, Guid.NewGuid(),
            "spam", null, default);

        result.Error.Should().Be(ReportCreateError.TargetNotFound);
    }

    [Fact]
    public async Task ResolveAsync_NotFound_ReturnsFailure()
    {
        await _fx.ResetAsync();
        await using var db = _fx.CreateContext();
        var svc = new ReportService(db, TimeProvider.System);

        var result = await svc.ResolveAsync(Guid.NewGuid(), Guid.NewGuid(), "resolved", default);

        result.Error.Should().Be(ReportResolveError.NotFound);
    }

    [Fact]
    public async Task ResolveAsync_TwoConcurrentResolvers_ExactlyOneSucceeds()
    {
        // Pins the CAS guarantee: with the previous read-modify-write, two moderators
        // racing on the same Open report would both load it, both think they were the
        // first resolver, and the loser's SaveChangesAsync would clobber the winner's
        // audit fields. ExecuteUpdateAsync makes the second resolver get AlreadyResolved
        // even when both calls launch from independent scopes.
        await _fx.ResetAsync();
        var (reporter, owner) = await SeedTwoUsersAsync();
        var clipId = await SeedClipAsync(owner);

        // ResolvedBy is FK-constrained to users; seed two real moderators so the UPDATE
        // doesn't fail with 23503 instead of testing the CAS path we care about.
        Guid modA, modB;
        await using (var seedDb = _fx.CreateContext())
        {
            var ma = new User
            {
                Username = "modA",
                Email = "ma@example.com",
                Role = UserRoles.Moderator,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            var mb = new User
            {
                Username = "modB",
                Email = "mb@example.com",
                Role = UserRoles.Moderator,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            seedDb.Users.AddRange(ma, mb);
            await seedDb.SaveChangesAsync();
            modA = ma.Id;
            modB = mb.Id;
        }

        Guid reportId;
        await using (var seedDb = _fx.CreateContext())
        {
            var created = await new ReportService(seedDb, TimeProvider.System)
                .CreateAsync(reporter, ReportTargetTypes.Clip, clipId, "spam", null, default);
            reportId = created.ReportId!.Value;
        }

        // Each task gets its own DbContext so they aren't serialised through one scope.
        var taskA = Task.Run(async () =>
        {
            await using var db = _fx.CreateContext();
            return await new ReportService(db, TimeProvider.System)
                .ResolveAsync(reportId, modA, ReportStatuses.Resolved, default);
        });
        var taskB = Task.Run(async () =>
        {
            await using var db = _fx.CreateContext();
            return await new ReportService(db, TimeProvider.System)
                .ResolveAsync(reportId, modB, ReportStatuses.Dismissed, default);
        });

        var results = await Task.WhenAll(taskA, taskB);
        results.Count(r => r.IsSuccess).Should().Be(1);
        results.Count(r => r.Error == ReportResolveError.AlreadyResolved).Should().Be(1);
    }

    [Fact]
    public async Task DbCheck_OtherWithoutNote_RejectsDirectInsert()
    {
        // Pins the defense-in-depth: ck_reports_other_note must reject an out-of-band
        // INSERT that bypasses ReportService. Without this round-trip, only the service
        // layer enforces "other requires a note" and a future call site (or hand-SQL)
        // could write a malformed row.
        await _fx.ResetAsync();
        var (reporter, owner) = await SeedTwoUsersAsync();
        var clipId = await SeedClipAsync(owner);

        await using var db = _fx.CreateContext();
        db.Reports.Add(new Report
        {
            ReporterId = reporter,
            TargetType = ReportTargetTypes.Clip,
            TargetId = clipId,
            Reason = ReportReasons.Other,
            Note = null,
            Status = ReportStatuses.Open,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
