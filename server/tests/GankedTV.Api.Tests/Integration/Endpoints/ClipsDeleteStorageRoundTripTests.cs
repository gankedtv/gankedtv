using System.IO;
using System.Net;
using System.Text;
using Amazon.S3;
using FluentAssertions;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Tests.Integration.Endpoints;

// End-to-end DELETE /clips/{id} round trip against a real S3-compatible storage container.
// The substituted version of this scenario lives in
// ClipsMutateEndpointsTests.Delete_Owner_Returns204_RemovesRow_DeletesS3Objects (it pins
// which bucket each key goes to via NSubstitute call verification). This sister test pins
// that the keys actually round-trip — i.e. the SDK call deletes the same object the upload
// would have created, in the same bucket. A bucket-vs-key swap that the substituted test
// happens to miss would still survive its assertions but fail here on the post-delete HEAD.
[Collection("PostgresAndS3")]
public class ClipsDeleteStorageRoundTripTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private readonly S3Fixture _s3;
    private AuthApiFactory? _factory;

    public ClipsDeleteStorageRoundTripTests(PostgresFixture pg, S3Fixture s3)
    {
        _pg = pg;
        _s3 = s3;
    }

    public async Task InitializeAsync()
    {
        await _pg.ResetAsync();
        await _s3.ResetAsync();
        _factory = new AuthApiFactory(_pg.ConnectionString, s3Fixture: _s3);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Delete_RemovesClipRowAndBothBlobs_RoundTrip()
    {
        var (userId, token) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_pg, _factory!);

        // Seed a Ready clip directly. We bypass the upload pipeline because the goal here
        // is to validate DELETE's S3 cleanup, not the upload path (the orphan-sweep test
        // covers the upload round trip).
        var clipId = Guid.NewGuid();
        var videoKey = $"{userId}/{clipId}.mp4";
        var thumbnailKey = $"{userId}/{clipId}.jpg";
        var now = DateTimeOffset.UtcNow;
        await using (var db = _pg.CreateContext())
        {
            db.Clips.Add(new Clip
            {
                Id = clipId,
                UserId = userId,
                Title = "delete-me",
                VideoKey = videoKey,
                ThumbnailKey = thumbnailKey,
                Status = ClipStatuses.Ready,
                Visibility = "public",
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        // Place real bytes at both keys via the test-side admin client.
        await PutObjectAsync(S3Fixture.ClipsBucket, videoKey, "video-bytes", "video/mp4");
        await PutObjectAsync(S3Fixture.ThumbnailsBucket, thumbnailKey, "thumb-bytes", "image/jpeg");

        // Sanity: both objects exist before DELETE.
        (await _s3.AdminClient.GetObjectMetadataAsync(S3Fixture.ClipsBucket, videoKey))
            .HttpStatusCode.Should().Be(HttpStatusCode.OK);
        (await _s3.AdminClient.GetObjectMetadataAsync(S3Fixture.ThumbnailsBucket, thumbnailKey))
            .HttpStatusCode.Should().Be(HttpStatusCode.OK);

        // DELETE via the endpoint.
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, token);
        var resp = await client.DeleteAsync($"/clips/{clipId}");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Row gone.
        await using (var db = _pg.CreateContext())
        {
            (await db.Clips.AnyAsync(c => c.Id == clipId)).Should().BeFalse();
        }

        // Both blobs gone.
        await AssertObjectMissing(S3Fixture.ClipsBucket, videoKey);
        await AssertObjectMissing(S3Fixture.ThumbnailsBucket, thumbnailKey);
    }

    private async Task PutObjectAsync(string bucket, string key, string body, string contentType)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        await _s3.AdminClient.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            AutoCloseStream = false,
        });
    }

    private async Task AssertObjectMissing(string bucket, string key)
    {
        var act = async () => await _s3.AdminClient.GetObjectMetadataAsync(bucket, key);
        var ex = await act.Should().ThrowAsync<AmazonS3Exception>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

}
