using System.Globalization;
using System.Text.Json;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Clips;
using GankedTV.Api.Services.Media.Import;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Media;

// Stage 0 (URL-import): claims 'importing' rows, shells out to yt-dlp to pull the source into
// a temp file, uploads the bytes to S3 at the clip's VideoKey, and advances status to
// 'processing'. From there the existing ThumbnailWorker → CompressWorker chain takes over
// unchanged. Inherits MediaStageWorker<ClaimedImportJob> directly (not ClipStageWorker)
// because the import claim returns a richer record (ImportSourceUrl + Title).
public sealed class ImportWorker : MediaStageWorker<ClaimedImportJob>
{
    private readonly ILogger<ImportWorker> _logger;

    public ImportWorker(
        IServiceScopeFactory scopeFactory,
        IFfmpegRunner ffmpeg,
        IOptionsMonitor<MediaJobOptions> options,
        ILogger<ImportWorker> logger)
        : base(scopeFactory, ffmpeg, options, logger)
    {
        _logger = logger;
    }

    protected override string StageName => "import";

    // Two gates: pipeline-level (Import.Enabled — covers operator kill-switch) AND
    // instance-level (Import.WorkerEnabled — covers the GPU-host split). Either off → exit.
    protected override bool IsWorkerEnabled(MediaJobOptions opts) =>
        opts.Import.Enabled && opts.Import.WorkerEnabled;

    // Boot-time probe extends the base ffmpeg/ffprobe checks with yt-dlp so a missing
    // binary surfaces as a single startup warning instead of one failed clip after another
    // with no obvious cause.
    protected override async Task ProbeBinariesAsync(MediaJobOptions opts, CancellationToken ct)
    {
        await base.ProbeBinariesAsync(opts, ct);
        await ProbeOneAsync(opts.Import.YtdlpPath, ct, "--version");
    }

    protected override Guid ClipIdOf(ClaimedImportJob job) => job.ClipId;
    protected override int AttemptOf(ClaimedImportJob job) => job.AttemptNumber;

    protected override Task<ClaimedImportJob?> ClaimAsync(
        IServiceProvider scope,
        MediaJobOptions opts,
        CancellationToken ct) =>
        scope.GetRequiredService<IClipMediaJobStore>()
            .ClaimNextImportAsync(opts.LeaseDuration, opts.MaxAttempts, ct);

    protected override Task ReleaseAsync(IServiceProvider scope, ClaimedImportJob job, CancellationToken ct) =>
        scope.GetRequiredService<IClipMediaJobStore>()
            .ReleaseLeaseAsync(job.ClipId, job.AttemptNumber, ClipStatuses.Importing, ct);

    protected override Task FailAsync(IServiceProvider scope, ClaimedImportJob job, CancellationToken ct) =>
        scope.GetRequiredService<IClipMediaJobStore>()
            // Retries exhausted with a non-rejection error (timeout, transient extractor
            // failure, etc.) — surface as 'fetch_failed'. Pre-flight rejections (too long /
            // unavailable) write their own structured reasons via the catch in ProcessAsync.
            .MarkFailedAsync(job.ClipId, job.AttemptNumber, ClipStatuses.Importing, ct,
                reason: ClipFailureReasons.FetchFailed);

