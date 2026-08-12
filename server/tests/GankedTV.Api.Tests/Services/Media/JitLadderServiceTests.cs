using FluentAssertions;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GankedTV.Api.Tests.Services.Media;

public class JitLadderServiceTests
{
    private static List<HlsRung> DefaultLadder() =>
    [
        new HlsRung { Height = 1080, VideoKbps = 5000, MaxrateKbps = 5350, AudioKbps = 128 },
        new HlsRung { Height = 720, VideoKbps = 2800, MaxrateKbps = 2996, AudioKbps = 128 },
        new HlsRung { Height = 480, VideoKbps = 1400, MaxrateKbps = 1498, AudioKbps = 96 },
    ];

    // --- SelectRungs (source-capping) -------------------------------------------------

    [Fact]
    public void SelectRungs_CapsToSourceHeight_NeverUpscales()
    {
        JitLadderService.SelectRungs(720, DefaultLadder()).Select(r => r.Height).Should().Equal(720, 480);
    }

    [Fact]
    public void SelectRungs_FullLadderWhenSourceTallEnough()
    {
        JitLadderService.SelectRungs(1080, DefaultLadder()).Select(r => r.Height).Should().Equal(1080, 720, 480);
    }

    [Fact]
    public void SelectRungs_UnknownHeight_FallsBackToLowestRung()
    {
        JitLadderService.SelectRungs(null, DefaultLadder()).Select(r => r.Height).Should().Equal(480);
        JitLadderService.SelectRungs(0, DefaultLadder()).Select(r => r.Height).Should().Equal(480);
    }

    [Fact]
    public void SelectRungs_SourceShorterThanEveryRung_EncodesAtNativeHeight()
    {
        var rungs = JitLadderService.SelectRungs(360, DefaultLadder());
        rungs.Should().ContainSingle();
        rungs[0].Height.Should().Be(360);
        rungs[0].VideoKbps.Should().Be(1400);
    }

    // --- BuildFfmpegArgs --------------------------------------------------------------

    [Fact]
    public void BuildFfmpegArgs_HonorsJitEncoderAndEmitsOneVariantPerRung()
    {
        var opts = new MediaJobOptions { JitVideoEncoder = "h264_nvenc", SegmentType = "mpegts" };
        var rungs = JitLadderService.SelectRungs(1080, DefaultLadder());

        var args = JitLadderService.BuildFfmpegArgs("http://input", "/tmp/work", rungs, opts, includeAudio: true);

        args.Should().Contain("h264_nvenc");
        var mapIdx = args.IndexOf("-var_stream_map");
        mapIdx.Should().BeGreaterThan(-1);
        args[mapIdx + 1].Should().Be("v:0,a:0 v:1,a:1 v:2,a:2");
        args.Should().Contain(a => a.EndsWith("stream_%v_%03d.ts"));
        args.Should().NotContain("-hls_fmp4_init_filename");
    }

    [Fact]
    public void BuildFfmpegArgs_SilentSource_OmitsAudioFromMapAndStreamMap()
    {
        var opts = new MediaJobOptions { JitVideoEncoder = "libx264", SegmentType = "mpegts" };
        var rungs = JitLadderService.SelectRungs(720, DefaultLadder());

        var args = JitLadderService.BuildFfmpegArgs("http://input", "/tmp/work", rungs, opts, includeAudio: false);

        // Video-only variants — no audio map, no audio codec, no a:N in the stream map.
        args.Should().NotContain("a:0");
        args.Should().NotContain("-c:a");
        var mapIdx = args.IndexOf("-var_stream_map");
        args[mapIdx + 1].Should().Be("v:0 v:1");
    }

    [Fact]
    public void BuildFfmpegArgs_Fmp4_AddsInitFilenameAndM4sSegments()
    {
        var opts = new MediaJobOptions { JitVideoEncoder = "libx264", SegmentType = "fmp4" };
        var rungs = JitLadderService.SelectRungs(720, DefaultLadder());

        var args = JitLadderService.BuildFfmpegArgs("http://input", "/tmp/work", rungs, opts, includeAudio: true);

        args.Should().Contain("-hls_fmp4_init_filename");
        args.Should().Contain(a => a.EndsWith("stream_%v_%03d.m4s"));
    }

