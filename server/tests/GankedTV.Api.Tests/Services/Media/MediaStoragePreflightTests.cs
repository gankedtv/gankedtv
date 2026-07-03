using System.Net;
using System.Security.Authentication;
using FluentAssertions;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GankedTV.Api.Tests.Services.Media;

public class MediaStoragePreflightTests
{
    private static IOptionsMonitor<S3Options> Options(string clipsBucket = "clips")
    {
        var monitor = Substitute.For<IOptionsMonitor<S3Options>>();
        monitor.CurrentValue.Returns(new S3Options { ClipsBucket = clipsBucket });
        return monitor;
    }

    private static IObjectStorageService Storage(string url = "http://storage.internal/clips/probe")
    {
        var storage = Substitute.For<IObjectStorageService>();
        storage.GetPresignedGetUrlForWorker(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns(url);
        return storage;
    }

    private static MediaStoragePreflight Build(HttpMessageHandler handler, IObjectStorageService? storage = null, IOptionsMonitor<S3Options>? options = null) =>
        new(new HttpClient(handler), storage ?? Storage(), options ?? Options());

    [Fact]
    public async Task CheckAsync_ResponseReceived_IsReachable()
    {
        // A 404 for the non-existent probe key still proves the endpoint is reachable + TLS trusted.
        var result = await Build(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))
            .CheckAsync(CancellationToken.None);

        result.Status.Should().Be(StorageReachability.Reachable);
    }

    [Fact]
    public async Task CheckAsync_CertificateFailure_IsTlsUntrusted()
    {
        var tls = new HttpRequestException(
            "The SSL connection could not be established.",
            new AuthenticationException("certificate verify failed"));

        var result = await Build(new StubHandler(_ => throw tls))
            .CheckAsync(CancellationToken.None);

        result.Status.Should().Be(StorageReachability.TlsUntrusted);
    }

    [Fact]
    public async Task CheckAsync_ConnectionError_IsUnreachable()
    {
        var refused = new HttpRequestException("Connection refused");

        var result = await Build(new StubHandler(_ => throw refused))
            .CheckAsync(CancellationToken.None);

        result.Status.Should().Be(StorageReachability.Unreachable);
    }

    [Fact]
    public async Task CheckAsync_NoClipsBucket_IsMisconfigured()
    {
        var result = await Build(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), options: Options(clipsBucket: ""))
            .CheckAsync(CancellationToken.None);

        result.Status.Should().Be(StorageReachability.Misconfigured);
    }

    [Fact]
    public async Task CheckAsync_UnparseablePresignedUrl_IsMisconfigured()
    {
        var result = await Build(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), storage: Storage("not-a-url"))
            .CheckAsync(CancellationToken.None);

        result.Status.Should().Be(StorageReachability.Misconfigured);
    }

    [Fact]
    public void Report_TlsUntrusted_LogsCriticalWithRemediation()
    {
        var logger = new ListLogger();
        MediaStoragePreflightLog.Report(logger, "thumbnail",
            new StoragePreflightResult(StorageReachability.TlsUntrusted, "certificate verify failed"));

        logger.Entries.Should().ContainSingle();
        var (level, message) = logger.Entries[0];
        level.Should().Be(LogLevel.Critical);
        message.Should().Contain("TLS verification FAILED");
        message.Should().Contain("S3_INTERNAL_ENDPOINT");
    }

    [Theory]
    [InlineData(StorageReachability.Reachable, LogLevel.Information)]
    [InlineData(StorageReachability.Unreachable, LogLevel.Error)]
    [InlineData(StorageReachability.Misconfigured, LogLevel.Error)]
    public void Report_OtherOutcomes_LogAtExpectedSeverity(StorageReachability status, LogLevel expected)
    {
        var logger = new ListLogger();
        MediaStoragePreflightLog.Report(logger, "jit", new StoragePreflightResult(status, "detail"));

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(expected);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
