using FluentAssertions;
using GankedTV.Api.Services.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GankedTV.Api.Tests.Services.Media;

public class CropDetectServiceTests
{
    private const int FrameW = 3440;
    private const int FrameH = 1440;

    private static readonly MediaJobOptions DefaultOptions = new()
    {
        FfmpegPath = "ffmpeg",
        FfprobePath = "ffprobe",
        CropDetectSamples = 3,
        CropDetectLimit = 24,
        CropDetectTimeout = TimeSpan.FromSeconds(8),
    };

    private static (CropDetectService Svc, IFfmpegRunner Ffmpeg) Build(MediaJobOptions? opts = null)
    {
        var ffmpeg = Substitute.For<IFfmpegRunner>();
        var monitor = Substitute.For<IOptionsMonitor<MediaJobOptions>>();
        monitor.CurrentValue.Returns(opts ?? DefaultOptions);
        return (new CropDetectService(ffmpeg, monitor, NullLogger<CropDetectService>.Instance), ffmpeg);
    }

    // A cropdetect stderr line in the shape ffmpeg 7.x emits. Note x1/x2/y1/y2 are the CONTENT
    // bounds, not the frame's — on a pillarboxed 3440-wide source holding 2560px of picture,
    // real ffmpeg prints x2:2999. The service must never read the frame size from them.
    private static string CropLine(int x, int y, int w, int h) =>
        $"[Parsed_cropdetect_0 @ 0x1] x1:{x} x2:{x + w - 1} y1:{y} y2:{y + h - 1} "
        + $"w:{w} h:{h} x:{x} y:{y} pts:1024 t:0.042000 limit:24.000000 crop={w}:{h}:{x}:{y}";

    private static string ProbeJson(int? width = FrameW, int? height = FrameH)
    {
        var fields = new List<string>();
        if (width is { } w) fields.Add("\"width\":" + w);
        if (height is { } h) fields.Add("\"height\":" + h);
        return "{\"streams\":[{" + string.Join(",", fields) + "}]}";
    }

    private static void StubProbe(IFfmpegRunner ffmpeg, string json, int exitCode = 0) =>
        ffmpeg.RunAsync(Arg.Is("ffprobe"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new FfmpegResult(exitCode, json, ""));

    // ffprobe runs first (frame dimensions), then the cropdetect samples. Stubbed by binary path
    // so the two can never get crossed.
    private static void StubStderr(IFfmpegRunner ffmpeg, params string[] perCallStderr)
    {
        StubProbe(ffmpeg, ProbeJson());
        var i = 0;
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => new FfmpegResult(0, "", perCallStderr[Math.Min(i++, perCallStderr.Length - 1)]));
    }

    // ---- parsing ----

    [Fact]
    public void ParseLastCropLine_ReadsTheContentRectInSourcePixels()
    {
        var rect = CropDetectService.ParseLastCropLine(CropLine(440, 0, 2560, 1440));

        rect.Should().NotBeNull();
        rect!.X.Should().Be(440);
        rect.Y.Should().Be(0);
        rect.Width.Should().Be(2560);
        rect.Height.Should().Be(1440);
    }

    [Fact]
    public void ParseLastCropLine_ParsesRealFfmpegOutputVerbatim()
    {
        // Captured from ffmpeg 7.x against a 3440x1440 source pillarboxing 2560px of picture.
        // x2 reads 2999 — the CONTENT edge — which is exactly why the frame size has to come
        // from ffprobe rather than from this line.
        const string real =
            "[Parsed_cropdetect_0 @ 0xbf702cd80] x1:440 x2:2999 y1:0 y2:1439 w:2560 h:1440 "
            + "x:440 y:0 pts:11 t:0.440000 limit:24.000000 crop=2560:1440:440:0";

        var rect = CropDetectService.ParseLastCropLine(real);

        rect.Should().NotBeNull();
        rect!.X.Should().Be(440);
        rect.Width.Should().Be(2560);
        rect.Height.Should().Be(1440);
    }

