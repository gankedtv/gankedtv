namespace GankedTV.Api.Auth;

public sealed class JwtOptions
{
    public required string Secret { get; set; }
    public required string Issuer { get; set; }
    public required string Audience { get; set; }
    public int ExpiryMinutes { get; set; } = 15;
}
