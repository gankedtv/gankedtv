using StackExchange.Redis;

namespace GankedTV.Api.Services.Caching;

/// <summary>
/// Redis connection config for the shared cache (hot feeds + trending) and cluster-wide
/// rate limiting. Bound in Program.cs from the <c>REDIS_URL</c> env var. OPTIONAL: when
/// <see cref="Url"/> is unset the API runs without Redis — an in-process L1 cache and
/// per-instance rate limiters take over (mirrors the IGDB-optional pattern). Never 500s
/// on a missing/unreachable Redis.
/// </summary>
public sealed class RedisOptions
{
    /// <summary>
    /// Connection string in URL form: <c>redis://[:password@]host:port</c> (or
    /// <c>rediss://</c> for TLS). StackExchange.Redis doesn't parse this scheme natively,
    /// so <see cref="TryBuildConfiguration"/> translates it.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>Whether Redis-backed features are wired (vs. the in-process fallback).</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url);

    /// <summary>
    /// Translate <see cref="Url"/> into StackExchange.Redis <see cref="ConfigurationOptions"/>.
    /// Returns false (rather than throwing) on a malformed URL so a typo degrades to the
    /// in-process fallback instead of crashing startup. <c>AbortOnConnectFail = false</c> so
    /// a Redis that's down at boot never hangs/throws — the multiplexer reconnects in the
    /// background and the cache/limiter degrade gracefully until it does.
    /// </summary>
    public bool TryBuildConfiguration(out ConfigurationOptions configuration)
    {
        configuration = new ConfigurationOptions { AbortOnConnectFail = false };

        if (!IsConfigured || !Uri.TryCreate(Url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var isTls = string.Equals(uri.Scheme, "rediss", StringComparison.OrdinalIgnoreCase);
        if (!isTls && !string.Equals(uri.Scheme, "redis", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var port = uri.Port > 0 ? uri.Port : 6379;
        configuration.EndPoints.Add(uri.Host, port);
        configuration.Ssl = isTls;

        // userinfo carries credentials: redis://:pw@host (password only) or redis://user:pw@host
        // (ACL username + password). Preserve both so Redis 6+ ACL auth works, not just the
        // legacy password-only AUTH.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            if (parts.Length == 2)
            {
                if (parts[0].Length > 0)
                {
                    configuration.User = Uri.UnescapeDataString(parts[0]);
                }
                configuration.Password = Uri.UnescapeDataString(parts[1]);
            }
            else
            {
                configuration.Password = Uri.UnescapeDataString(parts[0]);
            }
        }

        return true;
    }
}
