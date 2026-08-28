using System.Globalization;
using System.Text.RegularExpressions;
using GankedTV.Api.Services.Caching;
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

        // A trim we can't validate must not proceed: silently dropping it would publish
        // footage the user cut away, and an out-of-range -ss can slip through ffmpeg with
        // exit 0 and a header-only file. Fail the clip instead — deterministic per file,
        // so the worker skips the retry budget (TrimUnverifiableException is fail-fast).
        if (job.TrimStartSecs is not null && job.TrimEndSecs is not null && probe.DurationSecs is null)
        {
            throw new TrimUnverifiableException(
                "Clip has a trim range but ffprobe could not determine the source duration; failing rather than encoding an unverifiable cut.");
        }

        // Crop the poster only when the master is actually going to be cropped, which needs BOTH
        // toggles: the compress stage rechecks CropEnabled, and with TranscodeEnabled off this
        // stage advances straight to 'ready' over a master that is never re-encoded at all. Either
        // one alone would leave the poster and the recorded dimensions — which shape the player —
        // describing a frame that was never encoded. (Both toggles must therefore hold the same
        // value on every host in a split deployment; DEPLOYMENT.md says so.)
        var requestedCrop = opts.CropEnabled && opts.TranscodeEnabled ? job.Crop : null;

        // Divergence from trim, deliberately: a crop we can't validate is DROPPED with a warning
        // rather than failing the clip. A dropped trim would publish footage the user cut away;
        // a dropped crop just leaves the black bars in place, and that's fixable post-publish.
        var sanitized = SanitizeCrop(requestedCrop, probe.Width, probe.Height);
        var crop = sanitized?.Rect;
        if (requestedCrop is not null && sanitized is null && (probe.Width is null || probe.Height is null))
        {
            _logger.LogWarning(
                "clip={ClipId}: ffprobe reported no source dimensions; publishing without the requested crop.",
                job.ClipId);
        }

        var trim = SanitizeTrim(job.TrimStartSecs, job.TrimEndSecs, probe.DurationSecs);
        var playableStart = trim?.Start ?? 0;
        var playableSecs = trim is { } span ? span.End - span.Start : probe.DurationSecs;

        // Poster must come from inside the kept range. When the playable span is unknown
        // or shorter than the offset, seek to the range start (0 for untrimmed clips) —
        // that always yields a decodable frame.
        var seekOffset = playableSecs is null
            || playableSecs.Value <= opts.ThumbnailFrameOffset.TotalSeconds
            ? TimeSpan.FromSeconds(playableStart)
            : TimeSpan.FromSeconds(playableStart) + opts.ThumbnailFrameOffset;

        // Include a per-attempt token so a re-claimed lease (e.g. after the original
        // worker hung past LeaseDuration) can't collide on the same temp path.
        var thumbPath = Path.Combine(
            Path.GetTempPath(),
            $"gankedtv-thumb-{job.ClipId:N}-{Guid.NewGuid():N}.jpg");
        try
        {
            await ExtractFrameAsync(videoUrl, thumbPath, seekOffset, crop, opts, ct);

            var thumbnailKey = ClipKeys.BuildThumbnailKey(job.UserId, job.ClipId, gameSlug);
            await using (var stream = File.OpenRead(thumbPath))
            {
                // Safe to cache because the URL is versioned on edit_count (see SignedUrlCache).
                await _storage.PutObjectAsync(
                    buckets.ThumbnailsBucket,
                    thumbnailKey,
                    stream,
                    "image/jpeg",
                    ct,
                    SignedUrlCache.CacheControlHeader);
            }

            // Dimensions are POST-crop: they drive the player's aspect ratio and the JIT ladder's
            // source cap, both of which see the cropped master, never the source frame. Taken from
            // the sanitizer's own pixel arithmetic rather than re-derived from its fractions —
            // fraction*frame lands 1 ULP low often enough to snap a further 2px off (442/3440*3440
            // = 441.99999999999994), which would record a frame 2px narrower than the one that
            // actually ships.
            var (outWidth, outHeight) = sanitized is { } c
                ? ((int?)c.Width, (int?)c.Height)
                : (probe.Width, probe.Height);

            return new FinalizedMediaJob(
                ThumbnailKey: thumbnailKey,
                DurationSecs: ToShortSecs(playableSecs),
                Width: ToShort(outWidth),
                Height: ToShort(outHeight),
                TrimStartSecs: trim?.Start,
                TrimEndSecs: trim?.End,
                Crop: crop);
        }
        finally
        {
            TryDelete(thumbPath);
        }
    }

    // Clamps a requested trim to the probed duration. Returns null for no trim, a
    // degenerate range (source shorter than the minimum cut), or a whole-clip range.
    // With an unknown duration the request is kept as-is (ExtractAsync fails that case
    // before calling this — never silently drop a user's cut).
    internal static (double Start, double End)? SanitizeTrim(
        double? trimStart, double? trimEnd, double? durationSecs)
    {
        if (trimStart is not { } start || trimEnd is not { } end)
        {
            return null;
        }

        const double minSpan = ClipUploadService.MinTrimSpanSecs;
        if (durationSecs is { } dur)
        {
            end = Math.Min(end, dur);
            start = Math.Max(0, Math.Min(start, end - minSpan));
            if (end - start < minSpan - 1e-9) return null;
            if (start <= 0.05 && end >= dur - 0.05) return null;
        }

        return (start, end);
    }

    // Round-trips a requested rect through the probed pixel grid: fractions → pixels → clamp
    // into frame → snap DOWN to even (yuv420p chroma alignment) → back to fractions. Flooring
    // here is the conservative direction — it can only ever pull the rect further inside the
    // frame. MediaFilters.Crop then rounds those fractions back to the nearest even pixel, which
    // recovers these exact values, so the persisted rect is what actually gets encoded.
    //
    // Carries the pixel dimensions back out alongside the fractions: the caller records them as
    // the clip's post-crop frame, and re-deriving them from the fractions is not lossless.
    // Returns null for no crop, unknown source dimensions, a degenerate axis, or a rect that
    // still covers the whole frame — all of which mean "don't crop".
    internal static SanitizedCrop? SanitizeCrop(CropRect? crop, int? sourceWidth, int? sourceHeight)
    {
        if (crop is null) return null;
        if (sourceWidth is not { } sw || sourceHeight is not { } sh || sw <= 0 || sh <= 0) return null;
        if (!IsFinite(crop.X) || !IsFinite(crop.Y) || !IsFinite(crop.Width) || !IsFinite(crop.Height))
        {
            return null;
        }

        var x = SnapEven(Math.Clamp(crop.X, 0, 1) * sw);
        var y = SnapEven(Math.Clamp(crop.Y, 0, 1) * sh);
        var w = SnapEven(Math.Clamp(crop.Width, 0, 1) * sw);
        var h = SnapEven(Math.Clamp(crop.Height, 0, 1) * sh);

        // Shrink before shifting: a rect that overhangs the frame keeps its origin (the user
        // dragged that edge deliberately) and loses the overhang instead of sliding inward.
        w = Math.Min(w, SnapEven(sw - x));
        h = Math.Min(h, SnapEven(sh - y));
        if (w < 2 || h < 2) return null;

        var fx = (double)x / sw;
        var fy = (double)y / sh;
        var fw = (double)w / sw;
        var fh = (double)h / sh;
        if (fw < ClipCropExtents.MinExtent || fh < ClipCropExtents.MinExtent) return null;

        // Whole frame after snapping — encoding a no-op crop filter would only cost clarity.
        if (x == 0 && y == 0 && w >= sw - 1 && h >= sh - 1) return null;

        return new SanitizedCrop(new CropRect(fx, fy, fw, fh), w, h);
    }

    private static bool IsFinite(double v) => double.IsFinite(v);

    private static int SnapEven(double pixels) => (int)Math.Floor(Math.Max(pixels, 0) / 2) * 2;

    private async Task<MediaProbeResult> ProbeAsync(string inputUrl, MediaJobOptions opts, CancellationToken ct)
    {
        var result = await _ffmpeg.RunAsync(opts.FfprobePath, MediaProbe.BuildArgs(inputUrl), opts.ProcessTimeout, ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffprobe failed (exit {result.ExitCode}): {RedactUrls(result.Stderr)}");
        }

        // A probe we can't read is a hard failure here, unlike the advisory crop suggestion:
        // the poster offset and the trim clamp both depend on the duration.
        return MediaProbe.Parse(result.Stdout)
            ?? throw new InvalidOperationException("ffprobe returned malformed JSON.");
    }

    private async Task ExtractFrameAsync(
        string inputUrl,
        string outputPath,
        TimeSpan seekOffset,
        CropRect? crop,
        MediaJobOptions opts,
        CancellationToken ct)
    {
        // -ss before -i = fast seek (decode-skip to nearest keyframe). For a thumbnail
        // we don't care about exact frame accuracy; speed matters more.
        // -frames:v 1 = single frame; -q:v 4 = JPEG quality (~lossy but small).
        //
        // Runs BEFORE compress, so the input is the raw upload — uncapped, a 4K capture yields a
        // 4K poster for a ~320px card. See BuildScaleFilter for the clamping.
        var maxEdge = Math.Max(2, opts.ThumbnailMaxEdge);
        var args = new List<string>
        {
            "-y",
            "-ss", seekOffset.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", inputUrl,
        };

        // One -vf slot, composed, mirroring CompressJobService. ffmpeg honours only the LAST
        // -vf, so two of them silently drop one filter — a poster that kept the pillarbox bars
        // while the video lost them, on the surface most people see. Crop comes FIRST so the
        // edge cap measures the kept region, not the source frame.
        var filters = new List<string>();
        if (crop is not null)
        {
            filters.Add(MediaFilters.Crop(crop));
        }
        filters.Add(BuildScaleFilter(maxEdge));

        args.AddRange(new[]
        {
            "-frames:v", "1",
            "-vf", string.Join(",", filters),
            "-q:v", "4",
            "-f", "mjpeg",
            outputPath,
        });

        var result = await _ffmpeg.RunAsync(opts.FfmpegPath, args, opts.ProcessTimeout, ct);
        if (result.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg frame extraction failed (exit {result.ExitCode}): {RedactUrls(result.Stderr)}");
        }
    }

    internal static string BuildScaleFilter(int maxEdge) => string.Create(
        CultureInfo.InvariantCulture,
        $"scale=w='min(iw,{maxEdge})':h='min(ih,{maxEdge})':force_original_aspect_ratio=decrease:force_divisible_by=2");

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

    // Width/height/duration land in `smallint` columns. Clamp at the upper end so a
    // theoretical 99999×99999 reading from ffprobe still persists; ffprobe never reports
    // negative dimensions so no lower-bound check is needed.
    private static short? ToShort(int? v) =>
        v is null ? null : v.Value > short.MaxValue ? short.MaxValue : (short)v.Value;

    private static short? ToShortSecs(double? v) =>
        v is null ? null : ToShort((int)Math.Round(v.Value));
}

// A crop rect that has been snapped onto a real pixel grid, carrying both the fractions the
// pipeline stores and the exact even pixel dimensions they were derived from.
internal sealed record SanitizedCrop(CropRect Rect, int Width, int Height);
