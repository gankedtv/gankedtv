using Amazon.S3;
using GankedTV.Api.Auth;
using GankedTV.Api.Auth.Jwt;
using GankedTV.Api.Auth.Providers;
using GankedTV.Api.Auth.State;
using GankedTV.Api.Auth.Tokens;
using GankedTV.Api.Data;
using GankedTV.Api.Endpoints;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
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
    opts.Endpoint  = Environment.GetEnvironmentVariable("S3_ENDPOINT")   ?? builder.Configuration["Minio:Endpoint"]  ?? "http://localhost:9000";
    opts.AccessKey = Environment.GetEnvironmentVariable("S3_ACCESS_KEY") ?? builder.Configuration["Minio:AccessKey"] ?? "minioadmin";
    opts.SecretKey = Environment.GetEnvironmentVariable("S3_SECRET_KEY") ?? builder.Configuration["Minio:SecretKey"] ?? "minioadmin";
    // .env.example ships `S3_PUBLIC_URL=` (empty); treat empty/whitespace as unset so the config fallback wins.
    var envPublic  = Environment.GetEnvironmentVariable("S3_PUBLIC_URL");
    opts.PublicUrl = !string.IsNullOrWhiteSpace(envPublic) ? envPublic : builder.Configuration["Minio:PublicUrl"];
    var clips      = builder.Configuration["Minio:ClipsBucket"];
    var thumbs     = builder.Configuration["Minio:ThumbnailsBucket"];
    if (!string.IsNullOrWhiteSpace(clips))  opts.ClipsBucket = clips;
    if (!string.IsNullOrWhiteSpace(thumbs)) opts.ThumbnailsBucket = thumbs;
});

builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var o = sp.GetRequiredService<IOptions<MinioOptions>>().Value;
    return new AmazonS3Client(o.AccessKey, o.SecretKey, new AmazonS3Config
    {
        ServiceURL = o.Endpoint,
        ForcePathStyle = true,
    });
});

builder.Services.AddSingleton<IObjectStorageService, MinioObjectStorageService>();
builder.Services.AddHostedService<BucketBootstrapHostedService>();

// ---- Auth configuration ----

builder.Services.Configure<JwtOptions>(opts =>
{
    opts.Secret   = Environment.GetEnvironmentVariable("JWT_SECRET")   ?? builder.Configuration["Jwt:Secret"]   ?? "";
    opts.Issuer   = Environment.GetEnvironmentVariable("JWT_ISSUER")   ?? builder.Configuration["Jwt:Issuer"]   ?? "gankedtv";
    opts.Audience = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? builder.Configuration["Jwt:Audience"] ?? "gankedtv-web";
    var expiry    = Environment.GetEnvironmentVariable("JWT_EXPIRY_MINUTES") ?? builder.Configuration["Jwt:ExpiryMinutes"];
    if (int.TryParse(expiry, out var mins) && mins > 0) opts.ExpiryMinutes = mins;
});

builder.Services.Configure<RefreshTokenOptions>(opts =>
{
    var days = Environment.GetEnvironmentVariable("REFRESH_TOKEN_EXPIRY_DAYS") ?? builder.Configuration["Jwt:RefreshTokenExpiryDays"];
    if (int.TryParse(days, out var d) && d > 0) opts.ExpiryDays = d;
});

builder.Services.Configure<OAuthOptions>(opts =>
{
    opts.StateSecret = Environment.GetEnvironmentVariable("OAUTH_STATE_SECRET") ?? builder.Configuration["OAuth:StateSecret"] ?? "";
    opts.WebOrigin   = Environment.GetEnvironmentVariable("WEB_ORIGIN")         ?? builder.Configuration["OAuth:WebOrigin"]   ?? "http://localhost:5173";
    opts.Discord.ClientId     = Environment.GetEnvironmentVariable("DISCORD_CLIENT_ID")     ?? builder.Configuration["OAuth:Discord:ClientId"]     ?? "";
    opts.Discord.ClientSecret = Environment.GetEnvironmentVariable("DISCORD_CLIENT_SECRET") ?? builder.Configuration["OAuth:Discord:ClientSecret"] ?? "";
    opts.Discord.RedirectUri  = Environment.GetEnvironmentVariable("DISCORD_REDIRECT_URI")  ?? builder.Configuration["OAuth:Discord:RedirectUri"]  ?? "";
    opts.Google.ClientId      = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID")      ?? builder.Configuration["OAuth:Google:ClientId"]      ?? "";
    opts.Google.ClientSecret  = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET")  ?? builder.Configuration["OAuth:Google:ClientSecret"]  ?? "";
    opts.Google.RedirectUri   = Environment.GetEnvironmentVariable("GOOGLE_REDIRECT_URI")   ?? builder.Configuration["OAuth:Google:RedirectUri"]   ?? "";
});

builder.Services.AddHttpClient(DiscordOAuthProvider.ProviderName);
builder.Services.AddHttpClient(GoogleOAuthProvider.ProviderName);

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

builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
        };
    });

builder.Services.AddAuthorization();

var corsPolicy = "WebOrigin";
builder.Services.AddCors(options =>
{
    options.AddPolicy(corsPolicy, policy =>
    {
        var origin = Environment.GetEnvironmentVariable("WEB_ORIGIN")
            ?? builder.Configuration["OAuth:WebOrigin"]
            ?? "http://localhost:5173";
        policy.WithOrigins(origin)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

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

app.Run();

public partial class Program;
