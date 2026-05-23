import {
  MessageFlags,
  SlashCommandBuilder,
  type AutocompleteInteraction,
  type ChatInputCommandInteraction,
} from 'discord.js';
import type { Command, CommandContext } from './index.ts';
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
      .setDescription('Remove a subscription from this channel.')
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

const ephemeral = (content: string) => ({
  content,
  flags: MessageFlags.Ephemeral as MessageFlags.Ephemeral,
});

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

async function resolveGameId(
  ctx: CommandContext,
  raw: string | null,
): Promise<{ gameId: number | null; gameName: string | null; error: string | null }> {
  if (!raw) return { gameId: null, gameName: null, error: null };
  // The autocomplete handler returns `value: g.slug`, so the happy path here
  // is a slug match. Manual typing falls back to a name search.
  const matches = await ctx.api.listGames({ search: raw, limit: 25 });
  if (matches.length === 0)
    return { gameId: null, gameName: null, error: `No game matched "${raw}".` };
  // Prefer an exact slug hit (autocomplete-supplied) over the top name match,
  // so picking a less-popular autocomplete suggestion doesn't get overridden
  // by a more popular substring match.
  const exact = matches.find((g) => g.slug === raw);
  const chosen = exact ?? matches[0]!;
  return { gameId: chosen.id, gameName: chosen.name, error: null };
}

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
  await interaction.deferReply({ flags: MessageFlags.Ephemeral });

  const gameRaw = interaction.options.getString('game');
  const creatorRaw = interaction.options.getString('creator');
  const pingRole = interaction.options.getRole('ping_role');

  const { gameId, gameName, error: gameErr } = await resolveGameId(ctx, gameRaw);
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
  await interaction.deferReply({ flags: MessageFlags.Ephemeral });

  const gameRaw = interaction.options.getString('game');
  const creatorRaw = interaction.options.getString('creator');

  const { gameId, error: gameErr } = await resolveGameId(ctx, gameRaw);
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
  await interaction.deferReply({ flags: MessageFlags.Ephemeral });
  const subs = await ctx.db.listSubscriptionsForChannel(interaction.channelId);
  if (subs.length === 0) {
    await interaction.editReply('No subscriptions in this channel.');
    return;
  }
  const lines = subs.map(formatSubscription);
  await interaction.editReply(`**Subscriptions in this channel:**\n${lines.join('\n')}`);
}

function formatSubscription(sub: Subscription): string {
  const parts: string[] = [];
  parts.push(sub.gameId !== null ? `game=${sub.gameId}` : 'game=any');
  parts.push(sub.creatorId !== null ? `creator=${sub.creatorId}` : 'creator=any');
  if (sub.pingRoleId) parts.push(`ping=<@&${sub.pingRoleId}>`);
  if (sub.paused) parts.push('**paused**');
  return `• ${parts.join(' · ')}`;
}

async function handlePauseToggle(
  interaction: ChatInputCommandInteraction,
  ctx: CommandContext,
  paused: boolean,
): Promise<void> {
  await interaction.deferReply({ flags: MessageFlags.Ephemeral });
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
  const focused = interaction.options.getFocused(true);
  if (focused.name !== 'game') return;
  const query = focused.value?.toString() ?? '';
  // Empty input: show first 25 games-with-clips so users see *something* rather
  // than nothing. Non-empty: case-insensitive substring search via the games API.
  // Returns slug values (matching /clip autocomplete) so resolveGameId can take
  // an exact-slug shortcut over the noisier name search.
  const games = await ctx.api.listGames({
    search: query.length > 0 ? query : undefined,
    limit: 25,
    hasClips: true,
  });
  await interaction.respond(games.slice(0, 25).map((g) => ({ name: g.name, value: g.slug })));
}

export const command: Command = { data, execute, autocomplete };
