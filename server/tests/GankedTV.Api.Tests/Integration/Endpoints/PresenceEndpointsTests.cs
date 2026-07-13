using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Services.Presence;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("PostgresSocial")]
public class PresenceEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory _factory = null!;

    public PresenceEndpointsTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync()
    {
        _factory = new AuthApiFactory(_fx.ConnectionString, Substitute.For<IObjectStorageService>());
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed record UserDto(Guid Id, string Username, string? AvatarUrl);

    private sealed record SummaryDto(int Online, List<UserDto> FollowsOnline, int FollowsOnlineCount);

    private Task<(Guid userId, string token)> SeedUserAsync(string username) =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory, username);

    [Fact]
    public async Task Summary_Anonymous_CountsCallerWithEmptyFollows()
    {
        await _fx.ResetAsync();
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/presence/summary");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SummaryDto>();
        body!.Online.Should().Be(1);
        body.FollowsOnline.Should().BeEmpty();
    }

    [Fact]
    public async Task Summary_DistinctClientIds_CountSeparately()
    {
        await _fx.ResetAsync();
        using var client = _factory.CreateClient();

        await client.GetAsync("/presence/summary?cid=browser-a");
        var resp = await client.GetAsync("/presence/summary?cid=browser-b");

        var body = await resp.Content.ReadFromJsonAsync<SummaryDto>();
        body!.Online.Should().Be(2);
    }

    [Fact]
    public async Task Summary_Authenticated_IncludesOnlineFollow()
    {
        await _fx.ResetAsync();
        var (aliceId, aliceToken) = await SeedUserAsync("alice");
        var (bobId, bobToken) = await SeedUserAsync("bob");
        await FollowAsync(follower: aliceId, followee: bobId);

        // Bob polls first → recorded as online; then Alice polls and should see him.
        using (var bob = AuthTestHelpers.CreateBearerClient(_factory, bobToken))
        {
            await bob.GetAsync("/presence/summary");
        }

        using var alice = AuthTestHelpers.CreateBearerClient(_factory, aliceToken);
        var resp = await alice.GetAsync("/presence/summary");

        var body = await resp.Content.ReadFromJsonAsync<SummaryDto>();
        body!.FollowsOnline.Should().ContainSingle().Which.Id.Should().Be(bobId);
        body.FollowsOnlineCount.Should().Be(1);
    }

    [Fact]
    public async Task Summary_FollowsOnlineCount_IsUncappedTotal()
    {
        await _fx.ResetAsync();
        // Cap the page at 2: the list is capped but the count stays the true total,
        // so the client's "+N more" overflow is honest.
        await using var factory = new AuthApiFactory(
            _fx.ConnectionString,
            Substitute.For<IObjectStorageService>(),
            configureServices: s => s.Configure<PresenceOptions>(o => o.FollowsOnlineCap = 2));

        var (aliceId, aliceToken) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, factory, "alice");
        for (var i = 0; i < 3; i++)
        {
            var (followeeId, followeeToken) =
                await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, factory, $"streamer{i}");
            await FollowAsync(follower: aliceId, followee: followeeId);
            using var followee = AuthTestHelpers.CreateBearerClient(factory, followeeToken);
            await followee.GetAsync("/presence/summary");
        }

        using var alice = AuthTestHelpers.CreateBearerClient(factory, aliceToken);
        var resp = await alice.GetAsync("/presence/summary");

        var body = await resp.Content.ReadFromJsonAsync<SummaryDto>();
        body!.FollowsOnline.Should().HaveCount(2);
        body.FollowsOnlineCount.Should().Be(3);
    }

    [Fact]
    public async Task Summary_Authenticated_ExcludesOfflineFollow()
    {
        await _fx.ResetAsync();
        var (aliceId, aliceToken) = await SeedUserAsync("alice");
        var (bobId, _) = await SeedUserAsync("bob");
        await FollowAsync(follower: aliceId, followee: bobId);

        // Bob never polls, so he isn't online.
        using var alice = AuthTestHelpers.CreateBearerClient(_factory, aliceToken);
        var resp = await alice.GetAsync("/presence/summary");

        var body = await resp.Content.ReadFromJsonAsync<SummaryDto>();
        body!.FollowsOnline.Should().BeEmpty();
    }

    [Fact]
    public async Task Summary_Disabled_Returns503()
    {
        await _fx.ResetAsync();
        // configureServices runs last, so this Configure wins over the env-binding registration.
        await using var factory = new AuthApiFactory(
            _fx.ConnectionString,
            Substitute.For<IObjectStorageService>(),
            configureServices: s => s.Configure<PresenceOptions>(o => o.Enabled = false));
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/presence/summary");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Summary_ExceedingPerIpLimit_Returns429()
    {
        await _fx.ResetAsync();
        using var client = _factory.CreateClient();

        // Exhaust the per-IP fixed window; the calls share one bucket (same loopback IP).
        for (var i = 0; i < PresenceRateLimiting.PermitLimit; i++)
        {
            (await client.GetAsync("/presence/summary")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var resp = await client.GetAsync("/presence/summary");

        resp.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    private async Task FollowAsync(Guid follower, Guid followee)
    {
        await using var db = _fx.CreateContext();
        db.Follows.Add(new Follow
        {
            FollowerId = follower,
            FolloweeId = followee,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
