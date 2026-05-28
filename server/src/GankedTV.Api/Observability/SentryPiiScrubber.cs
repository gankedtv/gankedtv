using Sentry;
using Sentry.Extensibility;

namespace GankedTV.Api.Observability;

/// <summary>
/// Defense-in-depth PII scrubbing for outgoing Sentry events. <c>SendDefaultPii=false</c>
/// already keeps the SDK from attaching request headers/cookies/bodies, but this processor
/// strips credential-bearing fields unconditionally so auth never leaks to GlitchTip even if
/// another integration attaches them.
/// </summary>
public sealed class SentryPiiScrubber : ISentryEventProcessor
{
    private static readonly string[] SensitiveHeaders =
    [
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
    ];

    public SentryEvent Process(SentryEvent @event)
    {
        foreach (var name in SensitiveHeaders)
        {
            @event.Request.Headers.Remove(name);
        }

        @event.Request.Cookies = null;
        // OAuth callback URLs carry ?code=…&state=…; the SDK captures the query string regardless
        // of SendDefaultPii, so strip credential-bearing params before the event leaves.
        @event.Request.QueryString = SensitiveQuery.Redact(@event.Request.QueryString);
        return @event;
    }
}
