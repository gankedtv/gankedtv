namespace GankedTV.Api.Services.Media;

public interface IJitLadderService
{
    // Transcodes a clip's stored master into a source-capped H.264 HLS ladder
    // (master.m3u8 + variant playlists + segments) and uploads it to the public-read
    // stream-cache bucket under the clip's prefix. Used at watch time for devices that
    // can't decode the master's codec (e.g. AV1). Returns the master playlist's object key.
    Task<string> BuildAsync(ClaimedStreamJob job, CancellationToken ct);
}
