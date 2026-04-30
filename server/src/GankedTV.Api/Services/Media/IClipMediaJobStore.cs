namespace GankedTV.Api.Services.Media;

public sealed record ClaimedMediaJob(
    Guid ClipId,
    Guid UserId,
    int? GameId,
    string VideoKey,
    int AttemptNumber);

public sealed record FinalizedMediaJob(
    string ThumbnailKey,
    short? DurationSecs,
    short? Width,
    short? Height);

public interface IClipMediaJobStore
{
    // Atomically claims one clip in 'processing' status that is not currently leased and
    // hasn't exhausted its retry budget. Bumps ProcessingAttempts and ProcessingStartedAt
    // before returning so other workers skip the row. Returns null when the queue is empty.
    Task<ClaimedMediaJob?> ClaimNextAsync(
        TimeSpan leaseDuration,
        int maxAttempts,
        CancellationToken ct);

    // Resolves the slug for a previously claimed clip's GameId. Null when the clip has
    // no game or the game id is somehow stale. Lookup-only — does not mutate state.
    Task<string?> GetGameSlugAsync(int? gameId, CancellationToken ct);

    // Marks the claimed job ready: writes thumbnail + ffprobe metadata onto the clip row,
    // clears the lease, and flips status to 'ready'. The status + attempt guard ensures
    // we don't clobber a row that another worker already re-claimed (e.g. after this
    // worker's lease elapsed mid-extraction) or that was already moved to 'failed'.
    Task MarkReadyAsync(
        Guid clipId,
        int expectedAttempt,
        FinalizedMediaJob result,
        CancellationToken ct);

    // Marks the claimed job permanently failed. Called only after attempts >= max.
    // The expectedAttempt guard prevents a late failure from killing a row that another
    // worker has since re-claimed.
    Task MarkFailedAsync(Guid clipId, int expectedAttempt, CancellationToken ct);

    // Releases a lease without changing status by clearing ProcessingStartedAt, so the
    // row becomes immediately eligible for re-claim by any worker on the next tick.
    // Used after a transient failure that hasn't yet exhausted attempts. The
    // expectedAttempt guard prevents this worker from releasing another worker's lease
    // (race: this worker's lease expired, another worker re-claimed and bumped attempts,
    // then this worker's late-arriving failure releases the new claim mid-flight).
    Task ReleaseLeaseAsync(Guid clipId, int expectedAttempt, CancellationToken ct);
}
