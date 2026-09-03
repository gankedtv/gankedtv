# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

GankedTV is a social media platform for sharing gaming clips. It uses a monorepo structure with separate `server/` (.NET) and `web/` (Vue) directories.

## Tech Stack

- **Server:** C# / .NET 10, Entity Framework Core (PostgreSQL), AWS S3 SDK
- **Web:** Vue 3, TypeScript, Vite, Vitest, Bun
- **Infrastructure:** PostgreSQL, MinIO (S3-compatible storage)

## Development Commands

### Infrastructure
```bash
make up                       # Start PostgreSQL and MinIO
make down                     # Stop infrastructure
make clean                    # Stop and remove volumes
make logs                     # View infrastructure logs
make seed                     # Dev seed: test user + sample clips + placeholder game covers
make import-games             # Backfill games catalog + cover art from IGDB (needs IGDB creds)
```

`make import-games` pulls the most popular games from IGDB, mirrors each cover into the
`game-covers` MinIO bucket (anonymous-read; `cover_url` holds a stable public URL), and upserts
rows keyed by `igdb_id` (curated seed rows are adopted by name, not duplicated). It's idempotent
and resumable, and requires `IGDB_CLIENT_ID` / `IGDB_CLIENT_SECRET` (a Twitch app's
client-credentials pair). A fresh dev DB renders ffmpeg-generated placeholder covers via
`make seed`, so IGDB credentials are not needed for local development.

An optional background re-sync (`IgdbSyncHostedService`) keeps the catalog current — it re-runs
the same importer on a timer, re-downloading a cover only when IGDB's `image_id` changed and
renaming importer-managed games (curated seeds are never renamed). It's **off by default**;
enable per-environment with `IGDB_SYNC_ENABLED=true` (interval via `IGDB_SYNC_INTERVAL_DAYS`,
default 7). No-op without credentials.

The catalog is also **self-healing on the request path**: when `GET /games?search=` misses locally
for an *authenticated* caller, `GameSearchImportService` looks the term up on IGDB and reconciles
matches through the same importer, so long-tail games (outside the popularity window) are pickable
on first search. This means **prod makes outbound IGDB calls while serving a user request** — it's
rate limited (`games-search`), memoized per term, bounded by an 8s budget, and degrades to the plain
local result on any IGDB failure. Without `IGDB_CLIENT_ID` / `IGDB_CLIENT_SECRET` it's a no-op, and
the picker is then limited to whatever `make import-games` last pulled in.

### Host requirements

