import {
  SlashCommandBuilder,
  type ChatInputCommandInteraction,
  type AutocompleteInteraction,
} from 'discord.js';
import type { Command, CommandContext } from './index.ts';
import { ephemeral, safeDefer } from './replies.ts';
import { buildClipEmbed } from '../embeds.ts';
import { respondGameAutocomplete } from '../lib/games.ts';

const data = new SlashCommandBuilder()
  .setName('clip')
  .setDescription('Pull GankedTV clips on demand.')
  .addSubcommand((s) =>
    s
      .setName('latest')
      .setDescription('Post the latest public clip.')
      .addStringOption((o) =>
        o
          .setName('game')
          .setDescription('Restrict to a specific game (slug).')
          .setAutocomplete(true)
          .setRequired(false),
      ),
  )
  .addSubcommand((s) =>
    s
      .setName('top')
      .setDescription('Post the top trending clip.')
      .addStringOption((o) =>
        o
          .setName('window')
          .setDescription('Trending window.')
          .addChoices({ name: 'Last 24 hours', value: '24h' }, { name: 'Last 7 days', value: '7d' })
          .setRequired(false),
      ),
  )
  .addSubcommand((s) =>
    s
      .setName('search')
      .setDescription('Search clips by keyword and post the top match.')
      .addStringOption((o) => o.setName('query').setDescription('Search terms.').setRequired(true)),
  );

async function execute(
  interaction: ChatInputCommandInteraction,
  ctx: CommandContext,
): Promise<void> {
  const sub = interaction.options.getSubcommand();
  switch (sub) {
    case 'latest':
      return handleLatest(interaction, ctx);
    case 'top':
      return handleTop(interaction, ctx);
    case 'search':
      return handleSearch(interaction, ctx);
    default:
      await interaction.reply(ephemeral('Unknown subcommand.'));
  }
}

async function handleLatest(
  interaction: ChatInputCommandInteraction,
  ctx: CommandContext,
): Promise<void> {
  if (!(await safeDefer(interaction))) return;
  const gameSlug = interaction.options.getString('game');

  const page = gameSlug
    ? await ctx.api.getClipsForGame(gameSlug, { limit: 1 })
    : await ctx.api.getFeed({ limit: 1 });

  const clip = page.items[0];
  if (!clip) {
    await interaction.editReply(
      gameSlug ? `No clips found for **${gameSlug}**.` : 'No clips found.',
    );
    return;
  }
  await interaction.editReply({ embeds: [buildClipEmbed(clip, ctx.publicBase).toJSON()] });
}

async function handleTop(
  interaction: ChatInputCommandInteraction,
  ctx: CommandContext,
): Promise<void> {
  if (!(await safeDefer(interaction))) return;
  const window = (interaction.options.getString('window') ?? '24h') as '24h' | '7d';
  const page = await ctx.api.getFeed({ sort: 'trending', window, limit: 1 });
  const clip = page.items[0];
  if (!clip) {
    await interaction.editReply(`No trending clips in the last ${window}.`);
    return;
  }
  await interaction.editReply({ embeds: [buildClipEmbed(clip, ctx.publicBase).toJSON()] });
}

async function handleSearch(
  interaction: ChatInputCommandInteraction,
  ctx: CommandContext,
): Promise<void> {
  if (!(await safeDefer(interaction))) return;
  const query = interaction.options.getString('query', true);
  const res = await ctx.api.search(query, { type: 'clips', limit: 1 });
  const clip = res.clips[0];
  if (!clip) {
    await interaction.editReply(`No clips matched "${query}".`);
    return;
  }
  await interaction.editReply({ embeds: [buildClipEmbed(clip, ctx.publicBase).toJSON()] });
}

async function autocomplete(
  interaction: AutocompleteInteraction,
  ctx: CommandContext,
): Promise<void> {
  await respondGameAutocomplete(interaction, ctx.api);
}

export const command: Command = { data, execute, autocomplete };
