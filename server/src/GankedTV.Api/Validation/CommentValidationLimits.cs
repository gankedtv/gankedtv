namespace GankedTV.Api.Validation;

/// <summary>
/// Compile-time limits for comment bodies, for use in
/// <c>System.ComponentModel.DataAnnotations</c> attributes (which require constant args).
/// Mirrors the <c>body VARCHAR(2000)</c> column.
/// </summary>
public static class CommentValidationLimits
{
    public const int MaxBodyLength = 2000;
}
