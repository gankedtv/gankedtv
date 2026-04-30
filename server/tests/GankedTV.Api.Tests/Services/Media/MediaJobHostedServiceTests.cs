using FluentAssertions;
using GankedTV.Api.Services.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GankedTV.Api.Tests.Services.Media;

public class MediaJobHostedServiceTests
{
    private static (MediaJobHostedService svc, IClipMediaJobStore store, IThumbnailJobService thumbnailer, IFfmpegRunner ffmpeg)
        Build(MediaJobOptions? options = null)
    {
        var store = Substitute.For<IClipMediaJobStore>();
        var thumbnailer = Substitute.For<IThumbnailJobService>();
        var ffmpeg = Substitute.For<IFfmpegRunner>();
        // Default the startup probe to "binary works" so existing tests don't need
        // to think about it. The probe-specific tests override this.
        ffmpeg.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new FfmpegResult(0, "ffmpeg version 99.0", ""));

        var services = new ServiceCollection();
        services.AddScoped(_ => store);
        services.AddScoped(_ => thumbnailer);
        var sp = services.BuildServiceProvider();

        var optsMonitor = Substitute.For<IOptionsMonitor<MediaJobOptions>>();
        optsMonitor.CurrentValue.Returns(options ?? new MediaJobOptions { MaxAttempts = 3 });