The dev workflow runs `dotnet watch` on the host, which means the API process — including the thumbnail-generation worker (issue #57) — runs on your host directly. Install these locally:

- **`dotnet`** (SDK matching `<TargetFramework>` in [server/src/GankedTV.Api/GankedTV.Api.csproj](server/src/GankedTV.Api/GankedTV.Api.csproj)).
- **`ffmpeg`** (provides `ffmpeg` and `ffprobe`) — required for the thumbnail worker. Without it, `POST /clips/{id}/complete` enqueues the clip but the worker fails on every attempt and the clip eventually lands in `status='failed'`.
  - Arch/CachyOS: `sudo pacman -S ffmpeg`
  - Debian/Ubuntu: `sudo apt install ffmpeg`
  - macOS: `brew install ffmpeg`
  - Override the binary location with `FFMPEG_PATH` / `FFPROBE_PATH` env vars when not on `$PATH`.
- **`yt-dlp`** — required for the URL-import worker (issue #106) that fetches Medal.tv / YouTube clips on behalf of `POST /clips/import`. Without it, imports stay in `status='importing'` and eventually land in `status='failed'`; you can also disable the feature with `MEDIA_IMPORT_ENABLED=false` (the endpoint then 503s).
  - Arch/CachyOS: `sudo pacman -S yt-dlp`
  - Debian/Ubuntu: `sudo apt install yt-dlp`
  - macOS: `brew install yt-dlp`
  - Override the binary location with `YTDLP_PATH` when not on `$PATH`.
- **`bun`** for the web app.

[server/Dockerfile.api](server/Dockerfile.api) ships an API image with both **ffmpeg** and **yt-dlp** pre-installed (`YTDLP_PATH` overrides the binary location) — it's there for production / CI parity builds, not used by the dev compose stack.

### Media pipeline: compress-in-place + just-in-time playback (issue #102)

The pipeline is built to **minimise persistent storage** (Tdarr-style): each clip keeps exactly one
efficiently-compressed master on disk; adaptive HLS ladders are generated **on demand at watch
time** and cached transiently, never stored permanently. Workers extend the generic
`MediaStageWorker<TJob>` ([server/src/GankedTV.Api/Services/Media/MediaStageWorker.cs](server/src/GankedTV.Api/Services/Media/MediaStageWorker.cs)).

**Upload-time stages** (status flow `draft → processing → transcoding → ready`):

1. **Thumbnail** (`ThumbnailWorker`, claims `processing`) — poster + ffprobe metadata, then advances to `transcoding` (or straight to `ready` when `TranscodeEnabled=false`).
2. **Compress** (`CompressWorker` → `CompressJobService`, claims `transcoding`) — re-encodes the raw upload into ONE resolution-capped, quality-targeted master (AV1 on the GPU box, H.264 in dev), repoints the clip's `video_key` at it, records `video_codec`, **deletes the original**, advances to `ready`. Net disk per clip goes *down*. If a hardware (`*_nvenc`) encode fails to open the encoder (ffmpeg newer than the host NVIDIA driver, busy/absent GPU), it retries once with the software encoder of the same codec family (`av1_nvenc`→`libsvtav1`) so uploads don't hard-fail the whole clip — toggle via `MEDIA_HARDWARE_ENCODER_FALLBACK_ENABLED` (default `true`).

**Watch-time JIT stage** (no persisted ladder):

- The clip detail returns the presigned master `videoUrl` + `videoCodec`. The web player plays the master directly when the browser can decode it (H.264 always; AV1 on capable devices).
- Otherwise the player calls `GET /clips/{id}/stream`: a cache hit returns the public master-playlist URL; a miss enqueues a `clip_stream_jobs` row (202, client polls). `StreamRenditionWorker` → `JitLadderService` transcodes the master → H.264 HLS ladder into the anonymous-read **`stream-cache`** bucket, which auto-evicts via a lifecycle rule (`StreamCacheTtlDays`, default 14). A re-watch after eviction simply re-enqueues. Because clips are short, a whole-clip transcode is fast — no segment-level JIT needed.

Failures from any stage respect `MaxAttempts` and never wedge the worker. **Trade-off (intentional, Tdarr-style):** deleting the original means re-encodes come from a lossy master.

Both GPU stages (compress + JIT) are **location-independent** — the queues use `FOR UPDATE SKIP LOCKED`, so they can run on a separate GPU host. Controlled by env toggles ([MediaJobOptions.cs](server/src/GankedTV.Api/Services/Media/MediaJobOptions.cs)):

| Instance | `MEDIA_TRANSCODE_ENABLED` | `MEDIA_THUMBNAIL_WORKER_ENABLED` | `MEDIA_TRANSCODE_WORKER_ENABLED` | `MEDIA_VIDEO_ENCODER` | `MEDIA_JIT_VIDEO_ENCODER` |
|---|---|---|---|---|---|
| Main API server | `true` | `true` | `false` | — | — |
| GPU box (TrueNAS + NVENC) | `true` | `false` | `true` | `av1_nvenc` (+`MEDIA_VIDEO_CODEC=av1`) | `h264_nvenc` |
| No-compress (store upload as-is) | `false` | `true` | `false` | — | — |

In dev, all workers run **in-process** on the host (toggles default `true`), using host ffmpeg — no new container. The master encoder (`MEDIA_VIDEO_ENCODER`/`MEDIA_VIDEO_CODEC`), JIT encoder (`MEDIA_JIT_VIDEO_ENCODER`), resolution cap (`MEDIA_MAX_HEIGHT`), and quality (`MEDIA_CRF`) are all configurable, so moving the GPU box to AV1 is a config change, not code.

**Pre-upload trimming + cropping:** the web upload wizard's `'edit'` step ("Trim & crop") tabs between `ClipTrimmer.vue` and `ClipCropper.vue` (only the active tab is mounted, so one `<video>` decodes at a time) and sends one optional body on `POST /clips/{id}/complete` carrying whichever operations the user set. The cut is applied by the **existing compress stage** (`-ss`/`-t` on the single re-encode — no extra encode); the thumbnail stage clamps the range to the probed duration, takes the poster inside the kept range, and records the trimmed duration. Trim requires `MEDIA_TRANSCODE_ENABLED=true` (400 `trim_unavailable` otherwise) and is **web-only**: API-key callers (rewynd trims locally) get 400 `trim_not_supported`; a body-less complete is unchanged.

**Cropping (`crop_x`/`crop_y`/`crop_width`/`crop_height`):** ultrawide captures bake pillarbox bars
into the recording, so both write paths accept an optional crop rect. It rides the **same single
compress re-encode** as the trim — no extra encode stage — and is expressed as **normalized 0..1
fractions of the current master's frame, never pixels**: `/complete` records the request before
anything has been probed, and the master is rescaled by `MEDIA_MAX_HEIGHT` on every edit generation,
so a pixel rect would silently mean something different after each `.cmp{n}`.

- `MediaFilters.Crop(CropRect)` builds the `crop=…` filter in ffmpeg's `iw`/`ih` expression
  language and is shared by the **poster and the master**, so the two can never drift — the feed
  renders the poster, so a poster that kept the bars would make the feature look broken where most
  people look. It can't be pre-computed pixels: `ClaimedMediaJob.SourceHeight` is `clips.height`,
  which `AdvanceThumbnailAsync` has already overwritten with the *post-crop* height by the time
  `CompressWorker` claims the row.
- The thumbnail stage's `SanitizeCrop` snaps the rect against the probed frame (fractions → pixels →
  clamp → even-snap → fractions) and writes back **post-crop `width`/`height`** — taken from its own
  pixel arithmetic, never re-derived from the fractions it returns (`442/3440*3440` is
  `441.99999999999994`, which snaps 2px small on ~3.5% of widths). The compress stage composes one
  `-vf` slot, **crop before scale**, and judges the `MEDIA_MAX_HEIGHT` cap against the frame the clip
  *would* have had uncropped (`clips.height / crop_height`) — on the post-crop height alone a
  wide/short crop skips the scale entirely and ships a master with up to twice the pixels of the
  same clip published whole.
- Cropping the poster needs **both** `MEDIA_CROP_ENABLED` and `MEDIA_TRANSCODE_ENABLED`, since with
  compression off the thumbnail stage advances straight to `ready` over a master nothing re-encodes.
  Every host in a split deployment must agree on those toggles or the poster and the master diverge.
- **Divergence from trim:** when ffprobe reports no source dimensions the crop is **dropped with a
  warning, not failed**. A dropped trim would publish footage the user cut away; a dropped crop just
  leaves the bars, and that's fixable post-publish.
- The web cropper **gates the emit behind a pre-checked "Crop this clip" toggle**. It still opens
  pre-framed to 16:9 on a wider-than-16:9 source, but mounting the component is not consent to
  destroy a quarter of the frame permanently — opening the tab to look and switching away used to
  publish the cut. Dragging, nudging, picking a preset or applying a suggestion all tick it; Escape
  unticks it.
- **API-key (rewynd) callers may crop on both routes** — there is no `crop_not_supported`. rewynd
  trims locally because that saves upload bytes; cropping pillarbox saves almost nothing (black
  encodes to near-zero bitrate) while costing a full re-encode on the user's gaming PC, and the
  server re-encodes anyway. `trim_not_supported` is unchanged.
- `GET /clips/{id}/crop-suggestion` (owner-only, allowed on `draft` *and* `ready`) runs ffmpeg's
  `cropdetect` at `MEDIA_CROPDETECT_SAMPLES` timestamps and combines them as a **union bounding box**, so a
  fade-to-black sample widens the suggestion back toward the full frame instead of eating real
  content. It ffprobes the frame size first: cropdetect's `x1`/`x2`/`y1`/`y2` are the bounds of the
  detected *content*, not of the frame (a 3440-wide pillarboxed source reports `x2:2999`), so
  deriving dimensions from them would normalize the rect by exactly the width of the bars being
  measured. Any failure returns `detected: false` — never a 5xx, never a stored side effect. It is
  deliberately **not** part of the thumbnail stage: detection costs ~1–3s on *every* upload, ~95% of
  which are plain 16:9, and the answer is only useful while a human is in the crop editor. The
  `draft` allowance is for **API-key uploaders** (create row → PUT object → ask → `/complete`);
  the web wizard can't use it, because it holds a local `File` and doesn't create the clip until
  `startUpload()`, so the post-publish crop dialog is the only place the web offers the button.
  Draft rows have no probed duration yet, so detection there takes a single sample at t=0 rather
  than re-running the same one N times.
- Toggles: `MEDIA_CROP_ENABLED` (default `true`; also requires `MEDIA_TRANSCODE_ENABLED`),
  `MEDIA_CROPDETECT_ENABLED` / `_SAMPLES` / `_LIMIT` / `_TIMEOUT_SECS`. Errors: `invalid_crop` (400),
  `crop_unavailable` (400), `crop_detect_unavailable` (503).
- `GET /clips/{id}` does **not** echo the crop back — the columns are a pending operation against the
  current master, not durable state, and echoing them would invite a client to double-crop.
  `width`/`height` already carry the post-crop aspect, and the web player now binds its
  `aspect-ratio` from them instead of a hard-coded `aspect-video`.

**Post-publish re-edit (`POST /clips/{id}/edit`):** owners can re-trim and/or re-crop a live clip
from the clip page — **one** kebab entry ("Trim & crop") opening `ClipVideoEditDialog.vue`, a tabbed
dialog over the clip's presigned master that sends both operations in one call. Splitting it into a
Trim dialog and a Crop dialog would walk the owner through two full re-encodes for the result
`/edit` exists to deliver in one. (`ClipEditDialog.vue` is the unrelated title/description/tags
dialog.)
Rather than add a second encode path, the endpoint walks the row **back to the head of the
pipeline** — new operations, lease/attempts/failure reason reset, `ready → processing` — so the
thumbnail stage re-posters inside the kept span and the compress stage applies both. Trim and crop
in one call means **one** generation of quality loss, not two.

`POST /clips/{id}/trim` stays as a thin forwarder with an identical body and response shape, so
shipped web and rewynd builds keep working. The endpoint **writes all six operation columns
unconditionally** — a crop-only edit that left the trim columns alone would re-apply the
already-applied range to the already-trimmed master and cut it twice.

Consequences worth knowing:

- Offsets are seconds into the **current master**, not the long-deleted raw upload, and each re-cut encodes from a lossy source (the same Tdarr-style trade-off as above).
- The clip **leaves `ready` for the duration of the re-encode**, so its detail route 404s and it drops out of feeds; `ClipView` reuses its existing "still processing" poll to ride that out. Feed cache and the cached JIT HLS ladder (keyed by clip id alone) are both dropped on request.
- `clips.edit_count` is the compressed master's **key generation** (`…{clipId}.cmp.mp4` → `.cmp1.mp4` → `.cmp2.mp4`), so an encode never writes over the master it replaces and the key doesn't grow a suffix per edit.
- `edit_count` also scopes the JIT ladder's cache prefix (`{clipId:N}/` → `{clipId:N}/e2/`), so a ladder a `StreamRenditionWorker` was already building from the pre-cut master can't be served once the cut lands. All generations stay under `{clipId:N}/`, so the existing delete-by-prefix purges still cover them.
- `clips.edited_at` is stamped on request and surfaces as `editedAt` on the clip detail → the web's **"Edited" badge**, visible to every viewer. Metadata-only edits (`PATCH /clips/{id}`) deliberately never set it.
- **A failed re-cut restores the frame the clip is published with.** `/edit` snapshots
  `duration_secs`/`width`/`height` into `pre_edit_*` before the thumbnail stage overwrites them with
  the post-edit values, and `MarkFailedAsync` restores from that snapshot. The duration is the reason
  it has to be a snapshot rather than arithmetic on the row: a `[2, 8]` cut says nothing about how
  long the source was. `CompleteCompressionAsync` clears the columns once an edit lands.
- **A failed re-cut never takes a live clip dark.** `MarkFailedAsync` detects a re-cut (`edited_at` is only ever stamped on a published clip) and rolls the row back to `ready` with the pending range cleared, instead of `failed` — the previous master is still in storage because compress deletes the old object only after a successful swap. First-publish failures are untouched.
- Rejected with 409 `invalid_state` unless the clip is `ready` (which also serialises concurrent re-edits), 403 `moderated` for hidden clips, 400 `trim_unavailable` / `crop_unavailable` when the relevant toggle is off, and 400 `no_operations` for a body that asks for nothing (requeuing a full re-encode to apply no change would burn a generation of quality for free).

### Online presence (`GET /presence/summary`)

Powers the nav's live "N online" count (web wiring lands with the Arena design-system work). Returns `{ online, followsOnline: UserSummary[] }` — `followsOnline` (capped at `PRESENCE_FOLLOWS_ONLINE_CAP`) only for authenticated callers.

**Delivery is polling, not SSE/WebSocket** — a 30–60s client poll is plenty for a vanity count and needs no persistent connections. The `GET` itself **records the caller then returns the summary**, so the client's poll doubles as its heartbeat (no separate heartbeat endpoint, no per-request middleware). A viewer is "online" if seen within `PRESENCE_WINDOW_SECONDS` (default 120; keep it above the client poll interval). Viewer identity: `u:{userId}` when authenticated, else `?cid=<stable-id>` (the future web client sends a per-browser id), else `ip:{addr}`.

Storage mirrors the rate limiter ([PresenceTracker.cs](server/src/GankedTV.Api/Services/Presence/PresenceTracker.cs)): a Redis sorted set when `REDIS_URL` is set (cluster-wide), an in-process map otherwise or after any Redis failure (per-pod, never 500). Toggle with `PRESENCE_ENABLED` (default `true`; disabled → `503`). The endpoint is per-IP rate limited (anonymous write) via `PresenceRateLimiting`. **Caveat:** anonymous IP keying collapses behind a reverse proxy without `UseForwardedHeaders`; the `?cid=` path avoids that.

### Parallel worktrees

For working on multiple issues in parallel, `./scripts/new-worktree.sh <issue>` (also exposed as `/worktree <n>`) creates `.worktrees/issue-<n>/` inside the repo with its own dev stack on offset ports — deterministic per-issue, derived from a SHA1 of the issue number. The script writes a `.env.worktree.local` (auto-loaded by the Makefile and passed through to `docker compose` via `--env-file`) which sets `COMPOSE_PROJECT_NAME=gankedtv-issue-<n>` so containers and volumes are visibly scoped to this codebase. It then runs the equivalent of `make setup` minus the destructive clean, and opens the worktree in a new editor window — defaults to `code`, override with `WORKTREE_EDITOR=cursor` (or any VS Code-family CLI), or `WORKTREE_NO_OPEN=1` to skip. Use `make ports` from inside a worktree to re-read its URLs after the bootstrap scrollback is gone. Tear down with `./scripts/remove-worktree.sh <issue>` (or `/worktree-remove <n>`); teardown also deletes the local branch via `git branch -d` (safe; pass `--force` to escalate to `-D`). The `.worktrees/` directory is gitignored. The main checkout with no `.env.worktree.local` is unchanged — defaults still resolve to 5435 / 9000 / 9001 / 5050 / 5173.

### Git hooks
```bash
make hooks                    # One-time: install pre-push hook via core.hooksPath
```
Hook lives at `.githooks/pre-push` and mirrors CI, scoped to changed top-level dirs (`server/` or `web/`). Bypass with `PREPUSH_SKIP=1 git push` or `git push --no-verify`.

### CI mirror (run all CI checks locally)
```bash
make ci                       # Runs server + web CI checks (format, build, test+coverage)
make ci-server                # Server-only subset
make ci-web                   # Web-only subset
```
Mirrors `.github/workflows/server.yml` and `.github/workflows/web.yml` step-for-step. Pre-push hook stays separate — it scopes to changed dirs; `make ci` is the full matrix.

### Server (from repository root)
```bash
dotnet build server           # Build
dotnet test server            # Run all tests
dotnet test server --filter "FullyQualifiedName~TestClassName"  # Run specific tests
make server  # Run with hot reload
dotnet test server /p:CollectCoverage=true /p:Threshold=85%2C85  # Run with coverage + threshold gate (%2C is an escaped comma)
```

Server coverage gate: **85% line / 85% branch** (total), enforced by CI and the pre-push hook. Excluded from the denominator: EF migrations (`server/src/GankedTV.Api/Data/Migrations/**`), `*.generated.cs` (OpenAPI source-generator artifacts), and `Program.cs` (DI/bootstrap wiring, covered indirectly via `WebApplicationFactory` integration tests — see [server/src/GankedTV.Api/Program.Coverage.cs](server/src/GankedTV.Api/Program.Coverage.cs)). **Keep `Program.cs` to pure DI wiring + config binding;** any real logic (validators, feature checks, computation) belongs in a service so it stays inside the coverage denominator. Coverlet config lives in [server/tests/GankedTV.Api.Tests/GankedTV.Api.Tests.csproj](server/tests/GankedTV.Api.Tests/GankedTV.Api.Tests.csproj).

### Database Migrations (run from `server/`)
```bash
dotnet tool restore                                          # first time: restore dotnet-ef local tool
dotnet ef migrations add <Name> --project src/GankedTV.Api   # create a new migration
dotnet ef database update --project src/GankedTV.Api         # apply migrations to the local DB
```
Connection string comes from `DATABASE_URL` env var, falling back to `ConnectionStrings:DefaultConnection` in `appsettings.Development.json`.

### Web (from repository root)
```bash
cd web && bun install         # Install dependencies
cd web && bun dev             # Dev server (http://localhost:5173)
cd web && bun run build       # Production build
cd web && bun run lint        # Lint (oxlint + eslint with auto-fix)
cd web && bun run type-check  # TypeScript check
cd web && bun run test:unit   # Run tests (Vitest)
cd web && bun run test:unit -- --filter="test name"  # Run specific test
cd web && bun run test:coverage  # Run with coverage + threshold gate (scoped)
```

Web coverage gate: **85% line / 85% branch**, scoped to `src/api/**`, `src/router/**`, `src/stores/**` (HTTP client, auth, routing). Components, views, `App.vue`, `main.ts`, and `assets/` are deliberately excluded — the goal is protecting auth/network/routing logic, not display code. Coverage config lives in [web/vitest.config.ts](web/vitest.config.ts).

### Discord bot (from repository root)
```bash
make discord-install          # Install dependencies (bun install)
make discord                  # Run in watch mode (talks to host API + host Postgres)
make discord-test             # Run tests (bun test)
make discord-lint             # Lint (oxlint + eslint with auto-fix)
make ci-discord               # Full CI mirror (format/lint/type-check/coverage)
```

The bot lives in [discord/](discord/) as a sibling of `server/` and `web/`. It's **off by default** — without `DISCORD_BOT_TOKEN` + `DISCORD_BOT_APP_ID` it logs `disabled; exiting` on boot and no-ops (same contract as `IgdbSyncHostedService`). The compose service is gated behind the `discord` profile (`docker compose --profile discord up`) so `make up` doesn't build the Bun image by default. Stack is **TypeScript + Bun + discord.js**, with `postgres` for direct DB access. The bot owns its own tables (`discord_subscriptions`, `discord_post_log`, `discord_bot_state`) — EF Core does NOT model them, so they live in the shared Postgres without colliding with API migrations. The bot applies its own SQL migrations from `discord/src/migrations/*.sql` on boot.

**Postgres ≥ 15 required.** The initial migration uses `UNIQUE NULLS NOT DISTINCT` (added in PG15) to keep `(channel_id, NULL, NULL)` firehose subscriptions from duplicating. The dev compose stack runs PG18 so this is invisible locally; operators on shared older clusters need to upgrade or fork the migration.

Detection is **polling** (`GET /clips/feed` every `DISCORD_POLL_INTERVAL_SECONDS`, default 30s). Each round tracks a high-water-mark by `created_at` and uses a per-`(channel_id, clip_id)` post-log row as the dedupe guard, so a crash mid-fanout doesn't double-post on restart. A future upgrade to push-based delivery (webhook on `clip.ready`) would only swap the poller's entry-point — fanout, filters, dedupe, and command surface stay identical.

Slash commands ship in two namespaces:
- `/gankedtv subscribe|unsubscribe|subscriptions|pause|resume` — subscription CRUD per channel (gated to `ManageChannels`).
- `/clip latest|top|search` — on-demand clip pulls; works in any guild even without a subscription.

**Env loading:** the bot reads shared values from the repo-root [.env](.env) (mirrors web's `envDir: '../'` convention), then layers `discord/.env` on top (auto-loaded by Bun), then shell env wins. Precedence is first-set-wins, so the same `DATABASE_URL` you have at the root works automatically. See [discord/src/loadEnv.ts](discord/src/loadEnv.ts).

Discord coverage gate: **85% line / 85% function** — enforced by [discord/scripts/check-coverage.ts](discord/scripts/check-coverage.ts), which parses `coverage/lcov.info` and exits non-zero when under threshold (Bun's own `coverageThreshold` setting in [discord/bunfig.toml](discord/bunfig.toml) is documentation-only on Bun 1.3.13 — it prints but doesn't enforce). The script also emits a markdown summary to `$GITHUB_STEP_SUMMARY` in CI. Excluded from the denominator: `src/index.ts` (boot wiring, covered indirectly), `src/db.ts` + `src/api.ts` (I/O wrappers; integration tests would need testcontainers), `src/migrator.ts`, `src/migrations/`.

### Error monitoring (Sentry → self-hosted GlitchTip)

All three apps wire the official **Sentry SDK** to the self-hosted **GlitchTip** instance — server
(`Sentry.AspNetCore`, [Program.cs](server/src/GankedTV.Api/Program.cs) + [SentryPiiScrubber.cs](server/src/GankedTV.Api/Observability/SentryPiiScrubber.cs)),
web (`@sentry/vue`, [web/src/lib/sentry.ts](web/src/lib/sentry.ts)), and the bot (`@sentry/bun`,
[discord/src/sentry.ts](discord/src/sentry.ts)). It's **off by default** (no-op when the DSN is
unset, same opt-in as IGDB/Discord), **one GlitchTip project per app**, errors + light tracing
(`sendDefaultPii=false`, no replay/profiling/logs). Each app's DSN var is distinct so all three fit
one shared `.env`: API `SENTRY_*`, web `VITE_SENTRY_*`, bot `DISCORD_SENTRY_*` (prefixed because the
bot also reads the root `.env`). Full table in
[DEPLOYMENT.md](DEPLOYMENT.md#error-monitoring-sentry--self-hosted-glitchtip).

## Architecture

```
gankedtv/
├── server/                   # .NET 10 backend
│   ├── src/GankedTV.Api/     # Main API project
│   └── tests/GankedTV.Api.Tests/  # xUnit tests with FluentAssertions
├── web/                      # Vue 3 frontend (Bun + Vite)
│   └── src/
├── discord/                  # Discord bot (Bun + discord.js, off by default)
│   ├── src/
│   └── tests/
├── docker-compose.dev.yml    # PostgreSQL + MinIO for local dev
└── Makefile                  # Development commands
```

## Local Services

| Service    | URL                    | Credentials              |
|------------|------------------------|--------------------------|
| API        | http://localhost:5050  | -                        |
| Web        | http://localhost:5173  | -                        |
| PostgreSQL | localhost:5435         | gankedtv / gankedtv_dev  |
| MinIO API  | http://localhost:9000  | minioadmin / minioadmin  |
| MinIO UI   | http://localhost:9001  | minioadmin / minioadmin  |

## Git workflow

- Do not add yourself (Claude / any AI) as a co-author. Never append `Co-Authored-By: Claude ...` trailers to commit messages.
- Do not add AI attribution to PR descriptions either — no "🤖 Generated with Claude Code" (or similar) trailer in PR bodies.

## Code comments

- Default to no comment. Add one only when the *why* is non-obvious — a trap, a hidden constraint, a security reason, an ordering requirement — or to orient the reader through a genuinely complex/tricky section. Don't narrate *what* the code does (well-named identifiers cover that), and don't over-explain: one or two tight lines, never a paragraph.
- Never reference issue or PR numbers in code comments (e.g. `// issue #123`, `// M4 from review #111`). They rot as the code moves and the context belongs in the commit/PR. Prose docs (this file, DEPLOYMENT.md) may cross-reference issues; source comments must not.

## Frontend Design

**Before writing any frontend (Vue components, CSS, Tailwind classes):** Read [web/DESIGN.md](web/DESIGN.md).

It defines the "Arena" design system: typography (`--font-condensed` Barlow Condensed, `--font-body` Inter), color tokens (`--color-surface-base`, `--color-surface-raised`, `--color-accent`, etc.), layout rules, motion principles, and anti-patterns to avoid. All tokens are CSS custom properties defined in the `@theme` block of `web/src/assets/base.css`.

## graphify

Optional, per-checkout: `graphify-out/` is gitignored and absent unless you ran `graphify init`
yourself. The tooling below no-ops when it isn't there, so nothing depends on it.

Rules (all conditional on `graphify-out/` existing):
- Before answering architecture or codebase questions, read graphify-out/GRAPH_REPORT.md for god nodes and community structure
- If graphify-out/wiki/index.md exists, navigate it instead of reading raw files
- After modifying code files in this session, run `graphify update .` to keep the graph current (AST-only, no API cost)
