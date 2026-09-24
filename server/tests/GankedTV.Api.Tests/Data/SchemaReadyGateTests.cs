using FluentAssertions;
using GankedTV.Api.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GankedTV.Api.Tests.Data;

public sealed class SchemaReadyGateTests : IDisposable
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(5);

    // A gate left waiting keeps polling every FastPoll for the rest of the run.
    private readonly List<SchemaReadyGate> _gates = new();

    public void Dispose()
    {
        foreach (var gate in _gates) gate.Dispose();
    }

    private (SchemaReadyGate gate, IPendingMigrationsProbe probe, LevelCountingLogger logger) Build()
    {
        var probe = Substitute.For<IPendingMigrationsProbe>();
        var services = new ServiceCollection();
        services.AddScoped(_ => probe);
        var logger = new LevelCountingLogger();
        var gate = new SchemaReadyGate(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), logger, FastPoll);
        _gates.Add(gate);
        return (gate, probe, logger);
    }

    private static IReadOnlyList<string> Pending(params string[] names) => names;

    [Fact]
    public async Task NothingPending_CompletesAndCachesTheVerdict()
    {
        var (gate, probe, logger) = Build();
        probe.GetPendingAsync(Arg.Any<CancellationToken>()).Returns(Pending());

        await gate.WaitUntilReadyAsync(CancellationToken.None);
        await gate.WaitUntilReadyAsync(CancellationToken.None);

        await probe.Received(1).GetPendingAsync(Arg.Any<CancellationToken>());
        logger.Count(LogLevel.Warning).Should().Be(0);
    }

    [Fact]
    public async Task PendingThenApplied_WaitsThenCompletes()
    {
        var (gate, probe, logger) = Build();
        probe.GetPendingAsync(Arg.Any<CancellationToken>())
            .Returns(Pending("20260825_AddClipCrop"), Pending("20260825_AddClipCrop"), Pending());

        await gate.WaitUntilReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        await probe.Received(3).GetPendingAsync(Arg.Any<CancellationToken>());
        logger.Count(LogLevel.Warning).Should().Be(1);
        logger.Count(LogLevel.Information).Should().Be(1);
    }

    [Fact]
    public async Task ProbeThrows_KeepsWaitingInsteadOfFailing()
    {
        var (gate, probe, logger) = Build();
        probe.GetPendingAsync(Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new InvalidOperationException("db down"),
                _ => Pending());

        await gate.WaitUntilReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        await probe.Received(2).GetPendingAsync(Arg.Any<CancellationToken>());
        logger.Count(LogLevel.Warning).Should().Be(1);
    }

    [Fact]
    public async Task CancelledWhileWaiting_ThrowsOperationCanceled()
    {
        var (gate, probe, _) = Build();
        probe.GetPendingAsync(Arg.Any<CancellationToken>()).Returns(Pending("m1"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => gate.WaitUntilReadyAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ConcurrentWaiters_AnnounceTheWaitOnce()
    {
        var (gate, probe, logger) = Build();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.GetPendingAsync(Arg.Any<CancellationToken>())
            .Returns(_ => release.Task.IsCompleted ? Pending() : Pending("m1"));

        var waiters = Enumerable.Range(0, 5)
            .Select(_ => gate.WaitUntilReadyAsync(CancellationToken.None))
            .ToArray();
        await Task.Delay(50);
        release.SetResult();
        await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5));

        logger.Count(LogLevel.Warning).Should().Be(1);
        logger.Count(LogLevel.Information).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentWaiters_ShareOneProbeInFlight()
    {
        var (gate, probe, _) = Build();
        var answer = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.GetPendingAsync(Arg.Any<CancellationToken>()).Returns(answer.Task);

        var waiters = Enumerable.Range(0, 5)
            .Select(_ => gate.WaitUntilReadyAsync(CancellationToken.None))
            .ToArray();
        await Task.Delay(50);

        await probe.Received(1).GetPendingAsync(Arg.Any<CancellationToken>());
        answer.SetResult(Pending());
        await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OneWaiterCancelling_DoesNotReleaseOrCancelTheOthers()
    {
        var (gate, probe, _) = Build();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.GetPendingAsync(Arg.Any<CancellationToken>())
            .Returns(_ => release.Task.IsCompleted ? Pending() : Pending("m1"));
        using var cts = new CancellationTokenSource();

        var cancelled = gate.WaitUntilReadyAsync(cts.Token);
        var patient = gate.WaitUntilReadyAsync(CancellationToken.None);
        cts.Cancel();

        await cancelled.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        patient.IsCompleted.Should().BeFalse();
        release.SetResult();
        await patient.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Dispose_StopsTheWaitForEveryone()
    {
        var (gate, probe, _) = Build();
        probe.GetPendingAsync(Arg.Any<CancellationToken>()).Returns(Pending("m1"));

        var waiter = gate.WaitUntilReadyAsync(CancellationToken.None);
        await Task.Delay(20);
        gate.Dispose();

        await waiter.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        gate.Invoking(g => g.Dispose()).Should().NotThrow();
    }

    [Fact]
    public async Task WaitForSchema_NoGateRegistered_CompletesImmediately()
    {
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        await scopes.WaitForSchemaAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WaitForSchema_UsesTheRegisteredGate()
    {
        var gate = Substitute.For<ISchemaReadyGate>();
        var services = new ServiceCollection();
        services.AddSingleton(gate);
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        await scopes.WaitForSchemaAsync(CancellationToken.None);

        await gate.Received(1).WaitUntilReadyAsync(Arg.Any<CancellationToken>());
    }

    private sealed class LevelCountingLogger : ILogger<SchemaReadyGate>
    {
        private readonly List<LogLevel> _levels = new();

        public int Count(LogLevel level)
        {
            lock (_levels) return _levels.Count(l => l == level);
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_levels) _levels.Add(logLevel);
        }
    }
}
