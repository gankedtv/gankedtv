using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GankedTV.Api.Services.Moderation;

public sealed class ReportService(GankedTvDbContext db, TimeProvider clock) : IReportService
{
    public async Task<ReportCreateResult> CreateAsync(
        Guid reporterId,
        string targetType,
        Guid targetId,
        string reason,
        string? note,
        CancellationToken ct)
    {
        if (!ReportTargetTypes.IsValid(targetType))
        {
            return ReportCreateResult.Failure(ReportCreateError.InvalidTargetType);
        }
        if (!ReportReasons.IsValid(reason))
        {
            return ReportCreateResult.Failure(ReportCreateError.InvalidReason);
        }
        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        // Free-form "other" must explain itself — the queue is useless if reports collapse
        // into a single un-labelled bucket. All other reasons may attach an optional note.
        if (reason == ReportReasons.Other && string.IsNullOrEmpty(trimmedNote))
        {
            return ReportCreateResult.Failure(ReportCreateError.NoteRequired);
        }

        // Self-report guard: the reporter can't be the target user, the clip's owner, or the
        // comment's author. Looked up via a single SELECT keyed on TargetType so the service
        // has one consistent ownership check across the three target kinds.
        var (exists, ownerId) = await LookupTargetAsync(targetType, targetId, ct);
        if (!exists)
        {
            return ReportCreateResult.Failure(ReportCreateError.TargetNotFound);
        }
        if (ownerId == reporterId)
        {
            return ReportCreateResult.Failure(ReportCreateError.SelfReport);
        }

        // App-level duplicate guard. The DB partial unique index (created in the migration)
        // is the authoritative guard against races between concurrent submits.
        var openDup = await db.Reports.AnyAsync(
            r => r.ReporterId == reporterId
                && r.TargetType == targetType
                && r.TargetId == targetId
                && r.Status == ReportStatuses.Open,
            ct);
        if (openDup)
        {
            return ReportCreateResult.Failure(ReportCreateError.DuplicateOpenReport);
        }

        var report = new Report
        {
            ReporterId = reporterId,
            TargetType = targetType,
            TargetId = targetId,
            Reason = reason,
            Note = trimmedNote,
            Status = ReportStatuses.Open,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Reports.Add(report);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateOpenReport(ex))
        {
            // Lost the race against a concurrent submit; surface the same 409 as the
            // app-level check above.
            return ReportCreateResult.Failure(ReportCreateError.DuplicateOpenReport);
        }
        return ReportCreateResult.Success(report.Id);
    }

    public async Task<ReportResolveResult> ResolveAsync(
        Guid reportId,
        Guid moderatorId,
        string outcome,
        CancellationToken ct)
    {
        if (outcome != ReportStatuses.Resolved && outcome != ReportStatuses.Dismissed)
        {
            return ReportResolveResult.Failure(ReportResolveError.InvalidOutcome);
        }

        // Atomic CAS via ExecuteUpdateAsync: only the row that's still Open can be updated,
        // so two concurrent moderators racing on the same report can't both succeed and
        // overwrite each other's resolvedBy/resolvedAt. The previous read-modify-write
        // would let both load the Open row, both think they're the first resolver, and the
        // last SaveChangesAsync would clobber the winner's audit fields.
        var now = clock.GetUtcNow();
        var updated = await db.Reports
            .Where(r => r.Id == reportId && r.Status == ReportStatuses.Open)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, outcome)
                .SetProperty(r => r.ResolvedBy, (Guid?)moderatorId)
                .SetProperty(r => r.ResolvedAt, (DateTimeOffset?)now), ct);
        if (updated == 0)
        {
            // Either the row doesn't exist or it was already resolved by someone else.
            // Distinguish those with a single follow-up SELECT so the SPA can render the
            // right message (404 vs 409). The lookup races with concurrent inserts but
            // not in a way that matters — if a brand-new report appeared between the
            // UPDATE-miss and this SELECT, returning "not found" is still defensible.
            var exists = await db.Reports.AsNoTracking().AnyAsync(r => r.Id == reportId, ct);
            return exists
                ? ReportResolveResult.Failure(ReportResolveError.AlreadyResolved)
                : ReportResolveResult.Failure(ReportResolveError.NotFound);
        }

        // Surface the updated row to the caller (the admin queue refresh uses the response
        // body). AsNoTracking is fine — we're not going to mutate it again.
        var report = await db.Reports.AsNoTracking().FirstAsync(r => r.Id == reportId, ct);
        return ReportResolveResult.Success(report);
    }

    public Task<int> ResolveForTargetAsync(
        string targetType,
        Guid targetId,
        Guid moderatorId,
        CancellationToken ct) =>
        // Pass reason=null through the shared helper: the predicate skips the reason filter
        // when reason is null, so the "all open reports for this target" case stays one SQL
        // UPDATE just like before.
        ResolveOpenReportsAsync(targetType, targetId, reason: null, moderatorId, ct);

    public Task<int> ResolveForTargetByReasonAsync(
        string targetType,
        Guid targetId,
        string reason,
        Guid moderatorId,
        CancellationToken ct) =>
        ResolveOpenReportsAsync(targetType, targetId, reason, moderatorId, ct);

    private async Task<int> ResolveOpenReportsAsync(
        string targetType,
        Guid targetId,
        string? reason,
        Guid moderatorId,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // ExecuteUpdateAsync emits a single UPDATE so closing a hot queue of reports for
        // one bad actor doesn't materialize every row into the change tracker.
        //
        // The `reason == null` branch is evaluated client-side by EF at translation time —
        // when `reason` is null the predicate collapses to TRUE and the column filter drops
        // out of the generated SQL; when set, it becomes `AND r.reason = @reason`. So the
        // "all reasons" and "one reason" paths each produce the minimal WHERE clause, no
        // CASE expression or runtime branch in the DB.
        return await db.Reports
            .Where(r => r.TargetType == targetType
                && r.TargetId == targetId
                && r.Status == ReportStatuses.Open
                && (reason == null || r.Reason == reason))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, ReportStatuses.Resolved)
                .SetProperty(r => r.ResolvedBy, (Guid?)moderatorId)
                .SetProperty(r => r.ResolvedAt, (DateTimeOffset?)now), ct);
    }

    private async Task<(bool Exists, Guid? OwnerId)> LookupTargetAsync(
        string targetType,
        Guid targetId,
        CancellationToken ct)
    {
        switch (targetType)
        {
            case ReportTargetTypes.Clip:
                {
                    var owner = await db.Clips.AsNoTracking()
                        .Where(c => c.Id == targetId)
                        .Select(c => (Guid?)c.UserId)
                        .FirstOrDefaultAsync(ct);
                    return (owner.HasValue, owner);
                }
            case ReportTargetTypes.Comment:
                {
                    var owner = await db.Comments.AsNoTracking()
                        .Where(c => c.Id == targetId)
                        .Select(c => (Guid?)c.UserId)
                        .FirstOrDefaultAsync(ct);
                    return (owner.HasValue, owner);
                }
            case ReportTargetTypes.User:
                {
                    var exists = await db.Users.AsNoTracking()
                        .AnyAsync(u => u.Id == targetId, ct);
                    return (exists, targetId);
                }
            default:
                return (false, null);
        }
    }

    private static bool IsDuplicateOpenReport(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg
        && pg.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(pg.ConstraintName, "idx_reports_open_unique", StringComparison.Ordinal);
}
