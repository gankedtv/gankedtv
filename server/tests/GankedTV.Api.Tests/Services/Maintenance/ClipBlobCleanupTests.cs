using FluentAssertions;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Maintenance;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace GankedTV.Api.Tests.Services.Maintenance;

public class ClipBlobCleanupTests
{
    private static readonly MinioOptions Buckets = new()
    {
        ClipsBucket = "clips",
        ThumbnailsBucket = "thumbnails",
    };

    private static Clip MakeClip(string videoKey = "v.mp4", string? thumbnailKey = "t.jpg") => new()
    {
        Id = Guid.NewGuid(),
        Title = "title",
        VideoKey = videoKey,
        ThumbnailKey = thumbnailKey,
    };

    [Fact]
    public async Task DeletesBothObjects_WhenThumbnailPresent()
    {
        var storage = Substitute.For<IObjectStorageService>();
        var clip = MakeClip();

        await ClipBlobCleanup.TryDeleteAsync(storage, Buckets, clip, NullLogger.Instance, CancellationToken.None);

        await storage.Received(1).DeleteObjectAsync("clips", "v.mp4", Arg.Any<CancellationToken>());
        await storage.Received(1).DeleteObjectAsync("thumbnails", "t.jpg", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SkipsThumbnail_WhenKeyIsNull()
    {
        var storage = Substitute.For<IObjectStorageService>();
        var clip = MakeClip(thumbnailKey: null);

        await ClipBlobCleanup.TryDeleteAsync(storage, Buckets, clip, NullLogger.Instance, CancellationToken.None);

        await storage.Received(1).DeleteObjectAsync("clips", "v.mp4", Arg.Any<CancellationToken>());
        await storage.DidNotReceive().DeleteObjectAsync("thumbnails", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VideoFailure_DoesNotSkipThumbnail()
    {
        var storage = Substitute.For<IObjectStorageService>();
        storage.DeleteObjectAsync("clips", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => Task.FromException(new InvalidOperationException("s3 down")));

        var logger = Substitute.For<ILogger>();
        var clip = MakeClip();

        await ClipBlobCleanup.TryDeleteAsync(storage, Buckets, clip, logger, CancellationToken.None);

        await storage.Received(1).DeleteObjectAsync("thumbnails", "t.jpg", Arg.Any<CancellationToken>());
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<InvalidOperationException>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task ThumbnailFailure_LogsWarning()
    {
        var storage = Substitute.For<IObjectStorageService>();
        storage.DeleteObjectAsync("thumbnails", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => Task.FromException(new InvalidOperationException("s3 down")));

        var logger = Substitute.For<ILogger>();
        var clip = MakeClip();

        await ClipBlobCleanup.TryDeleteAsync(storage, Buckets, clip, logger, CancellationToken.None);

        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<InvalidOperationException>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task BothSucceed_NoWarnings()
    {
        var storage = Substitute.For<IObjectStorageService>();
        var logger = Substitute.For<ILogger>();
        var clip = MakeClip();

        await ClipBlobCleanup.TryDeleteAsync(storage, Buckets, clip, logger, CancellationToken.None);

        logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
