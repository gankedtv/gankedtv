using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Services.Media;

public sealed class ClipStreamJobStore : IClipStreamJobStore
{
    private readonly GankedTvDbContext _db;
    private readonly TimeProvider _clock;

    public ClipStreamJobStore(GankedTvDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task EnqueueAsync(Guid clipId, CancellationToken ct)
    {
        // Insert-if-absent. ON CONFLICT DO NOTHING keeps concurrent /stream requests for the
        // same clip from stacking duplicates or racing on the clip_id PK; an existing row
        // (pending / in-flight / failed) is left untouched.
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO clip_stream_jobs (clip_id, status, processing_attempts, created_at, updated_at)
            VALUES ({clipId}, 'pending', 0, now(), now())
            ON CONFLICT (clip_id) DO NOTHING
        ", ct);
    }

    public async Task<string?> GetStatusAsync(Guid clipId, CancellationToken ct) =>
        await _db.ClipStreamJobs
            .AsNoTracking()
            .Where(j => j.ClipId == clipId)
            .Select(j => j.Status)
            .FirstOrDefaultAsync(ct);

    public async Task<ClaimedStreamJob?> ClaimNextAsync(TimeSpan leaseDuration, int maxAttempts, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var leaseExpiry = now - leaseDuration;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var rows = await _db.ClipStreamJobs
            .FromSqlInterpolated($@"
                SELECT *
                FROM clip_stream_jobs
                WHERE status = 'pending'
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

        var job = rows[0];

        // The clip must still exist and be ready to serve as the transcode source.
        var clip = await _db.Clips
            .AsNoTracking()
            .Where(c => c.Id == job.ClipId)
            .Select(c => new { c.VideoKey, c.Height, c.Status })
            .FirstOrDefaultAsync(ct);

        if (clip is null || clip.Status != ClipStatuses.Ready)
        {
            // Source gone / not ready — drop the stale job so it doesn't churn the queue.
            await _db.ClipStreamJobs.Where(j => j.ClipId == job.ClipId).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            return null;
        }

        var nextAttempt = job.ProcessingAttempts + 1;
        await _db.ClipStreamJobs
            .Where(j => j.ClipId == job.ClipId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.ProcessingStartedAt, now)
                .SetProperty(j => j.ProcessingAttempts, nextAttempt)
                .SetProperty(j => j.UpdatedAt, now), ct);

        await tx.CommitAsync(ct);

        return new ClaimedStreamJob(job.ClipId, clip.VideoKey, clip.Height, nextAttempt);
    }

    public async Task CompleteAsync(Guid clipId, CancellationToken ct) =>
        await _db.ClipStreamJobs.Where(j => j.ClipId == clipId).ExecuteDeleteAsync(ct);

    public async Task MarkFailedAsync(Guid clipId, int expectedAttempt, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await _db.ClipStreamJobs
            .Where(j => j.ClipId == clipId
                && j.Status == ClipStreamJobStatuses.Pending
                && j.ProcessingAttempts == expectedAttempt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.Status, ClipStreamJobStatuses.Failed)
                .SetProperty(j => j.ProcessingStartedAt, (DateTimeOffset?)null)
                .SetProperty(j => j.UpdatedAt, now), ct);
    }

    public async Task ReleaseLeaseAsync(Guid clipId, int expectedAttempt, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await _db.ClipStreamJobs
            .Where(j => j.ClipId == clipId
                && j.Status == ClipStreamJobStatuses.Pending
                && j.ProcessingAttempts == expectedAttempt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.ProcessingStartedAt, (DateTimeOffset?)null)
                .SetProperty(j => j.UpdatedAt, now), ct);
    }
}
