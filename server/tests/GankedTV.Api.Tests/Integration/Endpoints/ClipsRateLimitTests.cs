using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Clips;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("PostgresClips")]
public class ClipsRateLimitTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public ClipsRateLimitTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync()
    {
        // Substituting IObjectStorageService keeps these tests away from real S3 — they only
        // care about how many requests the limiter accepts, not what the upload-url handler
        // does with the response.
        _storage = Substitute.For<IObjectStorageService>();
        // A fresh factory per test gives each case its own in-process rate-limiter state, so
        // a 31st request in one test doesn't leak into another.
        _factory = new AuthApiFactory(_fx.ConnectionString, _storage);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }
    }

    private Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username) =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);

    private HttpClient ClientWithBearer(string token) =>
        AuthTestHelpers.CreateBearerClient(_factory!, token);

    private async Task<Guid> SeedClipAsync(Guid ownerId)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = ownerId,
            Title = "rl-target",
            VideoKey = $"clips/{ownerId}/{id}.mp4",
            ShareCode = ShareCodeGenerator.Next(),
            Status = "ready",
            Visibility = "public",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task CreateClip_ExceedsWritePermitLimit_Returns429WithProblemEnvelope()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync("rl-creator");
        using var client = ClientWithBearer(token);

        // Each accepted POST returns 200 with a draft clip. We don't care about the body —
        // only that the limiter lets exactly PermitLimit requests through, then blocks.
        for (var i = 0; i < ClipsRateLimiting.WritePermitLimit; i++)
        {
            var ok = await client.PostAsJsonAsync("/clips", new { title = $"clip-{i}" });
            ok.StatusCode.Should().Be(HttpStatusCode.OK, $"request #{i + 1} should be allowed");
        }

        var blocked = await client.PostAsJsonAsync("/clips", new { title = "over-limit" });
        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // RFC 7807 envelope with the machine-readable `code` extension — same shape every
        // other 4xx in the API emits.
        blocked.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty(ProblemResults.CodeKey).GetString().Should().Be(ClipsRateLimiting.RateLimitedCode);

        // Retry-After is set from the lease metadata (fixed window + AutoReplenishment emits it).
        // Asserted here rather than in every 429 test — pinning it once proves the OnRejected
        // header path runs.
        blocked.Headers.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task Like_ExceedsLikePermitLimit_Returns429()
    {
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("rl-author");
        var (_, fanToken) = await SeedUserAndIssueTokenAsync("rl-fan");
        var clipId = await SeedClipAsync(authorId);
        using var client = ClientWithBearer(fanToken);

        // Alternate like/unlike so one clip can absorb every accepted hit without a unique-key
        // collision — the limiter counts requests, not the semantics underneath.
        for (var i = 0; i < ClipsRateLimiting.LikePermitLimit; i++)
        {
            var resp = (i % 2 == 0)
                ? await client.PostAsync($"/clips/{clipId}/like", content: null)
                : await client.DeleteAsync($"/clips/{clipId}/like");
            resp.StatusCode.Should().Be(HttpStatusCode.OK, $"request #{i + 1} should be allowed");
        }

        var blocked = await client.PostAsync($"/clips/{clipId}/like", content: null);
        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        // OnRejected is global on RateLimiterOptions, so the envelope fires regardless of which
        // policy or endpoint group rejected — assert it here too so the contract is pinned for
        // the likes path on its own, not just transitively via the create test.
        blocked.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty(ProblemResults.CodeKey).GetString().Should().Be(ClipsRateLimiting.RateLimitedCode);
    }

    [Fact]
    public async Task MixedWrites_ShareBucket_AcrossEndpointGroups()
    {
        // The /clips write groups attach ClipsWritePolicy by name, so one user shares one bucket
        // across the write surface. Pin that as an intentional contract: attaching the policy to
        // another group later can't widen the per-user budget without breaking this test.
        // Likes are deliberately NOT part of it — see Likes_DoNotShareTheWriteBucket.
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync("rl-mixed");
        using var client = ClientWithBearer(token);

        // Split the permit between two groups: POST /clips (Upload) and DELETE /clips/{id}
        // (Mutate). Each delete targets a clip this user just created, so both halves are
        // legitimate requests rather than rejections the limiter would count differently.
        var half = ClipsRateLimiting.WritePermitLimit / 2;
        var created = new List<string>();
        for (var i = 0; i < half; i++)
        {
            var create = await client.PostAsJsonAsync("/clips", new { title = $"mix-{i}" });
            create.StatusCode.Should().Be(HttpStatusCode.OK);
            created.Add((await create.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id").GetString()!);
        }
        for (var i = 0; i < ClipsRateLimiting.WritePermitLimit - half; i++)
        {
            var del = await client.DeleteAsync($"/clips/{created[i]}");
            del.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        // One more from EITHER group must 429 — the bucket carries state across groups, not just
        // within the one the budget was spent in.
        var blocked = await client.PostAsJsonAsync("/clips", new { title = "over" });
        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Likes_DoNotShareTheWriteBucket()
    {
        // Liking is the cheapest, highest-frequency action in the product. On the shared bucket a
        // run down a comment thread would lock the same user out of uploading for a minute, so it
        // has its own budget: exhausting the write bucket must leave liking unaffected.
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("rl-like-author");
        var (_, fanToken) = await SeedUserAndIssueTokenAsync("rl-like-fan");
        var clipId = await SeedClipAsync(authorId);
        using var client = ClientWithBearer(fanToken);

        for (var i = 0; i < ClipsRateLimiting.WritePermitLimit; i++)
        {
            (await client.PostAsJsonAsync("/clips", new { title = $"burn-{i}" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
        (await client.PostAsJsonAsync("/clips", new { title = "over" }))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the write bucket is spent");

        var like = await client.PostAsync($"/clips/{clipId}/like", content: null);

        like.StatusCode.Should().Be(HttpStatusCode.OK, "likes have their own bucket");
    }

    [Fact]
    public async Task TwoUsers_DoNotShareBucket()
    {
        await _fx.ResetAsync();
        var (_, tokenA) = await SeedUserAndIssueTokenAsync("rl-user-a");
        var (_, tokenB) = await SeedUserAndIssueTokenAsync("rl-user-b");
        using var clientA = ClientWithBearer(tokenA);
        using var clientB = ClientWithBearer(tokenB);

        // Each user fires the full PermitLimit through their own bucket. If the policy were
        // partitioning by IP instead of by sub, the shared 127.0.0.1 bucket would collapse
        // both clients into one and the second user's last request would 429.
        for (var i = 0; i < ClipsRateLimiting.WritePermitLimit; i++)
        {
            var a = await clientA.PostAsJsonAsync("/clips", new { title = $"a-{i}" });
            a.StatusCode.Should().Be(HttpStatusCode.OK);
            var b = await clientB.PostAsJsonAsync("/clips", new { title = $"b-{i}" });
            b.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