    [Theory]
    [InlineData("master.m3u8", "application/vnd.apple.mpegurl")]
    [InlineData("stream_0_000.ts", "video/mp2t")]
    [InlineData("stream_0_000.m4s", "video/mp4")]
    [InlineData("weird.bin", "application/octet-stream")]
    public void ContentTypeFor_MapsByExtension(string fileName, string expected)
    {
        JitLadderService.ContentTypeFor(fileName).Should().Be(expected);
    }

    [Fact]
    public void BuildCachePrefix_UsesClipId()
    {
        var id = Guid.NewGuid();
        JitLadderService.BuildCachePrefix(id).Should().Be($"{id:N}");
    }

    [Fact]
    public void BuildCachePrefix_ScopesByReCutGeneration()
    {
        // A ladder built from a superseded master must be unreachable after the cut lands, and
        // every generation has to stay under `{clipId:N}/` so the delete-by-prefix purges cover it.
        var id = Guid.NewGuid();
        JitLadderService.BuildCachePrefix(id, 2).Should().Be($"{id:N}/e2");
        JitLadderService.BuildCachePrefix(id, 2).Should().NotBe(JitLadderService.BuildCachePrefix(id, 1));
        JitLadderService.BuildCachePrefix(id, 2).Should().StartWith($"{id:N}/");
    }

    // --- BuildAsync end-to-end (fake ffmpeg writes files) -----------------------------

