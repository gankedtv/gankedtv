using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Tests.Integration.Storage;

// End-to-end thumbnail-worker round trip against a real S3-compatible storage container
// AND real ffmpeg. The substituted IFfmpegRunner used in ThumbnailJobServiceTests can't
// catch bugs that surface only against real ffmpeg/ffprobe output (probe JSON shape
// mismatches, presigned-URL incompatibilities with ffmpeg's HTTP demuxer, or the worker
// uploading the thumbnail to the wrong bucket/key). This test does the full path:
// real mp4 → presigned PUT → POST /complete → run worker → real ffmpeg extracts frame →
// real S3 PUT for the thumbnail → assert via real S3 HEAD.
//
// Requires ffmpeg + ffprobe on PATH (server.yml CI installs them; local dev already
// does per CLAUDE.md).
[Collection("PostgresAndS3")]
public class ThumbnailRoundTripTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private readonly S3Fixture _s3;
    private AuthApiFactory? _factory;

    public ThumbnailRoundTripTests(PostgresFixture pg, S3Fixture s3)
    {
        _pg = pg;
        _s3 = s3;
    }

    public async Task InitializeAsync()
    {
        await _pg.ResetAsync();
        await _s3.ResetAsync();
        _factory = new AuthApiFactory(_pg.ConnectionString, s3Fixture: _s3);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Worker_ProcessesCompletedClip_ExtractsThumbnail_FlipsToReady()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"gankedtv-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var mp4Path = Path.Combine(tempDir, "input.mp4");
        try
        {
            await GenerateTestMp4Async(mp4Path);
            var mp4Bytes = await File.ReadAllBytesAsync(mp4Path);

            var (userId, token) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_pg, _factory!);
            using var client = AuthTestHelpers.CreateBearerClient(_factory!, token);

            // Create draft clip
            var createResp = await client.PostAsJsonAsync("/clips", new
            {
                title = "thumb-roundtrip",
                description = (string?)null,
                gameId = (int?)null,
                visibility = "public",
            });
            createResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var clipId = (await createResp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id").GetGuid();

            // Get presigned PUT
            var urlResp = await client.PostAsync($"/clips/{clipId}/upload-url", content: null);
            urlResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var upload = await urlResp.Content.ReadFromJsonAsync<JsonElement>();
            var presignedPut = upload.GetProperty("url").GetString()!;
            var contentType = upload.GetProperty("contentType").GetString()!;

            // PUT real mp4 bytes
            using (var rawClient = new HttpClient())
            {
                using var putContent = new ByteArrayContent(mp4Bytes);
                putContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                var putResp = await rawClient.PutAsync(presignedPut, putContent);
                putResp.StatusCode.Should().Be(HttpStatusCode.OK,
                    "MinIO should accept the SigV4-signed PUT for a real mp4");
            }

            // POST /complete → flips to Processing + enqueues a media job
            var completeResp = await client.PostAsync($"/clips/{clipId}/complete", content: null);
            completeResp.StatusCode.Should().Be(HttpStatusCode.OK);
            await using (var db = _pg.CreateContext())
            {
                var status = await db.Clips.AsNoTracking()
                    .Where(c => c.Id == clipId).Select(c => c.Status).FirstAsync();
                status.Should().Be(ClipStatuses.Processing);
            }

            // Run one worker tick manually. The factory removed all hosted services, so
            // we instantiate MediaJobHostedService here with the real IFfmpegRunner that
            // the API would have used in production.
            var sp = _factory!.Services;
            var worker = new MediaJobHostedService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IFfmpegRunner>(),
                sp.GetRequiredService<IOptionsMonitor<MediaJobOptions>>(),
                NullLogger<MediaJobHostedService>.Instance);

            var processed = await worker.TryProcessOneAsync(CancellationToken.None);
            processed.Should().BeTrue("the just-completed clip is claimable");

            // Assert: clip flipped to Ready, ThumbnailKey set, thumbnail blob present in real S3.
            string? thumbnailKey;
            await using (var db = _pg.CreateContext())
            {
                var clip = await db.Clips.AsNoTracking().FirstAsync(c => c.Id == clipId);
                clip.Status.Should().Be(ClipStatuses.Ready);
                clip.ThumbnailKey.Should().NotBeNullOrEmpty();
                thumbnailKey = clip.ThumbnailKey;
            }
            // BuildThumbnailKey for no-game uploads is {userId}/{clipId}.jpg — same shape
            // pinned by OrphanSweepStorageTests for the video key.
            thumbnailKey.Should().Be($"{userId}/{clipId}.jpg");

            var meta = await _s3.AdminClient.GetObjectMetadataAsync(
                S3Fixture.ThumbnailsBucket, thumbnailKey!);
            meta.HttpStatusCode.Should().Be(HttpStatusCode.OK);
            meta.Headers?.ContentType.Should().Be("image/jpeg");
            meta.ContentLength.Should().BeGreaterThan(0);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (IOException) { /* best effort — temp dir cleanup must not mask the real result */ }
        }
    }

    // Generates a tiny (~few KB) mp4 via ffmpeg's lavfi testsrc source so the round-trip
    // doesn't depend on a checked-in binary asset. ultrafast preset keeps generation
    // sub-second; 64×48 / 2 fps / 1s duration is enough for ffprobe to read dimensions
    // and for the worker's seek-and-extract to land a frame.
    private static async Task GenerateTestMp4Async(string outputPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("testsrc=duration=1:size=64x48:rate=2");
        psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("ultrafast");
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add(outputPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg");
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var stderr = await stderrTask;
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg failed to generate test mp4 (exit {proc.ExitCode}): {stderr}");
        }
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new InvalidOperationException($"ffmpeg exited 0 but no mp4 at {outputPath}");
        }
    }
}
