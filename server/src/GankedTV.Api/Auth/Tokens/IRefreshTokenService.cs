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
