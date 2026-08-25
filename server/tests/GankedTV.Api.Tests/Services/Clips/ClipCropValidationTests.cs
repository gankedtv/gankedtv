using FluentAssertions;
using GankedTV.Api.Services.Clips;

namespace GankedTV.Api.Tests.Services.Clips;

public class ClipCropValidationTests
{
    [Fact]
    public void TryParse_AllFieldsAbsent_IsNoCropAndValid()
    {
        // The body-less /complete contract rewynd and API scripts rely on.
        var (crop, invalid) = ClipCropValidation.TryParse(null, null, null, null);

        crop.Should().BeNull();
        invalid.Should().BeFalse();
    }

    [Theory]
    [InlineData(0.1, null, null, null)]
    [InlineData(0.1, 0.1, null, null)]
    [InlineData(0.1, 0.1, 0.5, null)]
    [InlineData(null, 0.1, 0.5, 0.5)]
    public void TryParse_PartialRect_IsInvalid(double? x, double? y, double? w, double? h)
    {
        // A partial rect has no defensible interpretation — guessing one would crop somewhere
        // the user never asked for.
        var (crop, invalid) = ClipCropValidation.TryParse(x, y, w, h);

        crop.Should().BeNull();
        invalid.Should().BeTrue();
    }

    [Fact]
    public void TryParse_WellFormedRect_RoundTrips()
    {
        var (crop, invalid) = ClipCropValidation.TryParse(0.1279, 0, 0.7442, 1);

        invalid.Should().BeFalse();
        crop.Should().NotBeNull();
        crop!.X.Should().BeApproximately(0.1279, 1e-9);
        crop.Y.Should().Be(0);
        crop.Width.Should().BeApproximately(0.7442, 1e-9);
        crop.Height.Should().Be(1);
    }

    [Theory]
    [InlineData(double.NaN, 0, 0.5, 0.5)]
    [InlineData(0, double.PositiveInfinity, 0.5, 0.5)]
    [InlineData(0, 0, double.NaN, 0.5)]
    [InlineData(0, 0, 0.5, double.NegativeInfinity)]
    public void TryParse_NonFinite_IsInvalid(double x, double y, double w, double h)
    {
        ClipCropValidation.TryParse(x, y, w, h).Invalid.Should().BeTrue();
    }

    [Theory]
    // Negative origin.
    [InlineData(-0.01, 0, 0.5, 0.5)]
    [InlineData(0, -0.01, 0.5, 0.5)]
    // Below the minimum extent on either axis.
    [InlineData(0, 0, 0.04, 0.5)]
    [InlineData(0, 0, 0.5, 0.04)]
    // Overhangs the right/bottom edge.
    [InlineData(0.6, 0, 0.5, 0.5)]
    [InlineData(0, 0.6, 0.5, 0.5)]
    public void TryParse_OutOfRange_IsInvalid(double x, double y, double w, double h)
    {
        ClipCropValidation.TryParse(x, y, w, h).Invalid.Should().BeTrue();
    }

    [Fact]
    public void TryParse_ExactMinimumExtent_IsAccepted()
    {
        // The floor is inclusive; FP slack must not make it unreachable.
        ClipCropValidation.TryParse(0, 0, ClipCropValidation.MinCropExtent, ClipCropValidation.MinCropExtent)
            .Invalid.Should().BeFalse();
    }

    [Fact]
    public void TryParse_FullFrame_IsAccepted()
    {
        // A whole-frame rect is a legitimate thing for a client to send (the user reset the
        // cropper). The thumbnail stage normalizes it away rather than the validator rejecting it.
        var (crop, invalid) = ClipCropValidation.TryParse(0, 0, 1, 1);

        invalid.Should().BeFalse();
        crop.Should().Be(new GankedTV.Api.Services.Media.CropRect(0, 0, 1, 1));
    }

    [Fact]
    public void TryParse_FloatingPointOvershoot_IsClampedNotRejected()
    {
        // A browser computing width as `1 - x` from pointer fractions can land a hair past 1.0.
        // Rejecting that would make the right edge of the cropper literally undraggable.
        var (crop, invalid) = ClipCropValidation.TryParse(0.5, 0.5, 0.5 + 5e-7, 0.5 + 5e-7);

        invalid.Should().BeFalse();
        crop.Should().NotBeNull();
        // Clamped so the persisted rect still satisfies ck_clips_crop_rect.
        (crop!.X + crop.Width).Should().BeLessThanOrEqualTo(1);
        (crop.Y + crop.Height).Should().BeLessThanOrEqualTo(1);
    }
}
