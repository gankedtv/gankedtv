namespace GankedTV.Api.Services.Media;

// A claimed JIT rendition job: the clip plus the master it should transcode from.
public sealed record ClaimedStreamJob(
    Guid ClipId,
    string VideoKey,
    short? SourceHeight,
    int AttemptNumber,
    // Re-cut generation of the master being transcoded — scopes the cache prefix so a ladder
    // built from a superseded master can't be served after the cut lands.
    int EditCount = 0);

public interface IClipStreamJobStore
{
    // Insert a pending JIT job for the clip if none exists (no-op when one is already
    // pending/in-flight/failed). Called by the /stream endpoint on a cache miss.
    Task EnqueueAsync(Guid clipId, CancellationToken ct);

    // Current job row status for the clip, or null when there is no row. Lets the endpoint
    // distinguish "in flight" (202) from "failed" (503).
    Task<string?> GetStatusAsync(Guid clipId, CancellationToken ct);

    // Atomically claim one pending job whose clip is 'ready', bumping its lease + attempts.
    // Returns null when the queue is empty (or the clip vanished / isn't ready, in which case
    // the stale job row is removed).
    Task<ClaimedStreamJob?> ClaimNextAsync(TimeSpan leaseDuration, int maxAttempts, CancellationToken ct);

    // Success: the rendition is cached — remove the job row (the bucket object is now the
    // source of truth; it auto-expires via lifecycle and a re-watch re-enqueues).
    Task CompleteAsync(Guid clipId, CancellationToken ct);

    // Permanent failure (attempts exhausted): mark the row failed so the endpoint surfaces an
    // error instead of looping. Guards on attempt number against a re-claim race.
    Task MarkFailedAsync(Guid clipId, int expectedAttempt, CancellationToken ct);

    // Transient failure: clear the lease so the row is re-claimable next tick.
    Task ReleaseLeaseAsync(Guid clipId, int expectedAttempt, CancellationToken ct);
}
