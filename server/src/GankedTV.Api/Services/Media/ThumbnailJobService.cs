using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GankedTV.Api.Services.Clips;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Media;

public sealed class ThumbnailJobService : IThumbnailJobService
{
    private static readonly TimeSpan DownloadUrlLifetime = TimeSpan.FromMinutes(10);

    private readonly IObjectStorageService _storage;
    private readonly IFfmpegRunner _ffmpeg;
    private readonly IOptionsMonitor<MediaJobOptions> _jobOptions;
    private readonly IOptionsMonitor<S3Options> _s3;
    private readonly ILogger<ThumbnailJobService> _logger;

    public ThumbnailJobService(
        IObjectStorageService storage,
        IFfmpegRunner ffmpeg,
        IOptionsMonitor<MediaJobOptions> jobOptions,
        IOptionsMonitor<S3Options> s3,
        ILogger<ThumbnailJobService> logger)
    {
        _storage = storage;
        _ffmpeg = ffmpeg;
        _jobOptions = jobOptions;
        _s3 = s3;
        _logger = logger;
    }

    public async Task<FinalizedMediaJob> ExtractAsync(
        ClaimedMediaJob job,
        string? gameSlug,
        CancellationToken ct)
    {
        var opts = _jobOptions.CurrentValue;
        var buckets = _s3.CurrentValue;

        // Presign a worker-facing GET: against S3_INTERNAL_ENDPOINT when set (a split worker host
        // that reaches storage over a trusted internal endpoint), else the same URL browsers get.
        var videoUrl = _storage.GetPresignedGetUrlForWorker(buckets.ClipsBucket, job.VideoKey, DownloadUrlLifetime);

        // Probe first so we know the duration before deciding the seek offset, plus we
        // get width/height to backfill the clip row (issue #57 mentions DurationSecs only,
        // but the columns are already there and the worker has the data — populate them).
        var probe = await ProbeAsync(videoUrl, opts, ct);

        // When ffprobe couldn't determine duration, fall back to seek=0 — seeking 1s
        // into a sub-1s clip with unknown duration produces no frame, while seek=0 always
        // yields the first decoded frame.
        var seekOffset = probe.DurationSecs is null
            || probe.DurationSecs.Value <= opts.ThumbnailFrameOffset.TotalSeconds
            ? TimeSpan.Zero
            : opts.ThumbnailFrameOffset;

        // Include a per-attempt token so a re-claimed lease (e.g. after the original
        // worker hung past LeaseDuration) can't collide on the same temp path.
        var thumbPath = Path.Combine(
            Path.GetTempPath(),
            $"gankedtv-thumb-{job.ClipId:N}-{Guid.NewGuid():N}.jpg");
        try
        {
            await ExtractFrameAsync(videoUrl, thumbPath, seekOffset, opts, ct);

            var thumbnailKey = ClipKeys.BuildThumbnailKey(job.UserId, job.ClipId, gameSlug);
            await using (var stream = File.OpenRead(thumbPath))
            {
                await _storage.PutObjectAsync(buckets.ThumbnailsBucket, thumbnailKey, stream, "image/jpeg", ct);
            }

            return new FinalizedMediaJob(
                ThumbnailKey: thumbnailKey,
                DurationSecs: ToShortSecs(probe.DurationSecs),
                Width: ToShort(probe.Width),
                Height: ToShort(probe.Height));
        }
        finally
        {
            TryDelete(thumbPath);
        }
    }

    private async Task<ProbeResult> ProbeAsync(string inputUrl, MediaJobOptions opts, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            "-select_streams", "v:0",
            inputUrl,
        };

        var result = await _ffmpeg.RunAsync(opts.FfprobePath, args, opts.ProcessTimeout, ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffprobe failed (exit {result.ExitCode}): {RedactUrls(result.Stderr)}");
        }

        // ffprobe occasionally reports duration only on the format object (container-level)
        // and not on the stream — check both.
        try
        {
            using var doc = JsonDocument.Parse(result.Stdout);
            var root = doc.RootElement;

            int? width = null;
            int? height = null;
            double? duration = null;

            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array && streams.GetArrayLength() > 0)
            {
                var s = streams[0];
                width = TryGetInt(s, "width");
                height = TryGetInt(s, "height");
                duration = TryGetDouble(s, "duration");
            }

            if (root.TryGetProperty("format", out var format))
            {
                duration ??= TryGetDouble(format, "duration");
            }

            return new ProbeResult(width, height, duration);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"ffprobe returned malformed JSON: {ex.Message}", ex);
        }
    }

    private async Task ExtractFrameAsync(
        string inputUrl,
        string outputPath,
        TimeSpan seekOffset,
        MediaJobOptions opts,
        CancellationToken ct)
    {
        // -ss before -i = fast seek (decode-skip to nearest keyframe). For a thumbnail
        // we don't care about exact frame accuracy; speed matters more.
        // -frames:v 1 = single frame; -q:v 4 = JPEG quality (~lossy but small).
        var args = new List<string>
        {
            "-y",
            "-ss", seekOffset.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", inputUrl,
            "-frames:v", "1",
            "-q:v", "4",
            "-f", "mjpeg",
            outputPath,
        };

        var result = await _ffmpeg.RunAsync(opts.FfmpegPath, args, opts.ProcessTimeout, ct);
        if (result.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg frame extraction failed (exit {result.ExitCode}): {RedactUrls(result.Stderr)}");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving a temp file behind is harmless; logging the path lets ops sweep
            // /tmp manually if it ever becomes a problem. Catch broadly so a cleanup
            // failure (e.g. permissions) never masks the real processing exception.
            _logger.LogWarning(ex, "Failed to delete temp thumbnail file {Path}", path);
        }
    }

    // Strip http(s) URLs from ffmpeg/ffprobe stderr before embedding in exceptions.
    // Stderr routinely echoes the input URL on failure ("Failed to open https://…?
    // X-Amz-Signature=…"); the presigned URL signature is short-lived but should not
    // ride along into log lines or upstream error envelopes in clear text.
    private static readonly Regex UrlPattern = new(
        @"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static string RedactUrls(string input) =>
        string.IsNullOrEmpty(input) ? input : UrlPattern.Replace(input, "[redacted-url]");

    private static int? TryGetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static double? TryGetDouble(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        // ffprobe emits duration as a string ("2.123456") when -of json is used, so try
        // string-parse first and fall back to native number parsing.
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return s;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return null;
    }

    // Width/height/duration land in `smallint` columns. Clamp at the upper end so a
    // theoretical 99999×99999 reading from ffprobe still persists; ffprobe never reports
    // negative dimensions so no lower-bound check is needed.
    private static short? ToShort(int? v) =>
        v is null ? null : v.Value > short.MaxValue ? short.MaxValue : (short)v.Value;

    private static short? ToShortSecs(double? v) =>
        v is null ? null : ToShort((int)Math.Round(v.Value));

    private sealed record ProbeResult(int? Width, int? Height, double? DurationSecs);
}
