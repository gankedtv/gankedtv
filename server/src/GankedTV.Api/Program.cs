using Amazon.S3;
using GankedTV.Api.Auth;
using GankedTV.Api.Auth.Jwt;
using GankedTV.Api.Auth.Providers;
using GankedTV.Api.Auth.State;
using GankedTV.Api.Auth.Tokens;
using GankedTV.Api.Data;
using GankedTV.Api.Endpoints;
using GankedTV.Api.Services.Clips;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Validation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "DATABASE_URL env var or ConnectionStrings:DefaultConnection must be set");

builder.Services.AddDbContext<GankedTvDbContext>(opts =>
    opts.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

builder.Services.Configure<MinioOptions>(opts =>
{
    opts.Endpoint = Environment.GetEnvironmentVariable("S3_ENDPOINT") ?? builder.Configuration["Minio:Endpoint"] ?? "http://localhost:9000";
    opts.AccessKey = Environment.GetEnvironmentVariable("S3_ACCESS_KEY") ?? builder.Configuration["Minio:AccessKey"] ?? "minioadmin";
    opts.SecretKey = Environment.GetEnvironmentVariable("S3_SECRET_KEY") ?? builder.Configuration["Minio:SecretKey"] ?? "minioadmin";
    // .env.example ships `S3_PUBLIC_URL=` (empty); treat empty/whitespace as unset so the config fallback wins.
    var envPublic = Environment.GetEnvironmentVariable("S3_PUBLIC_URL");
    opts.PublicUrl = !string.IsNullOrWhiteSpace(envPublic) ? envPublic : builder.Configuration["Minio:PublicUrl"];
    var clips = builder.Configuration["Minio:ClipsBucket"];
    var thumbs = builder.Configuration["Minio:ThumbnailsBucket"];
    if (!string.IsNullOrWhiteSpace(clips)) opts.ClipsBucket = clips;
    if (!string.IsNullOrWhiteSpace(thumbs)) opts.ThumbnailsBucket = thumbs;
});

builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var o = sp.GetRequiredService<IOptions<MinioOptions>>().Value;
    // GetPreSignedURL in the AWS SDK defaults to https:// regardless of ServiceURL scheme
    // unless UseHttp is set. MinIO in dev runs plain HTTP, so presigned PUTs fail TLS
    // negotiation without this flag.
    var useHttp = o.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    return new AmazonS3Client(o.AccessKey, o.SecretKey, new AmazonS3Config
    {
        ServiceURL = o.Endpoint,
        ForcePathStyle = true,
        UseHttp = useHttp,
    });
});

builder.Services.AddSingleton<IObjectStorageService, MinioObjectStorageService>();
builder.Services.AddHostedService<BucketBootstrapHostedService>();

builder.Services.AddOptions<ClipValidationOptions>()
    .Configure(opts =>
    {
        var size = Environment.GetEnvironmentVariable("MAX_UPLOAD_SIZE_MB")
            ?? builder.Configuration["Clips:MaxUploadSizeMb"];
        if (int.TryParse(size, out var mb) && mb > 0) opts.MaxUploadSizeMb = mb;
        var dur = Environment.GetEnvironmentVariable("MAX_CLIP_DURATION_SECS")
            ?? builder.Configuration["Clips:MaxClipDurationSecs"];
        if (int.TryParse(dur, out var secs) && secs > 0) opts.MaxClipDurationSecs = secs;
    })
    .Validate(o => o.MaxUploadSizeMb is > 0 and <= 5000,
        "MAX_UPLOAD_SIZE_MB must be in [1, 5000].")
    .Validate(o => o.MaxClipDurationSecs is > 0 and <= 3600,
        "MAX_CLIP_DURATION_SECS must be in [1, 3600].")
    .ValidateOnStart();

builder.Services.AddScoped<IClipUploadService, ClipUploadService>();

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

builder.Services.AddSingleton<IJwtService, JwtService>();
builder.Services.AddSingleton<IStateCookieService, StateCookieService>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();
builder.Services.AddScoped<UserUpsertService>();

builder.Services.AddSingleton<IOAuthProvider, DiscordOAuthProvider>();
builder.Services.AddSingleton<IOAuthProvider, GoogleOAuthProvider>();
builder.Services.AddSingleton<OAuthProviderRegistry>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

// Reuse the same TokenValidationParameters instance JwtService uses so the two sides
// can't drift (NameClaimType, ClockSkew, issuer/audience, signing key).
builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
    {
        bearer.TokenValidationParameters = JwtService.BuildValidationParameters(jwtOptions.Value);
    });

builder.Services.AddAuthorization();

const string corsPolicy = "WebOrigin";
// We register the CORS policy via AddOptions<CorsOptions>().Configure<IOptions<OAuthOptions>>
// instead of the idiomatic AddCors(o => o.AddPolicy(...)) because the policy's origin comes
// from the already-bound OAuthOptions — the AddCors lambda overload can't inject IOptions<T>.
// AddCors() below wires the middleware + ICorsService; our ConfigureOptions adds the policy.
builder.Services
    .AddOptions<CorsOptions>()
    .Configure<IOptions<OAuthOptions>>((cors, oauth) =>
    {
        cors.AddPolicy(corsPolicy, policy => policy
            .WithOrigins(oauth.Value.WebOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
    });
builder.Services.AddCors();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors(corsPolicy);
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapMeEndpoints();
app.MapClipsUploadEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapDevAuthEndpoints();
}

app.Run();

public partial class Program;
