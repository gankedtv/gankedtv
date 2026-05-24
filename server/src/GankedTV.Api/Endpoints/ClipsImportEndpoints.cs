using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GankedTV.Api.Clips;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.Clips;
using GankedTV.Api.Services.Tags;
using GankedTV.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace GankedTV.Api.Endpoints;

// Sibling of ClipsUploadEndpoints. Single endpoint POST /clips/import that mirrors the
// existing /clips create handler shape — same auth, same rate limiter, same error envelope.
public static class ClipsImportEndpoints
{
    private static readonly string LogCategory = typeof(ClipsImportEndpoints).FullName!;

    public static IEndpointRouteBuilder MapClipsImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/clips")
            .RequireAuthorization()
            .RequireRateLimiting(ClipsRateLimiting.ClipsWritePolicy);
        group.MapPost("/import", ImportClip).WithValidation<ImportClipRequest>();
        group.MapPost("/import/preview", PreviewImport).WithValidation<PreviewImportRequest>();
        return app;
    }

    private static async Task<IResult> PreviewImport(
        [FromBody] PreviewImportRequest? req,
        ClaimsPrincipal principal,
        IClipImportService imports,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out _))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }
        if (req is null)
        {
            return ProblemResults.InvalidBody();
        }

        var result = await imports.PreviewAsync(req.Url, ct);
        return result.IsSuccess
            ? Results.Ok(result.Value!.ToPreviewResponse())
            : MapError(result.Error!.Value, loggerFactory);
    }

    private static async Task<IResult> ImportClip(
        [FromBody] ImportClipRequest? req,
        ClaimsPrincipal principal,
        IClipImportService imports,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }
        if (req is null)
        {
            return ProblemResults.InvalidBody();
        }

        var result = await imports.SubmitAsync(
            userId,
            new ImportClipInput(req.Url, req.Title, req.Description, req.GameId, req.Visibility, req.Tags),
            ct);

        return result.IsSuccess
            ? Results.Ok(result.Value!.ToImportClipResponse())
            : MapError(result.Error!.Value, loggerFactory);
    }

    private static IResult MapError(ClipUploadError error, ILoggerFactory loggerFactory) => error switch
    {
        ClipUploadError.InvalidUrl => ProblemResults.BadRequest("invalid_url"),
        ClipUploadError.UnsupportedHost => ProblemResults.BadRequest("unsupported_host"),
        ClipUploadError.SourceUnavailable => ProblemResults.BadRequest("source_unavailable"),
        // 503, not 400: fetch_failed is "yt-dlp / network / extractor infra broke", which is
        // a server-side problem the caller can retry — not a malformed request. Pairs with
        // the same code surfaced by the worker on retry exhaustion.
        ClipUploadError.FetchFailed => ProblemResults.ServiceUnavailable("fetch_failed"),
        ClipUploadError.ImportDisabled => ProblemResults.ServiceUnavailable("import_disabled"),
        ClipUploadError.InvalidTitle => ProblemResults.BadRequest("invalid_title"),
        ClipUploadError.InvalidDescription => ProblemResults.BadRequest("invalid_description"),
        ClipUploadError.InvalidVisibility => ProblemResults.BadRequest("invalid_visibility"),
        ClipUploadError.InvalidGame => ProblemResults.BadRequest("invalid_game"),
        ClipUploadError.TooManyTags => ProblemResults.BadRequest(TagsResolveProblemCodes.TooManyTags),
        ClipUploadError.InvalidTag => ProblemResults.BadRequest(TagsResolveProblemCodes.InvalidTag),
        _ => UnmappedError(error, loggerFactory),
    };

    private static IResult UnmappedError(ClipUploadError error, ILoggerFactory loggerFactory)
    {
        loggerFactory.CreateLogger(LogCategory)
            .LogError("Unmapped ClipUploadError value {Error} in import endpoint; add a case to MapError.", error);
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
