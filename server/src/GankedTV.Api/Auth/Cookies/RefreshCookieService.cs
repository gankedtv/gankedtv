using GankedTV.Api.Auth.Tokens;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Auth.Cookies;

public sealed class RefreshCookieOptions
{
    public bool Enabled { get; set; }
}

public sealed class RefreshCookieService(
    IOptions<RefreshCookieOptions> options,
    IOptions<RefreshTokenOptions> tokenOptions,
    IWebHostEnvironment env) : IRefreshCookieService
{
    public const string CookieName = "gtv_refresh";

    public bool Enabled => options.Value.Enabled;

    public void Append(HttpResponse response, string refreshToken)
    {
        var cookieOptions = BuildOptions();
        cookieOptions.MaxAge = TimeSpan.FromDays(tokenOptions.Value.ExpiryDays);
        response.Cookies.Append(CookieName, refreshToken, cookieOptions);
    }

    public void Clear(HttpResponse response) =>
        // Deletion must carry the same Path/Secure/SameSite attributes or browsers treat it
        // as a different cookie and keep the original.
        response.Cookies.Delete(CookieName, BuildOptions());

    public string? Read(HttpRequest request) =>
        request.Cookies.TryGetValue(CookieName, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : null;

    private CookieOptions BuildOptions()
    {
        var secure = !env.IsDevelopment();
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            // Production runs the web and API on different subdomains, so the cookie must be
            // SameSite=None (which requires Secure). Dev falls back to Lax — browsers refuse
            // None without Secure — which means real-browser cookie mode doesn't work across
            // localhost ports; integration tests use HttpClient cookie containers and don't care.
            SameSite = secure ? SameSiteMode.None : SameSiteMode.Lax,
            Path = "/auth",
        };
    }
}
