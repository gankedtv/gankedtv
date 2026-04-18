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

    public AuthApiFactory(string connectionString, IObjectStorageService? storageOverride = null)
    {
        _connectionString = connectionString;
        _storageOverride = storageOverride;
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

        builder.UseEnvironment("Development");
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
        });
    }
}
