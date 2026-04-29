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

    private sealed class Harness : IAsyncDisposable
    {
        public required MaintenanceHostedService Service { get; init; }
        public required IServiceScope Scope { get; init; }
        public required IObjectStorageService Storage { get; init; }
        public required FakeClock Clock { get; init; }
        public required ServiceProvider Provider { get; init; }

        public async ValueTask DisposeAsync()
        {
            Scope.Dispose();
            await Provider.DisposeAsync();
        }
    }

    private Harness Build(MaintenanceOptions options, DateTimeOffset now)
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

        return new Harness
        {
            Service = svc,
            Scope = sp.CreateScope(),
            Storage = storage,
            Clock = clock,
            Provider = sp,
        };
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

        await using var harness = Build(
            new MaintenanceOptions { ClipStaleThreshold = TimeSpan.FromHours(1) },
            now);

        await harness.Service.SweepOrphanedClipsAsync(harness.Scope, CancellationToken.None);

        await using var verify = _fx.CreateContext();
        var remainingTitles = await verify.Clips.AsNoTracking().Select(c => c.Title).ToListAsync();
        remainingTitles.Should().BeEquivalentTo("fresh-draft", "old-ready");

        await harness.Storage.Received(1).DeleteObjectAsync("clips", "user/stale.mp4", Arg.Any<CancellationToken>());
        await harness.Storage.Received(1).DeleteObjectAsync("thumbnails", "user/stale.jpg", Arg.Any<CancellationToken>());
        await harness.Storage.DidNotReceive().DeleteObjectAsync("clips", "user/fresh.mp4", Arg.Any<CancellationToken>());
        await harness.Storage.DidNotReceive().DeleteObjectAsync("clips", "user/ready.mp4", Arg.Any<CancellationToken>());
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

        await using var harness = Build(
            new MaintenanceOptions { ClipStaleThreshold = TimeSpan.FromHours(1) },
            now);

        await harness.Service.SweepOrphanedClipsAsync(harness.Scope, CancellationToken.None);

        await harness.Storage.DidNotReceive().DeleteObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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

        // Storage factory that throws so the clip sweep fails. Seed a stale draft clip so
        // SweepOrphanedClipsAsync actually resolves IObjectStorageService and hits the throw.
        var services = new ServiceCollection();
        services.AddDbContext<GankedTvDbContext>(opts =>
            opts.UseNpgsql(_fx.ConnectionString).UseSnakeCaseNamingConvention());
        services.AddScoped<IObjectStorageService>(_ => throw new InvalidOperationException("storage poisoned"));
        await using var sp = services.BuildServiceProvider();

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
            ClipBatchSize = 1000,
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

        // Poll deterministically until the token sweep has run (or the budget elapses).
        // The sweep deletes hash-ancient — when it's gone, sweep 2 has executed.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var poll = _fx.CreateContext();
            if (!await poll.RefreshTokens.AsNoTracking().AnyAsync(t => t.TokenHash == "hash-ancient"))
            {
                break;
            }
            await Task.Delay(50);
        }

        await svc.StopAsync(CancellationToken.None);

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

        await using var harness = Build(
            new MaintenanceOptions { RefreshTokenRetention = TimeSpan.FromDays(30) },
            now);

        await harness.Service.SweepExpiredRefreshTokensAsync(harness.Scope, CancellationToken.None);

        await using var verify = _fx.CreateContext();
        var hashes = await verify.RefreshTokens.AsNoTracking().Select(t => t.TokenHash).ToListAsync();
        hashes.Should().BeEquivalentTo("hash-recent-expired", "hash-live");
    }
}
