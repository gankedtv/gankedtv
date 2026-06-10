using System.Security.Claims;
using GankedTV.Api.Auth;
using GankedTV.Api.Auth.Cookies;
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
            // Keep OpenAPI in sync with the shapes Refresh can return. 403 covers the
            // banned-account branch (see ProblemResults.Forbidden("account_banned") in
            // the handler); 401 covers invalid_refresh.
            .Produces<TokenResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        app.MapPost("/auth/logout", Logout)
            .Produces(StatusCodes.Status204NoContent)
            // 403 covers the CSRF origin check on cookie-sourced tokens.
            .ProducesProblem(StatusCodes.Status403Forbidden);

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
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            // Banned accounts get a 403 + code=account_banned even after credentials check
            // out, so the SPA can render a dedicated "your account is disabled" message
            // rather than the generic invalid-credentials copy.
            .ProducesProblem(StatusCodes.Status403Forbidden);

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
        IRefreshCookieService refreshCookies,
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
        if (user.BannedAt is not null)
        {
            // OAuth round-trips already burned the authorization code, so failing here means
            // the user has to start sign-in from scratch — that's intended. Sending a redirect
            // with the error code lets the SPA surface a banned-account screen instead of a
            // generic OAuth-failed toast.
            var webOriginBanned = oauthOptions.Value.WebOrigin.TrimEnd('/');
            return Results.Redirect($"{webOriginBanned}/auth/callback?error=account_banned");
        }
        var token = jwt.Issue(user);
        var refresh = await refreshTokens.IssueAsync(user.Id, ct);

        var webOrigin = oauthOptions.Value.WebOrigin.TrimEnd('/');
        var returnTo = stateResult.ReturnTo;
        var location = $"{webOrigin}/auth/callback?token={Uri.EscapeDataString(token)}";
        if (refreshCookies.Enabled)
        {
            // The redirect is a top-level navigation on the API origin, so Set-Cookie works
            // here. Keeping the refresh token out of the redirect URL keeps it out of
            // browser history, proxy logs, and Referer headers.
            refreshCookies.Append(http.Response, refresh);
        }
        else
        {
            location += $"&refresh={Uri.EscapeDataString(refresh)}";
        }
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
        HttpContext http,
        CredentialAuthService credentials,
        IJwtService jwt,
        IRefreshTokenService refreshTokens,
        IRefreshCookieService refreshCookies,
        IOptions<JwtOptions> jwtOptions,
        CancellationToken ct)
    {
        var result = await credentials.TryRegisterAsync(req!.Email, req.Username, req.Password, ct);
        return result switch
        {
            RegisterResult.SuccessResult ok => await IssueTokenResponseAsync(
                ok.User, http, jwt, refreshTokens, refreshCookies, jwtOptions, ct),
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
        HttpContext http,
        CredentialAuthService credentials,
        IJwtService jwt,
        IRefreshTokenService refreshTokens,
        IRefreshCookieService refreshCookies,
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
        if (user.BannedAt is not null)
        {
            // Bypass the generic-401 collapse: the user authenticated, so leaking the ban
            // signal is intended — the SPA needs to render a "your account has been disabled"
            // screen instead of "wrong password".
            return ProblemResults.Forbidden("account_banned");
        }

        return await IssueTokenResponseAsync(user, http, jwt, refreshTokens, refreshCookies, jwtOptions, ct);
    }

    private static async Task<IResult> SetPassword(
        [FromBody] SetPasswordRequest? req,
        ClaimsPrincipal principal,
        CredentialAuthService credentials,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
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
        HttpContext http,
        IJwtService jwt,
        IRefreshTokenService refreshTokens,
        IRefreshCookieService refreshCookies,
        IOptions<JwtOptions> jwtOptions,
        CancellationToken ct)
    {
        var token = jwt.Issue(user);
        var refresh = await refreshTokens.IssueAsync(user.Id, ct);
        if (refreshCookies.Enabled)
        {
            // Cookie mode: the refresh token must never reach script-readable space, so the
            // JSON body carries an empty refresh field and the HttpOnly cookie is the only
            // copy — otherwise an XSS payload could exfiltrate the long-lived credential.
            refreshCookies.Append(http.Response, refresh);
            return Results.Ok(new TokenResponse(token, "", jwtOptions.Value.ExpiryMinutes * 60));
        }
        return Results.Ok(new TokenResponse(token, refresh, jwtOptions.Value.ExpiryMinutes * 60));
    }

    private static async Task<IResult> Refresh(
        // Nullable so a literal JSON `null` body reaches the ValidationEndpointFilter.
        [FromBody] RefreshRequest? req,
        HttpContext http,
        IJwtService jwt,
        IRefreshTokenService refreshTokens,
        IRefreshCookieService refreshCookies,
        ITrustedOriginValidator origins,
        IOptions<JwtOptions> jwtOptions,
        CancellationToken ct)
    {
        // Defensive: the WithValidation<RefreshRequest> filter returns 400 for null bodies.
        if (req is null)
        {
            return ProblemResults.InvalidBody();
        }

        // Body first, cookie fallback. A body token is an explicit, script-initiated call
        // (localStorage mode) and needs no CSRF check; the cookie travels automatically on
        // any cross-site POST, so cookie-sourced tokens require a trusted Origin/Referer.
        var raw = req.Refresh;
        var fromCookie = false;
        if (string.IsNullOrEmpty(raw) && refreshCookies.Enabled)
        {
            raw = refreshCookies.Read(http.Request);
            fromCookie = raw is not null;
        }
        if (string.IsNullOrEmpty(raw))
        {
            return ProblemResults.Unauthorized("invalid_refresh");
        }
        if (fromCookie && !origins.IsTrusted(http.Request))
        {
            return ProblemResults.Forbidden("csrf_origin_rejected");
        }

        try
        {
            var result = await refreshTokens.RotateAsync(raw, ct);
            var token = jwt.Issue(result.User);
            if (refreshCookies.Enabled)
            {
                refreshCookies.Append(http.Response, result.NewRawToken);
                return Results.Ok(new TokenResponse(token, "", jwtOptions.Value.ExpiryMinutes * 60));
            }
            return Results.Ok(result.ToTokenResponse(
                token,
                jwtOptions.Value.ExpiryMinutes * 60));
        }
        catch (BannedAccountException)
        {
            // RotateAsync revoked the old token (breaking the refresh chain — security
            // positive for a banned account) but did NOT insert a successor row. A banned
            // client polling this endpoint can no longer drive write amplification on the
            // refresh_tokens table.
            return ProblemResults.Forbidden("account_banned");
        }
        catch (InvalidRefreshTokenException)
        {
            return ProblemResults.Unauthorized("invalid_refresh");
        }
    }

    private static async Task<IResult> Logout(
        // Nullable so an empty body is valid — cookie-mode clients send nothing.
        [FromBody] LogoutRequest? req,
        HttpContext http,
        IRefreshTokenService refreshTokens,
        IRefreshCookieService refreshCookies,
        ITrustedOriginValidator origins,
        CancellationToken ct)
    {
        var raw = req?.Refresh;
        var fromCookie = false;
        if (string.IsNullOrEmpty(raw) && refreshCookies.Enabled)
        {
            raw = refreshCookies.Read(http.Request);
            fromCookie = raw is not null;
        }
        // Same CSRF rule as Refresh. A forged logout is "only" a nuisance, but family
        // revocation is a write — don't let hostile pages trigger it.
        if (fromCookie && !origins.IsTrusted(http.Request))
        {
            return ProblemResults.Forbidden("csrf_origin_rejected");
        }

        if (!string.IsNullOrEmpty(raw))
        {
            await refreshTokens.RevokeFamilyAsync(raw, ct);
        }
        if (refreshCookies.Enabled)
        {
            refreshCookies.Clear(http.Response);
        }
        // 204 regardless of whether the token was known — logout is idempotent and the
        // response must not leak token validity.
        return Results.NoContent();
    }
}
