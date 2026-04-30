using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace GankedTV.Api.Services.Media;

public sealed class FfmpegRunner : IFfmpegRunner
{
    // Cap captured stdout/stderr to prevent OOM on a chatty ffmpeg run (a multi-hour
    // verbose log can be hundreds of MB). 256 KB per stream is more than enough to
    // keep the tail of any error trace; once we hit it we append a marker so callers
    // can see in logs that the buffer was truncated.
    private const int MaxCapturedChars = 256 * 1024;
    private const string TruncationMarker = "...(truncated)";

    // Strip http(s) URLs from stderr before embedding in a TimeoutException — ffmpeg
    // echoes its input URL on failure, and presigned MinIO/S3 URLs carry signed query
    // params that shouldn't ride along into log lines or upstream error envelopes.
    private static readonly Regex UrlPattern = new(
        @"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<FfmpegResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => AppendCapped(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => AppendCapped(stderr, e.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {executable}");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Compose the caller's cancellation token with our timeout. Either trigger ends
        // the wait; we then force-kill the process tree and bubble up an appropriate
        // exception. WaitForExitAsync (no timer) is what flushes the async readers.
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            TryKillTree(process);
            // After Kill, await exit (no token) so the async stdout/stderr readers
            // finish draining before `using` disposes the Process. Otherwise the
            // Begin*ReadLine pipeline can race with disposal and lose data or throw.
            await WaitForExitWithoutTokenAsync(process);
            throw new TimeoutException(
                $"Process '{executable}' exceeded {timeout.TotalSeconds:F0}s timeout. Stderr: {UrlPattern.Replace(stderr.ToString(), "[redacted-url]")}");
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            await WaitForExitWithoutTokenAsync(process);
            throw;
        }

        return new FfmpegResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void AppendCapped(StringBuilder buffer, string? data)
    {
        if (data is null) return;
        lock (buffer)
        {
            if (buffer.Length >= MaxCapturedChars) return;
            var lineWithBreak = data.Length + Environment.NewLine.Length;
            var remaining = MaxCapturedChars - buffer.Length;
            if (lineWithBreak <= remaining)
            {
                buffer.AppendLine(data);
                return;
            }
            // Append whatever fits, then a single truncation marker so callers know
            // output was cut. Subsequent lines are dropped without further markers.
            var slice = Math.Min(data.Length, Math.Max(0, remaining - TruncationMarker.Length));
            if (slice > 0) buffer.Append(data, 0, slice);
            buffer.Append(TruncationMarker);
        }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort kill — the process may have already exited between the check
            // and the call, or the OS may refuse on a zombie. Either way we have nothing
            // useful to do here.
        }
    }

    private static async Task WaitForExitWithoutTokenAsync(Process process)
    {
        // Best-effort drain. After Kill the OS reaps the process within milliseconds, so
        // an unbounded wait here is fine in practice. Swallow exceptions so a disposed
        // or already-exited Process doesn't replace the original cancellation/timeout.
        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
        }
    }
}
