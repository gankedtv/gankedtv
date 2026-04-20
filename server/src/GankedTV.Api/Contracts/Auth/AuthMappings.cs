using GankedTV.Api.Auth.Tokens;

namespace GankedTV.Api.Contracts.Auth;

public static class AuthMappings
{
    public static TokenResponse ToTokenResponse(
        this RotateResult result,
        string accessToken,
        int expiresInSeconds) =>
        new(accessToken, result.NewRawToken, expiresInSeconds);
}
