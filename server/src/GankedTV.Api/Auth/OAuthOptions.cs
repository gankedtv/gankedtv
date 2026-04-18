namespace GankedTV.Api.Auth;

public sealed class OAuthOptions
{
    public string StateSecret { get; set; } = "";
    public required string WebOrigin { get; set; }
    public OAuthProviderOptions Discord { get; set; } = new();
    public OAuthProviderOptions Google { get; set; } = new();

    public bool AnyProviderConfigured => Discord.IsConfigured || Google.IsConfigured;
}

public sealed class OAuthProviderOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(RedirectUri);
}
