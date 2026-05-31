using System.Reflection;
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

    // Peek the backing field instead of the property getter — `@event.Request` lazy-instantiates,
    // so touching it on a background/hosted-service crash would attach an empty Request{} block
    // to the event we send to GlitchTip. Falls back to property access if the SDK renames the field.
    private static readonly FieldInfo? RequestField = typeof(SentryEvent)
        .GetField("_request", BindingFlags.Instance | BindingFlags.NonPublic);

    public SentryEvent Process(SentryEvent @event)
    {
        var request = RequestField is null
            ? @event.Request
            : RequestField.GetValue(@event) as SentryRequest;
        if (request is null)
        {
            return @event;
        }

        foreach (var name in SensitiveHeaders)
        {
            request.Headers.Remove(name);
        }

        request.Cookies = null;
        // The SDK captures the query string regardless of SendDefaultPii; redact OAuth code/state.
        request.QueryString = SensitiveQuery.Redact(request.QueryString);
        return @event;
    }
}
