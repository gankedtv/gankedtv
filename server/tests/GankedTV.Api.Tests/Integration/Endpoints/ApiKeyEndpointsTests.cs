using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Auth.ApiKeys;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GankedTV.Api.Tests.Integration.Endpoints;

// The /me/api-keys surface is view + revoke only — keys are minted by the device-authorization
// flow, not a manual create endpoint. Tests seed keys via ApiKeyService directly.
[Collection("PostgresAuth")]
public class ApiKeyEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;

    public ApiKeyEndpointsTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync()
    {
        _factory = new AuthApiFactory(_fx.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    private Task<(Guid userId, string token)> SeedUserAsync(string username = "keyuser") =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);

    private HttpClient JwtClient(string token) => AuthTestHelpers.CreateBearerClient(_factory!, token);

    private HttpClient ApiKeyClient(string rawKey)
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, rawKey);
        return client;
    }

    private async Task<(Guid id, string rawKey)> MintKeyAsync(Guid userId, string? name = "k")
    {
        using var scope = _factory!.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
        var result = await svc.CreateAsync(userId, name, null);
        return (result.Key!.Id, result.RawKey!);
    }

    [Fact]
    public async Task List_ReturnsMetadataButNeverTheSecret()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAsync();
        await MintKeyAsync(userId, "one");
        using var client = JwtClient(token);

        var resp = await client.GetAsync("/me/api-keys");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetArrayLength().Should().Be(1);
        var item = body[0];
        item.GetProperty("keyPrefix").GetString().Should().StartWith(ApiKeyService.KeyPrefix);
        item.GetProperty("name").GetString().Should().Be("one");
        // The secret and its hash must never be serialized.
        item.TryGetProperty("key", out _).Should().BeFalse();
        item.TryGetProperty("keyHash", out _).Should().BeFalse();
    }

    [Fact]
    public async Task List_NoAuth_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/me/api-keys");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_OnlyReturnsCallersOwnKeys()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAsync("me");
        var (otherId, _) = await SeedUserAsync("other");
        await MintKeyAsync(userId, "mine");
        await MintKeyAsync(otherId, "theirs");
        using var client = JwtClient(token);

        var body = await (await client.GetAsync("/me/api-keys")).Content.ReadFromJsonAsync<JsonElement>();

        body.GetArrayLength().Should().Be(1);
        body[0].GetProperty("name").GetString().Should().Be("mine");
    }

    [Fact]
    public async Task Revoke_OwnKey_Returns204_AndListShowsRevokedAt()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAsync();
        var (id, _) = await MintKeyAsync(userId);
        using var client = JwtClient(token);

        var resp = await client.DeleteAsync($"/me/api-keys/{id}");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await (await client.GetAsync("/me/api-keys")).Content.ReadFromJsonAsync<JsonElement>();
        list[0].GetProperty("revokedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Revoke_OtherUsersKey_Returns404()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAsync("owner");
        var (_, attackerToken) = await SeedUserAsync("attacker");
        var (keyId, _) = await MintKeyAsync(ownerId);

        using var client = JwtClient(attackerToken);
        var resp = await client.DeleteAsync($"/me/api-keys/{keyId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var db = _fx.CreateContext();
        (await db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == keyId)).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task Revoke_UnknownId_Returns404()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAsync();
        using var client = JwtClient(token);

        var resp = await client.DeleteAsync($"/me/api-keys/{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Containment: an API key must not be able to act on the key-management surface
    // (interactive-only policy) — it can upload but can't list or revoke keys.
    [Fact]
    public async Task ManagementEndpoints_RejectApiKeyAuth_ButAcceptJwt()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAsync();
        var (id, rawKey) = await MintKeyAsync(userId);

        using var keyClient = ApiKeyClient(rawKey);
        (await keyClient.GetAsync("/me/api-keys")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await keyClient.DeleteAsync($"/me/api-keys/{id}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Same operations succeed with the interactive JWT.
        using var jwt = JwtClient(token);
        (await jwt.GetAsync("/me/api-keys")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
