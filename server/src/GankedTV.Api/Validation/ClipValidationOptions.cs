namespace GankedTV.Api.Validation;

public sealed class ClipValidationOptions
{
    public int MaxUploadSizeMb { get; set; } = 500;
    public int MaxClipDurationSecs { get; set; } = 120;
    public int MaxTitleLength { get; set; } = 255;
    public int MaxDescriptionLength { get; set; } = 5000;
    public IReadOnlyList<string> AllowedContentTypes { get; set; } = new[] { "video/mp4" };

    public long MaxUploadSizeBytes => (long)MaxUploadSizeMb * 1024 * 1024;
}
