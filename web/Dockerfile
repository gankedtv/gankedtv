# Multi-stage build for the production web bundle. Build context is the repo ROOT (like
# discord/Dockerfile) because Vite reads env via envDir:'../' and the prebuild imports the shared
# Vaultwarden client (../../shared). The bundle is environment-AGNOSTIC: per-deployment VITE_* values
# are NOT baked — they're injected at container start into /srv/config.js (see web/docker-entrypoint.sh
# + src/config.ts), so one published image works for every deployment.

FROM oven/bun:1.4.0-alpine AS build
WORKDIR /app

# Keep the repo-relative layout (/app/web + /app/shared) so envDir:'../' and the prebuild's
# ../../shared import resolve exactly as they do in the working tree. Install deps first for a
# cache-friendly layer; .dockerignore keeps host node_modules/dist out of the context.
COPY web/package.json web/bun.lock ./web/
COPY shared ./shared
RUN cd web && bun install --frozen-lockfile

COPY web ./web
# `bun run build` runs prebuild (vault no-op here) + type-check (vue-tsc) + `vite build`, so the
# full dev deps installed above are required. Emits /app/web/dist (incl. the default config.js).
RUN cd web && bun run build

FROM caddy:2-alpine AS runtime
# jq is used by the entrypoint to safely JSON-encode env values into the runtime config.
RUN apk add --no-cache jq
COPY --from=build /app/web/dist /srv
COPY web/Caddyfile /etc/caddy/Caddyfile
COPY web/docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh
EXPOSE 80
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget -qO- http://localhost:80/ >/dev/null 2>&1 || exit 1
ENTRYPOINT ["/docker-entrypoint.sh"]
