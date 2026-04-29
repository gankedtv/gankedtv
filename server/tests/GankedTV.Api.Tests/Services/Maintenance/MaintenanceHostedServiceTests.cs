using FluentAssertions;
using GankedTV.Api.Services.Maintenance;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GankedTV.Api.Tests.Services.Maintenance;

public class MaintenanceHostedServiceTests
{
    private static MaintenanceHostedService Build(
        IServiceScopeFactory scopeFactory,
        MaintenanceOptions options)
    {
        var optsMonitor = Substitute.For<IOptionsMonitor<MaintenanceOptions>>();
        optsMonitor.CurrentValue.Returns(options);
        var minioMonitor = Substitute.For<IOptionsMonitor<MinioOptions>>();
        minioMonitor.CurrentValue.Returns(new MinioOptions { ClipsBucket = "clips", ThumbnailsBucket = "thumbnails" });

        return new MaintenanceHostedService(
            scopeFactory,
            optsMonitor,
            minioMonitor,
            TimeProvider.System,
            NullLogger<MaintenanceHostedService>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_DisabledOption_ReturnsImmediately()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var svc = Build(scopeFactory, new MaintenanceOptions { Enabled = false });

        await svc.StartAsync(CancellationToken.None);
        // BackgroundService.StopAsync waits for ExecuteAsync to finish; with Enabled=false it
        // exits the loop immediately.
        await svc.StopAsync(CancellationToken.None);

        scopeFactory.DidNotReceive().CreateScope();
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringWait_ExitsCleanly()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        // Long interval so the timer doesn't fire before we cancel.
        var svc = Build(scopeFactory, new MaintenanceOptions
        {
            Enabled = true,
            SweepInterval = TimeSpan.FromHours(1),
        });

        // Start with a dummy scope to avoid NRE on the immediate-startup tick.
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scope.ServiceProvider.Returns(sp);
        // Without a real DbContext registered, the immediate sweep will throw — caught and logged.
        scopeFactory.CreateScope().Returns(scope);

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        cts.Cancel();
        // StopAsync should observe cancellation and return without throwing.
        var act = async () => await svc.StopAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }
}
