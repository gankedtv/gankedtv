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
    // clears the lease, and flips status to 'ready'. The status guard ensures we don't
    // accidentally resurrect a row that another worker already moved to 'failed'.
    Task MarkReadyAsync(
        Guid clipId,
        FinalizedMediaJob result,
        CancellationToken ct);

    // Marks the claimed job permanently failed. Called only after attempts >= max.
    // Clears the lease so the row is no longer counted as "in flight".
    Task MarkFailedAsync(Guid clipId, CancellationToken ct);

    // Releases a lease without changing status, so the row drops out of "in flight"
    // immediately and another worker can retry it once the lease window elapses
    // naturally. Used after a transient failure that hasn't yet exhausted attempts.
    Task ReleaseLeaseAsync(Guid clipId, CancellationToken ct);
}
