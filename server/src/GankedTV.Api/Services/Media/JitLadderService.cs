using System.Globalization;
using System.Text;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Media;

// Watch-time, on-demand transcode of a clip's stored master into a transient H.264 HLS ladder
// in the public-read stream-cache bucket. Because clips are short, a whole-clip transcode is
// fast enough that segment-accurate JIT isn't needed. Cached renditions auto-evict via the
// bucket's lifecycle rule; a later re-watch re-enqueues a build.
public sealed class JitLadderService : IJitLadderService
{
    private static readonly TimeSpan DownloadUrlLifetime = TimeSpan.FromMinutes(30);

    private readonly IObjectStorageService _storage;
    private readonly IFfmpegRunner _ffmpeg;
    private readonly IOptionsMonitor<MediaJobOptions> _jobOptions;
    private readonly IOptionsMonitor<S3Options> _s3;
    private readonly ILogger<JitLadderService> _logger;

    public JitLadderService(
        IObjectStorageService storage,
        IFfmpegRunner ffmpeg,
        IOptionsMonitor<MediaJobOptions> jobOptions,
        IOptionsMonitor<S3Options> s3,
        ILogger<JitLadderService> logger)
    {
        _storage = storage;
        _ffmpeg = ffmpeg;
        _jobOptions = jobOptions;
        _s3 = s3;
        _logger = logger;
    }

