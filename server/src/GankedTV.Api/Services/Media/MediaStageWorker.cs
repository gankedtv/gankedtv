using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Media;

// Generic worker loop shared by every transcode stage — the upload-time thumbnail and compress
// stages (clips table) and the watch-time JIT stage (clip_stream_jobs table). Each stage leases
// work via its own DB queue (FOR UPDATE SKIP LOCKED), so any number of worker instances —
// including ones on a separate GPU host — can run safely against the same database. Subclasses
// supply how to claim/release/fail a job and the per-job work; all the drain/retry/shutdown
// invariants live here.
public abstract class MediaStageWorker<TJob> : BackgroundService
    where TJob : class
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFfmpegRunner _ffmpeg;
    private readonly IOptionsMonitor<MediaJobOptions> _options;
    private readonly ILogger _logger;

    protected MediaStageWorker(
        IServiceScopeFactory scopeFactory,
        IFfmpegRunner ffmpeg,
        IOptionsMonitor<MediaJobOptions> options,
        ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _ffmpeg = ffmpeg;
        _options = options;
        _logger = logger;
    }

    // Human-readable stage label for logs ("thumbnail" / "compress" / "jit").
    protected abstract string StageName { get; }

    // Whether this instance should run this stage's worker (lets a deployment place the GPU
    // stages on a separate host while the API server runs only the thumbnail stage).
    protected abstract bool IsWorkerEnabled(MediaJobOptions opts);

    // Atomically claim one unit of work, or null when the queue is empty.
    protected abstract Task<TJob?> ClaimAsync(IServiceProvider scope, MediaJobOptions opts, CancellationToken ct);

    // Identity + attempt accessors (logging + the MaxAttempts decision).
    protected abstract Guid ClipIdOf(TJob job);
    protected abstract int AttemptOf(TJob job);

    // Run the stage's work for one claimed job AND record success. Throws on failure; the base
    // loop turns that into a release (retry) or a permanent fail.
    protected abstract Task ProcessAsync(IServiceProvider scope, TJob job, MediaJobOptions opts, CancellationToken ct);

    // Release the lease for retry / mark the job permanently failed. Called with a fresh
    // non-cancellable token so the outcome is recorded even during shutdown.
    protected abstract Task ReleaseAsync(IServiceProvider scope, TJob job, CancellationToken ct);
    protected abstract Task FailAsync(IServiceProvider scope, TJob job, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var snapshot = _options.CurrentValue;
        if (!snapshot.Enabled)
        {
            _logger.LogInformation("Media-job worker disabled by configuration; {Stage} worker exiting.", StageName);
            return;
        }
        if (!IsWorkerEnabled(snapshot))
        {
            _logger.LogInformation("{Stage} worker disabled by configuration; exiting.", StageName);
            return;
        }

        // Probe ffmpeg/ffprobe once at startup so a misconfigured host fails loudly here instead
        // of silently retrying every clip until each lands in 'failed'. Warn-only.
        await ProbeBinariesAsync(snapshot, stoppingToken);

        using var timer = new PeriodicTimer(snapshot.PollInterval);
        _logger.LogInformation(
            "{Stage} worker started (pollInterval={Interval}, lease={Lease}, maxAttempts={Max}).",
            StageName, snapshot.PollInterval, snapshot.LeaseDuration, snapshot.MaxAttempts);

        try
        {
            // Drain greedily on each tick (capped by MaxDrainPerTick) so a backlog isn't
            // drip-fed at PollInterval cadence while still yielding to graceful shutdown.
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
                        "{Stage} drain cap hit ({Cap}); remainder rolls into next tick.", StageName, perTickCap);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutdown — not an error.
        }
    }

    // Virtual so stages with extra binaries (e.g. ImportWorker → yt-dlp) can extend the
    // boot probe without duplicating the loop / lease machinery.
    protected virtual async Task ProbeBinariesAsync(MediaJobOptions opts, CancellationToken ct)
    {
        await ProbeOneAsync(opts.FfmpegPath, ct);
        await ProbeOneAsync(opts.FfprobePath, ct);
    }

    // versionArg defaults to ffmpeg/ffprobe's single-dash "-version"; yt-dlp needs GNU-style
    // "--version" — a single dash makes yt-dlp parse "-version" as bundled short flags and error.
    protected async Task ProbeOneAsync(string executable, CancellationToken ct, string versionArg = "-version")
    {
        try
        {
            var result = await _ffmpeg.RunAsync(executable, new[] { versionArg }, TimeSpan.FromSeconds(5), ct);
            if (result.ExitCode != 0)
            {
                _logger.LogWarning(
                    "Probe of '{Executable}' exited {ExitCode}; {Stage} jobs will fail. Stderr: {Stderr}",
                    executable, result.ExitCode, StageName, result.Stderr);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Generic message: this probe runs for every binary a stage depends on (ffmpeg,
            // ffprobe, yt-dlp, …). Stage-specific remediation lives in the per-stage config
            // (CLAUDE.md "Host requirements" lists the *_PATH envs) — here we just point at
            // the missing executable + which stage needs it.
            _logger.LogWarning(ex,
                "Could not invoke '{Executable}'. The {Stage} worker is enabled but jobs will fail "
                + "until this binary is installed or the configured path is corrected.",
                executable, StageName);
        }
    }

    // Returns true if a job was processed (or attempted) this tick, false when the queue is
    // empty. Internal so unit tests can drive a single iteration deterministically.
    internal async Task<bool> TryProcessOneAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        TJob? job = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;

            job = await ClaimAsync(sp, opts, ct);
            if (job is null)
            {
                return false;
            }

            var attempt = AttemptOf(job);
            _logger.LogInformation(
                "Claimed {Stage} job clip={ClipId} attempt={Attempt}/{Max}",
                StageName, ClipIdOf(job), attempt, opts.MaxAttempts);

            try
            {
                await ProcessAsync(sp, job, opts, ct);
                _logger.LogInformation("{Stage} stage complete clip={ClipId}", StageName, ClipIdOf(job));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Use a fresh non-cancellable token so the outcome is recorded even when shutdown
                // is in flight; the store guards on attempt number so a stale late call can't
                // clobber a row another worker re-claimed.
                if (attempt >= opts.MaxAttempts)
                {
                    _logger.LogError(ex,
                        "{Stage} failed for clip={ClipId} after {Attempt} attempts; marking failed.",
                        StageName, ClipIdOf(job), attempt);
                    await FailAsync(sp, job, CancellationToken.None);
                }
                else
                {
                    _logger.LogWarning(ex,
                        "{Stage} failed for clip={ClipId} on attempt {Attempt}; releasing for retry.",
                        StageName, ClipIdOf(job), attempt);
                    await ReleaseAsync(sp, job, CancellationToken.None);
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
            // Claim-side error: the DB threw or DI couldn't resolve. The claim transaction rolled
            // back, so no row is leased. Log and let the next tick retry.
            _logger.LogError(ex, "{Stage} tick failed before processing (clip={ClipId})",
                StageName, job is null ? (Guid?)null : ClipIdOf(job));
            return false;
        }
    }
}
