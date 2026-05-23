namespace GankedTV.Api.Services.Media;

public sealed record ClaimedMediaJob(
    Guid ClipId,
    Guid UserId,
    int? GameId,
    string VideoKey,
    short? SourceHeight,
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
    // from killing a row that another worker has since re-claimed or advanced.
    Task MarkFailedAsync(Guid clipId, int expectedAttempt, string fromStatus, CancellationToken ct);

    // Releases a lease without changing status by clearing ProcessingStartedAt, so the
    // row becomes immediately eligible for re-claim by any worker on the next tick.
    // Used after a transient failure that hasn't yet exhausted attempts. The expectedAttempt
    // + fromStatus guards prevent this worker from releasing another worker's lease.
    Task ReleaseLeaseAsync(Guid clipId, int expectedAttempt, string fromStatus, CancellationToken ct);
}
