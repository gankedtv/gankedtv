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
        var outputKey = CompressedKeyFor(job.VideoKey, job.EditCount);

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
            var encoder = opts.VideoEncoder;
            var failure = await RunEncodeAsync(inputUrl, outPath, job, opts, encoder, opts.TranscodeTimeout, ct);

            // A hardware (*_nvenc) encoder that won't open — ffmpeg outrunning the host NVIDIA
            // driver, a busy or absent GPU — fails the clip identically on every retry. Fall back
            // once to the software encoder of the same codec family so uploads keep flowing until
            // the GPU path recovers; VideoCodec (and thus the player path) is unchanged.
            var remaining = opts.TranscodeTimeout - elapsed.Elapsed;
            if (failure is not null
                && opts.HardwareEncoderFallbackEnabled
                && MediaEncoders.IsNvencEncoder(opts.VideoEncoder)
                && remaining > TimeSpan.Zero
                && !ct.IsCancellationRequested)
            {
                encoder = MediaEncoders.SoftwareEncoderFor(opts.VideoEncoder);
                _logger.LogWarning(
                    "compress clip={ClipId}: hardware encoder {Hardware} failed to produce output; falling back to software {Software}.",
                    job.ClipId, opts.VideoEncoder, encoder);
                failure = await RunEncodeAsync(inputUrl, outPath, job, opts, encoder, remaining, ct);
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
                "Compressed clip={ClipId} codec={Codec} encoder={Encoder} key={Key} size={Size}B",
                job.ClipId, opts.VideoCodec, encoder, outputKey, new FileInfo(outPath).Length);

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
        var args = BuildCompressArgs(
            inputUrl, outPath, job.SourceHeight, opts, encoder, job.TrimStartSecs, job.TrimEndSecs);
        var result = await _ffmpeg.RunAsync(opts.FfmpegPath, args, timeout, ct);
        if (result.ExitCode != 0 || !File.Exists(outPath) || new FileInfo(outPath).Length <= 0)
        {
            return $"exit {result.ExitCode}: {result.Stderr}";
        }

        return null;
    }

    // The compressed master lives at a distinct key (…/{clipId}.cmp.mp4) so the encode never
    // overwrites the source mid-job; the worker deletes the original only after the DB has
    // been repointed at this key. A post-publish re-cut compresses the previous master, so the
    // generation suffix (.cmp2, .cmp3, …) replaces the old one instead of stacking onto it —
    // still deterministic per job, so a re-claimed lease can't flap the output key.
    internal static string CompressedKeyFor(string originalKey, int generation = 0)
    {
        var stem = originalKey.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            ? originalKey[..^4]
            : originalKey;
        var root = StripCompressedSuffix(stem);
        var key = root + CompressedSuffix(generation) + ".mp4";

        // An admin requeue can re-run compress on a master already written at this generation.
        // Skipping ahead keeps the "never encode onto the source key" invariant without giving
        // up determinism — the same (key, generation) still maps to the same output.
        return string.Equals(key, originalKey, StringComparison.Ordinal)
            ? root + CompressedSuffix(generation + 1) + ".mp4"
            : key;
    }

    private static string CompressedSuffix(int generation) =>
        generation <= 0 ? ".cmp" : ".cmp" + generation.ToString(CultureInfo.InvariantCulture);

    // Drops a trailing ".cmp" / ".cmp<digits>" segment so generations replace rather than nest.
    private static string StripCompressedSuffix(string stem)
    {
        var dot = stem.LastIndexOf('.');
        if (dot < 0) return stem;

        var segment = stem.AsSpan(dot + 1);
        if (!segment.StartsWith("cmp", StringComparison.Ordinal)) return stem;

        foreach (var c in segment[3..])
        {
            if (!char.IsAsciiDigit(c)) return stem;
        }
        return stem[..dot];
    }

    internal static List<string> BuildCompressArgs(
        string inputUrl,
        string outputPath,
        short? sourceHeight,
        MediaJobOptions opts,
        string? encoder = null,
        double? trimStartSecs = null,
        double? trimEndSecs = null)
    {
        var videoEncoder = encoder ?? opts.VideoEncoder;
        var ci = CultureInfo.InvariantCulture;
        var args = new List<string> { "-y" };

        // -ss before -i is frame-accurate here because the output re-encodes (ffmpeg
        // keyframe-seeks, then decodes and discards up to the exact point). Span goes
        // through -t: with input seeking, timestamps reset to 0 so -to would misread.
        var trim = trimStartSecs is { } ts && trimEndSecs is { } te && te > ts
            ? (Start: ts, Span: te - ts)
            : ((double Start, double Span)?)null;
        if (trim is { } t)
        {
            args.Add("-ss");
            args.Add(t.Start.ToString("F3", ci));
        }

        args.AddRange(new[]
        {
            "-i", inputUrl,
            "-map", "0:v:0",
            "-map", "0:a:0?",
        });

        if (trim is { } t2)
        {
            args.Add("-t");
            args.Add(t2.Span.ToString("F3", ci));
        }

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
        args.Add(MediaEncoders.IsNvencEncoder(videoEncoder) ? "-cq" : "-crf");
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
