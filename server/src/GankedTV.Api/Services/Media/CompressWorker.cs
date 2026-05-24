using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Media;

// Stage 2: claims 'transcoding' clips, re-encodes the upload into one efficient master, repoints
// the clip at it, and advances to 'ready'. The original upload is deleted only AFTER the DB has
// been repointed, so a crash never leaves the clip pointing at a deleted object. This is the
// GPU-heavy stage; in production it runs on the GPU box (TranscodeWorkerEnabled).
public sealed class CompressWorker : ClipStageWorker
{
    private readonly ILogger<CompressWorker> _logger;

    public CompressWorker(
        IServiceScopeFactory scopeFactory,
        IFfmpegRunner ffmpeg,
        IOptionsMonitor<MediaJobOptions> options,
        ILogger<CompressWorker> logger)
        : base(scopeFactory, ffmpeg, options, logger)
    {
        _logger = logger;
    }

    protected override string ClaimStatus => ClipStatuses.Transcoding;
    protected override string StageName => "compress";
    protected override bool IsWorkerEnabled(MediaJobOptions opts) => opts.TranscodeWorkerEnabled;

    protected override async Task ProcessAsync(
        IServiceProvider scope,
        ClaimedMediaJob job,
        MediaJobOptions opts,
        CancellationToken ct)
    {
        var store = scope.GetRequiredService<IClipMediaJobStore>();
        var compressor = scope.GetRequiredService<ICompressJobService>();

        var result = await compressor.CompressAsync(job, ct);
        await store.CompleteCompressionAsync(job.ClipId, job.AttemptNumber, result.VideoKey, result.VideoCodec, ct);

        // The clip is now 'ready' (feed-visible) — drop cached feed pages so it appears promptly.
        await InvalidateFeedsBestEffortAsync(scope, ct);

        // Delete the original only now that the row points at the compressed master. The clip
        // is already 'ready', so a delete failure must NOT bubble up: that would log a bogus
        // "compress failed" and trigger a pointless retry on a row that's no longer claimable.
        // A failed delete just leaves an orphan original (disk waste, not corruption).
        if (!string.Equals(result.OriginalKey, result.VideoKey, StringComparison.Ordinal))
        {
            var storage = scope.GetRequiredService<IObjectStorageService>();
            var clips = scope.GetRequiredService<IOptionsMonitor<S3Options>>().CurrentValue.ClipsBucket;
            try
            {
                await storage.DeleteObjectAsync(clips, result.OriginalKey, ct);
            }
            catch (Exception ex)
            {
                // Error, not Warning: a leaked original silently eats the disk savings this
                // pipeline exists to deliver, so it should be alertable / dashboarded.
                _logger.LogError(ex,
                    "Compressed clip={ClipId} but failed to delete the original {Key}; it is now an orphan consuming storage.",
                    job.ClipId, result.OriginalKey);
            }
        }
    }
}
