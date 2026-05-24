using FluentAssertions;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Caching;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GankedTV.Api.Tests.Services.Media;

// Covers the shared MediaStageWorker loop via its three concrete subclasses (ThumbnailWorker,
// CompressWorker, StreamRenditionWorker). The lease/retry/shutdown invariants live in the base,
// so the thumbnail tests exercise them; the other stages confirm their wiring + finalizers.
public class MediaStageWorkerTests
{
    private static (ThumbnailWorker svc, IClipMediaJobStore store, IThumbnailJobService thumbnailer)
        BuildThumbnail(MediaJobOptions? options = null)
    {
        var store = Substitute.For<IClipMediaJobStore>();
        var thumbnailer = Substitute.For<IThumbnailJobService>();
        var sp = Scope(s => { s.AddScoped(_ => store); s.AddScoped(_ => thumbnailer); });
        var svc = new ThumbnailWorker(sp.GetRequiredService<IServiceScopeFactory>(), Ffmpeg(), Monitor(options), NullLogger<ThumbnailWorker>.Instance);
        return (svc, store, thumbnailer);
    }

    private static (CompressWorker svc, IClipMediaJobStore store, ICompressJobService compressor, IObjectStorageService storage)
        BuildCompress(MediaJobOptions? options = null)
    {
        var store = Substitute.For<IClipMediaJobStore>();
        var compressor = Substitute.For<ICompressJobService>();
        var storage = Substitute.For<IObjectStorageService>();
        var s3 = Substitute.For<IOptionsMonitor<S3Options>>();
        s3.CurrentValue.Returns(new S3Options { ClipsBucket = "clips" });
        var sp = Scope(s =>
        {
            s.AddScoped(_ => store);
            s.AddScoped(_ => compressor);
            s.AddScoped(_ => storage);
            s.AddScoped(_ => s3);
        });
        var svc = new CompressWorker(sp.GetRequiredService<IServiceScopeFactory>(), Ffmpeg(), Monitor(options), NullLogger<CompressWorker>.Instance);
        return (svc, store, compressor, storage);
    }

    private static (StreamRenditionWorker svc, IClipStreamJobStore store, IJitLadderService jit)
        BuildStream(MediaJobOptions? options = null)
    {
        var store = Substitute.For<IClipStreamJobStore>();
        var jit = Substitute.For<IJitLadderService>();
        var sp = Scope(s => { s.AddScoped(_ => store); s.AddScoped(_ => jit); });
        var svc = new StreamRenditionWorker(sp.GetRequiredService<IServiceScopeFactory>(), Ffmpeg(), Monitor(options), NullLogger<StreamRenditionWorker>.Instance);
        return (svc, store, jit);
    }

