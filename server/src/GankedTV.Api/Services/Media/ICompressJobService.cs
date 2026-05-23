namespace GankedTV.Api.Services.Media;

// Result of compressing one clip's upload into a single stored master.
public sealed record CompressionResult(string VideoKey, string VideoCodec, string OriginalKey);

public interface ICompressJobService
{
    // Compresses the claimed clip's current master (the raw upload) into one efficient,
    // resolution-capped file, uploads it to the clips bucket under a new key, and returns the
    // new key + codec + the original key. Does NOT mutate the clip row or delete the original
    // (the worker completes the DB transition first, then deletes — so a crash never leaves the
    // row pointing at a deleted object).
    Task<CompressionResult> CompressAsync(ClaimedMediaJob job, CancellationToken ct);
}
