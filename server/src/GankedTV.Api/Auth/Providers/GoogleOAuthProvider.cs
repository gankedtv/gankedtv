using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Auth.Providers;

public sealed class GoogleOAuthProvider : IOAuthProvider
{
    public const string ProviderName = "google";
    private const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string UserInfoEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";
    private const string Scopes = "openid email profile";

    private readonly IHttpClientFactory _httpFactory;
    private readonly OAuthProviderOptions _options;
    private readonly ILogger<GoogleOAuthProvider>? _logger;

    public GoogleOAuthProvider(
        IHttpClientFactory httpFactory,
        IOptions<OAuthOptions> options,
        ILogger<GoogleOAuthProvider>? logger = null)
    {
        _httpFactory = httpFactory;
        _options = options.Value.Google;
        _logger = logger;
    }

    public string Name => ProviderName;

    public string BuildAuthorizeUrl(string state, string? overrideRedirectUri = null)
    {
        var redirect = overrideRedirectUri ?? _options.RedirectUri;
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = redirect,
            ["response_type"] = "code",
            ["scope"] = Scopes,
            ["state"] = state,
            ["access_type"] = "online",
            ["prompt"] = "select_account",
        };
        return OAuthQueryString.Append(AuthorizeEndpoint, query);
    }

    public async Task<OAuthUserInfo> ExchangeCodeAsync(string code, string? overrideRedirectUri = null, CancellationToken ct = default)
    {
        var redirect = overrideRedirectUri ?? _options.RedirectUri;
        var http = _httpFactory.CreateClient(ProviderName);

        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect,
        });

        using var tokenResp = await http.PostAsync(TokenEndpoint, tokenForm, ct);
        if (!tokenResp.IsSuccessStatusCode)
        {
            throw await BuildExchangeExceptionAsync("token exchange", tokenResp, ct);
        }
        var tokenBody = await tokenResp.Content.ReadFromJsonAsync<GoogleTokenResponse>(cancellationToken: ct)
            ?? throw new OAuthExchangeException("Google token response was empty.");

        using var userReq = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenBody.AccessToken);
        using var userResp = await http.SendAsync(userReq, ct);
        if (!userResp.IsSuccessStatusCode)
        {
            throw await BuildExchangeExceptionAsync("userinfo", userResp, ct);
        }
        var user = await userResp.Content.ReadFromJsonAsync<GoogleUser>(cancellationToken: ct)
            ?? throw new OAuthExchangeException("Google userinfo was empty.");

        var username = !string.IsNullOrWhiteSpace(user.Name)
            ? user.Name
            : EmailLocalPart(user.Email);

        return new OAuthUserInfo(
            ProviderUserId: user.Sub,
            Email: user.Email,
            Username: username,
            AvatarUrl: user.Picture,
            EmailVerified: user.EmailVerified ?? false);
    }

    private async Task<OAuthExchangeException> BuildExchangeExceptionAsync(
        string stage,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        string? parsedError = null;
        try
        {
            var err = await response.Content.ReadFromJsonAsync<OAuthErrorResponse>(cancellationToken: ct);
            parsedError = err?.Error;
        }
        catch
        {
            // Unparseable body — intentionally not included in exception message.
        }

        if (_logger is not null && _logger.IsEnabled(LogLevel.Debug))
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("Google {Stage} failed ({Status}): {Body}", stage, status, body);
        }

        return parsedError is null
            ? new OAuthExchangeException($"Google {stage} failed ({status}).")
            : new OAuthExchangeException($"Google {stage} failed ({status}): {parsedError}.");
    }

    private static string? EmailLocalPart(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }
        var at = email.IndexOf('@');
        return at <= 0 ? email : email[..at];
    }

    private sealed record GoogleTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType);

    private sealed record GoogleUser(
        [property: JsonPropertyName("sub")] string Sub,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("picture")] string? Picture,
        [property: JsonPropertyName("email_verified")] bool? EmailVerified);
}
