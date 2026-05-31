using Amazon.S3;
using GankedTV.Api.Auth;
using GankedTV.Api.Auth.Jwt;
using GankedTV.Api.Auth.Passwords;
using GankedTV.Api.Auth.Providers;
using GankedTV.Api.Auth.State;
using GankedTV.Api.Auth.Tokens;
using GankedTV.Api.Clips;
using GankedTV.Api.Configuration;
using GankedTV.Api.Data;
using GankedTV.Api.Endpoints;
using GankedTV.Api.HostedServices;
using GankedTV.Api.Middleware;
using GankedTV.Api.Notifications;
using GankedTV.Api.Observability;
using GankedTV.Api.Services.Caching;
using GankedTV.Api.Services.Clips;
using GankedTV.Api.Services.Igdb;
using GankedTV.Api.Services.Maintenance;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Services.Moderation;
using GankedTV.Api.Services.Health;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Services.Tags;
using GankedTV.Api.Tools;
using GankedTV.Api.Validation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sentry.Extensibility;
using System.Globalization;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Load repo-root .env file for local development
if (builder.Environment.IsDevelopment())
{
    var envPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "..", ".env");
    if (File.Exists(envPath))
    {
        DotNetEnv.Env.NoClobber().Load(envPath);
    }
}

// Vaultwarden secret bootstrap: like the DotNetEnv load above, fetch the manifest and set each as
// an env var only when unset, so shell/.env still wins and every GetEnvironmentVariable read site
// (the connection-string read below, the Production validation later) picks it up unchanged. No-op
// when the bootstrap vars are unset; logic lives in the loader to stay in the coverage denominator.
var vaultwardenOptions = new VaultwardenOptions
{
    ApiUrl = Environment.GetEnvironmentVariable("VAULTWARDEN_API_URL")?.Trim(),
    ApiKey = Environment.GetEnvironmentVariable("VAULTWARDEN_API_KEY")?.Trim(),
    Organization = Environment.GetEnvironmentVariable("VAULTWARDEN_ORG")?.Trim() is { Length: > 0 } vaultOrg
        ? vaultOrg
        : "GankedTV",
    Collection = Environment.GetEnvironmentVariable("VAULTWARDEN_COLLECTION"),
};
if (vaultwardenOptions.IsConfigured)
{
    // One short-lived HttpClient for this pre-host bootstrap fetch — IHttpClientFactory isn't
    // available before builder.Build(), and pooling buys nothing for a few sequential one-shot calls.
    using var vaultwardenHttp = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(10),
        MaxResponseContentBufferSize = 64 * 1024,
    };
    using var vaultwardenLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
    var vaultwardenLoader = new VaultwardenSecretsLoader(
        vaultwardenHttp,
        vaultwardenOptions,
        vaultwardenOptions.EffectiveCollection(builder.Environment.EnvironmentName),
        vaultwardenLoggerFactory.CreateLogger<VaultwardenSecretsLoader>());
    vaultwardenLoader
        .LoadAsync(
            failFast: builder.Environment.IsProduction(),
            Environment.GetEnvironmentVariable,
            Environment.SetEnvironmentVariable)
        .GetAwaiter()
        .GetResult();
}

// Error monitoring (Sentry → GlitchTip). Opt-in: the SDK throws on a null DSN and treats "" as
// disabled, so default to "" when SENTRY_DSN is unset. Runs after the Vaultwarden bootstrap so a
// DSN provisioned there is seen; SENTRY_ENVIRONMENT / SENTRY_RELEASE are auto-read from env.
builder.WebHost.UseSentry(o =>
{
    o.Dsn = Environment.GetEnvironmentVariable("SENTRY_DSN") ?? "";
    o.SendDefaultPii = false;
    o.TracesSampleRate =
        double.TryParse(
            Environment.GetEnvironmentVariable("SENTRY_TRACES_SAMPLE_RATE"),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var rate)
            ? rate
            : builder.Configuration.GetValue("Sentry:TracesSampleRate", 0.01);
});
builder.Services.AddSingleton<ISentryEventProcessor, SentryPiiScrubber>();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// RFC 7807 ProblemDetails for all framework-generated 4xx/5xx bodies (empty responses
// from UseAuthorization/UseAuthentication get shaped automatically). Endpoint-authored
// errors go through ProblemResults.*.
builder.Services.AddProblemDetails();
builder.Services.AddTransient<ErrorHandlingMiddleware>();
builder.Services.AddTransient<BannedUserMiddleware>();
builder.Services.AddScoped<SeedCommand>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddHostedService<AdminBootstrap>();

