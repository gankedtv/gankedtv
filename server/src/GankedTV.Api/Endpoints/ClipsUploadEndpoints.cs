using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GankedTV.Api.Services.Clips;
using Microsoft.AspNetCore.Mvc;

namespace GankedTV.Api.Endpoints;

public static class ClipsUploadEndpoints
{
    public sealed record CreateClipRequest(
        string? Title,
        string? Description,
        int? GameId,
        string? Visibility);

    public sealed record CreateClipResponse(Guid Id);
    public sealed record UploadUrlResponse(string Url, DateTimeOffset ExpiresAt);
    public sealed record CompleteClipResponse(Guid Id, long FileSizeBytes);

    public static IEndpointRouteBuilder MapClipsUploadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/clips").RequireAuthorization();
        group.MapPost("/", CreateClip);
        group.MapPost("/{id:guid}/upload-url", GetUploadUrl);
        group.MapPost("/{id:guid}/complete", CompleteClip);
        return app;
    }

    private static async Task<IResult> CreateClip(
        [FromBody] CreateClipRequest req,
        ClaimsPrincipal principal,
        IClipUploadService clips,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Results.Unauthorized();
        }

        var result = await clips.CreateAsync(
            userId,
            new CreateClipInput(req.Title, req.Description, req.GameId, req.Visibility),
            ct);

        return result.IsSuccess
            ? Results.Ok(new CreateClipResponse(result.Value!.ClipId))
            : MapError(result.Error!.Value);
    }

    private static async Task<IResult> GetUploadUrl(
        Guid id,
        ClaimsPrincipal principal,
        IClipUploadService clips,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Results.Unauthorized();
        }

        var result = await clips.GetUploadUrlAsync(userId, id, ct);
        return result.IsSuccess
            ? Results.Ok(new UploadUrlResponse(result.Value!.Url, result.Value.ExpiresAt))
            : MapError(result.Error!.Value);
    }

    private static async Task<IResult> CompleteClip(
        Guid id,
        ClaimsPrincipal principal,
        IClipUploadService clips,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Results.Unauthorized();
        }

        var result = await clips.CompleteAsync(userId, id, ct);
        return result.IsSuccess
            ? Results.Ok(new CompleteClipResponse(result.Value!.ClipId, result.Value.FileSizeBytes))
            : MapError(result.Error!.Value);
    }

    private static IResult MapError(ClipUploadError error) => error switch
    {
        ClipUploadError.InvalidTitle => Results.BadRequest(new { error = "invalid_title" }),
        ClipUploadError.InvalidDescription => Results.BadRequest(new { error = "invalid_description" }),
        ClipUploadError.InvalidVisibility => Results.BadRequest(new { error = "invalid_visibility" }),
        ClipUploadError.NotFound => Results.NotFound(new { error = "not_found" }),
        ClipUploadError.InvalidState => Results.BadRequest(new { error = "invalid_state" }),
        ClipUploadError.ObjectNotUploaded => Results.BadRequest(new { error = "object_not_uploaded" }),
        ClipUploadError.FileTooLarge => Results.BadRequest(new { error = "file_too_large" }),
        ClipUploadError.UnsupportedContentType => Results.BadRequest(new { error = "unsupported_content_type" }),
        _ => Results.Problem(statusCode: 500),
    };

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        userId = default;
        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out userId);
    }
}
