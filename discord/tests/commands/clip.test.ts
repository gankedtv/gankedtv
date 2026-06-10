import { describe, expect, test } from 'bun:test';
import type { APIEmbed } from 'discord.js';
import { command } from '../../src/commands/clip.ts';
import { commandDefinitions, dispatchChatInput, commands } from '../../src/commands/index.ts';
import { clip } from '../factories.ts';
import { ctx, fakeApi, fakeAutocomplete, fakeChatInput } from './helpers.ts';

// Replies now carry a rich embed; the share URL lives on the embed title link.
function embedUrl(payload: unknown): string | undefined {
  return (payload as { embeds?: APIEmbed[] }).embeds?.[0]?.url;
}

describe('/clip latest', () => {
  test('posts the share URL of the latest feed clip', async () => {
    const c = clip({ shareCode: 'top1' });
    const c2 = clip({ shareCode: 'top2' });
    const f = fakeChatInput({ subcommand: 'latest' });
    const context = ctx({
      api: fakeApi({
        getFeed: async () => ({ items: [c, c2], nextCursor: null }),
      }),
    });

    await command.execute(f.interaction, context);
    expect(f.wasDeferred()).toBe(true);
    expect(embedUrl(f.replies[0]?.payload)).toBe('https://gankedtv.com/c/top1');
  });

  test('replies with "no clips" when feed is empty', async () => {
    const f = fakeChatInput({ subcommand: 'latest' });
    await command.execute(f.interaction, ctx());
    expect(f.replies[0]?.payload).toBe('No clips found.');
  });

  test('uses /games/{slug}/clips when game filter is set', async () => {
    const c = clip({ shareCode: 'gamed' });
    const f = fakeChatInput({ subcommand: 'latest', strings: { game: 'valorant' } });
    const called: { slug?: string } = {};
    const context = ctx({
      api: fakeApi({
        getClipsForGame: async (slug) => {
          called.slug = slug;
          return { items: [c], nextCursor: null };
        },
      }),
    });
    await command.execute(f.interaction, context);
    expect(called.slug).toBe('valorant');
    expect(embedUrl(f.replies[0]?.payload)).toBe('https://gankedtv.com/c/gamed');
  });

  test('/clip latest game filter with no results returns specific message', async () => {
    const f = fakeChatInput({ subcommand: 'latest', strings: { game: 'apex' } });
    await command.execute(f.interaction, ctx());
    expect(f.replies[0]?.payload).toBe('No clips found for **apex**.');
  });
});

describe('/clip top', () => {
  test('uses trending sort with the requested window', async () => {
    const c = clip({ shareCode: 'trnd' });
    const f = fakeChatInput({ subcommand: 'top', strings: { window: '7d' } });
    let captured: { sort?: string; window?: string } = {};
    const context = ctx({
      api: fakeApi({
        getFeed: async (opts) => {
          captured = { sort: opts?.sort, window: opts?.window };
          return { items: [c], nextCursor: null };
        },
      }),
    });
    await command.execute(f.interaction, context);
    expect(captured).toEqual({ sort: 'trending', window: '7d' });
    expect(embedUrl(f.replies[0]?.payload)).toBe('https://gankedtv.com/c/trnd');
  });

  test('defaults to 24h window', async () => {
    const f = fakeChatInput({ subcommand: 'top' });
    const captured: { window?: string } = {};
    const context = ctx({
      api: fakeApi({
        getFeed: async (opts) => {
          captured.window = opts?.window;
          return { items: [], nextCursor: null };
        },
      }),
    });
    await command.execute(f.interaction, context);
    expect(captured.window).toBe('24h');
    expect(f.replies[0]?.payload).toBe('No trending clips in the last 24h.');
  });
});

describe('/clip search', () => {
  test('posts the top search result', async () => {
    const c = clip({ shareCode: 'srch' });
    const f = fakeChatInput({ subcommand: 'search', strings: { query: 'flick' } });
    const context = ctx({
      api: fakeApi({
        search: async () => ({ clips: [c], games: [] }),
      }),
    });
    await command.execute(f.interaction, context);
    expect(embedUrl(f.replies[0]?.payload)).toBe('https://gankedtv.com/c/srch');
  });

  test('search with no results returns user-facing message', async () => {
    const f = fakeChatInput({ subcommand: 'search', strings: { query: 'xyzzy' } });
    await command.execute(f.interaction, ctx());
    expect(f.replies[0]?.payload).toBe('No clips matched "xyzzy".');
  });
});

describe('/clip unknown subcommand', () => {
  test('replies ephemerally', async () => {
    const f = fakeChatInput({ subcommand: 'whatever' });
    await command.execute(f.interaction, ctx());
    expect(f.replies[0]?.phase).toBe('reply');
  });
});

describe('/clip autocomplete', () => {
  test('returns slug-keyed options for game field', async () => {
    const a = fakeAutocomplete({ name: 'game', value: 'val' });
    const context = ctx({
      api: fakeApi({
        listGames: async () => [
          { id: 1, name: 'Valorant', slug: 'valorant', tag: 'val', coverUrl: null },
        ],
      }),
    });
    await command.autocomplete!(a.interaction, context);
    expect(a.responses[0]).toEqual([{ name: 'Valorant', value: 'valorant' }]);
  });

  test('ignores autocomplete for non-game fields', async () => {
    const a = fakeAutocomplete({ name: 'window', value: '2' });
    await command.autocomplete!(a.interaction, ctx());
    expect(a.responses).toHaveLength(0);
  });
});

describe('registry', () => {
  test('exposes /clip and /gankedtv', () => {
    expect(Object.keys(commands).sort()).toEqual(['clip', 'gankedtv']);
    const defs = commandDefinitions();
    expect(defs).toHaveLength(2);
    expect(defs.map((d) => d.name).sort()).toEqual(['clip', 'gankedtv']);
  });

  test('dispatchChatInput routes by command name', async () => {
    const c = clip({ shareCode: 'route' });
    const f = fakeChatInput({ subcommand: 'latest' });
    (f.interaction as unknown as { commandName: string }).commandName = 'clip';
    const context = ctx({
      api: fakeApi({ getFeed: async () => ({ items: [c], nextCursor: null }) }),
    });
    await dispatchChatInput(f.interaction, context);
    expect(embedUrl(f.replies[0]?.payload)).toBe('https://gankedtv.com/c/route');
  });

  test('dispatchChatInput replies on unknown command', async () => {
    const f = fakeChatInput({ subcommand: 'noop' });
    (f.interaction as unknown as { commandName: string }).commandName = 'nope';
    await dispatchChatInput(f.interaction, ctx());
    expect(f.replies[0]?.phase).toBe('reply');
  });
});
