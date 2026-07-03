using System.Diagnostics;
using System.Globalization;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Media;

// Upload-time compression (the disk win). Re-encodes the raw upload into a single
// resolution-capped, quality-targeted master (AV1 on the GPU box, H.264 in dev), so each clip
// keeps exactly one efficient video file on disk. The original is deleted by the worker after
// the DB points at the new master.
public sealed class CompressJobService : ICompressJobService
{
    private static readonly TimeSpan DownloadUrlLifetime = TimeSpan.FromMinutes(30);

    private readonly IObjectStorageService _storage;
    private readonly IFfmpegRunner _ffmpeg;
    private readonly IOptionsMonitor<MediaJobOptions> _jobOptions;
    private readonly IOptionsMonitor<S3Options> _s3;
    private readonly ILogger<CompressJobService> _logger;

    public CompressJobService(
        IObjectStorageService storage,
        IFfmpegRunner ffmpeg,
        IOptionsMonitor<MediaJobOptions> jobOptions,
        IOptionsMonitor<S3Options> s3,
        ILogger<CompressJobService> logger)
    {
        _storage = storage;
        _ffmpeg = ffmpeg;
        _jobOptions = jobOptions;
        _s3 = s3;
        _logger = logger;
    }

    public async Task<CompressionResult> CompressAsync(ClaimedMediaJob job, CancellationToken ct)
    {
        var opts = _jobOptions.CurrentValue;
        var buckets = _s3.CurrentValue;

        var inputUrl = _storage.GetPresignedGetUrlForWorker(buckets.ClipsBucket, job.VideoKey, DownloadUrlLifetime);
        var outputKey = CompressedKeyFor(job.VideoKey);

        // Per-attempt token so a re-claimed lease can't collide on the same temp path.
        var outPath = Path.Combine(
            Path.GetTempPath(),
            $"gankedtv-cmp-{job.ClipId:N}-{Guid.NewGuid():N}.mp4");
        try
        {
            // Both encode attempts share ONE TranscodeTimeout budget: a fallback must never push a
            // single job attempt past the lease (LeaseDuration), or another worker could re-claim the
            // row mid-encode and flap the deterministic output key. See MediaJobOptions.LeaseDuration.
            var elapsed = Stopwatch.StartNew();
            var failure = await RunEncodeAsync(inputUrl, outPath, job, opts, opts.VideoEncoder, opts.TranscodeTimeout, ct);

            // A hardware (*_nvenc) encoder that won't open — ffmpeg outrunning the host NVIDIA
            // driver, a busy or absent GPU — fails the clip identically on every retry. Fall back
            // once to the software encoder of the same codec family so uploads keep flowing until
            // the GPU path recovers; VideoCodec (and thus the player path) is unchanged.
            var remaining = opts.TranscodeTimeout - elapsed.Elapsed;
            if (failure is not null
                && opts.HardwareEncoderFallbackEnabled
                && IsNvencEncoder(opts.VideoEncoder)
                && remaining > TimeSpan.Zero
                && !ct.IsCancellationRequested)
            {
                var software = SoftwareEncoderFor(opts.VideoEncoder);
                _logger.LogWarning(
                    "compress clip={ClipId}: hardware encoder {Hardware} failed to produce output; falling back to software {Software}.",
                    job.ClipId, opts.VideoEncoder, software);
                failure = await RunEncodeAsync(inputUrl, outPath, job, opts, software, remaining, ct);
            }

            if (failure is not null)
            {
                throw new InvalidOperationException(
                    $"ffmpeg compression failed ({ThumbnailJobService.RedactUrls(failure)})");
            }

            await using (var stream = File.OpenRead(outPath))
            {
                await _storage.PutObjectAsync(buckets.ClipsBucket, outputKey, stream, "video/mp4", ct);
            }

            _logger.LogInformation(
                "Compressed clip={ClipId} codec={Codec} key={Key} size={Size}B",
                job.ClipId, opts.VideoCodec, outputKey, new FileInfo(outPath).Length);

            return new CompressionResult(outputKey, opts.VideoCodec, job.VideoKey);
        }
        finally
        {
            TryDelete(outPath);
        }
    }

    // Runs one encode attempt with the given encoder, bounded by `timeout`. Returns null on success,
    // or a redaction-ready "exit N: <stderr>" diagnostic on failure (non-zero exit, or a missing/empty
    // output file).
    private async Task<string?> RunEncodeAsync(
        string inputUrl,
        string outPath,
        ClaimedMediaJob job,
        MediaJobOptions opts,
        string encoder,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var args = BuildCompressArgs(inputUrl, outPath, job.SourceHeight, opts, encoder);
        var result = await _ffmpeg.RunAsync(opts.FfmpegPath, args, timeout, ct);
        if (result.ExitCode != 0 || !File.Exists(outPath) || new FileInfo(outPath).Length <= 0)
        {
            return $"exit {result.ExitCode}: {result.Stderr}";
        }

        return null;
    }

    private static bool IsNvencEncoder(string encoder) =>
        encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase);

    // Software encoder of the same codec family as a hardware encoder, so a fallback re-encode keeps
    // the clip's persisted VideoCodec correct. All targets take the CRF quality flag (see BuildCompressArgs).
    internal static string SoftwareEncoderFor(string hardwareEncoder)
    {
        if (hardwareEncoder.Contains("av1", StringComparison.OrdinalIgnoreCase)) return "libsvtav1";
        if (hardwareEncoder.Contains("hevc", StringComparison.OrdinalIgnoreCase)
            || hardwareEncoder.Contains("h265", StringComparison.OrdinalIgnoreCase)) return "libx265";
        return "libx264";
    }

    // The compressed master lives at a distinct key (…/{clipId}.cmp.mp4) so the encode never
    // overwrites the source mid-job; the worker deletes the original only after the DB has
    // been repointed at this key.
    internal static string CompressedKeyFor(string originalKey) =>
        originalKey.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            ? originalKey[..^4] + ".cmp.mp4"
            : originalKey + ".cmp.mp4";

    internal static List<string> BuildCompressArgs(
        string inputUrl,
        string outputPath,
        short? sourceHeight,
        MediaJobOptions opts,
        string? encoder = null)
    {
        var videoEncoder = encoder ?? opts.VideoEncoder;
        var ci = CultureInfo.InvariantCulture;
        var args = new List<string>
        {
            "-y",
            "-i", inputUrl,
            "-map", "0:v:0",
            "-map", "0:a:0?",
        };

        // Only downscale (never upscale): scale to MaxHeight when the source is known to be
        // taller. -2 keeps width even and preserves aspect ratio.
        if (sourceHeight is { } h && h > opts.MaxHeight)
        {
            args.Add("-vf");
            args.Add($"scale=-2:{opts.MaxHeight.ToString(ci)}");
        }

        args.Add("-c:v");
        args.Add(videoEncoder);
        // NVENC takes the quality target as -cq; software encoders (libx264/libsvtav1) use -crf.
        args.Add(IsNvencEncoder(videoEncoder) ? "-cq" : "-crf");
        args.Add(opts.Crf.ToString(ci));
        args.Add("-pix_fmt");
        args.Add("yuv420p");

        args.AddRange(new[]
        {
            "-c:a", "aac",
            "-b:a", "128k",
            "-movflags", "+faststart",
            outputPath,
        });

        return args;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete temp compressed file {Path}", path);
        }
    }
}