// Readiness probe: DB reachable + migrations applied (logic in ReadinessHealthCheck so it
// stays in the coverage denominator). Liveness needs no checks — mapped with an empty predicate.
builder.Services.AddHealthChecks()
    .AddCheck<ReadinessHealthCheck>("ready", tags: ["ready"]);

var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "DATABASE_URL env var or ConnectionStrings:DefaultConnection must be set");

builder.Services.AddDbContext<GankedTvDbContext>(opts =>
    opts.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

builder.Services.Configure<S3Options>(opts =>
{
    opts.Endpoint = Environment.GetEnvironmentVariable("S3_ENDPOINT") ?? builder.Configuration["S3:Endpoint"] ?? "http://localhost:9000";
    opts.AccessKey = Environment.GetEnvironmentVariable("S3_ACCESS_KEY") ?? builder.Configuration["S3:AccessKey"] ?? "minioadmin";
    opts.SecretKey = Environment.GetEnvironmentVariable("S3_SECRET_KEY") ?? builder.Configuration["S3:SecretKey"] ?? "minioadmin";
    // .env.example ships `S3_PUBLIC_URL=` (empty); treat empty/whitespace as unset so the config fallback wins.
    var envPublic = Environment.GetEnvironmentVariable("S3_PUBLIC_URL");
    opts.PublicUrl = !string.IsNullOrWhiteSpace(envPublic) ? envPublic : builder.Configuration["S3:PublicUrl"];
    var clips = builder.Configuration["S3:ClipsBucket"];
    var thumbs = builder.Configuration["S3:ThumbnailsBucket"];
    var covers = builder.Configuration["S3:GameCoversBucket"];
    var streamCache = builder.Configuration["S3:StreamCacheBucket"];
    if (!string.IsNullOrWhiteSpace(clips)) opts.ClipsBucket = clips;
    if (!string.IsNullOrWhiteSpace(thumbs)) opts.ThumbnailsBucket = thumbs;
    if (!string.IsNullOrWhiteSpace(covers)) opts.GameCoversBucket = covers;
    if (!string.IsNullOrWhiteSpace(streamCache)) opts.StreamCacheBucket = streamCache;
    var streamTtl = builder.Configuration["S3:StreamCacheTtlDays"];
    if (int.TryParse(streamTtl, out var ttl) && ttl > 0) opts.StreamCacheTtlDays = ttl;
});

builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var o = sp.GetRequiredService<IOptions<S3Options>>().Value;
    return new AmazonS3Client(o.AccessKey, o.SecretKey, new AmazonS3Config
    {
        ServiceURL = o.Endpoint,
        ForcePathStyle = true,
    });
});

builder.Services.AddSingleton<IObjectStorageService, S3ObjectStorageService>();
builder.Services.AddHostedService<BucketBootstrapHostedService>();

builder.Services.AddOptions<MaintenanceOptions>()
    .Configure(opts =>
    {
        builder.Configuration.GetSection("Maintenance").Bind(opts);
        var enabled = Environment.GetEnvironmentVariable("MAINTENANCE_ENABLED");
        if (bool.TryParse(enabled, out var e)) opts.Enabled = e;
        var interval = Environment.GetEnvironmentVariable("MAINTENANCE_SWEEP_INTERVAL_MINUTES");
        if (int.TryParse(interval, out var im) && im > 0) opts.SweepInterval = TimeSpan.FromMinutes(im);
        var clipHours = Environment.GetEnvironmentVariable("MAINTENANCE_CLIP_THRESHOLD_HOURS");
        if (int.TryParse(clipHours, out var ch) && ch > 0) opts.ClipStaleThreshold = TimeSpan.FromHours(ch);
        var rtDays = Environment.GetEnvironmentVariable("MAINTENANCE_REFRESH_TOKEN_RETENTION_DAYS");
        if (int.TryParse(rtDays, out var rd) && rd > 0) opts.RefreshTokenRetention = TimeSpan.FromDays(rd);
        var batch = Environment.GetEnvironmentVariable("MAINTENANCE_CLIP_BATCH_SIZE");
        if (int.TryParse(batch, out var bs) && bs > 0) opts.ClipBatchSize = bs;
    })
    .Validate(o => o.SweepInterval > TimeSpan.Zero, "Maintenance.SweepInterval must be positive.")
    .Validate(o => o.ClipStaleThreshold > TimeSpan.Zero, "Maintenance.ClipStaleThreshold must be positive.")
    .Validate(o => o.RefreshTokenRetention > TimeSpan.Zero, "Maintenance.RefreshTokenRetention must be positive.")
    .Validate(o => o.ClipBatchSize > 0, "Maintenance.ClipBatchSize must be positive.")
    .ValidateOnStart();