    [Fact]
    public void ParseLastCropLine_TakesTheLastLine()
    {
        // reset=0 accumulates, so the final line reflects every frame sampled.
        var stderr = CropLine(0, 0, 3440, 1440) + "\n" + CropLine(440, 0, 2560, 1440);

        CropDetectService.ParseLastCropLine(stderr)!.X.Should().Be(440);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ffmpeg version 7.1\nno cropdetect output here")]
    public void ParseLastCropLine_NoUsableLine_ReturnsNull(string? stderr)
    {
        CropDetectService.ParseLastCropLine(stderr).Should().BeNull();
    }

    // ---- detection ----

    [Fact]
    public async Task DetectAsync_Pillarbox_SuggestsTheInnerRect()
    {
        var (svc, ffmpeg) = Build();
        StubStderr(ffmpeg, CropLine(440, 0, 2560, 1440));

        var result = await svc.DetectAsync("http://signed/v.mp4", 30, CancellationToken.None);

        result.Detected.Should().BeTrue();
        result.Crop.Should().NotBeNull();
        result.Crop!.X.Should().BeApproximately(440d / FrameW, 1e-6);
        result.Crop.Width.Should().BeApproximately(2560d / FrameW, 1e-6);
        result.SourceWidth.Should().Be(FrameW);
        result.SourceHeight.Should().Be(FrameH);
        result.Samples.Should().Be(3);
    }

    [Fact]
    public async Task DetectAsync_NormalizesAgainstTheProbedFrame_NotTheDetectedContent()
    {
        // The regression the probe exists for. Deriving the frame width from cropdetect's x2
        // would give 3000px, so the suggestion would read 0.147 instead of 0.128 — wrong by
        // exactly the width of the bars it is supposed to be measuring.
        var (svc, ffmpeg) = Build();
        StubStderr(ffmpeg, CropLine(440, 0, 2560, 1440));

        var result = await svc.DetectAsync("http://signed/v.mp4", 30, CancellationToken.None);

        result.Crop!.X.Should().BeApproximately(440d / 3440, 1e-6);
        result.Crop.X.Should().NotBeApproximately(440d / 3000, 1e-3);
    }

    [Fact]
    public async Task DetectAsync_DarkSample_CannotNarrowTheSuggestion()
    {
        // The load-bearing property. A fade-to-black sample reports a tiny rect; taking the UNION
        // widens the suggestion back out instead of permanently destroying picture. Too-loose is
        // recoverable by dragging; too-tight is a lossy re-encode of footage that no longer exists.
        var (svc, ffmpeg) = Build();
        StubStderr(
            ffmpeg,
            CropLine(440, 0, 2560, 1440),
            CropLine(1600, 700, 200, 100), // fade-to-black frame
            CropLine(440, 0, 2560, 1440));

        var result = await svc.DetectAsync("http://signed/v.mp4", 30, CancellationToken.None);

        result.Detected.Should().BeTrue();
        result.Crop!.X.Should().BeApproximately(440d / FrameW, 1e-6);
        result.Crop.Width.Should().BeApproximately(2560d / FrameW, 1e-6);
    }

    [Fact]
    public async Task DetectAsync_SamplesDisagree_UnionsToTheWidestBox()
    {
        var (svc, ffmpeg) = Build();
        StubStderr(
            ffmpeg,
            CropLine(500, 0, 2400, 1440),
            CropLine(440, 0, 2560, 1440),
            CropLine(600, 0, 2200, 1440));

        var result = await svc.DetectAsync("http://signed/v.mp4", 30, CancellationToken.None);

        // Union: leftmost left edge (440) through rightmost right edge (440+2560 = 3000).
        result.Crop!.X.Should().BeApproximately(440d / FrameW, 1e-6);
        result.Crop.Width.Should().BeApproximately(2560d / FrameW, 1e-6);
    }

    [Fact]
    public async Task DetectAsync_AlreadyFullFrame_ReportsNoSuggestion()
    {
        // A plain 16:9 upload — ~95% of them. Offering a no-op crop would have the user pay a
        // re-encode to see nothing change.
        var (svc, ffmpeg) = Build();
        StubProbe(ffmpeg, ProbeJson(1920, 1080));
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new FfmpegResult(0, "", CropLine(0, 0, 1920, 1080)));

        var result = await svc.DetectAsync("http://signed/v.mp4", 30, CancellationToken.None);

        result.Detected.Should().BeFalse();
        result.Crop.Should().BeNull();
        // Frame size still reported — the editor uses it even without a suggestion.
        result.SourceWidth.Should().Be(1920);
    }

