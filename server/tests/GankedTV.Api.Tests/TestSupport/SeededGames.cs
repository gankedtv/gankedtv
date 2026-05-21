using GankedTV.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Tests.TestSupport;

/// <summary>
/// Shared helper for suites that mutate game rows. PostgresFixture preserves the games table
/// across Respawn resets, so these tests must scrub back to the seeded baseline themselves.
/// </summary>
internal static class SeededGames
{
    /// <summary>The slugs seeded via <c>HasData</c> in GankedTvDbContext.</summary>
    public static readonly string[] Slugs =
    [
        "league-of-legends", "valorant", "cs2", "fortnite", "apex-legends",
        "rocket-league", "overwatch-2", "dota-2", "marvel-rivals",
    ];

    /// <summary>Deletes non-seed rows and clears IGDB-derived metadata back to the clean baseline.</summary>
    public static async Task ResetBaselineAsync(GankedTvDbContext db)
    {
        await db.Games.Where(g => !Slugs.Contains(g.Slug)).ExecuteDeleteAsync();
        await db.Games.ExecuteUpdateAsync(s => s
            .SetProperty(g => g.CoverUrl, (string?)null)
            .SetProperty(g => g.CoverImageId, (string?)null)
            .SetProperty(g => g.IgdbId, (int?)null)
            .SetProperty(g => g.IgdbManaged, false));
    }
}
