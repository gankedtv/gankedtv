using System.Collections.Concurrent;

namespace GankedTV.Api.Services.Igdb;

/// <summary>
/// Remembers which IGDB search terms have already been attempted, so repeated misses (typeahead
/// keystrokes, abuse) don't burn IGDB's 4 req/s budget. Deliberately not the app-wide
/// <c>IMemoryCache</c>: search terms are attacker-controlled and high-cardinality, so this holds
/// its own bounded set instead of growing the shared cache.
/// </summary>
public sealed class GameSearchMemo(TimeProvider clock)
{
    // Hard cap on remembered terms. Only reachable under abuse (the search endpoint is rate
    // limited); overflowing just re-queries IGDB for a term, so dropping entries is safe.
    internal const int MaxEntries = 10_000;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _until = new(StringComparer.Ordinal);

    public bool IsMemoized(string term)
    {
        if (!_until.TryGetValue(term, out var expiresAt))
        {
            return false;
        }

        if (expiresAt > clock.GetUtcNow())
        {
            return true;
        }

        _until.TryRemove(new KeyValuePair<string, DateTimeOffset>(term, expiresAt));
        return false;
    }

    public void Remember(string term, TimeSpan ttl)
    {
        var now = clock.GetUtcNow();
        _until[term] = now + ttl;
        if (_until.Count > MaxEntries)
        {
            Prune(now);
        }
    }

    public void Forget(string term) => _until.TryRemove(term, out _);

    private void Prune(DateTimeOffset now)
    {
        foreach (var (term, expiresAt) in _until)
        {
            if (expiresAt <= now)
            {
                _until.TryRemove(new KeyValuePair<string, DateTimeOffset>(term, expiresAt));
            }
        }

        // Nothing expired and still over the cap ⇒ evict the soonest-to-expire entries.
        var overflow = _until.Count - MaxEntries;
        if (overflow <= 0)
        {
            return;
        }

        foreach (var (term, _) in _until.OrderBy(kv => kv.Value).Take(overflow))
        {
            _until.TryRemove(term, out _);
        }
    }
}
