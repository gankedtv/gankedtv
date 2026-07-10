using FluentAssertions;
using GankedTV.Api.Services.Igdb;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GankedTV.Api.Tests.Services.Igdb;

public class GameSearchImportServiceTests
{
    private readonly IIgdbMetadataService _igdb = Substitute.For<IIgdbMetadataService>();
    private readonly IGameCatalogImporter _importer = Substitute.For<IGameCatalogImporter>();
    private readonly MemoryCache _memo = new(new MemoryCacheOptions());

    private GameSearchImportService Build(bool configured = true)
    {
        var opts = Options.Create(new IgdbOptions
        {
            ClientId = configured ? "cid" : "",
            ClientSecret = configured ? "secret" : "",
        });
        return new GameSearchImportService(
            _igdb, _importer, opts, _memo, NullLogger<GameSearchImportService>.Instance);
    }

    [Fact]
    public async Task MissingCredentials_ReturnsFalse_WithoutCallingIgdb()
    {
        var imported = await Build(configured: false).TryImportMatchesAsync("satisfactory");

        imported.Should().BeFalse();
        await _igdb.DidNotReceive().SearchGamesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TooShortTerm_ReturnsFalse_WithoutCallingIgdb()
    {
        var imported = await Build().TryImportMatchesAsync(" s ");

        imported.Should().BeFalse();
        await _igdb.DidNotReceive().SearchGamesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoMatches_ReturnsFalse_WithoutImporting()
    {
        _igdb.SearchGamesAsync("satisfactory", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var imported = await Build().TryImportMatchesAsync("satisfactory");

        imported.Should().BeFalse();
        await _importer.DidNotReceive().ImportAsync(Arg.Any<IReadOnlyList<IgdbGame>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Matches_ImportsThem_AndReturnsTrue()
    {
        var matches = new[] { new IgdbGame(100, "Satisfactory", "sat1") };
        _igdb.SearchGamesAsync("satisfactory", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(matches);
        _importer.ImportAsync(matches, Arg.Any<CancellationToken>())
            .Returns(new GameCatalogImportResult(1, 1, 0));

        var imported = await Build().TryImportMatchesAsync("  Satisfactory ");

        imported.Should().BeTrue();
        await _importer.Received(1).ImportAsync(matches, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepeatedTerm_IsMemoized_SoIgdbIsHitOnce()
    {
        _igdb.SearchGamesAsync("satisfactory", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var svc = Build();

        await svc.TryImportMatchesAsync("satisfactory");
        await svc.TryImportMatchesAsync("SATISFACTORY");

        await _igdb.Received(1).SearchGamesAsync("satisfactory", Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IgdbFailure_ReturnsFalse_InsteadOfThrowing()
    {
        _igdb.SearchGamesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("igdb down"));

        var imported = await Build().TryImportMatchesAsync("satisfactory");

        imported.Should().BeFalse();
    }
}