    [Fact]
    public async Task DetectAsync_AllSamplesBlack_ReportsNoSuggestion()
    {
        // Every sample landed on a dark frame, so the union is still tiny. Below the minimum
        // extent it's far likelier to be a dark scene than a letterbox.
        var (svc, ffmpeg) = Build();
        StubStderr(ffmpeg, CropLine(1700, 700, 40, 40));

        var result = await svc.DetectAsync("http://signed/v.mp4", 30, CancellationToken.None);

        result.Detected.Should().BeFalse();
    }

    [Fact]
    public async Task DetectAsync_SampleLargerThanTheProbedFrame_IsIgnored()
    {
        // A rect that doesn't fit the probed frame means the sample and the probe disagree about
        // what was decoded; unioning it in would corrupt every other sample.
        var (svc, ffmpeg) = Build();
        StubStderr(
            ffmpeg,
            CropLine(440, 0, 2560, 1440),
            CropLine(0, 0, 7680, 4320),
            CropLine(440, 0, 2560, 1440));

        var result = await svc.DetectAsync("http://signed/v.mp4", 30, CancellationToken.None);

        result.Detected.Should().BeTrue();
        result.Crop!.Width.Should().BeApproximately(2560d / FrameW, 1e-6);
        result.Samples.Should().Be(2);
    }

    [Fact]
    public async Task DetectAsync_NoCropdetectOutput_ReportsNoSuggestion()
    {
        var (svc, ffmpeg) = Build();
        StubStderr(ffmpeg, "ffmpeg version 7.1\nConversion failed!");

        var result = await svc.DetectAsync("http://signed/v.mp4", 30, CancellationToken.None);

        result.Detected.Should().BeFalse();
        result.Samples.Should().Be(0);
    }

    [Fact]
    public async Task DetectAsync_ProbeFails_ReportsNoSuggestionWithoutSampling()
    {
        // Without real dimensions there is no way to normalize the rect, so there is no honest
        // suggestion to give — and no point spending the budget on cropdetect passes.
        var (svc, ffmpeg) = Build();
        StubProbe(ffmpeg, "", exitCode: 1);

        var result = await svc.DetectAsync("http://signed/v.mp4", 30, CancellationToken.None);

        result.Detected.Should().BeFalse();
        result.SourceWidth.Should().BeNull();
        await ffmpeg.DidNotReceive().RunAsync(
            Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetectAsync_ProbeMissingDimensions_ReportsNoSuggestion()
    {
        var (svc, ffmpeg) = Build();
        StubProbe(ffmpeg, ProbeJson(width: null, height: null));

        var result = await svc.DetectAsync("http://signed/v.mp4", 30, CancellationToken.None);

        result.Detected.Should().BeFalse();
    }

    [Fact]
    public async Task DetectAsync_ProbeReturnsMalformedJson_ReportsNoSuggestion()
    {
        var (svc, ffmpeg) = Build();
        StubProbe(ffmpeg, "{not json");

        var result = await svc.DetectAsync("http://signed/v.mp4", 30, CancellationToken.None);

        result.Detected.Should().BeFalse();
    }

    [Fact]
    public async Task DetectAsync_FfmpegThrows_DegradesInsteadOfPropagating()
    {
        // The caller is a user sitting in the crop editor; a broken invocation must not 500.
        var (svc, ffmpeg) = Build();
        StubProbe(ffmpeg, ProbeJson());
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ffmpeg not found"));

        var result = await svc.DetectAsync("http://signed/v.mp4", 30, CancellationToken.None);

        result.Detected.Should().BeFalse();
        result.Crop.Should().BeNull();
    }

    [Fact]
    public async Task DetectAsync_SamplesAcrossTheDuration()
    {
        // Both ends of a clip are the most likely place to hit a fade or a title card, so the
        // sample points sit inside them.
        var (svc, ffmpeg) = Build();
        StubProbe(ffmpeg, ProbeJson());
        var offsets = new List<string>();
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var args = call.Arg<IReadOnlyList<string>>().ToList();
                offsets.Add(args[args.IndexOf("-ss") + 1]);
                return new FfmpegResult(0, "", CropLine(440, 0, 2560, 1440));
            });

        await svc.DetectAsync("http://signed/v.mp4", 100, CancellationToken.None);

        offsets.Should().Equal("15.000", "50.000", "85.000");
    }

