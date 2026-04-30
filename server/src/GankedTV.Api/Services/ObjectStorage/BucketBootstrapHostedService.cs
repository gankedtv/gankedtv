using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.ObjectStorage;

public sealed class BucketBootstrapHostedService : IHostedService
{
    private readonly IObjectStorageService _storage;
    private readonly S3Options _options;
    private readonly ILogger<BucketBootstrapHostedService> _logger;

    public BucketBootstrapHostedService(
        IObjectStorageService storage,
        IOptions<S3Options> options,
        ILogger<BucketBootstrapHostedService> logger)
    {
        _storage = storage;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _storage.EnsureBucketsAsync(cancellationToken);
            _logger.LogInformation(
                "Object storage ready at {Endpoint} (buckets: {Clips}, {Thumbnails})",
                _options.Endpoint, _options.ClipsBucket, _options.ThumbnailsBucket);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to bootstrap object storage buckets — aborting startup");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
