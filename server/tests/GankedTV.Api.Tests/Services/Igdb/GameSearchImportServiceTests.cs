using FluentAssertions;
using GankedTV.Api.Services.Igdb;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GankedTV.Api.Tests.Services.Igdb;

public class GameSearchImportServiceTests
{
    private readonly IIgdbMetadataService _igdb = Substitute.For<IIgdbMetadataService>();
    private readonly IGameCatalogImporter _importer = Substitute.For<IGameCatalogImporter>();
    private readonly FakeClock _clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly GameSearchMemo _memo;

    public GameSearchImportServiceTests() => _memo = new GameSearchMemo(_clock);

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
        var imported = await Build().TryImportMatchesAsync(" cs ");

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
        await _importer.DidNotReceive().ImportAsync(Arg.Any<IReadOnlyList<IgdbGame>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Matches_ImportsThem_AndReturnsTrue()
    {
        _igdb.SearchGamesAsync("satisfactory", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new IgdbGame(100, "Satisfactory", "sat1")]);
        _importer.ImportAsync(Arg.Any<IReadOnlyList<IgdbGame>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new GameCatalogImportResult(1, 1, 1, 0));

        var imported = await Build().TryImportMatchesAsync("  Satisfactory ");

        imported.Should().BeTrue();
        await _importer.Received(1).ImportAsync(
            Arg.Is<IReadOnlyList<IgdbGame>>(g => g.Single().Id == 100), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FuzzyMatchesNotContainingTerm_AreNotImported()
    {
        // IGDB's search is fuzzy, so a term pulls in titles the retried local ILIKE can't find.
        // Importing those would mint permanent rows (+ mirrored covers) for nothing.
        _igdb.SearchGamesAsync("satisfactory", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([
                new IgdbGame(100, "Satisfactory", "sat1"),
                new IgdbGame(101, "Factorio", "fac1"),
            ]);
        _importer.ImportAsync(Arg.Any<IReadOnlyList<IgdbGame>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new GameCatalogImportResult(1, 1, 1, 0));

        var imported = await Build().TryImportMatchesAsync("satisfactory");

        imported.Should().BeTrue();
        await _importer.Received(1).ImportAsync(
            Arg.Is<IReadOnlyList<IgdbGame>>(g => g.Count == 1 && g[0].Id == 100), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllMatchesFuzzy_ReturnsFalse_WithoutImporting()
    {
        _igdb.SearchGamesAsync("satisfactory", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new IgdbGame(101, "Factorio", "fac1")]);

        var imported = await Build().TryImportMatchesAsync("satisfactory");

        imported.Should().BeFalse();
        await _importer.DidNotReceive().ImportAsync(Arg.Any<IReadOnlyList<IgdbGame>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GamesAlreadyInCatalog_ReturnFalse_SoTheCallerSkipsAPointlessRetry()
    {
        _igdb.SearchGamesAsync("satisfactory", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new IgdbGame(100, "Satisfactory", "sat1")]);
        // Processed counts every input game; nothing was created or renamed, so a retried local
        // query returns the same miss.
        _importer.ImportAsync(Arg.Any<IReadOnlyList<IgdbGame>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new GameCatalogImportResult(1, 0, 0, 0));

        var imported = await Build().TryImportMatchesAsync("satisfactory");

        imported.Should().BeFalse();
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

    [Fact]
    public async Task IgdbTimeout_ReturnsFalse_InsteadOfBubblingOut()
    {
        // HttpClient.Timeout throws TaskCanceledException — an OperationCanceledException with the
        // caller's token still alive. Rethrowing it turns a slow IGDB into a 500 on the picker.
        _igdb.SearchGamesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("igdb timed out"));

        var imported = await Build().TryImportMatchesAsync("satisfactory", CancellationToken.None);

        imported.Should().BeFalse();
    }

    [Fact]
    public async Task CallerCancellation_Rethrows_AndDoesNotMemoizeTheTerm()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _igdb.SearchGamesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        var svc = Build();

        var act = () => svc.TryImportMatchesAsync("satisfactory", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        _igdb.ClearReceivedCalls();
        _igdb.SearchGamesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        await svc.TryImportMatchesAsync("satisfactory");

        await _igdb.Received(1).SearchGamesAsync("satisfactory", Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransientFailure_CoolsDownBriefly_ThenRetriesIgdb()
    {
        _igdb.SearchGamesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("igdb 503"));
        var svc = Build();
        var start = _clock.GetUtcNow();

        await svc.TryImportMatchesAsync("satisfactory");

        // Inside the cooldown the term stays memoized…
        _clock.Set(start.AddSeconds(30));
        await svc.TryImportMatchesAsync("satisfactory");
        await _igdb.Received(1).SearchGamesAsync("satisfactory", Arg.Any<int>(), Arg.Any<CancellationToken>());

        // …but a transient failure must not blackhole the term for the full success TTL.
        _clock.Set(start.AddSeconds(61));
        await svc.TryImportMatchesAsync("satisfactory");
        await _igdb.Received(2).SearchGamesAsync("satisfactory", Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
