using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Auth.ApiKeys;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("PostgresAuth")]
public class DeviceAuthEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;

    public DeviceAuthEndpointsTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync()
    {
        _factory = new AuthApiFactory(_fx.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    private async Task<(string deviceCode, string userCode)> StartAsync(string? clientName = "rewynd")
    {
        using var client = _factory!.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/device", new { clientName });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("deviceCode").GetString()!, body.GetProperty("userCode").GetString()!);
    }

    [Fact]
    public async Task Start_ReturnsCodesAndVerificationUris()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsJsonAsync("/auth/device", new { clientName = "rewynd" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("deviceCode").GetString().Should().StartWith("dvc_");
        body.GetProperty("userCode").GetString().Should().Contain("-"); // display-formatted WXYZ-1234
        body.GetProperty("verificationUri").GetString().Should().EndWith("/device");
        body.GetProperty("verificationUriComplete").GetString().Should().Contain("/device?code=");
        body.GetProperty("interval").GetInt32().Should().Be(5);
        body.GetProperty("expiresIn").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Start_EmptyBody_IsAccepted()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        using var content = new StringContent("null", System.Text.Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/auth/device", content);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Start_ClientNameTooLong_Returns400()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsJsonAsync("/auth/device", new { clientName = new string('x', 101) });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_client_name");
    }

    [Fact]
    public async Task Poll_BeforeApproval_ReturnsAuthorizationPending()
    {
        await _fx.ResetAsync();
        var (deviceCode, _) = await StartAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsJsonAsync("/auth/device/token", new { deviceCode });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("authorization_pending");
    }

    [Fact]
    public async Task Poll_UnknownDeviceCode_ReturnsExpiredToken()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsJsonAsync("/auth/device/token", new { deviceCode = "dvc_nope" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("expired_token");
    }

    [Fact]
    public async Task ApproveThenPoll_ReturnsMintedKey_ThatAppearsUnderConnectedApps()
    {
        await _fx.ResetAsync();
        var (userId, token) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, "owner");
        var (deviceCode, userCode) = await StartAsync("rewynd");

        // Approve interactively.
        using (var jwt = AuthTestHelpers.CreateBearerClient(_factory!, token))
        {
            var approve = await jwt.PostAsJsonAsync("/me/device/approve", new { userCode });
            approve.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        // Poll returns the key.
        using var anon = _factory!.CreateClient();
        var tokenResp = await anon.PostAsJsonAsync("/auth/device/token", new { deviceCode });
        tokenResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var minted = (await tokenResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        minted.Should().StartWith(ApiKeyService.KeyPrefix);

        // It shows up in the caller's Connected apps list.
        using var jwt2 = AuthTestHelpers.CreateBearerClient(_factory!, token);
        var list = await (await jwt2.GetAsync("/me/api-keys")).Content.ReadFromJsonAsync<JsonElement>();
        list.GetArrayLength().Should().Be(1);
        list[0].GetProperty("name").GetString().Should().Be("rewynd");

        await using var db = _fx.CreateContext();
        (await db.ApiKeys.AsNoTracking().SingleAsync()).UserId.Should().Be(userId);
        // Device row is consumed after the exchange.
        (await db.DeviceAuthorizations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DenyThenPoll_ReturnsAccessDenied()
    {
        await _fx.ResetAsync();
        var (_, token) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, "owner");
        var (deviceCode, userCode) = await StartAsync();

        using (var jwt = AuthTestHelpers.CreateBearerClient(_factory!, token))
        {
            (await jwt.PostAsJsonAsync("/me/device/deny", new { userCode })).StatusCode
                .Should().Be(HttpStatusCode.NoContent);
        }

        using var anon = _factory!.CreateClient();
        var resp = await anon.PostAsJsonAsync("/auth/device/token", new { deviceCode });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("access_denied");
    }

    [Fact]
    public async Task Lookup_ReturnsClientNameForPendingRequest()
    {
        await _fx.ResetAsync();
        var (_, token) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, "owner");
        var (_, userCode) = await StartAsync("rewynd");

        using var jwt = AuthTestHelpers.CreateBearerClient(_factory!, token);
        var resp = await jwt.GetAsync($"/me/device/{Uri.EscapeDataString(userCode)}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("clientName").GetString().Should().Be("rewynd");
        body.GetProperty("status").GetString().Should().Be("pending");
    }

    [Fact]
    public async Task Approve_UnknownCode_Returns404()
    {
        await _fx.ResetAsync();
        var (_, token) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, "owner");
        using var jwt = AuthTestHelpers.CreateBearerClient(_factory!, token);

        var resp = await jwt.PostAsJsonAsync("/me/device/approve", new { userCode = "ZZZZ-ZZZZ" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ApprovalEndpoints_RequireInteractiveAuth()
    {
        await _fx.ResetAsync();
        var (_, userCode) = await StartAsync();
        using var anon = _factory!.CreateClient();

        (await anon.GetAsync($"/me/device/{Uri.EscapeDataString(userCode)}")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync("/me/device/approve", new { userCode })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync("/me/device/deny", new { userCode })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }
}
