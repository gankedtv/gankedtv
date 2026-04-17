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
    public async Task GetPresignedPutUrlAsync_UsesFifteenMinExpiryByDefault()
    {
        var s3 = Substitute.For<IAmazonS3>();
        GetPreSignedUrlRequest? captured = null;
        s3.GetPreSignedURL(Arg.Do<GetPreSignedUrlRequest>(r => captured = r))
            .Returns("http://minio:9000/clips/key?sig=abc");

        var before = DateTime.UtcNow;
        await BuildService(s3).GetPresignedPutUrlAsync("clips", "key", "video/mp4");
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
    public async Task GetPresignedPutUrlAsync_HonorsCustomExpiry()
    {
        var s3 = Substitute.For<IAmazonS3>();
        GetPreSignedUrlRequest? captured = null;
        s3.GetPreSignedURL(Arg.Do<GetPreSignedUrlRequest>(r => captured = r))
            .Returns("http://minio:9000/clips/key?sig=abc");

        var before = DateTime.UtcNow;
        await BuildService(s3).GetPresignedPutUrlAsync("clips", "key", "video/mp4", TimeSpan.FromHours(1));
        var after = DateTime.UtcNow;

        captured!.Expires.Should().BeOnOrAfter(before.AddHours(1));
        captured.Expires.Should().BeOnOrBefore(after.AddHours(1));
    }

    [Fact]
    public async Task GetPresignedGetUrlAsync_RewritesHostWhenPublicUrlSet()
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>())
            .Returns("http://minio:9000/clips/some/key?X-Amz-Signature=abc123");

        var url = await BuildService(s3, publicUrl: "http://localhost:9000")
            .GetPresignedGetUrlAsync("clips", "some/key");

        url.Should().StartWith("http://localhost:9000/clips/some/key");
        url.Should().Contain("X-Amz-Signature=abc123");
    }

    [Fact]
    public async Task GetPresignedGetUrlAsync_ReturnsUnmodifiedUrlWhenPublicUrlNotSet()
    {
        var s3 = Substitute.For<IAmazonS3>();
        const string signed = "http://minio:9000/clips/key?X-Amz-Signature=abc123";
        s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>()).Returns(signed);

        var url = await BuildService(s3).GetPresignedGetUrlAsync("clips", "key");

        url.Should().Be(signed);
    }

    [Fact]
    public async Task GetPresignedGetUrlAsync_UsesGetVerb()
    {
        var s3 = Substitute.For<IAmazonS3>();
        GetPreSignedUrlRequest? captured = null;
        s3.GetPreSignedURL(Arg.Do<GetPreSignedUrlRequest>(r => captured = r))
            .Returns("http://minio:9000/clips/key?sig=abc");

        await BuildService(s3).GetPresignedGetUrlAsync("clips", "key");

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
}
