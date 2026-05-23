namespace GankedTV.Api.Data.Entities;

public static class ClipStatuses
{
    public const string Draft = "draft";
    public const string Processing = "processing";
    public const string Transcoding = "transcoding";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

public static class ClipVisibilities
{
    public const string Public = "public";
    public const string Unlisted = "unlisted";

    public static bool IsValid(string value) =>
        string.Equals(value, Public, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Unlisted, StringComparison.OrdinalIgnoreCase);

    // Callers receive the canonical lowercase form so the DB column stays consistent
    // regardless of how the client cased the input.
    public static string Normalize(string value) => value.ToLowerInvariant();
}
