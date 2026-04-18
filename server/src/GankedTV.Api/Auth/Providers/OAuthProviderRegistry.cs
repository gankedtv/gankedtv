using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Auth.Providers;

public sealed class OAuthProviderRegistry
{
    private readonly Dictionary<string, IOAuthProvider> _providers;

    public OAuthProviderRegistry(IEnumerable<IOAuthProvider> providers, IOptions<OAuthOptions> options)
    {
        var opts = options.Value;
        _providers = providers
            .Where(p => IsConfigured(p.Name, opts))
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGet(string name, [NotNullWhen(true)] out IOAuthProvider? provider) =>
        _providers.TryGetValue(name, out provider);

    public IReadOnlyCollection<string> ConfiguredProviderNames => _providers.Keys;

    private static bool IsConfigured(string name, OAuthOptions opts) => name switch
    {
        DiscordOAuthProvider.ProviderName => opts.Discord.IsConfigured,
        GoogleOAuthProvider.ProviderName => opts.Google.IsConfigured,
        _ => false,
    };
}
