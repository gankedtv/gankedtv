using System.Security.Claims;
using GankedTV.Api.Auth;
using GankedTV.Api.Clips;
using GankedTV.Api.Contracts.Moderation;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.Moderation;
using GankedTV.Api.Validation;
using Microsoft.AspNetCore.Mvc;

namespace GankedTV.Api.Endpoints;

// Three endpoints, one handler shape: POST /clips/{id}/report, /comments/{id}/report,
// /users/{id}/report. Differs only in the TargetType passed into ReportService.CreateAsync —
// keeps validation, error mapping, and rate limiting in lockstep across all three.
public static class ReportsEndpoints
{
    public static IEndpointRouteBuilder MapReportsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("")
            .RequireAuthorization()
            .RequireRateLimiting(ClipsRateLimiting.ClipsWritePolicy);

        group.MapPost("/clips/{id:guid}/report",
            (Guid id, CreateReportRequest? req, ClaimsPrincipal principal, IReportService reports, CancellationToken ct)
                => ReportTarget(ReportTargetTypes.Clip, id, req, principal, reports, ct))
            .WithValidation<CreateReportRequest>();

        group.MapPost("/comments/{id:guid}/report",
            (Guid id, CreateReportRequest? req, ClaimsPrincipal principal, IReportService reports, CancellationToken ct)
                => ReportTarget(ReportTargetTypes.Comment, id, req, principal, reports, ct))
            .WithValidation<CreateReportRequest>();

        group.MapPost("/users/{id:guid}/report",
            (Guid id, CreateReportRequest? req, ClaimsPrincipal principal, IReportService reports, CancellationToken ct)
                => ReportTarget(ReportTargetTypes.User, id, req, principal, reports, ct))
            .WithValidation<CreateReportRequest>();

        return app;
    }

    private static async Task<IResult> ReportTarget(
        string targetType,
        Guid targetId,
        [FromBody] CreateReportRequest? req,
        ClaimsPrincipal principal,
        IReportService reports,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var reporterId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }
        // Defensive: WithValidation<T> already 400s null bodies, but keep the same envelope
        // so a filter removal doesn't change the shape clients see.
        if (req?.Reason is null)
        {
            return ProblemResults.InvalidBody();
        }

        var result = await reports.CreateAsync(reporterId, targetType, targetId, req.Reason, req.Note, ct);
        if (result.IsSuccess)
        {
            return Results.Json(new CreateReportResponse(result.ReportId!.Value),
                statusCode: StatusCodes.Status201Created);
        }
        return result.Error switch
        {
            ReportCreateError.InvalidTargetType => ProblemResults.BadRequest("invalid_target"),
            ReportCreateError.InvalidReason => ProblemResults.BadRequest("invalid_reason"),
            ReportCreateError.NoteRequired => ProblemResults.BadRequest("note_required"),
            ReportCreateError.TargetNotFound => ProblemResults.NotFound("not_found"),
            ReportCreateError.SelfReport => ProblemResults.BadRequest("self_report"),
            ReportCreateError.DuplicateOpenReport => ProblemResults.Conflict("duplicate_report"),
            _ => ProblemResults.Internal("unmapped_error"),
        };
    }
}
