namespace GankedTV.Api.Services.Profile;

public sealed class ProfileMediaOptions
{
    // Per-file caps for browser-side uploads. 5 MiB / 10 MiB matches what the edit modal
    // resizes to after canvas downscaling — the cap is server-side defence in depth, not the
    // primary size enforcement (the client should be sending much smaller bytes).
    public long MaxAvatarBytes { get; set; } = 5 * 1024 * 1024;
    public long MaxBannerBytes { get; set; } = 10 * 1024 * 1024;

    // Allowed MIME types, checked both at upload-url signing time and when verifying the
    // uploaded object via HEAD. PNG/JPEG/WebP cover the canvas-encoded outputs from the
    // browser; we deliberately exclude SVG and animated formats (GIF/APNG) — animated is
    // out of scope per the issue, and SVG carries XSS risk if served from a same-origin
    // public bucket.
    public IList<string> AllowedContentTypes { get; set; } = new List<string>
    {
        "image/png",
        "image/jpeg",
        "image/webp",
    };
}
