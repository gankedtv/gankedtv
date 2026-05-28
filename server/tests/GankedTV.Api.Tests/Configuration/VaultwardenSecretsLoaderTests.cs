using System.Net;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Configuration;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GankedTV.Api.Tests.Configuration;

public class VaultwardenSecretsLoaderTests
{
    private const string ApiUrl = "https://vault.test";
    private const string ApiKey = "test-api-key";
    private const string SecretPrefix = "https://vault.test/secret/";

    private static VaultwardenSecretsLoader Build(
        HttpMessageHandler handler,
        string collection = "Secrets - DEV")
    {
        var options = new VaultwardenOptions { ApiUrl = ApiUrl, ApiKey = ApiKey, Organization = "GankedTV" };
        return new VaultwardenSecretsLoader(
            new HttpClient(handler, disposeHandler: false),
            options,
            collection,
            NullLogger<VaultwardenSecretsLoader>.Instance);
    }

    // A fake env backing store so LoadAsync never touches the real process environment.
    private static (Func<string, string?> Get, Action<string, string> Set, Dictionary<string, string?> Store) FakeEnv(
        IDictionary<string, string?>? seed = null)
    {
        var store = seed is null
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(seed, StringComparer.OrdinalIgnoreCase);
        return (k => store.TryGetValue(k, out var v) ? v : null, (k, v) => store[k] = v, store);
    }

    [Fact]
    public void Manifest_ContainsRequiredKeys_AndNoSentryDsn()
    {
        VaultwardenSecretsLoader.Manifest.Should().HaveCount(18);
        VaultwardenSecretsLoader.Manifest.Should().Contain(["DATABASE_URL", "JWT_SECRET", "S3_SECRET_KEY", "CORS_ORIGINS"]);
        VaultwardenSecretsLoader.Manifest.Should().NotContain("SENTRY_DSN");
    }

