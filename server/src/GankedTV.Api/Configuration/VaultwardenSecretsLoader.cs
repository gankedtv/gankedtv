using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GankedTV.Api.Configuration;

/// <summary>
/// Fetches the server's required secrets from the Vaultwarden-API at startup and sets each as an
/// env var when unset (like <c>DotNetEnv.Env.NoClobber()</c>), so a real env var still wins and
/// existing <c>Environment.GetEnvironmentVariable(...)</c> read sites pick the values up unchanged.
/// </summary>
public sealed class VaultwardenSecretsLoader
{
    /// <summary>
    /// The server's required secrets, fetched by these exact names. No <c>SENTRY_DSN</c> — there's
    /// no Sentry integration yet.
    /// </summary>
    public static readonly IReadOnlyList<string> Manifest =
    [
        "DATABASE_URL",
        "JWT_SECRET",
        "OAUTH_STATE_SECRET",
        "S3_ENDPOINT",
        "S3_ACCESS_KEY",
        "S3_SECRET_KEY",
        "S3_PUBLIC_URL",
        "DISCORD_CLIENT_ID",
        "DISCORD_CLIENT_SECRET",
        "DISCORD_REDIRECT_URI",
        "GOOGLE_CLIENT_ID",
        "GOOGLE_CLIENT_SECRET",
        "GOOGLE_REDIRECT_URI",
        "IGDB_CLIENT_ID",
        "IGDB_CLIENT_SECRET",
        "REDIS_URL",
        "WEB_ORIGIN",
        "CORS_ORIGINS",
    ];

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly VaultwardenOptions _options;
    private readonly string _collection;
    private readonly ILogger<VaultwardenSecretsLoader> _logger;

    public VaultwardenSecretsLoader(
        HttpClient httpClient,
        VaultwardenOptions options,
        string collection,
        ILogger<VaultwardenSecretsLoader> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        _httpClient = httpClient;
        _options = options;
        _collection = collection;
        _logger = logger;
    }

    /// <summary>
    /// Fetches one secret, scoped to the org + collection. Returns the value, or <c>null</c> on 404;
    /// any other non-success status throws <see cref="HttpRequestException"/>.
    /// </summary>
    public async Task<string?> FetchSecretAsync(string name, CancellationToken ct)
    {
        var requestUri =
            $"{_options.ApiUrl!.TrimEnd('/')}/secret/{Uri.EscapeDataString(name)}"
            + $"?organization_name={Uri.EscapeDataString(_options.Organization)}"
            + $"&collection_name={Uri.EscapeDataString(_collection)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<SecretResponse>(json, JsonOpts)?.Value;
    }

    /// <summary>
    /// Fetches the manifest sequentially and applies each value via the injected
    /// <paramref name="get"/>/<paramref name="set"/> (real env in <c>Program.cs</c>, a fake map in
    /// tests). Keys already set are skipped (env wins). With <paramref name="failFast"/> a missing
    /// or errored key throws; otherwise it logs and continues. Returns the keys applied.
    /// </summary>
    public async Task<IReadOnlyList<string>> LoadAsync(
        bool failFast,
        Func<string, string?> get,
        Action<string, string> set,
        IReadOnlyList<string>? manifest = null,
        CancellationToken ct = default)
    {
        var applied = new List<string>();

        foreach (var key in manifest ?? Manifest)
        {
            if (!string.IsNullOrWhiteSpace(get(key)))
            {
                continue; // already set → env wins, no request needed
            }

            string? value;
            try
            {
                value = await FetchSecretAsync(key, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // caller cancelled — propagate, don't swallow as a fetch failure
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (failFast)
                {
                    throw new InvalidOperationException(
                        $"Vaultwarden: failed to fetch required secret '{key}' from collection "
                        + $"'{_collection}': {ex.Message}", ex);
                }

                _logger.LogWarning(ex, "Vaultwarden: fetch of {Key} failed; falling back to env/.env.", key);
                continue;
            }

            if (string.IsNullOrEmpty(value))
            {
                if (failFast)
                {
                    throw new InvalidOperationException(
                        $"Vaultwarden: required secret '{key}' not found in collection '{_collection}'.");
                }

                _logger.LogWarning(
                    "Vaultwarden: secret {Key} not found in {Collection}; falling back to env/.env.",
                    key, _collection);
                continue;
            }

            set(key, value);
            applied.Add(key);
        }

        if (applied.Count > 0)
        {
            _logger.LogInformation(
                "Vaultwarden: loaded {Count} secret(s) from collection '{Collection}'.",
                applied.Count, _collection);
        }

        return applied;
    }

    private sealed record SecretResponse(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("value")] string? Value);
}
