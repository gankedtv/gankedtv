using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GankedTV.Api.Auth;
using GankedTV.Api.Auth.Jwt;
using GankedTV.Api.Auth.Providers;
using GankedTV.Api.Auth.State;
using GankedTV.Api.Auth.Tokens;
using GankedTV.Api.Contracts.Auth;
using GankedTV.Api.Problems;
using GankedTV.Api.Validation;
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
        app.MapPost("/auth/refresh", Refresh)
            .WithValidation<RefreshRequest>()
            // Keep OpenAPI in sync with the three shapes Refresh can return. Moved onto the
            // route-group call because the handler now returns IResult (needed to return a
            // ProblemDetails body on 401 via ProblemResults.Unauthorized).
            .Produces<TokenResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        app.MapPost("/auth/register", Register)
            .WithValidation<RegisterRequest>()
            .RequireRateLimiting(AuthRateLimiting.CredentialsPolicy)
            .Produces<TokenResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapPost("/auth/login", Login)
            .WithValidation<LoginRequest>()
            .RequireRateLimiting(AuthRateLimiting.CredentialsPolicy)
            .Produces<TokenResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        app.MapPost("/auth/password", SetPassword)
            .RequireAuthorization()
            .WithValidation<SetPasswordRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

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
            return ProblemResults.NotFound("unknown_provider");
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
            return ProblemResults.BadRequest("missing_code_or_state");
        }

        if (!registry.TryGet(provider, out var oauth))
        {
            return ProblemResults.NotFound("unknown_provider");
        }

        var cookie = http.Request.Cookies[StateCookieService.CookieName];
        http.Response.Cookies.Delete(StateCookieService.CookieName, new CookieOptions { Path = "/auth" });

        var stateResult = stateCookies.ValidateState(state, cookie);
        if (!stateResult.Ok)
        {
            return ProblemResults.BadRequest("invalid_state");
        }

        OAuthUserInfo info;
        try
        {
            info = await oauth.ExchangeCodeAsync(code, overrideRedirectUri: null, ct);
        }
        catch (OAuthExchangeException ex)
        {
            return ProblemResults.BadRequest("oauth_exchange_failed", ex.Message);
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

    // The `[FromBody]` parameters are nullable so a literal JSON `null` reaches the
    // ValidationEndpointFilter (matching the Refresh handler's convention). The filter
    // returns 400 InvalidBody for both nulls and validation failures, so the handler
    // body can treat the request as effectively non-null.
    private static async Task<IResult> Register(
        [FromBody] RegisterRequest? req,
        CredentialAuthService credentials,
        IJwtService jwt,
        IRefreshTokenService refreshTokens,
        IOptions<JwtOptions> jwtOptions,
        CancellationToken ct)
    {
        var result = await credentials.TryRegisterAsync(req!.Email, req.Username, req.Password, ct);
        return result switch
        {
            RegisterResult.SuccessResult ok => await IssueTokenResponseAsync(ok.User, jwt, refreshTokens, jwtOptions, ct),
            // 409 with a code the SPA can branch on to nudge users into the OAuth-then-attach
            // flow. Account-takeover on a verified-email OAuth account would otherwise be
            // possible without an email-verification step (deliberately deferred).
            RegisterResult.EmailTakenResult => ProblemResults.Conflict(
                "email_taken",
                "An account with this email already exists. Sign in with your existing method, then attach a password from your profile."),
            RegisterResult.InvalidPasswordResult bad => ProblemResults.BadRequest("weak_password", bad.Error),
            // [EmailAddress] on the DTO catches malformed emails before the handler runs.
            // Any non-success result that isn't email-taken / weak-password is therefore an
            // invalid email — collapse into the same 400 instead of a separate switch arm.
            _ => ProblemResults.BadRequest("invalid_email"),
        };
    }

    private static async Task<IResult> Login(
        [FromBody] LoginRequest? req,
        CredentialAuthService credentials,
        IJwtService jwt,
        IRefreshTokenService refreshTokens,
        IOptions<JwtOptions> jwtOptions,
        CancellationToken ct)
    {
        var user = await credentials.TryLoginAsync(req!.Email, req.Password, ct);
        if (user is null)
        {
            // Generic 401 so attackers can't distinguish "no such user", "no password set",
            // and "wrong password". TryLoginAsync runs a constant-time-equivalent dummy
            // verify in the missing-user / no-password paths to keep the timing flat.
            return ProblemResults.Unauthorized("invalid_credentials");
        }

        return await IssueTokenResponseAsync(user, jwt, refreshTokens, jwtOptions, ct);
    }

    private static async Task<IResult> SetPassword(
        [FromBody] SetPasswordRequest? req,
        ClaimsPrincipal principal,
        CredentialAuthService credentials,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Results.Unauthorized();
        }

        var result = await credentials.SetPasswordAsync(userId, req!.CurrentPassword, req.NewPassword, ct);
        return result switch
        {
            SetPasswordResult.OkResult => Results.NoContent(),
            SetPasswordResult.WrongCurrentPasswordResult => ProblemResults.BadRequest("wrong_current_password"),
            SetPasswordResult.InvalidPasswordResult bad => ProblemResults.BadRequest("weak_password", bad.Error),
            // JWT sub points at a user that no longer exists — same shape MeEndpoints uses
            // for the analogous case. Any unhandled subtype falls through to the same 401.
            _ => Results.Unauthorized(),
        };
    }

    private static async Task<IResult> IssueTokenResponseAsync(
        Data.Entities.User user,
        IJwtService jwt,
        IRefreshTokenService refreshTokens,
        IOptions<JwtOptions> jwtOptions,
        CancellationToken ct)
    {
        var token = jwt.Issue(user);
        var refresh = await refreshTokens.IssueAsync(user.Id, ct);
        return Results.Ok(new TokenResponse(token, refresh, jwtOptions.Value.ExpiryMinutes * 60));
    }

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        userId = default;
        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out userId);
    }

    private static async Task<IResult> Refresh(
        // Nullable so a literal JSON `null` body reaches the ValidationEndpointFilter.
        [FromBody] RefreshRequest? req,
        IJwtService jwt,
        IRefreshTokenService refreshTokens,
        IOptions<JwtOptions> jwtOptions,
        CancellationToken ct)
    {
        // Defensive: the WithValidation<RefreshRequest> filter returns 400 for null bodies.
        if (req is null)
        {
            return ProblemResults.InvalidBody();
        }

        try
        {
            var result = await refreshTokens.RotateAsync(req.Refresh, ct);
            var token = jwt.Issue(result.User);
            return Results.Ok(result.ToTokenResponse(
                token,
                jwtOptions.Value.ExpiryMinutes * 60));
        }
        catch (InvalidRefreshTokenException)
        {
            return ProblemResults.Unauthorized("invalid_refresh");
        }
    }
}
