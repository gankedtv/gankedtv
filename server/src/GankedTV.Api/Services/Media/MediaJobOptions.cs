namespace GankedTV.Api.Services.Media;

public sealed class MediaJobOptions
{
    public bool Enabled { get; set; } = true;

    // How often the worker polls when the queue is empty. Reads stay cheap on the
    // partial idx_clips_processing_updated_at index, so a tight loop is fine.
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    // A claimed row is hidden from other workers until ProcessingStartedAt expires. MUST stay
    // comfortably above the longest expected ffmpeg run (TranscodeTimeout, 10min) so an
    // in-flight compress/JIT encode is never re-claimed mid-run — two workers writing the same
    // deterministic output key would otherwise flap the object and waste GPU. Trade-off: a
    // genuinely crashed job isn't retried until this window elapses.
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(15);

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

    // --- Compression + just-in-time playback (issue #102) -------------------------------
    // The pipeline runs as two upload-time queue stages — thumbnail (status 'processing') and
    // compress (status 'transcoding') — plus a watch-time JIT stage (clip_stream_jobs). The
    // GPU-heavy work (compress + JIT) is gated by TranscodeWorkerEnabled so it can run on a
    // separate host: the DB queues use FOR UPDATE SKIP LOCKED, so the main API server can run
    // the thumbnail worker only while a GPU box runs the compress + JIT workers.

    // Whether compression is part of the pipeline. Controls the thumbnail stage's success
    // transition: true → 'transcoding' (compress stage runs next); false → straight to 'ready'
    // (store the raw upload as-is). Independent of whether *this* instance runs the workers.
    public bool TranscodeEnabled { get; set; } = true;

    // Whether this instance runs the thumbnail BackgroundService (claims 'processing').
    public bool ThumbnailWorkerEnabled { get; set; } = true;

    // Whether this instance runs the GPU workers — the compress stage (claims 'transcoding')
    // and the JIT stream-rendition stage (claims pending clip_stream_jobs).
    public bool TranscodeWorkerEnabled { get; set; } = true;

    // Hard ceiling on a single ffmpeg encode (compress or a JIT ladder). Far larger than
    // ProcessTimeout because a full encode takes much longer than a one-frame thumbnail.
    public TimeSpan TranscodeTimeout { get; set; } = TimeSpan.FromMinutes(10);

    // --- Compressed master (stored, the disk win) ---
    // Encoder for the single stored master. 'libx264' for dev/GPU-less; the GPU box overrides
    // to 'av1_nvenc' (max savings) or 'libsvtav1' for software AV1. Audio is always AAC.
    public string VideoEncoder { get; set; } = "libx264";

    // Codec label persisted on the clip (drives the web player's native-vs-JIT decision).
    // Should match VideoEncoder's output family — e.g. 'av1' for av1_nvenc/libsvtav1.
    public string VideoCodec { get; set; } = "h264";

    // Cap the master's height (never upscale); oversized uploads (4K phone clips) shrink to
    // this. Quality target: CRF for libx264/libsvtav1, mapped to -cq for *_nvenc.
    public int MaxHeight { get; set; } = 1080;
    public int Crf { get; set; } = 30;

    // --- JIT H.264 ladder (transient, watch-time) ---
    // Encoder for the on-demand compatibility ladder — always an H.264 family for universal
    // playback ('libx264' dev, 'h264_nvenc' on the GPU box).
    public string JitVideoEncoder { get; set; } = "libx264";

    // Target segment length for the JIT HLS renditions (ffmpeg -hls_time).
    public TimeSpan SegmentDuration { get; set; } = TimeSpan.FromSeconds(6);

    // HLS segment container for the JIT ladder (ffmpeg -hls_segment_type). 'mpegts' (.ts) is
    // the universal H.264 choice. (Cache eviction TTL lives on S3Options.StreamCacheTtlDays —
    // it's a bucket-lifecycle concern.)
    public string SegmentType { get; set; } = "mpegts";

    // JIT rendition ladder, highest-first. Source-capped at transcode time: rungs taller than
    // the source are skipped (never upscale). Defaults to a 1080/720/480 H.264 ladder.
    public List<HlsRung> Ladder { get; set; } =
    [
        new HlsRung { Height = 1080, VideoKbps = 5000, MaxrateKbps = 5350, AudioKbps = 128 },
        new HlsRung { Height = 720, VideoKbps = 2800, MaxrateKbps = 2996, AudioKbps = 128 },
        new HlsRung { Height = 480, VideoKbps = 1400, MaxrateKbps = 1498, AudioKbps = 96 },
    ];

    // --- URL import (issue #106) --------------------------------------------------------
    // The /clips/import endpoint inserts a row with status='importing' and returns. The
    // ImportWorker claims it, shells out to yt-dlp to fetch the source, writes the bytes to
    // the clips bucket, and advances the row to 'processing' — at which point the existing
    // thumbnail → compress → ready pipeline takes over with zero downstream changes. Size
    // and duration caps come from ClipValidationOptions (same caps as direct upload).
    public ImportOptions Import { get; set; } = new();
}

public sealed class ImportOptions
{
    // Pipeline-level toggle (parallels TranscodeEnabled). When false the endpoint 503s and the
    // worker exits at startup. Lets operators disable URL ingestion entirely without removing
    // yt-dlp from the host.
    public bool Enabled { get; set; } = true;

    // Whether this instance runs the ImportWorker BackgroundService. The fetch is I/O-bound
    // (network + S3 PUT), so it belongs on the API host; the GPU box leaves this off.
    public bool WorkerEnabled { get; set; } = true;

    // Host-only allow-list (case-insensitive). The validator rejects any URL whose host isn't
    // a member, both at the endpoint (fast 400) and inside the worker (defence in depth
    // against config drift between submit and dequeue).
    public List<string> AllowedHosts { get; set; } =
    [
        "youtube.com", "www.youtube.com", "m.youtube.com", "youtu.be",
        "medal.tv", "www.medal.tv",
    ];

    // Path to the yt-dlp binary — overridable via YTDLP_PATH for hosts where it lives outside
    // the default PATH (e.g. a sidecar virtualenv install).
    public string YtdlpPath { get; set; } = "yt-dlp";

    // Hard ceiling on a single fetch — far larger than ProcessTimeout because a 500 MB pull
    // over a slow extractor can legitimately take several minutes. A hung yt-dlp is killed
    // and the row is released for retry.
    public TimeSpan FetchTimeout { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed class HlsRung
{
    // Target rendition height in pixels; width is derived from the source aspect ratio
    // (scale=-2:{Height}) so it stays even and undistorted.
    public int Height { get; set; }
    public int VideoKbps { get; set; }
    public int MaxrateKbps { get; set; }
    public int AudioKbps { get; set; }
}
