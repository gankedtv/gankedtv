using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GankedTV.Api.Tests.Services.Media;

public class CompressJobServiceTests
{
    [Theory]
    [InlineData("user/clip.mp4", "user/clip.cmp.mp4")]
    [InlineData("user/game/abc.MP4", "user/game/abc.cmp.mp4")]
    [InlineData("user/clip", "user/clip.cmp.mp4")]
    public void CompressedKeyFor_InsertsCmpSuffix(string original, string expected)
    {
        CompressJobService.CompressedKeyFor(original).Should().Be(expected);
    }

    [Theory]
    // A re-cut compresses the previous master, so the generation replaces the old suffix
    // instead of stacking (…cmp.cmp.cmp…) — and never collides with the key it replaces.
    [InlineData("user/clip.cmp.mp4", 1, "user/clip.cmp1.mp4")]
    [InlineData("user/clip.cmp1.mp4", 2, "user/clip.cmp2.mp4")]
    [InlineData("user/clip.cmp12.mp4", 13, "user/clip.cmp13.mp4")]
    // Not a generation suffix: a dotted stem must survive untouched.
    [InlineData("user/my.clip.mp4", 1, "user/my.clip.cmp1.mp4")]
    [InlineData("user/clip.cmpx.mp4", 1, "user/clip.cmpx.cmp1.mp4")]
    public void CompressedKeyFor_ReplacesPreviousGeneration(string original, int generation, string expected)
    {
        CompressJobService.CompressedKeyFor(original, generation).Should().Be(expected);
    }

    [Fact]
    public void CompressedKeyFor_NeverReturnsItsOwnInput()
    {
        // The encode must never write onto the key it reads from, so the output has to differ
        // from the input for every generation — including a re-run at a generation the master
        // already carries (an admin requeue back into 'transcoding').
        var key = "user/clip.mp4";
        for (var generation = 0; generation < 5; generation++)
        {
            var next = CompressJobService.CompressedKeyFor(key, generation);
            next.Should().NotBe(key);
            // Same clip re-entering compress without the generation advancing.
            CompressJobService.CompressedKeyFor(next, generation).Should().NotBe(next);
            key = next;
        }
    }

    [Fact]
    public void CompressedKeyFor_IsDeterministic()
    {
        // A re-claimed lease re-derives the key mid-encode; a non-deterministic result would
        // orphan the object the previous attempt uploaded.
        CompressJobService.CompressedKeyFor("user/clip.cmp1.mp4", 1)
            .Should().Be(CompressJobService.CompressedKeyFor("user/clip.cmp1.mp4", 1));
    }

    [Fact]
    public void BuildCompressArgs_DownscalesOnlyWhenSourceTallerThanCap()
    {
        var opts = new MediaJobOptions { VideoEncoder = "libx264", MaxHeight = 1080, Crf = 23 };

        var tall = CompressJobService.BuildCompressArgs("in", "out.mp4", sourceHeight: 2160, opts);
        tall.Should().Contain("-vf");
        tall.Should().Contain("scale=-2:1080");

        var short_ = CompressJobService.BuildCompressArgs("in", "out.mp4", sourceHeight: 720, opts);
        short_.Should().NotContain("-vf"); // never upscale; no scaling when already within cap

        var unknown = CompressJobService.BuildCompressArgs("in", "out.mp4", sourceHeight: null, opts);
        unknown.Should().NotContain("-vf");
    }

    [Fact]
    public void BuildCompressArgs_WithTrim_SeeksBeforeInputAndBoundsSpan()
    {
        var opts = new MediaJobOptions { VideoEncoder = "libx264" };

        var args = CompressJobService.BuildCompressArgs(
            "in", "o.mp4", 720, opts, trimStartSecs: 1.5, trimEndSecs: 9.75);

        // -ss must precede -i (input seek); -t carries the span, not the end time.
        args.IndexOf("-ss").Should().BeLessThan(args.IndexOf("-i"));
        args[args.IndexOf("-ss") + 1].Should().Be("1.500");
        args.IndexOf("-t").Should().BeGreaterThan(args.IndexOf("-i"));
        args[args.IndexOf("-t") + 1].Should().Be("8.250");
    }

