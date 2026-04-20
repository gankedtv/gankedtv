using FluentAssertions;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GankedTV.Api.Tests.Services.ObjectStorage;

public class BucketBootstrapHostedServiceTests
{
    private static BucketBootstrapHostedService Build(IObjectStorageService storage)
    {
        var opts = Options.Create(new MinioOptions
        {
            Endpoint = "http://minio:9000",
            AccessKey = "k",
            SecretKey = "s",
            ClipsBucket = "clips",
            ThumbnailsBucket = "thumbnails",
        });
        return new BucketBootstrapHostedService(storage, opts, NullLogger<BucketBootstrapHostedService>.Instance);
    }

    [Fact]
    public async Task StartAsync_InvokesEnsureBucketsAsyncOnceWithToken()
    {
        var storage = Substitute.For<IObjectStorageService>();
        var cts = new CancellationTokenSource();

        await Build(storage).StartAsync(cts.Token);

        await storage.Received(1).EnsureBucketsAsync(cts.Token);
    }

    [Fact]
    public async Task StartAsync_PropagatesFailureAfterLoggingCritical()
    {
        var storage = Substitute.For<IObjectStorageService>();
        storage.EnsureBucketsAsync(Arg.Any<CancellationToken>())
            .Returns<Task>(_ => Task.FromException(new InvalidOperationException("minio down")));

        var act = async () => await Build(storage).StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("minio down");
    }

    [Fact]
    public async Task StopAsync_IsNoOp()
    {
        var storage = Substitute.For<IObjectStorageService>();

        await Build(storage).StopAsync(CancellationToken.None);

        await storage.DidNotReceive().EnsureBucketsAsync(Arg.Any<CancellationToken>());
    }
}
