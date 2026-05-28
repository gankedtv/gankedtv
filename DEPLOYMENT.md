# Deployment

Production runtime requirements and configuration for GankedTV. This covers the **API server**
and the **web frontend**; container build/publish and the auto-deploy pipeline are tracked
separately in issue #123.

> **Secrets are never committed.** In production they come from the self-hosted Vaultwarden-API
> (see [Secret management](#secret-management-vaultwarden)) or your orchestrator's secret store, fed
> in via environment variables at runtime — there are no secrets in `appsettings*.json` or any
> checked-in file.

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

## Secret management (Vaultwarden)

Secrets are sourced from the self-hosted [Vaultwarden-API](https://github.com/Turbootzz/Vaultwarden-API)
rather than hand-placed per environment. Each app fetches its secrets at startup (API,
bot) or build (web) and layers them into the environment; **a value already set in the environment
always wins**, so the vault is the source of truth without removing per-key overrides. The same TS
client (`shared/vaultwarden/`) backs the bot and the web build; the API has an equivalent in
[VaultwardenSecretsLoader](server/src/GankedTV.Api/Configuration/VaultwardenSecretsLoader.cs).

**Bootstrap vars** (the only secrets that stay in the environment — you can't fetch secrets without
one):

| Var | Purpose |
|---|---|
| `VAULTWARDEN_API_URL` | Base URL of the Vaultwarden-API. Unset → the whole integration no-ops and apps fall back to `.env`/env. |
| `VAULTWARDEN_API_KEY` | Bearer token of the dedicated `secrets@` Vaultwarden user (a member of the `GankedTV` org with access to both collections). |
| `VAULTWARDEN_ORG` | Organization. Defaults to `GankedTV`. |
| `VAULTWARDEN_COLLECTION` | Optional explicit collection override (else env-derived). |

**Collection per environment.** `ASPNETCORE_ENVIRONMENT` (the bot also falls back to `NODE_ENV`)
selects the collection: `Production` → **`Secrets - PROD`**, anything else → **`Secrets - DEV`**.
Vault items are named exactly like the env keys, scoped by org + collection so the same key can hold
different values in DEV vs PROD without colliding.

**Resilience.** Fetches are sequential (the API rate-limits 30 req/min/IP) and one-shot at
startup/build — rotate a secret by restarting/rebuilding. In **Production** a required secret that
can't be fetched **fails the boot** with a clear, Vaultwarden-specific error; in development a
missing/unreachable vault falls back to `.env`. The API's fetch runs **before** the fail-fast
validation above, so a prod boot supplied with only the two bootstrap vars has the vault populate
`DATABASE_URL` / `JWT_SECRET` / `S3_*` / … and then passes validation.

**Per-app manifest** (the keys each app fetches):

| App | Keys |
|---|---|
| API | `DATABASE_URL`, `JWT_SECRET`, `OAUTH_STATE_SECRET`, `S3_ENDPOINT`, `S3_ACCESS_KEY`, `S3_SECRET_KEY`, `S3_PUBLIC_URL`, `DISCORD_CLIENT_ID`, `DISCORD_CLIENT_SECRET`, `DISCORD_REDIRECT_URI`, `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, `GOOGLE_REDIRECT_URI`, `IGDB_CLIENT_ID`, `IGDB_CLIENT_SECRET`, `REDIS_URL`, `WEB_ORIGIN`, `CORS_ORIGINS` |
| Discord bot | `DISCORD_BOT_TOKEN`, `DISCORD_BOT_APP_ID`, `DISCORD_DATABASE_URL` |
| Web build | `VITE_API_BASE_URL`, `VITE_GA_MEASUREMENT_ID`, `VITE_USE_SECURE_COOKIES`, `VITE_MAX_UPLOAD_SIZE_MB` (baked into the public bundle — single source of truth, not secrecy) |

`SENTRY_DSN` is intentionally **absent** — there's no Sentry integration yet (tracked in #124); add
it to the manifests when that lands.

**CI.** The web build workflow pulls `VITE_*` from the PROD collection on pushes to `main` (using a
`VAULTWARDEN_API_KEY` GitHub secret and the API's `ENABLE_GITHUB_IP_RANGES` to whitelist runner IPs);
PR builds skip the fetch and use the committed `.env`, so PR CI doesn't depend on vault availability.
Sourcing the **deploy** job's secrets from Vaultwarden is part of #123.

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
| `VITE_SENTRY_DSN` | Sentry/GlitchTip DSN for the web project. Error monitoring is a no-op when empty. Optional: `VITE_SENTRY_ENVIRONMENT`, `VITE_SENTRY_RELEASE`, `VITE_SENTRY_TRACES_SAMPLE_RATE`. |

> The Dockerfile build-arg wiring that feeds these into the production web image is part of
> issue #123. **Cookie-consent gating for analytics is a known follow-up** — GA currently loads
> whenever the measurement id is present.

## Error monitoring (Sentry → self-hosted GlitchTip)

All three apps ship the official **Sentry SDK** pointed at the self-hosted **GlitchTip** instance
(Sentry-API-compatible). It is **opt-in and disabled by default** — each SDK no-ops when its DSN is
unset, the same contract as the IGDB sync and Discord bot. Posture is **errors/crashes + light
tracing** (sample rate `0.1`); session replay, profiling, and Sentry "logs" are intentionally off
(GlitchTip doesn't support them). `SendDefaultPii` is `false` everywhere and credential-bearing
headers/cookies/query params are scrubbed before events leave the app.

Create **one GlitchTip project per app** (api / web / discord) and supply each service its own DSN:

| App | DSN var | Environment / Release source |
|---|---|---|
| API (.NET) | `SENTRY_DSN` | env `SENTRY_ENVIRONMENT` → falls back to `ASPNETCORE_ENVIRONMENT`; `SENTRY_RELEASE` → entry-assembly version |
| Web (Vite) | `VITE_SENTRY_DSN` (build-time) | `VITE_SENTRY_ENVIRONMENT` → Vite mode; `VITE_SENTRY_RELEASE` → package version |
| Discord bot (Bun) | `SENTRY_DSN` (set on the bot container or `discord/.env`, not the shared root) | `SENTRY_ENVIRONMENT` → `NODE_ENV`; `SENTRY_RELEASE` → package version |

`SENTRY_TRACES_SAMPLE_RATE` (web: `VITE_SENTRY_TRACES_SAMPLE_RATE`) overrides the `0.1` default.
Leave `SENTRY_RELEASE` unset for now — the #123 deploy pipeline will set it to the commit SHA so
issues map to deploys.

## Reference

A full annotated list of every variable lives in [.env.example](.env.example).