builder.Services.AddHostedService<MaintenanceHostedService>();

builder.Services.AddOptions<MediaJobOptions>()
    .Configure(opts =>
    {
        builder.Configuration.GetSection("MediaJobs").Bind(opts);
        var enabled = Environment.GetEnvironmentVariable("MEDIA_JOBS_ENABLED");
        if (bool.TryParse(enabled, out var e)) opts.Enabled = e;
        var pollSec = Environment.GetEnvironmentVariable("MEDIA_JOBS_POLL_INTERVAL_SECS");
        if (int.TryParse(pollSec, out var ps) && ps > 0) opts.PollInterval = TimeSpan.FromSeconds(ps);
        var leaseMin = Environment.GetEnvironmentVariable("MEDIA_JOBS_LEASE_MINUTES");
        if (int.TryParse(leaseMin, out var lm) && lm > 0) opts.LeaseDuration = TimeSpan.FromMinutes(lm);
        var maxAttempts = Environment.GetEnvironmentVariable("MEDIA_JOBS_MAX_ATTEMPTS");
        if (int.TryParse(maxAttempts, out var ma) && ma > 0) opts.MaxAttempts = ma;
        var ffmpeg = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(ffmpeg)) opts.FfmpegPath = ffmpeg;
        var ffprobe = Environment.GetEnvironmentVariable("FFPROBE_PATH");
        if (!string.IsNullOrWhiteSpace(ffprobe)) opts.FfprobePath = ffprobe;
        // Two-stage pipeline + GPU-split toggles (issue #102). TranscodeEnabled controls
        // whether the pipeline includes the HLS stage; the *_WORKER_ENABLED flags control
        // which workers run on *this* instance so the GPU-heavy transcode work can live on
        // a separate host.
        var transcodeEnabled = Environment.GetEnvironmentVariable("MEDIA_TRANSCODE_ENABLED");
        if (bool.TryParse(transcodeEnabled, out var te)) opts.TranscodeEnabled = te;
        var thumbWorker = Environment.GetEnvironmentVariable("MEDIA_THUMBNAIL_WORKER_ENABLED");
        if (bool.TryParse(thumbWorker, out var tw)) opts.ThumbnailWorkerEnabled = tw;
        var transWorker = Environment.GetEnvironmentVariable("MEDIA_TRANSCODE_WORKER_ENABLED");
        if (bool.TryParse(transWorker, out var trw)) opts.TranscodeWorkerEnabled = trw;
        // Master compression (stored) + JIT ladder (transient) encoders.
        var encoder = Environment.GetEnvironmentVariable("MEDIA_VIDEO_ENCODER");
        if (!string.IsNullOrWhiteSpace(encoder)) opts.VideoEncoder = encoder;
        var codec = Environment.GetEnvironmentVariable("MEDIA_VIDEO_CODEC");
        if (!string.IsNullOrWhiteSpace(codec)) opts.VideoCodec = codec;
        var jitEncoder = Environment.GetEnvironmentVariable("MEDIA_JIT_VIDEO_ENCODER");
        if (!string.IsNullOrWhiteSpace(jitEncoder)) opts.JitVideoEncoder = jitEncoder;
        var maxHeight = Environment.GetEnvironmentVariable("MEDIA_MAX_HEIGHT");
        if (int.TryParse(maxHeight, out var mh) && mh > 0) opts.MaxHeight = mh;
        var crf = Environment.GetEnvironmentVariable("MEDIA_CRF");
        if (int.TryParse(crf, out var cr) && cr > 0) opts.Crf = cr;
        var segType = Environment.GetEnvironmentVariable("MEDIA_HLS_SEGMENT_TYPE");
        if (!string.IsNullOrWhiteSpace(segType)) opts.SegmentType = segType;
        var segDur = Environment.GetEnvironmentVariable("MEDIA_HLS_SEGMENT_SECONDS");
        if (int.TryParse(segDur, out var sd) && sd > 0) opts.SegmentDuration = TimeSpan.FromSeconds(sd);
        var transcodeMin = Environment.GetEnvironmentVariable("MEDIA_TRANSCODE_TIMEOUT_MINUTES");
        if (int.TryParse(transcodeMin, out var tm) && tm > 0) opts.TranscodeTimeout = TimeSpan.FromMinutes(tm);
        // URL import (issue #106). Independent of the GPU split — the fetch is I/O bound and
        // belongs on the API host. AllowedHosts is comma-separated, trimmed, lowercased.
        var importEnabled = Environment.GetEnvironmentVariable("MEDIA_IMPORT_ENABLED");
        if (bool.TryParse(importEnabled, out var ie)) opts.Import.Enabled = ie;
        var importWorkerEnabled = Environment.GetEnvironmentVariable("MEDIA_IMPORT_WORKER_ENABLED");
        if (bool.TryParse(importWorkerEnabled, out var iwe)) opts.Import.WorkerEnabled = iwe;
        var ytdlp = Environment.GetEnvironmentVariable("YTDLP_PATH");
        if (!string.IsNullOrWhiteSpace(ytdlp)) opts.Import.YtdlpPath = ytdlp;
        var fetchSecs = Environment.GetEnvironmentVariable("MEDIA_IMPORT_FETCH_TIMEOUT_SECS");
        if (int.TryParse(fetchSecs, out var fs) && fs > 0) opts.Import.FetchTimeout = TimeSpan.FromSeconds(fs);
        var importHosts = Environment.GetEnvironmentVariable("MEDIA_IMPORT_ALLOWED_HOSTS");
        if (!string.IsNullOrWhiteSpace(importHosts))
        {
            opts.Import.AllowedHosts = importHosts
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(h => h.ToLowerInvariant())
                .ToList();
        }
    })
    .Validate(o => o.PollInterval > TimeSpan.Zero, "MediaJobs.PollInterval must be positive.")
    .Validate(o => o.LeaseDuration > TimeSpan.Zero, "MediaJobs.LeaseDuration must be positive.")
    .Validate(o => o.ProcessTimeout > TimeSpan.Zero, "MediaJobs.ProcessTimeout must be positive.")
    .Validate(o => o.TranscodeTimeout > TimeSpan.Zero, "MediaJobs.TranscodeTimeout must be positive.")
    .Validate(o => o.SegmentDuration > TimeSpan.Zero, "MediaJobs.SegmentDuration must be positive.")
    .Validate(o => o.MaxAttempts > 0, "MediaJobs.MaxAttempts must be positive.")
    .Validate(o => o.MaxDrainPerTick > 0, "MediaJobs.MaxDrainPerTick must be positive.")
    .Validate(o => o.MaxHeight > 0, "MediaJobs.MaxHeight must be positive.")
    .Validate(o => o.Crf > 0, "MediaJobs.Crf must be positive.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.FfmpegPath), "MediaJobs.FfmpegPath must be set.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.FfprobePath), "MediaJobs.FfprobePath must be set.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.VideoEncoder), "MediaJobs.VideoEncoder must be set.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.VideoCodec), "MediaJobs.VideoCodec must be set.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.JitVideoEncoder), "MediaJobs.JitVideoEncoder must be set.")
    .Validate(o => o.Ladder is { Count: > 0 }, "MediaJobs.Ladder must define at least one rung.")
    .Validate(o => o.Import.FetchTimeout > TimeSpan.Zero, "MediaJobs.Import.FetchTimeout must be positive.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Import.YtdlpPath), "MediaJobs.Import.YtdlpPath must be set.")
    .Validate(o => o.Import.AllowedHosts is { Count: > 0 }, "MediaJobs.Import.AllowedHosts must list at least one host.")
    .ValidateOnStart();
builder.Services.AddSingleton<IFfmpegRunner, FfmpegRunner>();
builder.Services.AddScoped<IClipMediaJobStore, ClipMediaJobStore>();
builder.Services.AddScoped<IClipStreamJobStore, ClipStreamJobStore>();
builder.Services.AddScoped<IThumbnailJobService, ThumbnailJobService>();
builder.Services.AddScoped<ICompressJobService, CompressJobService>();
builder.Services.AddScoped<IJitLadderService, JitLadderService>();
builder.Services.AddHostedService<ThumbnailWorker>();
builder.Services.AddHostedService<CompressWorker>();
builder.Services.AddHostedService<StreamRenditionWorker>();
// URL-import stage (issue #106). Worker exits at startup when MEDIA_IMPORT_ENABLED=false
// (or MEDIA_IMPORT_WORKER_ENABLED=false on the GPU box), mirroring the IGDB-sync pattern.
builder.Services.AddSingleton<GankedTV.Api.Services.Media.Import.IClipImportSource,
    GankedTV.Api.Services.Media.Import.YtDlpImportSource>();
builder.Services.AddHostedService<ImportWorker>();

builder.Services.AddOptions<ClipValidationOptions>()
    .Configure(opts =>
    {
        // Bind the full Clips section first so appsettings can configure
        // AllowedContentTypes / MaxTitleLength / MaxDescriptionLength alongside the two
        // env-var-only settings below.
        builder.Configuration.GetSection("Clips").Bind(opts);
        var size = Environment.GetEnvironmentVariable("MAX_UPLOAD_SIZE_MB");
        if (int.TryParse(size, out var mb) && mb > 0) opts.MaxUploadSizeMb = mb;
        var dur = Environment.GetEnvironmentVariable("MAX_CLIP_DURATION_SECS");
        if (int.TryParse(dur, out var secs) && secs > 0) opts.MaxClipDurationSecs = secs;
    })
    .Validate(o => o.MaxUploadSizeMb is > 0 and <= 5000,
        "MAX_UPLOAD_SIZE_MB must be in [1, 5000].")
    .Validate(o => o.MaxClipDurationSecs is > 0 and <= 3600,
        "MAX_CLIP_DURATION_SECS must be in [1, 3600].")
    .ValidateOnStart();

builder.Services.AddScoped<IClipUploadService, ClipUploadService>();
builder.Services.AddScoped<IClipImportService, ClipImportService>();
builder.Services.AddScoped<IClipImportUrlValidator, ClipImportUrlValidator>();
builder.Services.AddScoped<ITagsResolver, TagsResolver>();
builder.Services.AddScoped<INotificationService, NotificationService>();

builder.Services.AddOptions<GankedTV.Api.Services.Profile.ProfileMediaOptions>()
    .Configure(opts => builder.Configuration.GetSection("ProfileMedia").Bind(opts));
builder.Services.AddScoped<GankedTV.Api.Services.Profile.IProfileMediaService,
    GankedTV.Api.Services.Profile.ProfileMediaService>();

// ---- Auth configuration ----

builder.Services.AddOptions<JwtOptions>()
    .Configure(opts =>
    {
        opts.Secret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? builder.Configuration["Jwt:Secret"] ?? "";
        opts.Issuer = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? builder.Configuration["Jwt:Issuer"] ?? "gankedtv";
        opts.Audience = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? builder.Configuration["Jwt:Audience"] ?? "gankedtv-web";
        var expiry = Environment.GetEnvironmentVariable("JWT_EXPIRY_MINUTES") ?? builder.Configuration["Jwt:ExpiryMinutes"];
        if (int.TryParse(expiry, out var mins) && mins > 0) opts.ExpiryMinutes = mins;
    })
    .Validate(o => Encoding.UTF8.GetByteCount(o.Secret) >= 32,
        "JWT_SECRET must be at least 32 bytes.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer) && !string.IsNullOrWhiteSpace(o.Audience),
        "JWT_ISSUER and JWT_AUDIENCE must be set.")
    .ValidateOnStart();

builder.Services.AddOptions<RefreshTokenOptions>()
    .Configure(opts =>
    {
        var days = Environment.GetEnvironmentVariable("REFRESH_TOKEN_EXPIRY_DAYS") ?? builder.Configuration["Jwt:RefreshTokenExpiryDays"];
        if (int.TryParse(days, out var d) && d > 0) opts.ExpiryDays = d;
    })
    .Validate(o => o.ExpiryDays is > 0 and <= 365,
        "REFRESH_TOKEN_EXPIRY_DAYS must be between 1 and 365.")
    .ValidateOnStart();

builder.Services.AddOptions<OAuthOptions>()
    .Configure(opts =>
    {
        opts.StateSecret = Environment.GetEnvironmentVariable("OAUTH_STATE_SECRET") ?? builder.Configuration["OAuth:StateSecret"] ?? "";
        opts.WebOrigin = Environment.GetEnvironmentVariable("WEB_ORIGIN") ?? builder.Configuration["OAuth:WebOrigin"] ?? "http://localhost:5173";
        opts.Discord.ClientId = Environment.GetEnvironmentVariable("DISCORD_CLIENT_ID") ?? builder.Configuration["OAuth:Discord:ClientId"] ?? "";
        opts.Discord.ClientSecret = Environment.GetEnvironmentVariable("DISCORD_CLIENT_SECRET") ?? builder.Configuration["OAuth:Discord:ClientSecret"] ?? "";
        opts.Discord.RedirectUri = Environment.GetEnvironmentVariable("DISCORD_REDIRECT_URI") ?? builder.Configuration["OAuth:Discord:RedirectUri"] ?? "";
        opts.Google.ClientId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID") ?? builder.Configuration["OAuth:Google:ClientId"] ?? "";
        opts.Google.ClientSecret = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET") ?? builder.Configuration["OAuth:Google:ClientSecret"] ?? "";
        opts.Google.RedirectUri = Environment.GetEnvironmentVariable("GOOGLE_REDIRECT_URI") ?? builder.Configuration["OAuth:Google:RedirectUri"] ?? "";
    })
    .Validate(o => !o.AnyProviderConfigured || Encoding.UTF8.GetByteCount(o.StateSecret) >= 32,
        "OAUTH_STATE_SECRET must be at least 32 bytes when any OAuth provider is configured.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.WebOrigin),
        "WEB_ORIGIN must be set.")
    .ValidateOnStart();

// OAuth providers shouldn't hang the callback endpoint if upstream is slow.
// We deliberately do NOT add retry — replaying a consumed authorization code fails anyway.
// MaxResponseContentBufferSize caps token/userinfo responses at 64 KB (real responses are <4 KB)
// so a hostile or malfunctioning provider can't OOM us.
const long OAuthMaxResponseBytes = 64 * 1024;
builder.Services.AddHttpClient(DiscordOAuthProvider.ProviderName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.MaxResponseContentBufferSize = OAuthMaxResponseBytes;
});
builder.Services.AddHttpClient(GoogleOAuthProvider.ProviderName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.MaxResponseContentBufferSize = OAuthMaxResponseBytes;
});

