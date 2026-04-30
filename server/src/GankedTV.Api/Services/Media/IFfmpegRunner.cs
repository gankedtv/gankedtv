namespace GankedTV.Api.Services.Media;

public sealed record FfmpegResult(int ExitCode, string Stdout, string Stderr);

public interface IFfmpegRunner
{
    // Spawns the binary at `executable` with the given args. Returns the captured
    // stdout/stderr and exit code; throws TimeoutException if the process exceeds
    // `timeout` (the process is killed in that case).
    Task<FfmpegResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct);
}
