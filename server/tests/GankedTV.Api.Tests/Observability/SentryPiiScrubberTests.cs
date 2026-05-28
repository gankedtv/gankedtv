using FluentAssertions;
using GankedTV.Api.Observability;
using Sentry;

namespace GankedTV.Api.Tests.Observability;

public class SentryPiiScrubberTests
{
    private static readonly SentryPiiScrubber Scrubber = new();

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Cookie")]
    [InlineData("Set-Cookie")]
    public void Process_RemovesCredentialBearingHeaders(string header)
    {
        var @event = new SentryEvent();
        @event.Request.Headers[header] = "Bearer super-secret-token";

        var result = Scrubber.Process(@event);

        result.Request.Headers.Should().NotContainKey(header);
    }

    [Fact]
    public void Process_ClearsCookies()
    {
        var @event = new SentryEvent();
        @event.Request.Cookies = "session=abc; refresh=def";

        Scrubber.Process(@event).Request.Cookies.Should().BeNull();
    }

    [Fact]
    public void Process_RedactsSensitiveQueryParams()
    {
        var @event = new SentryEvent();
        @event.Request.QueryString = "?code=secret&state=xyz&page=2";

        Scrubber.Process(@event).Request.QueryString.Should().Be("?page=2");
    }

    [Fact]
    public void Process_KeepsNonSensitiveHeaders()
    {
        var @event = new SentryEvent();
        @event.Request.Headers["Authorization"] = "Bearer secret";
        @event.Request.Headers["Content-Type"] = "application/json";

        var result = Scrubber.Process(@event);

        result.Request.Headers.Should().ContainKey("Content-Type");
        result.Request.Headers.Should().NotContainKey("Authorization");
    }
}
