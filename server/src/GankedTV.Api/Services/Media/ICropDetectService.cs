namespace GankedTV.Api.Services.Media;

public interface ICropDetectService
{
    // Samples the clip with ffmpeg's cropdetect and suggests a rect the caller may apply.
    // Never throws for a detection failure: everything that isn't a confident suggestion comes
    // back as Detected=false. Purely advisory — nothing is written anywhere.
    Task<CropSuggestion> DetectAsync(string videoUrl, double? durationSecs, CancellationToken ct);
}

// Detected=false means "we have no suggestion" for any reason at all — ffmpeg failed, the
// result was the whole frame, an axis came back below the minimum, the budget ran out. The
// caller shows the manual cropper and says nothing about bars.
public sealed record CropSuggestion(
    bool Detected,
    CropRect? Crop,
    int? SourceWidth,
    int? SourceHeight,
    int Samples);
