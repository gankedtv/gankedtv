using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.Clips;
using GankedTV.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace GankedTV.Api.Endpoints;

public static class ClipsUploadEndpoints
{
    // Category used when logging unmapped enum values so the failure is traceable
    // if a new ClipUploadError case is added without updating MapError.
    private static readonly string LogCategory = typeof(ClipsUploadEndpoints).FullName!;

    public static IEndpointRouteBuilder MapClipsUploadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/clips").RequireAuthorization();
        group.MapPost("/", CreateClip).WithValidation<CreateClipRequest>();
        group.MapPost("/{id:guid}/upload-url", GetUploadUrl);
        group.MapPost("/{id:guid}/complete", CompleteClip);
        return app;
    }

    private static async Task<IResult> CreateClip(
        // Nullable so a literal JSON `null` body hits the ValidationEndpointFilter (which
        // returns 400 ValidationProblemDetails) rather than a framework-generated 400.
        [FromBody] CreateClipRequest? req,
        ClaimsPrincipal principal,
        IClipUploadService clips,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        // Defensive: the WithValidation<T> filter guards null bodies; this is unreachable at
        // runtime but keeps the handler safe if the filter is ever removed — same envelope.
        if (req is null)
        {
            return ProblemResults.InvalidBody();
        }

        var result = await clips.CreateAsync(
            userId,
            new CreateClipInput(req.Title, req.Description, req.GameId, req.Visibility),
            ct);

        return result.IsSuccess
            ? Results.Ok(result.Value!.ToCreateClipResponse())
            : MapError(result.Error!.Value, loggerFactory);
    }

    private static async Task<IResult> GetUploadUrl(
        Guid id,
        ClaimsPrincipal principal,
        IClipUploadService clips,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        var result = await clips.GetUploadUrlAsync(userId, id, ct);
        return result.IsSuccess
            ? Results.Ok(result.Value!.ToUploadUrlResponse())
            : MapError(result.Error!.Value, loggerFactory);
    }

    private static async Task<IResult> CompleteClip(
        Guid id,
        ClaimsPrincipal principal,
        IClipUploadService clips,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        var result = await clips.CompleteAsync(userId, id, ct);
        return result.IsSuccess
            ? Results.Ok(result.Value!.ToCompleteClipResponse())
            : MapError(result.Error!.Value, loggerFactory);
    }

    private static IResult MapError(ClipUploadError error, ILoggerFactory loggerFactory) => error switch
    {
        ClipUploadError.InvalidTitle => ProblemResults.BadRequest("invalid_title"),
        ClipUploadError.InvalidDescription => ProblemResults.BadRequest("invalid_description"),
        ClipUploadError.InvalidVisibility => ProblemResults.BadRequest("invalid_visibility"),
        ClipUploadError.NotFound => ProblemResults.NotFound("not_found"),
        ClipUploadError.InvalidState => ProblemResults.BadRequest("invalid_state"),
        ClipUploadError.ObjectNotUploaded => ProblemResults.BadRequest("object_not_uploaded"),
        ClipUploadError.FileTooLarge => ProblemResults.BadRequest("file_too_large"),
        ClipUploadError.UnsupportedContentType => ProblemResults.BadRequest("unsupported_content_type"),
        _ => UnmappedError(error, loggerFactory),
    };

    private static IResult UnmappedError(ClipUploadError error, ILoggerFactory loggerFactory)
    {
        loggerFactory.CreateLogger(LogCategory)
            .LogError("Unmapped ClipUploadError value {Error}; add a case to MapError.", error);
        return ProblemResults.Internal("unmapped_error", $"Unhandled error: {error}");
    }

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        userId = default;
        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out userId);
    }
}
