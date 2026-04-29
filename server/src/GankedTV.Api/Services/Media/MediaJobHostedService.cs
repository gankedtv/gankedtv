using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Media;

public sealed class MediaJobHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFfmpegRunner _ffmpeg;
    private readonly IOptionsMonitor<MediaJobOptions> _options;
    private readonly ILogger<MediaJobHostedService> _logger;

    public MediaJobHostedService(
        IServiceScopeFactory scopeFactory,
        IFfmpegRunner ffmpeg,
        IOptionsMonitor<MediaJobOptions> options,
        ILogger<MediaJobHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _ffmpeg = ffmpeg;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var snapshot = _options.CurrentValue;
        if (!snapshot.Enabled)
        {
            _logger.LogInformation("Media-job worker disabled by configuration; exiting.");
            return;
        }

        // Probe the configured ffmpeg/ffprobe binaries once at startup so a
        // misconfigured host fails loudly here instead of silently retrying every
        // upload until each clip lands in 'failed'. Warn-only: don't abort startup
        // because the rest of the API still works for read traffic.
        await ProbeBinariesAsync(snapshot, stoppingToken);

        // PollInterval is captured at startup. Lease/MaxAttempts/MaxDrainPerTick
        // are read fresh each tick so config reloads tune behavior without a restart.
        using var timer = new PeriodicTimer(snapshot.PollInterval);
        _logger.LogInformation(
            "Media-job worker started (pollInterval={Interval}, lease={Lease}, maxAttempts={Max}).",
            snapshot.PollInterval, snapshot.LeaseDuration, snapshot.MaxAttempts);

        try
        {
            // Drain greedily on each tick: while we keep finding work, keep claiming.
            // This keeps a backed-up queue from being drip-fed at PollInterval cadence
            // (e.g. after the worker comes back from a crash). Capped by MaxDrainPerTick
            // so a huge backlog doesn't monopolize the loop and starve graceful shutdown.
            do
            {
                var perTickCap = _options.CurrentValue.MaxDrainPerTick;
                var drained = 0;
                while (!stoppingToken.IsCancellationRequested
                    && drained < perTickCap
                    && await TryProcessOneAsync(stoppingToken))
                {
                    drained++;
                }
                if (drained == perTickCap)
                {
                    _logger.LogInformation(
                        "Media-job drain cap hit ({Cap}); remainder rolls into next tick.", perTickCap);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutdown — not an error.
        }
    }

    private async Task ProbeBinariesAsync(MediaJobOptions opts, CancellationToken ct)
    {
        // Each probe is short-circuited via -version (no inputs, prints + exits).
        // We swallow exceptions so a missing binary is logged but doesn't crash the
        // hosted service host (BackgroundService failures bubble up to the host and
        // can take the whole API down depending on BackgroundServiceExceptionBehavior).
        await ProbeOneAsync(opts.FfmpegPath, ct);
        await ProbeOneAsync(opts.FfprobePath, ct);
    }

    private async Task ProbeOneAsync(string executable, CancellationToken ct)
    {
        try
        {
            var result = await _ffmpeg.RunAsync(
                executable,
                new[] { "-version" },
                TimeSpan.FromSeconds(5),
                ct);
            if (result.ExitCode != 0)
            {
                _logger.LogWarning(
                    "Probe of '{Executable}' exited {ExitCode}; clip thumbnail extraction will fail. Stderr: {Stderr}",
                    executable, result.ExitCode, result.Stderr);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not invoke '{Executable}'. The thumbnail worker is enabled but extraction will fail until this is fixed. "
                + "Install ffmpeg or set MediaJobs:FfmpegPath / MediaJobs:FfprobePath (env: FFMPEG_PATH / FFPROBE_PATH).",
                executable);
        }
    }

    // Returns true if a job was processed (or attempted) this tick, false when the queue
    // is empty. Internal so unit tests can drive a single iteration deterministically.
    internal async Task<bool> TryProcessOneAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        ClaimedMediaJob? job = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IClipMediaJobStore>();
            var thumbnailer = scope.ServiceProvider.GetRequiredService<IThumbnailJobService>();

            job = await store.ClaimNextAsync(opts.LeaseDuration, opts.MaxAttempts, ct);
            if (job is null)
            {
                return false;
            }

            _logger.LogInformation(
                "Claimed media job clip={ClipId} attempt={Attempt}/{Max}",
                job.ClipId, job.AttemptNumber, opts.MaxAttempts);

            try
            {
                var slug = await store.GetGameSlugAsync(job.GameId, ct);
                var result = await thumbnailer.ExtractAsync(job, slug, ct);
                await store.MarkReadyAsync(job.ClipId, result, ct);
                _logger.LogInformation(
                    "Thumbnail ready clip={ClipId} key={Key} duration={Duration}s",
                    job.ClipId, result.ThumbnailKey, result.DurationSecs);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (job.AttemptNumber >= opts.MaxAttempts)
                {
                    _logger.LogError(ex,
                        "Thumbnail extraction failed for clip={ClipId} after {Attempt} attempts; marking failed.",
                        job.ClipId, job.AttemptNumber);
                    // Use a fresh non-cancellable token so the failure is recorded even
                    // when shutdown is in flight; without this a crash on the final attempt
                    // could leave the row leased and stuck.
                    await store.MarkFailedAsync(job.ClipId, CancellationToken.None);
                }
                else
                {
                    _logger.LogWarning(ex,
                        "Thumbnail extraction failed for clip={ClipId} on attempt {Attempt}; releasing for retry.",
                        job.ClipId, job.AttemptNumber);
                    await store.ReleaseLeaseAsync(job.ClipId, CancellationToken.None);
                }
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Errors out here are claim-side: the DB threw or DI couldn't resolve. The
            // claim transaction would have rolled back, so no row is leased. Log and let
            // the next tick retry.
            _logger.LogError(ex, "Media-job tick failed before extraction (clip={ClipId})", job?.ClipId);
            return false;
        }
    }
}
