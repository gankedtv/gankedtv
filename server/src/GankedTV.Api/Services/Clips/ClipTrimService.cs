using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Caching;
using GankedTV.Api.Services.Maintenance;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Clips;

// Post-publish re-cut. Rather than bolt a second encode path onto the pipeline, this walks the
// clip back to the start of it: the row returns to 'processing' with the new range, so the
// thumbnail stage re-probes and re-posters inside the kept span and the compress stage applies
// the cut to a fresh master and deletes the old one. The range is relative to the CURRENT
// master (what the owner just watched in the trimmer), not the long-deleted raw upload.
public sealed class ClipTrimService : IClipTrimService
{
    private readonly GankedTvDbContext _db;
    private readonly IObjectStorageService _storage;
    private readonly IOptionsMonitor<MediaJobOptions> _mediaJobs;
    private readonly IOptionsMonitor<S3Options> _s3;
    private readonly IFeedCache _feedCache;
    private readonly ILogger<ClipTrimService> _logger;
    private readonly TimeProvider _clock;

    public ClipTrimService(
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptionsMonitor<MediaJobOptions> mediaJobs,
        IOptionsMonitor<S3Options> s3,
        IFeedCache feedCache,
        ILogger<ClipTrimService> logger,
        TimeProvider clock)
    {
        _db = db;
        _storage = storage;
        _mediaJobs = mediaJobs;
        _s3 = s3;
        _feedCache = feedCache;
        _logger = logger;
        _clock = clock;
    }

    public async Task<ClipTrimError?> TrimAsync(
        Guid userId,
        Guid clipId,
        ClipTrimInput trim,
        CancellationToken ct)
    {
        if (!_mediaJobs.CurrentValue.TranscodeEnabled)
        {
            return ClipTrimError.TrimUnavailable;
        }

        // AsNoTracking: the mutation goes through ExecuteUpdateAsync, which bypasses the tracker.
        var clip = await _db.Clips
            .AsNoTracking()
            .Where(c => c.Id == clipId)
            .Select(c => new { c.UserId, c.Status, c.Visibility, c.DurationSecs })
            .FirstOrDefaultAsync(ct);

        if (clip is null)
        {
            return ClipTrimError.NotFound;
        }

        if (clip.UserId != userId)
        {
            return ClipTrimError.Forbidden;
        }

        if (clip.Visibility == ClipVisibilities.Hidden)
        {
            return ClipTrimError.Moderated;
        }

        if (clip.Status != ClipStatuses.Ready)
        {
            return ClipTrimError.InvalidState;
        }

        if (!IsShapeValid(trim, clip.DurationSecs))
        {
            return ClipTrimError.InvalidTrim;
        }

        var now = _clock.GetUtcNow();

        // Conditional on status so two concurrent re-cuts can't both requeue the row (the second
        // sees 'processing' and loses). Attempts + lease + failure reason reset so the fresh run
        // gets the full retry budget, exactly like the admin requeue path.
        var rowsAffected = await _db.Clips
            .Where(c => c.Id == clipId && c.Status == ClipStatuses.Ready)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, ClipStatuses.Processing)
                .SetProperty(c => c.TrimStartSecs, trim.StartSecs)
                .SetProperty(c => c.TrimEndSecs, trim.EndSecs)
                .SetProperty(c => c.EditedAt, now)
                .SetProperty(c => c.EditCount, c => c.EditCount + 1)
                .SetProperty(c => c.ProcessingAttempts, 0)
                .SetProperty(c => c.ProcessingStartedAt, (DateTimeOffset?)null)
                .SetProperty(c => c.FailureReason, (string?)null)
                .SetProperty(c => c.UpdatedAt, now), ct);

        if (rowsAffected == 0)
        {
            return ClipTrimError.InvalidState;
        }

        // Reclaim the cached JIT ladder built from the pre-cut master. The re-cut bumps the
        // ladder's key generation, so this is about disk rather than correctness — but it must
        // still not hang off the request: None, not ct, so a client disconnect after the commit
        // can't skip it (same contract as the delete/hide purges).
        await ClipBlobCleanup.TryDeleteStreamCacheAsync(
            _storage, _s3.CurrentValue, clipId, _logger, CancellationToken.None);

        // The clip has just left 'ready', so cached feed pages now advertise a clip whose detail
        // 404s. Best-effort — the row is already committed, so a cache fault must not 500, and
        // that includes a cancellation raised by the caller hanging up.
        try
        {
            await _feedCache.InvalidateFeedsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Feed cache invalidation failed after a clip re-cut; entries will expire via TTL.");
        }

        return null;
    }

    // Shape-only validation. The authoritative clamp lives in the thumbnail stage, which has the
    // exact probed duration — DurationSecs here is rounded to whole seconds, so anything tighter
    // would false-reject legitimate sub-second cuts near the end.
    private static bool IsShapeValid(ClipTrimInput trim, short? durationSecs) =>
        double.IsFinite(trim.StartSecs)
        && double.IsFinite(trim.EndSecs)
        && trim.StartSecs >= 0
        && trim.EndSecs - trim.StartSecs >= ClipUploadService.MinTrimSpanSecs - 1e-9
        && (durationSecs is not { } d || trim.StartSecs < d);
}
