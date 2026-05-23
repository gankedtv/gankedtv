FROM oven/bun:1.3.13-alpine

WORKDIR /app

# Install + copy as root so the working tree ends up owned by root (cached layer
# stays small), then chown to a dedicated unprivileged user for runtime. The bot
# only needs read access to its own files + outbound network — no reason for it
# to run as uid 0.
COPY package.json bun.lock ./
RUN bun install --frozen-lockfile --production

COPY src ./src
COPY tsconfig.json bunfig.toml ./

RUN addgroup -S bot && adduser -S bot -G bot && chown -R bot:bot /app
USER bot

CMD ["bun", "run", "src/index.ts"]
