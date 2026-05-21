using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Igdb;

/// <summary>
/// IGDB metadata client: Twitch client-credentials OAuth (token cached + refreshed on
/// expiry/401) and a self-imposed request throttle to stay under IGDB's 4 req/s cap.
/// </summary>
public sealed class IgdbMetadataService : IIgdbMetadataService
{
    public const string ApiClientName = "igdb-api";
    public const string ImageClientName = "igdb-image";

    // IGDB returns at most 500 rows per /games query.
    private const int MaxPageSize = 500;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpFactory;
    private readonly IgdbOptions _options;
    private readonly ILogger<IgdbMetadataService> _logger;
    private readonly TimeProvider _clock;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt;

    // Throttle gate: serialize api.igdb.com calls and enforce a minimum spacing between them.
    private readonly SemaphoreSlim _throttleLock = new(1, 1);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public IgdbMetadataService(
        IHttpClientFactory httpFactory,
        IOptions<IgdbOptions> options,
        ILogger<IgdbMetadataService> logger,
        TimeProvider clock)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _logger = logger;
        _clock = clock;
    }

    public async Task<IReadOnlyList<IgdbGame>> GetPopularGamesAsync(int count, CancellationToken ct = default)
    {
        var results = new List<IgdbGame>(count);

        // Always page by MaxPageSize and advance offset by the rows actually returned — never by
        // the (smaller) "rows still wanted", or filtered-out cover-less rows would make offset
        // overshoot and skip a window of games. Over-fetch is trimmed to `count` at the end.
        for (var offset = 0; results.Count < count; offset += MaxPageSize)
        {
            // category = 0 → main games (exclude DLC/bundles/mods); version_parent = null →
            // exclude alternate editions. Most-rated first so the catalog leads with titles
            // people actually clip.
            var query =
                $"fields name, cover.image_id; where cover != null & category = 0 & version_parent = null; " +
                $"sort total_rating_count desc; limit {MaxPageSize}; offset {offset};";

            var page = await PostGamesQueryAsync(query, ct);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var g in page)
            {
                if (results.Count >= count)
                {
                    break;
                }
                if (g.Name is { Length: > 0 } && g.Cover?.ImageId is { Length: > 0 } imageId)
                {
                    results.Add(new IgdbGame(g.Id, g.Name, imageId));
                }
            }

            if (page.Count < MaxPageSize)
            {
                break; // last page
            }
        }

        return results;
    }

    public async Task<byte[]?> DownloadCoverAsync(string imageId, CancellationToken ct = default)
    {
        var url = $"{_options.ImageBaseUrl}{_options.CoverSize}/{imageId}.jpg";
        var http = _httpFactory.CreateClient(ImageClientName);
        using var resp = await http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("IGDB cover image {ImageId} not found (404).", imageId);
            return null;
        }
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<List<IgdbGameDto>> PostGamesQueryAsync(string query, CancellationToken ct)
    {
        // One transparent retry: a cached token can be revoked/expired server-side; on 401 we
        // drop it and re-auth before failing.
        for (var attempt = 0; ; attempt++)
        {
            var token = await GetAccessTokenAsync(ct);
            await ThrottleAsync(ct);

            var http = _httpFactory.CreateClient(ApiClientName);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBaseUrl}games")
            {
                Content = new StringContent(query),
            };
            req.Headers.Add("Client-ID", _options.ClientId);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                _logger.LogWarning("IGDB returned 401; invalidating cached token and retrying.");
                InvalidateToken();
                continue;
            }

            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<List<IgdbGameDto>>(json, JsonOpts) ?? [];
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        // Refresh slightly ahead of expiry to avoid racing the boundary mid-request.
        if (_accessToken is not null && _clock.GetUtcNow() < _tokenExpiresAt - TimeSpan.FromMinutes(1))
        {
            return _accessToken;
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && _clock.GetUtcNow() < _tokenExpiresAt - TimeSpan.FromMinutes(1))
            {
                return _accessToken;
            }

            // Credentials go in the form body, not the query string — query strings are the
            // most log-prone part of a request (HttpClient logging, proxies, APM).
            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["grant_type"] = "client_credentials",
            });

            var http = _httpFactory.CreateClient(ApiClientName);
            using var resp = await http.PostAsync(_options.TokenUrl, body, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            var token = JsonSerializer.Deserialize<TwitchTokenDto>(json, JsonOpts)
                ?? throw new InvalidOperationException("IGDB token response was empty.");
            if (string.IsNullOrWhiteSpace(token.AccessToken))
            {
                throw new InvalidOperationException("IGDB token response had no access_token.");
            }

            _accessToken = token.AccessToken;
            _tokenExpiresAt = _clock.GetUtcNow().AddSeconds(token.ExpiresIn);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private void InvalidateToken()
    {
        _accessToken = null;
        _tokenExpiresAt = DateTimeOffset.MinValue;
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        var minInterval = TimeSpan.FromSeconds(1.0 / _options.MaxRequestsPerSecond);
        await _throttleLock.WaitAsync(ct);
        try
        {
            var now = _clock.GetUtcNow();
            var sinceLast = now - _lastRequestAt;
            if (sinceLast < minInterval)
            {
                await Task.Delay(minInterval - sinceLast, _clock, ct);
            }
            _lastRequestAt = _clock.GetUtcNow();
        }
        finally
        {
            _throttleLock.Release();
        }
    }

    private sealed record IgdbGameDto(int Id, string? Name, IgdbCoverDto? Cover);

    private sealed record IgdbCoverDto(int Id, [property: JsonPropertyName("image_id")] string? ImageId);

    private sealed record TwitchTokenDto(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
