using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Media;

// Watch-time JIT stage: claims pending clip_stream_jobs, transcodes the clip's master into a
// cached H.264 HLS ladder, then removes the job row. GPU work, so it's gated by the same
// TranscodeWorkerEnabled flag as the compress stage and typically runs on the GPU box.
public sealed class StreamRenditionWorker : MediaStageWorker<ClaimedStreamJob>
{
    public StreamRenditionWorker(
        IServiceScopeFactory scopeFactory,
        IFfmpegRunner ffmpeg,
        IOptionsMonitor<MediaJobOptions> options,
        ILogger<StreamRenditionWorker> logger)
        : base(scopeFactory, ffmpeg, options, logger)
    {
    }

    protected override string StageName => "jit";
    protected override bool IsWorkerEnabled(MediaJobOptions opts) => opts.TranscodeWorkerEnabled;

    protected override Task<ClaimedStreamJob?> ClaimAsync(IServiceProvider scope, MediaJobOptions opts, CancellationToken ct) =>
        scope.GetRequiredService<IClipStreamJobStore>().ClaimNextAsync(opts.LeaseDuration, opts.MaxAttempts, ct);

    protected override Guid ClipIdOf(ClaimedStreamJob job) => job.ClipId;
    protected override int AttemptOf(ClaimedStreamJob job) => job.AttemptNumber;

    protected override async Task ProcessAsync(IServiceProvider scope, ClaimedStreamJob job, MediaJobOptions opts, CancellationToken ct)
    {
        var jit = scope.GetRequiredService<IJitLadderService>();
        var store = scope.GetRequiredService<IClipStreamJobStore>();
        await jit.BuildAsync(job, ct);
        await store.CompleteAsync(job.ClipId, ct);
    }

    protected override Task ReleaseAsync(IServiceProvider scope, ClaimedStreamJob job, CancellationToken ct) =>
        scope.GetRequiredService<IClipStreamJobStore>().ReleaseLeaseAsync(job.ClipId, job.AttemptNumber, ct);

    protected override Task FailAsync(IServiceProvider scope, ClaimedStreamJob job, CancellationToken ct) =>
        scope.GetRequiredService<IClipStreamJobStore>().MarkFailedAsync(job.ClipId, job.AttemptNumber, ct);
}