    [Fact]
    public void Constructor_RejectsBlankBootstrapVars()
    {
        using var http = new HttpClient();
        var act = () => new VaultwardenSecretsLoader(
            http,
            new VaultwardenOptions { ApiUrl = "", ApiKey = "" },
            "Secrets - DEV",
            NullLogger<VaultwardenSecretsLoader>.Instance);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task FetchSecretAsync_ParsesValue_AndScopesRequestByOrgAndCollection()
    {
        var handler = new TestHttpMessageHandler()
            .OnGet(SecretPrefix, HttpStatusCode.OK, """{"name":"DATABASE_URL","value":"postgres://x"}""");

        var value = await Build(handler).FetchSecretAsync("DATABASE_URL", CancellationToken.None);

        value.Should().Be("postgres://x");
        var uri = handler.Requests.Should().ContainSingle().Subject.RequestUri!.AbsoluteUri;
        uri.Should().Contain("/secret/DATABASE_URL")
            .And.Contain("organization_name=GankedTV")
            .And.Contain("collection_name=Secrets%20-%20DEV");
        var auth = handler.Requests[0].Headers.Authorization!;
        auth.Scheme.Should().Be("Bearer");
        auth.Parameter.Should().Be(ApiKey);
    }

    [Fact]
    public async Task FetchSecretAsync_404_ReturnsNull()
    {
        var handler = new TestHttpMessageHandler()
            .OnGet(SecretPrefix, HttpStatusCode.NotFound, """{"error":"secret not found"}""");

        (await Build(handler).FetchSecretAsync("MISSING", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task FetchSecretAsync_NonSuccess_Throws()
    {
        var handler = new TestHttpMessageHandler()
            .OnGet(SecretPrefix, HttpStatusCode.InternalServerError, """{"error":"boom"}""");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Build(handler).FetchSecretAsync("DATABASE_URL", CancellationToken.None));
    }

    [Fact]
    public async Task FetchSecretAsync_MalformedJson_Throws()
    {
        var handler = new TestHttpMessageHandler()
            .OnGet(SecretPrefix, HttpStatusCode.OK, "<html>not json</html>");

        await Assert.ThrowsAsync<JsonException>(
            () => Build(handler).FetchSecretAsync("DATABASE_URL", CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_FillsUnsetKeys_AndSkipsAlreadySetWithoutRequest()
    {
        var handler = new TestHttpMessageHandler()
            .OnGet(SecretPrefix, HttpStatusCode.OK, """{"name":"x","value":"from-vault"}""");
        // JWT_SECRET is already set (env wins); DATABASE_URL is not.
        var (get, set, store) = FakeEnv(new Dictionary<string, string?> { ["JWT_SECRET"] = "from-env" });

        var applied = await Build(handler).LoadAsync(
            failFast: false, get, set, manifest: ["JWT_SECRET", "DATABASE_URL"]);

        applied.Should().ContainSingle().Which.Should().Be("DATABASE_URL");
        store["DATABASE_URL"].Should().Be("from-vault");
        store["JWT_SECRET"].Should().Be("from-env"); // never overwritten
        // Only the unset key triggered a request.
        handler.Requests.Should().ContainSingle().Subject.RequestUri!.AbsoluteUri.Should().Contain("/secret/DATABASE_URL");
    }

    [Fact]
    public async Task LoadAsync_Production_MissingRequiredSecret_Throws()
    {
        var handler = new TestHttpMessageHandler()
            .OnGet(SecretPrefix, HttpStatusCode.NotFound, """{"error":"secret not found"}""");
        var (get, set, _) = FakeEnv();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler).LoadAsync(failFast: true, get, set, manifest: ["DATABASE_URL"]));
        ex.Message.Should().Contain("DATABASE_URL").And.Contain("Secrets - DEV");
    }

    [Fact]
    public async Task LoadAsync_Production_FetchError_ThrowsWrapped()
    {
        var handler = new TestHttpMessageHandler()
            .OnGet(SecretPrefix, HttpStatusCode.Unauthorized, """{"error":"invalid api key"}""");
        var (get, set, _) = FakeEnv();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler).LoadAsync(failFast: true, get, set, manifest: ["DATABASE_URL"]));
        ex.Message.Should().Contain("failed to fetch");
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task LoadAsync_Development_MissingSecret_ContinuesWithoutThrowing()
    {
        var handler = new TestHttpMessageHandler()
            .OnGet(SecretPrefix, HttpStatusCode.NotFound, """{"error":"secret not found"}""");
        var (get, set, _) = FakeEnv();

        var applied = await Build(handler).LoadAsync(failFast: false, get, set, manifest: ["DATABASE_URL"]);

        applied.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_Development_FetchError_FallsBackWithoutThrowing()
    {
        var handler = new TestHttpMessageHandler()
            .OnGet(SecretPrefix, HttpStatusCode.InternalServerError, """{"error":"boom"}""");
        var (get, set, _) = FakeEnv();

        var applied = await Build(handler).LoadAsync(failFast: false, get, set, manifest: ["DATABASE_URL"]);

        applied.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_Production_MalformedJson_Throws()
    {
        var handler = new TestHttpMessageHandler().OnGet(SecretPrefix, HttpStatusCode.OK, "<html>");
        var (get, set, _) = FakeEnv();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler).LoadAsync(failFast: true, get, set, manifest: ["DATABASE_URL"]));
    }

    [Fact]
    public async Task LoadAsync_Development_MalformedJson_FallsBack()
    {
        var handler = new TestHttpMessageHandler().OnGet(SecretPrefix, HttpStatusCode.OK, "<html>");
        var (get, set, _) = FakeEnv();

        var applied = await Build(handler).LoadAsync(failFast: false, get, set, manifest: ["DATABASE_URL"]);

        applied.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_DefaultManifest_UsesFullManifest()
    {
        // No explicit manifest → the full 18-key Manifest is iterated. All 404 in dev → no-op.
        var handler = new TestHttpMessageHandler()
            .OnGet(SecretPrefix, HttpStatusCode.NotFound, """{"error":"secret not found"}""");
        var (get, set, _) = FakeEnv();

        var applied = await Build(handler).LoadAsync(failFast: false, get, set);

        applied.Should().BeEmpty();
        handler.Requests.Should().HaveCount(VaultwardenSecretsLoader.Manifest.Count);
    }

    [Fact]
    public async Task LoadAsync_CallerCancellation_Propagates()
    {
        var handler = new TestHttpMessageHandler()
            .OnGet(SecretPrefix, HttpStatusCode.OK, """{"name":"DATABASE_URL","value":"v"}""");
        var (get, set, _) = FakeEnv();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Build(handler).LoadAsync(failFast: false, get, set, manifest: ["DATABASE_URL"], ct: cts.Token));
    }
}
