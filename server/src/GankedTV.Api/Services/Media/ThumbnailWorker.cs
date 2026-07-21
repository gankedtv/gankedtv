using GankedTV.Api.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Media;

// Stage 1: claims 'processing' clips, extracts a thumbnail + ffprobe metadata, then advances
// the clip to 'transcoding' (when the compress stage runs next) or straight to 'ready' (when
// compression is disabled pipeline-wide — store the raw upload as-is).
public sealed class ThumbnailWorker : ClipStageWorker
{
    private readonly ILogger<ThumbnailWorker> _logger;

    public ThumbnailWorker(
        IServiceScopeFactory scopeFactory,
        IFfmpegRunner ffmpeg,
        IOptionsMonitor<MediaJobOptions> options,
        ILogger<ThumbnailWorker> logger)
        : base(scopeFactory, ffmpeg, options, logger)
    {
        _logger = logger;
    }

    protected override string ClaimStatus => ClipStatuses.Processing;
    protected override string StageName => "thumbnail";
    protected override string? TerminalFailureReason => ClipFailureReasons.ThumbnailFailed;
    protected override bool IsWorkerEnabled(MediaJobOptions opts) => opts.ThumbnailWorkerEnabled;

    protected override async Task ProcessAsync(
        IServiceProvider scope,
        ClaimedMediaJob job,
        MediaJobOptions opts,
        CancellationToken ct)
    {
        var store = scope.GetRequiredService<IClipMediaJobStore>();
        var thumbnailer = scope.GetRequiredService<IThumbnailJobService>();
        var slug = await store.GetGameSlugAsync(job.GameId, ct);

        FinalizedMediaJob result;
        try
        {
            result = await thumbnailer.ExtractAsync(job, slug, ct);
        }
        catch (TrimUnverifiableException ex)
        {
            // Deterministic rejection — retrying re-probes the same bytes. Fail the clip now
            // (mirrors ImportWorker's ImportSourceRejectedException path); the base loop's
            // release/fail is a no-op afterwards because the row no longer matches 'processing'.
            _logger.LogInformation(
                "Thumbnail stage rejecting clip={ClipId}: {Message}", job.ClipId, ex.Message);
            await store.MarkFailedAsync(
                job.ClipId, job.AttemptNumber, ClipStatuses.Processing, ct,
                reason: ClipFailureReasons.TrimUnverifiable);
            return;
        }

        // When compression is part of the pipeline, hand the clip to the compress stage;
        // otherwise the thumbnail is the last step and the clip is ready (stores the upload).
        var toStatus = opts.TranscodeEnabled ? ClipStatuses.Transcoding : ClipStatuses.Ready;
        await store.AdvanceThumbnailAsync(job.ClipId, job.AttemptNumber, result, toStatus, ct);

        // Only the thumbnail-is-final path makes the clip feed-visible; when it hands off to the
        // compress stage, CompressWorker invalidates once it reaches 'ready'.
        if (toStatus == ClipStatuses.Ready)
        {
            await InvalidateFeedsBestEffortAsync(scope, ct);
        }
    }
}
