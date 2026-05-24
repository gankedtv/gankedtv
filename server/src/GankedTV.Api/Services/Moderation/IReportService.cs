using GankedTV.Api.Data.Entities;

namespace GankedTV.Api.Services.Moderation;

public interface IReportService
{
    Task<ReportCreateResult> CreateAsync(
        Guid reporterId,
        string targetType,
        Guid targetId,
        string reason,
        string? note,
        CancellationToken ct);

    Task<ReportResolveResult> ResolveAsync(
        Guid reportId,
        Guid moderatorId,
        string outcome,
        CancellationToken ct);

    // Closes every open report against the given target. Called from admin moderation
    // actions (hide clip, remove comment, ban user) so the queue self-cleans.
    Task<int> ResolveForTargetAsync(
        string targetType,
        Guid targetId,
        Guid moderatorId,
        CancellationToken ct);

    // Closes open reports against a target that match a specific reason. Used by partial
    // remediation: fixing a clip's game tag should resolve `wrong_game` reports without
    // touching unrelated abuse reports against the same clip.
    Task<int> ResolveForTargetByReasonAsync(
        string targetType,
        Guid targetId,
        string reason,
        Guid moderatorId,
        CancellationToken ct);
}

public enum ReportCreateError
{
    InvalidTargetType,
    InvalidReason,
    NoteRequired,
    TargetNotFound,
    SelfReport,
    DuplicateOpenReport,
}

public enum ReportResolveError
{
    NotFound,
    InvalidOutcome,
    AlreadyResolved,
}

public sealed record ReportCreateResult(Guid? ReportId, ReportCreateError? Error)
{
    public bool IsSuccess => ReportId.HasValue;
    public static ReportCreateResult Success(Guid id) => new(id, null);
    public static ReportCreateResult Failure(ReportCreateError error) => new(null, error);
}

public sealed record ReportResolveResult(Report? Report, ReportResolveError? Error)
{
    public bool IsSuccess => Report is not null;
    public static ReportResolveResult Success(Report report) => new(report, null);
    public static ReportResolveResult Failure(ReportResolveError error) => new(null, error);
}
