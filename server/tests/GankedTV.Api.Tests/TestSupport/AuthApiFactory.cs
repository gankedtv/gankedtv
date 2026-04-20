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

    // `oauthProviders`, when non-null, REPLACES the real Discord/Google registrations
    // wholesale (they are not merged). Passing an empty list therefore leaves the registry
    // with zero providers and every `/auth/{provider}/start` returns 404. Pass null to keep
    // the real providers untouched.
    public AuthApiFactory(
        string connectionString,
        IObjectStorageService? storageOverride = null,
        string environment = "Development",
        IReadOnlyList<IOAuthProvider>? oauthProviders = null)
    {
        _connectionString = connectionString;
        _storageOverride = storageOverride;
        _environment = environment;
        _oauthProviders = oauthProviders;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("DATABASE_URL", _connectionString);
        Environment.SetEnvironmentVariable("JWT_SECRET", "smoke-test-jwt-secret-at-least-32-bytes-xx");
        Environment.SetEnvironmentVariable("OAUTH_STATE_SECRET", "smoke-test-state-secret-at-least-32-bytes");
        Environment.SetEnvironmentVariable("WEB_ORIGIN", "http://localhost:5173");
        Environment.SetEnvironmentVariable("DISCORD_CLIENT_ID", "test-discord-client");
        Environment.SetEnvironmentVariable("DISCORD_CLIENT_SECRET", "test-discord-secret");
        Environment.SetEnvironmentVariable("DISCORD_REDIRECT_URI", "http://localhost:5000/auth/discord/callback");
        Environment.SetEnvironmentVariable("GOOGLE_CLIENT_ID", "test-google-client");
        Environment.SetEnvironmentVariable("GOOGLE_CLIENT_SECRET", "test-google-secret");
        Environment.SetEnvironmentVariable("GOOGLE_REDIRECT_URI", "http://localhost:5000/auth/google/callback");

        builder.UseEnvironment(_environment);
        builder.ConfigureServices(services =>
        {
            // Replace the hosted bucket-bootstrap service so we don't need MinIO running.
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

            if (_oauthProviders is not null)
            {
                services.RemoveAll<IOAuthProvider>();
                foreach (var provider in _oauthProviders)
                {
                    services.AddSingleton(provider);
                }
            }
        });
    }
}