// ---- IGDB metadata (game covers) ----
builder.Services.AddOptions<IgdbOptions>()
    .Configure(opts =>
    {
        builder.Configuration.GetSection("Igdb").Bind(opts);
        opts.ClientId = Environment.GetEnvironmentVariable("IGDB_CLIENT_ID") ?? opts.ClientId;
        opts.ClientSecret = Environment.GetEnvironmentVariable("IGDB_CLIENT_SECRET") ?? opts.ClientSecret;
        var count = Environment.GetEnvironmentVariable("IGDB_IMPORT_COUNT");
        if (int.TryParse(count, out var c) && c > 0) opts.PopularImportCount = c;
        var syncEnabled = Environment.GetEnvironmentVariable("IGDB_SYNC_ENABLED");
        if (bool.TryParse(syncEnabled, out var se)) opts.SyncEnabled = se;
        var syncDays = Environment.GetEnvironmentVariable("IGDB_SYNC_INTERVAL_DAYS");
        if (int.TryParse(syncDays, out var sd) && sd > 0) opts.SyncInterval = TimeSpan.FromDays(sd);
    })
    .Validate(o => o.PopularImportCount > 0, "Igdb.PopularImportCount must be positive.")
    .Validate(o => o.MaxRequestsPerSecond > 0, "Igdb.MaxRequestsPerSecond must be positive.")
    .Validate(o => o.SyncInterval > TimeSpan.Zero, "Igdb.SyncInterval must be positive.")
    .ValidateOnStart();

