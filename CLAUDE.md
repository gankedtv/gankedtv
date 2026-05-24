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

[server/Dockerfile.api](server/Dockerfile.api) ships an API image with ffmpeg pre-installed — it's there for production / CI parity builds, not used by the dev compose stack.

### Media pipeline: compress-in-place + just-in-time playback (issue #102)

The pipeline is built to **minimise persistent storage** (Tdarr-style): each clip keeps exactly one
efficiently-compressed master on disk; adaptive HLS ladders are generated **on demand at watch
time** and cached transiently, never stored permanently. Workers extend the generic
`MediaStageWorker<TJob>` ([server/src/GankedTV.Api/Services/Media/MediaStageWorker.cs](server/src/GankedTV.Api/Services/Media/MediaStageWorker.cs)).

**Upload-time stages** (status flow `draft → processing → transcoding → ready`):

1. **Thumbnail** (`ThumbnailWorker`, claims `processing`) — poster + ffprobe metadata, then advances to `transcoding` (or straight to `ready` when `TranscodeEnabled=false`).
2. **Compress** (`CompressWorker` → `CompressJobService`, claims `transcoding`) — re-encodes the raw upload into ONE resolution-capped, quality-targeted master (AV1 on the GPU box, H.264 in dev), repoints the clip's `video_key` at it, records `video_codec`, **deletes the original**, advances to `ready`. Net disk per clip goes *down*.

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

## Frontend Design

**Before writing any frontend (Vue components, CSS, Tailwind classes):** Read [web/DESIGN.md](web/DESIGN.md).

It defines the "Underground Arena" design system: typography (Rajdhani, Barlow Condensed, DM Sans, DM Mono), color tokens (`--color-surface-base`, `--color-brand`, `--color-neon`, etc.), layout rules, motion principles, and anti-patterns to avoid. All tokens are CSS custom properties defined in `web/src/assets/base.css`.

## graphify

This project has a graphify knowledge graph at graphify-out/.

Rules:
- Before answering architecture or codebase questions, read graphify-out/GRAPH_REPORT.md for god nodes and community structure
- If graphify-out/wiki/index.md exists, navigate it instead of reading raw files
- After modifying code files in this session, run `graphify update .` to keep the graph current (AST-only, no API cost)
