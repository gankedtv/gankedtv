using System.Net.Http.Headers;
using GankedTV.Api.Auth.Jwt;
using GankedTV.Api.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace GankedTV.Api.Tests.TestSupport;

// Shared helpers for integration tests that need an authenticated user. Each test class
// keeps a thin one-line wrapper that fixes the file-local default username; the wrapper
// delegates here for the actual seeding + token-issue body.
internal static class AuthTestHelpers
{
    // `configure` lets a caller set additional User fields (Bio, AvatarUrl, ...) before
    // the row is persisted. Most tests don't need it — the parameter is optional.
    public static async Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(
        PostgresFixture pg,
        AuthApiFactory factory,
        string username = "owner",
        Action<User>? configure = null)
    {
        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            Username = username,
            Email = $"{username}@example.com",
            CreatedAt = now,
            UpdatedAt = now,
        };
        configure?.Invoke(user);

        Guid id;
        await using (var db = pg.CreateContext())
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();
            id = user.Id;
        }

        using var scope = factory.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<IJwtService>();
        // Issue the token from the persisted entity, not a fresh struct rebuilt from local
        // vars — `configure` could have mutated Username / Email, and the JWT claims must
        // reflect the row that's actually in the DB.
        var token = jwt.Issue(user);
        return (id, token);
    }

    public static HttpClient CreateBearerClient(AuthApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
