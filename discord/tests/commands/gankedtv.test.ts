import { describe, expect, test } from 'bun:test';
import { command } from '../../src/commands/gankedtv.ts';
import { dispatchAutocomplete } from '../../src/commands/index.ts';
import { subscription } from '../factories.ts';
import { ctx, fakeApi, fakeAutocomplete, fakeChatInput, fakeDb } from './helpers.ts';

describe('/gankedtv subscribe', () => {
  test('mutation requires ManageChannels', async () => {
    const f = fakeChatInput({ subcommand: 'subscribe', hasManageChannels: false });
    await command.execute(f.interaction, ctx());
    expect(f.replies[0]?.payload).toMatchObject({
      content: expect.stringContaining('Manage Channels'),
    });
  });

  test('all-filters-null subscribe records the firehose', async () => {
    const f = fakeChatInput({ subcommand: 'subscribe' });
    let inserted: unknown = null;
    const db = fakeDb({
      addSubscription: async (input) => {
        inserted = input;
        return subscription({ ...input });
      },
    });
    await command.execute(f.interaction, ctx({ db }));
    expect(inserted).toMatchObject({ gameId: null, creatorId: null });
    expect(f.replies[0]?.payload).toMatch(/Subscribed.*all clips/);
  });

  test('duplicate subscribe reports already-subscribed', async () => {
    const f = fakeChatInput({ subcommand: 'subscribe' });
    const db = fakeDb({ addSubscription: async () => null });
    await command.execute(f.interaction, ctx({ db }));
    expect(f.replies[0]?.payload).toMatch(/already subscribed/);
  });

  test('game filter resolves via search when name passed', async () => {
    const f = fakeChatInput({ subcommand: 'subscribe', strings: { game: 'valorant' } });
    const captured: { gameId: number | null } = { gameId: null };
    const db = fakeDb({
      addSubscription: async (input) => {
        captured.gameId = input.gameId;
        return subscription({ ...input });
      },
    });
    const api = fakeApi({
      listGames: async () => [
        { id: 42, name: 'Valorant', slug: 'valorant', tag: 'val', coverUrl: null },
      ],
    });
    await command.execute(f.interaction, ctx({ db, api }));
    expect(captured.gameId).toBe(42);
    expect(f.replies[0]?.payload).toMatch(/Valorant/);
  });

  test('exact slug match wins over top name match', async () => {
    // Autocomplete returned `valorant` (slug). The API returns multiple games
    // where the top result by name relevance is something else — the resolver
    // should still pick the exact-slug match.
    const f = fakeChatInput({ subcommand: 'subscribe', strings: { game: 'valorant' } });
    const captured: { gameId: number | null } = { gameId: null };
    const db = fakeDb({
      addSubscription: async (input) => {
        captured.gameId = input.gameId;
        return subscription({ ...input });
      },
    });
    const api = fakeApi({
      listGames: async () => [
        { id: 1, name: 'Valorant Mobile', slug: 'valorant-mobile', tag: 'valm', coverUrl: null },
        { id: 42, name: 'Valorant', slug: 'valorant', tag: 'val', coverUrl: null },
      ],
    });
    await command.execute(f.interaction, ctx({ db, api }));
    expect(captured.gameId).toBe(42);
  });

  test('falls back to top match when no exact slug match', async () => {
    const f = fakeChatInput({ subcommand: 'subscribe', strings: { game: 'val' } });
    const captured: { gameId: number | null } = { gameId: null };
    const db = fakeDb({
      addSubscription: async (input) => {
        captured.gameId = input.gameId;
        return subscription({ ...input });
      },
    });
    const api = fakeApi({
      listGames: async () => [
        { id: 1, name: 'Valorant', slug: 'valorant', tag: 'val', coverUrl: null },
      ],
    });
    await command.execute(f.interaction, ctx({ db, api }));
    expect(captured.gameId).toBe(1);
  });

  test('creator filter validates UUID format and rejects garbage', async () => {
    const f = fakeChatInput({ subcommand: 'subscribe', strings: { creator: 'banana' } });
    const db = fakeDb({
      addSubscription: async () => {
        throw new Error('should not be called');
      },
    });
    await command.execute(f.interaction, ctx({ db }));
    expect(f.replies[0]?.payload).toMatch(/must be a UUID/);
  });

  test('creator filter accepts valid UUID', async () => {
    const uuid = '11111111-2222-3333-4444-555555555555';
    const f = fakeChatInput({ subcommand: 'subscribe', strings: { creator: uuid } });
    const captured: { creatorId: string | null } = { creatorId: null };
    const db = fakeDb({
      addSubscription: async (input) => {
        captured.creatorId = input.creatorId;
        return subscription({ ...input });
      },
    });
    await command.execute(f.interaction, ctx({ db }));
    expect(captured.creatorId).toBe(uuid);
    expect(f.replies[0]?.payload).toMatch(/creator/);
  });

  test('game filter with no matches surfaces error to user', async () => {
    const f = fakeChatInput({ subcommand: 'subscribe', strings: { game: 'asdfzzz' } });
    await command.execute(f.interaction, ctx());
    expect(f.replies[0]?.payload).toMatch(/No game matched/);
  });
});

