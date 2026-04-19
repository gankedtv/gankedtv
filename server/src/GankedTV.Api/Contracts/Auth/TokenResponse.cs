namespace GankedTV.Api.Contracts.Auth;

public sealed record TokenResponse(string Token, string Refresh, int ExpiresIn);
