using FluentAssertions;
using GankedTV.Api.Services.Caching;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GankedTV.Api.Tests.Services.Media;

public class ThumbnailJobServiceTests
{
    private static readonly MediaJobOptions DefaultJobOptions = new()
    {
        FfmpegPath = "ffmpeg",
        FfprobePath = "ffprobe",
        ProcessTimeout = TimeSpan.FromSeconds(30),
        ThumbnailFrameOffset = TimeSpan.FromSeconds(1),
    };

    private static readonly S3Options DefaultS3Options = new()
    {
        ClipsBucket = "clips",
        ThumbnailsBucket = "thumbnails",
    };

    private static (ThumbnailJobService svc, IFfmpegRunner ffmpeg, IObjectStorageService storage)
        Build(MediaJobOptions? options = null)
    {
        var storage = Substitute.For<IObjectStorageService>();
        var ffmpeg = Substitute.For<IFfmpegRunner>();
        var jobOpts = Substitute.For<IOptionsMonitor<MediaJobOptions>>();
        jobOpts.CurrentValue.Returns(options ?? DefaultJobOptions);
        var s3Opts = Substitute.For<IOptionsMonitor<S3Options>>();
        s3Opts.CurrentValue.Returns(DefaultS3Options);
        storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("http://signed/video.mp4");

        var svc = new ThumbnailJobService(storage, ffmpeg, jobOpts, s3Opts, NullLogger<ThumbnailJobService>.Instance);
        return (svc, ffmpeg, storage);
    }

