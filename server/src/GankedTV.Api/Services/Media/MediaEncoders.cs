namespace GankedTV.Api.Services.Media;

// Shared helpers for the hardware→software encoder fallback used by both upload-time compression
// (CompressJobService) and watch-time JIT ladders (JitLadderService). A hardware (*_nvenc) encoder
// can fail to open — ffmpeg newer than the host NVIDIA driver, a busy or absent GPU — and would
// otherwise hard-fail every clip; both stages fall back to the software encoder of the same codec
// family so playback keeps working.
public static class MediaEncoders
{
    public static bool IsNvencEncoder(string encoder) =>
        encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase);

    // Software encoder of the same codec family as a hardware encoder, so a fallback re-encode keeps
    // the output codec (and thus the persisted VideoCodec / the JIT ladder's H.264 contract) correct.
    // All targets take the CRF quality flag; NVENC takes -cq (see CompressJobService.BuildCompressArgs).
    public static string SoftwareEncoderFor(string hardwareEncoder)
    {
        if (hardwareEncoder.Contains("av1", StringComparison.OrdinalIgnoreCase)) return "libsvtav1";
        if (hardwareEncoder.Contains("hevc", StringComparison.OrdinalIgnoreCase)
            || hardwareEncoder.Contains("h265", StringComparison.OrdinalIgnoreCase)) return "libx265";
        return "libx264";
    }
}
