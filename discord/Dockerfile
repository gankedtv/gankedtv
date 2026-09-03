FROM oven/bun:1.4.0-alpine

# Build context is the repo ROOT (see docker-compose.dev.yml) so the image can include the shared
# shared/ TypeScript module the bot imports. We keep the repo-relative layout (/app/discord +
# /app/shared) so the bot's `../../shared/...` import resolves identically in dev and in the image.
WORKDIR /app

# Install + copy as root so the working tree ends up owned by root (cached layer stays small), then
# chown to a dedicated unprivileged user for runtime. The bot only needs read access to its own
# files + outbound network — no reason for it to run as uid 0.
COPY discord/package.json discord/bun.lock ./discord/
RUN cd discord && bun install --frozen-lockfile --production

COPY shared ./shared
COPY discord/src ./discord/src
COPY discord/tsconfig.json discord/bunfig.toml ./discord/

RUN addgroup -S bot && adduser -S bot -G bot && chown -R bot:bot /app
USER bot

CMD ["bun", "run", "discord/src/index.ts"]
