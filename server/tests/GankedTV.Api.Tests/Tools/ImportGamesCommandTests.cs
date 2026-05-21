using FluentAssertions;
using GankedTV.Api.Services.Igdb;
using GankedTV.Api.Tools;
using NSubstitute;

namespace GankedTV.Api.Tests.Tools;

public class ImportGamesCommandTests
{
    [Theory]
    [InlineData(new[] { "--import-games" }, true)]
    [InlineData(new[] { "--seed", "--import-games" }, true)]
    [InlineData(new[] { "--seed" }, false)]
    [InlineData(new string[0], false)]
    public void ShouldRun_DetectsFlag(string[] args, bool expected) =>
        ImportGamesCommand.ShouldRun(args).Should().Be(expected);

    [Fact]
    public async Task RunAsync_DelegatesToImporter()
    {
        var importer = Substitute.For<IGameCatalogImporter>();
        importer.RunAsync(Arg.Any<CancellationToken>()).Returns(new GameCatalogImportResult(1, 1, 0));

        await new ImportGamesCommand(importer).RunAsync(CancellationToken.None);

        await importer.Received(1).RunAsync(Arg.Any<CancellationToken>());
    }
}
