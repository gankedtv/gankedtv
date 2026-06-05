using GankedTV.Api.Auth.Providers;
using GankedTV.Api.Data;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace GankedTV.Api.Tests.TestSupport;

public sealed class AuthApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly IObjectStorageService? _storageOverride;
    private readonly string _environment;
    private readonly IReadOnlyList<IOAuthProvider>? _oauthProviders;
    private readonly S3Fixture? _s3Fixture;
    private readonly Action<IServiceCollection>? _configureServices;

    // `oauthProviders`, when non-null, REPLACES the real Discord/Google registrations
    // wholesale (they are not merged). Passing an empty list therefore leaves the registry
    // with zero providers and every `/auth/{provider}/start` returns 404. Pass null to keep
    // the real providers untouched.
    //
    // `s3Fixture`, when non-null, points the production object-storage service at the test
    // S3 container by overriding S3Options. This is the end-to-end path used by
    // [Collection("PostgresAndS3")] tests — IObjectStorageService is NOT replaced.
    // Mutually exclusive with `storageOverride` (which substitutes the service wholesale).
    //
    // `configureServices`, when non-null, runs LAST inside ConfigureWebHost so tests can
    // swap arbitrary singletons/scoped services for substitutes (e.g. IClipImportSource)
    // without growing this constructor every time a new replaceable service appears.
    public AuthApiFactory(
        string connectionString,
        IObjectStorageService? storageOverride = null,
        string environment = "Development",
        IReadOnlyList<IOAuthProvider>? oauthProviders = null,
        S3Fixture? s3Fixture = null,
        Action<IServiceCollection>? configureServices = null)
    {
        if (storageOverride is not null && s3Fixture is not null)
        {
            throw new ArgumentException(
                "storageOverride and s3Fixture are mutually exclusive: one substitutes IObjectStorageService, " +
                "the other rewires it to a real S3 container.",
                nameof(s3Fixture));
        }

        _connectionString = connectionString;
        _storageOverride = storageOverride;
        _environment = environment;
        _oauthProviders = oauthProviders;
        _s3Fixture = s3Fixture;
        _configureServices = configureServices;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("DATABASE_URL", _connectionString);
        Environment.SetEnvironmentVariable("JWT_SECRET", "smoke-test-jwt-secret-at-least-32-bytes-xx");
        Environment.SetEnvironmentVariable("OAUTH_STATE_SECRET", "smoke-test-state-secret-at-least-32-bytes");
        Environment.SetEnvironmentVariable("WEB_ORIGIN", "http://localhost:5173");
        // Required by ProductionStartupValidator when environment == Production. Harmless in
        // Development runs (the validator only fires in Production). Non-default S3 creds + a
        // PublicUrl satisfy the "no dev defaults in prod" checks. CORS_ORIGINS must be present
        // for the validator, but CorsOriginsTests set their own value before constructing the
        // factory — only default it when unset so their assertions still win.
        if (Environment.GetEnvironmentVariable("CORS_ORIGINS") is null)
        {
            Environment.SetEnvironmentVariable("CORS_ORIGINS", "http://localhost:5173");
        }
        Environment.SetEnvironmentVariable("S3_ENDPOINT", "http://localhost:9000");
        Environment.SetEnvironmentVariable("S3_ACCESS_KEY", "test-s3-access-key");
        Environment.SetEnvironmentVariable("S3_SECRET_KEY", "test-s3-secret-key");
        Environment.SetEnvironmentVariable("S3_PUBLIC_URL", "http://localhost:9000");
        Environment.SetEnvironmentVariable("DISCORD_CLIENT_ID", "test-discord-client");
        Environment.SetEnvironmentVariable("DISCORD_CLIENT_SECRET", "test-discord-secret");
        Environment.SetEnvironmentVariable("DISCORD_REDIRECT_URI", "http://localhost:5000/auth/discord/callback");
        Environment.SetEnvironmentVariable("GOOGLE_CLIENT_ID", "test-google-client");
        Environment.SetEnvironmentVariable("GOOGLE_CLIENT_SECRET", "test-google-secret");
        Environment.SetEnvironmentVariable("GOOGLE_REDIRECT_URI", "http://localhost:5000/auth/google/callback");
        // Force the in-process rate limiter inside the test rig. Without this, a worktree that
        // sets REDIS_URL (per the `parallel worktrees` workflow) makes every AuthApiFactory
        // instance share one Redis-backed rate-limit bucket — `[Collection("Postgres")]`
        // serialises tests within a class but each test still creates a fresh factory, and the
        // 5-req/min credentials bucket collapses across all of them, surfacing as cascading 429s
        // and downstream 401s in tests that depend on a successful register.
        Environment.SetEnvironmentVariable("REDIS_URL", null);

        // Neutralise the Vaultwarden bootstrap inside the test rig. A developer's repo-root .env
        // (loaded by Program.cs in Development) sets VAULTWARDEN_API_URL/KEY process-wide, and that
        // value leaks across in-process host boots — a later Production-environment factory would
        // then fail-fast fetching real secrets from a live vault. Empty (not null) so it reads as
        // not-configured AND survives the .env NoClobber reload; tests stay hermetic and need no
        // secrets backend, exactly like CI (which has no .env).
        Environment.SetEnvironmentVariable("VAULTWARDEN_API_URL", "");
        Environment.SetEnvironmentVariable("VAULTWARDEN_API_KEY", "");

        builder.UseEnvironment(_environment);
        builder.ConfigureServices(services =>
        {
            // Replace the hosted bucket-bootstrap service so we don't need an S3 backend running.
            services.RemoveAll<IHostedService>();

            // Ensure the DbContext uses the fixture's connection string even if a prior
            // test registration survived.
            services.RemoveAll<DbContextOptions<GankedTvDbContext>>();
            services.AddDbContext<GankedTvDbContext>(opts =>
                opts.UseNpgsql(_connectionString).UseSnakeCaseNamingConvention());

            if (_storageOverride is not null)
            {
                services.RemoveAll<IObjectStorageService>();
                services.AddSingleton(_storageOverride);
            }

            if (_s3Fixture is not null)
            {
                // Re-Configure runs after Program.cs's binding, so the values below win.
                // PublicUrl stays null: the fixture endpoint is already host-reachable
                // (http://localhost:{mappedPort}), so signed URLs are usable as-is and
                // RewriteHost becomes a pass-through (its non-trivial branches are pinned
                // by ObjectStorageTests.RewriteHost*).
                var fx = _s3Fixture;
                services.Configure<S3Options>(opts =>
                {
                    opts.Endpoint = fx.Endpoint;
                    opts.AccessKey = fx.AccessKey;
                    opts.SecretKey = fx.SecretKey;
                    opts.PublicUrl = null;
                    opts.ClipsBucket = S3Fixture.ClipsBucket;
                    opts.ThumbnailsBucket = S3Fixture.ThumbnailsBucket;
                });
            }

            if (_oauthProviders is not null)
            {
                services.RemoveAll<IOAuthProvider>();
                foreach (var provider in _oauthProviders)
                {
                    services.AddSingleton(provider);
                }
            }

            // Catch-all extension point — runs last so callers can replace whatever they want
            // (RemoveAll<T> + AddSingleton/Scoped) without the factory needing a dedicated
            // constructor parameter per service.
            _configureServices?.Invoke(services);
        });
    }
}
