using System.Linq;

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

    // Content rejections: the clip itself is unacceptable, so a retry can never fix it. Single
    // source of truth for both IsRetryable and the media-requeue query (ClipMediaJobStore), which
    // filters on this same set so the two can't drift.
    public static readonly string[] NonRetryableReasons = { SourceTooLong, SourceTooLarge };

    // Whether requeuing a failed clip is worth it. Content rejections are skipped; everything else
    // — infra/probe/fetch/transcode/thumbnail faults, or an unrecorded (null) reason — is retryable
    // once the underlying cause (e.g. a storage TLS misconfig) is fixed.
    public static bool IsRetryable(string? reason) =>
        reason is null || !NonRetryableReasons.Contains(reason);
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

    // Owner-only: never listed, and every read path (detail, share code, stream, comments,
    // likes) 404s for anyone but the owner. Unlike unlisted, knowing the link is not enough.
    public const string Private = "private";

    // Set by moderation. Reuses the existing feed predicate (Visibility = 'public') so no
    // new query branches are needed — a hidden clip drops out of every feed automatically.
    // The owner still sees their own clip via owner-scoped read paths; the moderator can
    // restore via /admin/clips/{id}/unhide.
    public const string Hidden = "hidden";

    // IsValid gates user-facing visibility writes (create / patch / import), which must
    // never accept "hidden" — that value is reserved for moderation.
    public static bool IsValid(string value) =>
        string.Equals(value, Public, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Unlisted, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Private, StringComparison.OrdinalIgnoreCase);

    // Callers receive the canonical lowercase form so the DB column stays consistent
    // regardless of how the client cased the input.
    public static string Normalize(string value) => value.ToLowerInvariant();
}
