using FluentAssertions;
using GankedTV.Api.Auth.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Tests.Auth;

public class TrustedOriginValidatorTests
{
    private static TrustedOriginValidator BuildValidator(
        string? corsOriginsRaw = "https://staging.ganked.tv",
        string webOrigin = "https://ganked.tv") =>
        new(Options.Create(new TrustedOriginOptions
        {
            CorsOriginsRaw = corsOriginsRaw,
            WebOrigin = webOrigin,
        }));

    private static HttpRequest Request(string? origin = null, string? referer = null)
    {
        var ctx = new DefaultHttpContext();
        if (origin is not null) ctx.Request.Headers.Origin = origin;
        if (referer is not null) ctx.Request.Headers.Referer = referer;
        return ctx.Request;
    }

    [Theory]
    [InlineData("https://ganked.tv")]
    [InlineData("https://staging.ganked.tv")]
    [InlineData("HTTPS://GANKED.TV")]
    [InlineData("https://ganked.tv/")]
    public void IsTrusted_AllowedOrigin_ReturnsTrue(string origin)
    {
        BuildValidator().IsTrusted(Request(origin: origin)).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://ganked.tv")]
    [InlineData("null")]
    public void IsTrusted_UnknownOrigin_ReturnsFalse(string origin)
    {
        BuildValidator().IsTrusted(Request(origin: origin)).Should().BeFalse();
    }

    [Fact]
    public void IsTrusted_NoOrigin_FallsBackToReferer()
    {
        var validator = BuildValidator();

        validator.IsTrusted(Request(referer: "https://ganked.tv/clips/abc?x=1")).Should().BeTrue();
        validator.IsTrusted(Request(referer: "https://evil.example/ganked.tv")).Should().BeFalse();
        validator.IsTrusted(Request(referer: "not a url")).Should().BeFalse();
    }

    [Fact]
    public void IsTrusted_OriginWinsOverReferer()
    {
        // A present-but-untrusted Origin must not be rescued by a trusted Referer.
        BuildValidator()
            .IsTrusted(Request(origin: "https://evil.example", referer: "https://ganked.tv/"))
            .Should().BeFalse();
    }

    [Fact]
    public void IsTrusted_NeitherHeader_ReturnsFalse()
    {
        BuildValidator().IsTrusted(Request()).Should().BeFalse();
    }

    [Fact]
    public void WebOrigin_AlwaysIncluded_EvenWithoutCorsList()
    {
        BuildValidator(corsOriginsRaw: null)
            .IsTrusted(Request(origin: "https://ganked.tv"))
            .Should().BeTrue();
    }
}
