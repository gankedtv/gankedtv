using FluentAssertions;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Clips;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Services.Media.Import;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GankedTV.Api.Tests.Services.Media;

public class ImportWorkerTests
{
    private sealed record Harness(
        ImportWorker Worker,
        IClipMediaJobStore Store,
        IClipImportSource Source,
        IObjectStorageService Storage,
        IClipImportUrlValidator Validator,
        IFfmpegRunner Ffmpeg);

    // ffprobeStdout, when non-null, replaces the default empty-JSON ffmpeg/ffprobe stdout —
    // lets tests simulate "ffprobe says duration=240s" without rebuilding the harness.
    private static Harness Build(
        MediaJobOptions? options = null,
        ClipValidationOptions? validation = null,
        string? ffprobeStdout = null)
    {
        var store = Substitute.For<IClipMediaJobStore>();
        var source = Substitute.For<IClipImportSource>();
        var storage = Substitute.For<IObjectStorageService>();
        var validator = Substitute.For<IClipImportUrlValidator>();
        var s3 = Substitute.For<IOptionsMonitor<S3Options>>();
        s3.CurrentValue.Returns(new S3Options { ClipsBucket = "clips" });
        var validationOpts = Microsoft.Extensions.Options.Options.Create(validation ?? new ClipValidationOptions());

        // Default the URL validator to accept anything (TryParse → true). Individual tests
        // override when they want the worker's defence-in-depth path exercised.
        validator
            .TryParse(Arg.Any<string?>(), out Arg.Any<string>(), out Arg.Any<ImportUrlValidationError>())
            .Returns(call =>
            {
                call[1] = (string?)call[0] ?? string.Empty;
                call[2] = default(ImportUrlValidationError);
                return true;
            });

        // Mock ffmpeg runner — default returns exit 0 with empty stdout so ffprobe-derived
        // duration is null (i.e. unknown), which the worker treats as "skip the duration
        // cap check, trust yt-dlp's metadata". Individual tests override when they want to
        // simulate an actual ffprobe duration.
        var ffmpeg = Substitute.For<IFfmpegRunner>();
        ffmpeg.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new FfmpegResult(0, ffprobeStdout ?? "ok", ""));

        var services = new ServiceCollection();
        services.AddScoped(_ => store);
        services.AddScoped(_ => source);
        services.AddScoped(_ => storage);
        services.AddScoped(_ => validator);
        services.AddScoped(_ => ffmpeg);
        services.AddScoped<IOptionsMonitor<S3Options>>(_ => s3);
        services.AddScoped<IOptions<ClipValidationOptions>>(_ => validationOpts);
        var sp = services.BuildServiceProvider();
        var monitor = Substitute.For<IOptionsMonitor<MediaJobOptions>>();
        monitor.CurrentValue.Returns(options ?? new MediaJobOptions { MaxAttempts = 3 });

