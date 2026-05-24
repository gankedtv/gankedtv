using GankedTV.Api.Services.Caching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Media;

// Shared base for the two clips-table stages (thumbnail, compress): both claim/release/fail via
// IClipMediaJobStore keyed by the status they own. Subclasses only supply ClaimStatus + the work.
public abstract class ClipStageWorker : MediaStageWorker<ClaimedMediaJob>
{
    protected ClipStageWorker(
        IServiceScopeFactory scopeFactory,
        IFfmpegRunner ffmpeg,
        IOptionsMonitor<MediaJobOptions> options,
        ILogger logger)
        : base(scopeFactory, ffmpeg, options, logger)
    {
    }

    // The clip status this stage claims ('processing' or 'transcoding').
    protected abstract string ClaimStatus { get; }

    // Machine-readable code persisted to clips.failure_reason when this stage exhausts its
    // retry budget. Lets the wizard surface specific copy per stage instead of a generic
    // "import failed" default. Null falls back to "no reason recorded" — kept null on the
    // base for safety so a new stage that forgets to override doesn't accidentally inherit
    // a misleading code.
    protected virtual string? TerminalFailureReason => null;

    protected override Task<ClaimedMediaJob?> ClaimAsync(IServiceProvider scope, MediaJobOptions opts, CancellationToken ct) =>
        scope.GetRequiredService<IClipMediaJobStore>().ClaimNextAsync(ClaimStatus, opts.LeaseDuration, opts.MaxAttempts, ct);

    protected override Guid ClipIdOf(ClaimedMediaJob job) => job.ClipId;
    protected override int AttemptOf(ClaimedMediaJob job) => job.AttemptNumber;

    protected override Task ReleaseAsync(IServiceProvider scope, ClaimedMediaJob job, CancellationToken ct) =>
        scope.GetRequiredService<IClipMediaJobStore>().ReleaseLeaseAsync(job.ClipId, job.AttemptNumber, ClaimStatus, ct);

    protected override Task FailAsync(IServiceProvider scope, ClaimedMediaJob job, CancellationToken ct) =>
        scope.GetRequiredService<IClipMediaJobStore>()
            .MarkFailedAsync(job.ClipId, job.AttemptNumber, ClaimStatus, ct, reason: TerminalFailureReason);

    // Drop cached feed pages once a clip becomes feed-visible (reaches 'ready'). Best-effort: the
    // status transition has already committed, so a cache failure (e.g. Redis down) must NOT bubble
    // up — that would trip the stage's failure/retry path on an already-ready clip. Resolved from
    // the scope to avoid widening worker constructors; the short TTL self-heals if a hit is missed.
    protected static async Task InvalidateFeedsBestEffortAsync(IServiceProvider scope, CancellationToken ct)
    {
        try
        {
            await scope.GetRequiredService<IFeedCache>().InvalidateFeedsAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            scope.GetService<ILoggerFactory>()?.CreateLogger(typeof(ClipStageWorker).FullName!)
                .LogWarning(ex, "Feed cache invalidation failed after a clip reached ready; entries will expire via TTL.");
        }
    }
}
