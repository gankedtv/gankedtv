using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Media;

// "Remove black bars" suggestion behind GET /clips/{id}/crop-suggestion. Runs ffmpeg's
// cropdetect at a few timestamps and reports the rect it thinks holds the picture.
//
// Deliberately NOT part of the thumbnail stage: detection costs a second or three and several
// extra range reads on every upload, ~95% of which are plain 16:9 and will never be cropped,
// and the answer is only useful while a human is looking at the crop editor.
public sealed partial class CropDetectService : ICropDetectService
{
    private readonly IFfmpegRunner _ffmpeg;
    private readonly IOptionsMonitor<MediaJobOptions> _jobOptions;
    private readonly ILogger<CropDetectService> _logger;

    public CropDetectService(
        IFfmpegRunner ffmpeg,
        IOptionsMonitor<MediaJobOptions> jobOptions,
        ILogger<CropDetectService> logger)
    {
        _ffmpeg = ffmpeg;
        _jobOptions = jobOptions;
        _logger = logger;
    }

    // Where to sample, as fractions of the duration. Avoids both ends: intros and outros are the
    // most likely places to hit a fade or a title card.
    private static readonly double[] SamplePoints = [0.15, 0.50, 0.85];

    // A suggestion this close to the full frame isn't worth offering — the user would apply it,
    // see nothing change, and pay a re-encode for it.
    private const double NearFullFrame = 0.98;

    public async Task<CropSuggestion> DetectAsync(
        string videoUrl,
        double? durationSecs,
        CancellationToken ct)
    {
        var opts = _jobOptions.CurrentValue;
        var budget = Stopwatch.StartNew();

        // Probe the frame size FIRST. cropdetect's own x1/x2/y1/y2 are the bounds of the detected
        // *content*, not of the frame — on a 3440-wide pillarboxed source holding 2560px of
        // picture, x2 reads 2999. Deriving the frame width from it would normalize the rect
        // against 3000px and hand back a crop that's wrong by the width of the bars, which is
        // precisely the thing being measured. Without real dimensions there is no suggestion.
        var frame = await ProbeDimensionsAsync(videoUrl, opts, RemainingBudget(opts, budget), ct);
        if (frame is not { Width: > 0, Height: > 0 })
        {
            return NotDetected(0);
        }

        var (fw, fh) = (frame.Value.Width, frame.Value.Height);
        var sampleCount = Math.Clamp(opts.CropDetectSamples, 1, SamplePoints.Length);

        // Union bounding box across samples, in source pixels. A fade-to-black sample reports a
        // tiny rect; taking the union means it widens the suggestion back toward the full frame
        // instead of eating real content — the failure mode that matters, because a crop that's
        // too tight permanently destroys picture while one that's too loose just leaves some bar
        // behind for the user to drag away.
        int? left = null, top = null, right = null, bottom = null;
        var detectedSamples = 0;

        for (var i = 0; i < sampleCount; i++)
        {
            if (ct.IsCancellationRequested) break;

            var remaining = RemainingBudget(opts, budget);
            if (remaining <= TimeSpan.Zero) break;

            // Without a known duration, sample from the very start — the only offset guaranteed
            // to decode. Better one usable sample than three seeks past the end.
            var offset = durationSecs is { } dur && dur > 0 ? dur * SamplePoints[i] : 0;

            var parsed = await RunSampleAsync(videoUrl, offset, opts, remaining, ct);
            if (parsed is not { } rect) continue;

            // A rect that doesn't fit the probed frame means the two disagree about what was
            // decoded; unioning it in would corrupt every other sample.
            if (rect.X < 0 || rect.Y < 0
                || rect.X + rect.Width > fw || rect.Y + rect.Height > fh
                || rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            detectedSamples++;
            left = left is { } l ? Math.Min(l, rect.X) : rect.X;
            top = top is { } t ? Math.Min(t, rect.Y) : rect.Y;
            right = right is { } r ? Math.Max(r, rect.X + rect.Width) : rect.X + rect.Width;
            bottom = bottom is { } b ? Math.Max(b, rect.Y + rect.Height) : rect.Y + rect.Height;
        }

        if (detectedSamples == 0
            || left is not { } x0 || top is not { } y0
            || right is not { } x1 || bottom is not { } y1)
        {
            return new CropSuggestion(false, null, fw, fh, detectedSamples);
        }

        var crop = new CropRect((double)x0 / fw, (double)y0 / fh, (double)(x1 - x0) / fw, (double)(y1 - y0) / fh);

        // Nothing worth cropping, or something so aggressive it's more likely a dark scene than
        // a letterbox. Both come back as "no suggestion" — the manual cropper still works.
        if ((crop.Width >= NearFullFrame && crop.Height >= NearFullFrame)
            || crop.Width < ClipCropExtents.MinExtent
            || crop.Height < ClipCropExtents.MinExtent)
        {
            return new CropSuggestion(false, null, fw, fh, detectedSamples);
        }

        return new CropSuggestion(true, crop, fw, fh, detectedSamples);
    }

    private static TimeSpan RemainingBudget(MediaJobOptions opts, Stopwatch elapsed) =>
        opts.CropDetectTimeout - elapsed.Elapsed;

    private static CropSuggestion NotDetected(int samples) => new(false, null, null, null, samples);

    // Frame dimensions of the first video stream. Null on any failure — the caller degrades to
    // "no suggestion" rather than guessing.
    private async Task<(int Width, int Height)?> ProbeDimensionsAsync(
        string videoUrl,
        MediaJobOptions opts,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero) return null;

        var args = new List<string>
        {
            "-v", "error",
            "-print_format", "json",
            "-show_streams",
            "-select_streams", "v:0",
            videoUrl,
        };

        try
        {
            var result = await _ffmpeg.RunAsync(opts.FfprobePath, args, timeout, ct);
            if (result.ExitCode != 0) return null;

            using var doc = JsonDocument.Parse(result.Stdout);
            if (!doc.RootElement.TryGetProperty("streams", out var streams)
                || streams.ValueKind != JsonValueKind.Array
                || streams.GetArrayLength() == 0)
            {
                return null;
            }

            var s = streams[0];
            if (!s.TryGetProperty("width", out var w) || w.ValueKind != JsonValueKind.Number
                || !s.TryGetProperty("height", out var h) || h.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            return (w.GetInt32(), h.GetInt32());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "cropdetect dimension probe failed; skipping the suggestion.");
            return null;
        }
    }

