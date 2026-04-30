using System.ComponentModel.DataAnnotations;

namespace GankedTV.Api.Contracts.Auth;

public sealed record LoginRequest(
    [property: Required, EmailAddress, StringLength(255)]
    string Email,
    [property: Required, StringLength(128, MinimumLength = 1)]
    string Password);
