using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("PostgresAdmin")]
public class ProfileMediaEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public ProfileMediaEndpointsTests(PostgresFixture fx) => _fx = fx;

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

    private Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username = "uploader") =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);

    private HttpClient ClientWithBearer(string token) =>
        AuthTestHelpers.CreateBearerClient(_factory!, token);

    // ---- POST /auth/me/avatar/upload-url ----

    [Fact]
    public async Task AvatarUploadUrl_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/me/avatar/upload-url", new { contentType = "image/png" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AvatarUploadUrl_DisallowedContentType_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/auth/me/avatar/upload-url", new { contentType = "image/gif" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("unsupported_content_type");
    }

    [Fact]
    public async Task AvatarUploadUrl_Happy_ReturnsSignedUrlAndUserScopedKey()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();

        _storage.GetPresignedPutUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("http://localhost:9000/avatars/signed?sig=abc");

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync("/auth/me/avatar/upload-url", new { contentType = "image/png" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("url").GetString().Should().Be("http://localhost:9000/avatars/signed?sig=abc");
        body.GetProperty("contentType").GetString().Should().Be("image/png");
        // The objectKey must be namespaced by user id + kind so the complete endpoint can
        // verify ownership without trusting the client.
        body.GetProperty("objectKey").GetString().Should().StartWith($"{userId}/avatar-");
    }

    [Fact]
    public async Task BannerUploadUrl_Happy_KeyHasBannerPrefix()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        _storage.GetPresignedPutUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("http://localhost:9000/avatars/banner-signed?sig=xyz");

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync("/auth/me/banner/upload-url", new { contentType = "image/webp" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("objectKey").GetString().Should().StartWith($"{userId}/banner-");
    }

    // ---- POST /auth/me/avatar/complete ----

    [Fact]
    public async Task AvatarComplete_OtherUsersKey_Returns400()
    {
        // Ownership-prefix guard: a user cannot complete an upload for an object that doesn't
        // live under their own userId/avatar-* prefix.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync("me");
        var attackerObjectKey = $"{Guid.NewGuid()}/avatar-victim.png";

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync("/auth/me/avatar/complete", new { objectKey = attackerObjectKey });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_object_key");
        // Storage HEAD must not be called — we reject before paying the round-trip.
        await _storage.DidNotReceive().GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _ = userId;
    }

    [Fact]
    public async Task AvatarComplete_BannerKeyOnAvatarEndpoint_Returns400()
    {
        // Cross-kind guard: a banner upload's key can't be claimed as the user's avatar.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync("/auth/me/avatar/complete",
            new { objectKey = $"{userId}/banner-aaa.png" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_object_key");
    }

    [Fact]
    public async Task AvatarComplete_ObjectMissing_Returns400()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var key = $"{userId}/avatar-abc.png";
        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ObjectMetadata?)null);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync("/auth/me/avatar/complete", new { objectKey = key });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("object_not_uploaded");
    }

    [Fact]
    public async Task AvatarComplete_FileTooLarge_Returns400AndDeletesOrphan()
    {
        // Presigned PUTs can't enforce size, so the oversized object is already in the bucket
        // when complete rejects it. Inline cleanup prevents a client from spamming the bucket
        // with rejected uploads (avatars bucket has no lifecycle expiry).
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var key = $"{userId}/avatar-abc.png";
        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(20L * 1024 * 1024, "image/png"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync("/auth/me/avatar/complete", new { objectKey = key });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("file_too_large");
        await _storage.Received(1).DeleteObjectAsync(Arg.Any<string>(), key, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AvatarComplete_DisallowedMime_Returns400AndDeletesOrphan()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var key = $"{userId}/avatar-abc.png";
        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "image/gif"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync("/auth/me/avatar/complete", new { objectKey = key });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("unsupported_content_type");
        await _storage.Received(1).DeleteObjectAsync(Arg.Any<string>(), key, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AvatarComplete_Happy_PersistsRowAndDeletesPreviousObject()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        // Seed a prior upload so we can assert the cleanup happens.
        await using (var db = _fx.CreateContext())
        {
            var u = await db.Users.SingleAsync();
            u.AvatarObjectKey = $"{userId}/avatar-OLD.png";
            u.AvatarUrl = "http://prev/avatar.png";
            u.AvatarSource = "upload";
            await db.SaveChangesAsync();
        }

        var key = $"{userId}/avatar-NEW.png";
        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "image/png"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync("/auth/me/avatar/complete", new { objectKey = key });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("avatarSource").GetString().Should().Be("upload");

        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync();
        user.AvatarObjectKey.Should().Be(key);
        user.AvatarSource.Should().Be("upload");
        user.AvatarUrl.Should().Contain($"avatars/{key}");
        await _storage.Received(1).DeleteObjectAsync(Arg.Any<string>(), $"{userId}/avatar-OLD.png", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AvatarComplete_DeleteOldObjectFails_StillReturnsOk()
    {
        // Cleanup is best-effort — a backend hiccup must not break the user-facing flow.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        await using (var db = _fx.CreateContext())
        {
            var u = await db.Users.SingleAsync();
            u.AvatarObjectKey = $"{userId}/avatar-OLD.png";
            await db.SaveChangesAsync();
        }
        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "image/png"));
        _storage.When(s => s.DeleteObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("simulated S3 failure"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync("/auth/me/avatar/complete",
            new { objectKey = $"{userId}/avatar-NEW.png" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- DELETE /auth/me/avatar ----

    [Fact]
    public async Task AvatarDelete_NoOAuthStash_NullsAllFields()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        await using (var db = _fx.CreateContext())
        {
            var u = await db.Users.SingleAsync();
            u.AvatarUrl = "http://prev/avatar.png";
            u.AvatarSource = "upload";
            u.AvatarObjectKey = $"{userId}/avatar-X.png";
            await db.SaveChangesAsync();
        }

        using var client = ClientWithBearer(token);
        var resp = await client.DeleteAsync("/auth/me/avatar");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync();
        user.AvatarUrl.Should().BeNull();
        user.AvatarSource.Should().BeNull();
        user.AvatarObjectKey.Should().BeNull();
        await _storage.Received(1).DeleteObjectAsync(Arg.Any<string>(), $"{userId}/avatar-X.png", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AvatarDelete_WithOAuthStash_RestoresProviderAvatarImmediately()
    {
        // The "Reset to OAuth avatar" affordance must show the user's Discord/Google picture
        // come back immediately, without waiting for the next OAuth login.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        await using (var db = _fx.CreateContext())
        {
            var u = await db.Users.SingleAsync();
            u.AvatarUrl = "http://prev/upload.png";
            u.AvatarSource = "upload";
            u.AvatarObjectKey = $"{userId}/avatar-X.png";
            u.OAuthAvatarUrl = "https://cdn.discord/d.png";
            u.OAuthAvatarSource = "oauth:discord";
            await db.SaveChangesAsync();
        }

        using var client = ClientWithBearer(token);
        var resp = await client.DeleteAsync("/auth/me/avatar");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("url").GetString().Should().Be("https://cdn.discord/d.png");
        body.GetProperty("avatarSource").GetString().Should().Be("oauth:discord");

        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync();
        user.AvatarUrl.Should().Be("https://cdn.discord/d.png");
        user.AvatarSource.Should().Be("oauth:discord");
        user.AvatarObjectKey.Should().BeNull();
    }

    [Fact]
    public async Task BannerDelete_NullsBannerFieldsAndDeletesObject()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        await using (var db = _fx.CreateContext())
        {
            var u = await db.Users.SingleAsync();
            u.BannerUrl = "http://prev/banner.png";
            u.BannerObjectKey = $"{userId}/banner-X.png";
            await db.SaveChangesAsync();
        }

        using var client = ClientWithBearer(token);
        var resp = await client.DeleteAsync("/auth/me/banner");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync();
        user.BannerUrl.Should().BeNull();
        user.BannerObjectKey.Should().BeNull();
        await _storage.Received(1).DeleteObjectAsync(Arg.Any<string>(), $"{userId}/banner-X.png", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AvatarUploadUrl_UserDeletedAfterTokenIssued_Returns404()
    {
        // Defensive branch — token's sub points at a row that no longer exists.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        await using (var db = _fx.CreateContext())
        {
            db.Users.Remove(await db.Users.SingleAsync(u => u.Id == userId));
            await db.SaveChangesAsync();
        }

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync("/auth/me/avatar/upload-url", new { contentType = "image/png" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AvatarComplete_UserDeletedAfterTokenIssued_Returns404()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        await using (var db = _fx.CreateContext())
        {
            db.Users.Remove(await db.Users.SingleAsync(u => u.Id == userId));
            await db.SaveChangesAsync();
        }

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "image/png"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync("/auth/me/avatar/complete",
            new { objectKey = $"{userId}/avatar-x.png" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AvatarComplete_ContentTypeWithCharset_IsAccepted()
    {
        // MIME normalization branch: HEAD returns "image/png; charset=foo" — the strip-charset
        // path inside NormalizeContentType must accept it.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "image/png; charset=binary"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync("/auth/me/avatar/complete",
            new { objectKey = $"{userId}/avatar-x.png" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AvatarComplete_EmptyObjectKey_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);
        // ValidationEndpointFilter rejects empty [Required] before the handler.
        var resp = await client.PostAsJsonAsync("/auth/me/avatar/complete", new { objectKey = "" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BannerComplete_Happy_PersistsBannerFields()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var key = $"{userId}/banner-abc.webp";
        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(2048, "image/webp"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync("/auth/me/banner/complete", new { objectKey = key });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync();
        user.BannerObjectKey.Should().Be(key);
        user.BannerUrl.Should().Contain($"avatars/{key}");
        // No source tracking for banners — banner is upload-only this pass.
        user.AvatarSource.Should().BeNull();
    }
}
