namespace GankedTV.Api.Services.Media.Import;

// Metadata extracted from the source by yt-dlp's --print output. All fields are optional
// because not every extractor exposes every field (Medal.tv tends to expose less than
// YouTube). ThumbnailUrl is the platform CDN URL yt-dlp resolves — passed to the wizard
// so the preview card shows the actual clip frame even before the import finishes.
public sealed record ImportedMedia(
    string? Title,
    int? DurationSecs,
    int? Width,
    int? Height,
    string? ThumbnailUrl);

// Per-fetch caps. The worker passes the same limits the upload path enforces
// (ClipValidationOptions.MaxUploadSizeBytes / MaxClipDurationSecs) so an imported
// clip can never sneak past the cap that direct-upload clips obey.
public sealed record ImportFetchOptions(long MaxBytes, int MaxDurationSecs);

public sealed class ImportFetchException : Exception
{
    public ImportFetchException(string message) : base(message) { }
    public ImportFetchException(string message, Exception inner) : base(message, inner) { }
}

// Thrown when the source fails a pre-flight cap check (duration or size). Distinct from
// the generic ImportFetchException so the worker can mark the clip 'failed' with a
// machine-readable Reason code and skip retries — the cap won't change between attempts.
public sealed class ImportSourceRejectedException : Exception
{
    public ImportSourceRejectedException(string reason, string message, int? actualDurationSecs = null)
        : base(message)
    {
        Reason = reason;
        ActualDurationSecs = actualDurationSecs;
    }

    public string Reason { get; }
    public int? ActualDurationSecs { get; }
}

public interface IClipImportSource
{
    // Pulls the source video at `url` into `destinationFilePath`. Returns metadata parsed
    // from the extractor's JSON dump.
    //
    // Exception contract — implementations MUST classify failures so the worker can
    // distinguish retry-worthy from terminal:
    //   * <see cref="ImportFetchException"/>: transient / retryable — network errors,
    //     timeouts, non-zero exit, "exited 0 but no file produced". The MediaStageWorker
    //     loop releases the lease and another attempt picks the clip back up.
    //   * <see cref="ImportSourceRejectedException"/>: non-retryable — duration/size cap
    //     exceeded, or the extractor reports the source as private/geo-blocked/removed.
    //     Retrying won't change the outcome; the worker marks the clip 'failed' with the
    //     exception's structured Reason code on the first attempt.
    Task<ImportedMedia> FetchAsync(
        string url,
        string destinationFilePath,
        ImportFetchOptions options,
        CancellationToken ct);

    // Metadata-only probe. Same shape as FetchAsync's metadata return but no download —
    // costs one HTTP roundtrip to the extractor's metadata endpoint. Used by the import
    // preview endpoint to surface duration/title before the user fills out step 2. Throws
    // ImportSourceRejectedException when the source is unavailable (private, geo-blocked).
    Task<ImportedMedia> ProbeAsync(string url, CancellationToken ct);
}
