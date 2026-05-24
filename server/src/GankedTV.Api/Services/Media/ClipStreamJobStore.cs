using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Services.Media;

public sealed class ClipStreamJobStore : IClipStreamJobStore
{
    private readonly GankedTvDbContext _db;
    private readonly TimeProvider _clock;

    // How long a 'failed' job is left alone before /stream is allowed to re-enqueue it. Bounds
    // retries on a permanently-broken clip while letting a transient GPU outage recover.
    private static readonly TimeSpan FailedRetryCooldown = TimeSpan.FromMinutes(5);

    public ClipStreamJobStore(GankedTvDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task EnqueueAsync(Guid clipId, CancellationToken ct)
    {
        // Insert-if-absent, with one exception: a 'failed' row older than the cooldown is reset
        // back to 'pending' so a transient transcode outage doesn't permanently block the clip.
        // Concurrent requests can't stack duplicates (clip_id PK); pending/in-flight rows and
        // freshly-failed rows (within cooldown) are left untouched by the conditional DO UPDATE.
        var retryCutoff = _clock.GetUtcNow() - FailedRetryCooldown;
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO clip_stream_jobs (clip_id, status, processing_attempts, created_at, updated_at)
            VALUES ({clipId}, 'pending', 0, now(), now())
            ON CONFLICT (clip_id) DO UPDATE
              SET status = 'pending', processing_attempts = 0, processing_started_at = NULL, updated_at = now()
              WHERE clip_stream_jobs.status = 'failed' AND clip_stream_jobs.updated_at < {retryCutoff}
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

        // Same claim shape as ClipMediaJobStore.ClaimNextAsync: SELECT … FOR UPDATE SKIP LOCKED
        // materialized via AsNoTracking().ToListAsync() (the row lock is held by the surrounding
        // transaction, not EF's tracker), then a follow-up ExecuteUpdateAsync writes the lease.
        // It's two statements rather than an UPDATE … WHERE id = (SELECT … FOR UPDATE), but keeps
        // the lock + claim atomic and the code readable — don't "optimize" into one statement.
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
