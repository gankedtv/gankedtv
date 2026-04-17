namespace GankedTV.Api.Services.ObjectStorage;

public sealed class BucketBootstrapHostedService : IHostedService
{
    private readonly IObjectStorageService _storage;
    private readonly ILogger<BucketBootstrapHostedService> _logger;

    public BucketBootstrapHostedService(
        IObjectStorageService storage,
        ILogger<BucketBootstrapHostedService> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _storage.EnsureBucketsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to bootstrap object storage buckets — aborting startup");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
