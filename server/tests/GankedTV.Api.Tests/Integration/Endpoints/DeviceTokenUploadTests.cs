using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

// The whole point of the device grant: a key minted by approving a device request authenticates
// against the /clips upload group with no browser and no pasted key.
[Collection("PostgresClips")]
public class DeviceTokenUploadTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public DeviceTokenUploadTests(PostgresFixture fx) => _fx = fx;

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

    [Fact]
    public async Task DeviceGrant_EndToEnd_MintedKeyUploadsAsApprover()
    {
        await _fx.ResetAsync();
        var (userId, token) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, "gamer");

        // 1. Device starts the flow (anonymous).
        string deviceCode, userCode;
        using (var anon = _factory!.CreateClient())
        {
            var start = await (await anon.PostAsJsonAsync("/auth/device", new { clientName = "rewynd" }))
                .Content.ReadFromJsonAsync<JsonElement>();
            deviceCode = start.GetProperty("deviceCode").GetString()!;
            userCode = start.GetProperty("userCode").GetString()!;
        }

        // 2. User approves in the browser (interactive).
        using (var jwt = AuthTestHelpers.CreateBearerClient(_factory!, token))
        {
            (await jwt.PostAsJsonAsync("/me/device/approve", new { userCode })).StatusCode
                .Should().Be(HttpStatusCode.NoContent);
        }

        // 3. Device polls and receives the minted key.
        string apiKey;
        using (var anon = _factory!.CreateClient())
        {
            var tok = await (await anon.PostAsJsonAsync("/auth/device/token", new { deviceCode }))
                .Content.ReadFromJsonAsync<JsonElement>();
            apiKey = tok.GetProperty("token").GetString()!;
        }

        // 4. The key uploads a clip, acting as the approver — no cookies/JWT.
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var create = await client.PostAsJsonAsync("/clips", new { title = "device-uploaded" });

        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var clipId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var db = _fx.CreateContext();
        (await db.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId)).UserId.Should().Be(userId);
    }
}
