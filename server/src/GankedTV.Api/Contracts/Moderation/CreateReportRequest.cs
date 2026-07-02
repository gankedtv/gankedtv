using System.ComponentModel.DataAnnotations;

namespace GankedTV.Api.Contracts.Moderation;

public sealed record CreateReportRequest(
    [property: Required]
    [property: StringLength(32, MinimumLength = 1)]
    string? Reason,
    // Optional free-form context. Required when Reason == "other" (enforced by ReportService).
    [property: StringLength(2000)]
    string? Note);

public sealed record ResolveReportRequest(
    [property: Required]
    [property: StringLength(16, MinimumLength = 1)]
    string? Outcome);

public sealed record BanUserRequest(
    [property: StringLength(500)]
    string? Reason);

public sealed record SetClipGameRequest(
    // Nullable so an admin can also CLEAR a bad game tag entirely when no correct one fits;
    // null sets game_id back to NULL on the clip.
    int? GameId);

public sealed record RequeueFailedMediaRequest(
    // Narrows the requeue to one clip; null requeues every matching failed clip.
    Guid? ClipId,
    // Include content rejections (too long / too large) too. Off by default: those won't
    // succeed on a retry, so the recovery path only revives infra/transient failures.
    bool? IncludeContentFailures);
