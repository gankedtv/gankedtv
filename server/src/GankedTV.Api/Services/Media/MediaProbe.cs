using System.Globalization;
using System.Text.Json;

namespace GankedTV.Api.Services.Media;

// The single ffprobe question the pipeline asks — "how big is the first video stream, and how
// long is it" — and the single parser for the answer. Shared by the thumbnail stage and the
// crop-suggestion endpoint so a change in ffprobe's output, or a fix to the `streams[0]`
// assumption when the first stream is an attached cover image, lands in one place.
//
// Running the process and deciding what a failure means stay with the callers on purpose: the
// thumbnail stage fails the clip on a bad probe, while the suggestion endpoint degrades to
// "no suggestion".
internal static class MediaProbe
{
    internal static List<string> BuildArgs(string inputUrl) =>
    [
        "-v", "error",
        "-print_format", "json",
        "-show_format",
        "-show_streams",
        "-select_streams", "v:0",
        inputUrl,
    ];

    // Null only when the payload isn't parseable JSON at all; a well-formed payload that simply
    // lacks a field comes back with that field null.
    internal static MediaProbeResult? Parse(string? stdout)
    {
        if (string.IsNullOrEmpty(stdout)) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            int? width = null;
            int? height = null;
            double? duration = null;

            if (root.TryGetProperty("streams", out var streams)
                && streams.ValueKind == JsonValueKind.Array
                && streams.GetArrayLength() > 0)
            {
                var s = streams[0];
                width = TryGetInt(s, "width");
                height = TryGetInt(s, "height");
                duration = TryGetDouble(s, "duration");
            }

            // ffprobe occasionally reports duration only on the format object (container-level)
            // and not on the stream — check both.
            if (root.TryGetProperty("format", out var format))
            {
                duration ??= TryGetDouble(format, "duration");
            }

            return new MediaProbeResult(width, height, duration);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? TryGetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static double? TryGetDouble(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        // ffprobe emits duration as a string ("2.123456") when -of json is used, so try
        // string-parse first and fall back to native number parsing.
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
        {
            return s;
        }
        return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    }
}

internal sealed record MediaProbeResult(int? Width, int? Height, double? DurationSecs);
