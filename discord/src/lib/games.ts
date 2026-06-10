import type { ApiClient } from '../api.ts';

// Tight timeout for autocomplete API calls: Discord deadlines autocomplete at
// 3s — a slow games API would otherwise leave the interaction token expired
// (UnknownInteraction). Shared by /gankedtv and /clip.
export const AUTOCOMPLETE_TIMEOUT_MS = 2500;

export type ResolvedGame = {
  gameId: number | null;
  gameName: string | null;
  error: string | null;
};

export async function resolveGameId(api: ApiClient, raw: string | null): Promise<ResolvedGame> {
  if (!raw) return { gameId: null, gameName: null, error: null };
  // The autocomplete handler returns `value: g.slug`, so the happy path here
  // is a slug match. Manual typing falls back to a name search.
  const matches = await api.listGames({ search: raw, limit: 25 });
  if (matches.length === 0)
    return { gameId: null, gameName: null, error: `No game matched "${raw}".` };
  // Prefer an exact slug hit (autocomplete-supplied) over the top name match,
  // so picking a less-popular autocomplete suggestion doesn't get overridden
  // by a more popular substring match.
  const exact = matches.find((g) => g.slug === raw);
  const chosen = exact ?? matches[0]!;
  return { gameId: chosen.id, gameName: chosen.name, error: null };
}

// Structural slice of discord.js's AutocompleteInteraction so tests can pass fakes.
export type GameAutocompleteInteraction = {
  options: { getFocused(getFull: true): { name: string; value: string | number } };
  respond(choices: { name: string; value: string }[]): Promise<unknown>;
};

// Shared 'game' option autocomplete for /gankedtv and /clip. Empty input shows
// the first 25 games-with-clips so users see *something*; non-empty input is a
// case-insensitive substring search. Responds with slugs so the chosen value
// flows straight into resolveGameId's exact-slug shortcut (or /games/{slug}/clips).
export async function respondGameAutocomplete(
  interaction: GameAutocompleteInteraction,
  api: ApiClient,
): Promise<void> {
  const focused = interaction.options.getFocused(true);
  if (focused.name !== 'game') return;
  const query = focused.value?.toString() ?? '';
  let games: { name: string; slug: string }[];
  try {
    games = await api.listGames({
      search: query.length > 0 ? query : undefined,
      limit: 25,
      hasClips: true,
      timeoutMs: AUTOCOMPLETE_TIMEOUT_MS,
    });
  } catch {
    games = [];
  }
  await interaction.respond(games.slice(0, 25).map((g) => ({ name: g.name, value: g.slug })));
}
