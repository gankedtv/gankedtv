using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Auth.ApiKeys;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

// Proves the API-key auth path works end-to-end against the /clips upload group and that adding
// it didn't break JWT auth (forward-selector regression).
[Collection("PostgresClips")]
public class ApiKeyUploadAuthTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public ApiKeyUploadAuthTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync()
    {
        _storage = Substitute.For<IObjectStorageService>();
        _factory = new AuthApiFactory(_fx.ConnectionString, _storage);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    private async Task<(Guid userId, string rawKey, HttpClient jwtClient)> SeedUserAndMintKeyAsync()
    {
        var (userId, token) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, "clipper");
        var jwtClient = AuthTestHelpers.CreateBearerClient(_factory!, token);
        using var scope = _factory!.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
        var rawKey = (await svc.CreateAsync(userId, "desktop", null)).RawKey!;
        return (userId, rawKey, jwtClient);
    }

    [Fact]
    public async Task FullUpload_WithXApiKeyHeader_ActsAsOwner()
    {
        await _fx.ResetAsync();
        var (userId, rawKey, jwtClient) = await SeedUserAndMintKeyAsync();
        jwtClient.Dispose();

        _storage.GetPresignedPutUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("http://localhost:9000/clips/signed?sig=abc");
        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(4242, "video/mp4"));

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, rawKey);

        // Step 1: create
        var create = await client.PostAsJsonAsync("/clips", new { title = "keyed clip" });
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var clipId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Step 2: upload-url
        (await client.PostAsync($"/clips/{clipId}/upload-url", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Step 3: complete
        var complete = await client.PostAsync($"/clips/{clipId}/complete", null);
        complete.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = _fx.CreateContext();
        var clip = await db.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.UserId.Should().Be(userId);
        clip.Status.Should().Be("processing");
    }

    [Fact]
    public async Task Complete_WithTrim_ViaApiKey_Returns400TrimNotSupported()
    {
        await _fx.ResetAsync();
        var (userId, rawKey, jwtClient) = await SeedUserAndMintKeyAsync();
        jwtClient.Dispose();

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "video/mp4"));

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, rawKey);
        var create = await client.PostAsJsonAsync("/clips", new { title = "keyed clip" });
        var clipId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // The trimmer is web-only; API-key uploads must trim before uploading (rewynd does).
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/complete",
            new { trimStartSeconds = 1.0, trimEndSeconds = 5.0 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("trim_not_supported");

        // Trim-free body still completes fine over the same key.
        var ok = await client.PostAsJsonAsync($"/clips/{clipId}/complete", new { });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = _fx.CreateContext();
        var clip = await db.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.UserId.Should().Be(userId);
        clip.TrimStartSecs.Should().BeNull();
    }

    [Fact]
    public async Task Create_WithBearerApiKey_ActsAsOwner()
    {
        await _fx.ResetAsync();
        var (userId, rawKey, jwtClient) = await SeedUserAndMintKeyAsync();
        jwtClient.Dispose();

        using var client = _factory!.CreateClient();
        // rewynd's preferred format: the key travels as a Bearer credential, disambiguated
        // from a JWT by the gtv_ prefix.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawKey);

        var resp = await client.PostAsJsonAsync("/clips", new { title = "bearer keyed" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var clipId = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var db = _fx.CreateContext();
        (await db.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId)).UserId.Should().Be(userId);
    }

    [Fact]
    public async Task RevokedKey_Returns401()
    {
        await _fx.ResetAsync();
        var (_, rawKey, jwtClient) = await SeedUserAndMintKeyAsync();

        // Revoke through the interactive (JWT) client.
        Guid keyId;
        await using (var db = _fx.CreateContext())
        {
            keyId = (await db.ApiKeys.AsNoTracking().SingleAsync()).Id;
        }
        (await jwtClient.DeleteAsync($"/me/api-keys/{keyId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        jwtClient.Dispose();

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, rawKey);
        var resp = await client.PostAsJsonAsync("/clips", new { title = "should fail" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task JwtAuth_StillWorks_AfterAddingApiKeyScheme()
    {
        await _fx.ResetAsync();
        var (userId, token) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, "jwtuser");
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, token);

        var resp = await client.PostAsJsonAsync("/clips", new { title = "jwt still works" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var clipId = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var db = _fx.CreateContext();
        (await db.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId)).UserId.Should().Be(userId);
    }
}