    [Fact]
    public async Task DetectAsync_UnknownDuration_SamplesFromTheStart()
    {
        // Zero is the only offset guaranteed to decode; seeking past the end would waste the
        // whole budget on samples that produce nothing.
        var (svc, ffmpeg) = Build();
        StubProbe(ffmpeg, ProbeJson());
        var offsets = new List<string>();
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var args = call.Arg<IReadOnlyList<string>>().ToList();
                offsets.Add(args[args.IndexOf("-ss") + 1]);
                return new FfmpegResult(0, "", CropLine(440, 0, 2560, 1440));
            });

        await svc.DetectAsync("http://signed/v.mp4", null, CancellationToken.None);

        offsets.Should().OnlyContain(o => o == "0.000");
    }

    [Fact]
    public async Task DetectAsync_UnknownDuration_TakesOneSampleInsteadOfRepeatingIt()
    {
        // Every offset collapses to 0 without a duration — the state every 'draft' clip is in —
        // so extra passes fork the identical ffmpeg command for the identical rect. Repeating it
        // triples request-path CPU and inflates `samples`, the very field a caller reads to tell
        // a solid result from one lucky frame.
        var (svc, ffmpeg) = Build();
        StubProbe(ffmpeg, ProbeJson());
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new FfmpegResult(0, "", CropLine(440, 0, 2560, 1440)));

        var result = await svc.DetectAsync("http://signed/v.mp4", null, CancellationToken.None);

        result.Detected.Should().BeTrue();
        result.Samples.Should().Be(1);
        await ffmpeg.Received(1).RunAsync(
            Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetectAsync_SampleCountAboveThree_SpreadsThatManyOffsets()
    {
        // MEDIA_CROPDETECT_SAMPLES is documented as a free tunable, so a value above the old
        // hard-coded three has to actually sample that many times rather than be clamped down
        // while `samples` keeps reporting three.
        var (svc, ffmpeg) = Build(new MediaJobOptions
        {
            FfmpegPath = "ffmpeg",
            FfprobePath = "ffprobe",
            CropDetectSamples = 5,
            CropDetectLimit = 24,
            CropDetectTimeout = TimeSpan.FromSeconds(8),
        });
        StubProbe(ffmpeg, ProbeJson());
        var offsets = new List<string>();
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var args = call.Arg<IReadOnlyList<string>>().ToList();
                offsets.Add(args[args.IndexOf("-ss") + 1]);
                return new FfmpegResult(0, "", CropLine(440, 0, 2560, 1440));
            });

        var result = await svc.DetectAsync("http://signed/v.mp4", 100, CancellationToken.None);

        offsets.Should().Equal("15.000", "32.500", "50.000", "67.500", "85.000");
        result.Samples.Should().Be(5);
    }

    [Fact]
    public async Task DetectAsync_RespectsConfiguredSampleCountAndLimit()
    {
        var (svc, ffmpeg) = Build(new MediaJobOptions
        {
            FfmpegPath = "ffmpeg",
            FfprobePath = "ffprobe",
            CropDetectSamples = 1,
            CropDetectLimit = 64,
            CropDetectTimeout = TimeSpan.FromSeconds(8),
        });
        StubProbe(ffmpeg, ProbeJson());
        IReadOnlyList<string>? args = null;
        ffmpeg.RunAsync(Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                args = call.Arg<IReadOnlyList<string>>();
                return new FfmpegResult(0, "", CropLine(440, 0, 2560, 1440));
            });

        var result = await svc.DetectAsync("http://signed/v.mp4", 30, CancellationToken.None);

        result.Samples.Should().Be(1);
        await ffmpeg.Received(1).RunAsync(
            Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        var list = args!.ToList();
        list[list.IndexOf("-vf") + 1].Should().Contain("limit=64");
    }

    [Fact]
    public async Task DetectAsync_AlreadyCancelled_ReportsNoSuggestionWithoutForkingFfmpeg()
    {
        var (svc, ffmpeg) = Build();
        StubProbe(ffmpeg, ProbeJson());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await svc.DetectAsync("http://signed/v.mp4", 30, cts.Token);

        result.Detected.Should().BeFalse();
        await ffmpeg.DidNotReceive().RunAsync(
            Arg.Is("ffmpeg"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }
}
