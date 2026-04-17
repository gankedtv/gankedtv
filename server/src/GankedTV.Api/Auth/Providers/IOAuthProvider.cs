namespace GankedTV.Api.Auth.Providers;

public interface IOAuthProvider
{
    string Name { get; }
    string BuildAuthorizeUrl(string state, string? overrideRedirectUri = null);
    Task<OAuthUserInfo> ExchangeCodeAsync(string code, string? overrideRedirectUri = null, CancellationToken ct = default);
}
