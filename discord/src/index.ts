// Importing loadEnv runs its side effect (merging the repo-root .env into process.env) and exposes
// loadVaultwardenSecrets, which main() awaits before loadConfig().
import { loadVaultwardenSecrets, optionalVaultwardenManifest } from './loadEnv.ts';

import * as Sentry from '@sentry/bun';
import { Client, GatewayIntentBits, REST, Routes, type Interaction } from 'discord.js';
import { loadConfig } from './config.ts';
import { initSentry } from './sentry.ts';
import { connect, createDb } from './db.ts';
import { runMigrations } from './migrator.ts';
import { createApi } from './api.ts';
import {
  commandDefinitions,
  dispatchAutocomplete,
  dispatchChatInput,
  type CommandContext,
} from './commands/index.ts';
import { ephemeral } from './commands/replies.ts';
import { createFanout } from './fanout.ts';
import { startPoller, type PollerLogger } from './poller.ts';

// Off-by-default contract mirrors IgdbSyncHostedService: if the bot token is
// missing, log "disabled" and exit cleanly. The compose service still starts,
// just no-ops. This keeps local-dev `make up` working for contributors who
// haven't set up a Discord application yet.
const log: PollerLogger = {
  info: (m, f) => console.log(JSON.stringify({ level: 'info', msg: m, ...f })),
  warn: (m, f) => console.warn(JSON.stringify({ level: 'warn', msg: m, ...f })),
  error: (m, f) => console.error(JSON.stringify({ level: 'error', msg: m, ...f })),
};

async function main(): Promise<void> {
  // Pull secrets from Vaultwarden (no-op unless the bootstrap vars are set) before reading config.
  await loadVaultwardenSecrets(process.env);
  // Opt-in config (Sentry DSN, etc.): best-effort, never fails boot if absent from the vault.
  await loadVaultwardenSecrets(process.env, {
    manifest: optionalVaultwardenManifest,
    optional: true,
  });
  const config = loadConfig();

  // No-op unless DISCORD_SENTRY_DSN is set; before the enabled check so boot crashes still report.
  initSentry(config);

  if (!config.enabled) {
    log.info('Discord bot disabled (DISCORD_BOT_TOKEN or DISCORD_BOT_APP_ID unset); exiting.');
    return;
  }

  const sql = connect(config.DISCORD_DATABASE_URL);
  const db = createDb(sql);
  const api = createApi(config.GANKEDTV_API_BASE);

  log.info('Running migrations...');
  const ran = await runMigrations(sql);
  log.info('Migrations complete', { applied: ran });

  const ctx: CommandContext = {
    db,
    api,
    publicBase: config.GANKEDTV_PUBLIC_BASE,
  };

  const client = new Client({
    intents: [GatewayIntentBits.Guilds],
  });

  client.once('clientReady', () => {
    log.info('Discord client ready', { user: client.user?.tag });
  });

  client.on('interactionCreate', async (interaction: Interaction) => {
    try {
      if (interaction.isChatInputCommand()) {
        await dispatchChatInput(interaction, ctx);
      } else if (interaction.isAutocomplete()) {
        await dispatchAutocomplete(interaction, ctx);
      }
    } catch (err) {
      log.error('interaction handler threw', {
        commandName: 'commandName' in interaction ? interaction.commandName : null,
        err: String(err),
      });
      Sentry.captureException(err);
      if (interaction.isRepliable()) {
        try {
          // All command handlers call deferReply() first, so by the time we
          // catch here the interaction is typically `deferred && !replied`.
          // Calling reply() in that state throws InteractionAlreadyReplied —
          // we have to follow up via editReply() instead. The ephemeral flag
          // was already set at deferReply time so editReply inherits it.
          if (interaction.deferred || interaction.replied) {
            await interaction.editReply({ content: 'Something went wrong.' });
          } else {
            await interaction.reply(ephemeral('Something went wrong.'));
          }
        } catch {
          /* swallow — original error is already logged */
        }
      }
    }
  });

  // Slash command registration: guild-scoped if a dev guild id is set (propagates
  // within seconds), global otherwise (up to an hour). We register on every boot
  // because the cost is one REST call and it keeps definitions in sync with code.
  const rest = new REST({ version: '10' }).setToken(config.DISCORD_BOT_TOKEN!);
  if (config.DISCORD_BOT_GUILD_ID) {
    log.info('Registering guild-scoped slash commands', { guild: config.DISCORD_BOT_GUILD_ID });
    await rest.put(
      Routes.applicationGuildCommands(config.DISCORD_BOT_APP_ID!, config.DISCORD_BOT_GUILD_ID),
      { body: commandDefinitions() },
    );
  } else {
    log.info('Registering global slash commands');
    await rest.put(Routes.applicationCommands(config.DISCORD_BOT_APP_ID!), {
      body: commandDefinitions(),
    });
  }

  // Graceful shutdown: stop the poller, disconnect Discord, close the DB pool.
  const abort = new AbortController();
  const shutdown = async (signal: string) => {
    log.info('Shutdown signal received', { signal });
    abort.abort();
    try {
      await client.destroy();
    } catch (err) {
      log.warn('client destroy threw', { err: String(err) });
    }
    try {
      await db.close();
    } catch (err) {
      log.warn('db close threw', { err: String(err) });
    }
    process.exit(0);
  };
  process.on('SIGINT', () => void shutdown('SIGINT'));
  process.on('SIGTERM', () => void shutdown('SIGTERM'));

  await client.login(config.DISCORD_BOT_TOKEN!);

  const fanout = createFanout({
    channels: client.channels,
    db,
    log,
    publicBase: config.GANKEDTV_PUBLIC_BASE,
  });

  log.info('Starting poller', { intervalSeconds: config.DISCORD_POLL_INTERVAL_SECONDS });
  await startPoller({ db, api, fanout, log }, config.DISCORD_POLL_INTERVAL_SECONDS, abort.signal);
}

main().catch(async (err) => {
  log.error('fatal', { err: String(err), stack: err instanceof Error ? err.stack : undefined });
  Sentry.captureException(err);
  // captureException only enqueues; flush before exiting or the event is dropped (bounded so a
  // dead transport can't hang shutdown).
  await Sentry.flush(2000);
  process.exit(1);
});
