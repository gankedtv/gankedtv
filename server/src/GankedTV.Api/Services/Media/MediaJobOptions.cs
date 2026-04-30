namespace GankedTV.Api.Services.Media;

public sealed class MediaJobOptions
{
    public bool Enabled { get; set; } = true;

    // How often the worker polls when the queue is empty. Reads stay cheap on the
    // partial idx_clips_processing_updated_at index, so a tight loop is fine.
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    // A claimed row is hidden from other workers until ProcessingStartedAt expires.
    // Set comfortably above the longest expected ffmpeg run so a healthy job is never
    // double-claimed; if a worker crashes mid-job, the next worker picks it up after
    // this window.
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    // Maximum number of times a row is claimed before it lands in 'failed'. The first
    // run is attempt 1, so MaxAttempts=3 means up to two retries after the initial try.
    public int MaxAttempts { get; set; } = 3;

    // Cap on jobs processed per tick. The drain loop normally runs until the queue
    // is empty, but a recovery scenario (e.g. worker resumes after a crash with a
    // large backlog) shouldn't monopolize the process for minutes — yield back to
    // the timer so other hosted services and graceful shutdown stay responsive.
    public int MaxDrainPerTick { get; set; } = 100;

    // Path to the binaries — overridable via env when ffmpeg lives somewhere unusual
    // (e.g. a sidecar container or a custom build with NVENC support).
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";

    // Hard ceiling on a single ffmpeg/ffprobe invocation. Frame extraction at ~1s for
    // a few-MB clip should be fast; anything that exceeds this budget is treated as a
    // hung process and force-killed.
    public TimeSpan ProcessTimeout { get; set; } = TimeSpan.FromMinutes(2);

    // Where in the clip to grab the thumbnail frame. Falls back to the keyframe at 0
    // if the clip is shorter than this.
    public TimeSpan ThumbnailFrameOffset { get; set; } = TimeSpan.FromSeconds(1);
}
