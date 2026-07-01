using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Auth.ApiKeys;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    // Raw keys arrive as `Authorization: Bearer <key>` (rewynd's preferred format) or via
    // the X-Api-Key header. Bearer keys are told apart from JWTs by the gtv_ prefix.
    public const string BearerPrefix = "Bearer ";

    // Scheme forwarder used by the "smart" default authentication scheme: API-key requests
    // (X-Api-Key header, or an Authorization: Bearer credential carrying the gtv_ prefix)
    // resolve to the ApiKey handler; everything else (browser JWTs) to the bearer handler. A
    // JWT never starts with gtv_, so the two Bearer credential types can't be confused. Lives
    // here rather than inline in Program.cs so the routing logic stays inside the test-coverage
    // denominator (Program.cs is excluded per CLAUDE.md).
    public static string SelectScheme(HttpRequest request)
    {
        if (request.Headers.ContainsKey(HeaderName))
        {
            return Scheme;
        }
        var auth = request.Headers.Authorization.ToString();
        return auth.StartsWith(BearerPrefix + ApiKeyService.KeyPrefix, StringComparison.Ordinal)
            ? Scheme
            : JwtBearerDefaults.AuthenticationScheme;
    }
}

// Authenticates API-key requests into a JWT-shaped principal (sub/name/role via UserClaims),
// so every downstream reader — TryGetUserId, the role policies, and the rate-limiter partition
// — treats a key-authenticated caller identically to a JWT-authenticated one.
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly ApiKeyService _apiKeys;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiKeyService apiKeys)
        : base(options, logger, encoder)
    {
        _apiKeys = apiKeys;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var rawKey = ExtractKey();
        if (rawKey is null)
        {
            // No key on this request — stay anonymous so RequireAuthorization issues the 401
            // (or another scheme can handle it), rather than emitting a hard failure.
            return AuthenticateResult.NoResult();
        }

        var user = await _apiKeys.AuthenticateAsync(rawKey, Context.RequestAborted);
        if (user is null)
        {
            return AuthenticateResult.Fail("Invalid, revoked, or expired API key.");
        }

        var identity = new ClaimsIdentity(UserClaims.Build(user), Scheme.Name, JwtClaimsNameType, JwtClaimsRoleType);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    private string? ExtractKey()
    {
        if (Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var headerValue))
        {
            var key = headerValue.ToString();
            return string.IsNullOrEmpty(key) ? null : key;
        }

        var auth = Request.Headers.Authorization.ToString();
        if (auth.StartsWith(ApiKeyDefaults.BearerPrefix, StringComparison.Ordinal))
        {
            var token = auth[ApiKeyDefaults.BearerPrefix.Length..];
            if (token.StartsWith(ApiKeyService.KeyPrefix, StringComparison.Ordinal))
            {
                return token;
            }
        }
        return null;
    }

    // Match the JwtBearer principal shape: names are the "name" claim (NameClaimType) and roles
    // the "role" claim — MapInboundClaims=false keeps JWTs on the same short claim types.
    private const string JwtClaimsNameType = "name";
    private const string JwtClaimsRoleType = "role";
}