    [Fact]
    public async Task BuildAsync_UploadsLadderToStreamCache_AndReturnsMasterKey()
    {
        var storage = Substitute.For<IObjectStorageService>();
        storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("http://minio/clips/master.av1.mp4?sig=x");

        var ffmpeg = Substitute.For<IFfmpegRunner>();
        ffmpeg.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                // The audio probe runs first (ffprobe); report an audio stream present.
                if ((string)call[0] == "ffprobe") return new FfmpegResult(0, "0\n", "");
                var args = (IReadOnlyList<string>)call[1];
                var workDir = Path.GetDirectoryName(args.Single(a => a.EndsWith("stream_%v.m3u8")))!;
                File.WriteAllText(Path.Combine(workDir, "master.m3u8"), "#EXTM3U");
                File.WriteAllText(Path.Combine(workDir, "stream_0.m3u8"), "#EXTM3U");
                File.WriteAllText(Path.Combine(workDir, "stream_0_000.ts"), "tsdata");
                return new FfmpegResult(0, "", "");
            });

        var uploaded = new List<(string bucket, string key, string contentType)>();
        storage.PutObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                uploaded.Add(((string)call[0], (string)call[1], (string)call[3]));
                return Task.CompletedTask;
            });

        var svc = Build(storage, ffmpeg);
        var clipId = Guid.NewGuid();
        var job = new ClaimedStreamJob(clipId, "u/clip.cmp.mp4", SourceHeight: 720, AttemptNumber: 1);

        var masterKey = await svc.BuildAsync(job, CancellationToken.None);

        masterKey.Should().Be($"{clipId:N}/master.m3u8");
        uploaded.Should().HaveCount(3);
        uploaded.Should().OnlyContain(u => u.bucket == "stream-cache");
        uploaded.Should().Contain(u => u.key == $"{clipId:N}/master.m3u8" && u.contentType == "application/vnd.apple.mpegurl");
        uploaded.Should().Contain(u => u.key.EndsWith(".ts") && u.contentType == "video/mp2t");
    }

    [Fact]
    public async Task BuildAsync_FfmpegFails_ThrowsWithRedactedUrl()
    {
        var storage = Substitute.For<IObjectStorageService>();
        storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>()).Returns("http://x?sig=secret");
        var ffmpeg = Substitute.For<IFfmpegRunner>();
        ffmpeg.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new FfmpegResult(1, "", "boom http://x?sig=secret"));

        var svc = Build(storage, ffmpeg);
        var job = new ClaimedStreamJob(Guid.NewGuid(), "v.mp4", 720, 1);

        var act = async () => await svc.BuildAsync(job, CancellationToken.None);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().NotContain("sig=secret");
    }

    private static JitLadderService Build(IObjectStorageService storage, IFfmpegRunner ffmpeg, MediaJobOptions? opts = null)
    {
        opts ??= new MediaJobOptions { Ladder = DefaultLadder() };
        var jobOpts = Substitute.For<IOptionsMonitor<MediaJobOptions>>();
        jobOpts.CurrentValue.Returns(opts);
        var s3 = Substitute.For<IOptionsMonitor<S3Options>>();
        s3.CurrentValue.Returns(new S3Options { ClipsBucket = "clips", StreamCacheBucket = "stream-cache" });

        return new JitLadderService(storage, ffmpeg, jobOpts, s3, NullLogger<JitLadderService>.Instance);
    }

    // ffmpeg fake: audio probe reports a stream; a *_nvenc ladder run fails to open (no output),
    // any software encoder writes a minimal ladder. Records the ladder encoders it was asked to use.
    private static IFfmpegRunner FfmpegFailingOnNvenc(List<string> encodersSeen)
    {
        var ffmpeg = Substitute.For<IFfmpegRunner>();
        ffmpeg.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if ((string)call[0] == "ffprobe") return new FfmpegResult(0, "0\n", "");
                var args = (IReadOnlyList<string>)call[1];
                var encoder = args.SkipWhile(a => a != "-c:v:0").Skip(1).First();
                encodersSeen.Add(encoder);
                if (encoder.Contains("nvenc")) return new FfmpegResult(1, "", "nvenc open failed");
                var workDir = Path.GetDirectoryName(args.Single(a => a.EndsWith("stream_%v.m3u8")))!;
                File.WriteAllText(Path.Combine(workDir, "master.m3u8"), "#EXTM3U");
                File.WriteAllText(Path.Combine(workDir, "stream_0.m3u8"), "#EXTM3U");
                File.WriteAllText(Path.Combine(workDir, "stream_0_000.ts"), "tsdata");
                return new FfmpegResult(0, "", "");
            });
        return ffmpeg;
    }

    [Fact]
    public async Task BuildAsync_HardwareEncoderFails_FallsBackToSoftware_AndCaches()
    {
        var storage = Substitute.For<IObjectStorageService>();
        storage.GetPresignedGetUrlForWorker(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("http://minio/clips/master.av1.mp4?sig=x");
        var encodersSeen = new List<string>();
        var ffmpeg = FfmpegFailingOnNvenc(encodersSeen);

        var uploaded = new List<string>();
        storage.PutObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => { uploaded.Add((string)call[1]); return Task.CompletedTask; });

        var svc = Build(storage, ffmpeg,
            new MediaJobOptions { Ladder = DefaultLadder(), JitVideoEncoder = "h264_nvenc" });
        var clipId = Guid.NewGuid();
        var job = new ClaimedStreamJob(clipId, "u/clip.cmp.mp4", SourceHeight: 720, AttemptNumber: 1);

        var masterKey = await svc.BuildAsync(job, CancellationToken.None);

        masterKey.Should().Be($"{clipId:N}/master.m3u8");
        encodersSeen.Should().Equal("h264_nvenc", "libx264"); // GPU first, then software H.264
        uploaded.Should().Contain($"{clipId:N}/master.m3u8");
    }

    [Fact]
    public async Task BuildAsync_FallbackDisabled_HardwareFailureThrowsWithoutRetry()
    {
        var storage = Substitute.For<IObjectStorageService>();
        storage.GetPresignedGetUrlForWorker(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("http://x?sig=secret");
        var encodersSeen = new List<string>();
        var ffmpeg = FfmpegFailingOnNvenc(encodersSeen);

        var svc = Build(storage, ffmpeg,
            new MediaJobOptions { Ladder = DefaultLadder(), JitVideoEncoder = "h264_nvenc", HardwareEncoderFallbackEnabled = false });
        var job = new ClaimedStreamJob(Guid.NewGuid(), "v.mp4", 720, 1);

        var act = async () => await svc.BuildAsync(job, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        encodersSeen.Should().Equal("h264_nvenc"); // no software retry when the toggle is off
    }
}