describe('/gankedtv unsubscribe', () => {
  test('removes when match exists', async () => {
    const f = fakeChatInput({ subcommand: 'unsubscribe' });
    const db = fakeDb({ removeSubscription: async () => 1 });
    await command.execute(f.interaction, ctx({ db }));
    expect(f.replies[0]?.payload).toBe('Removed 1 subscription.');
  });

  test('reports no-match when nothing removed', async () => {
    const f = fakeChatInput({ subcommand: 'unsubscribe' });
    await command.execute(f.interaction, ctx());
    expect(f.replies[0]?.payload).toMatch(/No matching subscription/);
  });

  test('plural messaging for multi-row removal', async () => {
    const f = fakeChatInput({ subcommand: 'unsubscribe' });
    const db = fakeDb({ removeSubscription: async () => 3 });
    await command.execute(f.interaction, ctx({ db }));
    expect(f.replies[0]?.payload).toBe('Removed 3 subscriptions.');
  });

  test('unresolvable game filter blocks before deletion', async () => {
    const f = fakeChatInput({ subcommand: 'unsubscribe', strings: { game: 'nonsense' } });
    const db = fakeDb({
      removeSubscription: async () => {
        throw new Error('should not be called');
      },
    });
    await command.execute(f.interaction, ctx({ db }));
    expect(f.replies[0]?.payload).toMatch(/No game matched/);
  });

  test('invalid creator UUID is rejected before deletion', async () => {
    const f = fakeChatInput({ subcommand: 'unsubscribe', strings: { creator: 'not-a-uuid' } });
    const db = fakeDb({
      removeSubscription: async () => {
        throw new Error('should not be called');
      },
    });
    await command.execute(f.interaction, ctx({ db }));
    expect(f.replies[0]?.payload).toMatch(/must be a UUID/);
  });

  test('all:true wipes every subscription in the channel', async () => {
    const f = fakeChatInput({ subcommand: 'unsubscribe', booleans: { all: true } });
    const db = fakeDb({ removeAllSubscriptionsForChannel: async () => 3 });
    await command.execute(f.interaction, ctx({ db }));
    expect(f.replies[0]?.payload).toBe('Removed all 3 subscriptions in this channel.');
  });

  test('all:true on an empty channel says nothing was removed', async () => {
    const f = fakeChatInput({ subcommand: 'unsubscribe', booleans: { all: true } });
    await command.execute(f.interaction, ctx());
    expect(f.replies[0]?.payload).toBe('No subscriptions in this channel.');
  });

  test('all:true combined with filters is rejected', async () => {
    const f = fakeChatInput({
      subcommand: 'unsubscribe',
      booleans: { all: true },
      strings: { game: 'valorant' },
    });
    const db = fakeDb({
      removeAllSubscriptionsForChannel: async () => {
        throw new Error('should not be called');
      },
      removeSubscription: async () => {
        throw new Error('should not be called');
      },
    });
    await command.execute(f.interaction, ctx({ db }));
    expect(f.replies[0]?.payload).toMatch(/Cannot combine `all:true` with/);
  });

  test('all:false falls through to filtered delete', async () => {
    const f = fakeChatInput({ subcommand: 'unsubscribe', booleans: { all: false } });
    const db = fakeDb({ removeSubscription: async () => 1 });
    await command.execute(f.interaction, ctx({ db }));
    expect(f.replies[0]?.payload).toBe('Removed 1 subscription.');
  });
});

