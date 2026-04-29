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
        TimeSpan leaseDuration,
        int maxAttempts,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var leaseExpiry = now - leaseDuration;

        // Wrapping SELECT FOR UPDATE SKIP LOCKED + the lease bump in a single transaction
        // means another worker can't see the row between us locking it and us writing the
        // claim — without the lock, two pollers could both observe an expired lease and
        // both proceed.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var rows = await _db.Clips
            .FromSqlInterpolated($@"
                SELECT *
                FROM clips
                WHERE status = 'processing'
                  AND thumbnail_key IS NULL
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

        return new ClaimedMediaJob(clip.Id, clip.UserId, clip.GameId, clip.VideoKey, nextAttempt);
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

    public async Task MarkReadyAsync(
        Guid clipId,
        FinalizedMediaJob result,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await _db.Clips
            .Where(c => c.Id == clipId && c.Status == ClipStatuses.Processing)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, ClipStatuses.Ready)
                .SetProperty(c => c.ThumbnailKey, result.ThumbnailKey)
                .SetProperty(c => c.DurationSecs, result.DurationSecs)
                .SetProperty(c => c.Width, result.Width)
                .SetProperty(c => c.Height, result.Height)
                .SetProperty(c => c.ProcessingStartedAt, (DateTimeOffset?)null)
                .SetProperty(c => c.UpdatedAt, now), ct);
    }

    public async Task MarkFailedAsync(Guid clipId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await _db.Clips
            .Where(c => c.Id == clipId && c.Status == ClipStatuses.Processing)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, ClipStatuses.Failed)
                .SetProperty(c => c.ProcessingStartedAt, (DateTimeOffset?)null)
                .SetProperty(c => c.UpdatedAt, now), ct);
    }

    public async Task ReleaseLeaseAsync(Guid clipId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await _db.Clips
            .Where(c => c.Id == clipId && c.Status == ClipStatuses.Processing)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.ProcessingStartedAt, (DateTimeOffset?)null)
                .SetProperty(c => c.UpdatedAt, now), ct);
    }
}
