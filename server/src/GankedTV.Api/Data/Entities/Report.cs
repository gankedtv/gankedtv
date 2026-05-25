namespace GankedTV.Api.Data.Entities;

// Polymorphic moderation report: a single table covers clip / comment / user targets so
// the queue, indexes, and admin tooling stay DRY. (TargetType, TargetId) is the natural
// pointer — no foreign key, since each target lives in a different table.
public class Report
{
    public Guid Id { get; set; }
    public Guid ReporterId { get; set; }

    // One of ReportTargetTypes.{Clip,Comment,User}.
    public required string TargetType { get; set; }
    public Guid TargetId { get; set; }

    // One of ReportReasons.*. Note required for "other" (enforced by ReportService.CreateAsync).
    public required string Reason { get; set; }
    public string? Note { get; set; }

    public string Status { get; set; } = ReportStatuses.Open;
    public Guid? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public User Reporter { get; set; } = null!;
    public User? ResolvedByUser { get; set; }
}

public static class ReportTargetTypes
{
    public const string Clip = "clip";
    public const string Comment = "comment";
    public const string User = "user";

    public static bool IsValid(string value) =>
        value == Clip || value == Comment || value == User;
}

public static class ReportReasons
{
    public const string Spam = "spam";
    public const string Harassment = "harassment";
    public const string Hate = "hate";
    public const string Nsfw = "nsfw";
    public const string Violence = "violence";
    // Clip is tagged with the wrong game (only meaningful for clip reports, but the
    // server doesn't gate by target type — the queue still benefits from the breakdown
    // even if a comment/user report uses it loosely).
    public const string WrongGame = "wrong_game";
    public const string Other = "other";

    public static bool IsValid(string value) =>
        value is Spam or Harassment or Hate or Nsfw or Violence or WrongGame or Other;
}

public static class ReportStatuses
{
    public const string Open = "open";
    public const string Resolved = "resolved";
    public const string Dismissed = "dismissed";

    public static bool IsValid(string value) =>
        value is Open or Resolved or Dismissed;
}
