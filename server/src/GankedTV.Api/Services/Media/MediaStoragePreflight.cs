using System.Security.Authentication;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Media;

// Outcome of the worker's storage-reachability probe, in the order of severity the caller logs.
public enum StorageReachability
{
    // A response came back (any HTTP status): the endpoint is reachable and TLS verified.
    Reachable,

    // The TLS handshake failed certificate validation — the classic split-deployment fault this
    // probe exists to catch loudly at boot instead of on the first user clip.
    TlsUntrusted,

    // Connection refused / DNS / timeout: the endpoint isn't reachable from this host at all.
    Unreachable,

    // Nothing to probe against (no clips bucket / unparseable presigned URL).
    Misconfigured,
}

public sealed record StoragePreflightResult(StorageReachability Status, string Detail);

public interface IMediaStoragePreflight
{
    // Presigns a worker fetch against storage and issues one request, classifying whether the
    // media workers on THIS host can actually fetch source bytes. Never throws.
    Task<StoragePreflightResult> CheckAsync(CancellationToken ct);
}

public static class MediaStoragePreflightLog
{
    // Logs a preflight outcome at the severity that matches it — a TLS-trust fault is the loud,
    // actionable boot failure this probe exists to surface. Kept separate from the worker loop so
    // the classification-to-severity mapping is unit-testable without spinning a BackgroundService.
    public static void Report(ILogger logger, string stage, StoragePreflightResult result)
    {
        switch (result.Status)
        {
            case StorageReachability.Reachable:
                logger.LogInformation("{Stage} worker: storage reachable ({Detail}).", stage, result.Detail);
                break;
            case StorageReachability.TlsUntrusted:
                logger.LogCritical(
                    "{Stage} worker: storage TLS verification FAILED ({Detail}). Media fetches will fail and clips will land "
                    + "in 'failed'. Trust the storage CA in this worker's image (mount the cert + update-ca-certificates) or set "
                    + "S3_INTERNAL_ENDPOINT to an endpoint this host reaches and trusts.",
                    stage, result.Detail);
                break;
            case StorageReachability.Unreachable:
                logger.LogError(
                    "{Stage} worker: storage endpoint is unreachable from this host ({Detail}). Media fetches will fail until "
                    + "connectivity is restored.",
                    stage, result.Detail);
                break;
            default:
                logger.LogError("{Stage} worker: storage preflight could not run ({Detail}).", stage, result.Detail);
                break;
        }
    }
}

public sealed class MediaStoragePreflight : IMediaStoragePreflight
{
    // A key that does not exist: a valid signature returns 404, which is all the probe needs
    // (reachable + TLS trusted). Anything under this prefix is never written by the pipeline.
    private const string ProbeKey = "__preflight__/reachability-probe";

    private readonly HttpClient _http;
    private readonly IObjectStorageService _storage;
    private readonly IOptionsMonitor<S3Options> _s3;

    public MediaStoragePreflight(
        HttpClient http,
        IObjectStorageService storage,
        IOptionsMonitor<S3Options> s3)
    {
        _http = http;
        _storage = storage;
        _s3 = s3;
    }

    public async Task<StoragePreflightResult> CheckAsync(CancellationToken ct)
    {
        var bucket = _s3.CurrentValue.ClipsBucket;
        if (string.IsNullOrWhiteSpace(bucket))
        {
            return new StoragePreflightResult(StorageReachability.Misconfigured, "no clips bucket configured");
        }

        string url;
        try
        {
            url = _storage.GetPresignedGetUrlForWorker(bucket, ProbeKey, TimeSpan.FromMinutes(1));
        }
        catch (Exception ex)
        {
            return new StoragePreflightResult(StorageReachability.Misconfigured, ex.Message);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return new StoragePreflightResult(StorageReachability.Misconfigured, "presigned URL is not absolute");
        }

        try
        {
            // GET (the verb the URL is signed for) with headers-only completion: the sentinel
            // key 404s, so no body is transferred. A 4xx is still a good outcome here — it means
            // the request reached storage and TLS verified.
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            return new StoragePreflightResult(
                StorageReachability.Reachable,
                $"HTTP {(int)response.StatusCode} from {uri.Scheme}://{uri.Authority}");
        }
        catch (HttpRequestException ex) when (HasTlsFailure(ex))
        {
            return new StoragePreflightResult(StorageReachability.TlsUntrusted, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new StoragePreflightResult(StorageReachability.Unreachable, ex.Message);
        }
    }

    // A certificate-validation failure surfaces as an AuthenticationException somewhere in the
    // HttpRequestException chain (the TLS layer throws it, HttpClient wraps it).
    private static bool HasTlsFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException)
            {
                return true;
            }
        }
        return false;
    }
}
