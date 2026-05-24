import {
  SlashCommandBuilder,
  type ChatInputCommandInteraction,
  type AutocompleteInteraction,
} from 'discord.js';
import type { Command, CommandContext } from './index.ts';
import { ephemeral } from './replies.ts';
import { shareUrl } from '../lib/shareUrl.ts';

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
  await interaction.deferReply();
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
  await interaction.editReply(shareUrl(clip.shareCode, ctx.publicBase));
}

async function handleTop(
  interaction: ChatInputCommandInteraction,
  ctx: CommandContext,
): Promise<void> {
  await interaction.deferReply();
  const window = (interaction.options.getString('window') ?? '24h') as '24h' | '7d';
  const page = await ctx.api.getFeed({ sort: 'trending', window, limit: 1 });
  const clip = page.items[0];
  if (!clip) {
    await interaction.editReply(`No trending clips in the last ${window}.`);
    return;
  }
  await interaction.editReply(shareUrl(clip.shareCode, ctx.publicBase));
}

async function handleSearch(
  interaction: ChatInputCommandInteraction,
  ctx: CommandContext,
): Promise<void> {
  await interaction.deferReply();
  const query = interaction.options.getString('query', true);
  const res = await ctx.api.search(query, { type: 'clips', limit: 1 });
  const clip = res.clips[0];
  if (!clip) {
    await interaction.editReply(`No clips matched "${query}".`);
    return;
  }
  await interaction.editReply(shareUrl(clip.shareCode, ctx.publicBase));
}

async function autocomplete(
  interaction: AutocompleteInteraction,
  ctx: CommandContext,
): Promise<void> {
  const focused = interaction.options.getFocused(true);
  if (focused.name !== 'game') return;
  const query = focused.value?.toString() ?? '';
  // Autocomplete sends slugs (matching what /games/{slug}/clips expects) so the
  // returned value can flow straight back into the API call. Tighter timeout
  // than the default 10s because Discord deadlines autocomplete at 3s — if the
  // games API is slow we'd rather respond with [] than throw UnknownInteraction.
  let games: { name: string; slug: string }[];
  try {
    games = await ctx.api.listGames({
      search: query.length > 0 ? query : undefined,
      limit: 25,
      hasClips: true,
      timeoutMs: 2500,
    });
  } catch {
    games = [];
  }
  await interaction.respond(games.slice(0, 25).map((g) => ({ name: g.name, value: g.slug })));
}

export const command: Command = { data, execute, autocomplete };
