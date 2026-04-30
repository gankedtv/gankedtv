using System.ComponentModel.DataAnnotations;
using GankedTV.Api.Auth.Passwords;

namespace GankedTV.Api.Contracts.Auth;

public sealed record SetPasswordRequest(
    // Required only when the caller already has a password on file. The endpoint
    // enforces that conditionally so OAuth-only users can attach a password
    // without one (the OAuth login already proved control of the account).
    [property: StringLength(128)]
    string? CurrentPassword,
    [property: Required, StringLength(128, MinimumLength = PasswordPolicy.MinLength)]
    string NewPassword);
