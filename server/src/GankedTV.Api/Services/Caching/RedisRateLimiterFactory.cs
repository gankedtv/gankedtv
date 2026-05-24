using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace GankedTV.Api.Services.Caching;

/// <summary>
/// Builds the per-partition rate limiter used by the clip-write and credentials policies.
/// When a shared <see cref="IConnectionMultiplexer"/> is registered (i.e. <c>REDIS_URL</c> is
/// set) it returns a Redis fixed-window limiter so the limit is enforced cluster-wide across
/// pods; otherwise it returns the in-process <see cref="FixedWindowRateLimiter"/> — identical
/// to the pre-Redis behaviour. Resolved once per partition by the policy callbacks in
/// ClipsRateLimiting / AuthRateLimiting (the framework caches limiter instances per key).
/// </summary>
public sealed class RedisRateLimiterFactory(IServiceProvider services, ILoggerFactory loggerFactory)
{
    /// <param name="policy">Namespaces the Redis key so the two policies never share a bucket.</param>
    /// <param name="partitionKey">The caller bucket (e.g. <c>u:{userId}</c> / <c>ip:{ip}</c>).</param>
    /// <remarks>
    /// Callers must acquire with <c>permitCount &lt;= permitLimit</c>. The Redis path increments
    /// before checking, so a single over-limit acquisition would poison the window for its whole
    /// duration. Every current caller acquires exactly 1 permit, well under both policies' limits.
    /// </remarks>
    public RateLimiter Create(string policy, string partitionKey, int permitLimit, TimeSpan window)
    {
        var multiplexer = services.GetService<IConnectionMultiplexer>();
        if (multiplexer is null)
        {
            return CreateLocal(permitLimit, window);
        }

        return new RedisFixedWindowRateLimiter(
            multiplexer,
            $"rl:{policy}:{partitionKey}",
            permitLimit,
            window,
            () => CreateLocal(permitLimit, window),
            loggerFactory.CreateLogger<RedisFixedWindowRateLimiter>());
    }

    internal static FixedWindowRateLimiter CreateLocal(int permitLimit, TimeSpan window) =>
        new(new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
}

/// <summary>
/// Cluster-wide fixed-window limiter backed by an atomic Redis INCRBY+PEXPIRE script. On any
/// Redis failure it transparently delegates to a lazily-created in-process limiter — so a Redis
/// outage degrades to per-pod limiting (never unlimited, never a 500). The same partition's
/// limiter instance is reused by the framework, so the local fallback's window state persists.
/// </summary>
internal sealed class RedisFixedWindowRateLimiter : RateLimiter
{
    // Fixed window via a single round trip. INCRBY creates the key at 0 implicitly; the first
    // increment in a window (or any key that somehow lost its TTL) gets PEXPIRE so the window
    // always expires. Returns {count, ttlMs} — the caller compares count to the permit limit
    // and derives Retry-After from the remaining TTL.
    //
    // Note: the INCRBY runs before the limit check, so a rejected request still counts toward the
    // window. That makes this marginally stricter than the in-process FixedWindowRateLimiter
    // (which doesn't consume on rejection) — never looser — which is the safe direction for a
    // limiter and the conventional Redis fixed-window tradeoff (no decrement race on rejection).
    private const string Script = """
        local current = redis.call('INCRBY', KEYS[1], ARGV[1])
        local ttl = redis.call('PTTL', KEYS[1])
        if ttl < 0 then
          redis.call('PEXPIRE', KEYS[1], ARGV[2])
          ttl = tonumber(ARGV[2])
        end
        return {current, ttl}
        """;

    private readonly IConnectionMultiplexer _multiplexer;
    private readonly RedisKey _key;
    private readonly int _permitLimit;
    private readonly long _windowMs;
    private readonly Lazy<FixedWindowRateLimiter> _fallback;
    private readonly ILogger<RedisFixedWindowRateLimiter> _logger;
    private int _degradedLogged;
    private long _lastActivityTicks = Environment.TickCount64;

    public RedisFixedWindowRateLimiter(
        IConnectionMultiplexer multiplexer,
        string key,
        int permitLimit,
        TimeSpan window,
        Func<FixedWindowRateLimiter> fallbackFactory,
        ILogger<RedisFixedWindowRateLimiter> logger)
    {
        _multiplexer = multiplexer;
        _key = key;
        _permitLimit = permitLimit;
        _windowMs = (long)window.TotalMilliseconds;
        _fallback = new Lazy<FixedWindowRateLimiter>(fallbackFactory);
        _logger = logger;
    }

