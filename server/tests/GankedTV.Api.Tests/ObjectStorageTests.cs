using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GankedTV.Api.Tests;

public class ObjectStorageTests
{
    private static S3ObjectStorageService BuildService(
        IAmazonS3 s3,
        string? publicUrl = null)
    {
        var options = Options.Create(new S3Options
        {
            Endpoint = "http://minio:9000",
            AccessKey = "key",
            SecretKey = "secret",
            PublicUrl = publicUrl,
            ClipsBucket = "clips",
            ThumbnailsBucket = "thumbnails",
            GameCoversBucket = "game-covers",
            StreamCacheBucket = "stream-cache",
        });
        return new S3ObjectStorageService(s3, options, NullLogger<S3ObjectStorageService>.Instance);
    }

    [Fact]
    public async Task EnsureBucketsAsync_CreatesMissingBuckets()
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.ListBucketsAsync(Arg.Any<CancellationToken>())
            .Returns(new ListBucketsResponse { Buckets = new List<S3Bucket>() });

        await BuildService(s3).EnsureBucketsAsync();

        await s3.Received(1).PutBucketAsync(
            Arg.Is<PutBucketRequest>(r => r.BucketName == "clips"),
            Arg.Any<CancellationToken>());
        await s3.Received(1).PutBucketAsync(
            Arg.Is<PutBucketRequest>(r => r.BucketName == "thumbnails"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureBucketsAsync_SkipsExistingBuckets()
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.ListBucketsAsync(Arg.Any<CancellationToken>())
            .Returns(new ListBucketsResponse
            {
                Buckets = new List<S3Bucket>
                {
                    new() { BucketName = "clips" },
                    new() { BucketName = "thumbnails" },
                    new() { BucketName = "game-covers" },
                    new() { BucketName = "stream-cache" },
                },
            });

        await BuildService(s3).EnsureBucketsAsync();

        await s3.DidNotReceive().PutBucketAsync(
            Arg.Any<PutBucketRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureBucketsAsync_AppliesPublicReadPolicyToCoversBucket()
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.ListBucketsAsync(Arg.Any<CancellationToken>())
            .Returns(new ListBucketsResponse { Buckets = new List<S3Bucket>() });

        await BuildService(s3).EnsureBucketsAsync();

        await s3.Received(1).PutBucketAsync(
            Arg.Is<PutBucketRequest>(r => r.BucketName == "game-covers"),
            Arg.Any<CancellationToken>());
        await s3.Received(1).PutBucketPolicyAsync(
            Arg.Is<PutBucketPolicyRequest>(r =>
                r.BucketName == "game-covers" && r.Policy.Contains("s3:GetObject")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureBucketsAsync_CoversBucketAliasesPrivateBucket_SkipsPublicPolicy()
    {
        // Misconfig guard: if GameCoversBucket points at clips/thumbnails, the anonymous-read
        // policy must NOT be applied — that would expose private media.
        var s3 = Substitute.For<IAmazonS3>();
        s3.ListBucketsAsync(Arg.Any<CancellationToken>())
            .Returns(new ListBucketsResponse { Buckets = new List<S3Bucket>() });
        var options = Options.Create(new S3Options
        {
            Endpoint = "http://minio:9000",
            AccessKey = "k",
            SecretKey = "s",
            ClipsBucket = "clips",
            ThumbnailsBucket = "thumbnails",
            GameCoversBucket = "clips", // aliased onto a private bucket
            StreamCacheBucket = "thumbnails", // also aliased — neither public bucket may get a policy
        });

        await new S3ObjectStorageService(s3, options, NullLogger<S3ObjectStorageService>.Instance)
            .EnsureBucketsAsync();

        await s3.DidNotReceive().PutBucketPolicyAsync(
            Arg.Any<PutBucketPolicyRequest>(), Arg.Any<CancellationToken>());
        // …and the aliased name is created exactly once (deduplicated), not twice.
        await s3.Received(1).PutBucketAsync(
            Arg.Is<PutBucketRequest>(r => r.BucketName == "clips"), Arg.Any<CancellationToken>());
        await s3.Received(1).PutBucketAsync(
            Arg.Is<PutBucketRequest>(r => r.BucketName == "thumbnails"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void BuildPublicReadPolicy_GrantsAnonymousGetObjectOnBucket()
    {
        var policy = S3ObjectStorageService.BuildPublicReadPolicy("game-covers");

        policy.Should().Contain("\"Principal\":\"*\"");
        policy.Should().Contain("s3:GetObject");
        policy.Should().Contain("arn:aws:s3:::game-covers/*");
    }

    [Fact]
    public async Task EnsureBucketsAsync_CreatesStreamCacheBucketWithPublicReadAndLifecycle()
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.ListBucketsAsync(Arg.Any<CancellationToken>())
            .Returns(new ListBucketsResponse { Buckets = new List<S3Bucket>() });

        await BuildService(s3).EnsureBucketsAsync();

        await s3.Received(1).PutBucketAsync(
            Arg.Is<PutBucketRequest>(r => r.BucketName == "stream-cache"),
            Arg.Any<CancellationToken>());
        await s3.Received(1).PutBucketPolicyAsync(
            Arg.Is<PutBucketPolicyRequest>(r =>
                r.BucketName == "stream-cache" && r.Policy.Contains("s3:GetObject")),
            Arg.Any<CancellationToken>());
        // Transient cache → a lifecycle expiry rule auto-evicts cached renditions.
        await s3.Received(1).PutLifecycleConfigurationAsync(
            Arg.Is<PutLifecycleConfigurationRequest>(r =>
                r.BucketName == "stream-cache"
                && r.Configuration.Rules.Any(rule => rule.Expiration != null && rule.Expiration.Days > 0)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteByPrefixAsync_ListsAndBatchDeletesMatchingObjects()
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(new ListObjectsV2Response
            {
                S3Objects = new List<S3Object>
                {
                    new() { Key = "abc/master.m3u8" },
                    new() { Key = "abc/stream_0_000.ts" },
                },
                IsTruncated = false,
            });

        await BuildService(s3).DeleteByPrefixAsync("stream-cache", "abc/");

        await s3.Received(1).DeleteObjectsAsync(
            Arg.Is<DeleteObjectsRequest>(r => r.BucketName == "stream-cache" && r.Objects.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteByPrefixAsync_NoMatches_DoesNotDelete()
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(new ListObjectsV2Response { S3Objects = new List<S3Object>(), IsTruncated = false });

        await BuildService(s3).DeleteByPrefixAsync("stream-cache", "abc/");

        await s3.DidNotReceive().DeleteObjectsAsync(Arg.Any<DeleteObjectsRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void S3PublicUrls_BuildUrl_PrefersPublicUrlFallsBackToEndpoint()
    {
        var withPublic = new S3Options { Endpoint = "http://minio:9000", PublicUrl = "https://cdn.example.com/" };
        S3PublicUrls.BuildUrl(withPublic, "hls", "hls/abc/master.m3u8")
            .Should().Be("https://cdn.example.com/hls/hls/abc/master.m3u8");

        var noPublic = new S3Options { Endpoint = "http://localhost:9000/", PublicUrl = null };
        S3PublicUrls.BuildUrl(noPublic, "hls", "hls/abc/master.m3u8")
            .Should().Be("http://localhost:9000/hls/hls/abc/master.m3u8");
    }

    [Fact]
    public async Task EnsureBucketsAsync_CreatesOnlyMissingBucket()
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.ListBucketsAsync(Arg.Any<CancellationToken>())
            .Returns(new ListBucketsResponse
            {
                Buckets = new List<S3Bucket>
                {
                    new() { BucketName = "clips" },
                },
            });

        await BuildService(s3).EnsureBucketsAsync();

        await s3.DidNotReceive().PutBucketAsync(
            Arg.Is<PutBucketRequest>(r => r.BucketName == "clips"),
            Arg.Any<CancellationToken>());
        await s3.Received(1).PutBucketAsync(
            Arg.Is<PutBucketRequest>(r => r.BucketName == "thumbnails"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void GetPresignedPutUrl_UsesFifteenMinExpiryByDefault()
    {
        var s3 = Substitute.For<IAmazonS3>();
        GetPreSignedUrlRequest? captured = null;
        s3.GetPreSignedURL(Arg.Do<GetPreSignedUrlRequest>(r => captured = r))
            .Returns("http://minio:9000/clips/key?sig=abc");

        var before = DateTime.UtcNow;
        BuildService(s3).GetPresignedPutUrl("clips", "key", "video/mp4");
        var after = DateTime.UtcNow;

        captured.Should().NotBeNull();
        captured!.Verb.Should().Be(HttpVerb.PUT);
        captured.BucketName.Should().Be("clips");
        captured.Key.Should().Be("key");
        captured.ContentType.Should().Be("video/mp4");
        captured.Expires.Should().BeOnOrAfter(before.AddMinutes(15));
        captured.Expires.Should().BeOnOrBefore(after.AddMinutes(15));
    }

    [Fact]
    public void GetPresignedPutUrl_HonorsCustomExpiry()
    {
        var s3 = Substitute.For<IAmazonS3>();
        GetPreSignedUrlRequest? captured = null;
        s3.GetPreSignedURL(Arg.Do<GetPreSignedUrlRequest>(r => captured = r))
            .Returns("http://minio:9000/clips/key?sig=abc");

        var before = DateTime.UtcNow;
        BuildService(s3).GetPresignedPutUrl("clips", "key", "video/mp4", TimeSpan.FromHours(1));
        var after = DateTime.UtcNow;

        captured!.Expires.Should().BeOnOrAfter(before.AddHours(1));
        captured.Expires.Should().BeOnOrBefore(after.AddHours(1));
    }

    [Fact]
    public void GetPresignedGetUrl_RewritesHostWhenPublicUrlSet()
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>())
            .Returns("http://minio:9000/clips/some/key?X-Amz-Signature=abc123");

        var url = BuildService(s3, publicUrl: "http://localhost:9000")
            .GetPresignedGetUrl("clips", "some/key");

        url.Should().Be("http://localhost:9000/clips/some/key?X-Amz-Signature=abc123");
    }

    [Fact]
    public void GetPresignedPutUrl_RewritesHostWhenPublicUrlSet()
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>())
            .Returns("http://minio:9000/clips/some/key?X-Amz-Signature=def456");

        var url = BuildService(s3, publicUrl: "http://localhost:9000")
            .GetPresignedPutUrl("clips", "some/key", "video/mp4");

        url.Should().Be("http://localhost:9000/clips/some/key?X-Amz-Signature=def456");
    }

    [Fact]
    public void RewriteHost_ThrowsOnMalformedPublicUrl()
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>())
            .Returns("http://minio:9000/clips/key?sig=abc");

        var act = () => BuildService(s3, publicUrl: "not a url")
            .GetPresignedGetUrl("clips", "key");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*S3_PUBLIC_URL*");
    }

    [Fact]
    public void GetPresignedGetUrl_ReturnsUnmodifiedUrlWhenPublicUrlNotSet()
    {
        var s3 = Substitute.For<IAmazonS3>();
        const string signed = "http://minio:9000/clips/key?X-Amz-Signature=abc123";
        s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>()).Returns(signed);

        var url = BuildService(s3).GetPresignedGetUrl("clips", "key");

        url.Should().Be(signed);
    }

    [Fact]
    public void GetPresignedGetUrl_UsesGetVerb()
    {
        var s3 = Substitute.For<IAmazonS3>();
        GetPreSignedUrlRequest? captured = null;
        s3.GetPreSignedURL(Arg.Do<GetPreSignedUrlRequest>(r => captured = r))
            .Returns("http://minio:9000/clips/key?sig=abc");

        BuildService(s3).GetPresignedGetUrl("clips", "key");

        captured!.Verb.Should().Be(HttpVerb.GET);
    }

    [Fact]
    public async Task DeleteObjectAsync_CallsS3DeleteWithBucketAndKey()
    {
        var s3 = Substitute.For<IAmazonS3>();

        await BuildService(s3).DeleteObjectAsync("clips", "some/key");

        await s3.Received(1).DeleteObjectAsync(
            Arg.Is<DeleteObjectRequest>(r => r.BucketName == "clips" && r.Key == "some/key"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetObjectMetadataAsync_ReturnsSizeAndContentType()
    {
        var s3 = Substitute.For<IAmazonS3>();
        var response = new GetObjectMetadataResponse
        {
            ContentLength = 12345L,
        };
        response.Headers.ContentType = "video/mp4";
        s3.GetObjectMetadataAsync(
                Arg.Is<GetObjectMetadataRequest>(r => r.BucketName == "clips" && r.Key == "clips/abc.mp4"),
                Arg.Any<CancellationToken>())
            .Returns(response);

        var meta = await BuildService(s3).GetObjectMetadataAsync("clips", "clips/abc.mp4");

        meta.Should().NotBeNull();
        meta!.SizeBytes.Should().Be(12345L);
        meta.ContentType.Should().Be("video/mp4");
    }

    [Fact]
    public async Task GetObjectMetadataAsync_ReturnsNullWhenObjectMissing()
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
            .Returns<GetObjectMetadataResponse>(_ => throw new AmazonS3Exception("not found")
            {
                StatusCode = System.Net.HttpStatusCode.NotFound,
            });

        var meta = await BuildService(s3).GetObjectMetadataAsync("clips", "missing.mp4");

        meta.Should().BeNull();
    }

    [Fact]
    public void GetPresignedPutUrl_HttpsEndpoint_UsesHttpsProtocol()
    {
        var s3 = Substitute.For<IAmazonS3>();
        GetPreSignedUrlRequest? captured = null;
        s3.GetPreSignedURL(Arg.Do<GetPreSignedUrlRequest>(r => captured = r))
            .Returns("https://prod.example/clips/key?sig=abc");

        var opts = Options.Create(new S3Options
        {
            Endpoint = "https://prod.example",
            AccessKey = "k",
            SecretKey = "s",
            ClipsBucket = "clips",
            ThumbnailsBucket = "thumbnails",
        });
        new S3ObjectStorageService(s3, opts, NullLogger<S3ObjectStorageService>.Instance)
            .GetPresignedPutUrl("clips", "key", "video/mp4");

        captured!.Protocol.Should().Be(Protocol.HTTPS);
    }

    [Fact]
    public void GetPresignedGetUrl_NonDefaultPortInPublicUrl_PreservesPort()
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>())
            .Returns("http://minio:9000/clips/key?sig=abc");

        // Exercises the non-default-port branch of UriBuilder rewriting — a public URL on
        // a non-standard port must survive the rewrite instead of being normalised to `-1`.
        var url = BuildService(s3, publicUrl: "https://public.example:8443")
            .GetPresignedGetUrl("clips", "key");

        url.Should().StartWith("https://public.example:8443/clips/key");
    }

    [Fact]
    public async Task PutObjectAsync_PassesBucketKeyAndContentTypeToS3()
    {
        var s3 = Substitute.For<IAmazonS3>();
        PutObjectRequest? captured = null;
        s3.PutObjectAsync(Arg.Do<PutObjectRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new PutObjectResponse());

        using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        await BuildService(s3).PutObjectAsync("thumbnails", "u/c.jpg", stream, "image/jpeg");

        captured.Should().NotBeNull();
        captured!.BucketName.Should().Be("thumbnails");
        captured.Key.Should().Be("u/c.jpg");
        captured.ContentType.Should().Be("image/jpeg");
        captured.AutoCloseStream.Should().BeFalse();
        captured.InputStream.Should().BeSameAs(stream);
    }

    [Fact]
    public async Task GetObjectMetadataAsync_PropagatesNonNotFoundErrors()
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
            .Returns<GetObjectMetadataResponse>(_ => throw new AmazonS3Exception("boom")
            {
                StatusCode = System.Net.HttpStatusCode.InternalServerError,
            });

        var act = async () => await BuildService(s3).GetObjectMetadataAsync("clips", "x.mp4");

        await act.Should().ThrowAsync<AmazonS3Exception>();
    }
}
