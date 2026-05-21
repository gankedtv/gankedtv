using System.Net;

namespace GankedTV.Api.Tests.TestSupport;

public sealed class TestHttpMessageHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = new();

    public List<(string ContentType, string Body)> CapturedBodies { get; } = new();

    private readonly List<Func<HttpRequestMessage, HttpResponseMessage?>> _handlers = new();

    public TestHttpMessageHandler OnPost(string url, HttpStatusCode status, string responseJson)
    {
        _handlers.Add(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.ToString().StartsWith(url, StringComparison.Ordinal) == true)
            {
                return Build(status, responseJson);
            }
            return null;
        });
        return this;
    }

    public TestHttpMessageHandler OnGet(string url, HttpStatusCode status, string responseJson)
    {
        _handlers.Add(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.ToString().StartsWith(url, StringComparison.Ordinal) == true)
            {
                return Build(status, responseJson);
            }
            return null;
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var contentType = request.Content.Headers.ContentType?.MediaType ?? "";
            CapturedBodies.Add((contentType, body));
        }

        foreach (var handler in _handlers)
        {
            var response = handler(request);
            if (response is not null)
            {
                return response;
            }
        }
        return Build(HttpStatusCode.NotImplemented, "{\"error\":\"no_matching_handler\"}");
    }

    private static HttpResponseMessage Build(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };
}

public static class FakeHttpClientFactory
{
    // Accepts any HttpMessageHandler (not just TestHttpMessageHandler) so stateful test handlers
    // can reuse it. disposeHandler:false — the test owns the handler's lifetime.
    public static IHttpClientFactory Create(HttpMessageHandler handler) =>
        new SingleClientFactory(() => new HttpClient(handler, disposeHandler: false));

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly Func<HttpClient> _factory;
        public SingleClientFactory(Func<HttpClient> factory) => _factory = factory;
        public HttpClient CreateClient(string name) => _factory();
    }
}
