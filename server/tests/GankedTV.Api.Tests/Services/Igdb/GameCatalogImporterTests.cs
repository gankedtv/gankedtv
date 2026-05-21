using FluentAssertions;
using GankedTV.Api.Data;
using GankedTV.Api.Services.Igdb;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GankedTV.Api.Tests.Services.Igdb;

[Collection("Postgres")]
public class GameCatalogImporterTests : IAsyncLifetime
{
    private static readonly string[] SeedSlugs =
    [
        "league-of-legends", "valorant", "cs2", "fortnite", "apex-legends",
        "rocket-league", "overwatch-2", "dota-2", "marvel-rivals",
    ];

    private readonly PostgresFixture _fx;

    public GameCatalogImporterTests(PostgresFixture fx) => _fx = fx;

    // The games table is preserved across Respawn resets, so scrub any rows/metadata this suite
    // writes back to the seed baseline both before and after each test.
    public async Task InitializeAsync()
    {
        await _fx.ResetAsync();
        await RestoreGamesBaselineAsync();
    }

    public async Task DisposeAsync() => await RestoreGamesBaselineAsync();

    private async Task RestoreGamesBaselineAsync()
    {
        await using var db = _fx.CreateContext();
        await db.Games.Where(g => !SeedSlugs.Contains(g.Slug)).ExecuteDeleteAsync();
        await db.Games.ExecuteUpdateAsync(s => s
            .SetProperty(g => g.CoverUrl, (string?)null)
            .SetProperty(g => g.CoverImageId, (string?)null)
            .SetProperty(g => g.IgdbId, (int?)null)
            .SetProperty(g => g.IgdbManaged, false));
    }

    [Fact]
    public async Task MissingCredentials_DoesNothing()
    {
        var igdb = Substitute.For<IIgdbMetadataService>();
        var storage = new InMemoryObjectStorage();
        await using var db = _fx.CreateContext();
        var before = await db.Games.CountAsync();

        var result = await Build(db, igdb, storage, configured: false).RunAsync(CancellationToken.None);

        result.Should().Be(GameCatalogImportResult.Skipped);
        await igdb.DidNotReceive().GetPopularGamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        storage.PutCalls.Should().BeEmpty();
        (await db.Games.CountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task HappyPath_CreatesManagedRowsAndMirrorsCovers()
    {
        var igdb = StubIgdb(new IgdbGame(9001, "Hollow Test Knight", "imgA"), new IgdbGame(9002, "Celeste Test", "imgB"));
        var storage = new InMemoryObjectStorage();

        await using var db = _fx.CreateContext();
        var result = await Build(db, igdb, storage).RunAsync(CancellationToken.None);

        result.CoversMirrored.Should().Be(2);
        await using var verify = _fx.CreateContext();
        var knight = await verify.Games.SingleAsync(g => g.IgdbId == 9001);
        knight.Slug.Should().Be("hollow-test-knight");
        knight.Tag.Should().Be("HTK");
        knight.IgdbManaged.Should().BeTrue();
        knight.CoverImageId.Should().Be("imgA");
        knight.CoverUrl.Should().Be("http://minio:9000/game-covers/hollow-test-knight.jpg");
        storage.Objects.Keys.Should().Contain(("game-covers", "hollow-test-knight.jpg"));
    }

    [Fact]
    public async Task ReRun_SkipsCover_WhenImageIdUnchanged()
    {
        var igdb = StubIgdb(new IgdbGame(9001, "Hollow Test Knight", "imgA"));
        var storage = new InMemoryObjectStorage();

        await using (var db = _fx.CreateContext())
        {
            await Build(db, igdb, storage).RunAsync(CancellationToken.None);
        }
        await using (var db = _fx.CreateContext())
        {
            await Build(db, igdb, storage).RunAsync(CancellationToken.None);
        }

        await igdb.Received(1).DownloadCoverAsync("imgA", Arg.Any<CancellationToken>());
        storage.PutCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task ReRun_ReDownloadsCover_WhenImageIdChanged()
    {
        var storage = new InMemoryObjectStorage();
        var igdb = Substitute.For<IIgdbMetadataService>();
        igdb.DownloadCoverAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => "JPEG"u8.ToArray());

        igdb.GetPopularGamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<IgdbGame> { new(9001, "Drift Test", "old") });
        await using (var db = _fx.CreateContext())
        {
            await Build(db, igdb, storage).RunAsync(CancellationToken.None);
        }

        // IGDB now reports a different cover image for the same game.
        igdb.GetPopularGamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<IgdbGame> { new(9001, "Drift Test", "new") });
        await using (var db = _fx.CreateContext())
        {
            var result = await Build(db, igdb, storage).RunAsync(CancellationToken.None);
            result.CoversMirrored.Should().Be(1);
        }

        await igdb.Received(1).DownloadCoverAsync("old", Arg.Any<CancellationToken>());
        await igdb.Received(1).DownloadCoverAsync("new", Arg.Any<CancellationToken>());
        await using var verify = _fx.CreateContext();
        (await verify.Games.SingleAsync(g => g.IgdbId == 9001)).CoverImageId.Should().Be("new");
    }

