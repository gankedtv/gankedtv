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

        var inputUrl = _storage.GetPresignedGetUrl(buckets.ClipsBucket, job.VideoKey, DownloadUrlLifetime);
        var outputKey = CompressedKeyFor(job.VideoKey);

        // Per-attempt token so a re-claimed lease can't collide on the same temp path.
        var outPath = Path.Combine(
            Path.GetTempPath(),
            $"gankedtv-cmp-{job.ClipId:N}-{Guid.NewGuid():N}.mp4");
        try
        {
            var args = BuildCompressArgs(inputUrl, outPath, job.SourceHeight, opts);
            var result = await _ffmpeg.RunAsync(opts.FfmpegPath, args, opts.TranscodeTimeout, ct);
            if (result.ExitCode != 0 || !File.Exists(outPath) || new FileInfo(outPath).Length <= 0)
            {
                throw new InvalidOperationException(
                    $"ffmpeg compression failed (exit {result.ExitCode}): {ThumbnailJobService.RedactUrls(result.Stderr)}");
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
        MediaJobOptions opts)
    {
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
        args.Add(opts.VideoEncoder);
        // NVENC takes the quality target as -cq; software encoders (libx264/libsvtav1) use -crf.
        args.Add(opts.VideoEncoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase) ? "-cq" : "-crf");
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
