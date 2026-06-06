# Multi-stage build for the production web bundle. Build context is the repo ROOT (like
# discord/Dockerfile) because Vite reads env via envDir:'../' and the prebuild imports the shared
# Vaultwarden client (../../shared). VITE_* values are baked from build-args below; the prebuild
# (web/scripts/fetch-vault-env.ts) no-ops without VAULTWARDEN_* creds, so the image build needs no
# vault access — pass the values directly as --build-arg.

FROM oven/bun:1.3.12-alpine AS build
WORKDIR /app

# VITE_* build-time vars — baked into the public bundle by Vite (loadEnv merges process.env entries
# matching the VITE_ prefix). Keep this set in sync with web/scripts/fetch-vault-env.ts (viteManifest),
# web/env.d.ts, and .env.example. Defaults are empty so PR / local image builds need none of them.
ARG VITE_API_BASE_URL
ARG VITE_GA_MEASUREMENT_ID
ARG VITE_USE_SECURE_COOKIES
ARG VITE_MAX_UPLOAD_SIZE_MB
ARG VITE_SENTRY_DSN
ARG VITE_SENTRY_ENVIRONMENT
ARG VITE_SENTRY_RELEASE
ARG VITE_SENTRY_TRACES_SAMPLE_RATE
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL \
    VITE_GA_MEASUREMENT_ID=$VITE_GA_MEASUREMENT_ID \
    VITE_USE_SECURE_COOKIES=$VITE_USE_SECURE_COOKIES \
    VITE_MAX_UPLOAD_SIZE_MB=$VITE_MAX_UPLOAD_SIZE_MB \
    VITE_SENTRY_DSN=$VITE_SENTRY_DSN \
    VITE_SENTRY_ENVIRONMENT=$VITE_SENTRY_ENVIRONMENT \
    VITE_SENTRY_RELEASE=$VITE_SENTRY_RELEASE \
    VITE_SENTRY_TRACES_SAMPLE_RATE=$VITE_SENTRY_TRACES_SAMPLE_RATE

# Keep the repo-relative layout (/app/web + /app/shared) so envDir:'../' and the prebuild's
# ../../shared import resolve exactly as they do in the working tree. Install deps first for a
# cache-friendly layer; .dockerignore keeps host node_modules/dist out of the context.
COPY web/package.json web/bun.lock ./web/
COPY shared ./shared
RUN cd web && bun install --frozen-lockfile

COPY web ./web
# `bun run build` runs prebuild (vault no-op here) + type-check (vue-tsc) + `vite build`, so the
# full dev deps installed above are required. Emits /app/web/dist.
RUN cd web && bun run build

FROM caddy:2-alpine AS runtime
COPY --from=build /app/web/dist /srv
COPY web/Caddyfile /etc/caddy/Caddyfile
EXPOSE 80
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget -qO- http://localhost:80/ >/dev/null 2>&1 || exit 1
