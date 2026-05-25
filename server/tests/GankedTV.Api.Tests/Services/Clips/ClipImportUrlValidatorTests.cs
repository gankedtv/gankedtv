using FluentAssertions;
using GankedTV.Api.Services.Clips;
using GankedTV.Api.Services.Media;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GankedTV.Api.Tests.Services.Clips;

public class ClipImportUrlValidatorTests
{
    private static ClipImportUrlValidator Build(params string[] allowed)
    {
        var monitor = Substitute.For<IOptionsMonitor<MediaJobOptions>>();
        var options = new MediaJobOptions();
        options.Import.AllowedHosts = allowed.Length == 0 ? options.Import.AllowedHosts : allowed.ToList();
        monitor.CurrentValue.Returns(options);
        return new ClipImportUrlValidator(monitor);
    }

    [Theory]
    [InlineData("https://medal.tv/clips/abc123")]
    [InlineData("https://www.youtube.com/watch?v=abc")]
    [InlineData("https://youtu.be/abc")]
    public void TryParse_AllowedHost_ReturnsOk(string url)
    {
        var validator = Build();

        var ok = validator.TryParse(url, out var normalised, out var error);

        ok.Should().BeTrue();
        normalised.Should().NotBeEmpty();
        error.Should().Be(default);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com/x")]
    public void TryParse_MalformedOrNonHttps_ReturnsInvalidUrl(string? url)
    {
        var validator = Build();

        var ok = validator.TryParse(url, out _, out var error);

        ok.Should().BeFalse();
        error.Should().Be(ImportUrlValidationError.InvalidUrl);
    }

    [Fact]
    public void TryParse_HttpScheme_RejectsAsInvalidUrl()
    {
        var validator = Build();

        var ok = validator.TryParse("http://www.youtube.com/x", out _, out var error);

        ok.Should().BeFalse();
        error.Should().Be(ImportUrlValidationError.InvalidUrl);
    }

    [Fact]
    public void TryParse_DisallowedHost_ReturnsUnsupportedHost()
    {
        var validator = Build();

        var ok = validator.TryParse("https://vimeo.com/clip/xyz", out _, out var error);

        ok.Should().BeFalse();
        error.Should().Be(ImportUrlValidationError.UnsupportedHost);
    }

    [Fact]
    public void TryParse_StripsFragment_PreservesQueryString()
    {
        var validator = Build();

        var ok = validator.TryParse("https://www.youtube.com/watch?v=abc#t=30", out var normalised, out _);

        ok.Should().BeTrue();
        normalised.Should().Contain("v=abc");
        normalised.Should().NotContain("#");
    }
}
