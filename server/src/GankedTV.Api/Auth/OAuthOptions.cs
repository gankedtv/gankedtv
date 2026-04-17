namespace GankedTV.Api.Auth;

public sealed class OAuthOptions
{
    public required string StateSecret { get; set; }
    public required string WebOrigin { get; set; }
    public OAuthProviderOptions Discord { get; set; } = new();
    public OAuthProviderOptions Google { get; set; } = new();
}

public sealed class OAuthProviderOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
}
