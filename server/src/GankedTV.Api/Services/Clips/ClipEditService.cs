using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Caching;
using GankedTV.Api.Services.Maintenance;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Clips;

// Post-publish re-edit (re-cut and/or re-crop). Rather than bolt a second encode path onto the
// pipeline, this walks the clip back to the start of it: the row returns to 'processing' with
// the new operations, so the thumbnail stage re-probes and re-posters through them and the
// compress stage applies them to a fresh master and deletes the old one. Both operations ride
// that one re-encode, so a combined edit costs one generation of quality loss rather than two.
//
// Offsets and the crop rect are relative to the CURRENT master (what the owner just watched in
// the editor), not the long-deleted raw upload.
public sealed class ClipEditService : IClipEditService
{
    private readonly GankedTvDbContext _db;
    private readonly IObjectStorageService _storage;
    private readonly IOptionsMonitor<MediaJobOptions> _mediaJobs;
    private readonly IOptionsMonitor<S3Options> _s3;
    private readonly IFeedCache _feedCache;
    private readonly ILogger<ClipEditService> _logger;
    private readonly TimeProvider _clock;

    public ClipEditService(
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptionsMonitor<MediaJobOptions> mediaJobs,
        IOptionsMonitor<S3Options> s3,
        IFeedCache feedCache,
        ILogger<ClipEditService> logger,
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

    public async Task<ClipEditError?> EditAsync(
        Guid userId,
        Guid clipId,
        ClipEdits edits,
        CancellationToken ct)
    {
        if (!edits.HasAny)
        {
            return ClipEditError.NoOperations;
        }

        var media = _mediaJobs.CurrentValue;
        if (!media.TranscodeEnabled)
        {
            // Attribute the unavailability to something the caller actually asked for, so a
            // crop-only request doesn't come back complaining about trimming.
            return edits.Trim is not null ? ClipEditError.TrimUnavailable : ClipEditError.CropUnavailable;
        }

        if (edits.Crop is not null && !media.CropEnabled)
        {
            return ClipEditError.CropUnavailable;
        }

        // AsNoTracking: the mutation goes through ExecuteUpdateAsync, which bypasses the tracker.
        var clip = await _db.Clips
            .AsNoTracking()
            .Where(c => c.Id == clipId)
            .Select(c => new { c.UserId, c.Status, c.Visibility, c.DurationSecs })
            .FirstOrDefaultAsync(ct);

        if (clip is null)
        {
            return ClipEditError.NotFound;
        }

        if (clip.UserId != userId)
        {
            return ClipEditError.Forbidden;
        }

        if (clip.Visibility == ClipVisibilities.Hidden)
        {
            return ClipEditError.Moderated;
        }

        if (clip.Status != ClipStatuses.Ready)
        {
            return ClipEditError.InvalidState;
        }

        if (edits.Trim is { } trim && !IsTrimShapeValid(trim, clip.DurationSecs))
        {
            return ClipEditError.InvalidTrim;
        }

        var now = _clock.GetUtcNow();

        // Conditional on status so two concurrent re-cuts can't both requeue the row (the second
        // sees 'processing' and loses). The visibility guard is repeated here rather than trusted
        // from the read above: a moderator hiding the clip in between would otherwise let a
        // takedown be re-cut. Losing either race reports invalid_state, which is the honest
        // answer — the row moved under the caller. Attempts + lease + failure reason reset so the
        // fresh run gets the full retry budget, exactly like the admin requeue path.
        //
        // All six operation columns are written UNCONDITIONALLY, including the ones this request
        // didn't ask about. Leaving a previous trim in place during a crop-only edit would
        // re-apply an already-applied range to the already-trimmed master and cut it twice.
        var rowsAffected = await _db.Clips
            .Where(c => c.Id == clipId
                && c.Status == ClipStatuses.Ready
                && c.Visibility != ClipVisibilities.Hidden)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, ClipStatuses.Processing)
                .SetProperty(c => c.TrimStartSecs, edits.Trim == null ? null : (double?)edits.Trim.StartSecs)
                .SetProperty(c => c.TrimEndSecs, edits.Trim == null ? null : (double?)edits.Trim.EndSecs)
                .SetProperty(c => c.CropX, edits.Crop == null ? null : (double?)edits.Crop.X)
                .SetProperty(c => c.CropY, edits.Crop == null ? null : (double?)edits.Crop.Y)
                .SetProperty(c => c.CropWidth, edits.Crop == null ? null : (double?)edits.Crop.Width)
                .SetProperty(c => c.CropHeight, edits.Crop == null ? null : (double?)edits.Crop.Height)
                .SetProperty(c => c.EditedAt, now)
                .SetProperty(c => c.EditCount, c => c.EditCount + 1)
                .SetProperty(c => c.ProcessingAttempts, 0)
                .SetProperty(c => c.ProcessingStartedAt, (DateTimeOffset?)null)
                .SetProperty(c => c.FailureReason, (string?)null)
                .SetProperty(c => c.UpdatedAt, now), ct);

        if (rowsAffected == 0)
        {
            return ClipEditError.InvalidState;
        }

        // Reclaim the cached JIT ladder built from the pre-edit master. The edit bumps the
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
                "Feed cache invalidation failed after a clip re-edit; entries will expire via TTL.");
        }

        return null;
    }

    // Shape-only validation. The authoritative clamp lives in the thumbnail stage, which has the
    // exact probed duration — DurationSecs here is rounded to whole seconds, so anything tighter
    // would false-reject legitimate sub-second cuts near the end.
    private static bool IsTrimShapeValid(ClipTrimInput trim, short? durationSecs) =>
        double.IsFinite(trim.StartSecs)
        && double.IsFinite(trim.EndSecs)
        && trim.StartSecs >= 0
        && trim.EndSecs - trim.StartSecs >= ClipUploadService.MinTrimSpanSecs - 1e-9
        && (durationSecs is not { } d || trim.StartSecs < d);
}
