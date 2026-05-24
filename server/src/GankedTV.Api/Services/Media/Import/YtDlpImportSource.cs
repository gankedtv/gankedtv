using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using GankedTV.Api.Data.Entities;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Media.Import;

// yt-dlp adapter. Reuses IFfmpegRunner (despite the name — it's a generic process runner with
// stdout/stderr capture + timeout + safe redaction). Two-pass flow:
//   1. Metadata probe (--skip-download --dump-json) — cheap, no bandwidth used. Reveals the
//      real duration/size, so cap violations turn into a structured ImportSourceRejectedException
//      that the worker can surface as a specific user-facing error ("clip is X seconds, max Y").
//   2. Actual download — only runs when the probe passed. We drop yt-dlp's --match-filter
//      here because we've already gated on duration; leaving it in causes yt-dlp to silently
//      exit 0 with empty stdout/stderr when the filter rejects (the bug the two-pass fixes).
public sealed class YtDlpImportSource : IClipImportSource
{
    private readonly IFfmpegRunner _runner;
    private readonly IOptionsMonitor<MediaJobOptions> _options;
    private readonly ILogger<YtDlpImportSource> _logger;

    public YtDlpImportSource(
        IFfmpegRunner runner,
        IOptionsMonitor<MediaJobOptions> options,
        ILogger<YtDlpImportSource> logger)
    {
        _runner = runner;
        _options = options;
        _logger = logger;
    }

    public async Task<ImportedMedia> ProbeAsync(string url, CancellationToken ct)
    {
        var snapshot = _options.CurrentValue;
        // Probe is best-effort: an extractor that doesn't expose duration returns a media
        // record with DurationSecs=null. The caller (preview endpoint OR fetch path) decides
        // what to do with that.
        return await ProbeMetadataAsync(snapshot, url, ct) ?? new ImportedMedia(null, null, null, null, null);
    }

    public async Task<ImportedMedia> FetchAsync(
        string url,
        string destinationFilePath,
        ImportFetchOptions options,
        CancellationToken ct)
    {
        var snapshot = _options.CurrentValue;

        // ---- Pass 1: metadata-only probe --------------------------------------------------
        // --dump-single-json + --skip-download gives us the same JSON as --print-json without
        // touching any bytes. Cheap (single HTTP roundtrip to the extractor's metadata
        // endpoint) and lets us reject with the actual duration in the error.
        var probeMetadata = await ProbeMetadataAsync(snapshot, url, ct);
        if (probeMetadata is { DurationSecs: { } actualDur } && actualDur > options.MaxDurationSecs)
        {
            throw new ImportSourceRejectedException(
                reason: ClipFailureReasons.SourceTooLong,
                message: $"Source duration {actualDur}s exceeds cap {options.MaxDurationSecs}s.",
                actualDurationSecs: actualDur);
        }

        // ---- Pass 2: actual download ------------------------------------------------------
        // No --match-filter here: we already gated on duration in pass 1, and leaving it in
        // makes yt-dlp silently exit 0 with no file when the filter rejects (the very failure
        // mode this two-pass design exists to surface clearly).
        //
        // --print with the field-subset template emits a tiny JSON ({title,duration,...})
        // instead of yt-dlp's default 500KB+ info dump — the FfmpegRunner buffers at 256 KB
        // and would otherwise truncate a normal --print-json into unparseable garbage.
        var args = new List<string>
        {
            url,
            "--no-playlist",
            "--no-warnings",
            "--no-progress",
            "--max-filesize", options.MaxBytes.ToString(CultureInfo.InvariantCulture),
            "--format", "best[ext=mp4][vcodec!=none][acodec!=none]/best[ext=mp4]/best",
            "--merge-output-format", "mp4",
            "--restrict-filenames",
            "-o", destinationFilePath,
            "--print", CompactInfoTemplate,
            "--no-simulate",
        };

        FfmpegResult result;
        try
        {
            result = await _runner.RunAsync(snapshot.Import.YtdlpPath, args, snapshot.Import.FetchTimeout, ct);
        }
        catch (TimeoutException ex)
        {
            throw new ImportFetchException(
                $"yt-dlp timed out fetching the source after {snapshot.Import.FetchTimeout.TotalMinutes:F0} min.", ex);
        }
        catch (Win32Exception ex)
        {
            _logger.LogError(ex,
                "yt-dlp binary '{Path}' could not be launched. Install yt-dlp or set YTDLP_PATH.",
                snapshot.Import.YtdlpPath);
            throw new ImportFetchException(
                $"yt-dlp binary '{snapshot.Import.YtdlpPath}' is not available. Install it or set YTDLP_PATH.", ex);
        }

        if (result.ExitCode != 0)
        {
            _logger.LogWarning(
                "yt-dlp exited {ExitCode} for {Url}. Stderr tail: {Stderr}",
                result.ExitCode, url, Tail(result.Stderr, 500));
            throw new ImportFetchException(
                $"yt-dlp exited with code {result.ExitCode}. Stderr tail: {Tail(result.Stderr, 200)}");
        }

        if (!File.Exists(destinationFilePath))
        {
            var stderrTail = Tail(result.Stderr, 800);
            var stdoutTail = Tail(result.Stdout, 400);
            _logger.LogWarning(
                "yt-dlp exited 0 for {Url} but produced no file. Stderr tail: {Stderr} | Stdout tail: {Stdout}",
                url, stderrTail, stdoutTail);
            throw new ImportFetchException(
                "yt-dlp reported success but produced no output file. Stderr tail: " + stderrTail);
        }

        // Parse JSON from the download pass — prefer it over the probe's metadata since the
        // download confirms the actual file's dimensions/title.
        var fromDownload = ParseFirstJson(result.Stdout);
        return fromDownload ?? probeMetadata ?? new ImportedMedia(null, null, null, null, null);
    }