// api.igdb.com responses (game metadata) are small JSON; cap at 8 MB to bound a large
// popular-games page. Cover image downloads hit images.igdb.com — a separate buffer cap.
builder.Services.AddHttpClient(IgdbMetadataService.ApiClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.MaxResponseContentBufferSize = 8 * 1024 * 1024;
});
builder.Services.AddHttpClient(IgdbMetadataService.ImageClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.MaxResponseContentBufferSize = 4 * 1024 * 1024;
});
builder.Services.AddSingleton<IIgdbMetadataService, IgdbMetadataService>();
builder.Services.AddScoped<IGameCatalogImporter, GameCatalogImporter>();
builder.Services.AddScoped<ImportGamesCommand>();
builder.Services.AddHostedService<IgdbSyncHostedService>();

builder.Services.AddSingleton<IJwtService, JwtService>();
builder.Services.AddSingleton<IStateCookieService, StateCookieService>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();
builder.Services.AddScoped<UserUpsertService>();
builder.Services.AddScoped<CredentialAuthService>();

// Redis-backed shared cache (hot feeds + trending) + cluster-wide rate limiting. OPTIONAL:
// AddGankedCaching falls back to an in-process cache + per-instance limiters when REDIS_URL
// is unset/malformed, so local dev needs no Redis. Config binding only — logic lives in the
// Services/Caching/* services to stay inside the coverage denominator.
var redisOptions = new RedisOptions
{
    Url = Environment.GetEnvironmentVariable("REDIS_URL") ?? builder.Configuration["Redis:Url"],
};
builder.Services.AddSingleton(redisOptions);
builder.Services.AddGankedCaching(redisOptions);

