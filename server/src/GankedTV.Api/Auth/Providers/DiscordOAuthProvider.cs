using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        // Read the body once — HttpContent streams can only be consumed once unless buffered,
        // so we materialise to a string and reuse it for both parsing and (optional) debug log.
        var body = await response.Content.ReadAsStringAsync(ct);

        // Discord's OAuth2 errors use {error, error_description}; its REST errors (e.g. the
        // userinfo endpoint) use {message, code}. Try both shapes and surface the first hit.
        var detail = TryParseErrorDetail(body);

        // Full body at Debug for operators; never in the exception message (would bleed into
        // API responses / logs of downstream callers).
        if (_logger is not null && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Discord {Stage} failed ({Status}): {Body}", stage, status, body);
        }

        return detail is null
            ? new OAuthExchangeException($"Discord {stage} failed ({status}).")
            : new OAuthExchangeException($"Discord {stage} failed ({status}): {detail}.");
    }

    private static string? TryParseErrorDetail(string body)
    {
        try
        {
            var oauth = JsonSerializer.Deserialize<OAuthErrorResponse>(body);
            if (!string.IsNullOrEmpty(oauth?.Error))
            {
                return string.IsNullOrEmpty(oauth.ErrorDescription)
                    ? oauth.Error
                    : $"{oauth.Error} ({oauth.ErrorDescription})";
            }
        }
        catch (JsonException) { /* not the OAuth error shape */ }

        try
        {
            var rest = JsonSerializer.Deserialize<DiscordRestError>(body);
            if (!string.IsNullOrEmpty(rest?.Message))
            {
                return rest.Code is not null
                    ? $"{rest.Message} (code {rest.Code})"
                    : rest.Message;
            }
        }
        catch (JsonException) { /* not the REST error shape either */ }

        return null;
    }

    private sealed record DiscordRestError(
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("code")] int? Code);

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
