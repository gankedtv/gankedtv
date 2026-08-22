using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Validation;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Services.Media;

public sealed class ClipMediaJobStore : IClipMediaJobStore
{
    private readonly GankedTvDbContext _db;
    private readonly TimeProvider _clock;

    public ClipMediaJobStore(GankedTvDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<ClaimedMediaJob?> ClaimNextAsync(
        string status,
        TimeSpan leaseDuration,
        int maxAttempts,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var leaseExpiry = now - leaseDuration;

        // Wrapping SELECT FOR UPDATE SKIP LOCKED + the lease bump in a single transaction
        // means another worker can't see the row between us locking it and us writing the
        // claim — without the lock, two pollers could both observe an expired lease and
        // both proceed. The status parameter selects the stage queue ('processing' or
        // 'transcoding'); both are backed by a partial idx_clips_<status>_updated_at index.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // AsNoTracking on a FOR UPDATE query is safe: the row lock is held by the
        // surrounding transaction, not by EF's change tracker. Untracked materialization
        // just avoids a tracker entry we don't need (the follow-up write goes through
        // ExecuteUpdateAsync, which bypasses the tracker anyway).
        var rows = await _db.Clips
            .FromSqlInterpolated($@"
                SELECT *
                FROM clips
                WHERE status = {status}
                  AND (processing_started_at IS NULL OR processing_started_at < {leaseExpiry})
                  AND processing_attempts < {maxAttempts}
                ORDER BY updated_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            ")
            .AsNoTracking()
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        var clip = rows[0];
        var nextAttempt = clip.ProcessingAttempts + 1;

        await _db.Clips
            .Where(c => c.Id == clip.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.ProcessingStartedAt, now)
                .SetProperty(c => c.ProcessingAttempts, nextAttempt)
                .SetProperty(c => c.UpdatedAt, now), ct);

        await tx.CommitAsync(ct);

        return new ClaimedMediaJob(
            clip.Id, clip.UserId, clip.GameId, clip.VideoKey, clip.Height, nextAttempt,
            clip.TrimStartSecs, clip.TrimEndSecs, clip.EditCount, ToCropRect(clip));
    }

    // The four columns are written and cleared as a unit (ck_clips_crop_rect enforces it), so
    // one null means "no crop" rather than a partially-specified rect.
    private static CropRect? ToCropRect(Clip clip) =>
        clip.CropX is { } x && clip.CropY is { } y
            && clip.CropWidth is { } w && clip.CropHeight is { } h
            ? new CropRect(x, y, w, h)
            : null;

    public async Task<string?> GetGameSlugAsync(int? gameId, CancellationToken ct)
    {
        if (gameId is null) return null;
        return await _db.Games
            .AsNoTracking()
            .Where(g => g.Id == gameId.Value)
            .Select(g => g.Slug)
            .FirstOrDefaultAsync(ct);
    }

    public async Task AdvanceThumbnailAsync(
        Guid clipId,
        int expectedAttempt,
        FinalizedMediaJob result,
        string toStatus,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await _db.Clips
            .Where(c => c.Id == clipId
                && c.Status == ClipStatuses.Processing
                && c.ProcessingAttempts == expectedAttempt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, toStatus)
                .SetProperty(c => c.ThumbnailKey, result.ThumbnailKey)
                .SetProperty(c => c.DurationSecs, result.DurationSecs)
                .SetProperty(c => c.Width, result.Width)
                .SetProperty(c => c.Height, result.Height)
                .SetProperty(c => c.TrimStartSecs, result.TrimStartSecs)
                .SetProperty(c => c.TrimEndSecs, result.TrimEndSecs)
                // The snapped rect, so the compress stage crops through exactly the filter the
                // poster was taken with. Null when the crop was dropped (unknown source dims) or
                // snapped out to the whole frame — either way the master must not be cropped.
                .SetProperty(c => c.CropX, result.Crop == null ? null : (double?)result.Crop.X)
                .SetProperty(c => c.CropY, result.Crop == null ? null : (double?)result.Crop.Y)
                .SetProperty(c => c.CropWidth, result.Crop == null ? null : (double?)result.Crop.Width)
                .SetProperty(c => c.CropHeight, result.Crop == null ? null : (double?)result.Crop.Height)
                .SetProperty(c => c.ProcessingStartedAt, (DateTimeOffset?)null)
                // Reset the attempt counter so the next stage (compress) gets its own full
                // MaxAttempts budget rather than inheriting the thumbnail stage's count.
                .SetProperty(c => c.ProcessingAttempts, 0)
                .SetProperty(c => c.UpdatedAt, now), ct);
    }

