using System.ComponentModel.DataAnnotations;
using GankedTV.Api.Auth.Passwords;

namespace GankedTV.Api.Contracts.Auth;

public sealed record RegisterRequest(
    [property: Required, EmailAddress, StringLength(255)]
    string Email,
    [property: Required, StringLength(30, MinimumLength = 1)]
    string Username,
    // Floor lifted to PasswordPolicy.MinLength so blatantly-too-short passwords get
    // rejected at the validation filter (faster feedback to the SPA) instead of
    // sliding through to the policy check. PasswordPolicy is still the source of
    // truth for everything beyond raw length (common-list, equality with email/username).
    [property: Required, StringLength(128, MinimumLength = PasswordPolicy.MinLength)]
    string Password);