    protected override async Task ProcessAsync(
        IServiceProvider scope,
        ClaimedImportJob job,
        MediaJobOptions opts,
        CancellationToken ct)
    {
        // Defence in depth: a config flip between submit and dequeue could otherwise let an
        // attacker-supplied host sneak past. Same validator the endpoint uses.
        var validator = scope.GetRequiredService<IClipImportUrlValidator>();
        if (!validator.TryParse(job.ImportSourceUrl, out var url, out _))
        {
            throw new InvalidOperationException(
                $"Import URL no longer passes validation for clip={job.ClipId}.");
        }

        var validation = scope.GetRequiredService<IOptions<ClipValidationOptions>>().Value;
        var source = scope.GetRequiredService<IClipImportSource>();
        var storage = scope.GetRequiredService<IObjectStorageService>();
        var bucket = scope.GetRequiredService<IOptionsMonitor<S3Options>>().CurrentValue.ClipsBucket;
        var store = scope.GetRequiredService<IClipMediaJobStore>();

        // Per-attempt token in the temp filename so a re-claimed lease can't collide on the
        // same path. Mirrors the pattern in ThumbnailJobService.
        var tempFile = Path.Combine(
            Path.GetTempPath(),
            $"gankedtv-import-{job.ClipId:N}-{Guid.NewGuid():N}.mp4");

        try
        {
            var fetchOpts = new ImportFetchOptions(
                MaxBytes: validation.MaxUploadSizeBytes,
                MaxDurationSecs: validation.MaxClipDurationSecs);
            ImportedMedia media;
            try
            {
                media = await source.FetchAsync(url, tempFile, fetchOpts, ct);
                // ProcessAsync continues below — moved post-download checks into the inner try
                // so an authoritative ffprobe rejection (duration > cap, regardless of what
                // yt-dlp reported) is handled the same way as a pre-flight rejection: fail-fast,
                // persist the actual duration, no retries.
                await ProcessDownloadedAsync(scope, job, opts, validation, tempFile, media, bucket, storage, store, ct);
                return;
            }
            catch (ImportSourceRejectedException ex)
            {
                // Non-transient: retrying won't help (cap won't change between attempts). Persist
                // the real duration onto the row when the probe reported it so the status endpoint
                // can surface "your clip is X seconds, max Y" instead of a generic message; then
                // mark the row 'failed' with the structured reason. Return early — the base's
                // release/fail logic skips because the row no longer matches 'importing'.
                _logger.LogInformation(
                    "Import for clip={ClipId} rejected: reason={Reason} ({Message})",
                    job.ClipId, ex.Reason, ex.Message);
                // Mark the row 'failed' FIRST so the polling client sees the outcome even if
                // the duration write below trips (the duration update is a UX nice-to-have, not
                // load-bearing). Best-effort try around the duration write keeps a missing
                // DbContext / DB hiccup from masking the actual rejection.
                await store.MarkFailedAsync(job.ClipId, job.AttemptNumber, ClipStatuses.Importing, ct, reason: ex.Reason);
                if (ex.ActualDurationSecs is { } actualDur && actualDur > 0 && actualDur <= short.MaxValue)
                {
                    try
                    {
                        // Status filter mirrors MarkFailedAsync's attempt+status guard: if a
                        // racing worker has already moved the row past 'failed' (unlikely but
                        // possible during shutdown / re-claim windows), we must not clobber
                        // its duration. The row was just flipped to 'failed' above, so the
                        // common path matches.
                        await scope.GetRequiredService<GankedTvDbContext>()
                            .Clips.Where(c => c.Id == job.ClipId && c.Status == ClipStatuses.Failed)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(c => c.DurationSecs, (short)actualDur), ct);
                    }
                    catch (Exception writeEx)
                    {
                        _logger.LogWarning(writeEx,
                            "Could not persist actual duration={Dur}s onto failed clip={ClipId}; the wizard will show generic copy.",
                            actualDur, job.ClipId);
                    }
                }
                return;
            }
        }
        finally
        {
            // Best-effort cleanup — the temp file is replaceable, so a failed delete here is
            // not worth bubbling up over the work that just succeeded.
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            catch { }
        }
    }

    private static async Task ProcessDownloadedAsync(
        IServiceProvider scope,
        ClaimedImportJob job,
        MediaJobOptions opts,
        ClipValidationOptions validation,
        string tempFile,
        ImportedMedia media,
        string bucket,
        IObjectStorageService storage,
        IClipMediaJobStore store,
        CancellationToken ct)
    {
        var info = new FileInfo(tempFile);
        // Belt-and-suspenders: yt-dlp's --max-filesize aborts mid-download when the upstream
        // exposes a known size, but some extractors only know the size after the fact. A
        // post-download check catches the rest before we burn an S3 PUT on an over-cap file.
        if (info.Length > validation.MaxUploadSizeBytes)
        {
            throw new ImportSourceRejectedException(
                reason: ClipFailureReasons.SourceTooLarge,
                message: $"Fetched source is {info.Length} bytes; limit is {validation.MaxUploadSizeBytes}.");
        }
        if (info.Length <= 0)
        {
            throw new ImportFetchException("yt-dlp produced an empty file.");
        }
        // yt-dlp's reported duration is best-effort — some extractors omit it entirely,
        // so we can't rely on the metadata probe alone. ffprobe the actual downloaded
        // file: this is the authoritative duration check that nothing can bypass.
        var ffmpeg = scope.GetRequiredService<IFfmpegRunner>();
        var probedDuration = await ProbeDurationAsync(ffmpeg, tempFile, opts, ct);
        if (probedDuration is { } realDur && realDur > validation.MaxClipDurationSecs)
        {
            throw new ImportSourceRejectedException(
                reason: ClipFailureReasons.SourceTooLong,
                message: $"Source duration {realDur}s exceeds cap {validation.MaxClipDurationSecs}s.",
                actualDurationSecs: realDur);
        }

        await using (var stream = File.OpenRead(tempFile))
        {
            await storage.PutObjectAsync(bucket, job.VideoKey, stream, "video/mp4", ct);
        }

        await store.AdvanceImportAsync(
            job.ClipId,
            job.AttemptNumber,
            info.Length,
            media.Title,
            ClipImportDefaults.PlaceholderTitle,
            ct);
    }

    // Minimal ffprobe wrapper: returns the duration in whole seconds, or null when ffprobe
    // can't determine it (corrupted file, missing ffprobe, etc.). We use the format-level
    // duration which is more reliable than the stream-level field for containers like webm.
    private static async Task<int?> ProbeDurationAsync(
        IFfmpegRunner ffmpeg,
        string filePath,
        MediaJobOptions opts,
        CancellationToken ct)
    {
        var args = new[]
        {
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            filePath,
        };
        try
        {
            var result = await ffmpeg.RunAsync(opts.FfprobePath, args, opts.ProcessTimeout, ct);
            if (result.ExitCode != 0) return null;
            using var doc = JsonDocument.Parse(result.Stdout);
            if (!doc.RootElement.TryGetProperty("format", out var format)) return null;
            if (!format.TryGetProperty("duration", out var prop)) return null;
            // ffprobe emits duration as a string in JSON ("123.456").
            var raw = prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? (int)Math.Ceiling(seconds)
                : null;
        }
        catch
        {
            return null;
        }
    }
}
