using Amazon.S3;
using Amazon.S3.Model;
using Testcontainers.Minio;

namespace GankedTV.Api.Tests.TestSupport;

// Sister fixture to PostgresFixture: spins up an S3-compatible storage container
// (currently MinIO via Testcontainers.Minio — that's the lightest available module; if we
// later swap to a different S3 backend, only the container builder below changes) so
// end-to-end storage tests exercise the actual S3 client / bucket / key plumbing in the
// production object-storage service. The substituted IObjectStorageService used elsewhere
// echoes whatever key is passed in, so it can't catch key-construction bugs (the
// `clips/clips/{userId}/...` issue was the motivating example).
public sealed class S3Fixture : IAsyncLifetime
{
    public const string TestAccessKey = "minioadmin";
    public const string TestSecretKey = "minioadmin";
    public const string ClipsBucket = "clips";
    public const string ThumbnailsBucket = "thumbnails";

    // The container backend is MinIO — Testcontainers.Minio is currently the only
    // lightweight S3-compatible module. Swapping to a different S3 server (LocalStack,
    // SeaweedFS, Garage, ...) would replace this builder and the port constant; the rest
    // of the fixture is generic S3 SDK usage.
    private readonly MinioContainer _container = new MinioBuilder("minio/minio:RELEASE.2024-12-18T13-15-44Z")
        .WithUsername(TestAccessKey)
        .WithPassword(TestSecretKey)
        .Build();

    private IAmazonS3? _admin;

    // 9000 is MinIO's S3 API port (9001 is the web console; we don't need it for tests).
    private const ushort S3ApiPort = 9000;

    public string Endpoint => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(S3ApiPort)}";

    public string AccessKey => TestAccessKey;
    public string SecretKey => TestSecretKey;

    // Test-side S3 client kept separate from the API's IAmazonS3. Tests use this to assert
    // object presence / absence without going through the code under test.
    public IAmazonS3 AdminClient => _admin
        ?? throw new InvalidOperationException("S3Fixture not initialised; call InitializeAsync first.");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _admin = new AmazonS3Client(AccessKey, SecretKey, new AmazonS3Config
        {
            ServiceURL = Endpoint,
            ForcePathStyle = true,
        });

        foreach (var bucket in new[] { ClipsBucket, ThumbnailsBucket })
        {
            try
            {
                await _admin.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
            }
            catch (AmazonS3Exception ex) when (
                ex.ErrorCode == "BucketAlreadyOwnedByYou" || ex.ErrorCode == "BucketAlreadyExists")
            {
                // Idempotent — fixture restart with a recycled volume hits this. Safe to ignore.
            }
        }
    }

    // Per-test reset analog of Respawn for storage. Lists and serially deletes every object
    // in both buckets. Serial DeleteObjectAsync (rather than batch DeleteObjectsAsync) is
    // intentional: the current MinIO backend requires Content-MD5 on the multi-delete
    // request, which the modern AWS SDK no longer attaches by default — batch delete fails
    // with "Missing required header for this request: Content-Md5". Buckets hold 0–2 objects
    // per test in practice, so the per-object cost is negligible. (If we switch to a backend
    // that doesn't require the header, batch delete would also work — single-object delete
    // is the safe lowest-common-denominator.)
    public async Task ResetAsync(CancellationToken ct = default)
    {
        foreach (var bucket in new[] { ClipsBucket, ThumbnailsBucket })
        {
            string? continuation = null;
            do
            {
                var list = await AdminClient.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = bucket,
                    ContinuationToken = continuation,
                }, ct);

                if (list.S3Objects is { Count: > 0 } objects)
                {
                    foreach (var obj in objects)
                    {
                        await AdminClient.DeleteObjectAsync(bucket, obj.Key, ct);
                    }
                }

                continuation = list.IsTruncated == true ? list.NextContinuationToken : null;
            }
            while (continuation is not null);
        }
    }

    public async Task DisposeAsync()
    {
        _admin?.Dispose();
        await _container.DisposeAsync();
    }
}
