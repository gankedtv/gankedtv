using FluentAssertions;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Services.Media.Import;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GankedTV.Api.Tests.Services.Media.Import;

// Locks in the contract YtDlpImportSource has with the underlying yt-dlp binary. The fix for
// the "all nulls" probe bug (yt-dlp's default --dump-single-json output is ~570KB for normal
// YouTube videos, which overflows FfmpegRunner's 256KB capture buffer and gets truncated to
// unparseable garbage) is enforced here: every yt-dlp invocation must use the field-subset
// --print template so the captured JSON stays small.
public class YtDlpImportSourceTests
{
    // Field-subset output template — keep in sync with YtDlpImportSource.CompactInfoTemplate.
    // Hardcoded here on purpose so a future refactor that quietly switches back to
    // --dump-single-json / --print-json fails this regression test instead of the next user
    // who imports a YouTube clip. Thumbnail is included so the upload wizard can show a real
    // preview frame for Medal.tv (where the client-side YouTube fallback doesn't apply).
    private const string ExpectedTemplate = "%(.{title,duration,width,height,thumbnail})j";

    private static (YtDlpImportSource source, IFfmpegRunner runner) Build()
    {
        var runner = Substitute.For<IFfmpegRunner>();
        var monitor = Substitute.For<IOptionsMonitor<MediaJobOptions>>();
        monitor.CurrentValue.Returns(new MediaJobOptions
        {
            Import = new ImportOptions { YtdlpPath = "yt-dlp", FetchTimeout = TimeSpan.FromMinutes(1) },
            ProcessTimeout = TimeSpan.FromSeconds(30),
        });
        var source = new YtDlpImportSource(runner, monitor, NullLogger<YtDlpImportSource>.Instance);
        return (source, runner);
    }

    private static FfmpegResult Ok(string stdout, string stderr = "") => new(0, stdout, stderr);

    [Fact]
    public async Task ProbeAsync_UsesCompactPrintTemplate_NotDumpSingleJson()
    {
        var (source, runner) = Build();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok("""{"title":"x","duration":30}"""));

        await source.ProbeAsync("https://www.youtube.com/watch?v=abc", CancellationToken.None);

