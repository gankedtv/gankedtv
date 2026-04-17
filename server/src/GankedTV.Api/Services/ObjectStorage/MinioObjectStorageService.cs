using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.ObjectStorage;

public sealed class MinioObjectStorageService : IObjectStorageService
{
    private static readonly TimeSpan DefaultExpiry = TimeSpan.FromMinutes(15);

    private readonly IAmazonS3 _s3;
    private readonly MinioOptions _options;
    private readonly ILogger<MinioObjectStorageService> _logger;

    public MinioObjectStorageService(
        IAmazonS3 s3,
        IOptions<MinioOptions> options,
        ILogger<MinioObjectStorageService> logger)
    {
        _s3 = s3;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureBucketsAsync(CancellationToken ct = default)
    {
        var listResponse = await _s3.ListBucketsAsync(ct);
        var existing = new HashSet<string>(
            listResponse.Buckets?.Select(b => b.BucketName) ?? Enumerable.Empty<string>(),
            StringComparer.Ordinal);

        var required = new[] { _options.ClipsBucket, _options.ThumbnailsBucket };

        foreach (var name in required)
        {
            if (existing.Contains(name))
            {
                continue;
            }

            _logger.LogInformation("Creating missing bucket {Bucket}", name);
            await _s3.PutBucketAsync(new PutBucketRequest { BucketName = name }, ct);
        }
    }

    public string GetPresignedPutUrl(
        string bucket,
        string key,
        string contentType,
        TimeSpan? expiry = null)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = DateTime.UtcNow.Add(expiry ?? DefaultExpiry),
        };

        return _s3.GetPreSignedURL(request);
    }

    public string GetPresignedGetUrl(
        string bucket,
        string key,
        TimeSpan? expiry = null)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiry ?? DefaultExpiry),
        };

        return RewriteHost(_s3.GetPreSignedURL(request), _options.PublicUrl);
    }

    public async Task DeleteObjectAsync(string bucket, string key, CancellationToken ct = default)
    {
        await _s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = bucket,
            Key = key,
        }, ct);
    }

    // MinIO signs with the container-internal endpoint (http://minio:9000) but browsers
    // need to hit the host-visible URL. Preserve path, query, and signature verbatim.
    // Caveat: SigV4 canonicalizes the Host header into the signature, so post-sign host
    // rewriting is technically a mismatch. MinIO permits it in practice; if we ever
    // switch to strict S3, sign with PublicUrl directly and keep a second internal-only
    // IAmazonS3 for admin ops (ListBuckets / PutBucket / DeleteObject).
    internal static string RewriteHost(string signedUrl, string? publicUrl)
    {
        if (string.IsNullOrEmpty(publicUrl))
        {
            return signedUrl;
        }

        var signed = new Uri(signedUrl);
        var target = new Uri(publicUrl);

        var builder = new UriBuilder(signed)
        {
            Scheme = target.Scheme,
            Host = target.Host,
            Port = target.IsDefaultPort ? -1 : target.Port,
        };

        return builder.Uri.ToString();
    }
}