    [Fact]
    public async Task ReconcilesCuratedSeedByName_WithoutDuplicating_AndDoesNotMarkManaged()
    {
        var igdb = StubIgdb(new IgdbGame(126459, "Valorant", "valImg"));
        var storage = new InMemoryObjectStorage();

        await using var db = _fx.CreateContext();
        await Build(db, igdb, storage).RunAsync(CancellationToken.None);

        await using var verify = _fx.CreateContext();
        var valorants = await verify.Games.Where(g => g.Name == "Valorant").ToListAsync();
        valorants.Should().ContainSingle("the curated seed is adopted, not duplicated");
        valorants[0].Slug.Should().Be("valorant", "the hand-picked slug is preserved");
        valorants[0].IgdbId.Should().Be(126459);
        valorants[0].IgdbManaged.Should().BeFalse("adopted seeds stay curated/non-managed");
        valorants[0].CoverUrl.Should().Be("http://minio:9000/game-covers/valorant.jpg");
        valorants[0].CoverImageId.Should().Be("valImg");
    }

    [Fact]
    public async Task NameRefresh_AppliesToManagedRows_ButNotCuratedSeeds()
    {
        var storage = new InMemoryObjectStorage();
        var igdb = Substitute.For<IIgdbMetadataService>();
        igdb.DownloadCoverAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => "JPEG"u8.ToArray());

