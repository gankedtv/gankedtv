using GankedTV.Api.Data.Entities;

namespace GankedTV.Api.Auth.Tokens;

public interface IRefreshTokenService
{
    Task<string> IssueAsync(Guid userId, CancellationToken ct = default);
    Task<RotateResult> RotateAsync(string rawToken, CancellationToken ct = default);
    Task RevokeAsync(string rawToken, CancellationToken ct = default);
}

public sealed record RotateResult(User User, string NewRawToken);

public sealed class InvalidRefreshTokenException : Exception
{
    public InvalidRefreshTokenException(string message) : base(message) { }
}

// Thrown by RotateAsync when the refresh-token row belongs to a banned account. Distinct
// from InvalidRefreshTokenException so the endpoint can translate it to 403 account_banned
// (matching the login response shape) instead of the generic 401 invalid_refresh.
public sealed class BannedAccountException : Exception
{
    public BannedAccountException() : base("Account is banned.") { }
}
