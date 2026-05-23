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

    protected override Task<ClaimedMediaJob?> ClaimAsync(IServiceProvider scope, MediaJobOptions opts, CancellationToken ct) =>
        scope.GetRequiredService<IClipMediaJobStore>().ClaimNextAsync(ClaimStatus, opts.LeaseDuration, opts.MaxAttempts, ct);

    protected override Guid ClipIdOf(ClaimedMediaJob job) => job.ClipId;
    protected override int AttemptOf(ClaimedMediaJob job) => job.AttemptNumber;

    protected override Task ReleaseAsync(IServiceProvider scope, ClaimedMediaJob job, CancellationToken ct) =>
        scope.GetRequiredService<IClipMediaJobStore>().ReleaseLeaseAsync(job.ClipId, job.AttemptNumber, ClaimStatus, ct);

    protected override Task FailAsync(IServiceProvider scope, ClaimedMediaJob job, CancellationToken ct) =>
        scope.GetRequiredService<IClipMediaJobStore>().MarkFailedAsync(job.ClipId, job.AttemptNumber, ClaimStatus, ct);
}
