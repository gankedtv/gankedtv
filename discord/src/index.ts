// Side-effect import — merges the repo-root .env into process.env BEFORE
// loadConfig reads it. Mirrors web/vite.config.ts's `envDir: '../'`.
import './loadEnv.ts';

import { Client, GatewayIntentBits, REST, Routes, type Interaction } from 'discord.js';
import { loadConfig } from './config.ts';
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
import { buildMessage, postToChannel } from './posting.ts';
import { startPoller, type Fanout, type PollerLogger } from './poller.ts';

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
  const config = loadConfig();

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

  // One channel per call. The poller invokes this per matched (clip, sub) pair
  // and the return value (true=delivered, false=transient failure) drives
  // whether the post-log row is written. Returning false here means the poller
  // will isPosted()-check this pair again next round and retry.
  const fanout: Fanout = async (clip, target) => {
    // fetch() pulls from the cache when warm, REST when cold. discord.js v14
    // THROWS `DiscordAPIError` for missing/inaccessible resources (codes 10003,
    // 10004, 50001, 50007 etc.); the `null` return is reserved for forced
    // cache-miss configurations. The try/catch below (and the dead-channel
    // cleanup) handles both paths.
    let channel;
    try {
      channel = await client.channels.fetch(target.channelId);
    } catch (err) {
      const apiCode =
        err && typeof err === 'object' && 'code' in err ? (err as { code: number }).code : null;
      // Terminal channel errors → remove the sub so we don't loop on this
      // channel for every future clip (M4 from review #111). 10003: Unknown
      // Channel · 10004: Unknown Guild · 50001: Missing Access · 50007: Cannot
      // Send Messages To This User.
      if (apiCode === 10003 || apiCode === 10004 || apiCode === 50001 || apiCode === 50007) {
        const removed = await db.removeAllSubscriptionsForChannel(target.channelId);
        log.warn('channel inaccessible — removed subscriptions', {
          channelId: target.channelId,
          clipId: clip.id,
          code: apiCode,
          removed,
        });
        // Return false so the poller halts the cursor this round; next round
        // the subs are gone and the cursor will advance cleanly.
        return false;
      }
      throw err;
    }
    if (!channel || !channel.isTextBased() || !('send' in channel)) {
      log.warn('channel unavailable or not text-based', {
        channelId: target.channelId,
        clipId: clip.id,
      });
      return false;
    }
    const content = buildMessage(
      clip,
      { pingRoleId: target.pingRoleId },
      config.GANKEDTV_PUBLIC_BASE,
    );
    return postToChannel(channel, content);
  };

  log.info('Starting poller', { intervalSeconds: config.DISCORD_POLL_INTERVAL_SECONDS });
  await startPoller({ db, api, fanout, log }, config.DISCORD_POLL_INTERVAL_SECONDS, abort.signal);
}

main().catch((err) => {
  log.error('fatal', { err: String(err), stack: err instanceof Error ? err.stack : undefined });
  process.exit(1);
});
