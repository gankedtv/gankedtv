using FluentAssertions;
using GankedTV.Api.Services.Media;

namespace GankedTV.Api.Tests.Services.Media;

public class MediaFiltersTests
{
    [Fact]
    public void Crop_EmitsExpressionsNotPixels()
    {
        // The compress stage can't reach the source dimensions (the thumbnail stage has already
        // overwritten clips.height with the POST-crop height), so the filter has to resolve
        // against ffmpeg's own iw/ih rather than anything we pre-compute.
        var filter = MediaFilters.Crop(new CropRect(0.1279, 0, 0.7442, 1));

        filter.Should().StartWith("crop=");
        filter.Should().Contain("iw*").And.Contain("ih*");
    }

    [Fact]
    public void Crop_SingleQuotesClampsSoCommasDontSplitTheFiltergraph()
    {
        // An unquoted min(a,b) would make the comma read as a filter separator, and ffmpeg would
        // fail to parse the chain — the same trap JitLadderService's scale=-2:'min(ih,H)' avoids.
        var filter = MediaFilters.Crop(new CropRect(0.25, 0.25, 0.5, 0.5));

        foreach (var part in new[] { "w=", "h=", "x=", "y=" })
        {
            var at = filter.IndexOf(part, StringComparison.Ordinal);
            at.Should().BeGreaterThan(-1);
            filter[at + part.Length].Should().Be('\'', $"{part} value must be quoted");
        }

        // Every min( must sit inside quotes — count them to be sure none escaped.
        filter.Count(c => c == '\'').Should().Be(8);
    }

    [Fact]
    public void Crop_SnapsSizesAndOffsetsToEven()
    {
        // yuv420p subsamples chroma 2x2: an odd width/height won't encode, and an odd offset
        // shifts the chroma plane against the luma.
        var filter = MediaFilters.Crop(new CropRect(0.3, 0.3, 0.4, 0.4));

        filter.Should().Contain("iw*").And.Contain("/2)*2");
        filter.Should().Contain("ih*").And.Contain("/2)*2");
    }

    [Theory]
    // The exact fractions ThumbnailJobService.SanitizeCrop persists for a 3440x1440 ultrawide
    // holding a 2560-wide 16:9 game: 438/3440 and 2560/3440, serialized to 6 decimals.
    [InlineData(3440, 0.744186, 2560)]
    [InlineData(3440, 0.127326, 438)]
    [InlineData(1440, 1.0, 1440)]
    [InlineData(1920, 0.5, 960)]
    [InlineData(1080, 0.666667, 720)]
    public void Crop_RecoversTheExactEvenPixelCountTheFractionCameFrom(
        int dimension, double fraction, int expectedPixels)
    {
        // The regression: these fractions were produced by dividing an even pixel count by the
        // frame size, so multiplying back lands a hair BELOW the integer they came from
        // (3440 * 0.744186 = 2559.99984). Flooring to even turned that into 2558 — silently
        // shaving 2px off every crop, disagreeing with the width/height the thumbnail stage
        // recorded, and compounding on each edit generation.
        EvaluateSnapEven(MediaFilters.Crop(new CropRect(fraction, fraction, fraction, fraction)),
            dimension, fraction)
            .Should().Be(expectedPixels);
    }

    [Fact]
    public void Crop_RoundTripsEveryEvenWidthOnAnUltrawideFrame()
    {
        // Property check over the whole range rather than a handful of points: any even crop
        // width on a 3440px frame must survive fraction → F6 → filter and come back unchanged.
        const int frame = 3440;
        for (var px = 200; px <= frame; px += 2)
        {
            var fraction = (double)px / frame;
            var filter = MediaFilters.Crop(new CropRect(0, 0, fraction, 1));
            EvaluateSnapEven(filter, frame, fraction)
                .Should().Be(px, "width {0}px must round-trip through the filter", px);
        }
    }

    // Mirrors ffmpeg's evaluation of the emitted `floor((dim*frac+1)/2)*2` sub-expression,
    // reading the serialized fraction back out of the filter string so the test exercises the
    // same 6-decimal representation ffmpeg will see rather than the original double.
    private static int EvaluateSnapEven(string filter, int dimension, double fraction)
    {
        var serialized = fraction.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        filter.Should().Contain(serialized, "the filter must carry the F6 form ffmpeg parses");
        var parsed = double.Parse(serialized, System.Globalization.CultureInfo.InvariantCulture);
        return (int)Math.Floor((dimension * parsed + 1) / 2) * 2;
    }

    [Fact]
    public void Crop_ClampsOffsetsAgainstTheComputedOutputSize()
    {
        // x/y must clamp against iw-ow / ih-oh, not iw / ih: clamping to the frame width would
        // let the rect start past the point where the full output still fits, and ffmpeg errors.
        var filter = MediaFilters.Crop(new CropRect(0.9, 0.9, 0.1, 0.1));

        filter.Should().Contain("iw-ow").And.Contain("ih-oh");
    }

    [Fact]
    public void Crop_UsesInvariantDecimalSeparator()
    {
        // A comma decimal separator from a European locale would split the filtergraph — the
        // exact class of bug the quoting above exists to prevent, arriving by another route.
        var filter = MediaFilters.Crop(new CropRect(0.5, 0.25, 0.125, 0.0625));

        filter.Should().Contain("0.500000").And.Contain("0.062500");
        filter.Should().NotContain("0,");
    }

    [Fact]
    public void Crop_IsDeterministic()
    {
        // Poster and master build the filter separately; a non-deterministic result would let
        // the two disagree, which is exactly the drift this shared helper exists to prevent.
        var rect = new CropRect(0.1, 0.2, 0.3, 0.4);
        MediaFilters.Crop(rect).Should().Be(MediaFilters.Crop(rect));
    }
}