    [Fact]
    public void BuildCompressArgs_NoOrDegenerateTrim_OmitsSeekArgs()
    {
        var opts = new MediaJobOptions();

        CompressJobService.BuildCompressArgs("in", "o.mp4", 720, opts)
            .Should().NotContain("-ss").And.NotContain("-t");

        CompressJobService.BuildCompressArgs("in", "o.mp4", 720, opts, trimStartSecs: 5, trimEndSecs: 5)
            .Should().NotContain("-ss").And.NotContain("-t");

        CompressJobService.BuildCompressArgs("in", "o.mp4", 720, opts, trimStartSecs: 5, trimEndSecs: null)
            .Should().NotContain("-ss").And.NotContain("-t");
    }

    [Fact]
    public async Task CompressAsync_PassesJobTrimToFfmpeg()
    {
        var storage = Substitute.For<IObjectStorageService>();
        storage.GetPresignedGetUrlForWorker(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("http://minio/clips/orig.mp4?sig=x");

        IReadOnlyList<string>? seenArgs = null;
        var ffmpeg = Substitute.For<IFfmpegRunner>();
        ffmpeg.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                seenArgs = (IReadOnlyList<string>)call[1];
                File.WriteAllText(seenArgs[^1], "compressed-bytes");
                return new FfmpegResult(0, "", "");
            });

        var svc = Build(storage, ffmpeg, new MediaJobOptions());
        var job = new ClaimedMediaJob(Guid.NewGuid(), Guid.NewGuid(), null, "user/clip.mp4", 720, 1,
            TrimStartSecs: 2, TrimEndSecs: 6.5);

        await svc.CompressAsync(job, CancellationToken.None);