builder.Services.AddRateLimiter(opts => opts
    .AddCredentialsPolicy()
    .AddClipsWritePolicy()
    .AddClipsViewPolicy());

// Backs the view-dedup window in ClipsViewEndpoints. In-process for v1; per-pod state is
// fine for an anti-spam dedup (the worst-case drift on restart is a single bonus view per
// client). Phase 4 swaps for Redis when limits + dedup go multi-instance.
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<IOAuthProvider, DiscordOAuthProvider>();
builder.Services.AddSingleton<IOAuthProvider, GoogleOAuthProvider>();
builder.Services.AddSingleton<OAuthProviderRegistry>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

// Reuse the same TokenValidationParameters instance JwtService uses so the two sides
// can't drift (NameClaimType, ClockSkew, issuer/audience, signing key).
//
// MapInboundClaims=false stops the bearer handler from rewriting short JWT claim names
// (sub, role, email, ...) into the long ClaimTypes.* URIs at validation time. Without this,
// the "role" claim we emit lands on the principal as ClaimTypes.Role, and RequireClaim("role", ...)
// in the admin/moderator policies fails to match. JwtService.Issue is the authoritative
// shape of the claim set; the bearer should expose it identically.
builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
    {
        bearer.TokenValidationParameters = JwtService.BuildValidationParameters(jwtOptions.Value);
        bearer.MapInboundClaims = false;
    });

