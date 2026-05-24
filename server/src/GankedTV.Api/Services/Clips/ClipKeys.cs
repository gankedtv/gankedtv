namespace GankedTV.Api.Services.Clips;

// Object-storage key builder for clip artefacts. Centralised so every ingestion path
// (direct upload, URL import, dev seed) writes to the same layout — namespaced by user id
// (immutable) and grouped by game slug (when set) so listing the bucket is human-readable.
// No-game uploads omit the slug segment rather than inserting a "null/" placeholder.
public static class ClipKeys
{
    public static string BuildVideoKey(Guid userId, Guid clipId, string? gameSlug) =>
        gameSlug is { Length: > 0 }
            ? $"{userId}/{gameSlug}/{clipId}.mp4"
            : $"{userId}/{clipId}.mp4";

    // Mirrors BuildVideoKey so the thumbnail and video for a given clip live at parallel
    // paths in their respective buckets — keeps manual inspection / GDPR purges simple.
    public static string BuildThumbnailKey(Guid userId, Guid clipId, string? gameSlug) =>
        gameSlug is { Length: > 0 }
            ? $"{userId}/{gameSlug}/{clipId}.jpg"
            : $"{userId}/{clipId}.jpg";
}
