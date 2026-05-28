using Sentry;
using Sentry.Extensibility;

namespace GankedTV.Api.Observability;

/// <summary>
/// Strips credential-bearing fields from outgoing Sentry events. SendDefaultPii is already false;
/// this is belt-and-braces so auth never leaks even if another integration attaches it.
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
        // The SDK captures the query string regardless of SendDefaultPii; redact OAuth code/state.
        @event.Request.QueryString = SensitiveQuery.Redact(@event.Request.QueryString);
        return @event;
    }
}
