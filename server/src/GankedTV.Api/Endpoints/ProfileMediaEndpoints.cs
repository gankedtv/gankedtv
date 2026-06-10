using System.Security.Claims;
using GankedTV.Api.Auth;
using GankedTV.Api.Contracts.Users;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.Profile;
using GankedTV.Api.Validation;
using Microsoft.AspNetCore.Mvc;

namespace GankedTV.Api.Endpoints;

public static class ProfileMediaEndpoints
{
    public static IEndpointRouteBuilder MapProfileMediaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth/me").RequireAuthorization();
        group.MapPost("/avatar/upload-url", (ProfileMediaUploadUrlRequest? req, ClaimsPrincipal p, IProfileMediaService svc, CancellationToken ct) =>
                GetUploadUrl(req, p, svc, ProfileMediaKind.Avatar, ct))
            .WithValidation<ProfileMediaUploadUrlRequest>();
        group.MapPost("/avatar/complete", (ProfileMediaCompleteRequest? req, ClaimsPrincipal p, IProfileMediaService svc, CancellationToken ct) =>
                Complete(req, p, svc, ProfileMediaKind.Avatar, ct))
            .WithValidation<ProfileMediaCompleteRequest>();
        group.MapDelete("/avatar", (ClaimsPrincipal p, IProfileMediaService svc, CancellationToken ct) =>
            Delete(p, svc, ProfileMediaKind.Avatar, ct));

        group.MapPost("/banner/upload-url", (ProfileMediaUploadUrlRequest? req, ClaimsPrincipal p, IProfileMediaService svc, CancellationToken ct) =>
                GetUploadUrl(req, p, svc, ProfileMediaKind.Banner, ct))
            .WithValidation<ProfileMediaUploadUrlRequest>();
        group.MapPost("/banner/complete", (ProfileMediaCompleteRequest? req, ClaimsPrincipal p, IProfileMediaService svc, CancellationToken ct) =>
                Complete(req, p, svc, ProfileMediaKind.Banner, ct))
            .WithValidation<ProfileMediaCompleteRequest>();
        group.MapDelete("/banner", (ClaimsPrincipal p, IProfileMediaService svc, CancellationToken ct) =>
            Delete(p, svc, ProfileMediaKind.Banner, ct));

        return app;
    }

    private static async Task<IResult> GetUploadUrl(
        ProfileMediaUploadUrlRequest? req,
        ClaimsPrincipal principal,
        IProfileMediaService svc,
        ProfileMediaKind kind,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }
        if (req is null)
        {
            return ProblemResults.InvalidBody();
        }
        var result = await svc.GetUploadUrlAsync(userId, kind, req.ContentType, ct);
        return result.IsSuccess
            ? Results.Ok(new ProfileMediaUploadUrlResponse(
                result.Value!.Url,
                result.Value!.ExpiresAt,
                result.Value!.ContentType,
                result.Value!.ObjectKey))
            : MapError(result.Error!.Value);
    }

    private static async Task<IResult> Complete(
        ProfileMediaCompleteRequest? req,
        ClaimsPrincipal principal,
        IProfileMediaService svc,
        ProfileMediaKind kind,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }
        if (req is null)
        {
            return ProblemResults.InvalidBody();
        }
        var result = await svc.CompleteAsync(userId, kind, req.ObjectKey, ct);
        return result.IsSuccess
            ? Results.Ok(new ProfileMediaCompleteResponse(
                result.Value!.Url,
                result.Value!.ObjectKey,
                result.Value!.AvatarSource))
            : MapError(result.Error!.Value);
    }

    private static async Task<IResult> Delete(
        ClaimsPrincipal principal,
        IProfileMediaService svc,
        ProfileMediaKind kind,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }
        var result = await svc.DeleteAsync(userId, kind, ct);
        return result.IsSuccess
            ? Results.Ok(new ProfileMediaDeleteResponse(result.Value!.Url, result.Value!.AvatarSource))
            : MapError(result.Error!.Value);
    }

    private static IResult MapError(ProfileMediaError error) => error switch
    {
        ProfileMediaError.NotFound => ProblemResults.NotFound("not_found"),
        ProfileMediaError.UnsupportedContentType => ProblemResults.BadRequest("unsupported_content_type"),
        ProfileMediaError.ObjectNotUploaded => ProblemResults.BadRequest("object_not_uploaded"),
        ProfileMediaError.FileTooLarge => ProblemResults.BadRequest("file_too_large"),
        ProfileMediaError.InvalidObjectKey => ProblemResults.BadRequest("invalid_object_key"),
        _ => ProblemResults.Internal("unmapped_error", $"Unhandled error: {error}"),
    };
}
