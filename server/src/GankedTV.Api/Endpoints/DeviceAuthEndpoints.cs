using System.Security.Claims;
using GankedTV.Api.Auth;
using GankedTV.Api.Auth.Devices;
using GankedTV.Api.Contracts.Devices;
using GankedTV.Api.Problems;
using GankedTV.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Endpoints;

public static class DeviceAuthEndpoints
{
    private const int MaxClientNameLength = 100;

    public static IEndpointRouteBuilder MapDeviceAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Client-facing (anonymous) — the desktop app has no credential yet. Rate-limited per IP.
        app.MapPost("/auth/device", Start)
            .RequireRateLimiting(DeviceRateLimiting.DevicePolicy);
        app.MapPost("/auth/device/token", Poll)
            .WithValidation<DeviceTokenRequest>()
            .RequireRateLimiting(DeviceRateLimiting.DevicePolicy);

        // Browser-facing approval surface — interactive (JWT/cookie) auth only, like the
        // api-keys management group, so a leaked API key can't approve device requests.
        app.MapGet("/me/device/{userCode}", Lookup).RequireAuthorization(AuthPolicies.Interactive);
        app.MapPost("/me/device/approve", Approve)
            .RequireAuthorization(AuthPolicies.Interactive)
            .WithValidation<DeviceDecisionRequest>();
        app.MapPost("/me/device/deny", Deny)
            .RequireAuthorization(AuthPolicies.Interactive)
            .WithValidation<DeviceDecisionRequest>();
        return app;
    }

    private static async Task<IResult> Start(
        // Optional body: a client may POST nothing or `{}` to omit the label.
        [FromBody] DeviceStartRequest? req,
        DeviceAuthorizationService devices,
        IOptions<OAuthOptions> oauth,
        CancellationToken ct)
    {
        if (req?.ClientName is { Length: > MaxClientNameLength })
        {
            return ProblemResults.BadRequest("invalid_client_name", $"clientName must be at most {MaxClientNameLength} characters.");
        }

        var result = await devices.StartAsync(req?.ClientName, ct);

        var webOrigin = oauth.Value.WebOrigin.TrimEnd('/');
        var display = DeviceAuthorizationService.FormatUserCode(result.UserCode);
        var verificationUri = $"{webOrigin}/device";
        return Results.Ok(new DeviceStartResponse(
            result.DeviceCode,
            display,
            verificationUri,
            $"{verificationUri}?code={Uri.EscapeDataString(display)}",
            (int)DeviceAuthorizationService.Lifetime.TotalSeconds,
            result.IntervalSeconds));
    }

    private static async Task<IResult> Poll(
        [FromBody] DeviceTokenRequest? req,
        DeviceAuthorizationService devices,
        CancellationToken ct)
    {
        if (req is null)
        {
            return ProblemResults.InvalidBody();
        }

        var result = await devices.PollAsync(req.DeviceCode, ct);
        return result.Status switch
        {
            // "Bearer": the minted key is sent as `Authorization: Bearer <key>`, so the canonical
            // OAuth token type is the self-documenting, RFC-conformant value here.
            DevicePollStatus.Approved => Results.Ok(new DeviceTokenResponse(result.ApiKey!, "Bearer")),
            DevicePollStatus.Pending => ProblemResults.BadRequest("authorization_pending", "The user has not approved the request yet."),
            DevicePollStatus.SlowDown => ProblemResults.BadRequest("slow_down", "Polling too frequently; increase the interval."),
            DevicePollStatus.Denied => ProblemResults.BadRequest("access_denied", "The user denied the request."),
            DevicePollStatus.Expired => ProblemResults.BadRequest("expired_token", "The device code has expired; start over."),
            DevicePollStatus.TooManyKeys => ProblemResults.Conflict("too_many_keys", "You have too many active keys; revoke one and try again."),
            _ => ProblemResults.Internal("unmapped_error"),
        };
    }

    private static async Task<IResult> Lookup(
        string userCode,
        DeviceAuthorizationService devices,
        CancellationToken ct)
    {
        var result = await devices.LookupByUserCodeAsync(userCode, ct);
        return result is null
            ? ProblemResults.NotFound("not_found")
            : Results.Ok(new DeviceLookupResponse(result.ClientName, result.Status));
    }

    private static Task<IResult> Approve(
        [FromBody] DeviceDecisionRequest? req, ClaimsPrincipal principal, DeviceAuthorizationService devices, CancellationToken ct) =>
        DecideAsync(req, principal, devices, approve: true, ct);

    private static Task<IResult> Deny(
        [FromBody] DeviceDecisionRequest? req, ClaimsPrincipal principal, DeviceAuthorizationService devices, CancellationToken ct) =>
        DecideAsync(req, principal, devices, approve: false, ct);

    private static async Task<IResult> DecideAsync(
        DeviceDecisionRequest? req, ClaimsPrincipal principal, DeviceAuthorizationService devices, bool approve, CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }
        if (req is null)
        {
            return ProblemResults.InvalidBody();
        }

        var outcome = approve
            ? await devices.ApproveAsync(userId, req.UserCode, ct)
            : await devices.DenyAsync(userId, req.UserCode, ct);

        return outcome switch
        {
            DeviceDecisionOutcome.Ok => Results.NoContent(),
            DeviceDecisionOutcome.NotFound => ProblemResults.NotFound("not_found"),
            DeviceDecisionOutcome.AlreadyDecided => ProblemResults.Conflict("already_decided", "This request was already approved or denied."),
            _ => ProblemResults.Internal("unmapped_error"),
        };
    }
}