        var svc = new MediaJobHostedService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            ffmpeg,
            optsMonitor,
            NullLogger<MediaJobHostedService>.Instance);

        return (svc, store, thumbnailer, ffmpeg);
    }

    [Fact]
    public async Task TryProcessOneAsync_NoJob_ReturnsFalse()
    {
        var (svc, store, _, _) = Build();
        store.ClaimNextAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((ClaimedMediaJob?)null);

        var result = await svc.TryProcessOneAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryProcessOneAsync_HappyPath_MarksReadyWithThumbnailerResult()
    {
        var (svc, store, thumbnailer, _) = Build();
        var clipId = Guid.NewGuid();
        var job = new ClaimedMediaJob(clipId, Guid.NewGuid(), GameId: 1, VideoKey: "v.mp4", AttemptNumber: 1);
        store.ClaimNextAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        store.GetGameSlugAsync(1, Arg.Any<CancellationToken>()).Returns("valorant");
        var finalized = new FinalizedMediaJob("k.jpg", DurationSecs: 12, Width: 1920, Height: 1080);
        thumbnailer.ExtractAsync(job, "valorant", Arg.Any<CancellationToken>()).Returns(finalized);

        var result = await svc.TryProcessOneAsync(CancellationToken.None);

        result.Should().BeTrue();
        // Pass AttemptNumber so MarkReady's predicate guards against another worker that
        // re-claimed the row after our lease elapsed.
        await store.Received(1).MarkReadyAsync(clipId, 1, finalized, Arg.Any<CancellationToken>());
        await store.DidNotReceive().MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().ReleaseLeaseAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryProcessOneAsync_ExtractionThrowsOnNonFinalAttempt_ReleasesLease()
    {
        var (svc, store, thumbnailer, _) = Build(new MediaJobOptions { MaxAttempts = 3 });
        var clipId = Guid.NewGuid();
        var job = new ClaimedMediaJob(clipId, Guid.NewGuid(), GameId: null, VideoKey: "v.mp4", AttemptNumber: 1);
        store.ClaimNextAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        thumbnailer.ExtractAsync(Arg.Any<ClaimedMediaJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ffmpeg fell over"));

        var result = await svc.TryProcessOneAsync(CancellationToken.None);

        result.Should().BeTrue();
        // Asserts the locked invariant: shutdown-safe finalization uses CancellationToken.None
        // so a transient retry release isn't lost when the host is stopping. AttemptNumber=1
        // is passed so the release only fires for our own claim, not a re-claim by another
        // worker after our lease elapsed.
        await store.Received(1).ReleaseLeaseAsync(clipId, 1, CancellationToken.None);
        await store.DidNotReceive().MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().MarkReadyAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<FinalizedMediaJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryProcessOneAsync_ExtractionThrowsOnFinalAttempt_MarksFailed()
    {
        var (svc, store, thumbnailer, _) = Build(new MediaJobOptions { MaxAttempts = 3 });
        var clipId = Guid.NewGuid();
        var job = new ClaimedMediaJob(clipId, Guid.NewGuid(), GameId: null, VideoKey: "v.mp4", AttemptNumber: 3);
        store.ClaimNextAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        thumbnailer.ExtractAsync(Arg.Any<ClaimedMediaJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("permanent decode failure"));

        var result = await svc.TryProcessOneAsync(CancellationToken.None);

        result.Should().BeTrue();
        // Asserts the locked invariant: a final-attempt failure is recorded with
        // CancellationToken.None so it isn't lost during shutdown. AttemptNumber=3
        // is passed so the kill only fires if the row is still on our claim.
        await store.Received(1).MarkFailedAsync(clipId, 3, CancellationToken.None);
        await store.DidNotReceive().ReleaseLeaseAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryProcessOneAsync_ClaimThrows_ReturnsFalseAndDoesNotPropagate()
    {
        var (svc, store, _, _) = Build();
        store.ClaimNextAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db is down"));

        var result = await svc.TryProcessOneAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryProcessOneAsync_CancellationDuringExtraction_Propagates()
    {
        var (svc, store, thumbnailer, _) = Build();
        var job = new ClaimedMediaJob(Guid.NewGuid(), Guid.NewGuid(), null, "v.mp4", 1);
        store.ClaimNextAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        thumbnailer.ExtractAsync(Arg.Any<ClaimedMediaJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var act = async () => await svc.TryProcessOneAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteAsync_Disabled_ReturnsImmediately()
    {
        var (svc, store, _, _) = Build(new MediaJobOptions { Enabled = false });

        await svc.StartAsync(CancellationToken.None);
        await svc.StopAsync(CancellationToken.None);

        await store.DidNotReceive().ClaimNextAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_DrainsQueueThenWaitsForTick()
    {
        // Configure a long PollInterval so the only claims that happen are the ones we
        // explicitly script in the queue. ClaimNextAsync returns three jobs, then null,
        // then ExecuteAsync sits on WaitForNextTickAsync until we cancel.
        var (svc, store, thumbnailer, _) = Build(new MediaJobOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMinutes(5),
            MaxAttempts = 3,
        });

        var jobs = new Queue<ClaimedMediaJob?>(new ClaimedMediaJob?[]
        {
            new(Guid.NewGuid(), Guid.NewGuid(), null, "a.mp4", 1),
            new(Guid.NewGuid(), Guid.NewGuid(), null, "b.mp4", 1),
            new(Guid.NewGuid(), Guid.NewGuid(), null, "c.mp4", 1),
            null,
        });
        store.ClaimNextAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => jobs.Count == 0 ? null : jobs.Dequeue());
        thumbnailer.ExtractAsync(Arg.Any<ClaimedMediaJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new FinalizedMediaJob("k.jpg", 1, 1, 1));

        await svc.StartAsync(CancellationToken.None);
        // Spin briefly until the queue drains. The drain is synchronous-ish in test —
        // just yield until ClaimNextAsync has been called four times (3 jobs + 1 null).
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (store.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IClipMediaJobStore.ClaimNextAsync)) >= 4) break;
            await Task.Delay(10);
        }
        await svc.StopAsync(CancellationToken.None);

        await store.Received(3).MarkReadyAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<FinalizedMediaJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_StartupProbeRunsForBothBinaries()
    {
        // The startup probe is a one-shot ffmpeg/ffprobe -version call so a misconfigured
        // host fails loudly. Verify both probes fire and ClaimNextAsync still runs after.
        var (svc, store, _, ffmpeg) = Build(new MediaJobOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMinutes(5),
            FfmpegPath = "ffmpeg",
            FfprobePath = "ffprobe",
        });
        store.ClaimNextAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((ClaimedMediaJob?)null);

        await svc.StartAsync(CancellationToken.None);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var probes = ffmpeg.ReceivedCalls()
                .Count(c => c.GetMethodInfo().Name == nameof(IFfmpegRunner.RunAsync));
            if (probes >= 2) break;
            await Task.Delay(10);
        }
        await svc.StopAsync(CancellationToken.None);

        await ffmpeg.Received(1).RunAsync(
            "ffmpeg",
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("-version")),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
        await ffmpeg.Received(1).RunAsync(
            "ffprobe",
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("-version")),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_StartupProbeFailure_DoesNotPreventClaiming()
    {
        // Missing ffmpeg shouldn't crash startup — it should warn and let the loop continue
        // so a fix-via-config path (FFMPEG_PATH env var) doesn't require a restart.
        var (svc, store, _, ffmpeg) = Build(new MediaJobOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMinutes(5),
        });
        ffmpeg.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new System.ComponentModel.Win32Exception("no such file"));
        store.ClaimNextAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((ClaimedMediaJob?)null);

        await svc.StartAsync(CancellationToken.None);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var claims = store.ReceivedCalls()
                .Count(c => c.GetMethodInfo().Name == nameof(IClipMediaJobStore.ClaimNextAsync));
            if (claims >= 1) break;
            await Task.Delay(10);
        }
        await svc.StopAsync(CancellationToken.None);

        await store.Received().ClaimNextAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_DrainCapHonored_StopsAfterMaxDrainPerTick()
    {
        // With MaxDrainPerTick=2 and ClaimNextAsync always returning a job, the drain
        // loop should stop at 2 and yield to the timer (which we never let fire).
        var (svc, store, thumbnailer, _) = Build(new MediaJobOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMinutes(5),
            MaxAttempts = 3,
            MaxDrainPerTick = 2,
        });
        store.ClaimNextAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ClaimedMediaJob(Guid.NewGuid(), Guid.NewGuid(), null, "v.mp4", 1));
        thumbnailer.ExtractAsync(Arg.Any<ClaimedMediaJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new FinalizedMediaJob("k.jpg", 1, 1, 1));

        await svc.StartAsync(CancellationToken.None);
        // Wait long enough for any drain runaway to manifest, then stop. With the cap
        // working, MarkReadyAsync should be called exactly twice, not unbounded.
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(500);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        await svc.StopAsync(CancellationToken.None);

        var marks = store.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IClipMediaJobStore.MarkReadyAsync));
        marks.Should().Be(2);
    }
}
