using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using GankedTV.Api.Clips;
using GankedTV.Api.Contracts.Moderation;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("PostgresAdmin")]
public class AdminEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public AdminEndpointsTests(PostgresFixture fx) => _fx = fx;

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

    private Task<(Guid userId, string token)> SeedUserAsync(string username, string role = UserRoles.User) =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username, u => u.Role = role);

    private async Task<Guid> SeedClipAsync(Guid ownerId, string visibility = "public", int? gameId = null)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = ownerId,
            GameId = gameId,
            Title = "target",
            VideoKey = $"clips/{ownerId}/{id}.mp4",
            ThumbnailKey = $"thumbs/{id}.jpg",
            ShareCode = ShareCodeGenerator.Next(),
            Status = "ready",
            Visibility = visibility,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedReportAsync(Guid reporterId, string targetType, Guid targetId,
        string status = "open", string reason = "spam")
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        db.Reports.Add(new Report
        {
            Id = id,
            ReporterId = reporterId,
            TargetType = targetType,
            TargetId = targetId,
            Reason = reason,
            Status = status,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ---- Authorization ----

    [Fact]
    public async Task ListReports_AsAnonymous_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/admin/reports");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListReports_AsUser_Returns403()
    {
        await _fx.ResetAsync();
        var (_, userToken) = await SeedUserAsync("normal");
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, userToken);
        var resp = await client.GetAsync("/admin/reports");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListReports_AsModerator_Returns200()
    {
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.GetAsync("/admin/reports");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ListReports_AsAdmin_Returns200()
    {
        await _fx.ResetAsync();
        var (_, adminToken) = await SeedUserAsync("admin", UserRoles.Admin);
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, adminToken);
        var resp = await client.GetAsync("/admin/reports");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResolveReport_AsAdmin_Returns200()
    {
        // Pins the asymmetric policy stack: admin satisfies the moderator-tier group policy on
        // /admin/reports/{id}/resolve too. Mirrors RoleAuthorization.AddRolePolicies — without
        // this, a future refactor that breaks the "admin implies moderator" assertion would
        // only be caught by the moderator-positive test, leaving admins silently 403'd from
        // every queue action.
        await _fx.ResetAsync();
        var (_, adminToken) = await SeedUserAsync("admin", UserRoles.Admin);
        var (ownerId, _) = await SeedUserAsync("owner");
        var (reporterId, _) = await SeedUserAsync("reporter");
        var clipId = await SeedClipAsync(ownerId);
        var reportId = await SeedReportAsync(reporterId, ReportTargetTypes.Clip, clipId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, adminToken);
        var resp = await client.PostAsJsonAsync(
            $"/admin/reports/{reportId}/resolve",
            new { outcome = "resolved" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BanUser_AsModerator_Returns403()
    {
        // Moderator can hide content but cannot disable accounts — that's admin-only.
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        var (targetId, _) = await SeedUserAsync("target");
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.PostAsJsonAsync($"/admin/users/{targetId}/ban", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Queue hydration ----

    [Fact]
    public async Task ListReports_HydratesCommentTarget()
    {
        // Exercises the comment branch of the polymorphic ListReports projection.
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        var (ownerId, _) = await SeedUserAsync("owner");
        var (authorId, _) = await SeedUserAsync("author");
        var (reporterId, _) = await SeedUserAsync("reporter");
        var clipId = await SeedClipAsync(ownerId);

        Guid commentId;
        await using (var db = _fx.CreateContext())
        {
            var c = new Comment
            {
                Id = Guid.NewGuid(),
                ClipId = clipId,
                UserId = authorId,
                Body = "to-report",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Comments.Add(c);
            await db.SaveChangesAsync();
            commentId = c.Id;
        }
        await SeedReportAsync(reporterId, ReportTargetTypes.Comment, commentId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.GetAsync("/admin/reports?status=open");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await resp.Content.ReadFromJsonAsync<ReportListResponse>();
        page!.Items[0].TargetType.Should().Be(ReportTargetTypes.Comment);
        page.Items[0].Target.Comment.Should().NotBeNull();
        page.Items[0].Target.Comment!.Body.Should().Be("to-report");
        // Clip/user branches are null on a comment row — pins the discriminated-union shape.
        page.Items[0].Target.Clip.Should().BeNull();
        page.Items[0].Target.User.Should().BeNull();
    }

    [Fact]
    public async Task ListReports_HydratesUserTarget()
    {
        // Exercises the user branch of the polymorphic ListReports projection.
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        var (targetUserId, _) = await SeedUserAsync("target");
        var (reporterId, _) = await SeedUserAsync("reporter");
        await SeedReportAsync(reporterId, ReportTargetTypes.User, targetUserId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.GetAsync("/admin/reports?status=open");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await resp.Content.ReadFromJsonAsync<ReportListResponse>();
        page!.Items[0].TargetType.Should().Be(ReportTargetTypes.User);
        page.Items[0].Target.User.Should().NotBeNull();
        page.Items[0].Target.User!.Username.Should().Be("target");
        page.Items[0].Target.Clip.Should().BeNull();
        page.Items[0].Target.Comment.Should().BeNull();
    }

    [Fact]
    public async Task ListReports_HydratesClipTarget()
    {
        await _fx.ResetAsync();
        var (modId, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        var (_, _) = (modId, modToken);
        var (ownerId, _) = await SeedUserAsync("owner");
        var (reporterId, _) = await SeedUserAsync("reporter");
        var clipId = await SeedClipAsync(ownerId);
        await SeedReportAsync(reporterId, ReportTargetTypes.Clip, clipId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.GetAsync("/admin/reports?status=open");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await resp.Content.ReadFromJsonAsync<ReportListResponse>();
        page.Should().NotBeNull();
        page!.Items.Should().HaveCount(1);
        page.Items[0].TargetType.Should().Be(ReportTargetTypes.Clip);
        page.Items[0].Target.Clip.Should().NotBeNull();
        page.Items[0].Target.Clip!.Title.Should().Be("target");
    }

    // ---- Resolve ----

    [Fact]
    public async Task ResolveReport_AsModerator_Sets200AndStatus()
    {
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        var (ownerId, _) = await SeedUserAsync("owner");
        var (reporterId, _) = await SeedUserAsync("reporter");
        var clipId = await SeedClipAsync(ownerId);
        var reportId = await SeedReportAsync(reporterId, ReportTargetTypes.Clip, clipId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.PostAsJsonAsync($"/admin/reports/{reportId}/resolve",
            new { outcome = "resolved" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = _fx.CreateContext();
        var report = await verify.Reports.SingleAsync(r => r.Id == reportId);
        report.Status.Should().Be(ReportStatuses.Resolved);
        report.ResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ResolveReport_AlreadyResolved_Returns409()
    {
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        var (ownerId, _) = await SeedUserAsync("owner");
        var (reporterId, _) = await SeedUserAsync("reporter");
        var clipId = await SeedClipAsync(ownerId);
        var reportId = await SeedReportAsync(reporterId, ReportTargetTypes.Clip, clipId,
            status: ReportStatuses.Resolved);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.PostAsJsonAsync($"/admin/reports/{reportId}/resolve",
            new { outcome = "dismissed" });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- Hide clip + auto-resolve ----

    [Fact]
    public async Task HideClip_SetsVisibilityHidden_AndAutoResolvesOpenReports()
    {
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        var (ownerId, _) = await SeedUserAsync("owner");
        var (reporterId, _) = await SeedUserAsync("reporter");
        var clipId = await SeedClipAsync(ownerId);
        var reportId = await SeedReportAsync(reporterId, ReportTargetTypes.Clip, clipId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.PostAsJsonAsync($"/admin/clips/{clipId}/hide", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = _fx.CreateContext();
        (await verify.Clips.SingleAsync(c => c.Id == clipId)).Visibility.Should().Be(ClipVisibilities.Hidden);
        (await verify.Reports.SingleAsync(r => r.Id == reportId)).Status.Should().Be(ReportStatuses.Resolved);
    }

    [Fact]
    public async Task HideClip_PreviouslyPublic_FlipsVisibilityToHidden()
    {
        // The public feed uses `Visibility = "public"` as its filter — once the row's value
        // changes, the existing feed query excludes it for free. We assert against the DB
        // directly (rather than re-querying /clips/feed) so the test is independent of the
        // hot-feed Redis cache TTL behaviour, which is exercised separately.
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        var (ownerId, _) = await SeedUserAsync("owner");
        var clipId = await SeedClipAsync(ownerId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var hide = await client.PostAsJsonAsync($"/admin/clips/{clipId}/hide", new { });
        hide.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verify = _fx.CreateContext();
        (await verify.Clips.SingleAsync(c => c.Id == clipId)).Visibility.Should().Be(ClipVisibilities.Hidden);
    }

    [Fact]
    public async Task UnhideClip_RestoresVisibilityPublic()
    {
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        var (ownerId, _) = await SeedUserAsync("owner");
        var clipId = await SeedClipAsync(ownerId, visibility: ClipVisibilities.Hidden);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var unhide = await client.PostAsJsonAsync($"/admin/clips/{clipId}/unhide", new { });
        unhide.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verify = _fx.CreateContext();
        (await verify.Clips.SingleAsync(c => c.Id == clipId)).Visibility.Should().Be(ClipVisibilities.Public);
    }

    // ---- Ban / unban ----

    [Fact]
    public async Task BanUser_AsAdmin_SetsBannedAt_AndBlocksTokenOnNextRequest()
    {
        await _fx.ResetAsync();
        var (_, adminToken) = await SeedUserAsync("admin", UserRoles.Admin);
        var (targetId, targetToken) = await SeedUserAsync("target");

        // Target's token works before the ban.
        using (var preClient = AuthTestHelpers.CreateBearerClient(_factory!, targetToken))
        {
            var preMe = await preClient.GetAsync("/auth/me");
            preMe.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var adminClient = AuthTestHelpers.CreateBearerClient(_factory!, adminToken);
        var resp = await adminClient.PostAsJsonAsync($"/admin/users/{targetId}/ban",
            new { reason = "spamming" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verify = _fx.CreateContext();
        (await verify.Users.SingleAsync(u => u.Id == targetId)).BannedAt.Should().NotBeNull();

        // Existing JWT is rejected on the next call.
        using var bannedClient = AuthTestHelpers.CreateBearerClient(_factory!, targetToken);
        var meAfter = await bannedClient.GetAsync("/auth/me");
        meAfter.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BanUser_ReasonOverMaxLength_Returns400()
    {
        // The DB column is varchar(500); WithValidation<BanUserRequest> on the endpoint
        // should surface that as a 400 ValidationProblem instead of letting the request
        // through and triggering a DbUpdateException on save.
        await _fx.ResetAsync();
        var (_, adminToken) = await SeedUserAsync("admin", UserRoles.Admin);
        var (targetId, _) = await SeedUserAsync("target");
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, adminToken);

        var resp = await client.PostAsJsonAsync(
            $"/admin/users/{targetId}/ban",
            new { reason = new string('x', 501) });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BanUser_Self_Returns400()
    {
        await _fx.ResetAsync();
        var (adminId, adminToken) = await SeedUserAsync("admin", UserRoles.Admin);
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, adminToken);
        var resp = await client.PostAsJsonAsync($"/admin/users/{adminId}/ban", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnbanUser_ClearsBanAndRestoresAccess()
    {
        await _fx.ResetAsync();
        var (_, adminToken) = await SeedUserAsync("admin", UserRoles.Admin);
        var (targetId, targetToken) = await SeedUserAsync("target");

        using var adminClient = AuthTestHelpers.CreateBearerClient(_factory!, adminToken);
        await adminClient.PostAsJsonAsync($"/admin/users/{targetId}/ban", new { });
        var unban = await adminClient.PostAsJsonAsync($"/admin/users/{targetId}/unban", new { });
        unban.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verify = _fx.CreateContext();
        (await verify.Users.SingleAsync(u => u.Id == targetId)).BannedAt.Should().BeNull();

        // Old token usable again.
        using var targetClient = AuthTestHelpers.CreateBearerClient(_factory!, targetToken);
        var resp = await targetClient.GetAsync("/auth/me");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BannedUser_CannotCreateReport_Returns401()
    {
        await _fx.ResetAsync();
        var (_, adminToken) = await SeedUserAsync("admin", UserRoles.Admin);
        var (ownerId, _) = await SeedUserAsync("owner");
        var (targetId, targetToken) = await SeedUserAsync("reporter");
        var clipId = await SeedClipAsync(ownerId);

        using var adminClient = AuthTestHelpers.CreateBearerClient(_factory!, adminToken);
        await adminClient.PostAsJsonAsync($"/admin/users/{targetId}/ban", new { });

        using var bannedClient = AuthTestHelpers.CreateBearerClient(_factory!, targetToken);
        var resp = await bannedClient.PostAsJsonAsync($"/clips/{clipId}/report", new { reason = "spam" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- Fix game (wrong_game report remediation) ----

    [Fact]
    public async Task SetClipGame_UpdatesClipGame_AndResolvesOnlyWrongGameReports()
    {
        // The clip ends up with the new game tag, the wrong_game report flips to resolved,
        // but an unrelated spam report against the same clip stays open — admin still has
        // to triage abuse separately.
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        var (ownerId, _) = await SeedUserAsync("owner");
        var (reporterId, _) = await SeedUserAsync("reporter");
        var (otherReporterId, _) = await SeedUserAsync("reporter2");
        var clipId = await SeedClipAsync(ownerId);
        var wrongGameReportId = await SeedReportAsync(reporterId, ReportTargetTypes.Clip, clipId,
            reason: ReportReasons.WrongGame);
        var spamReportId = await SeedReportAsync(otherReporterId, ReportTargetTypes.Clip, clipId,
            reason: ReportReasons.Spam);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.PostAsJsonAsync($"/admin/clips/{clipId}/game", new { gameId = 3 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = _fx.CreateContext();
        (await verify.Clips.SingleAsync(c => c.Id == clipId)).GameId.Should().Be(3);
        (await verify.Reports.SingleAsync(r => r.Id == wrongGameReportId)).Status
            .Should().Be(ReportStatuses.Resolved);
        // Unrelated abuse report stays open — Fix game is reason-scoped.
        (await verify.Reports.SingleAsync(r => r.Id == spamReportId)).Status
            .Should().Be(ReportStatuses.Open);
    }

    [Fact]
    public async Task SetClipGame_NullGameId_ClearsTag()
    {
        // Seed with a non-null GameId so the assertion actually pins the transition (a clip
        // that was never tagged would trivially pass even if the endpoint did nothing).
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        var (ownerId, _) = await SeedUserAsync("owner");
        var clipId = await SeedClipAsync(ownerId, gameId: 2);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.PostAsJsonAsync($"/admin/clips/{clipId}/game", new { gameId = (int?)null });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = _fx.CreateContext();
        (await verify.Clips.SingleAsync(c => c.Id == clipId)).GameId.Should().BeNull();
    }

    [Fact]
    public async Task SetClipGame_UnknownGame_Returns400()
    {
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        var (ownerId, _) = await SeedUserAsync("owner");
        var clipId = await SeedClipAsync(ownerId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.PostAsJsonAsync($"/admin/clips/{clipId}/game", new { gameId = 99999 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetClipGame_AsUser_Returns403()
    {
        await _fx.ResetAsync();
        var (_, userToken) = await SeedUserAsync("normal");
        var (ownerId, _) = await SeedUserAsync("owner");
        var clipId = await SeedClipAsync(ownerId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, userToken);
        var resp = await client.PostAsJsonAsync($"/admin/clips/{clipId}/game", new { gameId = 1 });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Edge cases / error branches ----

    [Fact]
    public async Task ListReports_PageTooLarge_Returns400()
    {
        // (page-1)*pageSize must fit in an int. A hostile page=int.MaxValue request would
        // wrap to a negative offset under naïve int math; the endpoint clamps in long and
        // refuses anything that would overflow.
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.GetAsync($"/admin/reports?page={int.MaxValue}&pageSize=100");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListReports_InvalidStatus_Returns400()
    {
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.GetAsync("/admin/reports?status=bogus");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResolveReport_NotFound_Returns404()
    {
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.PostAsJsonAsync($"/admin/reports/{Guid.NewGuid()}/resolve",
            new { outcome = "resolved" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ResolveReport_InvalidOutcome_Returns400()
    {
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        var (ownerId, _) = await SeedUserAsync("owner");
        var (reporterId, _) = await SeedUserAsync("reporter");
        var clipId = await SeedClipAsync(ownerId);
        var reportId = await SeedReportAsync(reporterId, ReportTargetTypes.Clip, clipId);
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.PostAsJsonAsync($"/admin/reports/{reportId}/resolve",
            new { outcome = "bogus" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task HideClip_NotFound_Returns404()
    {
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.PostAsJsonAsync($"/admin/clips/{Guid.NewGuid()}/hide", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnhideClip_NotFound_Returns404()
    {
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.PostAsJsonAsync($"/admin/clips/{Guid.NewGuid()}/unhide", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RemoveComment_SoftDeletes_AndAutoResolvesReports()
    {
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        var (ownerId, _) = await SeedUserAsync("owner");
        var (authorId, _) = await SeedUserAsync("author");
        var (reporterId, _) = await SeedUserAsync("reporter");
        var clipId = await SeedClipAsync(ownerId);

        // Seed a comment + an open report against it.
        Guid commentId;
        await using (var db = _fx.CreateContext())
        {
            var c = new Comment
            {
                Id = Guid.NewGuid(),
                ClipId = clipId,
                UserId = authorId,
                Body = "bad",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Comments.Add(c);
            await db.SaveChangesAsync();
            commentId = c.Id;
        }
        var reportId = await SeedReportAsync(reporterId, ReportTargetTypes.Comment, commentId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.PostAsJsonAsync($"/admin/comments/{commentId}/remove", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verify = _fx.CreateContext();
        (await verify.Comments.SingleAsync(c => c.Id == commentId)).DeletedAt.Should().NotBeNull();
        (await verify.Reports.SingleAsync(r => r.Id == reportId)).Status.Should().Be(ReportStatuses.Resolved);
    }

    [Fact]
    public async Task RemoveComment_NotFound_Returns404()
    {
        await _fx.ResetAsync();
        var (_, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);
        var resp = await client.PostAsJsonAsync($"/admin/comments/{Guid.NewGuid()}/remove", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BanUser_NotFound_Returns404()
    {
        await _fx.ResetAsync();
        var (_, adminToken) = await SeedUserAsync("admin", UserRoles.Admin);
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, adminToken);
        var resp = await client.PostAsJsonAsync($"/admin/users/{Guid.NewGuid()}/ban", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnbanUser_NotFound_Returns404()
    {
        await _fx.ResetAsync();
        var (_, adminToken) = await SeedUserAsync("admin", UserRoles.Admin);
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, adminToken);
        var resp = await client.PostAsJsonAsync($"/admin/users/{Guid.NewGuid()}/unban", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MeResponse_IncludesRoleClaim()
    {
        await _fx.ResetAsync();
        var (_, adminToken) = await SeedUserAsync("admin", UserRoles.Admin);
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, adminToken);

        var resp = await client.GetAsync("/auth/me");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"role\":\"admin\"");
    }

    // ---- Requeue failed media ----

    private async Task<Guid> SeedFailedClipAsync(Guid ownerId, string failureReason)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = ownerId,
            Title = "failed",
            VideoKey = $"clips/{ownerId}/{id}.mp4",
            ShareCode = ShareCodeGenerator.Next(),
            Status = ClipStatuses.Failed,
            Visibility = "public",
            ProcessingAttempts = 3,
            FailureReason = failureReason,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task RequeueFailedMedia_AsUser_Returns403()
    {
        await _fx.ResetAsync();
        var (_, userToken) = await SeedUserAsync("normal");
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, userToken);

        var resp = await client.PostAsJsonAsync("/admin/clips/media/requeue", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RequeueFailedMedia_AsModerator_RequeuesInfraFailure()
    {
        await _fx.ResetAsync();
        var (modId, modToken) = await SeedUserAsync("mod", UserRoles.Moderator);
        var clipId = await SeedFailedClipAsync(modId, ClipFailureReasons.SourceUnavailable);
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, modToken);

        var resp = await client.PostAsJsonAsync("/admin/clips/media/requeue", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("requeued").GetInt32().Should().Be(1);

        await using var verify = _fx.CreateContext();
        var clip = await verify.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be(ClipStatuses.Processing);
        clip.FailureReason.Should().BeNull();
    }
}