    public async Task CompleteCompressionAsync(
        Guid clipId,
        int expectedAttempt,
        string videoKey,
        string videoCodec,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await _db.Clips
            .Where(c => c.Id == clipId
                && c.Status == ClipStatuses.Transcoding
                && c.ProcessingAttempts == expectedAttempt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, ClipStatuses.Ready)
                .SetProperty(c => c.VideoKey, videoKey)
                .SetProperty(c => c.VideoCodec, videoCodec)
                .SetProperty(c => c.ProcessingStartedAt, (DateTimeOffset?)null)
                .SetProperty(c => c.UpdatedAt, now), ct);
    }

    public async Task MarkFailedAsync(Guid clipId, int expectedAttempt, string fromStatus, CancellationToken ct, string? reason = null)
    {
        var now = _clock.GetUtcNow();

        // A failed re-cut must never take a live clip dark. Its previous master is still in
        // storage (compress deletes the old object only after the row is repointed), so drop the
        // pending cut and put the clip back the way viewers last saw it instead of failing it.
        // EditedAt is only ever stamped on an already-published clip, so its presence proves this
        // run is a re-cut rather than a first publish. The poster may already show a frame from
        // the discarded range — cosmetic, and the next successful re-cut replaces it.
        var rolledBack = await _db.Clips
            .Where(c => c.Id == clipId
                && c.Status == fromStatus
                && c.ProcessingAttempts == expectedAttempt
                && c.EditedAt != null
                && c.ThumbnailKey != null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, ClipStatuses.Ready)
                // Clear the range so an admin requeue can't re-apply the edit that just failed.
                .SetProperty(c => c.TrimStartSecs, (double?)null)
                .SetProperty(c => c.TrimEndSecs, (double?)null)
                .SetProperty(c => c.CropX, (double?)null)
                .SetProperty(c => c.CropY, (double?)null)
                .SetProperty(c => c.CropWidth, (double?)null)
                .SetProperty(c => c.CropHeight, (double?)null)
                // Only the first re-cut can restore "never edited"; later ones had a real
                // earlier edit whose stamp must survive.
                .SetProperty(c => c.EditedAt, c => c.EditCount <= 1 ? null : c.EditedAt)
                .SetProperty(c => c.ProcessingStartedAt, (DateTimeOffset?)null)
                .SetProperty(c => c.FailureReason, (string?)null)
                .SetProperty(c => c.UpdatedAt, now), ct);

        if (rolledBack > 0)
        {
            return;
        }

        await _db.Clips
            .Where(c => c.Id == clipId
                && c.Status == fromStatus
                && c.ProcessingAttempts == expectedAttempt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, ClipStatuses.Failed)
                .SetProperty(c => c.ProcessingStartedAt, (DateTimeOffset?)null)
                .SetProperty(c => c.FailureReason, reason)
                .SetProperty(c => c.UpdatedAt, now), ct);
    }

    public async Task ReleaseLeaseAsync(Guid clipId, int expectedAttempt, string fromStatus, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await _db.Clips
            .Where(c => c.Id == clipId
                && c.Status == fromStatus
                && c.ProcessingAttempts == expectedAttempt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.ProcessingStartedAt, (DateTimeOffset?)null)
                .SetProperty(c => c.UpdatedAt, now), ct);
    }

    public async Task<int> RequeueFailedMediaAsync(Guid? clipId, bool onlyRetryable, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        var query = _db.Clips.Where(c => c.Status == ClipStatuses.Failed);
        if (clipId is { } id)
        {
            query = query.Where(c => c.Id == id);
        }
        if (onlyRetryable)
        {
            // Same set as ClipFailureReasons.IsRetryable, as a query so it translates to SQL. A
            // null reason (unrecorded fault) is retryable, matching IsRetryable.
            query = query.Where(c =>
                c.FailureReason == null
                || !ClipFailureReasons.NonRetryableReasons.Contains(c.FailureReason));
        }

        // Restart each clip at the stage it still needs. An import that failed before its source
        // was fetched (has an import URL but no downloaded bytes) restarts at 'importing'; a clip
        // that has source but no thumbnail restarts at 'processing'; one with a thumbnail but no
        // compressed master restarts at 'transcoding'. Reset the lease + attempt counter so the
        // retry gets the full budget, and clear the stale failure reason.
        return await query.ExecuteUpdateAsync(setters => setters
            .SetProperty(c => c.Status, c =>
                c.ImportSourceUrl != null && c.FileSizeBytes == null ? ClipStatuses.Importing
                : c.ThumbnailKey == null ? ClipStatuses.Processing
                : ClipStatuses.Transcoding)
            .SetProperty(c => c.ProcessingAttempts, 0)
            .SetProperty(c => c.ProcessingStartedAt, (DateTimeOffset?)null)
            .SetProperty(c => c.FailureReason, (string?)null)
            .SetProperty(c => c.UpdatedAt, now), ct);
    }

    public async Task<ClaimedImportJob?> ClaimNextImportAsync(
        TimeSpan leaseDuration,
        int maxAttempts,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var leaseExpiry = now - leaseDuration;
        var importingStatus = ClipStatuses.Importing;

        // Same SKIP LOCKED + lease-bump shape as ClaimNextAsync — replicated here because the
        // import claim needs to return ImportSourceUrl + Title, which aren't on ClaimedMediaJob.
        // Refactoring the existing claim to a generic shape would balloon the diff; copying
        // the 20 lines is the cheaper route. Backed by idx_clips_importing_updated_at.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var rows = await _db.Clips
            .FromSqlInterpolated($@"
                SELECT *
                FROM clips
                WHERE status = {importingStatus}
                  AND (processing_started_at IS NULL OR processing_started_at < {leaseExpiry})
                  AND processing_attempts < {maxAttempts}
                ORDER BY updated_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            ")
            .AsNoTracking()
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        var clip = rows[0];
        var nextAttempt = clip.ProcessingAttempts + 1;

        await _db.Clips
            .Where(c => c.Id == clip.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.ProcessingStartedAt, now)
                .SetProperty(c => c.ProcessingAttempts, nextAttempt)
                .SetProperty(c => c.UpdatedAt, now), ct);

        await tx.CommitAsync(ct);

        // ImportSourceUrl is guaranteed non-null on importing rows by the submit service. The
        // null-coalescing is a defence-in-depth fallback: if the column is somehow null, the
        // worker re-validates and fails fast rather than feeding an empty string to yt-dlp.
        return new ClaimedImportJob(
            clip.Id,
            clip.UserId,
            clip.GameId,
            clip.VideoKey,
            clip.ImportSourceUrl ?? string.Empty,
            clip.Title,
            nextAttempt);
    }

    public async Task AdvanceImportAsync(
        Guid clipId,
        int expectedAttempt,
        long fileSizeBytes,
        string? extractorTitle,
        string placeholderTitle,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var trimmedExtractor = string.IsNullOrWhiteSpace(extractorTitle) ? null : extractorTitle.Trim();

        // Two-phase update so the title overwrite is conditional on placeholder match without
        // an extra round-trip. EF's ExecuteUpdateAsync can't conditionally set one property,
        // so we apply the always-set columns first, then the title separately when applicable.
        var rowsAffected = await _db.Clips
            .Where(c => c.Id == clipId
                && c.Status == ClipStatuses.Importing
                && c.ProcessingAttempts == expectedAttempt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, ClipStatuses.Processing)
                .SetProperty(c => c.FileSizeBytes, fileSizeBytes)
                .SetProperty(c => c.ProcessingStartedAt, (DateTimeOffset?)null)
                // Reset attempts so the next stage (thumbnail) gets its own full budget, just
                // like AdvanceThumbnailAsync does for the compress stage.
                .SetProperty(c => c.ProcessingAttempts, 0)
                .SetProperty(c => c.UpdatedAt, now), ct);

        if (rowsAffected == 0 || trimmedExtractor is null)
        {
            return;
        }

        // Only swap the title if it still equals the user-supplied placeholder. A user who
        // typed a real title before submit keeps it; the placeholder-only path picks up the
        // extractor's title here. Truncate to the shared ClipValidationLimits ceiling so the
        // hard cap stays single-sourced — the upload path also clamps against it via
        // DataAnnotations.
        var truncated = trimmedExtractor.Length > ClipValidationLimits.MaxTitleLength
            ? trimmedExtractor[..ClipValidationLimits.MaxTitleLength]
            : trimmedExtractor;
        await _db.Clips
            .Where(c => c.Id == clipId && c.Title == placeholderTitle)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Title, truncated)
                .SetProperty(c => c.UpdatedAt, now), ct);
    }
}
