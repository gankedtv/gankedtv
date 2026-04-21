using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Problems;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace GankedTV.Api.Tests.Problems;

public class ProblemResultsTests
{
    public static TheoryData<Func<IResult>, int, string> Cases => new()
    {
        { () => ProblemResults.BadRequest("bad"), StatusCodes.Status400BadRequest, "bad" },
        { () => ProblemResults.Unauthorized("no"), StatusCodes.Status401Unauthorized, "no" },
        { () => ProblemResults.Forbidden("nope"), StatusCodes.Status403Forbidden, "nope" },
        { () => ProblemResults.NotFound("missing"), StatusCodes.Status404NotFound, "missing" },
        { () => ProblemResults.Conflict("conflict"), StatusCodes.Status409Conflict, "conflict" },
        { () => ProblemResults.UnsupportedMediaType("bad_ct"), StatusCodes.Status415UnsupportedMediaType, "bad_ct" },
        { () => ProblemResults.Internal("boom"), StatusCodes.Status500InternalServerError, "boom" },
    };

    [Theory, MemberData(nameof(Cases))]
    public async Task Factory_ProducesExpectedStatusAndCode(Func<IResult> build, int expectedStatus, string expectedCode)
    {
        var (status, body) = await ExecuteAsync(build());

        status.Should().Be(expectedStatus);
        body.RootElement.GetProperty("status").GetInt32().Should().Be(expectedStatus);
        body.RootElement.GetProperty(ProblemResults.CodeKey).GetString().Should().Be(expectedCode);
    }

    [Fact]
    public async Task Detail_IsIncludedWhenProvided()
    {
        var (_, body) = await ExecuteAsync(ProblemResults.BadRequest("bad", "why"));
        body.RootElement.GetProperty("detail").GetString().Should().Be("why");
    }

    private static async Task<(int status, JsonDocument body)> ExecuteAsync(IResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        var sp = services.BuildServiceProvider();

        var ctx = new DefaultHttpContext { RequestServices = sp };
        using var ms = new MemoryStream();
        ctx.Response.Body = ms;

        await result.ExecuteAsync(ctx);

        ms.Position = 0;
        var body = await JsonDocument.ParseAsync(ms);
        return (ctx.Response.StatusCode, body);
    }
}
