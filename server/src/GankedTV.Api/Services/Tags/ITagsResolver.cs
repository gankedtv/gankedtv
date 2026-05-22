using GankedTV.Api.Data.Entities;

namespace GankedTV.Api.Services.Tags;

public interface ITagsResolver
{
    /// <summary>
    /// Normalize, dedupe, validate (max count + char/length rules), and get-or-create
    /// the given raw tag strings. The returned <see cref="TagsResolution.Tags"/> is in the
    /// same order as the (deduped) request and contains both freshly inserted and
    /// pre-existing rows. The caller is responsible for attaching them to a clip.
    /// </summary>
    Task<TagsResolution> ResolveAsync(IReadOnlyList<string> requested, CancellationToken ct);

    /// <summary>
    /// Replace a clip's tag set with the supplied (already-resolved) tags. Adds new
    /// associations and removes ones that are no longer present. Caller must have loaded
    /// <c>clip.ClipTags</c> on the change-tracker (e.g. via <c>Include</c>) so the diff
    /// runs against the actual current state.
    /// </summary>
    void SetClipTags(Clip clip, IReadOnlyList<Tag> resolved);
}

public sealed record TagsResolution(IReadOnlyList<Tag> Tags, TagsResolveError? Error)
{
    public bool IsSuccess => Error is null;

    public static TagsResolution Ok(IReadOnlyList<Tag> tags) => new(tags, null);
    public static TagsResolution Fail(TagsResolveError error) => new([], error);
}

public enum TagsResolveError
{
    TooManyTags,
    InvalidTag,
}

/// <summary>
/// Single source of truth for the machine-readable problem-detail codes any caller
/// surfaces when a <see cref="TagsResolveError"/> propagates out as an HTTP 400. Used
/// directly by <c>PATCH /clips</c> and via the <see cref="ClipUploadError"/> mapper
/// in <c>ClipUploadService.MapTagsError</c> → <c>ClipsUploadEndpoints.MapError</c>.
/// </summary>
public static class TagsResolveProblemCodes
{
    public const string TooManyTags = "too_many_tags";
    public const string InvalidTag = "invalid_tag";

    public static string ToCode(TagsResolveError error) => error switch
    {
        TagsResolveError.TooManyTags => TooManyTags,
        TagsResolveError.InvalidTag => InvalidTag,
        _ => throw new System.Diagnostics.UnreachableException($"Unmapped TagsResolveError: {error}"),
    };
}