    public async Task<string> BuildAsync(ClaimedStreamJob job, CancellationToken ct)
    {
        var opts = _jobOptions.CurrentValue;
        var buckets = _s3.CurrentValue;

        var inputUrl = _storage.GetPresignedGetUrlForWorker(buckets.ClipsBucket, job.VideoKey, DownloadUrlLifetime);
        var rungs = SelectRungs(job.SourceHeight, opts.Ladder);

        var workDir = Path.Combine(
            Path.GetTempPath(),
            $"gankedtv-jit-{job.ClipId:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            // Source may be silent (muted/no audio track). Probe first so var_stream_map only
            // references an audio rendition when one actually exists — otherwise ffmpeg errors
            // on the dangling a:N reference.
            var hasAudio = await ProbeHasAudioAsync(inputUrl, opts, ct);
            var args = BuildFfmpegArgs(inputUrl, workDir, rungs, opts, hasAudio);
            var result = await _ffmpeg.RunAsync(opts.FfmpegPath, args, opts.TranscodeTimeout, ct);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ffmpeg JIT transcode failed (exit {result.ExitCode}): {ThumbnailJobService.RedactUrls(result.Stderr)}");
            }

            var masterPath = Path.Combine(workDir, "master.m3u8");
            if (!File.Exists(masterPath))
            {
                throw new InvalidOperationException("ffmpeg JIT transcode produced no master.m3u8");
            }

            var keyPrefix = BuildCachePrefix(job.ClipId);
            // Upload segments + variant playlists first and master.m3u8 LAST. The /stream
            // endpoint treats the presence of master.m3u8 as "ready", so publishing it before
            // its referenced files exist would briefly serve a playlist whose segments 404.
            const string masterName = "master.m3u8";
            var paths = Directory.EnumerateFiles(workDir)
                .OrderBy(p => Path.GetFileName(p) == masterName) // false (0) sorts before true (1)
                .ToList();
            foreach (var path in paths)
            {
                var name = Path.GetFileName(path);
                await using var stream = File.OpenRead(path);
                await _storage.PutObjectAsync(buckets.StreamCacheBucket, $"{keyPrefix}/{name}", stream, ContentTypeFor(name), ct);
            }

            _logger.LogInformation(
                "JIT ladder cached clip={ClipId} rungs={Rungs} prefix={Prefix}",
                job.ClipId, rungs.Count, keyPrefix);

            return $"{keyPrefix}/master.m3u8";
        }
        finally
        {
            TryDeleteDir(workDir);
        }
    }

    // Object-key prefix for one clip's cached HLS output. Relative URIs inside the playlists
    // resolve against this prefix, so master + variant playlists + segments all live here.
    internal static string BuildCachePrefix(Guid clipId) => $"{clipId:N}";

    // Source-cap the ladder: keep only rungs no taller than the source (never upscale). When
    // the source height is unknown or shorter than every rung, fall back to a single rung at
    // the source height so we still produce a valid (non-upscaled) rendition.
    internal static IReadOnlyList<HlsRung> SelectRungs(short? sourceHeight, IReadOnlyList<HlsRung> ladder)
    {
        var ordered = ladder.OrderByDescending(r => r.Height).ToList();

        if (sourceHeight is null or <= 0)
        {
            return [ordered[^1]];
        }

        var capped = ordered.Where(r => r.Height <= sourceHeight.Value).ToList();
        if (capped.Count > 0)
        {
            return capped;
        }

        var lowest = ordered[^1];
        return
        [
            new HlsRung
            {
                Height = sourceHeight.Value,
                VideoKbps = lowest.VideoKbps,
                MaxrateKbps = lowest.MaxrateKbps,
                AudioKbps = lowest.AudioKbps,
            },
        ];
    }

    // True when the source has at least one audio stream. ffprobe prints one line per audio
    // stream; any output means audio is present. Probe failures are treated as "no audio" so a
    // flaky probe degrades to a video-only ladder rather than failing the whole transcode.
    private async Task<bool> ProbeHasAudioAsync(string inputUrl, MediaJobOptions opts, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-v", "error",
            "-select_streams", "a",
            "-show_entries", "stream=index",
            "-of", "csv=p=0",
            inputUrl,
        };
        var result = await _ffmpeg.RunAsync(opts.FfprobePath, args, opts.ProcessTimeout, ct);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout);
    }

    // Single ffmpeg invocation emitting the whole ladder: master.m3u8 + one variant playlist +
    // segment set per rung, flat in workDir so relative URIs resolve under one key prefix. Uses
    // the H.264 JIT encoder for universal browser playback. When includeAudio is false (silent
    // source) the variants are video-only and the stream map omits audio.
    internal static List<string> BuildFfmpegArgs(
        string inputUrl,
        string workDir,
        IReadOnlyList<HlsRung> rungs,
        MediaJobOptions opts,
        bool includeAudio)
    {
        var ci = CultureInfo.InvariantCulture;
        var n = rungs.Count;

        var args = new List<string> { "-y", "-i", inputUrl };

        var filter = new StringBuilder();
        filter.Append("[0:v]split=").Append(n.ToString(ci));
        for (var i = 0; i < n; i++) filter.Append("[v").Append(i).Append(']');
        for (var i = 0; i < n; i++)
        {
            // min(ih,H) caps the rung at the source height so we never upscale — covers both
            // the source-capped rungs and the unknown-height fallback. Single-quoted so the
            // comma inside min() isn't parsed as a filtergraph separator.
            filter.Append(";[v").Append(i).Append("]scale=-2:'min(ih,")
                .Append(rungs[i].Height.ToString(ci))
                .Append(")'[v").Append(i).Append("out]");
        }
        args.Add("-filter_complex");
        args.Add(filter.ToString());

        for (var i = 0; i < n; i++)
        {
            args.Add("-map");
            args.Add($"[v{i}out]");
            args.Add($"-c:v:{i}");
            args.Add(opts.JitVideoEncoder);
            args.Add($"-b:v:{i}");
            args.Add($"{rungs[i].VideoKbps.ToString(ci)}k");
            args.Add($"-maxrate:v:{i}");
            args.Add($"{rungs[i].MaxrateKbps.ToString(ci)}k");
            args.Add($"-bufsize:v:{i}");
            args.Add($"{(rungs[i].MaxrateKbps * 2).ToString(ci)}k");
        }

        if (includeAudio)
        {
            for (var i = 0; i < n; i++)
            {
                args.Add("-map");
                args.Add("a:0");
            }
            args.Add("-c:a");
            args.Add("aac");
            for (var i = 0; i < n; i++)
            {
                args.Add($"-b:a:{i}");
                args.Add($"{rungs[i].AudioKbps.ToString(ci)}k");
            }
        }

        // Pair each video rendition with its audio ("v:0,a:0 …") when audio exists, else
        // video-only variants ("v:0 v:1 …").
        var streamMap = string.Join(' ', Enumerable.Range(0, n)
            .Select(i => includeAudio ? $"v:{i},a:{i}" : $"v:{i}"));

        args.AddRange(new[]
        {
            "-f", "hls",
            "-hls_time", ((int)opts.SegmentDuration.TotalSeconds).ToString(ci),
            "-hls_playlist_type", "vod",
            "-hls_segment_type", opts.SegmentType,
            "-hls_flags", "independent_segments",
            "-master_pl_name", "master.m3u8",
            "-var_stream_map", streamMap,
        });

        if (string.Equals(opts.SegmentType, "fmp4", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-hls_fmp4_init_filename");
            args.Add("init_%v.mp4");
        }

        var segExt = string.Equals(opts.SegmentType, "fmp4", StringComparison.OrdinalIgnoreCase) ? "m4s" : "ts";
        args.Add("-hls_segment_filename");
        args.Add(Path.Combine(workDir, $"stream_%v_%03d.{segExt}"));
        args.Add(Path.Combine(workDir, "stream_%v.m3u8"));

        return args;
    }

    internal static string ContentTypeFor(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".m3u8" => "application/vnd.apple.mpegurl",
            ".ts" => "video/mp2t",
            ".m4s" => "video/mp4",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream",
        };
    }

    private void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete temp JIT dir {Dir}", dir);
        }
    }
}
