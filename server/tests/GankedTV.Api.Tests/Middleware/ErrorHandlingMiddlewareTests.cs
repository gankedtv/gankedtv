using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GankedTV.Api.Tests.Middleware;

public class ErrorHandlingMiddlewareTests
{
    [Fact]
    public async Task NoException_PassesThroughUnchanged()
    {
        var middleware = new ErrorHandlingMiddleware(NullLogger<ErrorHandlingMiddleware>.Instance);
        var ctx = BuildContext(out var body);

        await middleware.InvokeAsync(ctx, next: c =>
        {
            c.Response.StatusCode = 204;
            return Task.CompletedTask;
        });

        ctx.Response.StatusCode.Should().Be(204);
        body.Length.Should().Be(0);
    }

    [Fact]
    public async Task UnhandledException_Returns500ProblemJson()
    {
        var middleware = new ErrorHandlingMiddleware(NullLogger<ErrorHandlingMiddleware>.Instance);
        var ctx = BuildContext(out var body);

        await middleware.InvokeAsync(ctx, _ => throw new InvalidOperationException("kaboom"));

        ctx.Response.StatusCode.Should().Be(500);
        ctx.Response.ContentType.Should().StartWith("application/problem+json");

        body.Position = 0;
        var doc = await JsonDocument.ParseAsync(body);
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(500);
        doc.RootElement.GetProperty("code").GetString().Should().Be("internal_error");
        doc.RootElement.TryGetProperty("stackTrace", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("exception", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ExceptionAfterResponseStarted_Rethrows()
    {
        // Once the response has begun streaming, the best the middleware can do is log;
        // rewriting is impossible and swallowing would mask the fault. Contract: rethrow.
        var middleware = new ErrorHandlingMiddleware(NullLogger<ErrorHandlingMiddleware>.Instance);
        var ctx = BuildContext(out _);
        // DefaultHttpContext's response feature doesn't track HasStarted from a MemoryStream
        // write, so swap in a feature that reports HasStarted=true to exercise this branch.
        ctx.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());

        var act = async () => await middleware.InvokeAsync(ctx, _ =>
            throw new InvalidOperationException("late"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("late");
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }

    [Fact]
    public async Task ExceptionLogged_AtErrorLevel()
    {
        var logger = new CollectingLogger();
        var middleware = new ErrorHandlingMiddleware(logger);
        var ctx = BuildContext(out _);

        await middleware.InvokeAsync(ctx, _ => throw new InvalidOperationException("kaboom"));

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error);
    }

    private static DefaultHttpContext BuildContext(out MemoryStream body)
    {
        body = new MemoryStream();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = body },
            Request = { Method = "GET", Path = "/test" },
        };
    }

    private sealed class CollectingLogger : ILogger<ErrorHandlingMiddleware>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
