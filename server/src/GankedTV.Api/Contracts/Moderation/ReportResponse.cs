namespace GankedTV.Api.Contracts.Moderation;

public sealed record CreateReportResponse(Guid Id);

// One row in the admin reports queue. The target field is hydrated to the kind the row points
// at (clip / comment / user) — the SPA discriminates on `targetType`.
public sealed record ReportListItem(
    Guid Id,
    string TargetType,
    Guid TargetId,
    string Reason,
    string? Note,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    ReportUserRef Reporter,
    ReportTarget Target);

public sealed record ReportUserRef(Guid Id, string Username, string? AvatarUrl);

// Discriminated union, projected from the underlying clip / comment / user row at query time.
// All fields are nullable so EF can flatten this into a single LINQ projection rather than
// returning a polymorphic shape that needs a switch in C# code.
public sealed record ReportTarget(
    ReportClipTarget? Clip,
    ReportCommentTarget? Comment,
    ReportUserTarget? User);

public sealed record ReportClipTarget(
    Guid Id,
    string Title,
    string? ThumbnailKey,
    string Visibility,
    string Status,
    ReportUserRef Owner);

public sealed record ReportCommentTarget(
    Guid Id,
    Guid ClipId,
    string? Body,
    DateTimeOffset? DeletedAt,
    ReportUserRef Author);

public sealed record ReportUserTarget(
    Guid Id,
    string Username,
    string? AvatarUrl,
    DateTimeOffset? BannedAt,
    string Role);

public sealed record ReportListResponse(
    IReadOnlyList<ReportListItem> Items,
    int Page,
    int PageSize,
    int Total);
