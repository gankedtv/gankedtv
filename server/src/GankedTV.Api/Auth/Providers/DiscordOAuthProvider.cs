using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Auth.Providers;

public sealed class DiscordOAuthProvider : IOAuthProvider
{
    public const string ProviderName = "discord";
    private const string AuthorizeEndpoint = "https://discord.com/oauth2/authorize";
    private const string TokenEndpoint = "https://discord.com/api/oauth2/token";
    private const string UserInfoEndpoint = "https://discord.com/api/users/@me";
    private const string Scopes = "identify email";

    private readonly IHttpClientFactory _httpFactory;
    private readonly OAuthProviderOptions _options;
    private readonly ILogger<DiscordOAuthProvider>? _logger;

    public DiscordOAuthProvider(
        IHttpClientFactory httpFactory,
        IOptions<OAuthOptions> options,
        ILogger<DiscordOAuthProvider>? logger = null)
    {
        _httpFactory = httpFactory;
        _options = options.Value.Discord;
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
        var tokenBody = await tokenResp.Content.ReadFromJsonAsync<DiscordTokenResponse>(cancellationToken: ct)
            ?? throw new OAuthExchangeException("Discord token response was empty.");

        using var userReq = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenBody.AccessToken);
        using var userResp = await http.SendAsync(userReq, ct);
        if (!userResp.IsSuccessStatusCode)
        {
            throw await BuildExchangeExceptionAsync("userinfo", userResp, ct);
        }
        var user = await userResp.Content.ReadFromJsonAsync<DiscordUser>(cancellationToken: ct)
            ?? throw new OAuthExchangeException("Discord userinfo was empty.");

        return new OAuthUserInfo(
            ProviderUserId: user.Id,
            Email: user.Email,
            Username: user.Username,
            AvatarUrl: BuildAvatarUrl(user.Id, user.Avatar),
            EmailVerified: user.Verified ?? false);
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
            // Unparseable body — we intentionally do not include it in the exception message.
        }

        // Full body at Debug for operators; never in the exception message (would bleed into
        // API responses / logs of downstream callers).
        if (_logger is not null && _logger.IsEnabled(LogLevel.Debug))
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("Discord {Stage} failed ({Status}): {Body}", stage, status, body);
        }

        return parsedError is null
            ? new OAuthExchangeException($"Discord {stage} failed ({status}).")
            : new OAuthExchangeException($"Discord {stage} failed ({status}): {parsedError}.");
    }

    private static string? BuildAvatarUrl(string id, string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return null;
        }
        // Discord prefixes animated avatar hashes with "a_"; those must be served as .gif.
        var ext = hash.StartsWith("a_", StringComparison.Ordinal) ? "gif" : "png";
        return $"https://cdn.discordapp.com/avatars/{id}/{hash}.{ext}";
    }

    private sealed record DiscordTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType);

    private sealed record DiscordUser(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("avatar")] string? Avatar,
        [property: JsonPropertyName("verified")] bool? Verified);
}

public sealed class OAuthExchangeException : Exception
{
    public OAuthExchangeException(string message) : base(message) { }
}

internal sealed record OAuthErrorResponse(
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);
