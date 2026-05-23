import { z } from 'zod';

const Schema = z.object({
  DISCORD_BOT_TOKEN: z.string().min(1).optional(),
  DISCORD_BOT_APP_ID: z.string().min(1).optional(),
  DISCORD_BOT_GUILD_ID: z.string().min(1).optional(),
  DISCORD_POLL_INTERVAL_SECONDS: z.coerce.number().int().positive().default(30),
  // Distinct from the API's DATABASE_URL because the API stores its connection
  // string in dotnet semicolon form (Host=...;Port=...;...) which porsager's
  // `postgres` driver can't parse — we need a libpq URL (postgres://...).
  // Worktrees populate both: dotnet form in DATABASE_URL for the API, libpq form
  // here for the bot. See scripts/new-worktree.sh.
  DISCORD_DATABASE_URL: z.string().min(1),
  GANKEDTV_API_BASE: z.string().url().default('http://localhost:5050'),
  GANKEDTV_PUBLIC_BASE: z.string().url().default('http://localhost:5173'),
});

export type Config = z.infer<typeof Schema> & { enabled: boolean };

export function loadConfig(env: NodeJS.ProcessEnv = process.env): Config {
  const parsed = Schema.parse(env);
  // Symmetric with IgdbSyncHostedService: presence of the token is the on-switch.
  // Without it, boot logs "disabled" and exits — the compose service still starts,
  // it just no-ops. App ID is required alongside the token because slash command
  // registration uses the REST API and addresses commands by application id.
  const enabled = Boolean(parsed.DISCORD_BOT_TOKEN && parsed.DISCORD_BOT_APP_ID);
  return { ...parsed, enabled };
}
