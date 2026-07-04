using FluentAssertions;
using GankedTV.Api.Services.Presence;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using Xunit;

namespace GankedTV.Api.Tests.Services.Presence;

public class PresenceTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    private static PresenceTracker TrackerWith(
        IConnectionMultiplexer? multiplexer, FakeClock clock, int windowSeconds = 60, int cap = 20)
    {
        var services = new ServiceCollection();
        if (multiplexer is not null)
        {
            services.AddSingleton(multiplexer);
        }

        var options = Options.Create(new PresenceOptions
        {
            WindowSeconds = windowSeconds,
            FollowsOnlineCap = cap,
        });
        return new PresenceTracker(
            services.BuildServiceProvider(), options, clock, NullLogger<PresenceTracker>.Instance);
    }

    // ---- In-process path (no Redis) — the one all integration tests exercise ----

    [Fact]
    public async Task InProcess_RecordThenCount_CountsTheViewer()
    {
        var tracker = TrackerWith(null, new FakeClock(Start));

        await tracker.RecordAsync("u:1", CancellationToken.None);

        (await tracker.CountOnlineAsync(CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task InProcess_DistinctViewers_CountSeparately()
    {
        var tracker = TrackerWith(null, new FakeClock(Start));

        await tracker.RecordAsync("u:1", CancellationToken.None);
        await tracker.RecordAsync("a:browser-2", CancellationToken.None);
        await tracker.RecordAsync("ip:1.2.3.4", CancellationToken.None);

        (await tracker.CountOnlineAsync(CancellationToken.None)).Should().Be(3);
    }

    [Fact]
    public async Task InProcess_SameViewerTwice_CountsOnce()
    {
        var tracker = TrackerWith(null, new FakeClock(Start));

        await tracker.RecordAsync("u:1", CancellationToken.None);
        await tracker.RecordAsync("u:1", CancellationToken.None);

        (await tracker.CountOnlineAsync(CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task InProcess_StaleViewer_IsPrunedAndNotCounted()
    {
        var clock = new FakeClock(Start);
        var tracker = TrackerWith(null, clock, windowSeconds: 60);

        await tracker.RecordAsync("u:1", CancellationToken.None);
        clock.Set(Start.AddSeconds(61)); // past the 60s window

        (await tracker.CountOnlineAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task InProcess_ReRecord_RefreshesTheWindow()
    {
        var clock = new FakeClock(Start);
        var tracker = TrackerWith(null, clock, windowSeconds: 60);

        await tracker.RecordAsync("u:1", CancellationToken.None);
        clock.Set(Start.AddSeconds(40));
        await tracker.RecordAsync("u:1", CancellationToken.None); // refresh
        clock.Set(Start.AddSeconds(80)); // 80s after first sight, 40s after refresh

        (await tracker.CountOnlineAsync(CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task InProcess_GetOnlineSubset_ReturnsOnlyFreshKeys()
    {
        var clock = new FakeClock(Start);
        var tracker = TrackerWith(null, clock, windowSeconds: 60);

        await tracker.RecordAsync("u:a", CancellationToken.None);
        clock.Set(Start.AddSeconds(70)); // u:a now stale
        await tracker.RecordAsync("u:b", CancellationToken.None);

        var online = await tracker.GetOnlineSubsetAsync(["u:a", "u:b", "u:c"], CancellationToken.None);

        online.Should().BeEquivalentTo(["u:b"]);
    }

    [Fact]
    public async Task GetOnlineSubset_EmptyInput_ReturnsEmpty()
    {
        var tracker = TrackerWith(null, new FakeClock(Start));

        var online = await tracker.GetOnlineSubsetAsync([], CancellationToken.None);

        online.Should().BeEmpty();
    }

    // ---- Redis path (mocked IConnectionMultiplexer / IDatabase) ----

    [Fact]
    public async Task Redis_Record_AddsToSortedSetAndPrunes()
    {
        var db = Substitute.For<IDatabase>();
        var mux = MuxReturning(db);
        var tracker = TrackerWith(mux, new FakeClock(Start));

        await tracker.RecordAsync("u:1", CancellationToken.None);

        var expectedScore = Start.ToUnixTimeMilliseconds();
        await db.Received(1).SortedSetAddAsync(
            (RedisKey)"presence:online", (RedisValue)"u:1", expectedScore);
        await db.Received(1).SortedSetRemoveRangeByScoreAsync(
            (RedisKey)"presence:online", double.NegativeInfinity, Arg.Any<double>());
    }

    [Fact]
    public async Task Redis_Count_UsesZCountOverTheWindow()
    {
        var db = Substitute.For<IDatabase>();
        db.SortedSetLengthAsync(
                Arg.Any<RedisKey>(), Arg.Any<double>(), Arg.Any<double>(),
                Arg.Any<Exclude>(), Arg.Any<CommandFlags>())
            .Returns(42L);
        var tracker = TrackerWith(MuxReturning(db), new FakeClock(Start));

        (await tracker.CountOnlineAsync(CancellationToken.None)).Should().Be(42);

        var cutoff = Start.ToUnixTimeMilliseconds() - 60_000;
        await db.Received(1).SortedSetLengthAsync(
            (RedisKey)"presence:online", cutoff, double.PositiveInfinity, Exclude.Start, CommandFlags.None);
    }

    [Fact]
    public async Task Redis_GetOnlineSubset_FiltersByScore()
    {
        var db = Substitute.For<IDatabase>();
        var now = Start.ToUnixTimeMilliseconds();
        var cutoff = now - 60_000;
        // u:a fresh, u:b never seen (null), u:c stale (== cutoff, not strictly greater).
        db.SortedSetScoresAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(new double?[] { now, null, cutoff });
        var tracker = TrackerWith(MuxReturning(db), new FakeClock(Start));

        var online = await tracker.GetOnlineSubsetAsync(["u:a", "u:b", "u:c"], CancellationToken.None);

        online.Should().BeEquivalentTo(["u:a"]);
    }

    [Fact]
    public async Task Redis_RecordFailure_DegradesToInProcess()
    {
        var db = Substitute.For<IDatabase>();
        db.SortedSetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<double>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        db.SortedSetLengthAsync(
                Arg.Any<RedisKey>(), Arg.Any<double>(), Arg.Any<double>(),
                Arg.Any<Exclude>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        var tracker = TrackerWith(MuxReturning(db), new FakeClock(Start));

        // Both ops fall back to the in-process map, so the record is still counted.
        await tracker.RecordAsync("u:1", CancellationToken.None);
        (await tracker.CountOnlineAsync(CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task Redis_GetDatabaseThrows_DegradesToInProcess()
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object>())
            .Throws(new ObjectDisposedException("multiplexer"));
        var tracker = TrackerWith(mux, new FakeClock(Start));

        await tracker.RecordAsync("u:1", CancellationToken.None);
        (await tracker.CountOnlineAsync(CancellationToken.None)).Should().Be(1);
    }

    private static IConnectionMultiplexer MuxReturning(IDatabase db)
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        return mux;
    }
}
