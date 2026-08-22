using System.Globalization;

namespace GankedTV.Api.Services.Media;

// A crop rect as normalized 0..1 fractions of the source frame — the wire and storage
// contract shared by rewynd, the web cropper, and both pipeline stages. Never pixels: the
// request is recorded before the source has been probed, and the master is rescaled by the
// MaxHeight cap on every edit generation, so a pixel rect would drift out from under itself.
public sealed record CropRect(double X, double Y, double Width, double Height);

// ffmpeg filter fragments shared between the poster (ThumbnailJobService) and the master
// (CompressJobService). Both stages must express a crop identically or the published poster
// and video disagree — the feed shows the poster, so a drift there is the visible one.
public static class MediaFilters
{
    // Builds `crop=w=…:h=…:x=…:y=…` in ffmpeg's own expression language rather than
    // pre-computed pixels. Two reasons it has to be expressions:
    //   * By the time CompressWorker claims the row, AdvanceThumbnailAsync has already
    //     overwritten clips.height with the POST-crop height, so the compress stage can't
    //     reach the source dimensions to compute pixels from.
    //   * iw/ih are resolved by ffmpeg against the actual decoded frame, so the same filter
    //     string is correct for the poster and the master even if they differ.
    // Sizes and offsets snap to even values (yuv420p chroma subsampling needs even dimensions,
    // and an odd offset shifts the chroma plane against the luma). The min() clamps keep a rect
    // that rounds past the edge inside the frame; they're single-quoted so their commas aren't
    // read as filtergraph separators — same trick as JitLadderService's `scale=-2:'min(ih,H)'`.
    public static string Crop(CropRect crop)
    {
        var ci = CultureInfo.InvariantCulture;
        var x = crop.X.ToString("F6", ci);
        var y = crop.Y.ToString("F6", ci);
        var w = crop.Width.ToString("F6", ci);
        var h = crop.Height.ToString("F6", ci);

        // Width/height first so the offset clamps can reference the already-clamped size:
        // ffmpeg evaluates crop's w/h before x/y, and `ow`/`oh` refer to the output rect.
        return $"crop=w='min({SnapEven("iw", w)},iw)':h='min({SnapEven("ih", h)},ih)'"
             + $":x='min({SnapEven("iw", x)},iw-ow)':y='min({SnapEven("ih", y)},ih-oh)'";
    }

    // Nearest even value at or below `dim*fraction + 1` — i.e. round to nearest, then snap even.
    //
    // The +1 is load-bearing, not slop. The fractions arriving here were produced by dividing an
    // even pixel count by the frame size and serializing to 6 decimals, so multiplying back lands
    // a hair BELOW the integer it came from: 2560/3440 → "0.744186" → ×3440 = 2559.99984. A plain
    // floor-to-even turns that into 2558 and silently shaves 2px off every crop, which then
    // disagrees with the post-crop width/height the thumbnail stage recorded — and compounds by
    // another 2px on each edit generation. Rounding first recovers the exact even value.
    private static string SnapEven(string dim, string fraction) =>
        $"floor(({dim}*{fraction}+1)/2)*2";
}
