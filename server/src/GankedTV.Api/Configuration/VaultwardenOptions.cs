namespace GankedTV.Api.Configuration;

/// <summary>
/// Bootstrap config for fetching secrets from the self-hosted Vaultwarden-API. Only
/// <see cref="ApiUrl"/> + <see cref="ApiKey"/> live in the environment; everything else is pulled
/// from the vault at startup. When not <see cref="IsConfigured"/> the loader no-ops and the app
/// falls back to env/.env (same opt-in style as <c>IgdbOptions.IsConfigured</c>).
/// </summary>
public sealed class VaultwardenOptions
{
    /// <summary>Base URL of the Vaultwarden-API service (e.g. <c>https://vault.internal</c>).</summary>
    public string? ApiUrl { get; set; }

    /// <summary>Bearer token for the API (the <c>secrets@</c> service user's API key).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Vaultwarden organization the collections live under. Defaults to GankedTV.</summary>
    public string Organization { get; set; } = "GankedTV";

    /// <summary>
    /// Explicit collection override. When unset the collection is derived from the environment
    /// (see <see cref="ResolveCollection"/>).
    /// </summary>
    public string? Collection { get; set; }

    /// <summary>Both bootstrap vars present → the loader runs; otherwise it no-ops.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiUrl) && !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// Explicit collection wins; otherwise Production (case-insensitive) maps to
    /// <c>"Secrets - PROD"</c> and anything else to <c>"Secrets - DEV"</c>.
    /// </summary>
    public static string ResolveCollection(string? explicitCollection, string? aspnetEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(explicitCollection))
        {
            return explicitCollection.Trim();
        }

        return string.Equals(aspnetEnvironment, "Production", StringComparison.OrdinalIgnoreCase)
            ? "Secrets - PROD"
            : "Secrets - DEV";
    }

    /// <summary>The collection this instance resolves to for the given environment.</summary>
    public string EffectiveCollection(string? aspnetEnvironment) =>
        ResolveCollection(Collection, aspnetEnvironment);
}
