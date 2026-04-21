using GankedTV.Api.Auth.Providers;

namespace GankedTV.Api.Tests.TestSupport;

/// A pretend IOAuthProvider for integration-testing AuthEndpoints.Callback without real HTTP.
/// BuildAuthorizeUrl returns a deterministic URL containing the state so tests can extract it
/// from the Location header of /auth/{name}/start.
public sealed class FakeOAuthProvider : IOAuthProvider
{
    private readonly Func<string, string?, CancellationToken, Task<OAuthUserInfo>> _exchange;

    public FakeOAuthProvider(
        string name,
        Func<string, string?, CancellationToken, Task<OAuthUserInfo>> exchange)
    {
        Name = name;
        _exchange = exchange;
    }

    public string Name { get; }

    public string BuildAuthorizeUrl(string state, string? overrideRedirectUri = null) =>
        $"https://fake-{Name}.invalid/authorize?state={Uri.EscapeDataString(state)}";

    public Task<OAuthUserInfo> ExchangeCodeAsync(string code, string? overrideRedirectUri = null, CancellationToken ct = default) =>
        _exchange(code, overrideRedirectUri, ct);

    public static FakeOAuthProvider Returning(string name, OAuthUserInfo info) =>
        new(name, (_, _, _) => Task.FromResult(info));

    public static FakeOAuthProvider Throwing(string name, OAuthExchangeException ex) =>
        new(name, (_, _, _) => Task.FromException<OAuthUserInfo>(ex));
}
