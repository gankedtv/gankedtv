namespace GankedTV.Api.Data.Entities;

public static class ClipStatuses
{
    public const string Draft = "draft";
    public const string Processing = "processing";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

public static class ClipVisibilities
{
    public const string Public = "public";
    public const string Unlisted = "unlisted";

    public static bool IsValid(string value) =>
        value == Public || value == Unlisted;
}