        var worker = new ImportWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            ffmpeg,
            monitor,
            NullLogger<ImportWorker>.Instance);
        return new Harness(worker, store, source, storage, validator, ffmpeg);
    }

    private static ClaimedImportJob ImportJob(int attempt = 1) =>
        new(Guid.NewGuid(), Guid.NewGuid(), GameId: null,
            VideoKey: "u/v.mp4",
            ImportSourceUrl: "https://medal.tv/clips/abc",
            Title: "from import",
            AttemptNumber: attempt);

    [Fact]
    public async Task ImportWorker_StartupProbe_ProbesYtdlpWithDoubleDashVersion()
    {
        var h = Build(new MediaJobOptions { Enabled = true, PollInterval = TimeSpan.FromMinutes(5), MaxAttempts = 3 });
        h.Store.ClaimNextImportAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((ClaimedImportJob?)null);

        await h.Worker.StartAsync(CancellationToken.None);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (h.Ffmpeg.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IFfmpegRunner.RunAsync)) >= 3) break;
            await Task.Delay(10);
        }
        await h.Worker.StopAsync(CancellationToken.None);

        // yt-dlp needs GNU-style --version (a single dash misparses as bundled short flags);
        // ffmpeg/ffprobe keep the single-dash form.
        await h.Ffmpeg.Received(1).RunAsync("yt-dlp",
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("--version")), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await h.Ffmpeg.Received(1).RunAsync("ffmpeg",
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("-version")), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await h.Ffmpeg.Received(1).RunAsync("ffprobe",
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("-version")), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportWorker_NoJob_ReturnsFalse()
    {
        var h = Build();
        h.Store.ClaimNextImportAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((ClaimedImportJob?)null);

        (await h.Worker.TryProcessOneAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ImportWorker_HappyPath_AdvancesToProcessing()
    {
        var h = Build();
        var job = ImportJob();
        h.Store.ClaimNextImportAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);

        // Fake fetch: write a small file at the destination so the worker's stream upload + size
        // assertions succeed.
        h.Source
            .FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<ImportFetchOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var dest = (string)call[1];
                File.WriteAllBytes(dest, new byte[1024]);
                return Task.FromResult(new ImportedMedia("Extractor Title", 30, 1280, 720, null));
            });

        var processed = await h.Worker.TryProcessOneAsync(CancellationToken.None);

        processed.Should().BeTrue();
        await h.Storage.Received(1).PutObjectAsync(
            "clips", job.VideoKey, Arg.Any<Stream>(), "video/mp4", Arg.Any<CancellationToken>());
        await h.Store.Received(1).AdvanceImportAsync(
            job.ClipId,
            job.AttemptNumber,
            Arg.Any<long>(),
            "Extractor Title",
            ClipImportDefaults.PlaceholderTitle,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportWorker_FetchThrows_ReleasesLeaseForRetry()
    {
        var h = Build();
        var job = ImportJob(attempt: 1);
        h.Store.ClaimNextImportAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        h.Source
            .FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ImportFetchOptions>(), Arg.Any<CancellationToken>())
            .Throws(new ImportFetchException("network"));

        var processed = await h.Worker.TryProcessOneAsync(CancellationToken.None);

        processed.Should().BeTrue();
        await h.Store.Received(1).ReleaseLeaseAsync(
            job.ClipId, job.AttemptNumber, ClipStatuses.Importing, Arg.Any<CancellationToken>());
        await h.Store.DidNotReceive().AdvanceImportAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<long>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportWorker_FetchThrowsAtMaxAttempts_MarksFailed()
    {
        var h = Build(new MediaJobOptions { MaxAttempts = 2 });
        var job = ImportJob(attempt: 2);
        h.Store.ClaimNextImportAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        h.Source
            .FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ImportFetchOptions>(), Arg.Any<CancellationToken>())
            .Throws(new ImportFetchException("extractor error"));

        await h.Worker.TryProcessOneAsync(CancellationToken.None);

        // Retry-exhaustion terminal failure → 'fetch_failed' so the wizard surfaces a useful
        // message instead of the neutral fallback. Pre-flight rejections (too long /
        // unavailable) write their own codes via the ProcessAsync catch.
        await h.Store.Received(1).MarkFailedAsync(
            job.ClipId, job.AttemptNumber, ClipStatuses.Importing, Arg.Any<CancellationToken>(),
            reason: ClipFailureReasons.FetchFailed);
    }

    [Fact]
    public async Task ImportWorker_OversizeFile_FailsImmediately()
    {
        var h = Build(new MediaJobOptions { MaxAttempts = 3 }, new ClipValidationOptions { MaxUploadSizeMb = 1 });
        var job = ImportJob();
        h.Store.ClaimNextImportAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        h.Source
            .FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ImportFetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var dest = (string)call[1];
                // 2 MB > 1 MB cap.
                File.WriteAllBytes(dest, new byte[2 * 1024 * 1024]);
                return Task.FromResult(new ImportedMedia(null, null, null, null, null));
            });

        await h.Worker.TryProcessOneAsync(CancellationToken.None);

        // Oversize is a non-transient rejection — the worker should fail-fast (mark the row
        // 'failed' with reason='source_too_large') on attempt 1 instead of retrying.
        await h.Storage.DidNotReceive().PutObjectAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await h.Store.Received(1).MarkFailedAsync(
            job.ClipId, job.AttemptNumber, ClipStatuses.Importing, Arg.Any<CancellationToken>(),
            reason: ClipFailureReasons.SourceTooLarge);
        await h.Store.DidNotReceive().ReleaseLeaseAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportWorker_ProbeRejectsByDuration_FailsImmediately()
    {
        var h = Build(new MediaJobOptions { MaxAttempts = 3 });
        var job = ImportJob();
        h.Store.ClaimNextImportAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        h.Source
            .FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ImportFetchOptions>(), Arg.Any<CancellationToken>())
            .Throws(new ImportSourceRejectedException(
                ClipFailureReasons.SourceTooLong, "duration 240s > 120s", actualDurationSecs: 240));

        await h.Worker.TryProcessOneAsync(CancellationToken.None);

        // Pre-flight rejection (metadata probe says duration > cap) → no retries, mark 'failed'
        // with the structured reason. ReleaseLease must not be called.
        await h.Store.Received(1).MarkFailedAsync(
            job.ClipId, job.AttemptNumber, ClipStatuses.Importing, Arg.Any<CancellationToken>(),
            reason: ClipFailureReasons.SourceTooLong);
        await h.Store.DidNotReceive().ReleaseLeaseAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportWorker_FfprobeReportsDurationOverCap_FailsImmediately()
    {
        // The authoritative post-download cap check: yt-dlp's metadata may have lied (or
        // omitted duration), but ffprobe on the actual file is impossible to bypass. When
        // ffprobe reports duration > cap, the worker must fail-fast with 'source_too_long'
        // — no S3 upload, no retry, no AdvanceImportAsync.
        var h = Build(ffprobeStdout: """{"format":{"duration":"240.0"}}""");
        var job = ImportJob();
        h.Store.ClaimNextImportAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(job);
        h.Source
            .FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ImportFetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var dest = (string)call[1];
                File.WriteAllBytes(dest, new byte[1024]);
                // Metadata says null duration — extractor lied / omitted. ffprobe is the gate.
                return Task.FromResult(new ImportedMedia(null, null, null, null, null));
            });

        await h.Worker.TryProcessOneAsync(CancellationToken.None);

        await h.Storage.DidNotReceive().PutObjectAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await h.Store.Received(1).MarkFailedAsync(
            job.ClipId, job.AttemptNumber, ClipStatuses.Importing, Arg.Any<CancellationToken>(),
            reason: ClipFailureReasons.SourceTooLong);
        await h.Store.DidNotReceive().AdvanceImportAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<long>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
