import {
  MessageFlags,
  SlashCommandBuilder,
  type AutocompleteInteraction,
  type ChatInputCommandInteraction,
} from 'discord.js';
import type { Command, CommandContext } from './index.ts';
import { ephemeral, safeDefer } from './replies.ts';
import { resolveGameId, respondGameAutocomplete } from '../lib/games.ts';
import { requireManageChannels } from '../lib/permissions.ts';
import type { Subscription } from '../db.ts';

const data = new SlashCommandBuilder()
  .setName('gankedtv')
  .setDescription('Manage clip subscriptions for this channel.')
  .addSubcommand((s) =>
    s
      .setName('subscribe')
      .setDescription('Subscribe this channel to new GankedTV clips.')
      .addStringOption((o) =>
        o
          .setName('game')
          .setDescription('Only post clips for this game.')
          .setAutocomplete(true)
          .setRequired(false),
      )
      .addStringOption((o) =>
        o
          .setName('creator')
          .setDescription('Only post clips from this creator (uuid).')
          .setRequired(false),
      )
      .addRoleOption((o) =>
        o.setName('ping_role').setDescription('Role to ping on each post.').setRequired(false),
      ),
  )
  .addSubcommand((s) =>
    s
      .setName('unsubscribe')
      .setDescription('Remove a subscription from this channel. Pass all:true to wipe everything.')
      .addStringOption((o) =>
        o
          .setName('game')
          .setDescription('Game filter of the subscription to remove.')
          .setAutocomplete(true)
          .setRequired(false),
      )
      .addStringOption((o) =>
        o
          .setName('creator')
          .setDescription('Creator filter of the subscription to remove (uuid).')
          .setRequired(false),
      )
      .addBooleanOption((o) =>
        o
          .setName('all')
          .setDescription('Remove EVERY subscription in this channel. Cannot combine with filters.')
          .setRequired(false),
      ),
  )
  .addSubcommand((s) =>
    s.setName('subscriptions').setDescription('List subscriptions for this channel.'),
  )
  .addSubcommand((s) =>
    s.setName('pause').setDescription('Pause all subscriptions in this channel.'),
  )
  .addSubcommand((s) =>
    s.setName('resume').setDescription('Resume paused subscriptions in this channel.'),
  );

async function execute(
  interaction: ChatInputCommandInteraction,
  ctx: CommandContext,
): Promise<void> {
  if (!interaction.inGuild()) {
    await interaction.reply(ephemeral('This command only works inside a server.'));
    return;
  }
  const sub = interaction.options.getSubcommand();

  // Read-only subcommands stay open to all members; mutations gate on ManageChannels
  // so random members can't reconfigure the bot.
  const isMutation =
    sub === 'subscribe' || sub === 'unsubscribe' || sub === 'pause' || sub === 'resume';
  if (isMutation && !requireManageChannels(interaction)) {
    await interaction.reply(ephemeral('You need the **Manage Channels** permission to do that.'));
    return;
  }

  switch (sub) {
    case 'subscribe':
      return handleSubscribe(interaction, ctx);
    case 'unsubscribe':
      return handleUnsubscribe(interaction, ctx);
    case 'subscriptions':
      return handleList(interaction, ctx);
    case 'pause':
      return handlePauseToggle(interaction, ctx, true);
    case 'resume':
      return handlePauseToggle(interaction, ctx, false);
    default:
      await interaction.reply(ephemeral('Unknown subcommand.'));
  }
}

// UUID v4-ish: 8-4-4-4-12 hex with dashes. Used to validate `creator` filter
// input before it hits the UUID column (otherwise Postgres throws a type error
// that surfaces to the user as a generic "Something went wrong").
const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function validateCreator(raw: string | null): { creatorId: string | null; error: string | null } {
  if (!raw) return { creatorId: null, error: null };
  if (!UUID_RE.test(raw))
    return { creatorId: null, error: `Creator must be a UUID, got "${raw}".` };
  return { creatorId: raw.toLowerCase(), error: null };
}

async function handleSubscribe(
  interaction: ChatInputCommandInteraction,
  ctx: CommandContext,
): Promise<void> {
  if (!(await safeDefer(interaction, { flags: MessageFlags.Ephemeral }))) return;

  const gameRaw = interaction.options.getString('game');
  const creatorRaw = interaction.options.getString('creator');
  const pingRole = interaction.options.getRole('ping_role');

  const { gameId, gameName, error: gameErr } = await resolveGameId(ctx.api, gameRaw);
  if (gameErr) {
    await interaction.editReply(gameErr);
    return;
  }
  const { creatorId, error: creatorErr } = validateCreator(creatorRaw);
  if (creatorErr) {
    await interaction.editReply(creatorErr);
    return;
  }

  const result = await ctx.db.addSubscription({
    guildId: interaction.guildId!,
    channelId: interaction.channelId,
    gameId,
    creatorId,
    pingRoleId: pingRole?.id ?? null,
    createdBy: interaction.user.id,
  });

  if (result === null) {
    await interaction.editReply('This channel is already subscribed with those filters.');
    return;
  }

  const filterParts: string[] = [];
  if (gameName) filterParts.push(`game **${gameName}**`);
  if (creatorId) filterParts.push(`creator \`${creatorId}\``);
  const filterDesc = filterParts.length
    ? ` filtered by ${filterParts.join(' + ')}`
    : ' (all clips)';
  await interaction.editReply(`Subscribed${filterDesc}. New clips will post here.`);
}

