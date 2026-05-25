namespace GankedTV.Api.Services.Clips;

public interface IClipImportService
{
    // Validates the URL + metadata, inserts a Clip in status='importing', and returns its id.
    // The background ImportWorker picks the row up, fetches the source via yt-dlp, writes the
    // bytes to S3 at the clip's VideoKey, and advances the row into 'processing' — at which
    // point the existing thumbnail → compress → ready pipeline takes over.
    Task<ClipResult<ImportClipResult>> SubmitAsync(
        Guid userId,
        ImportClipInput input,
        CancellationToken ct);

    // Metadata-only preview. Validates the URL (allow-list) and runs a no-download yt-dlp
    // probe so the wizard can show duration + title in step 1 — and gate "Continue" when the
    // source already exceeds MaxClipDurationSecs. Authoritative duration enforcement still
    // happens in the worker (ffprobe on the downloaded file); this is a UX shortcut so the
    // user doesn't waste time filling step 2 for a doomed clip.
    Task<ClipResult<ImportClipPreviewResult>> PreviewAsync(string? url, CancellationToken ct);
}

public sealed record ImportClipInput(
    string? Url,
    string? Title,
    string? Description,
    int? GameId,
    string? Visibility,
    IReadOnlyList<string>? Tags);

public sealed record ImportClipResult(Guid ClipId, string Status);

// Wire response for POST /clips/import/preview. All metadata fields are nullable because
// not every extractor exposes them — the frontend gates only when DurationSecs is known.
// MaxClipDurationSecs is always included so the client doesn't have to know the cap.
public sealed record ImportClipPreviewResult(
    string? Title,
    int? DurationSecs,
    int? Width,
    int? Height,
    string? ThumbnailUrl,
    int MaxClipDurationSecs);

// Default title for imports that don't ship one up-front. The ImportWorker swaps this for
// the extractor's title (when it has one) by matching on this exact string — kept here so
// service + worker + tests all reference the same constant.
public static class ClipImportDefaults
{
    public const string PlaceholderTitle = "Importing…";
}
