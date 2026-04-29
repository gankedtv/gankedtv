using System.Diagnostics;
using System.Text;

namespace GankedTV.Api.Services.Media;

public sealed class FfmpegRunner : IFfmpegRunner
{
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
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

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
            throw new TimeoutException(
                $"Process '{executable}' exceeded {timeout.TotalSeconds:F0}s timeout. Stderr: {stderr}");
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            throw;
        }

        return new FfmpegResult(process.ExitCode, stdout.ToString(), stderr.ToString());
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
}
