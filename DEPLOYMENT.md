# Deployment

Production runtime requirements and configuration for GankedTV. This covers the **API server**
and the **web frontend**; container build/publish and the auto-deploy pipeline are tracked
separately in issue #123.

> **Secrets are never committed.** Everything below is provided via environment variables (or
> your orchestrator's secret store) at runtime — there are no secrets in `appsettings*.json` or
> any checked-in file.

## Hard requirements

- **`ASPNETCORE_ENVIRONMENT=Production` is mandatory.** It activates `appsettings.Production.json`,
  the fail-fast secret validation, strict CORS, HTTPS redirection, and disables the dev-only
  `POST /dev/token` and OpenAPI endpoints. A deploy that forgets this runs with dev affordances exposed.
- **PostgreSQL ≥ 15** (the dev stack and CI run PG18).
- The API image ([server/Dockerfile.api](server/Dockerfile.api)) ships `ffmpeg` + `yt-dlp` for the
  media/import workers.

## Fail-fast secret validation

On boot in Production the API validates required configuration and **refuses to start** with an
aggregated error if anything is missing or still set to a dev default (logic in
[ProductionStartupValidator](server/src/GankedTV.Api/Configuration/ProductionStartupValidator.cs)).
Required:

| Var | Requirement |
|---|---|
| `DATABASE_URL` | Postgres connection string (dotnet/Npgsql form). |
| `JWT_SECRET` | ≥ 32 bytes. Generate: `openssl rand -hex 32`. |
| `WEB_ORIGIN` | Public origin of the web app (e.g. `https://ganked.tv`). |
| `CORS_ORIGINS` | Comma-separated browser-origin allowlist. `WEB_ORIGIN` is always allowed implicitly. |
| `S3_ENDPOINT` | Object-storage endpoint. |
| `S3_ACCESS_KEY` / `S3_SECRET_KEY` | Non-default credentials (literal `minioadmin` is rejected). |
| `S3_PUBLIC_URL` | Public base URL for stored objects / presigned-URL host rewrite. |

Also recommended in production: `OAUTH_STATE_SECRET` (≥ 32 bytes; required once any OAuth provider
is configured) and the provider client credentials (`DISCORD_*`, `GOOGLE_*`).

## Startup database migrations

Migrations are **manual locally** (`make migrate`). In production, set
`RUN_MIGRATIONS_ON_STARTUP=true` on the migrating instance so a fresh DB self-migrates at boot,
before serving requests. `/health/ready` only reports healthy once all migrations are applied
(see [DatabaseMigrator](server/src/GankedTV.Api/Data/DatabaseMigrator.cs) and
[ReadinessHealthCheck](server/src/GankedTV.Api/Services/Health/ReadinessHealthCheck.cs)). EF Core
serialises migration runs via a `__EFMigrationsHistory` lock, so multiple replicas booting with the
flag enabled apply migrations safely (they wait, they don't race). For clarity you may still prefer
to gate the flag to one replica or run migrations as a separate init step, but it isn't required for
correctness. The flag accepts `true`/`1`/`yes`/`on`.

## Health endpoints

| Endpoint | Meaning | Use |
|---|---|---|
| `GET /health/live` | Process is up (no dependency checks). | Liveness probe / restart signal. |
| `GET /health/ready` | DB reachable **and** migrations applied. | Readiness gate, load-balancer membership, the #123 deploy smoke test. |

## Worker toggles (media pipeline)

The media pipeline can be split across hosts (see the table in [CLAUDE.md](CLAUDE.md) under
"Media pipeline"). `appsettings.Production.json` defaults to the **main API server** role
(thumbnail worker on, transcode worker off, transcode pipeline on). Override per host via env vars
to move GPU work to a separate box:

| Instance | `MEDIA_THUMBNAIL_WORKER_ENABLED` | `MEDIA_TRANSCODE_WORKER_ENABLED` | encoder vars |
|---|---|---|---|
| Main API server | `true` | `false` | — |
| GPU box (NVENC) | `false` | `true` | `MEDIA_VIDEO_ENCODER=av1_nvenc`, `MEDIA_VIDEO_CODEC=av1`, `MEDIA_JIT_VIDEO_ENCODER=h264_nvenc` |

## Web frontend (build-time config)

The web app is a static bundle; its config is baked in **at build time** by Vite (Vite reads the
repo-root `.env` via `envDir: '../'`). These must be set when running `bun run build`, not at
runtime:

| Var | Purpose |
|---|---|
| `VITE_API_BASE_URL` | API base URL the built frontend calls (e.g. `https://api.ganked.tv`). Falls back to `http://localhost:5050` if unset — so it **must** be set for prod builds. |
| `VITE_GA_MEASUREMENT_ID` | GA4 measurement id (`G-XXXXXXX`). Analytics is a complete no-op (no script, no cookies) when empty, so it stays off in dev/preview. |

> The Dockerfile build-arg wiring that feeds these into the production web image is part of
> issue #123. **Cookie-consent gating for analytics is a known follow-up** — GA currently loads
> whenever the measurement id is present.

## Reference

A full annotated list of every variable lives in [.env.example](.env.example).
