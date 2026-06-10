using GankedTV.Api.Configuration;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Auth.Cookies;

public sealed class TrustedOriginOptions
{
    public string? CorsOriginsRaw { get; set; }
    public string WebOrigin { get; set; } = "";
}

/// <summary>
/// CSRF guard for cookie-authenticated auth endpoints. Cookies travel automatically, so a
/// hostile page could POST /auth/refresh cross-site; browsers always attach Origin (or at
/// least Referer) to cross-site POSTs, and we only accept requests whose origin is in the
/// CORS allowlist. Requests with neither header are rejected — every legitimate caller is
/// the SPA, which sends Origin.
/// </summary>
public interface ITrustedOriginValidator
{
    bool IsTrusted(HttpRequest request);
}

public sealed class TrustedOriginValidator : ITrustedOriginValidator
{
    private readonly HashSet<string> _allowed;

    public TrustedOriginValidator(IOptions<TrustedOriginOptions> options)
    {
        var opts = options.Value;
        _allowed = new HashSet<string>(
            CorsOriginsParser.Parse(opts.CorsOriginsRaw, opts.WebOrigin)
                .Select(o => o.TrimEnd('/')),
            StringComparer.OrdinalIgnoreCase);
    }

    public bool IsTrusted(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin))
        {
            return _allowed.Contains(origin.TrimEnd('/'));
        }

        var referer = request.Headers.Referer.ToString();
        if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var uri))
        {
            return _allowed.Contains(uri.GetLeftPart(UriPartial.Authority));
        }

        return false;
    }
}
