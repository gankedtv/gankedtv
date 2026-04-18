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
    private static MinioObjectStorageService BuildService(
        IAmazonS3 s3,
        string? publicUrl = null)
    {
        var options = Options.Create(new MinioOptions
        {
            Endpoint = "http://minio:9000",
            AccessKey = "key",
            SecretKey = "secret",
            PublicUrl = publicUrl,
            ClipsBucket = "clips",
            ThumbnailsBucket = "thumbnails",
        });
        return new MinioObjectStorageService(s3, options, NullLogger<MinioObjectStorageService>.Instance);
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
                },
            });

        await BuildService(s3).EnsureBucketsAsync();

        await s3.DidNotReceive().PutBucketAsync(
            Arg.Any<PutBucketRequest>(),
            Arg.Any<CancellationToken>());
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