async function handleUnsubscribe(
  interaction: ChatInputCommandInteraction,
  ctx: CommandContext,
): Promise<void> {
  if (!(await safeDefer(interaction, { flags: MessageFlags.Ephemeral }))) return;

  const gameRaw = interaction.options.getString('game');
  const creatorRaw = interaction.options.getString('creator');
  const all = interaction.options.getBoolean('all') ?? false;

  // Reject ambiguous input rather than silently dropping the filters — a user
  // who passed both clearly meant one of them and shouldn't get a wipe by
  // accident.
  if (all && (gameRaw !== null || creatorRaw !== null)) {
    await interaction.editReply(
      'Cannot combine `all:true` with `game`/`creator` filters. Use one or the other.',
    );
    return;
  }

  if (all) {
    const removed = await ctx.db.removeAllSubscriptionsForChannel(interaction.channelId);
    if (removed === 0) {
      await interaction.editReply('No subscriptions in this channel.');
      return;
    }
    await interaction.editReply(
      `Removed all ${removed} subscription${removed === 1 ? '' : 's'} in this channel.`,
    );
    return;
  }

  const { gameId, error: gameErr } = await resolveGameId(ctx.api, gameRaw);
  if (gameErr) {
    await interaction.editReply(gameErr);
    return;
  }
  const { creatorId, error: creatorErr } = validateCreator(creatorRaw);
  if (creatorErr) {
    await interaction.editReply(creatorErr);
    return;
  }

  const removed = await ctx.db.removeSubscription({
    channelId: interaction.channelId,
    gameId,
    creatorId,
  });
  if (removed === 0) {
    await interaction.editReply('No matching subscription found.');
    return;
  }
  await interaction.editReply(`Removed ${removed} subscription${removed === 1 ? '' : 's'}.`);
}

async function handleList(
  interaction: ChatInputCommandInteraction,
  ctx: CommandContext,
): Promise<void> {
  if (!(await safeDefer(interaction, { flags: MessageFlags.Ephemeral }))) return;
  const subs = await ctx.db.listSubscriptionsForChannel(interaction.channelId);
  if (subs.length === 0) {
    await interaction.editReply(
      'No subscriptions in this channel. Use `/gankedtv subscribe` to add one.',
    );
    return;
  }

  // Single batched lookup so we can show names/slugs instead of raw numeric
  // ids — formatSubscription falls back to the id when a game isn't in the
  // cache (covers catalogs > listGames's 50-item cap).
  const games = await ctx.api.listGames({ limit: 50 });
  const byId = new Map(games.map((g) => [g.id, g] as const));

  const lines = subs.map((s, i) => formatSubscription(s, i + 1, byId));
  const count = subs.length === 1 ? '1 subscription' : `${subs.length} subscriptions`;
  await interaction.editReply(
    `**${count} in this channel:**\n${lines.join('\n')}\n\n` +
      '_Remove with `/gankedtv unsubscribe game:<slug>` (or `/gankedtv unsubscribe all:true` to wipe all)._',
  );
}

function formatSubscription(
  sub: Subscription,
  index: number,
  byId: Map<number, { name: string; slug: string }>,
): string {
  const parts: string[] = [];
  if (sub.gameId !== null) {
    const g = byId.get(sub.gameId);
    parts.push(g ? `game **${g.name}** (\`${g.slug}\`)` : `game id \`${sub.gameId}\``);
  }
  if (sub.creatorId !== null) parts.push(`creator \`${sub.creatorId}\``);
  if (sub.pingRoleId) parts.push(`ping <@&${sub.pingRoleId}>`);
  if (sub.paused) parts.push('**paused**');
  // If no filters were set this is the firehose — say so explicitly rather
  // than printing an empty bullet.
  const body = parts.length ? parts.join(' · ') : '_all clips_';
  return `${index}. ${body}`;
}

async function handlePauseToggle(
  interaction: ChatInputCommandInteraction,
  ctx: CommandContext,
  paused: boolean,
): Promise<void> {
  if (!(await safeDefer(interaction, { flags: MessageFlags.Ephemeral }))) return;
  const changed = await ctx.db.setPaused(interaction.channelId, paused);
  if (changed === 0) {
    await interaction.editReply('No subscriptions in this channel.');
    return;
  }
  await interaction.editReply(
    paused
      ? `Paused ${changed} subscription${changed === 1 ? '' : 's'}.`
      : `Resumed ${changed} subscription${changed === 1 ? '' : 's'}.`,
  );
}

async function autocomplete(
  interaction: AutocompleteInteraction,
  ctx: CommandContext,
): Promise<void> {
  await respondGameAutocomplete(interaction, ctx.api);
}

export const command: Command = { data, execute, autocomplete };