    private static IFfmpegRunner Ffmpeg()
    {
        var ffmpeg = Substitute.For<IFfmpegRunner>();
        ffmpeg.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new FfmpegResult(0, "ffmpeg version 99.0", ""));
        return ffmpeg;
    }

    private static IServiceProvider Scope(Action<ServiceCollection> register)
    {
        var services = new ServiceCollection();
        register(services);
        return services.BuildServiceProvider();
    }

    private static IOptionsMonitor<MediaJobOptions> Monitor(MediaJobOptions? options)
    {
        var m = Substitute.For<IOptionsMonitor<MediaJobOptions>>();
        m.CurrentValue.Returns(options ?? new MediaJobOptions { MaxAttempts = 3 });
        return m;
    }

    private static ClaimedMediaJob ClipJob(int attempt = 1, short? height = 1080) =>
        new(Guid.NewGuid(), Guid.NewGuid(), GameId: null, VideoKey: "v.mp4", SourceHeight: height, AttemptNumber: attempt);

    // --- Thumbnail stage (exercises the shared loop) ----------------------------------

    [Fact]
    public async Task Thumbnail_NoJob_ReturnsFalse()
    {
        var (svc, store, _) = BuildThumbnail();
        store.ClaimNextAsync(ClipStatuses.Processing, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((ClaimedMediaJob?)null);

        (await svc.TryProcessOneAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Thumbnail_HappyPath_AdvancesToTranscodingWhenEnabled()
    {
        var (svc, store, thumbnailer) = BuildThumbnail(new MediaJobOptions { MaxAttempts = 3, TranscodeEnabled = true });
        var job = ClipJob();
        store.ClaimNextAsync(ClipStatuses.Processing, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        var finalized = new FinalizedMediaJob("k.jpg", 12, 1920, 1080);
        thumbnailer.ExtractAsync(job, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(finalized);

        (await svc.TryProcessOneAsync(CancellationToken.None)).Should().BeTrue();

        await store.Received(1).AdvanceThumbnailAsync(job.ClipId, 1, finalized, ClipStatuses.Transcoding, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Thumbnail_HappyPath_AdvancesToReadyWhenCompressionDisabled()
    {
        var (svc, store, thumbnailer) = BuildThumbnail(new MediaJobOptions { MaxAttempts = 3, TranscodeEnabled = false });
        var job = ClipJob();
        store.ClaimNextAsync(ClipStatuses.Processing, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        var finalized = new FinalizedMediaJob("k.jpg", 12, 1920, 1080);
        thumbnailer.ExtractAsync(job, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(finalized);

        await svc.TryProcessOneAsync(CancellationToken.None);

        await store.Received(1).AdvanceThumbnailAsync(job.ClipId, 1, finalized, ClipStatuses.Ready, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Thumbnail_ThrowsOnNonFinalAttempt_ReleasesLeaseWithStatus()
    {
        var (svc, store, thumbnailer) = BuildThumbnail(new MediaJobOptions { MaxAttempts = 3 });
        var job = ClipJob(attempt: 1);
        store.ClaimNextAsync(ClipStatuses.Processing, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        thumbnailer.ExtractAsync(Arg.Any<ClaimedMediaJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ffmpeg fell over"));

        await svc.TryProcessOneAsync(CancellationToken.None);

        await store.Received(1).ReleaseLeaseAsync(job.ClipId, 1, ClipStatuses.Processing, CancellationToken.None);
        await store.DidNotReceive().MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Thumbnail_ThrowsOnFinalAttempt_MarksFailedWithStatus()
    {
        var (svc, store, thumbnailer) = BuildThumbnail(new MediaJobOptions { MaxAttempts = 3 });
        var job = ClipJob(attempt: 3);
        store.ClaimNextAsync(ClipStatuses.Processing, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        thumbnailer.ExtractAsync(Arg.Any<ClaimedMediaJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("permanent decode failure"));

        await svc.TryProcessOneAsync(CancellationToken.None);

        // Terminal failure carries the stage's structured reason so the status endpoint can
        // surface "we couldn't generate a thumbnail" instead of a generic "failed" message.
        await store.Received(1).MarkFailedAsync(
            job.ClipId, 3, ClipStatuses.Processing, CancellationToken.None,
            reason: ClipFailureReasons.ThumbnailFailed);
    }

    [Fact]
    public async Task Thumbnail_ClaimThrows_ReturnsFalse()
    {
        var (svc, store, _) = BuildThumbnail();
        store.ClaimNextAsync(ClipStatuses.Processing, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db is down"));

        (await svc.TryProcessOneAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Thumbnail_CancellationDuringWork_Propagates()
    {
        var (svc, store, thumbnailer) = BuildThumbnail();
        store.ClaimNextAsync(ClipStatuses.Processing, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(ClipJob());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        thumbnailer.ExtractAsync(Arg.Any<ClaimedMediaJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var act = async () => await svc.TryProcessOneAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Thumbnail_Disabled_ReturnsImmediately()
    {
        var (svc, store, _) = BuildThumbnail(new MediaJobOptions { Enabled = false });
        await svc.StartAsync(CancellationToken.None);
        await svc.StopAsync(CancellationToken.None);
        await store.DidNotReceive().ClaimNextAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Thumbnail_WorkerDisabled_ReturnsImmediately()
    {
        var (svc, store, _) = BuildThumbnail(new MediaJobOptions { Enabled = true, ThumbnailWorkerEnabled = false });
        await svc.StartAsync(CancellationToken.None);
        await svc.StopAsync(CancellationToken.None);
        await store.DidNotReceive().ClaimNextAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Thumbnail_DrainCapHonored_StopsAfterMaxDrainPerTick()
    {
        var (svc, store, thumbnailer) = BuildThumbnail(new MediaJobOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMinutes(5),
            MaxAttempts = 3,
            MaxDrainPerTick = 2,
        });
        store.ClaimNextAsync(ClipStatuses.Processing, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ => ClipJob());
        thumbnailer.ExtractAsync(Arg.Any<ClaimedMediaJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new FinalizedMediaJob("k.jpg", 1, 1, 1));

        await svc.StartAsync(CancellationToken.None);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(500);
        while (DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
        await svc.StopAsync(CancellationToken.None);

        store.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IClipMediaJobStore.AdvanceThumbnailAsync)).Should().Be(2);
    }

    [Fact]
    public async Task Thumbnail_StartupProbeRunsForBothBinaries()
    {
        var store = Substitute.For<IClipMediaJobStore>();
        var thumbnailer = Substitute.For<IThumbnailJobService>();
        var ffmpeg = Ffmpeg();
        var sp = Scope(s => { s.AddScoped(_ => store); s.AddScoped(_ => thumbnailer); });
        var svc = new ThumbnailWorker(sp.GetRequiredService<IServiceScopeFactory>(), ffmpeg,
            Monitor(new MediaJobOptions { Enabled = true, PollInterval = TimeSpan.FromMinutes(5) }),
            NullLogger<ThumbnailWorker>.Instance);
        store.ClaimNextAsync(ClipStatuses.Processing, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((ClaimedMediaJob?)null);

        await svc.StartAsync(CancellationToken.None);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ffmpeg.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IFfmpegRunner.RunAsync)) >= 2) break;
            await Task.Delay(10);
        }
        await svc.StopAsync(CancellationToken.None);

        await ffmpeg.Received(1).RunAsync("ffmpeg", Arg.Is<IReadOnlyList<string>>(a => a.Contains("-version")), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await ffmpeg.Received(1).RunAsync("ffprobe", Arg.Is<IReadOnlyList<string>>(a => a.Contains("-version")), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    // --- Compress stage ----------------------------------------------------------------

    [Fact]
    public async Task Compress_HappyPath_CompletesThenDeletesOriginal()
    {
        var (svc, store, compressor, storage) = BuildCompress(new MediaJobOptions { MaxAttempts = 3 });
        var job = ClipJob();
        store.ClaimNextAsync(ClipStatuses.Transcoding, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        compressor.CompressAsync(job, Arg.Any<CancellationToken>())
            .Returns(new CompressionResult("u/clip.cmp.mp4", "av1", "u/clip.mp4"));

        (await svc.TryProcessOneAsync(CancellationToken.None)).Should().BeTrue();

        await store.Received(1).CompleteCompressionAsync(job.ClipId, 1, "u/clip.cmp.mp4", "av1", Arg.Any<CancellationToken>());
        await storage.Received(1).DeleteObjectAsync("clips", "u/clip.mp4", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Compress_FeedCacheInvalidationThrows_StillCompletesAndDoesNotFail()
    {
        // Codifies the "Redis unavailable → no failure" guarantee for the ready transition: the
        // best-effort feed invalidation runs after the clip is already 'ready', so a throwing
        // IFeedCache must be swallowed — never tripping the stage's MarkFailed/ReleaseLease path.
        var store = Substitute.For<IClipMediaJobStore>();
        var compressor = Substitute.For<ICompressJobService>();
        var storage = Substitute.For<IObjectStorageService>();
        var s3 = Substitute.For<IOptionsMonitor<S3Options>>();
        s3.CurrentValue.Returns(new S3Options { ClipsBucket = "clips" });
        var feedCache = Substitute.For<IFeedCache>();
        feedCache.When(c => c.InvalidateFeedsAsync(Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("redis down"));
        var sp = Scope(s =>
        {
            s.AddScoped(_ => store);
            s.AddScoped(_ => compressor);
            s.AddScoped(_ => storage);
            s.AddScoped(_ => s3);
            s.AddScoped(_ => feedCache);
        });
        var svc = new CompressWorker(
            sp.GetRequiredService<IServiceScopeFactory>(), Ffmpeg(),
            Monitor(new MediaJobOptions { MaxAttempts = 3 }), NullLogger<CompressWorker>.Instance);
        var job = ClipJob();
        store.ClaimNextAsync(ClipStatuses.Transcoding, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        compressor.CompressAsync(job, Arg.Any<CancellationToken>())
            .Returns(new CompressionResult("same.mp4", "h264", "same.mp4"));

        (await svc.TryProcessOneAsync(CancellationToken.None)).Should().BeTrue();

        await store.Received(1).CompleteCompressionAsync(job.ClipId, 1, "same.mp4", "h264", Arg.Any<CancellationToken>());
        await store.DidNotReceive().MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().ReleaseLeaseAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Compress_DoesNotDeleteWhenKeyUnchanged()
    {
        var (svc, store, compressor, storage) = BuildCompress(new MediaJobOptions { MaxAttempts = 3 });
        var job = ClipJob();
        store.ClaimNextAsync(ClipStatuses.Transcoding, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        compressor.CompressAsync(job, Arg.Any<CancellationToken>())
            .Returns(new CompressionResult("same.mp4", "h264", "same.mp4"));

        await svc.TryProcessOneAsync(CancellationToken.None);

        await store.Received(1).CompleteCompressionAsync(job.ClipId, 1, "same.mp4", "h264", Arg.Any<CancellationToken>());
        await storage.DidNotReceive().DeleteObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Compress_ThrowsOnFinalAttempt_MarksFailedWithTranscodingStatus()
    {
        var (svc, store, compressor, _) = BuildCompress(new MediaJobOptions { MaxAttempts = 3 });
        var job = ClipJob(attempt: 3);
        store.ClaimNextAsync(ClipStatuses.Transcoding, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        compressor.CompressAsync(Arg.Any<ClaimedMediaJob>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("nvenc unavailable"));

        await svc.TryProcessOneAsync(CancellationToken.None);

        // Same structured-reason contract as the thumbnail stage — the compress stage uses
        // 'transcode_failed' so the wizard can render stage-specific copy.
        await store.Received(1).MarkFailedAsync(
            job.ClipId, 3, ClipStatuses.Transcoding, CancellationToken.None,
            reason: ClipFailureReasons.TranscodeFailed);
    }

    // --- JIT stream stage ---------------------------------------------------------------

    [Fact]
    public async Task Stream_HappyPath_BuildsThenCompletes()
    {
        var (svc, store, jit) = BuildStream(new MediaJobOptions { MaxAttempts = 3 });
        var job = new ClaimedStreamJob(Guid.NewGuid(), "u/clip.cmp.mp4", 720, 1);
        store.ClaimNextAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        jit.BuildAsync(job, Arg.Any<CancellationToken>()).Returns("abc/master.m3u8");

        (await svc.TryProcessOneAsync(CancellationToken.None)).Should().BeTrue();

        await store.Received(1).CompleteAsync(job.ClipId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stream_ThrowsOnFinalAttempt_MarksFailed()
    {
        var (svc, store, jit) = BuildStream(new MediaJobOptions { MaxAttempts = 3 });
        var job = new ClaimedStreamJob(Guid.NewGuid(), "u/clip.cmp.mp4", 720, 3);
        store.ClaimNextAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        jit.BuildAsync(Arg.Any<ClaimedStreamJob>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("transcode boom"));

        await svc.TryProcessOneAsync(CancellationToken.None);

        await store.Received(1).MarkFailedAsync(job.ClipId, 3, CancellationToken.None);
        await store.DidNotReceive().CompleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stream_WorkerDisabled_ReturnsImmediately()
    {
        var (svc, store, _) = BuildStream(new MediaJobOptions { Enabled = true, TranscodeWorkerEnabled = false });
        await svc.StartAsync(CancellationToken.None);
        await svc.StopAsync(CancellationToken.None);
        await store.DidNotReceive().ClaimNextAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
