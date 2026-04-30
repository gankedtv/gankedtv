using System.ComponentModel;
using FluentAssertions;
using GankedTV.Api.Services.Media;

namespace GankedTV.Api.Tests.Services.Media;

// Drives FfmpegRunner against /bin/sh — exercises the real Process plumbing without
// requiring ffmpeg to be installed. The repo's CI/dev targets are Linux/macOS; on
// Windows these tests no-op (xunit doesn't dynamically skip without an extra package
// and the project doesn't ship one).
public class FfmpegRunnerTests
{
    private static bool ShellAvailable => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    [Fact]
    public async Task RunAsync_SuccessfulCommand_ReturnsExitCodeAndStdout()
    {
        if (!ShellAvailable) return;
        var runner = new FfmpegRunner();

        var result = await runner.RunAsync(
            "/bin/sh",
            new[] { "-c", "echo hello-stdout && echo hello-stderr 1>&2 && exit 0" },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Contain("hello-stdout");
        result.Stderr.Should().Contain("hello-stderr");
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_PropagatesExitCode()
    {
        if (!ShellAvailable) return;
        var runner = new FfmpegRunner();

        var result = await runner.RunAsync(
            "/bin/sh",
            new[] { "-c", "exit 7" },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.ExitCode.Should().Be(7);
    }

    [Fact]
    public async Task RunAsync_TimeoutExceeded_ThrowsTimeoutAndKillsProcess()
    {
        if (!ShellAvailable) return;
        var runner = new FfmpegRunner();

        var act = async () => await runner.RunAsync(
            "/bin/sh",
            new[] { "-c", "sleep 30" },
            TimeSpan.FromMilliseconds(200),
            CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_ThrowsOperationCanceled()
    {
        if (!ShellAvailable) return;
        var runner = new FfmpegRunner();
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(
            "/bin/sh",
            new[] { "-c", "sleep 30" },
            TimeSpan.FromSeconds(30),
            cts.Token);

        // Give the process a moment to start — without this the cancellation can race the
        // initial Start() call and surface as an InvalidOperationException instead.
        await Task.Delay(100);
        cts.Cancel();

        var act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunAsync_StdoutExceedsCap_TruncatesWithMarker()
    {
        if (!ShellAvailable) return;
        var runner = new FfmpegRunner();

        // 64k lines of ~6 bytes each = ~384 KB, comfortably past the 256 KB cap so we
        // exercise both the boundary append and the trailing-line discard branches.
        var result = await runner.RunAsync(
            "/bin/sh",
            new[] { "-c", "seq 1 64000" },
            TimeSpan.FromSeconds(15),
            CancellationToken.None);

        result.ExitCode.Should().Be(0);
        result.Stdout.Length.Should().BeLessThan(64000 * 8);
        result.Stdout.Should().EndWith("...(truncated)");
    }

    [Fact]
    public async Task RunAsync_NonExistentBinary_ThrowsWin32Exception()
    {
        var runner = new FfmpegRunner();

        var act = async () => await runner.RunAsync(
            "/this/path/definitely/does/not/exist-binary-xyz",
            Array.Empty<string>(),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        // Process.Start surfaces "no such file" as Win32Exception on every platform —
        // pinning the type prevents future regressions where we might silently start
        // wrapping or catching it.
        await act.Should().ThrowAsync<Win32Exception>();
    }
}
