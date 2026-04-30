using System.IO;
using System.Net;
using FluentAssertions;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace GankedTV.Api.Tests.Integration.Storage;

// End-to-end presigned-GET round trip against a real S3-compatible storage container.
// Locks MinioObjectStorageService.GetPresignedGetUrl + ResolveProtocol against real signing
// — if anyone breaks SigV4 query construction (e.g. flips the Verb, drops the protocol
// override and ends up signing https://) the GET below will return 403/SignatureMismatch
// and the test fails. RewriteHost is exercised as a pass-through here (PublicUrl is null
// in the fixture); its non-trivial branches are pinned by ObjectStorageTests.RewriteHost*.
[Collection("PostgresAndS3")]
public class PresignedGetRoundTripTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private readonly S3Fixture _s3;
    private AuthApiFactory? _factory;

    public PresignedGetRoundTripTests(PostgresFixture pg, S3Fixture s3)
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
    public async Task GetPresignedGetUrl_ReturnsUrlThatFetchesTheObject()
    {
        // Place a fixed payload in the clips bucket via the test-side admin client.
        var key = $"roundtrip/{Guid.NewGuid()}.bin";
        var payload = new byte[64];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)i;

        using (var stream = new MemoryStream(payload))
        {
            await _s3.AdminClient.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
            {
                BucketName = S3Fixture.ClipsBucket,
                Key = key,
                InputStream = stream,
                ContentType = "application/octet-stream",
                AutoCloseStream = false,
            });
        }

        // Resolve the production storage service from the API host — it's wired to the
        // fixture's endpoint via AuthApiFactory's Configure<MinioOptions> override.
        var storage = _factory!.Services.GetRequiredService<IObjectStorageService>();
        var presignedGet = storage.GetPresignedGetUrl(S3Fixture.ClipsBucket, key);

        // Fetch via a fresh HttpClient — the URL points at the S3 backend, not the test server.
        using var rawClient = new HttpClient();
        var resp = await rawClient.GetAsync(presignedGet);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        bytes.Should().Equal(payload);
    }
}
