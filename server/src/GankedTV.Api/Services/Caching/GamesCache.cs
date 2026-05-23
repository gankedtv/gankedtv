using GankedTV.Api.Contracts.Games;
using Microsoft.Extensions.Caching.Hybrid;

namespace GankedTV.Api.Services.Caching;

/// <summary>
/// Caches the games catalog read paths (browse list + per-game detail). Separate from
/// <see cref="IFeedCache"/> because the payloads aren't feed pages and the freshness model
/// differs: catalog rows (name/slug/cover) change only via the IGDB importer, while
/// <c>clipCount</c>/<c>hasClips</c> track the clip lifecycle. Rather than couple this to clip
/// writes, it is TTL-only (like trending) — a short entry self-heals, so a new game appears in
/// the browse list and an updated clipCount lands within the TTL.
/// </summary>
public interface IGamesCache
{
    ValueTask<IReadOnlyList<GameListItem>> GetOrCreateListAsync(
        string key, Func<CancellationToken, ValueTask<IReadOnlyList<GameListItem>>> factory, CancellationToken ct);

    ValueTask<GameDetail?> GetOrCreateDetailAsync(
        string key, Func<CancellationToken, ValueTask<GameDetail?>> factory, CancellationToken ct);
}

/// <inheritdoc cref="IGamesCache"/>
public sealed class GamesCache(HybridCache cache) : IGamesCache
{
    public const string GamesTag = "games";

    // Same short window as the feed cache; clipCount / hasClips membership may lag by up to the
    // TTL, which is acceptable for a browse catalog and self-heals without write-time coupling.
    private static readonly HybridCacheEntryOptions Entry = new()
    {
        Expiration = TimeSpan.FromSeconds(60),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };

    private static readonly string[] Tags = [GamesTag];

    public ValueTask<IReadOnlyList<GameListItem>> GetOrCreateListAsync(
        string key, Func<CancellationToken, ValueTask<IReadOnlyList<GameListItem>>> factory, CancellationToken ct) =>
        cache.GetOrCreateAsync(key, factory, Entry, Tags, ct);

    public ValueTask<GameDetail?> GetOrCreateDetailAsync(
        string key, Func<CancellationToken, ValueTask<GameDetail?>> factory, CancellationToken ct) =>
        cache.GetOrCreateAsync(key, factory, Entry, Tags, ct);
}
