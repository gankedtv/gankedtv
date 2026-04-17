using Amazon.S3;
using GankedTV.Api.Data;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
    opts.PublicUrl = Environment.GetEnvironmentVariable("S3_PUBLIC_URL") ?? builder.Configuration["Minio:PublicUrl"];
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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
