namespace GankedTV.Api.Data.Entities;

// Short, machine-readable codes persisted in clips.failure_reason. The web wizard maps
// these to human copy ("clip is too long…"). Adding a code is safe; never rename.
public static class ClipFailureReasons
{
    public const string SourceTooLong = "source_too_long";
    public const string SourceTooLarge = "source_too_large";
    public const string SourceUnavailable = "source_unavailable";
    public const string FetchFailed = "fetch_failed";
    public const string TranscodeFailed = "transcode_failed";
    public const string ThumbnailFailed = "thumbnail_failed";

    // Whether requeuing a failed clip is worth it. Content rejections (the clip itself is
    // unacceptable) won't succeed on a retry, so the media-requeue recovery path skips them;
    // everything else — infra/probe/fetch/transcode/thumbnail faults, or an unrecorded reason —
    // is retryable once the underlying cause (e.g. a storage TLS misconfig) is fixed.
    public static bool IsRetryable(string? reason) =>
        reason is not SourceTooLong and not SourceTooLarge;
}

public static class ClipStatuses
{
    public const string Draft = "draft";
    // URL-import stage: the row exists, but the server-side fetcher (yt-dlp) hasn't pulled
    // the source bytes into the clips bucket yet. ImportWorker advances 'importing' →
    // 'processing' after a successful fetch, mirroring how /clips/{id}/complete flips a
    // drafted upload from 'draft' → 'processing'.
    public const string Importing = "importing";
    public const string Processing = "processing";
    public const string Transcoding = "transcoding";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

public static class ClipVisibilities
{
    public const string Public = "public";
    public const string Unlisted = "unlisted";

    // Set by moderation. Reuses the existing feed predicate (Visibility = 'public') so no
    // new query branches are needed — a hidden clip drops out of every feed automatically.
    // The owner still sees their own clip via owner-scoped read paths; the moderator can
    // restore via /admin/clips/{id}/unhide.
    public const string Hidden = "hidden";

    // Only IsValid keeps its original two-value contract — this gate protects user-facing
    // visibility writes (create / patch / upload), which must never accept "hidden".
    public static bool IsValid(string value) =>
        string.Equals(value, Public, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Unlisted, StringComparison.OrdinalIgnoreCase);

    // Callers receive the canonical lowercase form so the DB column stays consistent
    // regardless of how the client cased the input.
    public static string Normalize(string value) => value.ToLowerInvariant();
}