    // One cropdetect pass. Returns null for anything that isn't a clean parse — the caller
    // treats a missing sample as "this one didn't contribute", not as an error.
    private async Task<DetectedRect?> RunSampleAsync(
        string videoUrl,
        double offsetSecs,
        MediaJobOptions opts,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var ci = CultureInfo.InvariantCulture;
        var args = new List<string>
        {
            "-hide_banner",
            "-ss", offsetSecs.ToString("F3", ci),
            "-i", videoUrl,
            // reset=0 accumulates across the sampled frames rather than restarting per interval,
            // so the last cropdetect line reflects every frame we looked at. round=2 keeps the
            // result even, matching the yuv420p alignment the encoder needs anyway.
            "-vf", $"cropdetect=limit={opts.CropDetectLimit.ToString(ci)}:round=2:reset=0",
            "-frames:v", "12",
            "-f", "null", "-",
        };

        try
        {
            var result = await _ffmpeg.RunAsync(opts.FfmpegPath, args, timeout, ct);
            // Exit code is deliberately not checked: cropdetect writes its findings to stderr as
            // it goes, so a run that produced usable lines before dying is still usable.
            return ParseLastCropLine(result.Stderr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Detection is advisory; a broken ffmpeg invocation must degrade to "no suggestion"
            // rather than 500 a request the user made from inside the crop editor.
            _logger.LogWarning(ex, "cropdetect sample failed; continuing without it.");
            return null;
        }
    }

    // cropdetect logs `… x1:A x2:B y1:C y2:D w:W h:H x:X y:Y pts:… crop=W:H:X:Y`. The last line
    // carries the accumulated result. Only w/h/x/y are read — they are the content rect in
    // source pixels. x1/x2/y1/y2 describe the same rect's edges, NOT the frame, so they cannot
    // be used to recover the frame size (see ProbeDimensionsAsync).
    internal static DetectedRect? ParseLastCropLine(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return null;

        DetectedRect? last = null;
        foreach (Match m in CropLineRegex().Matches(stderr))
        {
            if (!TryInt(m, "w", out var w) || !TryInt(m, "h", out var h)
                || !TryInt(m, "x", out var x) || !TryInt(m, "y", out var y))
            {
                continue;
            }

            if (w <= 0 || h <= 0 || x < 0 || y < 0) continue;
            last = new DetectedRect(x, y, w, h);
        }

        return last;
    }

    private static bool TryInt(Match m, string group, out int value)
    {
        value = 0;
        var g = m.Groups[group];
        return g.Success && int.TryParse(g.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    // Anchored on the `w:… h:… x:… y:…` run so a line that happens to contain other `x:`-style
    // tokens can't produce a partial match.
    [GeneratedRegex(
        @"\bw:(?<w>-?\d+)\s+h:(?<h>-?\d+)\s+x:(?<x>-?\d+)\s+y:(?<y>-?\d+)",
        RegexOptions.ExplicitCapture)]
    private static partial Regex CropLineRegex();

    internal sealed record DetectedRect(int X, int Y, int Width, int Height);
}

// The minimum kept fraction, in the Media namespace so CropDetectService doesn't have to reach
// across into Services.Clips for it. ClipCropValidation.MinCropExtent is the same number and
// delegates here so the two can't drift.
public static class ClipCropExtents
{
    public const double MinExtent = 0.05;
}
