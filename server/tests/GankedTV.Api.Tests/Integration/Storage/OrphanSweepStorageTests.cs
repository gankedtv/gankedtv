using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using GankedTV.Api.Auth.Jwt;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Maintenance;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Tests.Integration.Storage;

// End-to-end orphan-sweep test against a real S3-compatible storage container. The
// substituted IObjectStorageService used elsewhere echoes whatever key it's given, so it
// cannot catch a key-construction bug like the `clips/clips/{userId}/...` regression we hit
// while QA-ing PR #58. This test does the real round trip: HTTP upload → real S3 PUT →
// sweep → real S3 HEAD assertion. If the SDK is ever called with a key that doesn't match
// what the upload service constructed, the post-sweep HEAD will still find the object and
// the test fails.
[Collection("PostgresAndS3")]
public class OrphanSweepStorageTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private readonly S3Fixture _s3;
    private AuthApiFactory? _factory;

    public OrphanSweepStorageTests(PostgresFixture pg, S3Fixture s3)
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
    public async Task Sweep_DeletesDraftClipRowAndUploadedBlob_RoundTrip()
    {
        // 1. Seed user + token
        var (userId, token) = await SeedUserAndIssueTokenAsync();

        // 2. POST /clips (draft, no game) — server picks the clip id and key layout.
        using var client = ClientWithBearer(token);
        var createResp = await client.PostAsJsonAsync("/clips", new
        {
            title = "orphan",
            description = (string?)null,
            gameId = (int?)null,
            visibility = "public",
        });
        createResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var createdId = (await createResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        // 3. POST /clips/{id}/upload-url
        var uploadUrlResp = await client.PostAsync($"/clips/{createdId}/upload-url", content: null);
        uploadUrlResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var uploadBody = await uploadUrlResp.Content.ReadFromJsonAsync<JsonElement>();
        var presignedPut = uploadBody.GetProperty("url").GetString()!;
        var contentType = uploadBody.GetProperty("contentType").GetString()!;

        // 4. PUT bytes to the presigned URL via a fresh HttpClient (the URL points at the
        //    S3 backend, not the test server). Use a small placeholder payload — the orphan
        //    sweep cares about presence, not content.
        var payload = Encoding.UTF8.GetBytes("not-actually-an-mp4-but-non-empty");
        using (var rawClient = new HttpClient())
        {
            using var putContent = new ByteArrayContent(payload);
            putContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            var putResp = await rawClient.PutAsync(presignedPut, putContent);
            putResp.StatusCode.Should().Be(HttpStatusCode.OK,
                "S3 returns 200 on a successful presigned PUT");
        }

        // 5. Assert the object lives at exactly the key the upload service wrote into the DB.
        //    This is the assertion that would have caught the `clips/clips/...` bug — if the
        //    SDK call uses a different key from the row, this HEAD will 404 even before sweep.
        string videoKey;
        await using (var db = _pg.CreateContext())
        {
            videoKey = await db.Clips.AsNoTracking()
                .Where(c => c.Id == createdId)
                .Select(c => c.VideoKey)
                .FirstAsync();
        }
        videoKey.Should().Be($"{userId}/{createdId}.mp4",
            "no-game uploads use the {userId}/{clipId}.mp4 layout (BuildVideoKey)");
        var preSweep = await _s3.AdminClient.GetObjectMetadataAsync(
            S3Fixture.ClipsBucket, videoKey);
        preSweep.HttpStatusCode.Should().Be(HttpStatusCode.OK);

        // 6. Build a MaintenanceHostedService manually with a clock 2h in the future
        //    (default ClipStaleThreshold = 1h). The factory removed the auto-running
        //    hosted service registration; we instantiate one explicitly here.
        var clock = new FakeClock(DateTimeOffset.UtcNow.AddHours(2));
        var sp = _factory!.Services;
        var maintenance = new MaintenanceHostedService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptionsMonitor<MaintenanceOptions>>(),
            sp.GetRequiredService<IOptionsMonitor<S3Options>>(),
            clock,
            NullLogger<MaintenanceHostedService>.Instance);

        // 7. Run the sweep
        using (var scope = sp.CreateScope())
        {
            await maintenance.SweepOrphanedClipsAsync(scope, CancellationToken.None);
        }

        // 8. Assert clip row gone
        await using (var db = _pg.CreateContext())
        {
            (await db.Clips.AnyAsync(c => c.Id == createdId)).Should().BeFalse();
        }

        // 9. Assert blob gone via the actual S3 API
        var act = async () => await _s3.AdminClient.GetObjectMetadataAsync(
            S3Fixture.ClipsBucket, videoKey);
        var ex = await act.Should().ThrowAsync<AmazonS3Exception>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username = "owner")
    {
        var now = DateTimeOffset.UtcNow;
        Guid id;
        await using (var db = _pg.CreateContext())
        {
            var user = new User
            {
                Username = username,
                Email = $"{username}@example.com",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            id = user.Id;
        }

        using var scope = _factory!.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<IJwtService>();
        var token = jwt.Issue(new User { Id = id, Username = username, Email = $"{username}@example.com" });
        return (id, token);
    }

    private HttpClient ClientWithBearer(string token)
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
