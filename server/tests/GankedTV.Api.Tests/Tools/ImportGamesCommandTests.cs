using FluentAssertions;
using GankedTV.Api.Data;
using GankedTV.Api.Services.Igdb;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using GankedTV.Api.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GankedTV.Api.Tests.Tools;

[Collection("Postgres")]
public class ImportGamesCommandTests : IAsyncLifetime
{
    private static readonly string[] SeedSlugs =
    [
        "league-of-legends", "valorant", "cs2", "fortnite", "apex-legends",
        "rocket-league", "overwatch-2", "dota-2", "marvel-rivals",
    ];

    private readonly PostgresFixture _fx;

    public ImportGamesCommandTests(PostgresFixture fx) => _fx = fx;

    // The games table is preserved across Respawn resets, so any rows/metadata this suite
    // writes must be scrubbed back to the seed baseline both before and after each test.
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
            .SetProperty(g => g.IgdbId, (int?)null));
    }

    [Theory]
    [InlineData(new[] { "--import-games" }, true)]
    [InlineData(new[] { "--seed", "--import-games" }, true)]
    [InlineData(new[] { "--seed" }, false)]
    [InlineData(new string[0], false)]
    public void ShouldRun_DetectsFlag(string[] args, bool expected) =>
        ImportGamesCommand.ShouldRun(args).Should().Be(expected);

    [Fact]
    public async Task MissingCredentials_DoesNothing()
    {
        var igdb = Substitute.For<IIgdbMetadataService>();
        var storage = new InMemoryObjectStorage();
        await using var db = _fx.CreateContext();
        var before = await db.Games.CountAsync();

        await Build(db, igdb, storage, configured: false).RunAsync(CancellationToken.None);

        await igdb.DidNotReceive().GetPopularGamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        storage.PutCalls.Should().BeEmpty();
        (await db.Games.CountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task HappyPath_CreatesRowsAndMirrorsCovers()
    {
        var igdb = Substitute.For<IIgdbMetadataService>();
        igdb.GetPopularGamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<IgdbGame>
            {
                new(9001, "Hollow Test Knight", "imgA"),
                new(9002, "Celeste Test", "imgB"),
            });
        igdb.DownloadCoverAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => "JPEG"u8.ToArray());
        var storage = new InMemoryObjectStorage();

        await using var db = _fx.CreateContext();
        await Build(db, igdb, storage).RunAsync(CancellationToken.None);

        await using var verify = _fx.CreateContext();
        var knight = await verify.Games.SingleAsync(g => g.IgdbId == 9001);
        knight.Slug.Should().Be("hollow-test-knight");
        knight.Tag.Should().Be("HTK");
        knight.CoverUrl.Should().Be("http://minio:9000/game-covers/hollow-test-knight.jpg");

        storage.Objects.Keys.Should().Contain(("game-covers", "hollow-test-knight.jpg"));
        storage.Objects.Keys.Should().Contain(("game-covers", "celeste-test.jpg"));
        storage.EnsureBucketsCallCount.Should().Be(1);
    }

    [Fact]
    public async Task ReRun_IsIdempotent_SkipsAlreadyMirroredCovers()
    {
        var igdb = Substitute.For<IIgdbMetadataService>();
        igdb.GetPopularGamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<IgdbGame> { new(9001, "Hollow Test Knight", "imgA") });
        igdb.DownloadCoverAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => "JPEG"u8.ToArray());
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
        (await _fx.CreateContext().Games.CountAsync(g => g.IgdbId == 9001)).Should().Be(1);
    }

    [Fact]
    public async Task ReconcilesCuratedSeedByName_WithoutDuplicating()
    {
        var igdb = Substitute.For<IIgdbMetadataService>();
        igdb.GetPopularGamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<IgdbGame> { new(126459, "Valorant", "valImg") });
        igdb.DownloadCoverAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => "JPEG"u8.ToArray());
        var storage = new InMemoryObjectStorage();

        await using var db = _fx.CreateContext();
        await Build(db, igdb, storage).RunAsync(CancellationToken.None);

        await using var verify = _fx.CreateContext();
        var valorants = await verify.Games.Where(g => g.Name == "Valorant").ToListAsync();
        valorants.Should().ContainSingle("the curated seed is adopted, not duplicated");
        var valorant = valorants[0];
        valorant.Slug.Should().Be("valorant", "the hand-picked slug is preserved");
        valorant.IgdbId.Should().Be(126459);
        valorant.CoverUrl.Should().Be("http://minio:9000/game-covers/valorant.jpg");
        storage.Objects.Keys.Should().Contain(("game-covers", "valorant.jpg"));
    }

    [Fact]
    public async Task CoverDownloadFailure_KeepsGameRow_AndContinues()
    {
        var igdb = Substitute.For<IIgdbMetadataService>();
        igdb.GetPopularGamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<IgdbGame>
            {
                new(8001, "Flaky Cover Test", "imgFail"),
                new(8002, "Good Cover Test", "imgOk"),
            });
        igdb.DownloadCoverAsync("imgFail", Arg.Any<CancellationToken>())
            .Returns<byte[]?>(_ => throw new HttpRequestException("image CDN 503"));
        igdb.DownloadCoverAsync("imgOk", Arg.Any<CancellationToken>())
            .Returns(_ => "JPEG"u8.ToArray());
        var storage = new InMemoryObjectStorage();

        await using var db = _fx.CreateContext();
        await Build(db, igdb, storage).RunAsync(CancellationToken.None);

        await using var verify = _fx.CreateContext();
        // Both rows exist; the flaky one has no cover yet (re-runnable), the other is mirrored.
        var flaky = await verify.Games.SingleAsync(g => g.IgdbId == 8001);
        flaky.CoverUrl.Should().BeNull();
        var good = await verify.Games.SingleAsync(g => g.IgdbId == 8002);
        good.CoverUrl.Should().Be("http://minio:9000/game-covers/good-cover-test.jpg");
    }

    [Fact]
    public async Task DuplicateNames_GetDistinctSlugs()
    {
        var igdb = Substitute.For<IIgdbMetadataService>();
        igdb.GetPopularGamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<IgdbGame>
            {
                new(7001, "Twin Test", "img1"),
                new(7002, "Twin Test", "img2"),
            });
        igdb.DownloadCoverAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => "JPEG"u8.ToArray());
        var storage = new InMemoryObjectStorage();

        await using var db = _fx.CreateContext();
        await Build(db, igdb, storage).RunAsync(CancellationToken.None);

        await using var verify = _fx.CreateContext();
        var slugs = await verify.Games.Where(g => g.IgdbId == 7001 || g.IgdbId == 7002)
            .Select(g => g.Slug).ToListAsync();
        slugs.Should().HaveCount(2);
        slugs.Should().OnlyHaveUniqueItems();
        slugs.Should().Contain("twin-test");
        slugs.Should().Contain("twin-test-7002");
    }

    private ImportGamesCommand Build(
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
        return new ImportGamesCommand(db, igdb, storage, s3, igdbOpts, NullLogger<ImportGamesCommand>.Instance);
    }
}
