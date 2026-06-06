# Deployment

Production runtime requirements and configuration for GankedTV — the **API server**, the **web
frontend**, and the **Discord bot**. Container images are published to GHCR by
[release.yml](.github/workflows/release.yml) and run via [docker-compose.prod.yml](docker-compose.prod.yml);
see [Container images](#container-images-ghcr) and [Single-host deployment](#single-host-deployment) below.

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

## Container images (GHCR)

[release.yml](.github/workflows/release.yml) builds and pushes three images on every push to `main`,
each tagged with the commit SHA **and** `latest`:

| Image | Built from | Contents |
|---|---|---|
| `ghcr.io/gankedtv/gankedtv-server` | [server/Dockerfile.api](server/Dockerfile.api) | API + media workers (bundles `ffmpeg`, `yt-dlp`, `curl`) |
| `ghcr.io/gankedtv/gankedtv-web` | [web/Dockerfile](web/Dockerfile) | Built Vue bundle served by Caddy (internal `:80`, no TLS) |
| `ghcr.io/gankedtv/gankedtv-discord` | [discord/Dockerfile](discord/Dockerfile) | Discord bot |
| `ghcr.io/gankedtv/gankedtv-dedicated-encoder` | [server/Dockerfile.dedicated-encoder](server/Dockerfile.dedicated-encoder) | `gankedtv-server` + NVENC ffmpeg for the GPU box. **Keep this package private** — it's deployment-specific. Built only when `server/` changes. |

A per-image change filter skips images whose sources didn't change — their `latest` already points at
the correct digest. **There is no deploy job**: the host pulls the moved `latest` itself (Watchtower /
freshdock, or a manual `docker compose pull && up -d`). The full commit-SHA tag is there for pinning
and rollback (`IMAGE_TAG=<sha>`).

The web image is **environment-agnostic — nothing is baked at build time**, so the one published
image works for every deployment. Its `VITE_*` config is injected at **container start**: the image's
entrypoint ([web/docker-entrypoint.sh](web/docker-entrypoint.sh)) writes `/srv/config.js` from the
container's env, and the app reads `window.__APP_CONFIG__` with a fallback to build-time
`import.meta.env` for dev ([web/src/config.ts](web/src/config.ts)). So **no GitHub Variables/Secrets
are needed for the web image** — set the `VITE_*` values as runtime env on the web container instead
(see [Single-host deployment](#single-host-deployment) and `.env.prod.example`).

**Package visibility.** For an unauthenticated host to pull, set the three GHCR packages **public**
(repo → Packages → each package → Package settings). If kept private, give the host a `read:packages`
token via `docker login ghcr.io`.

## Single-host deployment

[docker-compose.prod.yml](docker-compose.prod.yml) runs the whole stack — `postgres`, `redis`, `minio`,
`api`, `web`, and (with `--profile discord`) the bot — on one host from the GHCR images:

```bash
cp .env.prod.example .env      # fill in the REQUIRED values
docker compose -f docker-compose.prod.yml --env-file .env config   # validate interpolation
docker compose -f docker-compose.prod.yml --env-file .env up -d
docker compose -f docker-compose.prod.yml --profile discord --env-file .env up -d   # + bot
```

- The `api` runs with `RUN_MIGRATIONS_ON_STARTUP=true`, so it self-migrates before serving (see
  [Startup database migrations](#startup-database-migrations)); `/health/ready` goes green once applied.
- **Nothing serves TLS.** The `web` (`:80`), `api` (`:5000`), and `minio` (`:9000`) ports are published
  to the host — point **your** reverse proxy (nginx proxy manager, Traefik, …) at them and terminate TLS
  there: web app → `web:80`, API → `api:5000`, and `S3_PUBLIC_URL` → `minio:9000`.
- `S3_ENDPOINT` defaults to the internal `http://minio:9000` (api → minio); `S3_PUBLIC_URL` is the
  browser-facing media URL your proxy serves. The api creates all buckets on first boot
  (`BucketBootstrapHostedService`), marking `game-covers` / `stream-cache` / `avatars` anonymous-read.
- **Object store:** the `minio` service runs **AIStor** — MinIO's maintained successor (community
  MinIO was archived in early 2026). Its **free single-node license** is a token file you mount, not
  an env var: download a free key at <https://min.io/download>, save the token to `secrets/minio.license`
  (gitignored), and point `MINIO_LICENSE_FILE` at it (default `./secrets/minio.license`). Without a
  valid license AIStor blocks all S3 operations. The startup banner says "Community License" even when
  licensed — confirm with `mc license info` (it reports plan **FREE**, no expiry). The free tier is
  standalone single-node only (no distributed HA), which is exactly what this stack uses.
- `DATABASE_URL` / `DISCORD_DATABASE_URL` are derived from `POSTGRES_*`, so the password lives in one
  place — use a connection-string-safe value (no `;`, `@`, `/`).
- An all-in-one box flips `MEDIA_TRANSCODE_WORKER_ENABLED` back **on** (Production defaults it off,
  expecting a GPU host) so the api compresses in-process with the CPU encoder (libx264) — no GPU needed.

> The API logs a one-time `Failed to determine the https port for redirect` warning in Production
> (HTTPS redirection is enabled but there's no TLS inside the container). It's harmless behind a
> TLS-terminating proxy — requests pass through as HTTP, and the health probes are mapped before the
> redirection middleware, so `/health/*` is unaffected.

## Split deployment across hosts

The single-host compose is the baseline. You can peel **object storage** and/or **GPU transcoding**
onto a separate box (e.g. a TrueNAS server with the disks + an NVIDIA GPU) with **no code change** —
the S3 layer already separates the internal endpoint from the browser-facing one, and the media queues
use `FOR UPDATE SKIP LOCKED`, so a worker runs anywhere that reaches the DB.

**AIStor on a storage host** — run it there instead of the compose `minio` service (single-node free
tier; mount your free license token, same as the single-host stack):

```yaml
# storage host
services:
  minio:
    image: quay.io/minio/aistor/minio:RELEASE.2026-05-28T20-50-32Z
    command: minio server /data --console-address ":9001" --license /minio.license
    environment:
      MINIO_ROOT_USER: ${S3_ACCESS_KEY}       # == the api's S3_ACCESS_KEY
      MINIO_ROOT_PASSWORD: ${S3_SECRET_KEY}
    ports: ["9000:9000", "9001:9001"]
    volumes:
      - "/mnt/pool/minio:/data"               # a dataset with room to grow
      - "/mnt/pool/minio.license:/minio.license:ro"   # your free AIStor token
```

Then drop the `minio` service from the compose on the app host and set:

```
S3_ENDPOINT=http://<storage-lan-ip>:9000      # api → store over the LAN (path-style; already on)
S3_PUBLIC_URL=https://cdn.example.com          # browser-facing, via your reverse proxy → :9000
```

Buckets are still auto-created on api boot — no manual setup. The app-host api reaches the store over
the LAN; browsers use `S3_PUBLIC_URL` (presigned URLs are host-rewritten to it).

**GPU media worker** — the compress + JIT stages are GPU-heavy and location-independent (the job
queues use `FOR UPDATE SKIP LOCKED`), so they run on a separate GPU host using the
**`gankedtv-dedicated-encoder`** image (= `gankedtv-server` with an NVENC ffmpeg baked in). Run the
media workers there and leave the app-host api as pure API:

| Toggle | App-host api | GPU host (encoder) |
|---|---|---|
| `MEDIA_THUMBNAIL_WORKER_ENABLED` | `false` | `true` |
| `MEDIA_TRANSCODE_WORKER_ENABLED` | `false` | `true` |
| `MEDIA_TRANSCODE_ENABLED` | `true` | `true` |
| `MEDIA_IMPORT_WORKER_ENABLED` | `true` | `false` |
| `MEDIA_VIDEO_ENCODER` / `MEDIA_VIDEO_CODEC` | — | `av1_nvenc` / `av1` |
| `MEDIA_JIT_VIDEO_ENCODER` | — | `h264_nvenc` |
| `RUN_MIGRATIONS_ON_STARTUP` | `true` | `false` |

(Thumbnail is a software frame-grab — it doesn't use the GPU — so you can leave it `true` on the
app-host instead if you'd rather posters keep generating when the GPU host is down. Either works.)

```yaml
# GPU host — shares the app-host Postgres + the storage-host object store over the LAN
services:
  encoder:
    image: ghcr.io/gankedtv/gankedtv-dedicated-encoder:latest   # NVENC ffmpeg baked in
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      RUN_MIGRATIONS_ON_STARTUP: "false"        # the app-host api migrates — keep off here
      MEDIA_THUMBNAIL_WORKER_ENABLED: "true"
      MEDIA_TRANSCODE_WORKER_ENABLED: "true"
      MEDIA_TRANSCODE_ENABLED: "true"
      MEDIA_IMPORT_WORKER_ENABLED: "false"
      MEDIA_VIDEO_ENCODER: av1_nvenc
      MEDIA_VIDEO_CODEC: av1
      MEDIA_JIT_VIDEO_ENCODER: h264_nvenc
      # Full api boot → fetches JWT/S3/etc. from Secrets - PROD, same as the app-host.
      VAULTWARDEN_API_URL: https://<your-vault-api>
      VAULTWARDEN_API_KEY: <secrets@ token>
      # DB over the LAN to the app host (publish 5432 there — see below). Set explicitly so it points
      # at the app host, not the vault's compose-internal value.
      DATABASE_URL: "Host=<app-host-lan-ip>;Port=5432;Database=gankedtv;Username=gankedtv;Password=<pw>"
```

On **TrueNAS Scale**, just attach the app's GPU device — Apps wire the NVIDIA driver libs / passthrough
out of the box (no manual NVIDIA Container Toolkit), and the encoder image already bundles the NVENC
ffmpeg, so `av1_nvenc`/`h264_nvenc` work once the GPU is attached. Keep the `gankedtv-dedicated-encoder`
GHCR package **private** and give the host a `read:packages` token to pull it.

**App-host changes when you move transcoding to the GPU box:**
1. Set the api's `MEDIA_TRANSCODE_WORKER_ENABLED=false` (and `MEDIA_THUMBNAIL_WORKER_ENABLED=false` if
   you moved thumbnails too) — otherwise both hosts race the same jobs.
2. **Publish Postgres on the LAN** so the GPU host can reach it (the prod compose keeps `postgres`
   internal-only): add `ports: ["<app-host-lan-ip>:5432:5432"]` to the `postgres` service.
3. Stand the GPU worker up and confirm it's claiming jobs **before** flipping the app-host workers off,
   so there's no gap where nothing processes.

The worker owns no schema and runs no migrations — it only leases media jobs from the shared DB and
reads/writes the shared object store.

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
| Web build | *(none)* — the web image is runtime-configured; `VITE_*` are supplied as container env at deploy (see [Web frontend](#web-frontend-runtime-config)), not fetched from the vault. |

`SENTRY_DSN` is intentionally **absent** — there's no Sentry integration yet (tracked in #124); add
it to the manifests when that lands.

**CI.** The web build (`web.yml`) only **compiles** — it does **not** fetch from Vaultwarden, so neither
PR nor `main`/release CI depends on vault availability or runner-IP whitelisting. The published web
image is environment-agnostic; all web `VITE_*` config is supplied as container env at deploy. The API
and Discord bot still fetch their secrets from the host `.env` (or Vaultwarden) at startup.

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
| `GET /health/ready` | DB reachable **and** migrations applied. | Readiness gate, load-balancer membership, post-deploy smoke check. |

## Redis

Optional (`REDIS_URL`): backs the feed/trending cache, cluster-wide rate limiting, and — when set —
**DataProtection key persistence** (keys survive restarts instead of an ephemeral in-memory keyring).

> **Do not enable an `allkeys-*` `maxmemory-policy`** on this instance. DataProtection keys are stored
> as ordinary (non-expiring) Redis keys, so an all-keys eviction policy could silently drop them under
> memory pressure — invalidating everything they protect. The default `redis:7` config (no eviction)
> is correct; if you set a `maxmemory` limit, keep the policy at `noeviction` or a `volatile-*` one
> (those only evict keys that carry a TTL, which the DP keys don't). Keys are persisted unencrypted at
> rest, so keep this Redis on the internal network.

## Worker toggles (media pipeline)

The media pipeline can be split across hosts (see the table in [CLAUDE.md](CLAUDE.md) under
"Media pipeline"). `appsettings.Production.json` defaults to the **GPU-split** role (thumbnail worker
on, transcode worker off, transcode pipeline on) — it expects a separate GPU box to run the compress +
JIT stages. The single-host compose flips `MEDIA_TRANSCODE_WORKER_ENABLED` back on so one box does CPU
compression; to offload to a GPU host instead, see [Split deployment across hosts](#split-deployment-across-hosts).

| Instance | `MEDIA_THUMBNAIL_WORKER_ENABLED` | `MEDIA_TRANSCODE_WORKER_ENABLED` | encoder vars |
|---|---|---|---|
| All-in-one host (CPU) | `true` | `true` | — (defaults: `libx264` / `h264`) |
| Main API server (GPU split) | `true` | `false` | — |
| GPU box (NVENC) | `false` | `true` | `MEDIA_VIDEO_ENCODER=av1_nvenc`, `MEDIA_VIDEO_CODEC=av1`, `MEDIA_JIT_VIDEO_ENCODER=h264_nvenc` |

## Web frontend (runtime config)

The web app is a static bundle, but its config is **not** baked at build time — so one published image
works for every deployment. The image's entrypoint ([web/docker-entrypoint.sh](web/docker-entrypoint.sh))
writes `/srv/config.js` from the container's env at startup, and the app reads `window.__APP_CONFIG__`,
falling back to build-time `import.meta.env` for `bun dev` ([web/src/config.ts](web/src/config.ts)). Set
these as **runtime env on the web container** (see [.env.prod.example](.env.prod.example)):

| Var | Purpose |
|---|---|
| `VITE_API_BASE_URL` | API base URL the frontend calls (e.g. `https://api.ganked.tv`). Falls back to `http://localhost:5050` if unset — set it for prod. |
| `VITE_GA_MEASUREMENT_ID` | GA4 measurement id (`G-XXXXXXX`). Analytics is a complete no-op (no script, no cookies) when empty. |
| `VITE_USE_SECURE_COOKIES` | `true` to skip localStorage token persistence (HttpOnly-cookie strategy). |
| `VITE_MAX_UPLOAD_SIZE_MB` | Max upload size shown in the UI (default 500). |
| `VITE_SENTRY_DSN` | Sentry/GlitchTip DSN. No-op when empty. Optional: `VITE_SENTRY_ENVIRONMENT`, `VITE_SENTRY_RELEASE`, `VITE_SENTRY_TRACES_SAMPLE_RATE`. |

> For local dev the committed `public/config.js` is empty, so the app falls back to `import.meta.env`
> (the `VITE_*` from `.env` / Vaultwarden). **Cookie-consent gating for analytics is a known follow-up**
> — GA currently loads whenever the measurement id is present.

## Error monitoring (Sentry → self-hosted GlitchTip)

All three apps ship the official **Sentry SDK** pointed at the self-hosted **GlitchTip** instance
(Sentry-API-compatible). It is **opt-in and disabled by default** — each SDK no-ops when its DSN is
unset, the same contract as the IGDB sync and Discord bot. Posture is **errors/crashes + light
tracing** (sample rate `0.01`); session replay, profiling, and Sentry "logs" are intentionally off
(GlitchTip doesn't support them). `SendDefaultPii` is `false` everywhere and credential-bearing
headers/cookies/query params are scrubbed before events leave the app.

Create **one GlitchTip project per app** (api / web / discord). Each app's DSN var is distinct, so
all three can live in a single shared `.env` without colliding — the bot reads the repo-root `.env`
too, hence the `DISCORD_`-prefix:

| App | DSN var | Environment / Release source |
|---|---|---|
| API (.NET) | `SENTRY_DSN` | env `SENTRY_ENVIRONMENT` → falls back to `ASPNETCORE_ENVIRONMENT`; `SENTRY_RELEASE` → entry-assembly version |
| Web (Vite) | `VITE_SENTRY_DSN` (runtime) | `VITE_SENTRY_ENVIRONMENT` → Vite mode; `VITE_SENTRY_RELEASE` → package version |
| Discord bot (Bun) | `DISCORD_SENTRY_DSN` | `DISCORD_SENTRY_ENVIRONMENT` → `NODE_ENV`; `DISCORD_SENTRY_RELEASE` → package version |

`SENTRY_TRACES_SAMPLE_RATE` / `VITE_SENTRY_TRACES_SAMPLE_RATE` / `DISCORD_SENTRY_TRACES_SAMPLE_RATE`
override the `0.01` default. All three apps take their config from runtime env, so set `SENTRY_RELEASE`
/ `VITE_SENTRY_RELEASE` / `DISCORD_SENTRY_RELEASE` in the deploy env to map issues to a release; left
unset they fall back to the assembly/package version.

## Reference

A full annotated list of every variable lives in [.env.example](.env.example).
