using FluentAssertions;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Maintenance;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Maintenance;

[Collection("Postgres")]
public class MaintenanceHostedServiceIntegrationTests
{
    private readonly PostgresFixture _fx;

    public MaintenanceHostedServiceIntegrationTests(PostgresFixture fx) => _fx = fx;

    private (MaintenanceHostedService svc, IServiceScope scope, IObjectStorageService storage, FakeClock clock)
        Build(MaintenanceOptions options, DateTimeOffset now)
    {
        var clock = new FakeClock(now);
        var storage = Substitute.For<IObjectStorageService>();

        var services = new ServiceCollection();
        services.AddDbContext<GankedTvDbContext>(opts =>
            opts.UseNpgsql(_fx.ConnectionString).UseSnakeCaseNamingConvention());
        services.AddSingleton(storage);
        var sp = services.BuildServiceProvider();

        var optsMonitor = Substitute.For<IOptionsMonitor<MaintenanceOptions>>();
        optsMonitor.CurrentValue.Returns(options);
        var minioMonitor = Substitute.For<IOptionsMonitor<MinioOptions>>();
        minioMonitor.CurrentValue.Returns(new MinioOptions { ClipsBucket = "clips", ThumbnailsBucket = "thumbnails" });

        var svc = new MaintenanceHostedService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            optsMonitor,
            minioMonitor,
            clock,
            NullLogger<MaintenanceHostedService>.Instance);

