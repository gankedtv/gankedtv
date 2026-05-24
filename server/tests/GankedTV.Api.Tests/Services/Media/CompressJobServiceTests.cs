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
