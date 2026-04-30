namespace GankedTV.Api.Services.ObjectStorage;

public sealed class S3Options
{
    public string Endpoint { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string? PublicUrl { get; set; }
    public string ClipsBucket { get; set; } = "clips";
    public string ThumbnailsBucket { get; set; } = "thumbnails";
}