    // yt-dlp output template that emits a compact JSON of ONLY the fields we care about.
    // --dump-single-json / --print-json blow up to 500+ KB for normal YouTube videos (every
    // format variant, every thumbnail variant, etc.) and overflow FfmpegRunner's 256 KB
    // stdout buffer, making the response unparseable. The subset stays well under 1 KB.
    // 'thumbnail' is the platform-resolved best thumbnail URL (img.youtube.com for YT,
    // Medal's CDN for Medal.tv) and lets the wizard render a real preview frame regardless
    // of source — the YouTube-only client-side fallback can't help with Medal.
    private const string CompactInfoTemplate = "%(.{title,duration,width,height,thumbnail})j";

    private async Task<ImportedMedia?> ProbeMetadataAsync(MediaJobOptions snapshot, string url, CancellationToken ct)
    {
        var probeArgs = new List<string>
        {
            url,
            "--no-playlist",
            "--no-warnings",
            "--no-progress",
            "--skip-download",
            "--print", CompactInfoTemplate,
        };

        FfmpegResult probe;
        try
        {
            // Use ProcessTimeout (~2 min default) rather than the full FetchTimeout — a metadata
            // probe that takes minutes is already broken; don't make the user wait 15 min.
            probe = await _runner.RunAsync(snapshot.Import.YtdlpPath, probeArgs, snapshot.ProcessTimeout, ct);
        }
        catch (TimeoutException ex)
        {
            // Pre-flight timeout isn't itself a rejection — surface as a generic fetch error so
            // the worker retries (transient network issues sometimes cause this).
            throw new ImportFetchException("yt-dlp metadata probe timed out.", ex);
        }
        catch (Win32Exception ex)
        {
            _logger.LogError(ex,
                "yt-dlp binary '{Path}' could not be launched (probe). Install yt-dlp or set YTDLP_PATH.",
                snapshot.Import.YtdlpPath);
            throw new ImportFetchException(
                $"yt-dlp binary '{snapshot.Import.YtdlpPath}' is not available. Install it or set YTDLP_PATH.", ex);
        }

        if (probe.ExitCode != 0)
        {
            _logger.LogWarning(
                "yt-dlp metadata probe exited {ExitCode} for {Url}. Stderr tail: {Stderr}",
                probe.ExitCode, url, Tail(probe.Stderr, 500));
            // Non-zero on the probe is usually an extractor error (private video, geo-blocked,
            // members-only). Surface as ImportSourceRejectedException with the unavailable code
            // so the worker doesn't waste retry attempts.
            throw new ImportSourceRejectedException(
                reason: ClipFailureReasons.SourceUnavailable,
                message: "Source is unavailable (private, geo-blocked, or removed).");
        }

        return ParseFirstJson(probe.Stdout);
    }

    private static ImportedMedia? ParseFirstJson(string stdout)
    {
        // yt-dlp may emit banner / deprecation lines before the JSON object, so scan every
        // line and pick the first one that parses to a JSON object with a 'title' or similar.
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                return new ImportedMedia(
                    Title: GetStringOrNull(root, "title"),
                    DurationSecs: GetIntOrNull(root, "duration"),
                    Width: GetIntOrNull(root, "width"),
                    Height: GetIntOrNull(root, "height"),
                    ThumbnailUrl: GetStringOrNull(root, "thumbnail"));
            }
            catch (JsonException)
            {
                // Try the next line.
            }
        }
        return null;
    }

    private static string? GetStringOrNull(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static int? GetIntOrNull(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var i) => i,
            JsonValueKind.Number when prop.TryGetDouble(out var d) => (int)Math.Round(d),
            _ => null,
        };
    }

    private static string Tail(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : "..." + s[^max..];
}
