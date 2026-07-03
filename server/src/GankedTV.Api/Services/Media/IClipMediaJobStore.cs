namespace GankedTV.Api.Services.Media;

public sealed record ClaimedMediaJob(
    Guid ClipId,
    Guid UserId,
    int? GameId,
    string VideoKey,
    short? SourceHeight,
    int AttemptNumber);

// Import-stage variant — extends ClaimedMediaJob with the source URL the fetcher needs.
// Kept as a record-with-extra-property rather than a brand-new shape so the import worker
// can still share the base MediaStageWorker plumbing.
public sealed record ClaimedImportJob(
    Guid ClipId,
    Guid UserId,
    int? GameId,
    string VideoKey,
    string ImportSourceUrl,
    string Title,
    int AttemptNumber);

public sealed record FinalizedMediaJob(
    string ThumbnailKey,
    short? DurationSecs,
    short? Width,
    short? Height);

public interface IClipMediaJobStore
{
    // Atomically claims one clip in the given status that is not currently leased and
    // hasn't exhausted its retry budget. Bumps ProcessingAttempts and ProcessingStartedAt
    // before returning so other workers skip the row. Returns null when the queue is empty.
    // The status parameter selects the pipeline stage: 'processing' (thumbnail) or
    // 'transcoding' (HLS). SourceHeight carries the stored clip height so the transcode
    // stage can source-cap its ladder without re-probing.
    Task<ClaimedMediaJob?> ClaimNextAsync(
        string status,
        TimeSpan leaseDuration,
        int maxAttempts,
        CancellationToken ct);

    // Resolves the slug for a previously claimed clip's GameId. Null when the clip has
    // no game or the game id is somehow stale. Lookup-only — does not mutate state.
    Task<string?> GetGameSlugAsync(int? gameId, CancellationToken ct);

    // Thumbnail stage success: writes thumbnail + ffprobe metadata onto the clip row,
    // clears the lease, and advances status from 'processing' to toStatus ('transcoding'
    // when the transcode stage runs next, or 'ready' when transcoding is disabled). The
    // status + attempt guard ensures we don't clobber a row another worker re-claimed.
    Task AdvanceThumbnailAsync(
        Guid clipId,
        int expectedAttempt,
        FinalizedMediaJob result,
        string toStatus,
        CancellationToken ct);

    // Compress stage success: repoints VideoKey at the compressed master, records its codec,
    // clears the lease, and advances status from 'transcoding' to 'ready'. Same status +
    // attempt guard. (The original object is deleted separately by the job service.)
    Task CompleteCompressionAsync(
        Guid clipId,
        int expectedAttempt,
        string videoKey,
        string videoCodec,
        CancellationToken ct);

    // Marks the claimed job permanently failed (status fromStatus → 'failed'). Called only
    // after attempts >= max. The expectedAttempt + fromStatus guards prevent a late failure
    // from killing a row that another worker has since re-claimed or advanced. Optional
    // `reason` (one of ClipFailureReasons.*) lets the status endpoint surface a specific
    // user-facing message instead of a generic "failed".
    Task MarkFailedAsync(Guid clipId, int expectedAttempt, string fromStatus, CancellationToken ct, string? reason = null);

    // Releases a lease without changing status by clearing ProcessingStartedAt, so the
    // row becomes immediately eligible for re-claim by any worker on the next tick.
    // Used after a transient failure that hasn't yet exhausted attempts. The expectedAttempt
    // + fromStatus guards prevent this worker from releasing another worker's lease.
    Task ReleaseLeaseAsync(Guid clipId, int expectedAttempt, string fromStatus, CancellationToken ct);

    // Admin recovery: puts 'failed' clips back into the pipeline for another attempt after an
    // infrastructure fault (e.g. the storage TLS misconfig this issue addresses). A clip with no
    // thumbnail yet returns to 'processing'; one that has a thumbnail but no compressed master
    // returns to 'transcoding'. Clears the failure reason + attempt counter so the fresh run gets
    // the full retry budget. When onlyRetryable is true, content rejections (too long / too
    // large) are left failed. `clipId` narrows to a single clip; null requeues every match.
    // Returns the number of clips requeued.
    Task<int> RequeueFailedMediaAsync(Guid? clipId, bool onlyRetryable, CancellationToken ct);

    // Atomically claims one row in status 'importing' that isn't currently leased and hasn't
    // exhausted its retry budget. Same SKIP LOCKED + lease bump as ClaimNextAsync but returns
    // the import URL alongside (the worker needs it for the fetch).
    Task<ClaimedImportJob?> ClaimNextImportAsync(
        TimeSpan leaseDuration,
        int maxAttempts,
        CancellationToken ct);

    // Import stage success: writes file size + (optionally) the extractor's title onto the
    // row, clears the lease, and advances status 'importing' → 'processing'. The title is
    // overwritten only when the current value still equals `placeholderTitle` — so users who
    // typed a real title before submitting keep it, and the worker fills in extractor titles
    // for the placeholder-only path.
    Task AdvanceImportAsync(
        Guid clipId,
        int expectedAttempt,
        long fileSizeBytes,
        string? extractorTitle,
        string placeholderTitle,
        CancellationToken ct);
}
