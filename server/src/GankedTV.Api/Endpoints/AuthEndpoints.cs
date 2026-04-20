using GankedTV.Api.Auth;
using GankedTV.Api.Auth.Jwt;
using GankedTV.Api.Auth.Providers;
using GankedTV.Api.Auth.State;
using GankedTV.Api.Auth.Tokens;
using GankedTV.Api.Contracts.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/providers", ListProviders);
        app.MapGet("/auth/{provider}/start", Start);
        app.MapGet("/auth/{provider}/callback", Callback);
        app.MapPost("/auth/refresh", Refresh);
        return app;
    }

    private static IResult ListProviders(OAuthProviderRegistry registry) =>
        Results.Ok(new { providers = registry.ConfiguredProviderNames });

    private static IResult Start(
        string provider,
        string? returnTo,
        HttpContext http,
        OAuthProviderRegistry registry,
        IStateCookieService stateCookies,
        IWebHostEnvironment env)
    {
        if (!registry.TryGet(provider, out var oauth))
        {
            return Results.NotFound(new { error = "unknown_provider" });
        }

        var state = stateCookies.IssueState(returnTo);
        http.Response.Cookies.Append(StateCookieService.CookieName, state, new CookieOptions
        {
            HttpOnly = true,
            Secure = !env.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            MaxAge = StateCookieService.CookieTtl,
            Path = "/auth",
        });

        return Results.Redirect(oauth.BuildAuthorizeUrl(state));
    }

    private static async Task<IResult> Callback(
        string provider,
        string? code,
        string? state,
        HttpContext http,
        OAuthProviderRegistry registry,
        IStateCookieService stateCookies,
        IJwtService jwt,
        IRefreshTokenService refreshTokens,
        UserUpsertService users,
        IOptions<JwtOptions> jwtOptions,
        IOptions<OAuthOptions> oauthOptions,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return Results.BadRequest(new { error = "missing_code_or_state" });
        }

        if (!registry.TryGet(provider, out var oauth))
        {
            return Results.NotFound(new { error = "unknown_provider" });
        }

        var cookie = http.Request.Cookies[StateCookieService.CookieName];
        http.Response.Cookies.Delete(StateCookieService.CookieName, new CookieOptions { Path = "/auth" });

        var stateResult = stateCookies.ValidateState(state, cookie);
        if (!stateResult.Ok)
        {
            return Results.BadRequest(new { error = "invalid_state" });
        }

        OAuthUserInfo info;
        try
        {
            info = await oauth.ExchangeCodeAsync(code, overrideRedirectUri: null, ct);
        }
        catch (OAuthExchangeException ex)
        {
            return Results.BadRequest(new { error = "oauth_exchange_failed", message = ex.Message });
        }

        var user = await users.UpsertFromOAuthAsync(oauth.Name, info, ct);
        var token = jwt.Issue(user);
        var refresh = await refreshTokens.IssueAsync(user.Id, ct);

        var webOrigin = oauthOptions.Value.WebOrigin.TrimEnd('/');
        var returnTo = stateResult.ReturnTo;
        var location = $"{webOrigin}/auth/callback?token={Uri.EscapeDataString(token)}&refresh={Uri.EscapeDataString(refresh)}";
        if (!string.IsNullOrEmpty(returnTo))
        {
            location += $"&returnTo={Uri.EscapeDataString(returnTo)}";
        }
        return Results.Redirect(location);
    }

    private static async Task<Results<Ok<TokenResponse>, UnauthorizedHttpResult, BadRequest>> Refresh(
        [FromBody] RefreshRequest req,
        IJwtService jwt,
        IRefreshTokenService refreshTokens,
        IOptions<JwtOptions> jwtOptions,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrEmpty(req.Refresh))
        {
            return TypedResults.BadRequest();
        }

        try
        {
            var result = await refreshTokens.RotateAsync(req.Refresh, ct);
            var token = jwt.Issue(result.User);
            return TypedResults.Ok(result.ToTokenResponse(
                token,
                jwtOptions.Value.ExpiryMinutes * 60));
        }
        catch (InvalidRefreshTokenException)
        {
            return TypedResults.Unauthorized();
        }
    }
}
