using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    public DiscordOAuthProvider(IHttpClientFactory httpFactory, IOptions<OAuthOptions> options)
    {
        _httpFactory = httpFactory;
        _options = options.Value.Discord;
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
            ["prompt"] = "none",
        };
        return QueryString.Append(AuthorizeEndpoint, query);
    }

    public async Task<OAuthUserInfo> ExchangeCodeAsync(string code, string? overrideRedirectUri, CancellationToken ct)
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
            var body = await tokenResp.Content.ReadAsStringAsync(ct);
            throw new OAuthExchangeException($"Discord token exchange failed ({(int)tokenResp.StatusCode}): {body}");
        }
        var tokenBody = await tokenResp.Content.ReadFromJsonAsync<DiscordTokenResponse>(cancellationToken: ct)
            ?? throw new OAuthExchangeException("Discord token response was empty.");

        using var userReq = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenBody.AccessToken);
        using var userResp = await http.SendAsync(userReq, ct);
        if (!userResp.IsSuccessStatusCode)
        {
            var body = await userResp.Content.ReadAsStringAsync(ct);
            throw new OAuthExchangeException($"Discord userinfo failed ({(int)userResp.StatusCode}): {body}");
        }
        var user = await userResp.Content.ReadFromJsonAsync<DiscordUser>(cancellationToken: ct)
            ?? throw new OAuthExchangeException("Discord userinfo was empty.");

        var avatar = BuildAvatarUrl(user.Id, user.Avatar);
        return new OAuthUserInfo(
            ProviderUserId: user.Id,
            Email: user.Email,
            Username: user.Username,
            AvatarUrl: avatar);
    }

    private static string? BuildAvatarUrl(string id, string? hash) =>
        string.IsNullOrEmpty(hash)
            ? null
            : $"https://cdn.discordapp.com/avatars/{id}/{hash}.png";

    private sealed record DiscordTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType);

    private sealed record DiscordUser(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("avatar")] string? Avatar);
}

public sealed class OAuthExchangeException : Exception
{
    public OAuthExchangeException(string message) : base(message) { }
}

internal static class QueryString
{
    public static string Append(string baseUrl, IEnumerable<KeyValuePair<string, string?>> pairs)
    {
        var sb = new System.Text.StringBuilder(baseUrl);
        var first = true;
        foreach (var (k, v) in pairs)
        {
            if (v is null) continue;
            sb.Append(first ? '?' : '&');
            first = false;
            sb.Append(Uri.EscapeDataString(k));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(v));
        }
        return sb.ToString();
    }
}
