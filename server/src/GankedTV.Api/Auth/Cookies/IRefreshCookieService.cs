namespace GankedTV.Api.Auth.Cookies;

/// <summary>
/// Issues and reads the HttpOnly refresh-token cookie. Off by default
/// (AUTH_REFRESH_COOKIE_ENABLED): when disabled, auth endpoints return the refresh token
/// in the response body and the SPA persists it to localStorage; when enabled, the token
/// only ever lives in this cookie so script can't exfiltrate it.
/// </summary>
public interface IRefreshCookieService
{
    bool Enabled { get; }
    void Append(HttpResponse response, string refreshToken);
    void Clear(HttpResponse response);
    string? Read(HttpRequest request);
}
