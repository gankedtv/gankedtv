using FluentAssertions;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.HostedServices;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GankedTV.Api.Tests.Services;

[Collection("PostgresServices")]
public class AdminBootstrapTests : IDisposable
{
    private readonly PostgresFixture _fx;
    // Captured at construction so Dispose can restore any value the host process had set
    // (e.g. an outer integration run that exported ADMIN_EMAILS) — mutating a process-wide
    // env var inside a test without restoring it would leak into every later test that
    // calls AdminBootstrap.StartAsync.
    private readonly string? _originalAdminEmails =
        Environment.GetEnvironmentVariable("ADMIN_EMAILS");

    public AdminBootstrapTests(PostgresFixture fx) => _fx = fx;

    public void Dispose() =>
        Environment.SetEnvironmentVariable("ADMIN_EMAILS", _originalAdminEmails);

    private async Task<Guid> SeedUserAsync(string email, string role = UserRoles.User)
    {
        await using var db = _fx.CreateContext();
        var id = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = id,
            Username = $"u-{id:N}".Substring(0, 12),
            Email = email,
            Role = role,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private AdminBootstrap CreateBootstrap(string? envValue)
    {
        // Build an IServiceScopeFactory that hands out fresh DbContexts pointed at the fixture.
        var services = new ServiceCollection();
        services.AddDbContext<GankedTvDbContext>(opts =>
            opts.UseNpgsql(_fx.ConnectionString).UseSnakeCaseNamingConvention());
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var config = new ConfigurationBuilder().Build();
        Environment.SetEnvironmentVariable("ADMIN_EMAILS", envValue);
        return new AdminBootstrap(scopeFactory, config, NullLogger<AdminBootstrap>.Instance);
    }

    [Fact]
    public async Task NoEnv_NoOp()
    {
        await _fx.ResetAsync();
        var id = await SeedUserAsync("user@example.com");
        var bootstrap = CreateBootstrap(null);

        await bootstrap.StartAsync(CancellationToken.None);

        await using var verify = _fx.CreateContext();
        (await verify.Users.SingleAsync(u => u.Id == id)).Role.Should().Be(UserRoles.User);
    }

    [Fact]
    public async Task MatchingEmail_PromotesToAdmin()
    {
        await _fx.ResetAsync();
        var id = await SeedUserAsync("alice@example.com");
        var bootstrap = CreateBootstrap("alice@example.com");

        await bootstrap.StartAsync(CancellationToken.None);

        await using var verify = _fx.CreateContext();
        (await verify.Users.SingleAsync(u => u.Id == id)).Role.Should().Be(UserRoles.Admin);
    }

    [Fact]
    public async Task MultipleEmails_PromotesAll_Idempotent()
    {
        await _fx.ResetAsync();
        var a = await SeedUserAsync("alice@example.com");
        var b = await SeedUserAsync("bob@example.com");
        var c = await SeedUserAsync("uninvolved@example.com");
        var bootstrap = CreateBootstrap("alice@example.com, bob@example.com");

        // Run twice — idempotent.
        await bootstrap.StartAsync(CancellationToken.None);
        await bootstrap.StartAsync(CancellationToken.None);

        await using var verify = _fx.CreateContext();
        (await verify.Users.SingleAsync(u => u.Id == a)).Role.Should().Be(UserRoles.Admin);
        (await verify.Users.SingleAsync(u => u.Id == b)).Role.Should().Be(UserRoles.Admin);
        (await verify.Users.SingleAsync(u => u.Id == c)).Role.Should().Be(UserRoles.User);
    }

    [Fact]
    public void ParseEmails_HandlesCommasWhitespaceAndCase()
    {
        var parsed = AdminBootstrap.ParseEmails("Alice@Example.com, bob@example.com,,, charlie@x.io ");
        parsed.Should().BeEquivalentTo(["alice@example.com", "bob@example.com", "charlie@x.io"]);
    }
}