    private static void StubFfprobe(IFfmpegRunner ffmpeg, string json) =>
        ffmpeg.RunAsync(Arg.Is("ffprobe"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new FfmpegResult(0, json, ""));

    private static void StubFfmpegFrame(IFfmpegRunner ffmpeg, byte[] jpegBytes)
    {
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                // Simulate ffmpeg writing the frame to disk: copy our canned bytes to the
                // output path that ThumbnailJobService passed as the last positional arg.
                var args = call.Arg<IReadOnlyList<string>>();
                var outputPath = args[^1];
                File.WriteAllBytes(outputPath, jpegBytes);
                return new FfmpegResult(0, "", "");
            });
    }

    [Fact]
    public async Task ExtractAsync_HappyPath_UploadsToThumbnailsBucketAndReturnsMetadata()
    {
        var (svc, ffmpeg, storage) = Build();
        StubFfprobe(ffmpeg, """
        {
          "streams": [{ "width": 1920, "height": 1080, "duration": "12.345" }],
          "format": { "duration": "12.345" }
        }
        """);
        StubFfmpegFrame(ffmpeg, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 });

        var userId = Guid.NewGuid();
        var clipId = Guid.NewGuid();
        var job = new ClaimedMediaJob(clipId, userId, GameId: 2, VideoKey: $"{userId}/valorant/{clipId}.mp4", SourceHeight: null, AttemptNumber: 1);

        var result = await svc.ExtractAsync(job, "valorant", CancellationToken.None);

        result.ThumbnailKey.Should().Be($"{userId}/valorant/{clipId}.jpg");
        result.Width.Should().Be(1920);
        result.Height.Should().Be(1080);
        result.DurationSecs.Should().Be(12);

        await storage.Received(1).PutObjectAsync(
            "thumbnails",
            $"{userId}/valorant/{clipId}.jpg",
            Arg.Any<Stream>(),
            "image/jpeg",
            SignedUrlCache.CacheControlHeader,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_CapsPosterLongEdge()
    {
        // The configured cap has to reach the ffmpeg args; BuildScaleFilter covers the shape.
        var (svc, ffmpeg, _) = Build(new MediaJobOptions
        {
            FfmpegPath = "ffmpeg",
            FfprobePath = "ffprobe",
            ProcessTimeout = TimeSpan.FromSeconds(30),
            ThumbnailFrameOffset = TimeSpan.FromSeconds(1),
            ThumbnailMaxEdge = 480,
        });
        StubFfprobe(ffmpeg, """{"streams":[{"width":3840,"height":2160,"duration":"5.0"}]}""");
        StubFfmpegFrame(ffmpeg, new byte[] { 0xFF, 0xD8, 0xFF });

        await svc.ExtractAsync(NewJob(), null, CancellationToken.None);

        var args = ffmpeg.ReceivedCalls()
            .Where(c => c.GetArguments()[0] as string == "ffmpeg")
            .Select(c => (IReadOnlyList<string>)c.GetArguments()[1]!)
            .Single();
        args.Should().ContainInOrder("-vf", ThumbnailJobService.BuildScaleFilter(480));
    }

    [Theory]
    [InlineData(1280, "scale=w='min(iw,1280)':h='min(ih,1280)':force_original_aspect_ratio=decrease:force_divisible_by=2")]
    [InlineData(480, "scale=w='min(iw,480)':h='min(ih,480)':force_original_aspect_ratio=decrease:force_divisible_by=2")]
    public void BuildScaleFilter_ClampsBothAxes_SoASmallSourceIsNeverEnlarged(int maxEdge, string expected)
    {
        // `force_original_aspect_ratio=decrease` alone scales a 640x360 source UP into a
        // 1280x1280 box; the per-axis min() is what keeps it at 640x360.
        ThumbnailJobService.BuildScaleFilter(maxEdge).Should().Be(expected);
    }

    [Fact]
    public async Task ExtractAsync_StoresALongCacheControl()
    {
        // What turns a repeat page load into a cache hit.
        var (svc, ffmpeg, storage) = Build();
        StubFfprobe(ffmpeg, """{"streams":[{"width":640,"height":360,"duration":"5.0"}]}""");
        StubFfmpegFrame(ffmpeg, new byte[] { 0xFF, 0xD8, 0xFF });

        await svc.ExtractAsync(NewJob(), null, CancellationToken.None);

        await storage.Received(1).PutObjectAsync(
            "thumbnails",
            Arg.Any<string>(),
            Arg.Any<Stream>(),
            "image/jpeg",
            "private, max-age=900",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_NoGameSlug_KeyOmitsSlugSegment()
    {
        var (svc, ffmpeg, storage) = Build();
        StubFfprobe(ffmpeg, """{"streams":[{"width":640,"height":360,"duration":"5.0"}]}""");
        StubFfmpegFrame(ffmpeg, new byte[] { 0xFF, 0xD8, 0xFF });

        var userId = Guid.NewGuid();
        var clipId = Guid.NewGuid();
        var job = new ClaimedMediaJob(clipId, userId, GameId: null, VideoKey: $"{userId}/{clipId}.mp4", SourceHeight: null, AttemptNumber: 1);

        var result = await svc.ExtractAsync(job, gameSlug: null, CancellationToken.None);

        result.ThumbnailKey.Should().Be($"{userId}/{clipId}.jpg");
    }

    [Fact]
    public async Task ExtractAsync_FfprobeNonZeroExit_Throws()
    {
        var (svc, ffmpeg, _) = Build();
        ffmpeg.RunAsync(Arg.Is("ffprobe"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new FfmpegResult(1, "", "no such input"));

        var act = async () => await svc.ExtractAsync(NewJob(), null, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ffprobe failed*");
    }

    [Fact]
    public async Task ExtractAsync_FfprobeFailureMessage_RedactsPresignedUrl()
    {
        // Stderr that echoes the input URL must not leak the presigned signature into
        // the exception message — that ride-along ends up in logs / upstream envelopes.
        const string leakyStderr =
            "[https @ 0x55] HTTP error 403 Forbidden\n"
            + "https://minio.local/clips/abc.mp4?X-Amz-Signature=DEADBEEF&X-Amz-Date=20260430";
        var (svc, ffmpeg, _) = Build();
        ffmpeg.RunAsync(Arg.Is("ffprobe"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new FfmpegResult(1, "", leakyStderr));

        var act = async () => await svc.ExtractAsync(NewJob(), null, CancellationToken.None);
        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("[redacted-url]");
        thrown.Which.Message.Should().NotContain("X-Amz-Signature");
        thrown.Which.Message.Should().NotContain("minio.local");
    }

    [Fact]
    public async Task ExtractAsync_FfmpegFailureMessage_RedactsPresignedUrl()
    {
        const string leakyStderr =
            "Error opening input: https://minio.local/clips/x.mp4?X-Amz-Signature=CAFEBABE";
        var (svc, ffmpeg, _) = Build();
        StubFfprobe(ffmpeg, """{"streams":[{"width":1,"height":1,"duration":"5.0"}]}""");
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new FfmpegResult(1, "", leakyStderr));

        var act = async () => await svc.ExtractAsync(NewJob(), null, CancellationToken.None);
        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("[redacted-url]");
        thrown.Which.Message.Should().NotContain("X-Amz-Signature");
    }

    [Fact]
    public async Task ExtractAsync_FfprobeMalformedJson_Throws()
    {
        var (svc, ffmpeg, _) = Build();
        StubFfprobe(ffmpeg, "{ this is not json");

        var act = async () => await svc.ExtractAsync(NewJob(), null, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*malformed JSON*");
    }

    [Fact]
    public async Task ExtractAsync_FfmpegNonZeroExit_Throws()
    {
        var (svc, ffmpeg, _) = Build();
        StubFfprobe(ffmpeg, """{"streams":[{"width":1,"height":1,"duration":"1.0"}]}""");
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new FfmpegResult(1, "", "decode error"));

        var act = async () => await svc.ExtractAsync(NewJob(), null, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*frame extraction failed*");
    }

    [Fact]
    public async Task ExtractAsync_FfmpegProducesEmptyFile_Throws()
    {
        var (svc, ffmpeg, _) = Build();
        StubFfprobe(ffmpeg, """{"streams":[{"width":1,"height":1,"duration":"1.0"}]}""");
        StubFfmpegFrame(ffmpeg, Array.Empty<byte>());

        var act = async () => await svc.ExtractAsync(NewJob(), null, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*frame extraction failed*");
    }

    [Fact]
    public async Task ExtractAsync_DurationShorterThanOffset_SeeksToZero()
    {
        // For a 0.5s clip we should hit -ss 0, not -ss 1 (which would be past EOF).
        var (svc, ffmpeg, _) = Build();
        StubFfprobe(ffmpeg, """{"streams":[{"width":1,"height":1,"duration":"0.5"}]}""");
        IReadOnlyList<string>? capturedArgs = null;
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedArgs = call.Arg<IReadOnlyList<string>>();
                File.WriteAllBytes(capturedArgs[^1], new byte[] { 0xFF });
                return new FfmpegResult(0, "", "");
            });

        await svc.ExtractAsync(NewJob(), null, CancellationToken.None);

        capturedArgs.Should().NotBeNull();
        var ssIdx = capturedArgs!.ToList().IndexOf("-ss");
        ssIdx.Should().BeGreaterThanOrEqualTo(0);
        capturedArgs[ssIdx + 1].Should().Be("0.000");
    }

    [Fact]
    public async Task ExtractAsync_DurationEqualsOffset_SeeksToZero()
    {
        // Boundary case: duration == ThumbnailFrameOffset. The <= comparison should
        // prefer the safe -ss 0 path so we don't seek to exactly the EOF frame, which
        // some demuxers handle inconsistently.
        var (svc, ffmpeg, _) = Build();
        StubFfprobe(ffmpeg, """{"streams":[{"width":1,"height":1,"duration":"1.0"}]}""");
        IReadOnlyList<string>? capturedArgs = null;
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedArgs = call.Arg<IReadOnlyList<string>>();
                File.WriteAllBytes(capturedArgs[^1], new byte[] { 0xFF });
                return new FfmpegResult(0, "", "");
            });

        await svc.ExtractAsync(NewJob(), null, CancellationToken.None);

        capturedArgs.Should().NotBeNull();
        var ssIdx = capturedArgs!.ToList().IndexOf("-ss");
        ssIdx.Should().BeGreaterThanOrEqualTo(0);
        capturedArgs[ssIdx + 1].Should().Be("0.000");
    }

    [Fact]
    public async Task ExtractAsync_DurationFromFormatBlock_IsUsed()
    {
        // Some containers report duration only at the format level — exercise that fallback.
        var (svc, ffmpeg, _) = Build();
        StubFfprobe(ffmpeg, """
        {
          "streams": [{ "width": 100, "height": 50 }],
          "format": { "duration": "8.7" }
        }
        """);
        StubFfmpegFrame(ffmpeg, new byte[] { 1, 2, 3 });

        var result = await svc.ExtractAsync(NewJob(), null, CancellationToken.None);

        result.DurationSecs.Should().Be(9);
    }

    [Fact]
    public async Task ExtractAsync_MissingDimensions_LeavesNulls()
    {
        var (svc, ffmpeg, _) = Build();
        StubFfprobe(ffmpeg, """{"streams":[]}""");
        StubFfmpegFrame(ffmpeg, new byte[] { 1 });

        var result = await svc.ExtractAsync(NewJob(), null, CancellationToken.None);

        result.Width.Should().BeNull();
        result.Height.Should().BeNull();
        result.DurationSecs.Should().BeNull();
    }

    [Fact]
    public async Task ExtractAsync_DimensionsExceedShortMax_AreClampedToShortMax()
    {
        // Dimensions persist as smallint on the clips table; defend the cast even though
        // 8K videos at 7680x4320 are still well inside int16. Synthetic input forces the path.
        var (svc, ffmpeg, _) = Build();
        StubFfprobe(ffmpeg, """{"streams":[{"width":99999,"height":99999,"duration":"1.0"}]}""");
        StubFfmpegFrame(ffmpeg, new byte[] { 1 });

        var result = await svc.ExtractAsync(NewJob(), null, CancellationToken.None);

        result.Width.Should().Be(short.MaxValue);
        result.Height.Should().Be(short.MaxValue);
    }

    [Fact]
    public async Task ExtractAsync_WithTrim_SeeksInsideRangeAndReturnsTrimmedDuration()
    {
        var (svc, ffmpeg, _) = Build();
        StubFfprobe(ffmpeg, """{"streams":[{"width":1920,"height":1080,"duration":"30.0"}]}""");
        IReadOnlyList<string>? capturedArgs = null;
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedArgs = call.Arg<IReadOnlyList<string>>();
                File.WriteAllBytes(capturedArgs[^1], new byte[] { 0xFF });
                return new FfmpegResult(0, "", "");
            });

        var job = NewJob() with { TrimStartSecs = 10, TrimEndSecs = 18 };
        var result = await svc.ExtractAsync(job, null, CancellationToken.None);

        // Poster comes from inside the kept range: trim start + the 1s frame offset.
        capturedArgs.Should().NotBeNull();
        var ssIdx = capturedArgs!.ToList().IndexOf("-ss");
        capturedArgs![ssIdx + 1].Should().Be("11.000");
        result.DurationSecs.Should().Be(8);
        result.TrimStartSecs.Should().Be(10);
        result.TrimEndSecs.Should().Be(18);
    }

    [Fact]
    public async Task ExtractAsync_TrimEndPastDuration_IsClampedToSource()
    {
        var (svc, ffmpeg, _) = Build();
        StubFfprobe(ffmpeg, """{"streams":[{"width":1,"height":1,"duration":"10.0"}]}""");
        StubFfmpegFrame(ffmpeg, new byte[] { 1 });

        var job = NewJob() with { TrimStartSecs = 4, TrimEndSecs = 99 };
        var result = await svc.ExtractAsync(job, null, CancellationToken.None);

        result.TrimStartSecs.Should().Be(4);
        result.TrimEndSecs.Should().Be(10);
        result.DurationSecs.Should().Be(6);
    }

    [Theory]
    [InlineData(null, null, 10.0)] // no trim requested
    [InlineData(0.0, 0.1, 10.0)] // span under the minimum
    [InlineData(0.0, 10.0, 10.0)] // whole-clip range collapses to no trim
    [InlineData(0.04, 9.96, 10.0)] // effectively whole clip
    public void SanitizeTrim_DegenerateRanges_ReturnNull(double? start, double? end, double? dur)
    {
        ThumbnailJobService.SanitizeTrim(start, end, dur).Should().BeNull();
    }

    [Fact]
    public void SanitizeTrim_StartPastEnd_ClampsToTailCut()
    {
        // End clamps to the 10s source, start follows it down to keep the minimum span.
        var trim = ThumbnailJobService.SanitizeTrim(50, 60, 10.0);
        trim.Should().NotBeNull();
        trim!.Value.End.Should().Be(10.0);
        trim.Value.Start.Should().BeApproximately(9.8, 0.001);
    }

    [Fact]
    public async Task ExtractAsync_TrimWithUnknownDuration_Throws()
    {
        // An unverifiable cut must fail the stage — encoding it could publish garbage,
        // dropping it would publish footage the user cut away.
        var (svc, ffmpeg, _) = Build();
        StubFfprobe(ffmpeg, """{"streams":[{"width":1,"height":1}]}""");
        StubFfmpegFrame(ffmpeg, new byte[] { 1 });

        var job = NewJob() with { TrimStartSecs = 3, TrimEndSecs = 7 };
        var act = async () => await svc.ExtractAsync(job, null, CancellationToken.None);

        await act.Should().ThrowAsync<TrimUnverifiableException>().WithMessage("*trim*duration*");
    }

    [Fact]
    public void SanitizeTrim_UnknownDuration_KeepsRequestedRange()
    {
        // Dropping the trim would publish footage the user cut; keep it and let a bad
        // range fail the encode instead.
        ThumbnailJobService.SanitizeTrim(3, 7, null).Should().Be((3.0, 7.0));
    }

    private static ClaimedMediaJob NewJob() =>
        new(Guid.NewGuid(), Guid.NewGuid(), GameId: null, VideoKey: "k.mp4", SourceHeight: null, AttemptNumber: 1);

    [Fact]
    public async Task ExtractAsync_CropAndScale_ShareOneFilterSlot_CropFirst()
    {
        // ffmpeg honours only the LAST -vf, so emitting two silently drops one — a poster that
        // kept the pillarbox bars while the video lost them. Crop leads so the edge cap measures
        // the kept region.
        var (svc, ffmpeg, _) = Build();
        StubFfprobe(ffmpeg, """{"streams":[{"width":3440,"height":1440,"duration":"5.0"}]}""");
        StubFfmpegFrame(ffmpeg, new byte[] { 0xFF, 0xD8, 0xFF });

        await svc.ExtractAsync(JobWithCrop(new CropRect(0.1279, 0, 0.7442, 1)), null, CancellationToken.None);

        var args = ffmpeg.ReceivedCalls()
            .Where(c => c.GetArguments()[0] as string == "ffmpeg")
            .Select(c => (IReadOnlyList<string>)c.GetArguments()[1]!)
            .Single();

        args.Count(a => a == "-vf").Should().Be(1, "a second -vf would silently discard the first");
        var filter = args[args.ToList().IndexOf("-vf") + 1];
        filter.Should().StartWith("crop=", "crop must run before the edge cap");
        filter.Should().Contain("," + ThumbnailJobService.BuildScaleFilter(1280));
    }

    // The single composed -vf value. The poster always carries the edge cap, so crop assertions
    // look inside the filter chain rather than at the presence of the flag.
    private static string VfFilter(IReadOnlyList<string> args)
    {
        var i = args.ToList().IndexOf("-vf");
        return i < 0 ? string.Empty : args[i + 1];
    }

    // ---- crop ----

    private static ClaimedMediaJob JobWithCrop(CropRect crop) =>
        NewJob() with { Crop = crop };

    [Fact]
    public void SanitizeCrop_NoCrop_ReturnsNull()
    {
        ThumbnailJobService.SanitizeCrop(null, 1920, 1080).Should().BeNull();
    }

    [Theory]
    [InlineData(null, 1080)]
    [InlineData(1920, null)]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    public void SanitizeCrop_UnknownOrDegenerateSourceDims_DropsTheCrop(int? width, int? height)
    {
        // Divergence from trim: a crop we can't validate is dropped, not failed. Leaving the
        // black bars in is recoverable post-publish; publishing footage the user cut away isn't.
        ThumbnailJobService.SanitizeCrop(new CropRect(0.1, 0, 0.8, 1), width, height).Should().BeNull();
    }

    [Fact]
    public void SanitizeCrop_UltrawidePillarbox_SnapsToTheExpectedPixelRect()
    {
        // The issue's worked example: 3440x1440 ultrawide holding a 2560-wide 16:9 game.
        var crop = ThumbnailJobService.SanitizeCrop(new CropRect(0.1279, 0, 0.7442, 1), 3440, 1440);

        crop.Should().NotBeNull();
        crop!.Width.Should().Be(2560);
        crop.Height.Should().Be(1440);
        (crop.Rect.X * 3440).Should().BeApproximately(440, 2);
        (crop.Rect.Width * 3440).Should().BeApproximately(2560, 2);
        crop.Rect.Y.Should().Be(0);
        crop.Rect.Height.Should().Be(1);
    }

    [Theory]
    [InlineData(442)]
    [InlineData(230)]
    [InlineData(2560)]
    public void SanitizeCrop_ReportsTheExactPixelsItSnappedTo(int widthPx)
    {
        // The pixel dimensions must come from the sanitizer's own arithmetic, not from
        // re-deriving them out of the fractions it returns: (442/3440)*3440 evaluates to
        // 441.99999999999994, which floors to a 440px frame the encoder never produces.
        // 60 of the 1720 even widths on a 3440px frame land on that edge.
        // +1px on the way in so the sanitizer's own floor-to-even lands exactly on widthPx —
        // this test is about what it REPORTS having snapped to, not about the input rounding.
        var crop = ThumbnailJobService.SanitizeCrop(
            new CropRect(0, 0, (widthPx + 1) / 3440d, 0.5), 3440, 1440);

        crop!.Width.Should().Be(widthPx);
        crop.Height.Should().Be(720);
    }

    [Fact]
    public void SanitizeCrop_SnapsOffsetsAndSizesToEvenPixels()
    {
        // yuv420p needs even dimensions AND even offsets; the persisted rect must match what
        // the ffmpeg filter will actually cut, or the stored width/height lie about the master.
        var crop = ThumbnailJobService.SanitizeCrop(new CropRect(0.1, 0.1, 0.5, 0.5), 1921, 1081);

        crop.Should().NotBeNull();
        ((int)Math.Round(crop!.Rect.X * 1921) % 2).Should().Be(0);
        ((int)Math.Round(crop.Rect.Y * 1081) % 2).Should().Be(0);
        (crop.Width % 2).Should().Be(0);
        (crop.Height % 2).Should().Be(0);
    }

    [Fact]
    public void SanitizeCrop_WholeFrame_NormalizesToNull()
    {
        // A no-op crop filter would only cost clarity in the args and a pointless log line.
        ThumbnailJobService.SanitizeCrop(new CropRect(0, 0, 1, 1), 1920, 1080).Should().BeNull();
    }

    [Fact]
    public void SanitizeCrop_OverhangingRect_ShrinksInsteadOfSliding()
    {
        // The dragged origin is deliberate; the overhang isn't. Shrinking keeps the rect anchored
        // where the user put it rather than silently moving the picture they framed.
        var crop = ThumbnailJobService.SanitizeCrop(new CropRect(0.8, 0.8, 0.5, 0.5), 1920, 1080);

        crop.Should().NotBeNull();
        crop!.Rect.X.Should().BeApproximately(0.8, 0.002);
        (crop.Rect.X + crop.Rect.Width).Should().BeLessThanOrEqualTo(1.0001);
        (crop.Rect.Y + crop.Rect.Height).Should().BeLessThanOrEqualTo(1.0001);
    }

    [Fact]
    public void SanitizeCrop_BelowMinimumExtent_DropsTheCrop()
    {
        ThumbnailJobService.SanitizeCrop(new CropRect(0, 0, 0.01, 1), 1920, 1080).Should().BeNull();
        ThumbnailJobService.SanitizeCrop(new CropRect(0, 0, 1, 0.01), 1920, 1080).Should().BeNull();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void SanitizeCrop_NonFinite_DropsTheCrop(double bad)
    {
        ThumbnailJobService.SanitizeCrop(new CropRect(bad, 0, 0.5, 0.5), 1920, 1080).Should().BeNull();
    }

    [Fact]
    public void SanitizeCrop_IsIdempotent()
    {
        // The row is re-read on a requeue, so a second pass over an already-snapped rect must
        // not creep the crop inward one even-pixel step per retry.
        var once = ThumbnailJobService.SanitizeCrop(new CropRect(0.1279, 0, 0.7442, 1), 3440, 1440);
        var twice = ThumbnailJobService.SanitizeCrop(once!.Rect, 3440, 1440);

        twice.Should().Be(once);
    }

    [Fact]
    public async Task ExtractAsync_WithCrop_PostersThroughTheSameFilterAndReportsPostCropDims()
    {
        // The feed renders the POSTER, so a poster that keeps the bars while the video loses
        // them would make the feature look broken exactly where most people see it.
        var (svc, ffmpeg, _) = Build();
        StubFfprobe(ffmpeg, """{"streams":[{"width":3440,"height":1440,"duration":"12.0"}]}""");
        IReadOnlyList<string>? args = null;
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                args = call.Arg<IReadOnlyList<string>>();
                File.WriteAllBytes(args[^1], new byte[] { 0xFF });
                return new FfmpegResult(0, "", "");
            });

        var result = await svc.ExtractAsync(
            JobWithCrop(new CropRect(0.1279, 0, 0.7442, 1)), null, CancellationToken.None);

        args.Should().NotBeNull();
        var vf = args!.ToList().IndexOf("-vf");
        vf.Should().BeGreaterThanOrEqualTo(0);
        args[vf + 1].Should().StartWith("crop=");

        // Post-crop dimensions: they drive the player's aspect ratio and the JIT ladder's
        // source cap, both of which only ever see the cropped master.
        result.Width.Should().Be(2560);
        result.Height.Should().Be(1440);
        result.Crop.Should().NotBeNull();
    }

    [Fact]
    public async Task ExtractAsync_NoCrop_OmitsTheFilterEntirely()
    {
        var (svc, ffmpeg, _) = Build();
        StubFfprobe(ffmpeg, """{"streams":[{"width":1920,"height":1080,"duration":"12.0"}]}""");
        IReadOnlyList<string>? args = null;
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                args = call.Arg<IReadOnlyList<string>>();
                File.WriteAllBytes(args[^1], new byte[] { 0xFF });
                return new FfmpegResult(0, "", "");
            });

        var result = await svc.ExtractAsync(NewJob(), null, CancellationToken.None);

        VfFilter(args!).Should().NotContain("crop=", "the edge cap still ships; only the crop is absent");
        result.Crop.Should().BeNull();
        result.Width.Should().Be(1920);
        result.Height.Should().Be(1080);
    }

    [Fact]
    public async Task ExtractAsync_CropWithUnprobeableDims_PublishesWithoutCropping()
    {
        // Explicitly NOT a failure — the clip still publishes, just with its bars intact.
        var (svc, ffmpeg, _) = Build();
        StubFfprobe(ffmpeg, """{"streams":[{"duration":"12.0"}],"format":{"duration":"12.0"}}""");
        IReadOnlyList<string>? args = null;
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                args = call.Arg<IReadOnlyList<string>>();
                File.WriteAllBytes(args[^1], new byte[] { 0xFF });
                return new FfmpegResult(0, "", "");
            });

        var result = await svc.ExtractAsync(
            JobWithCrop(new CropRect(0.1, 0, 0.8, 1)), null, CancellationToken.None);

        result.ThumbnailKey.Should().NotBeNullOrEmpty();
        result.Crop.Should().BeNull();
        VfFilter(args!).Should().NotContain("crop=", "the edge cap still ships; only the crop is absent");
    }

    [Fact]
    public async Task ExtractAsync_TranscodeDisabled_IgnoresTheStoredRect()
    {
        // With compression off this stage advances straight to 'ready' over a master that is
        // never re-encoded at all, so there is no second stage to apply the crop. Cropping the
        // poster anyway would publish a cropped still and a cropped aspect ratio over an
        // untouched, still-pillarboxed video.
        var (svc, ffmpeg, _) = Build(new MediaJobOptions
        {
            FfmpegPath = "ffmpeg",
            FfprobePath = "ffprobe",
            ProcessTimeout = TimeSpan.FromSeconds(30),
            ThumbnailFrameOffset = TimeSpan.FromSeconds(1),
            TranscodeEnabled = false,
        });
        StubFfprobe(ffmpeg, """{"streams":[{"width":3440,"height":1440,"duration":"12.0"}]}""");
        IReadOnlyList<string>? args = null;
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                args = call.Arg<IReadOnlyList<string>>();
                File.WriteAllBytes(args[^1], new byte[] { 0xFF });
                return new FfmpegResult(0, "", "");
            });

        var result = await svc.ExtractAsync(
            JobWithCrop(new CropRect(0.1279, 0, 0.7442, 1)), null, CancellationToken.None);

        VfFilter(args!).Should().NotContain("crop=", "the edge cap still ships; only the crop is absent");
        result.Crop.Should().BeNull();
        result.Width.Should().Be(3440);
        result.Height.Should().Be(1440);
    }

    [Fact]
    public async Task ExtractAsync_CropDisabled_IgnoresTheStoredRect()
    {
        // The compress stage rechecks the same flag, so a poster cropped here would advertise a
        // frame the master never gets — and the dimensions written alongside it would shape the
        // player to that phantom frame.
        var (svc, ffmpeg, _) = Build(new MediaJobOptions
        {
            FfmpegPath = "ffmpeg",
            FfprobePath = "ffprobe",
            ProcessTimeout = TimeSpan.FromSeconds(30),
            ThumbnailFrameOffset = TimeSpan.FromSeconds(1),
            CropEnabled = false,
        });
        StubFfprobe(ffmpeg, """{"streams":[{"width":3440,"height":1440,"duration":"12.0"}]}""");
        IReadOnlyList<string>? args = null;
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                args = call.Arg<IReadOnlyList<string>>();
                File.WriteAllBytes(args[^1], new byte[] { 0xFF });
                return new FfmpegResult(0, "", "");
            });

        var result = await svc.ExtractAsync(
            JobWithCrop(new CropRect(0.1279, 0, 0.7442, 1)), null, CancellationToken.None);

        VfFilter(args!).Should().NotContain("crop=", "the edge cap still ships; only the crop is absent");
        result.Crop.Should().BeNull();
        result.Width.Should().Be(3440);
        result.Height.Should().Be(1440);
    }

    [Fact]
    public async Task ExtractAsync_TrimAndCrop_AppliesBothToThePoster()
    {
        // Orthogonal axes: the poster seeks inside the kept range AND crops the frame.
        var (svc, ffmpeg, _) = Build();
        StubFfprobe(ffmpeg, """{"streams":[{"width":3440,"height":1440,"duration":"30.0"}]}""");
        IReadOnlyList<string>? args = null;
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                args = call.Arg<IReadOnlyList<string>>();
                File.WriteAllBytes(args[^1], new byte[] { 0xFF });
                return new FfmpegResult(0, "", "");
            });

        var job = NewJob() with
        {
            TrimStartSecs = 5,
            TrimEndSecs = 15,
            Crop = new CropRect(0.1279, 0, 0.7442, 1),
        };
        var result = await svc.ExtractAsync(job, null, CancellationToken.None);

        var list = args!.ToList();
        list[list.IndexOf("-ss") + 1].Should().Be("6.000"); // trim start + frame offset
        list[list.IndexOf("-vf") + 1].Should().StartWith("crop=");
        result.DurationSecs.Should().Be(10);
        result.Crop.Should().NotBeNull();
    }
}
