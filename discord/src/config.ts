import { z } from 'zod';

const Schema = z.object({
  // No .min(1) on the bot-credential triple: shell envs and root .env both
  // represent "unset" as the empty string ("DISCORD_BOT_TOKEN=" with nothing
  // after the equals). Rejecting empty strings would force users to delete
  // the env var entirely just to get the "disabled" boot path — that breaks
  // the contract from .env.example which ships these as empty placeholders.
  // The `enabled` check below uses Boolean() which already treats "" as false.
  DISCORD_BOT_TOKEN: z.string().optional(),
  DISCORD_BOT_APP_ID: z.string().optional(),
  DISCORD_BOT_GUILD_ID: z.string().optional(),
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
  const hasToken = Boolean(parsed.DISCORD_BOT_TOKEN);
  const hasAppId = Boolean(parsed.DISCORD_BOT_APP_ID);

  // Operator misconfiguration guard: if one half of the credential pair is set
  // but the other isn't, the bot would silently disable with no visible signal
  // that something was forgotten. Log a single warn line so it appears in any
  // structured-log search before the disable message.
  if (hasToken !== hasAppId) {
    console.warn(
      JSON.stringify({
        level: 'warn',
        msg: 'Discord bot config is partial — both DISCORD_BOT_TOKEN and DISCORD_BOT_APP_ID are required; bot will stay disabled',
        hasToken,
        hasAppId,
      }),
    );
  }

  const enabled = hasToken && hasAppId;
  return { ...parsed, enabled };
}
