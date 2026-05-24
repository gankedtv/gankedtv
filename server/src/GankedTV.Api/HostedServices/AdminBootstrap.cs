using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.HostedServices;

// One-shot promotion of bootstrap-admin emails. Reads ADMIN_EMAILS at startup and elevates
// any existing user matching one of those emails (case-insensitive) to role=admin. Idempotent
// — re-runs each startup but the WHERE clause excludes already-admin rows so the UPDATE is a
// no-op once everyone is promoted. New users matching the list still have to register/login
// once before the bootstrap kicks in on the next start; that's fine for a seed mechanism.
public sealed class AdminBootstrap(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<AdminBootstrap> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var raw = Environment.GetEnvironmentVariable("ADMIN_EMAILS") ?? config["AdminEmails"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }
        var emails = ParseEmails(raw);
        if (emails.Count == 0)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GankedTvDbContext>();

        // LOWER(email) match: emails compared case-insensitively per RFC 5321 §2.4; we store
        // the user-supplied case but match against the normalized form.
        var updated = await db.Users
            .Where(u => u.Email != null && emails.Contains(u.Email!.ToLower()))
            .Where(u => u.Role != UserRoles.Admin)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRoles.Admin), cancellationToken);

        if (updated > 0)
        {
            logger.LogInformation(
                "AdminBootstrap: promoted {Count} user(s) to admin from ADMIN_EMAILS.",
                updated);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static HashSet<string> ParseEmails(string raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(part.ToLowerInvariant());
        }
        return set;
    }
}
