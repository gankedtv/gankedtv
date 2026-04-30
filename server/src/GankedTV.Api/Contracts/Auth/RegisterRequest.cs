using System.ComponentModel.DataAnnotations;

namespace GankedTV.Api.Contracts.Auth;

public sealed record RegisterRequest(
    [property: Required, EmailAddress, StringLength(255)]
    string Email,
    [property: Required, StringLength(30, MinimumLength = 1)]
    string Username,
    [property: Required, StringLength(128, MinimumLength = 1)]
    string Password);