        seenArgs.Should().ContainInOrder("-ss", "2.000");
        seenArgs.Should().ContainInOrder("-t", "4.500");
    }

    // ---- crop ----

    [Fact]
    public void BuildCompressArgs_WithCrop_EmitsCropFilter()
    {
        var args = CompressJobService.BuildCompressArgs(
            "in", "o.mp4", sourceHeight: 1440, new MediaJobOptions { MaxHeight = 1080 },
            crop: new CropRect(0.1279, 0, 0.7442, 1));

        var vf = args.IndexOf("-vf");
        vf.Should().BeGreaterThanOrEqualTo(0);
        args[vf + 1].Should().Contain("crop=");
    }

    [Fact]
    public void BuildCompressArgs_CropAndScale_ShareOneVfSlotWithCropFirst()
    {
        // Two -vf flags would silently drop the first. And crop MUST precede scale: capping the
        // height of the source frame instead of the kept region leaves an oversized output.
        var args = CompressJobService.BuildCompressArgs(
            "in", "o.mp4", sourceHeight: 1440, new MediaJobOptions { MaxHeight = 1080 },
            crop: new CropRect(0.1279, 0, 0.7442, 1));

        args.Count(a => a == "-vf").Should().Be(1);
        var chain = args[args.IndexOf("-vf") + 1];
        chain.IndexOf("crop=", StringComparison.Ordinal)
            .Should().BeLessThan(chain.IndexOf("scale=", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildCompressArgs_CropWithinHeightCap_OmitsScale()
    {
        // SourceHeight is already POST-crop by the time compress claims the row, so a crop that
        // left the height under the cap must not pull in a pointless rescale.
        var args = CompressJobService.BuildCompressArgs(
            "in", "o.mp4", sourceHeight: 720, new MediaJobOptions { MaxHeight = 1080 },
            crop: new CropRect(0.1, 0, 0.8, 1));

        var chain = args[args.IndexOf("-vf") + 1];
        chain.Should().StartWith("crop=").And.NotContain("scale=");
    }

    [Fact]
    public void BuildCompressArgs_TrimAndCrop_AreOrthogonal()
    {
        // Trim rides -ss/-t and crop rides -vf, so a combined edit needs no ordering care.
        var args = CompressJobService.BuildCompressArgs(
            "in", "o.mp4", 720, new MediaJobOptions(),
            trimStartSecs: 2, trimEndSecs: 6.5,
            crop: new CropRect(0.1, 0, 0.8, 1));

        args.IndexOf("-ss").Should().BeLessThan(args.IndexOf("-i"));
        args[args.IndexOf("-t") + 1].Should().Be("4.500");
        args[args.IndexOf("-vf") + 1].Should().Contain("crop=");
    }

    [Fact]
    public async Task CompressAsync_PassesJobCropToFfmpeg()
    {
        var (svc, seenArgs) = BuildCapturing(new MediaJobOptions());
        var job = new ClaimedMediaJob(Guid.NewGuid(), Guid.NewGuid(), null, "user/clip.mp4", 720, 1,
            Crop: new CropRect(0.1279, 0, 0.7442, 1));

        await svc.CompressAsync(job, CancellationToken.None);

        seenArgs.Value.Should().NotBeNull();
        seenArgs.Value!.Should().Contain("-vf");
        seenArgs.Value![seenArgs.Value.ToList().IndexOf("-vf") + 1].Should().Contain("crop=");
    }

    [Fact]
    public async Task CompressAsync_CropDisabled_IgnoresTheStoredRect()
    {
        // MEDIA_CROP_ENABLED=false is a kill switch: a rect already sitting on the row from
        // before the flag flipped must not still be applied.
        var (svc, seenArgs) = BuildCapturing(new MediaJobOptions { CropEnabled = false });
        var job = new ClaimedMediaJob(Guid.NewGuid(), Guid.NewGuid(), null, "user/clip.mp4", 720, 1,
            Crop: new CropRect(0.1279, 0, 0.7442, 1));

        await svc.CompressAsync(job, CancellationToken.None);

        seenArgs.Value.Should().NotBeNull();
        seenArgs.Value!.Should().NotContain("-vf");
    }

    // Wires a service whose ffmpeg stub records the args it was handed and writes a non-empty
    // output file so CompressAsync gets past its "did the encode produce anything" check.
    private static (CompressJobService Svc, StrongBox<IReadOnlyList<string>?> Args) BuildCapturing(
        MediaJobOptions opts)
    {
        var storage = Substitute.For<IObjectStorageService>();
        storage.GetPresignedGetUrlForWorker(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("http://minio/clips/orig.mp4?sig=x");

        var box = new StrongBox<IReadOnlyList<string>?>(null);
        var ffmpeg = Substitute.For<IFfmpegRunner>();
        ffmpeg.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var args = (IReadOnlyList<string>)call[1];
                box.Value = args;
                File.WriteAllText(args[^1], "compressed-bytes");
                return new FfmpegResult(0, "", "");
            });

        return (Build(storage, ffmpeg, opts), box);
    }

    [Fact]
    public void BuildCompressArgs_UsesCrfForSoftwareAndCqForNvenc()
    {
        var sw = CompressJobService.BuildCompressArgs("in", "o.mp4", 720, new MediaJobOptions { VideoEncoder = "libsvtav1", Crf = 30 });
        sw.Should().Contain("-crf");
        sw.Should().NotContain("-cq");

        var gpu = CompressJobService.BuildCompressArgs("in", "o.mp4", 720, new MediaJobOptions { VideoEncoder = "av1_nvenc", Crf = 30 });
        gpu.Should().Contain("-cq");
        gpu.Should().NotContain("-crf");
    }

    [Fact]
    public async Task CompressAsync_UploadsMaster_ReturnsKeyCodecAndOriginal()
    {
        var storage = Substitute.For<IObjectStorageService>();
        storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("http://minio/clips/orig.mp4?sig=x");

        var ffmpeg = Substitute.For<IFfmpegRunner>();
        ffmpeg.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var args = (IReadOnlyList<string>)call[1];
                var outPath = args[^1]; // output is the last arg
                File.WriteAllText(outPath, "compressed-bytes");
                return new FfmpegResult(0, "", "");
            });

        string? putBucket = null, putKey = null, putContentType = null;
        storage.PutObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                putBucket = (string)call[0];
                putKey = (string)call[1];
                putContentType = (string)call[3];
                return Task.CompletedTask;
            });

        var svc = Build(storage, ffmpeg, new MediaJobOptions { VideoEncoder = "libx264", VideoCodec = "h264" });
        var job = new ClaimedMediaJob(Guid.NewGuid(), Guid.NewGuid(), null, "user/clip.mp4", SourceHeight: 720, AttemptNumber: 1);

        var result = await svc.CompressAsync(job, CancellationToken.None);

        result.VideoKey.Should().Be("user/clip.cmp.mp4");
        result.VideoCodec.Should().Be("h264");
        result.OriginalKey.Should().Be("user/clip.mp4");
        putBucket.Should().Be("clips");
        putKey.Should().Be("user/clip.cmp.mp4");
        putContentType.Should().Be("video/mp4");
    }

    [Fact]
    public void BuildCompressArgs_EncoderOverride_ReplacesEncoderAndPicksCrf()
    {
        var opts = new MediaJobOptions { VideoEncoder = "av1_nvenc", Crf = 30 };

        var args = CompressJobService.BuildCompressArgs("in", "o.mp4", 720, opts, encoder: "libsvtav1");

        args.Should().Contain("libsvtav1");
        args.Should().NotContain("av1_nvenc");
        args.Should().Contain("-crf"); // software fallback target uses CRF, not the NVENC -cq
        args.Should().NotContain("-cq");
    }

    [Theory]
    [InlineData("av1_nvenc", "libsvtav1")]
    [InlineData("h264_nvenc", "libx264")]
    [InlineData("hevc_nvenc", "libx265")]
    [InlineData("h265_nvenc", "libx265")]
    public void SoftwareEncoderFor_MapsToSameCodecFamily(string hardware, string expected)
    {
        MediaEncoders.SoftwareEncoderFor(hardware).Should().Be(expected);
    }

    [Fact]
    public async Task CompressAsync_HardwareEncoderFails_FallsBackToSoftware_SameCodec()
    {
        var storage = Substitute.For<IObjectStorageService>();
        storage.GetPresignedGetUrlForWorker(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("http://minio/clips/orig.mp4?sig=x");

        var encodersSeen = new List<string>();
        var ffmpeg = Substitute.For<IFfmpegRunner>();
        ffmpeg.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var args = (IReadOnlyList<string>)call[1];
                var encoder = args.SkipWhile(a => a != "-c:v").Skip(1).First();
                encodersSeen.Add(encoder);
                if (encoder.Contains("nvenc")) // hardware encoder won't open → no output written
                    return new FfmpegResult(218, "", "nvenc open failed");
                File.WriteAllText(args[^1], "compressed-bytes");
                return new FfmpegResult(0, "", "");
            });

        var putHappened = false;
        storage.PutObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => { putHappened = true; return Task.CompletedTask; });

        var svc = Build(storage, ffmpeg, new MediaJobOptions { VideoEncoder = "av1_nvenc", VideoCodec = "av1" });
        var job = new ClaimedMediaJob(Guid.NewGuid(), Guid.NewGuid(), null, "user/clip.mp4", SourceHeight: 720, AttemptNumber: 1);

        var result = await svc.CompressAsync(job, CancellationToken.None);

        encodersSeen.Should().Equal("av1_nvenc", "libsvtav1"); // tried GPU first, then software
        result.VideoCodec.Should().Be("av1"); // codec label unchanged by the fallback
        putHappened.Should().BeTrue();
    }

    [Fact]
    public async Task CompressAsync_FallbackDisabled_HardwareFailureThrowsWithoutRetry()
    {
        var (storage, ffmpeg, calls) = FailingFfmpeg();
        var svc = Build(storage, ffmpeg,
            new MediaJobOptions { VideoEncoder = "av1_nvenc", VideoCodec = "av1", HardwareEncoderFallbackEnabled = false });
        var job = new ClaimedMediaJob(Guid.NewGuid(), Guid.NewGuid(), null, "v.mp4", 720, 1);

        var act = async () => await svc.CompressAsync(job, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        calls.Count.Should().Be(1); // no software retry when the toggle is off
    }

    [Fact]
    public async Task CompressAsync_HardwareFailureWithNoTimeBudgetLeft_SkipsFallback()
    {
        // Both encodes share one TranscodeTimeout budget; a zero budget means the hardware attempt
        // already consumed it, so there's no time to fall back — proves the fallback isn't a fresh
        // full-timeout run that could overrun the lease.
        var (storage, ffmpeg, calls) = FailingFfmpeg();
        var svc = Build(storage, ffmpeg,
            new MediaJobOptions { VideoEncoder = "av1_nvenc", VideoCodec = "av1", TranscodeTimeout = TimeSpan.Zero });
        var job = new ClaimedMediaJob(Guid.NewGuid(), Guid.NewGuid(), null, "v.mp4", 720, 1);

        var act = async () => await svc.CompressAsync(job, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        calls.Count.Should().Be(1); // no budget for a software retry
    }

    [Fact]
    public async Task CompressAsync_SoftwareEncoderFails_DoesNotFallBack()
    {
        var (storage, ffmpeg, calls) = FailingFfmpeg();
        var svc = Build(storage, ffmpeg, new MediaJobOptions { VideoEncoder = "libx264", VideoCodec = "h264" });
        var job = new ClaimedMediaJob(Guid.NewGuid(), Guid.NewGuid(), null, "v.mp4", 720, 1);

        var act = async () => await svc.CompressAsync(job, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        calls.Count.Should().Be(1); // already software; nothing better to fall back to
    }

    private static (IObjectStorageService, IFfmpegRunner, List<string>) FailingFfmpeg()
    {
        var storage = Substitute.For<IObjectStorageService>();
        storage.GetPresignedGetUrlForWorker(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("http://x?sig=secret");
        var calls = new List<string>();
        var ffmpeg = Substitute.For<IFfmpegRunner>();
        ffmpeg.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var args = (IReadOnlyList<string>)call[1];
                calls.Add(args.SkipWhile(a => a != "-c:v").Skip(1).First());
                return new FfmpegResult(1, "", "fail");
            });
        return (storage, ffmpeg, calls);
    }

    [Fact]
    public async Task CompressAsync_FfmpegFails_ThrowsWithRedactedUrl()
    {
        var storage = Substitute.For<IObjectStorageService>();
        storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>()).Returns("http://x?sig=secret");
        var ffmpeg = Substitute.For<IFfmpegRunner>();
        ffmpeg.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new FfmpegResult(1, "", "fail http://x?sig=secret"));

        var svc = Build(storage, ffmpeg, new MediaJobOptions());
        var job = new ClaimedMediaJob(Guid.NewGuid(), Guid.NewGuid(), null, "v.mp4", 720, 1);

        var act = async () => await svc.CompressAsync(job, CancellationToken.None);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().NotContain("sig=secret");
    }

    private static CompressJobService Build(IObjectStorageService storage, IFfmpegRunner ffmpeg, MediaJobOptions opts)
    {
        var jobOpts = Substitute.For<IOptionsMonitor<MediaJobOptions>>();
        jobOpts.CurrentValue.Returns(opts);
        var s3 = Substitute.For<IOptionsMonitor<S3Options>>();
        s3.CurrentValue.Returns(new S3Options { ClipsBucket = "clips" });

        return new CompressJobService(storage, ffmpeg, jobOpts, s3, NullLogger<CompressJobService>.Instance);
    }
}