builder.Services.AddAuthorization(opts => opts.AddRolePolicies());

const string corsPolicy = "WebOrigin";
// Allowed origins = CORS_ORIGINS (comma-separated) ∪ WebOrigin. WebOrigin is always included
// because OAuth redirects land on it and the browser's follow-up XHR must pass CORS — an
// operator who forgets to list it in CORS_ORIGINS would otherwise break the sign-in flow.
// We register the policy via AddOptions<CorsOptions>().Configure<IOptions<OAuthOptions>>
// instead of AddCors(o => o.AddPolicy(...)) because the origin list depends on the already-
// bound OAuthOptions and the AddCors lambda overload can't inject IOptions<T>.
var corsOriginsRaw = Environment.GetEnvironmentVariable("CORS_ORIGINS");
// Capture into a local so the lambda doesn't reach into `builder` (which is closed-over
// at scope exit and could surface stale state if Program ever evolves into a class).
var corsAllowAnyLocalhost = builder.Environment.IsDevelopment();
builder.Services
    .AddOptions<CorsOptions>()
    .Configure<IOptions<OAuthOptions>>((cors, oauth) =>
    {
        var origins = CorsOriginsParser.Parse(corsOriginsRaw, oauth.Value.WebOrigin);
        // SetIsOriginAllowed with an explicit predicate (not WithOrigins) so a literal "*"
        // in CORS_ORIGINS is matched as a string, not interpreted by CorsService as the
        // CORS-spec wildcard (which silently disables AllowCredentials). Host comparison
        // is case-insensitive per RFC 6454; scheme/port exact-match.
        //
        // Dev belt-and-suspenders: in Development we also accept any localhost origin so
        // a worktree on a non-default VITE_PORT (or a misconfigured WEB_ORIGIN) doesn't
        // surface as an opaque CORS error in the browser. Production keeps the strict
        // allowlist — there's no scenario in prod where a request from "localhost"
        // should reach the public API.
        cors.AddPolicy(corsPolicy, policy => policy
            .SetIsOriginAllowed(origin =>
                origins.Contains(origin, StringComparer.OrdinalIgnoreCase)
                || (corsAllowAnyLocalhost && CorsOriginsParser.IsLocalhostOrigin(origin)))
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
    });
builder.Services.AddCors();

// Fail-fast secret validation: in Production, refuse to boot when required secrets are missing
// (or still set to dev defaults) instead of running misconfigured and failing on the first
// request. Reads raw env vars — not the DI-bound options — so dev fallbacks (localhost WebOrigin,
// minioadmin S3 creds) can't mask an unset secret. Aggregation logic lives in
// ProductionStartupValidator to stay inside the coverage denominator.
if (builder.Environment.IsProduction())
{
    var secretErrors = ProductionStartupValidator.Validate(
        connectionString,
        new JwtOptions
        {
            Secret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? "",
            Issuer = "n/a",
            Audience = "n/a",
        },
        new OAuthOptions { WebOrigin = Environment.GetEnvironmentVariable("WEB_ORIGIN") ?? "" },
        new S3Options
        {
            Endpoint = Environment.GetEnvironmentVariable("S3_ENDPOINT") ?? "",
            AccessKey = Environment.GetEnvironmentVariable("S3_ACCESS_KEY") ?? "",
            SecretKey = Environment.GetEnvironmentVariable("S3_SECRET_KEY") ?? "",
            PublicUrl = Environment.GetEnvironmentVariable("S3_PUBLIC_URL"),
        },
        Environment.GetEnvironmentVariable("CORS_ORIGINS"));
    if (secretErrors.Count > 0)
    {
        throw new InvalidOperationException(
            "Refusing to start in Production — required configuration is missing or invalid:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, secretErrors.Select(e => "  - " + e)));
    }
}

var app = builder.Build();

// --seed short-circuit: runs the dev seed against the configured DB and exits. We still build
// the full app so DbContext/options are wired identically to the runtime path.
if (SeedCommand.ShouldRun(args))
{
    using var scope = app.Services.CreateScope();
    var seed = scope.ServiceProvider.GetRequiredService<SeedCommand>();
    await seed.RunAsync(CancellationToken.None);
    return;
}

// --import-games short-circuit: backfills the games catalog + cover art from IGDB and exits.
// Idempotent / resumable; requires IGDB_CLIENT_ID / IGDB_CLIENT_SECRET (the command exits
// cleanly with a log line if they're absent).
if (ImportGamesCommand.ShouldRun(args))
{
    using var scope = app.Services.CreateScope();
    var import = scope.ServiceProvider.GetRequiredService<ImportGamesCommand>();
    await import.RunAsync(CancellationToken.None);
    return;
}

// Startup migrations (gated by RUN_MIGRATIONS_ON_STARTUP, default off). Runs synchronously
// before the app serves so a fresh prod DB self-migrates and /health/ready only goes green
// once the schema exists. Locally migrations stay manual (`make migrate`).
if (DatabaseMigrator.IsEnabled(Environment.GetEnvironmentVariable(DatabaseMigrator.EnableEnvVar)))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<GankedTvDbContext>();
    await DatabaseMigrator.ApplyMigrationsAsync(db, app.Logger, CancellationToken.None);
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseMiddleware<ErrorHandlingMiddleware>();
// Shape framework-generated empty-body 4xx/5xx responses (JwtBearer 401 challenges,
// 404 for unmatched routes, 415 for unsupported media types) into ProblemDetails so every
// error response from the API has the same JSON envelope regardless of origin.
app.UseStatusCodePages();

// Health probes as terminal middleware placed BEFORE UseHttpsRedirection so an HTTP probe
// (container HEALTHCHECK, k8s probe, the #123 deploy smoke-test) short-circuits with 200/503
// instead of getting a 307 redirect to https. Liveness: process up, no dependency checks.
// Readiness: DB reachable + migrations applied (the "ready"-tagged ReadinessHealthCheck).
app.UseHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.UseHealthChecks(
    "/health/ready",
    new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

// Skip in dev — :5050 is plain HTTP and the redirect emits CORS-less 307s.
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseCors(corsPolicy);
app.UseAuthentication();
// Banned-user gate sits between authentication and authorization so a still-valid JWT can't
// reach any protected handler once the account is disabled. Authenticated requests pay one
// cheap row lookup per call; anonymous requests short-circuit before the DB hit.
app.UseMiddleware<BannedUserMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();

app.MapAuthEndpoints();
app.MapMeEndpoints();
app.MapProfileMediaEndpoints();
app.MapClipsUploadEndpoints();
app.MapClipsImportEndpoints();
app.MapClipsReadEndpoints();
app.MapClipsMutateEndpoints();
app.MapLikesEndpoints();
app.MapClipsViewEndpoints();
app.MapCommentsEndpoints();
app.MapUsersEndpoints();
app.MapFollowsEndpoints();
app.MapNotificationsEndpoints();
app.MapGamesEndpoints();
app.MapLeaderboardsEndpoints();
app.MapTagsEndpoints();
app.MapSearchEndpoints();
app.MapReportsEndpoints();
app.MapAdminEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapDevAuthEndpoints();
    app.Logger.LogWarning(
        "Development mode: POST /dev/token is mapped and will mint JWTs without authentication. "
        + "Ensure ASPNETCORE_ENVIRONMENT is NOT 'Development' in any internet-exposed deployment.");
}

app.Run();
