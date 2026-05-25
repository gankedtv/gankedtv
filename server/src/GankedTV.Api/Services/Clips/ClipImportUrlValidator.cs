using GankedTV.Api.Services.Media;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Clips;

public sealed class ClipImportUrlValidator : IClipImportUrlValidator
{
    private readonly IOptionsMonitor<MediaJobOptions> _options;

    public ClipImportUrlValidator(IOptionsMonitor<MediaJobOptions> options)
    {
        _options = options;
    }

    public bool TryParse(string? url, out string normalised, out ImportUrlValidationError error)
    {
        normalised = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
        {
            error = ImportUrlValidationError.InvalidUrl;
            return false;
        }

        // Absolute URI + https scheme only — http would expose us to MITM during the fetch,
        // and platform CDNs (Medal, YouTube) all serve https anyway.
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = ImportUrlValidationError.InvalidUrl;
            return false;
        }

        var allowedHosts = _options.CurrentValue.Import.AllowedHosts;
        var host = parsed.Host.ToLowerInvariant();
        var allowed = false;
        for (var i = 0; i < allowedHosts.Count; i++)
        {
            if (string.Equals(allowedHosts[i], host, StringComparison.OrdinalIgnoreCase))
            {
                allowed = true;
                break;
            }
        }

        if (!allowed)
        {
            error = ImportUrlValidationError.UnsupportedHost;
            return false;
        }

        // Strip fragments and rebuild a canonical absolute URL so the worker's defence-in-depth
        // re-check sees exactly the string we persisted. Query string is preserved — Medal.tv
        // and YouTube both encode the clip id in the query path.
        var builder = new UriBuilder(parsed)
        {
            Fragment = string.Empty,
        };
        normalised = builder.Uri.AbsoluteUri;
        error = default;
        return true;
    }
}
