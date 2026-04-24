namespace GankedTV.Api.Validation;

/// <summary>
/// Compile-time mirrors of <see cref="ClipValidationOptions"/> defaults, for use in
/// <c>System.ComponentModel.DataAnnotations</c> attributes (which require constant args).
/// These are the *hard ceiling* — <see cref="ClipValidationOptions"/> may impose a tighter
/// runtime cap via the upload service.
/// </summary>
public static class ClipValidationLimits
{
    public const int MaxTitleLength = 255;
    public const int MaxDescriptionLength = 5000;
}