describe('/gankedtv subscriptions', () => {
  test('empty channel returns "no subscriptions"', async () => {
    const f = fakeChatInput({ subcommand: 'subscriptions' });
    await command.execute(f.interaction, ctx());
    expect(f.replies[0]?.payload).toMatch(/No subscriptions/);
  });

  test('resolves game id to name + slug via the API', async () => {
    const f = fakeChatInput({ subcommand: 'subscriptions' });
    const db = fakeDb({
      listSubscriptionsForChannel: async () => [
        subscription({ gameId: 42, pingRoleId: '555', paused: true }),
        subscription({
          gameId: 99,
          creatorId: '11111111-2222-3333-4444-555555555555',
        }),
        subscription(), // firehose
      ],
    });
    const api = fakeApi({
      listGames: async () => [
        { id: 42, name: 'Valorant', slug: 'valorant', tag: 'val', coverUrl: null },
        // 99 deliberately missing — exercise the unresolved-id fallback path
      ],
    });
    await command.execute(f.interaction, ctx({ db, api }));
    const text = String(f.replies[0]?.payload);

    // Count header
    expect(text).toMatch(/3 subscriptions in this channel/);
    // Resolved game (#42)
    expect(text).toMatch(/Valorant.*valorant/);
    // Unresolved game (#99) falls back to id
    expect(text).toMatch(/game id `99`/);
    // Creator + ping + paused
    expect(text).toMatch(/creator `11111111/);
    expect(text).toMatch(/ping <@&555>/);
    expect(text).toMatch(/paused/);
    // Firehose row
    expect(text).toMatch(/all clips/);
    // Actionable hint mentions both single + bulk removal
    expect(text).toMatch(/unsubscribe game:<slug>/);
    expect(text).toMatch(/unsubscribe all:true/);
  });

  test('singular "1 subscription" header when only one row', async () => {
    const f = fakeChatInput({ subcommand: 'subscriptions' });
    const db = fakeDb({ listSubscriptionsForChannel: async () => [subscription()] });
    await command.execute(f.interaction, ctx({ db }));
    expect(String(f.replies[0]?.payload)).toMatch(/1 subscription in this channel/);
  });

  test('empty channel hint mentions /gankedtv subscribe', async () => {
    const f = fakeChatInput({ subcommand: 'subscriptions' });
    await command.execute(f.interaction, ctx());
    expect(String(f.replies[0]?.payload)).toMatch(/Use `\/gankedtv subscribe`/);
  });
});

describe('/gankedtv pause + resume', () => {
  test('pause without subscriptions returns no-op message', async () => {
    const f = fakeChatInput({ subcommand: 'pause' });
    await command.execute(f.interaction, ctx());
    expect(f.replies[0]?.payload).toMatch(/No subscriptions/);
  });

  test('pause updates and confirms count', async () => {
    const f = fakeChatInput({ subcommand: 'pause' });
    const db = fakeDb({ setPaused: async () => 2 });
    await command.execute(f.interaction, ctx({ db }));
    expect(f.replies[0]?.payload).toBe('Paused 2 subscriptions.');
  });

  test('resume confirms count', async () => {
    const f = fakeChatInput({ subcommand: 'resume' });
    const db = fakeDb({ setPaused: async () => 1 });
    await command.execute(f.interaction, ctx({ db }));
    expect(f.replies[0]?.payload).toBe('Resumed 1 subscription.');
  });
});

describe('/gankedtv DM safety', () => {
  test('replies ephemerally outside a guild', async () => {
    const f = fakeChatInput({ subcommand: 'subscribe', guildId: null });
    await command.execute(f.interaction, ctx());
    expect(f.replies[0]?.payload).toMatchObject({
      content: expect.stringContaining('inside a server'),
    });
  });

  test('unknown subcommand surfaces ephemeral fallback', async () => {
    const f = fakeChatInput({ subcommand: 'weird' });
    await command.execute(f.interaction, ctx());
    expect(f.replies[0]?.payload).toMatchObject({ content: 'Unknown subcommand.' });
  });
});

describe('/gankedtv autocomplete', () => {
  test('returns slug-keyed options (matches /clip autocomplete)', async () => {
    const a = fakeAutocomplete({ name: 'game', value: 'val' });
    const api = fakeApi({
      listGames: async () => [
        { id: 1, name: 'Valorant', slug: 'valorant', tag: 'val', coverUrl: null },
      ],
    });
    await command.autocomplete!(a.interaction, ctx({ api }));
    expect(a.responses[0]).toEqual([{ name: 'Valorant', value: 'valorant' }]);
  });

  test('skips when focused field is not "game"', async () => {
    const a = fakeAutocomplete({ name: 'creator', value: 'x' });
    await command.autocomplete!(a.interaction, ctx());
    expect(a.responses).toHaveLength(0);
  });

  test('dispatchAutocomplete forwards to the right command', async () => {
    const a = fakeAutocomplete({ name: 'game', value: '' });
    (a.interaction as unknown as { commandName: string }).commandName = 'gankedtv';
    const api = fakeApi({
      listGames: async () => [{ id: 7, name: 'CS2', slug: 'cs2', tag: 'cs', coverUrl: null }],
    });
    await dispatchAutocomplete(a.interaction, ctx({ api }));
    expect(a.responses[0]).toEqual([{ name: 'CS2', value: 'cs2' }]);
  });

  test('dispatchAutocomplete no-ops for unknown command', async () => {
    const a = fakeAutocomplete({ name: 'game', value: '' });
    (a.interaction as unknown as { commandName: string }).commandName = 'nope';
    await dispatchAutocomplete(a.interaction, ctx());
    expect(a.responses).toHaveLength(0);
  });
});
