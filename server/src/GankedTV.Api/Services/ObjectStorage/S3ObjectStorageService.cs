using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.ObjectStorage;

public sealed class S3ObjectStorageService : IObjectStorageService
{
    private static readonly TimeSpan DefaultExpiry = TimeSpan.FromMinutes(15);

    private readonly IAmazonS3 _s3;
    private readonly S3Options _options;
    private readonly ILogger<S3ObjectStorageService> _logger;

    public S3ObjectStorageService(
        IAmazonS3 s3,
        IOptions<S3Options> options,
        ILogger<S3ObjectStorageService> logger)
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

        // Deduplicate so an aliased config (e.g. GameCoversBucket == ClipsBucket) doesn't issue
        // a duplicate PutBucketAsync for the same name.
        var required = new HashSet<string>(
            new[]
            {
                _options.ClipsBucket,
                _options.ThumbnailsBucket,
                _options.GameCoversBucket,
                _options.StreamCacheBucket,
                _options.AvatarsBucket,
            },
            StringComparer.Ordinal);

        foreach (var name in required)
        {
            if (existing.Contains(name))
            {
                continue;
            }

            _logger.LogInformation("Creating missing bucket {Bucket}", name);
            await _s3.PutBucketAsync(new PutBucketRequest { BucketName = name }, ct);
        }

        // Game covers and the stream cache are public media served as stable URLs (no
        // presigning), so each gets an anonymous s3:GetObject policy (idempotent). Guard
        // against a misconfig that aliases a public bucket onto clips/thumbnails — applying
        // the policy there would silently expose private media to anonymous reads.
        await ApplyPublicReadIfSafeAsync(_options.GameCoversBucket, "GameCoversBucket", ct);
        await ApplyPublicReadIfSafeAsync(_options.StreamCacheBucket, "StreamCacheBucket", ct);
        await ApplyPublicReadIfSafeAsync(_options.AvatarsBucket, "AvatarsBucket", ct);

        // The stream cache is transient: a lifecycle rule expires cached renditions so the JIT
        // output never accumulates indefinitely. Skipped when aliased onto a private bucket.
        if (_options.StreamCacheBucket != _options.ClipsBucket
            && _options.StreamCacheBucket != _options.ThumbnailsBucket
            && _options.StreamCacheBucket != _options.GameCoversBucket
            && _options.StreamCacheBucket != _options.AvatarsBucket)
        {
            await ApplyStreamCacheLifecycleAsync(ct);
        }
    }

    private async Task ApplyStreamCacheLifecycleAsync(CancellationToken ct)
    {
        var days = Math.Max(1, _options.StreamCacheTtlDays);
        try
        {
            await _s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
            {
                BucketName = _options.StreamCacheBucket,
                Configuration = new LifecycleConfiguration
                {
                    Rules =
                    [
                        new LifecycleRule
                        {
                            Id = "expire-cached-renditions",
                            Status = LifecycleRuleStatus.Enabled,
                            // Empty-prefix filter = applies to every object in the bucket.
                            Filter = new LifecycleFilter { LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = "" } },
                            Expiration = new LifecycleRuleExpiration { Days = days },
                        },
                    ],
                },
            }, ct);
        }
        catch (AmazonS3Exception ex)
        {
            // Eviction is an optimization, not correctness — a backend that doesn't support the
            // lifecycle API must not abort startup. Cached renditions just won't auto-expire.
            _logger.LogWarning(ex,
                "Could not set lifecycle expiry on '{Bucket}'; cached renditions will not auto-evict.",
                _options.StreamCacheBucket);
        }
    }

    private async Task ApplyPublicReadIfSafeAsync(string bucket, string optionName, CancellationToken ct)
    {
        if (bucket == _options.ClipsBucket || bucket == _options.ThumbnailsBucket)
        {
            _logger.LogWarning(
                "{OptionName} '{Bucket}' aliases a private bucket; skipping the anonymous-read "
                + "policy to avoid exposing clips/thumbnails.", optionName, bucket);
            return;
        }

        await _s3.PutBucketPolicyAsync(new PutBucketPolicyRequest
        {
            BucketName = bucket,
            Policy = BuildPublicReadPolicy(bucket),
        }, ct);
    }

    internal static string BuildPublicReadPolicy(string bucket) =>
        $$"""
        {"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":"*","Action":["s3:GetObject"],"Resource":["arn:aws:s3:::{{bucket}}/*"]}]}
        """;

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
            Protocol = ResolveProtocol(_options.Endpoint),
        };

        return RewriteHost(_s3.GetPreSignedURL(request), _options.PublicUrl);
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
            Protocol = ResolveProtocol(_options.Endpoint),
        };

        return RewriteHost(_s3.GetPreSignedURL(request), _options.PublicUrl);
    }

    // GetPreSignedURL defaults to https:// regardless of the ServiceURL scheme. For MinIO dev
    // on plain HTTP we have to set Protocol.HTTP explicitly or the presigned URL won't match
    // MinIO's listener.
    private static Protocol ResolveProtocol(string endpoint) =>
        endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? Protocol.HTTP
            : Protocol.HTTPS;

    public async Task DeleteObjectAsync(string bucket, string key, CancellationToken ct = default)
    {
        await _s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = bucket,
            Key = key,
        }, ct);
    }

    public async Task DeleteByPrefixAsync(string bucket, string prefix, CancellationToken ct = default)
    {
        // List + batch-delete in pages. A clip's cached ladder is a handful of files, but page
        // defensively in case of many segments. DeleteObjects caps at 1000 keys per call, which
        // matches ListObjectsV2's default page size.
        string? token = null;
        do
        {
            var listed = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                ContinuationToken = token,
            }, ct);

            if (listed.S3Objects is { Count: > 0 } objects)
            {
                await _s3.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = bucket,
                    Objects = objects.Select(o => new KeyVersion { Key = o.Key }).ToList(),
                }, ct);
            }

            token = listed.IsTruncated == true ? listed.NextContinuationToken : null;
        }
        while (token is not null);
    }

    public async Task PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        string contentType,
        CancellationToken ct = default)
    {
        // AutoCloseStream=false so callers (which own the stream) can still dispose it
        // themselves; AWSSDK would otherwise dispose the InputStream after the upload.
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        }, ct);
    }

    public async Task<ObjectMetadata?> GetObjectMetadataAsync(
        string bucket,
        string key,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = key,
            }, ct);
            return new ObjectMetadata(response.ContentLength, response.Headers?.ContentType);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // MinIO signs with the container-internal endpoint (http://minio:9000) but browsers
    // need to hit the host-visible URL. Preserve path, query, and signature verbatim.
    // Caveat: SigV4 canonicalizes the Host header into the signature, so post-sign host
    // rewriting is technically a mismatch. MinIO permits it in practice; if we ever
    // switch to strict S3, sign with PublicUrl directly and keep a second internal-only
    // IAmazonS3 for admin ops (ListBuckets / PutBucket / DeleteObject).
    internal static string RewriteHost(string signedUrl, string? publicUrl)
    {
        if (string.IsNullOrWhiteSpace(publicUrl))
        {
            return signedUrl;
        }

        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var target))
        {
            throw new InvalidOperationException(
                $"S3_PUBLIC_URL / Minio:PublicUrl is not a valid absolute URL: '{publicUrl}'");
        }

        var signed = new Uri(signedUrl);
        var builder = new UriBuilder(signed)
        {
            Scheme = target.Scheme,
            Host = target.Host,
            Port = target.IsDefaultPort ? -1 : target.Port,
        };

        return builder.Uri.ToString();
    }
}
