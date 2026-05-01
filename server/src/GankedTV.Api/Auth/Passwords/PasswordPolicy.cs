namespace GankedTV.Api.Auth.Passwords;

// Centralised password rules. Kept simple on purpose:
//   * 12 char minimum (NIST SP 800-63B floor for memorised secrets is 8;
//     we bump it because there's no email-verification or breach-API check yet).
//   * Reject if equal to email or username (case-insensitive).
//   * Reject a tiny embedded list of obvious passwords. We deliberately do not
//     ship the full SecLists top-1M — that's a future enhancement (HIBP API
//     or k-anonymity check). The list here catches the laziest attempts.
public static class PasswordPolicy
{
    public const int MinLength = 12;

    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "passw0rd", "p@ssw0rd",
        "qwerty", "qwerty123", "qwertyuiop",
        "letmein", "welcome", "welcome1", "welcome123",
        "admin", "administrator", "root", "toor",
        "iloveyou", "trustno1", "monkey", "dragon", "master", "shadow",
        "123456789012", "111111111111", "abc123abc123", "test1234test", "changeme123",
    };

    public static PasswordValidationResult Validate(string password, string? email, string? username)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            // Whitespace-only is treated as missing — a user typing 12 spaces should not
            // sail past the length floor.
            return PasswordValidationResult.Invalid("Password is required.");
        }

        if (password.Length < MinLength)
        {
            return PasswordValidationResult.Invalid($"Password must be at least {MinLength} characters.");
        }

        if (!string.IsNullOrEmpty(email)
            && string.Equals(password, email, StringComparison.OrdinalIgnoreCase))
        {
            return PasswordValidationResult.Invalid("Password must not equal the email address.");
        }

        if (!string.IsNullOrEmpty(username)
            && string.Equals(password, username, StringComparison.OrdinalIgnoreCase))
        {
            return PasswordValidationResult.Invalid("Password must not equal the username.");
        }

        if (CommonPasswords.Contains(password))
        {
            return PasswordValidationResult.Invalid("Password is too common.");
        }

        return PasswordValidationResult.Valid;
    }
}

public readonly record struct PasswordValidationResult(bool IsValid, string? Error)
{
    public static readonly PasswordValidationResult Valid = new(true, null);
    public static PasswordValidationResult Invalid(string error) => new(false, error);
}