        return (svc, sp.CreateScope(), storage, clock);
    }

    private async Task<Guid> SeedUserAsync(string username)
    {
        await using var db = _fx.CreateContext();
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

    [Fact]
    public async Task SweepOrphanedClipsAsync_DeletesOnlyOldDrafts_AndCallsStorage()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("alice");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _fx.CreateContext())
        {
            db.Clips.AddRange(
                new Clip
                {
                    UserId = userId,
                    Title = "stale-draft",
                    VideoKey = "user/stale.mp4",
                    ThumbnailKey = "user/stale.jpg",
                    Status = ClipStatuses.Draft,
                    CreatedAt = now.AddHours(-2),
                    UpdatedAt = now.AddHours(-2),
                },
                new Clip
                {
                    UserId = userId,
                    Title = "fresh-draft",
                    VideoKey = "user/fresh.mp4",
                    Status = ClipStatuses.Draft,
                    CreatedAt = now.AddMinutes(-5),
                    UpdatedAt = now.AddMinutes(-5),
                },
                new Clip
                {
                    UserId = userId,
                    Title = "old-ready",
                    VideoKey = "user/ready.mp4",
                    Status = ClipStatuses.Ready,
                    CreatedAt = now.AddHours(-2),
                    UpdatedAt = now.AddHours(-2),
                });
            await db.SaveChangesAsync();
        }

        var (svc, scope, storage, _) = Build(
            new MaintenanceOptions { ClipStaleThreshold = TimeSpan.FromHours(1) },
            now);

        await svc.SweepOrphanedClipsAsync(scope, CancellationToken.None);

        await using var verify = _fx.CreateContext();
        var remainingTitles = await verify.Clips.AsNoTracking().Select(c => c.Title).ToListAsync();
        remainingTitles.Should().BeEquivalentTo("fresh-draft", "old-ready");

        await storage.Received(1).DeleteObjectAsync("clips", "user/stale.mp4", Arg.Any<CancellationToken>());
        await storage.Received(1).DeleteObjectAsync("thumbnails", "user/stale.jpg", Arg.Any<CancellationToken>());
        await storage.DidNotReceive().DeleteObjectAsync("clips", "user/fresh.mp4", Arg.Any<CancellationToken>());
        await storage.DidNotReceive().DeleteObjectAsync("clips", "user/ready.mp4", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepOrphanedClipsAsync_NoCandidates_NoStorageCalls()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("bob");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _fx.CreateContext())
        {
            db.Clips.Add(new Clip
            {
                UserId = userId,
                Title = "fresh",
                VideoKey = "u/v.mp4",
                Status = ClipStatuses.Draft,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var (svc, scope, storage, _) = Build(
            new MaintenanceOptions { ClipStaleThreshold = TimeSpan.FromHours(1) },
            now);

        await svc.SweepOrphanedClipsAsync(scope, CancellationToken.None);

        await storage.DidNotReceive().DeleteObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunTick_ClipSweepFailure_DoesNotSkipRefreshTokenSweep()
    {
        // Independent try/catch around each sweep — sweep 1 throwing must not starve sweep 2.
        // Drive ExecuteAsync once via StartAsync/StopAsync with a long interval so only the
        // immediate-startup tick runs, then assert the token sweep still ran.
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("noah");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _fx.CreateContext())
        {
            db.RefreshTokens.Add(new RefreshToken
            {
                UserId = userId,
                TokenHash = "hash-ancient",
                FamilyId = Guid.NewGuid(),
                ExpiresAt = now.AddDays(-90),
            });
            await db.SaveChangesAsync();
        }

        // Storage substitute that throws on the clip-sweep path. Even though no draft clips
        // exist (so the sweep returns early on count == 0), we make the DbContext throw by
        // poisoning the IObjectStorageService resolution: register a factory that throws.
        var services = new ServiceCollection();
        services.AddDbContext<GankedTvDbContext>(opts =>
            opts.UseNpgsql(_fx.ConnectionString).UseSnakeCaseNamingConvention());
        services.AddScoped<IObjectStorageService>(_ => throw new InvalidOperationException("storage poisoned"));
        var sp = services.BuildServiceProvider();

        // Seed a stale draft clip so SweepOrphanedClipsAsync resolves IObjectStorageService and throws.
        await using (var db = _fx.CreateContext())
        {
            db.Clips.Add(new Clip
            {
                UserId = userId,
                Title = "stale",
                VideoKey = "k.mp4",
                Status = ClipStatuses.Draft,
                CreatedAt = now.AddHours(-2),
                UpdatedAt = now.AddHours(-2),
            });
            await db.SaveChangesAsync();
        }

        var optsMonitor = Substitute.For<IOptionsMonitor<MaintenanceOptions>>();
        optsMonitor.CurrentValue.Returns(new MaintenanceOptions
        {
            Enabled = true,
            SweepInterval = TimeSpan.FromHours(1),
            ClipStaleThreshold = TimeSpan.FromHours(1),
            RefreshTokenRetention = TimeSpan.FromDays(30),
        });
        var minioMonitor = Substitute.For<IOptionsMonitor<MinioOptions>>();
        minioMonitor.CurrentValue.Returns(new MinioOptions { ClipsBucket = "clips", ThumbnailsBucket = "thumbnails" });

        var svc = new MaintenanceHostedService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            optsMonitor,
            minioMonitor,
            new FakeClock(now),
            NullLogger<MaintenanceHostedService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        // Give the immediate tick a moment to execute both sweeps.
        await Task.Delay(500);
        await svc.StopAsync(CancellationToken.None);

        // Token sweep ran despite clip sweep throwing on storage resolution.
        await using var verify = _fx.CreateContext();
        (await verify.RefreshTokens.AsNoTracking().AnyAsync(t => t.TokenHash == "hash-ancient"))
            .Should().BeFalse("the refresh-token sweep must run even when the clip sweep fails");
    }

    [Fact]
    public async Task SweepExpiredRefreshTokensAsync_DeletesOnlyRowsBeyondRetention()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("carol");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _fx.CreateContext())
        {
            db.RefreshTokens.AddRange(
                new RefreshToken
                {
                    UserId = userId,
                    TokenHash = "hash-old",
                    FamilyId = Guid.NewGuid(),
                    ExpiresAt = now.AddDays(-60),
                },
                new RefreshToken
                {
                    UserId = userId,
                    TokenHash = "hash-recent-expired",
                    FamilyId = Guid.NewGuid(),
                    ExpiresAt = now.AddDays(-5),
                },
                new RefreshToken
                {
                    UserId = userId,
                    TokenHash = "hash-live",
                    FamilyId = Guid.NewGuid(),
                    ExpiresAt = now.AddDays(20),
                });
            await db.SaveChangesAsync();
        }

        var (svc, scope, _, _) = Build(
            new MaintenanceOptions { RefreshTokenRetention = TimeSpan.FromDays(30) },
            now);

        await svc.SweepExpiredRefreshTokensAsync(scope, CancellationToken.None);

        await using var verify = _fx.CreateContext();
        var hashes = await verify.RefreshTokens.AsNoTracking().Select(t => t.TokenHash).ToListAsync();
        hashes.Should().BeEquivalentTo("hash-recent-expired", "hash-live");
    }
}