        // Capture the argument list passed to yt-dlp and assert it carries the compact
        // --print template. We deliberately also assert that none of the verbose flags are
        // present — passing one of those WOULD make the probe regress to 500KB+ stdout.
        var calls = runner.ReceivedCalls().ToList();
        calls.Should().HaveCount(1);
        var args = (IReadOnlyList<string>)calls[0].GetArguments()[1]!;
        args.Should().Contain("--print");
        args.Should().Contain(ExpectedTemplate);
        args.Should().Contain("--skip-download");
        args.Should().NotContain("--dump-single-json");
        args.Should().NotContain("--dump-json");
        args.Should().NotContain("--print-json");
    }

    [Fact]
    public async Task FetchAsync_UsesCompactPrintTemplate_NotPrintJson()
    {
        var (source, runner) = Build();
        // Probe call returns OK with no metadata; fetch call needs to write a file to the
        // destination path passed in args[-2] of the second call so the post-download branch
        // doesn't trip on "no file produced".
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(
                ci => Ok("{}"),
                ci =>
                {
                    var args = (IReadOnlyList<string>)ci[1]!;
                    var dest = ResolveOutputArg(args);
                    if (dest is not null) File.WriteAllBytes(dest, new byte[16]);
                    return Ok("""{"title":"x","duration":30}""");
                });

        var tempFile = Path.Combine(Path.GetTempPath(), $"ytdlp-test-{Guid.NewGuid():N}.mp4");
        try
        {
            await source.FetchAsync("https://www.youtube.com/watch?v=abc", tempFile,
                new ImportFetchOptions(MaxBytes: 100_000_000, MaxDurationSecs: 120),
                CancellationToken.None);

            // The download (2nd) call is the one we care about — must use the compact template.
            var downloadCall = runner.ReceivedCalls().Skip(1).First();
            var downloadArgs = (IReadOnlyList<string>)downloadCall.GetArguments()[1]!;
            downloadArgs.Should().Contain("--print");
            downloadArgs.Should().Contain(ExpectedTemplate);
            downloadArgs.Should().NotContain("--print-json");
            downloadArgs.Should().NotContain("--dump-single-json");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ProbeAsync_ParsesCompactJson_ReturnsMetadata()
    {
        var (source, runner) = Build();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok("""{"title":"toad sings chandelier","duration":216,"width":1920,"height":1080,"thumbnail":"https://i.ytimg.com/vi/abc/maxresdefault.jpg"}"""));

        var media = await source.ProbeAsync("https://www.youtube.com/watch?v=abc", CancellationToken.None);

        media.Title.Should().Be("toad sings chandelier");
        media.DurationSecs.Should().Be(216);
        media.Width.Should().Be(1920);
        media.Height.Should().Be(1080);
        media.ThumbnailUrl.Should().Be("https://i.ytimg.com/vi/abc/maxresdefault.jpg");
    }

    [Fact]
    public async Task ProbeAsync_MissingThumbnailField_ReturnsNullThumbnail()
    {
        // Some extractors don't expose a thumbnail. The probe must surface a null
        // ThumbnailUrl rather than throw — the wizard falls back to the client-side
        // YouTube-derived poster (or leaves it blank for Medal etc.).
        var (source, runner) = Build();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok("""{"title":"x","duration":10}"""));

        var media = await source.ProbeAsync("https://medal.tv/clips/foo", CancellationToken.None);

        media.ThumbnailUrl.Should().BeNull();
        media.Title.Should().Be("x");
        media.DurationSecs.Should().Be(10);
    }

    [Fact]
    public async Task ProbeAsync_TruncatedJson_ReturnsAllNullsWithoutThrow()
    {
        // Simulates the original bug: FfmpegRunner truncates oversized stdout and appends
        // "...(truncated)". The truncated suffix makes the JSON unparseable. The probe must
        // handle this gracefully (return null fields) rather than throw — otherwise a single
        // verbose extractor response wedges every import.
        var (source, runner) = Build();
        var truncatedJson = """{"title":"toad sings chandelier","duration":216,"formats":[{"format_id":"sb3""" + "...(truncated)";
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok(truncatedJson));

        var media = await source.ProbeAsync("https://www.youtube.com/watch?v=abc", CancellationToken.None);

        // None of the fields are recoverable from a broken JSON — better to return nulls and
        // let the worker's authoritative ffprobe step (post-download) decide than to crash.
        media.Title.Should().BeNull();
        media.DurationSecs.Should().BeNull();
        media.Width.Should().BeNull();
        media.Height.Should().BeNull();
    }

    [Fact]
    public async Task ProbeAsync_NonZeroExit_ThrowsSourceUnavailable()
    {
        var (source, runner) = Build();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new FfmpegResult(ExitCode: 1, Stdout: "", Stderr: "ERROR: Private video"));

        var act = async () => await source.ProbeAsync("https://www.youtube.com/watch?v=abc", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ImportSourceRejectedException>();
        ex.Which.Reason.Should().Be(ClipFailureReasons.SourceUnavailable);
    }

    [Fact]
    public async Task ProbeAsync_BinaryMissing_ThrowsImportFetchException()
    {
        var (source, runner) = Build();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Throws(new System.ComponentModel.Win32Exception(2,
                "An error occurred trying to start process 'yt-dlp'. No such file or directory"));

        var act = async () => await source.ProbeAsync("https://www.youtube.com/watch?v=abc", CancellationToken.None);

        await act.Should().ThrowAsync<ImportFetchException>()
            .WithMessage("*yt-dlp*not available*");
    }

    [Fact]
    public async Task FetchAsync_ProbeDurationOverCap_ThrowsWithActualDuration()
    {
        // Pre-flight rejection: the metadata probe returns a duration above the requested cap.
        // FetchAsync must throw ImportSourceRejectedException carrying the actual duration —
        // no second yt-dlp call for the download should fire.
        var (source, runner) = Build();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok("""{"title":"long","duration":300}"""));

        var tempFile = Path.Combine(Path.GetTempPath(), $"ytdlp-test-{Guid.NewGuid():N}.mp4");
        var act = async () => await source.FetchAsync("https://www.youtube.com/watch?v=abc", tempFile,
            new ImportFetchOptions(MaxBytes: 100_000_000, MaxDurationSecs: 120),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ImportSourceRejectedException>();
        ex.Which.Reason.Should().Be(ClipFailureReasons.SourceTooLong);
        ex.Which.ActualDurationSecs.Should().Be(300);
        // Probe was called; the download call must NOT have fired.
        runner.ReceivedCalls().Should().HaveCount(1);
    }

    [Fact]
    public async Task FetchAsync_NoOutputFile_ThrowsImportFetchException()
    {
        // Pass 1 (probe) returns OK with no duration. Pass 2 (download) exits 0 but doesn't
        // write a file — yt-dlp does this for live streams / members-only content even when
        // it doesn't print an explicit error.
        var (source, runner) = Build();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok("{}"), Ok("{}"));

        var tempFile = Path.Combine(Path.GetTempPath(), $"ytdlp-test-missing-{Guid.NewGuid():N}.mp4");
        var act = async () => await source.FetchAsync("https://www.youtube.com/watch?v=abc", tempFile,
            new ImportFetchOptions(MaxBytes: 100_000_000, MaxDurationSecs: 120),
            CancellationToken.None);

        await act.Should().ThrowAsync<ImportFetchException>().WithMessage("*produced no output*");
    }

    [Fact]
    public async Task ProbeAsync_ParsesFloatDuration_AndSkipsNoiseAndNonStringFields()
    {
        var (source, runner) = Build();
        // A banner line (skipped: doesn't start with '{'), then the JSON. duration is a float
        // (rounded), width is absent (→ null), thumbnail is a number not a string (→ null).
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok("[youtube] extracting\n" + """{"title":"Clip","duration":12.6,"height":1080,"thumbnail":42}"""));

        var media = await source.ProbeAsync("https://www.youtube.com/watch?v=abc", CancellationToken.None);

        media.Title.Should().Be("Clip");
        media.DurationSecs.Should().Be(13); // 12.6 → rounded
        media.Width.Should().BeNull();       // property absent
        media.Height.Should().Be(1080);      // integer number
        media.ThumbnailUrl.Should().BeNull(); // present but not a string
    }

    [Fact]
    public async Task ProbeAsync_SkipsMalformedJsonLine_AndIgnoresNonNumericDuration()
    {
        var (source, runner) = Build();
        // First '{'-line is malformed (JsonException → try next); the second parses. duration is
        // a string (non-number → null), title is absent (→ null), width is a plain integer.
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok("{ not valid json\n" + """{"duration":"NaN","width":640}"""));

        var media = await source.ProbeAsync("https://www.youtube.com/watch?v=abc", CancellationToken.None);

        media.Title.Should().BeNull();
        media.DurationSecs.Should().BeNull(); // string, not a number
        media.Width.Should().Be(640);
    }

    // Helpers ---------------------------------------------------------------------------

    private static string? ResolveOutputArg(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "-o") return args[i + 1];
        }
        return null;
    }
}
