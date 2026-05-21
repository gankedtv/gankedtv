namespace GankedTV.Api.Services.Igdb;

/// <summary>
/// IGDB (igdb.com) metadata source config. Credentials are a Twitch application's
/// client-credentials pair (IGDB auths through Twitch). Bound in Program.cs from
/// IGDB_CLIENT_ID / IGDB_CLIENT_SECRET env vars over the <c>Igdb</c> config section.
/// </summary>
public sealed class IgdbOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>Number of popular games the import command pulls into the catalog.</summary>
    public int PopularImportCount { get; set; } = 750;

    /// <summary>IGDB caps api.igdb.com at 4 requests/second.</summary>
    public int MaxRequestsPerSecond { get; set; } = 4;

    public string ApiBaseUrl { get; set; } = "https://api.igdb.com/v4/";
    public string TokenUrl { get; set; } = "https://id.twitch.tv/oauth2/token";
    public string ImageBaseUrl { get; set; } = "https://images.igdb.com/igdb/image/upload/";

    /// <summary>IGDB image size token. t_cover_big_2x = 528×748 — retina-crisp portrait box art.</summary>
    public string CoverSize { get; set; } = "t_cover_big_2x";

    /// <summary>
    /// Whether the periodic background re-sync (<see cref="IgdbSyncHostedService"/>) runs.
    /// Off by default — opt in per environment (and only effective when credentials are set).
    /// </summary>
    public bool SyncEnabled { get; set; }

    /// <summary>How often the background re-sync runs (also runs once on startup when enabled).</summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Whether IGDB-backed features (the import command) are usable.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
