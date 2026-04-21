using System.ComponentModel.DataAnnotations;

namespace GankedTV.Api.Contracts.Users;

public sealed record UpdateMeRequest(
    [property: StringLength(30)]
    string? Username,
    [property: StringLength(500)]
    string? Bio,
    [property: StringLength(2048)]
    string? AvatarUrl);
