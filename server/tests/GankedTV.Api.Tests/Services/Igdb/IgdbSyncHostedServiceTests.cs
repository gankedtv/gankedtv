using FluentAssertions;
using GankedTV.Api.Data;
using GankedTV.Api.Services.Igdb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GankedTV.Api.Tests.Services.Igdb;

public class IgdbSyncHostedServiceTests
{
    [Fact]
    public async Task Disabled_NeverRunsImporter()
    {
        var (svc, importer) = Build(enabled: false, configured: true);

        await svc.StartAsync(CancellationToken.None);
        // Give a scheduled run a window to fire — the assertion must hold even if it would have.
        await Task.Delay(75);
        await svc.StopAsync(CancellationToken.None);

        await importer.DidNotReceive().RunAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnabledButNoCredentials_Idles()
    {
        var (svc, importer) = Build(enabled: true, configured: false);

        await svc.StartAsync(CancellationToken.None);
        // Give a scheduled run a window to fire — the assertion must hold even if it would have.
        await Task.Delay(75);
        await svc.StopAsync(CancellationToken.None);

        await importer.DidNotReceive().RunAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnabledAndConfigured_RunsImporterOnStartup()
    {
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (svc, importer) = Build(enabled: true, configured: true);
        importer.RunAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            ran.TrySetResult();
            return GameCatalogImportResult.Skipped;
        });

        await svc.StartAsync(CancellationToken.None);
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await svc.StopAsync(CancellationToken.None);

        await importer.Received().RunAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnabledAndConfigured_WaitsForSchemaGateBeforeFirstSync()
    {
        var schemaReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = Substitute.For<ISchemaReadyGate>();
        gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(schemaReady.Task);
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (svc, importer) = Build(enabled: true, configured: true, gate);
        importer.RunAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            ran.TrySetResult();
            return GameCatalogImportResult.Skipped;
        });

        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        ran.Task.IsCompleted.Should().BeFalse();

        schemaReady.SetResult();
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await svc.StopAsync(CancellationToken.None);
    }

    private static (IgdbSyncHostedService svc, IGameCatalogImporter importer) Build(
        bool enabled, bool configured, ISchemaReadyGate? gate = null)
    {
        var importer = Substitute.For<IGameCatalogImporter>();
        importer.RunAsync(Arg.Any<CancellationToken>()).Returns(GameCatalogImportResult.Skipped);

        // The hosted service resolves the importer per-tick from a fresh scope, so wire a real
        // scope factory backed by a container that hands out the mock.
        var services = new ServiceCollection().AddScoped(_ => importer);
        if (gate is not null) services.AddSingleton(gate);
        var provider = services.BuildServiceProvider();

        var options = new StaticOptionsMonitor<IgdbOptions>(new IgdbOptions
        {
            SyncEnabled = enabled,
            ClientId = configured ? "cid" : "",
            ClientSecret = configured ? "secret" : "",
            SyncInterval = TimeSpan.FromDays(7),
        });

        var svc = new IgdbSyncHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<IgdbSyncHostedService>.Instance);
        return (svc, importer);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
