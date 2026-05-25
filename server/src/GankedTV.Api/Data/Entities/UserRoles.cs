namespace GankedTV.Api.Data.Entities;

public static class UserRoles
{
    public const string User = "user";
    public const string Moderator = "moderator";
    public const string Admin = "admin";

    public static bool IsValid(string value) =>
        value == User || value == Moderator || value == Admin;
}
