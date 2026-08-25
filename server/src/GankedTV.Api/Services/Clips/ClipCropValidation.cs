using GankedTV.Api.Services.Media;

namespace GankedTV.Api.Services.Clips;

// Shape validation for a requested crop rect, shared verbatim by POST /clips/{id}/complete and
// POST /clips/{id}/edit. Single-sourced deliberately: the two routes accept the same four
// fields from the same clients (web cropper and rewynd), so any drift between them would show
// up as "the same rect works on upload but 400s on re-crop".
//
// This is SHAPE only. The authoritative snap lives in ThumbnailJobService.SanitizeCrop, which
// has the probed pixel grid; anything tighter here would false-reject a legitimate rect on a
// frame whose dimensions we haven't seen yet.
public static class ClipCropValidation
{
    // Floor on either axis. A rect this small is far more likely to be a bug in the caller
    // than an intentional crop, and it would leave a master with almost no picture in it.
    public const double MinCropExtent = ClipCropExtents.MinExtent;

    // Floating-point slack for the edge tests: a browser computing `1 - x` from pointer
    // fractions can land a hair past 1.0, and rejecting that would make the right/bottom
    // edge of the cropper unreachable.
    private const double Epsilon = 1e-6;

    // Reads the four optional wire fields. Returns:
    //   (null, false)  — no crop requested (all four absent), which is always valid;
    //   (rect, false)  — a well-formed rect;
    //   (null, true)   — malformed: partially specified, non-finite, or out of range.
    public static (CropRect? Crop, bool Invalid) TryParse(
        double? x, double? y, double? width, double? height)
    {
        if (x is null && y is null && width is null && height is null)
        {
            return (null, false);
        }

        // All four or none: a partial rect has no defensible interpretation, and guessing one
        // would silently crop somewhere the user never asked for.
        if (x is not { } cx || y is not { } cy || width is not { } cw || height is not { } ch)
        {
            return (null, true);
        }

        if (!double.IsFinite(cx) || !double.IsFinite(cy)
            || !double.IsFinite(cw) || !double.IsFinite(ch))
        {
            return (null, true);
        }

        if (cx < -Epsilon || cy < -Epsilon
            || cw < MinCropExtent - Epsilon || ch < MinCropExtent - Epsilon
            || cx + cw > 1 + Epsilon || cy + ch > 1 + Epsilon)
        {
            return (null, true);
        }

        // Clamp after the range check so a value that only just overshot (FP noise from the
        // browser) still satisfies the DB's ck_clips_crop_rect.
        return (new CropRect(
            Math.Clamp(cx, 0, 1),
            Math.Clamp(cy, 0, 1),
            Math.Clamp(cw, 0, 1 - Math.Clamp(cx, 0, 1)),
            Math.Clamp(ch, 0, 1 - Math.Clamp(cy, 0, 1))), false);
    }
}
