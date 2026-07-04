using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GankedTV.Api.Services.Presence;

/// <summary>
/// Sliding-window online-presence set. A viewer key (<c>u:{id}</c> / <c>a:{cid}</c> / <c>ip:{ip}</c>)
/// is "online" if it was recorded within <see cref="PresenceOptions.WindowSeconds"/>.
///
/// When a shared <see cref="IConnectionMultiplexer"/> is registered (i.e. <c>REDIS_URL</c> is set)
/// presence lives in a Redis sorted set (<c>presence:online</c>, member = viewer key, score = last-seen
/// epoch-ms) so the count is cluster-wide across pods. Otherwise — and after any Redis failure — it
/// falls back to an in-process map: per-pod, but never a 500. This mirrors
/// <see cref="Caching.RedisRateLimiterFactory"/>; all integration tests force <c>REDIS_URL=null</c>,
/// so the in-process path is the one they exercise.
/// </summary>
public sealed class PresenceTracker(
    IServiceProvider services,
    IOptions<PresenceOptions> options,
    TimeProvider clock,
    ILogger<PresenceTracker> logger)
{
    private const string OnlineSetKey = "presence:online";

    // Per-pod fallback: viewer key → last-seen epoch-ms. Used when Redis is absent or after a
    // Redis failure. Bounded by opportunistic pruning on every read/write.
    private readonly ConcurrentDictionary<string, long> _local = new();
    private readonly PresenceOptions _options = options.Value;
    private int _degradedLogged;

    private long NowMs => clock.GetUtcNow().ToUnixTimeMilliseconds();

    private long CutoffMs => NowMs - (long)_options.WindowSeconds * 1000;

    /// <summary>Marks <paramref name="viewerKey"/> as active now.</summary>
    public async Task RecordAsync(string viewerKey, CancellationToken ct)
    {
        var now = NowMs;
        var db = TryGetDatabase();
        if (db is not null)
        {
            try
            {
                await db.SortedSetAddAsync(OnlineSetKey, viewerKey, now).WaitAsync(ct).ConfigureAwait(false);
                // Prune stale members so the set can't grow unbounded. Exclude.None removes
                // score <= cutoff, matching CountOnlineAsync's "score > cutoff" definition of online.
                await db.SortedSetRemoveRangeByScoreAsync(OnlineSetKey, double.NegativeInfinity, CutoffMs)
                    .WaitAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsRedisFailure(ex))
            {
                LogDegraded(ex);
            }
        }

        RecordLocal(viewerKey, now);
    }

    /// <summary>Current number of online viewers.</summary>
    public async Task<int> CountOnlineAsync(CancellationToken ct)
    {
        var db = TryGetDatabase();
        if (db is not null)
        {
            try
            {
                var count = await db
                    .SortedSetLengthAsync(OnlineSetKey, CutoffMs, double.PositiveInfinity, Exclude.Start)
                    .WaitAsync(ct).ConfigureAwait(false);
                return (int)Math.Min(count, int.MaxValue);
            }
            catch (Exception ex) when (IsRedisFailure(ex))
            {
                LogDegraded(ex);
            }
        }

        return CountLocal();
    }

    /// <summary>
    /// Returns the subset of <paramref name="viewerKeys"/> that are currently online — used to
    /// resolve which of a caller's follows are live.
    /// </summary>
    public async Task<IReadOnlySet<string>> GetOnlineSubsetAsync(
        IReadOnlyList<string> viewerKeys, CancellationToken ct)
    {
        if (viewerKeys.Count == 0)
        {
            return EmptySet;
        }

        var cutoff = CutoffMs;
        var db = TryGetDatabase();
        if (db is not null)
        {
            try
            {
                var members = new RedisValue[viewerKeys.Count];
                for (var i = 0; i < viewerKeys.Count; i++)
                {
                    members[i] = viewerKeys[i];
                }

                var scores = await db.SortedSetScoresAsync(OnlineSetKey, members)
                    .WaitAsync(ct).ConfigureAwait(false);

                var online = new HashSet<string>();
                for (var i = 0; i < viewerKeys.Count; i++)
                {
                    if (scores[i] is { } score && score > cutoff)
                    {
                        online.Add(viewerKeys[i]);
                    }
                }

                return online;
            }
            catch (Exception ex) when (IsRedisFailure(ex))
            {
                LogDegraded(ex);
            }
        }

        return GetOnlineSubsetLocal(viewerKeys, cutoff);
    }

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();

    private IDatabase? TryGetDatabase()
    {
        var multiplexer = services.GetService<IConnectionMultiplexer>();
        if (multiplexer is null)
        {
            return null;
        }

        try
        {
            return multiplexer.GetDatabase();
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            LogDegraded(ex);
            return null;
        }
    }

    private void RecordLocal(string viewerKey, long now)
    {
        _local[viewerKey] = now;
        PruneLocal(CutoffMs);
    }

    private int CountLocal()
    {
        var cutoff = CutoffMs;
        PruneLocal(cutoff);
        var count = 0;
        foreach (var ts in _local.Values)
        {
            if (ts > cutoff)
            {
                count++;
            }
        }

        return count;
    }

    private IReadOnlySet<string> GetOnlineSubsetLocal(IReadOnlyList<string> viewerKeys, long cutoff)
    {
        var online = new HashSet<string>();
        foreach (var key in viewerKeys)
        {
            if (_local.TryGetValue(key, out var ts) && ts > cutoff)
            {
                online.Add(key);
            }
        }

        return online;
    }

    private void PruneLocal(long cutoff)
    {
        foreach (var (key, ts) in _local)
        {
            if (ts <= cutoff)
            {
                _local.TryRemove(key, out _);
            }
        }
    }

    // Any infra-level failure degrades to the in-process map rather than 500. GetDatabase() can
    // throw ObjectDisposedException when the multiplexer is torn down during a shutdown/teardown race.
    private static bool IsRedisFailure(Exception ex) =>
        ex is RedisException or ObjectDisposedException or TimeoutException;

    private void LogDegraded(Exception ex)
    {
        // Presence is best-effort, so one warning per process is enough — don't spam a log line
        // on every poll while Redis is down.
        if (Interlocked.Exchange(ref _degradedLogged, 1) == 0)
        {
            logger.LogWarning(ex, "Redis unavailable for presence; degrading to in-process tracking.");
        }
    }
}
