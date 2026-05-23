using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
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

        return new ClaimedMediaJob(clip.Id, clip.UserId, clip.GameId, clip.VideoKey, clip.Height, nextAttempt);
    }

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
                .SetProperty(c => c.ProcessingStartedAt, (DateTimeOffset?)null)
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

    public async Task MarkFailedAsync(Guid clipId, int expectedAttempt, string fromStatus, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await _db.Clips
            .Where(c => c.Id == clipId
                && c.Status == fromStatus
                && c.ProcessingAttempts == expectedAttempt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, ClipStatuses.Failed)
                .SetProperty(c => c.ProcessingStartedAt, (DateTimeOffset?)null)
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
}