    // Report idle time since the last acquire so the partitioned limiter's reaper can evict
    // stale per-user / per-IP limiters — returning null (the default) would pin every partition
    // key ever seen in memory for the life of the process.
    public override TimeSpan? IdleDuration =>
        TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastActivityTicks));

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _lastActivityTicks, Environment.TickCount64);
        try
        {
            var db = _multiplexer.GetDatabase();
            // ScriptEvaluateAsync has no CancellationToken overload, so WaitAsync releases the
            // request when the client aborts (e.g. a stuck Redis) instead of pinning it until
            // AsyncTimeout. OperationCanceledException then propagates — deliberately not caught.
            var result = await db.ScriptEvaluateAsync(Script, [_key], [permitCount, _windowMs])
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return Evaluate(result);
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            LogDegraded(ex);
            return await _fallback.Value.AcquireAsync(permitCount, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        Interlocked.Exchange(ref _lastActivityTicks, Environment.TickCount64);
        try
        {
            var db = _multiplexer.GetDatabase();
            var result = db.ScriptEvaluate(Script, [_key], [permitCount, _windowMs]);
            return Evaluate(result);
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            LogDegraded(ex);
            return _fallback.Value.AttemptAcquire(permitCount);
        }
    }

    // Any infra-level failure must degrade to the in-process limiter rather than 500. Besides the
    // Redis exception types, GetDatabase() throws ObjectDisposedException when the multiplexer is
    // torn down during a graceful-shutdown / test-teardown race. Cancellation is NOT a failure —
    // it must propagate so an aborted request stops promptly.
    private static bool IsRedisFailure(Exception ex) =>
        ex is RedisException or RedisTimeoutException or ObjectDisposedException or TimeoutException;

    private RateLimitLease Evaluate(RedisResult result)
    {
        // A successful round trip means Redis is back; re-arm the degraded warning so a later
        // outage logs again (otherwise a flapping Redis logs only once for the life of the pod).
        Interlocked.Exchange(ref _degradedLogged, 0);

        var values = (RedisResult[])result!;
        var current = (long)values[0];
        var ttlMs = (long)values[1];
        if (current <= _permitLimit)
        {
            return RedisRateLimitLease.Acquired;
        }

        // Whole seconds, rounded up, per RFC 6585 Retry-After. A sub-second remainder still
        // costs the client a full second of back-off — never advise retrying before the window
        // actually rolls over.
        var retryAfter = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, ttlMs) / 1000.0));
        return new RedisRateLimitLease(retryAfter);
    }

    private void LogDegraded(Exception ex)
    {
        if (Interlocked.Exchange(ref _degradedLogged, 1) == 0)
        {
            _logger.LogWarning(ex,
                "Redis rate limiter unavailable for {Key}; degrading to in-process limiting until it recovers.",
                (string?)_key);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _fallback.IsValueCreated)
        {
            _fallback.Value.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Minimal lease for the Redis limiter: granted leases carry no metadata; rejected leases carry
/// the <c>Retry-After</c> the shared OnRejected handler stamps onto the 429.
/// </summary>
internal sealed class RedisRateLimitLease : RateLimitLease
{
    private static readonly string[] RetryAfterMetadata = [MetadataName.RetryAfter.Name];

    /// <summary>Shared granted lease — acquired leases are stateless, so one instance suffices.</summary>
    public static readonly RedisRateLimitLease Acquired = new();

    private readonly TimeSpan? _retryAfter;

    private RedisRateLimitLease() => _retryAfter = null;

    public RedisRateLimitLease(TimeSpan retryAfter) => _retryAfter = retryAfter;

    public override bool IsAcquired => _retryAfter is null;

    public override IEnumerable<string> MetadataNames =>
        _retryAfter is null ? Array.Empty<string>() : RetryAfterMetadata;

    public override bool TryGetMetadata(string metadataName, out object? metadata)
    {
        if (_retryAfter is { } value && metadataName == MetadataName.RetryAfter.Name)
        {
            metadata = value;
            return true;
        }

        metadata = null;
        return false;
    }
}
