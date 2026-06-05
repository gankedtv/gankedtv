using System.Threading.RateLimiting;
using FluentAssertions;
using GankedTV.Api.Services.Caching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using Xunit;

namespace GankedTV.Api.Tests.Services.Caching;

public class RedisRateLimiterFactoryTests
{
    private static RedisRateLimiterFactory FactoryWith(IConnectionMultiplexer? multiplexer)
    {
        var services = new ServiceCollection();
        if (multiplexer is not null)
        {
            services.AddSingleton(multiplexer);
        }
        var sp = services.BuildServiceProvider();
        return new RedisRateLimiterFactory(sp, NullLoggerFactory.Instance);
    }

    // {current, ttlMs} multi-bulk reply, matching the Lua script's return shape.
    private static RedisResult ScriptReply(long current, long ttlMs) =>
        RedisResult.Create(new RedisValue[] { current, ttlMs });

    private static (IConnectionMultiplexer mux, IDatabase db) MuxReturning(RedisResult reply)
    {
        var db = Substitute.For<IDatabase>();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(reply);
        db.ScriptEvaluate(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(reply);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        return (mux, db);
    }

    [Fact]
    public async Task NoMultiplexer_EnforcesLimitInProcess()
    {
        var limiter = FactoryWith(null).Create("clips-write", "u:1", permitLimit: 2, TimeSpan.FromMinutes(1));

        (await limiter.AcquireAsync(1)).IsAcquired.Should().BeTrue();
        (await limiter.AcquireAsync(1)).IsAcquired.Should().BeTrue();
        using var rejected = await limiter.AcquireAsync(1);
        rejected.IsAcquired.Should().BeFalse("the third acquire exceeds the local fixed window of 2");
    }

    [Fact]
    public async Task Redis_WithinLimit_IsAcquired()
    {
        var (mux, db) = MuxReturning(ScriptReply(current: 1, ttlMs: 60_000));
        var limiter = FactoryWith(mux).Create("clips-write", "u:1", permitLimit: 30, TimeSpan.FromMinutes(1));

        using var lease = await limiter.AcquireAsync(1);

        lease.IsAcquired.Should().BeTrue();
        // Pin the Lua contract: a regression that swaps INCRBY/PEXPIRE, reorders KEYS/ARGV, or
        // reshapes the call would otherwise slip past the lease-only assertions.
        await db.Received(1).ScriptEvaluateAsync(
            Arg.Is<string>(s => s.Contains("INCRBY") && s.Contains("PEXPIRE")),
            Arg.Is<RedisKey[]>(k => k.Length == 1 && k[0] == "rl:clips-write:u:1"),
            Arg.Is<RedisValue[]>(v => v.Length == 2 && (long)v[0] == 1 && (long)v[1] == 60_000),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Redis_OverLimit_RejectsWithRetryAfterFromTtl()
    {
        // count 31 > limit 30, 1500ms remaining → Retry-After rounds up to 2 seconds.
        var (mux, _) = MuxReturning(ScriptReply(current: 31, ttlMs: 1500));
        var limiter = FactoryWith(mux).Create("clips-write", "u:1", permitLimit: 30, TimeSpan.FromMinutes(1));

        using var lease = await limiter.AcquireAsync(1);

        lease.IsAcquired.Should().BeFalse();
        lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Redis_Throws_FallsBackToInProcessLimiter()
    {
        var db = Substitute.For<IDatabase>();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var limiter = FactoryWith(mux).Create("clips-write", "u:1", permitLimit: 1, TimeSpan.FromMinutes(1));

        // First acquire degrades to the in-process limiter and is granted; the second exceeds
        // the local limit of 1 — proving the fallback enforces (degraded, not fail-open).
        (await limiter.AcquireAsync(1)).IsAcquired.Should().BeTrue();
        using var rejected = await limiter.AcquireAsync(1);
        rejected.IsAcquired.Should().BeFalse();
    }

    [Fact]
    public void Redis_SyncAttemptAcquire_Works()
    {
        var (mux, _) = MuxReturning(ScriptReply(current: 1, ttlMs: 60_000));
        var limiter = FactoryWith(mux).Create("auth-credentials", "ip:1.2.3.4", permitLimit: 5, TimeSpan.FromMinutes(1));

        using var lease = limiter.AttemptAcquire(1);

        lease.IsAcquired.Should().BeTrue();
    }

    [Fact]
    public void AcquiredLease_ExposesNoRetryAfterMetadata()
    {
        RedisRateLimitLease.Acquired.IsAcquired.Should().BeTrue();
        RedisRateLimitLease.Acquired.MetadataNames.Should().BeEmpty();
        RedisRateLimitLease.Acquired.TryGetMetadata(MetadataName.RetryAfter, out _).Should().BeFalse();
    }

    [Fact]
    public void RejectedLease_ExposesRetryAfterMetadata()
    {
        var lease = new RedisRateLimitLease(TimeSpan.FromSeconds(5));

        lease.IsAcquired.Should().BeFalse();
        lease.MetadataNames.Should().Contain(MetadataName.RetryAfter.Name);
        lease.TryGetMetadata(MetadataName.RetryAfter, out var value).Should().BeTrue();
        value.Should().Be(TimeSpan.FromSeconds(5));
        // A non-matching metadata name returns false even though Retry-After is set.
        lease.TryGetMetadata("unrelated-metadata", out _).Should().BeFalse();
    }

    public static TheoryData<Exception> InfraFailures() =>
        new()
        {
            new ObjectDisposedException("multiplexer"), // torn down during a shutdown/teardown race
            new TimeoutException("client-side timeout"),
        };

    [Theory]
    [MemberData(nameof(InfraFailures))]
    public async Task Redis_InfraFailure_DegradesToInProcessLimiter(Exception failure)
    {
        var db = Substitute.For<IDatabase>();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(failure);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var limiter = FactoryWith(mux).Create("clips-write", "u:1", permitLimit: 1, TimeSpan.FromMinutes(1));

        (await limiter.AcquireAsync(1)).IsAcquired.Should().BeTrue();
        using var rejected = await limiter.AcquireAsync(1);
        rejected.IsAcquired.Should().BeFalse("the degraded in-process limiter still enforces the limit");
    }

    [Fact]
    public async Task Dispose_AfterFallbackCreated_DisposesFallback()
    {
        var db = Substitute.For<IDatabase>();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var limiter = FactoryWith(mux).Create("clips-write", "u:1", permitLimit: 1, TimeSpan.FromMinutes(1));
        await limiter.AcquireAsync(1); // forces lazy fallback creation

        var dispose = () => limiter.Dispose();
        dispose.Should().NotThrow();
    }

    [Fact]
    public void Dispose_WithoutFallback_IsNoOp()
    {
        var (mux, _) = MuxReturning(ScriptReply(current: 1, ttlMs: 60_000));
        var limiter = FactoryWith(mux).Create("clips-write", "u:1", permitLimit: 1, TimeSpan.FromMinutes(1));

        // Fallback was never created (no Redis failure), so Dispose takes the no-op branch.
        var dispose = () => limiter.Dispose();
        dispose.Should().NotThrow();
    }
}