        // Run 1: create a managed game + adopt the Valorant seed.
        igdb.GetPopularGamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<IgdbGame> { new(9001, "Old Name", "a"), new(126459, "Valorant", "v") });
        await using (var db = _fx.CreateContext())
        {
            await Build(db, igdb, storage).RunAsync(CancellationToken.None);
        }

        // Run 2: IGDB renamed both. The managed row follows; the curated seed does not.
        igdb.GetPopularGamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<IgdbGame> { new(9001, "New Name", "a"), new(126459, "VALORANT RENAMED", "v") });
        await using (var db = _fx.CreateContext())
        {
            var result = await Build(db, igdb, storage).RunAsync(CancellationToken.None);
            result.Renamed.Should().Be(1);
        }

        await using var verify = _fx.CreateContext();
        (await verify.Games.SingleAsync(g => g.IgdbId == 9001)).Name.Should().Be("New Name");
        (await verify.Games.SingleAsync(g => g.IgdbId == 126459)).Name
            .Should().Be("Valorant", "curated seed names are never overwritten by IGDB");
    }

    [Fact]
    public async Task AdoptingSeed_ReplacesPlaceholderCover_WithRealArt()
    {
        // Dev state: the seeded Valorant row has a placeholder cover (cover_url set,
        // cover_image_id null). Adoption must download real art because the image_id differs.
        const string keyName = "valorant.jpg";
        var storage = new InMemoryObjectStorage();
        storage.Objects[("game-covers", keyName)] = "PLACEHOLDER"u8.ToArray();
        await using (var setup = _fx.CreateContext())
        {
            await setup.Games.Where(g => g.Slug == "valorant").ExecuteUpdateAsync(s =>
                s.SetProperty(g => g.CoverUrl, "http://minio:9000/game-covers/valorant.jpg"));
        }

        var igdb = Substitute.For<IIgdbMetadataService>();
        igdb.GetPopularGamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<IgdbGame> { new(126459, "Valorant", "realImg") });
        igdb.DownloadCoverAsync("realImg", Arg.Any<CancellationToken>()).Returns(_ => "REAL_ART_BYTES"u8.ToArray());

        await using var db = _fx.CreateContext();
        await Build(db, igdb, storage).RunAsync(CancellationToken.None);

        await igdb.Received(1).DownloadCoverAsync("realImg", Arg.Any<CancellationToken>());
        System.Text.Encoding.UTF8.GetString(storage.Objects[("game-covers", keyName)])
            .Should().Be("REAL_ART_BYTES");
        (await db.Games.SingleAsync(g => g.Slug == "valorant")).CoverImageId.Should().Be("realImg");
    }

    [Fact]
    public async Task CoverDownloadFailure_KeepsGameRow_AndContinues()
    {
        var igdb = Substitute.For<IIgdbMetadataService>();
        igdb.GetPopularGamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<IgdbGame> { new(8001, "Flaky Cover Test", "imgFail"), new(8002, "Good Cover Test", "imgOk") });
        igdb.DownloadCoverAsync("imgFail", Arg.Any<CancellationToken>())
            .Returns<byte[]?>(_ => throw new HttpRequestException("image CDN 503"));
        igdb.DownloadCoverAsync("imgOk", Arg.Any<CancellationToken>()).Returns(_ => "JPEG"u8.ToArray());
        var storage = new InMemoryObjectStorage();

        await using var db = _fx.CreateContext();
        await Build(db, igdb, storage).RunAsync(CancellationToken.None);

        await using var verify = _fx.CreateContext();
        var flaky = await verify.Games.SingleAsync(g => g.IgdbId == 8001);
        flaky.CoverUrl.Should().BeNull();
        flaky.CoverImageId.Should().BeNull("a failed download must not record the image_id, so it retries next run");
        (await verify.Games.SingleAsync(g => g.IgdbId == 8002)).CoverUrl
            .Should().Be("http://minio:9000/game-covers/good-cover-test.jpg");
    }

    [Fact]
    public async Task DuplicateNames_GetDistinctSlugs()
    {
        var igdb = StubIgdb(new IgdbGame(7001, "Twin Test", "img1"), new IgdbGame(7002, "Twin Test", "img2"));
        var storage = new InMemoryObjectStorage();

        await using var db = _fx.CreateContext();
        await Build(db, igdb, storage).RunAsync(CancellationToken.None);

        await using var verify = _fx.CreateContext();
        var slugs = await verify.Games.Where(g => g.IgdbId == 7001 || g.IgdbId == 7002)
            .Select(g => g.Slug).ToListAsync();
        slugs.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        slugs.Should().Contain("twin-test").And.Contain("twin-test-7002");
    }

    private static IIgdbMetadataService StubIgdb(params IgdbGame[] games)
    {
        var igdb = Substitute.For<IIgdbMetadataService>();
        igdb.GetPopularGamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(games.ToList());
        igdb.DownloadCoverAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => "JPEG"u8.ToArray());
        return igdb;
    }

    private static GameCatalogImporter Build(
        GankedTvDbContext db,
        IIgdbMetadataService igdb,
        IObjectStorageService storage,
        bool configured = true)
    {
        var s3 = Options.Create(new S3Options { Endpoint = "http://minio:9000", GameCoversBucket = "game-covers" });
        var igdbOpts = Options.Create(new IgdbOptions
        {
            ClientId = configured ? "cid" : "",
            ClientSecret = configured ? "secret" : "",
            PopularImportCount = 50,
        });
        return new GameCatalogImporter(db, igdb, storage, s3, igdbOpts, NullLogger<GameCatalogImporter>.Instance);
    }
}
