using GankedTV.Api.Services.Media;

namespace GankedTV.Api.Contracts.Clips;

// GET /clips/{id}/crop-suggestion. `detected: false` means "no suggestion" for any reason at
// all — ffmpeg failed, the result was the whole frame, the budget ran out — and the client is
// expected to hide the "Remove black bars" affordance rather than surface an error. The rect,
// when present, is in the same normalized 0..1 space as the /complete and /edit bodies, so it
// can be handed straight back without conversion.
public sealed record CropSuggestionResponse(
    bool Detected,
    CropRectResponse? Crop,
    int? SourceWidth,
    int? SourceHeight,
    // How many of the configured sample points produced a usable cropdetect reading. Surfaced
    // so a client (or a developer with curl) can tell "one lucky sample" from a solid result.
    int Samples)
{
    public static CropSuggestionResponse From(CropSuggestion s) => new(
        s.Detected,
        s.Detected && s.Crop is { } c ? new CropRectResponse(c.X, c.Y, c.Width, c.Height) : null,
        s.SourceWidth,
        s.SourceHeight,
        s.Samples);
}

public sealed record CropRectResponse(double X, double Y, double Width, double Height);
