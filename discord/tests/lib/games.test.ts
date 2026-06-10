import { describe, expect, test } from 'bun:test';
import type { ApiClient, GameListItem } from '../../src/api.ts';
import {
  AUTOCOMPLETE_TIMEOUT_MS,
  resolveGameId,
  respondGameAutocomplete,
  type GameAutocompleteInteraction,
} from '../../src/lib/games.ts';

const game = (overrides: Partial<GameListItem> = {}): GameListItem => ({
  id: 1,
  name: 'Valorant',
  slug: 'valorant',
  tag: 'VAL',
  coverUrl: null,
  ...overrides,
});

function apiWith(listGames: ApiClient['listGames']): ApiClient {
  return {
    getFeed: async () => ({ items: [], nextCursor: null }),
    getClipsForGame: async () => ({ items: [], nextCursor: null }),
    search: async () => ({ clips: [], games: [] }),
    listGames,
  };
}

describe('resolveGameId', () => {
  test('null input resolves to no filter', async () => {
    const result = await resolveGameId(
      apiWith(async () => []),
      null,
    );
    expect(result).toEqual({ gameId: null, gameName: null, error: null });
  });

  test('no matches yields a user-facing error', async () => {
    const result = await resolveGameId(
      apiWith(async () => []),
      'nope',
    );
    expect(result.gameId).toBeNull();
    expect(result.error).toBe('No game matched "nope".');
  });

  test('exact slug match wins over the top name match', async () => {
    const games = [
      game({ id: 1, name: 'Valorant', slug: 'valorant' }),
      game({ id: 2, name: 'Valorant Mobile', slug: 'valorant-mobile' }),
    ];
    const result = await resolveGameId(
      apiWith(async () => games),
      'valorant-mobile',
    );
    expect(result.gameId).toBe(2);
    expect(result.gameName).toBe('Valorant Mobile');
  });

  test('falls back to the first match when no exact slug hit', async () => {
    const result = await resolveGameId(
      apiWith(async () => [game({ id: 7, name: 'Apex Legends', slug: 'apex-legends' })]),
      'apex',
    );
    expect(result.gameId).toBe(7);
    expect(result.error).toBeNull();
  });
});

describe('respondGameAutocomplete', () => {
  function fakeInteraction(focusedName: string, value: string) {
    const responded: { name: string; value: string }[][] = [];
    const interaction: GameAutocompleteInteraction = {
      options: { getFocused: () => ({ name: focusedName, value }) },
      respond: async (choices) => {
        responded.push(choices);
      },
    };
    return { interaction, responded };
  }

  test('responds with slug values using the tight autocomplete timeout', async () => {
    const { interaction, responded } = fakeInteraction('game', 'val');
    let captured: { search?: string; timeoutMs?: number; hasClips?: boolean } = {};
    const api = apiWith(async (opts) => {
      captured = { search: opts?.search, timeoutMs: opts?.timeoutMs, hasClips: opts?.hasClips };
      return [game()];
    });

    await respondGameAutocomplete(interaction, api);

    expect(captured).toEqual({ search: 'val', timeoutMs: AUTOCOMPLETE_TIMEOUT_MS, hasClips: true });
    expect(responded[0]).toEqual([{ name: 'Valorant', value: 'valorant' }]);
  });

  test('empty input omits the search term (shows games-with-clips)', async () => {
    const { interaction } = fakeInteraction('game', '');
    let search: string | undefined = 'sentinel';
    const api = apiWith(async (opts) => {
      search = opts?.search;
      return [];
    });

    await respondGameAutocomplete(interaction, api);

    expect(search).toBeUndefined();
  });

  test('API failure responds with an empty list instead of throwing', async () => {
    const { interaction, responded } = fakeInteraction('game', 'x');
    const api = apiWith(async () => {
      throw new Error('api down');
    });

    await respondGameAutocomplete(interaction, api);

    expect(responded[0]).toEqual([]);
  });

  test('non-game focus is ignored', async () => {
    const { interaction, responded } = fakeInteraction('creator', 'x');

    await respondGameAutocomplete(
      interaction,
      apiWith(async () => []),
    );

    expect(responded).toHaveLength(0);
  });
});
