import { describe, expect, test, mock } from 'bun:test';
import { pollOnce, startPoller, type Fanout, type PollerLogger } from '../src/poller.ts';
import type { ApiClient, ClipFeedItem, ClipFeedResponse } from '../src/api.ts';
import type { Db, Subscription } from '../src/db.ts';
import { clip, subscription } from './factories.ts';

const silentLog: PollerLogger = { info: () => {}, warn: () => {}, error: () => {} };

type StubDbState = {
  state: Map<string, string>;
  subs: Subscription[];
  postLog: Set<string>;
};

// `cursor` is sugar: every poller test needs to anchor the
// `last_clip_created_at` key, and writing the full Map literal each time made
// the intent of each test hard to spot. Passes through to `state` if both are
// supplied (explicit `state` wins so a test can still set arbitrary keys).
type StubDbInit = Partial<StubDbState> & { cursor?: string };

function stubDb(initial: StubDbInit = {}): Db & { _state: StubDbState } {
  const state =
    initial.state ??
    (initial.cursor !== undefined
      ? new Map([['last_clip_created_at', initial.cursor]])
      : new Map());
  const s: StubDbState = {
    state,
    subs: initial.subs ?? [],
    postLog: initial.postLog ?? new Set(),
  };
  return {
    _state: s,
    sql: null as never,
    async close() {},
    async addSubscription() {
      throw new Error('not used in poller tests');
    },
    async removeSubscription() {
      return 0;
    },
    async removeAllSubscriptionsForChannel() {
      return 0;
    },
    async listSubscriptionsForChannel() {
      return [];
    },
    async listAllSubscriptions() {
      return s.subs.filter((sub) => !sub.paused);
    },
    async setPaused() {
      return 0;
    },
    async isPosted(channelId, clipId) {
      return s.postLog.has(`${channelId}:${clipId}`);
    },
    async recordPost(channelId, clipId) {
      s.postLog.add(`${channelId}:${clipId}`);
    },
    async getState(key) {
      return s.state.get(key) ?? null;
    },
    async setState(key, value) {
      s.state.set(key, value);
    },
  };
}

// Default API returns a single page with no nextCursor — covers the no-
// pagination tests. Pagination tests use stubPagedApi below.
function stubApi(items: ClipFeedItem[]): ApiClient {
  return {
    async getFeed(): Promise<ClipFeedResponse> {
      return { items, nextCursor: null };
    },
    async getClipsForGame() {
      return { items: [], nextCursor: null };
    },
    async search() {
      return { clips: [], games: [] };
    },
    async listGames() {
      return [];
    },
  };
}

// Pagination stub: hand it N pages keyed by cursor (empty-string cursor =
// initial fetch). Each page has its own nextCursor so the poller walks the
// chain. Tracks how many pages were actually requested for assertions.
function stubPagedApi(
  pages: Map<string, ClipFeedResponse>,
): ApiClient & { callCount: () => number } {
  let calls = 0;
  return {
    callCount: () => calls,
    async getFeed(opts) {
      calls++;
      const key = opts?.cursor ?? '';
      const page = pages.get(key);
      if (!page) throw new Error(`stub: no page for cursor=${key}`);
      return page;
    },
    async getClipsForGame() {
      return { items: [], nextCursor: null };
    },
    async search() {
      return { clips: [], games: [] };
    },
    async listGames() {
      return [];
    },
  };
}

// Fanout that always succeeds; tracks calls for assertions.
function okFanout(): Fanout & {
  calls: { clipId: string; channelId: string; pingRoleId: string | null }[];
} {
  const calls: { clipId: string; channelId: string; pingRoleId: string | null }[] = [];
  const f: Fanout = async (clip, target) => {
    calls.push({ clipId: clip.id, channelId: target.channelId, pingRoleId: target.pingRoleId });
    return true;
  };
  return Object.assign(f, { calls });
}

describe('pollOnce', () => {
  test('first-ever poll seeds the cursor and does not fan out', async () => {
    const db = stubDb();
    const fanout = okFanout();
    const res = await pollOnce({ db, api: stubApi([clip(), clip()]), fanout, log: silentLog });
    expect(res).toEqual({ fetched: 0, newClips: 0, posts: 0 });
    expect(fanout.calls).toHaveLength(0);
    expect(db._state.state.get('last_clip_created_at')).toBeDefined();
  });

  test('fanout is called per-channel, records post log on success, advances cursor', async () => {
    const c1 = clip();
    const c2 = clip();
    const c3 = clip();
    const sub = subscription({ channelId: 'C1', pingRoleId: '555' });
    const db = stubDb({ cursor: c2.createdAt, subs: [sub] });
    // Cursor at c2: with the new `>=` filter, both c2 and c3 are fresh, but c2
    // ties the cursor exactly. The post log starts empty, so both fan out.
    const fanout = okFanout();

    const res = await pollOnce({ db, api: stubApi([c3, c2, c1]), fanout, log: silentLog });

    expect(res).toMatchObject({ fetched: 3, newClips: 2, posts: 2 });
    expect(fanout.calls).toHaveLength(2);
    // Calls are in chronological order (oldest first) so Discord matches upload order.
    expect(fanout.calls[0]).toMatchObject({ clipId: c2.id, channelId: 'C1', pingRoleId: '555' });
    expect(fanout.calls[1]).toMatchObject({ clipId: c3.id, channelId: 'C1', pingRoleId: '555' });
    expect(db._state.postLog.has(`C1:${c2.id}`)).toBe(true);
    expect(db._state.postLog.has(`C1:${c3.id}`)).toBe(true);
    expect(db._state.state.get('last_clip_created_at')).toBe(c3.createdAt);
  });

  test('isPosted pre-check skips clips already in post log', async () => {
    const c1 = clip();
    const c2 = clip();
    const sub = subscription({ channelId: 'C1' });
    const db = stubDb({
      cursor: c1.createdAt,
      subs: [sub],
      // Pre-existing entry (crashed-mid-fanout restart): isPosted returns true,
      // so fanout is NOT called and recordPost is NOT re-issued. Cursor still
      // advances because the clip has been "evaluated".
      postLog: new Set([`C1:${c2.id}`]),
    });
    const fanout = okFanout();

    const res = await pollOnce({ db, api: stubApi([c2]), fanout, log: silentLog });
    expect(res.posts).toBe(0);
    expect(fanout.calls).toHaveLength(0);
    expect(db._state.state.get('last_clip_created_at')).toBe(c2.createdAt);
  });

  test('fanout returning false does NOT record the post (retry next round)', async () => {
    const c1 = clip();
    const c2 = clip();
    const sub = subscription({ channelId: 'C1' });
    const db = stubDb({
      cursor: c1.createdAt,
      subs: [sub],
    });
    const fanout: Fanout = async () => false;

    const res = await pollOnce({ db, api: stubApi([c2]), fanout, log: silentLog });
    expect(res.posts).toBe(0);
    expect(db._state.postLog.has(`C1:${c2.id}`)).toBe(false);
    // Cursor still advances — failed sends are retried via isPosted next round
    // (which will still return false because we never recorded).
    expect(db._state.state.get('last_clip_created_at')).toBe(c2.createdAt);
  });

  test('fanout throwing is caught, logged, and treated as a failed send', async () => {
    const c1 = clip();
    const c2 = clip();
    const sub = subscription({ channelId: 'C1' });
    const errLog = mock(() => {});
    const db = stubDb({
      cursor: c1.createdAt,
      subs: [sub],
    });
    const fanout: Fanout = async () => {
      throw new Error('discord 502');
    };

    const res = await pollOnce({
      db,
      api: stubApi([c2]),
      fanout,
      log: { info: () => {}, warn: () => {}, error: errLog },
    });
    expect(res.posts).toBe(0);
    expect(errLog).toHaveBeenCalled();
    expect(db._state.postLog.has(`C1:${c2.id}`)).toBe(false);
  });

  test('clips matching no subscription advance the cursor without fanout', async () => {
    const c1 = clip();
    const c2 = clip({ game: { id: 999, name: 'X', slug: 'x', tag: 'x' } });
    const sub = subscription({ channelId: 'C1', gameId: 7 });
    const db = stubDb({
      cursor: c1.createdAt,
      subs: [sub],
    });
    const fanout = okFanout();

    const res = await pollOnce({ db, api: stubApi([c2]), fanout, log: silentLog });
    expect(res.newClips).toBe(1);
    expect(res.posts).toBe(0);
    expect(fanout.calls).toHaveLength(0);
    expect(db._state.state.get('last_clip_created_at')).toBe(c2.createdAt);
  });

  test('zero-fresh-after-cursor poll exits early without consulting subscriptions', async () => {
    // Cursor anchored AFTER the only feed item; nothing is `>=` lastSeen.
    const c1 = clip();
    const cursorAhead = new Date(new Date(c1.createdAt).getTime() + 60_000).toISOString();
    const listSubsSpy = mock(async () => [] as Subscription[]);
    const db = stubDb({ cursor: cursorAhead });
    db.listAllSubscriptions = listSubsSpy;

    const res = await pollOnce({
      db,
      api: stubApi([c1]),
      fanout: okFanout(),
      log: silentLog,
    });
    expect(res).toMatchObject({ fetched: 1, newClips: 0, posts: 0 });
    expect(listSubsSpy).not.toHaveBeenCalled();
  });

  test('multiple subscriptions on different channels: one fanout call per (clip, sub)', async () => {
    const c1 = clip();
    const c2 = clip();
    const subA = subscription({ channelId: 'A' });
    const subB = subscription({ channelId: 'B' });
    const db = stubDb({
      cursor: c1.createdAt,
      subs: [subA, subB],
    });
    const fanout = okFanout();

    const res = await pollOnce({ db, api: stubApi([c2]), fanout, log: silentLog });
    expect(res.posts).toBe(2);
    expect(fanout.calls).toHaveLength(2);
    expect(fanout.calls.map((c) => c.channelId).sort()).toEqual(['A', 'B']);
  });

  test('recordPost failure after a successful send is logged but does not throw', async () => {
    // Simulates a DB write hiccup AFTER Discord accepted the message. The send
    // succeeded so we count the post, but we log a warning because the next
    // round will see no log entry, isPosted-check will return false, and the
    // bot will double-post. This is the at-least-once tradeoff being exercised.
    const c1 = clip();
    const c2 = clip();
    const sub = subscription({ channelId: 'C1' });
    const warnLog = mock(() => {});
    const db = stubDb({
      cursor: c1.createdAt,
      subs: [sub],
    });
    db.recordPost = async () => {
      throw new Error('db write blip');
    };
    const fanout = okFanout();

    const res = await pollOnce({
      db,
      api: stubApi([c2]),
      fanout,
      log: { info: () => {}, warn: warnLog, error: () => {} },
    });
    expect(res.posts).toBe(1); // send succeeded → counts
    expect(warnLog).toHaveBeenCalled();
  });

  test('partial failure: one sub succeeds, another fails — only the success is recorded', async () => {
    const c1 = clip();
    const c2 = clip();
    const subA = subscription({ channelId: 'A' });
    const subB = subscription({ channelId: 'B' });
    const db = stubDb({
      cursor: c1.createdAt,
      subs: [subA, subB],
    });
    // Fail the first call (channel A), succeed the second (channel B).
    let n = 0;
    const fanout: Fanout = async () => {
      n++;
      return n !== 1;
    };

    const res = await pollOnce({ db, api: stubApi([c2]), fanout, log: silentLog });
    expect(res.posts).toBe(1);
    expect(db._state.postLog.has(`A:${c2.id}`)).toBe(false);
    expect(db._state.postLog.has(`B:${c2.id}`)).toBe(true);
    // Cursor advances — A will be retried next round (isPosted returns false).
    expect(db._state.state.get('last_clip_created_at')).toBe(c2.createdAt);
  });
});

describe('pollOnce cursor sanity', () => {
  test('garbage cursor value re-anchors to a valid ISO date and logs a warning', async () => {
    // Without the guard, `new Date('not-a-date')` is Invalid Date and every
    // `c.createdAt >= lastSeen` returns false → entire feed treated as stale
    // → silent posting outage. The fix logs and re-anchors instead.
    const warnLog = mock(() => {});
    const db = stubDb({
      cursor: 'not-a-date',
      subs: [subscription({ channelId: 'C1' })],
    });

    await pollOnce({
      db,
      api: stubApi([clip()]),
      fanout: okFanout(),
      log: { info: () => {}, warn: warnLog, error: () => {} },
    });

    expect(warnLog).toHaveBeenCalled();
    const newCursor = db._state.state.get('last_clip_created_at');
    expect(newCursor).toBeDefined();
    expect(Number.isNaN(new Date(newCursor!).getTime())).toBe(false);
  });
});

describe('pollOnce cursor + dedupe boundary cases', () => {
  test('tied-timestamp clip at the cursor boundary is reprocessed but dedupe blocks the post', async () => {
    // c1 has the exact same timestamp as the cursor; isPosted returns true
    // because we already recorded it last round. The `>=` filter brings it
    // back into `fresh`, but postOne's isPosted check short-circuits.
    const c1 = clip();
    const sub = subscription({ channelId: 'C1' });
    const db = stubDb({
      cursor: c1.createdAt,
      subs: [sub],
      postLog: new Set([`C1:${c1.id}`]),
    });
    const fanout = okFanout();

    const res = await pollOnce({ db, api: stubApi([c1]), fanout, log: silentLog });
    expect(res.posts).toBe(0);
    expect(fanout.calls).toHaveLength(0);
  });
});

describe('pollOnce pagination', () => {
  test('walks nextCursor when first page is full and all items are fresh', async () => {
    // clip() increments a seq, so a..d have strictly increasing timestamps.
    // The cursor is anchored at epoch so EVERY clip is fresh — the poller
    // should walk both pages until it sees a non-full page (page 2 returns 2
    // items but nextCursor=null, so the loop terminates after page 2).
    const a = clip();
    const b = clip();
    const c = clip();
    const d = clip();

    const pages = new Map<string, ClipFeedResponse>([
      // Feed sort is DESC, so the newest clips appear on page 1.
      ['', { items: [d, c], nextCursor: 'p2' }],
      ['p2', { items: [b, a], nextCursor: null }],
    ]);
    const api = stubPagedApi(pages);
    const sub = subscription({ channelId: 'C1' });
    const db = stubDb({
      cursor: new Date(0).toISOString(),
      subs: [sub],
    });
    const fanout = okFanout();

    const res = await pollOnce({
      db,
      api,
      fanout,
      log: silentLog,
      pageSize: 2,
    });

    // 4 fanouts in chronological order: a, b, c, d.
    expect(res.posts).toBe(4);
    expect(api.callCount()).toBe(2);
    expect(fanout.calls.map((c) => c.clipId)).toEqual([a.id, b.id, c.id, d.id]);
  });

  test('stops paginating when a page contains an item older than the cursor', async () => {
    const old = clip(); // will be older than cursor
    const a = clip();
    const b = clip();
    const c = clip();
    const cursor = new Date(
      (new Date(old.createdAt).getTime() + new Date(a.createdAt).getTime()) / 2,
    ).toISOString();
    const pages = new Map<string, ClipFeedResponse>([
      ['', { items: [c, b], nextCursor: 'p2' }],
      // Second page contains `old` which is < cursor → poller stops, doesn't
      // request a third page even if nextCursor is present.
      ['p2', { items: [a, old], nextCursor: 'p3' }],
    ]);
    const api = stubPagedApi(pages);
    const sub = subscription({ channelId: 'C1' });
    const db = stubDb({
      cursor: cursor,
      subs: [sub],
    });
    const fanout = okFanout();

    const res = await pollOnce({ db, api, fanout, log: silentLog, pageSize: 2 });
    expect(api.callCount()).toBe(2);
    expect(res.posts).toBe(3); // a, b, c — not old
    expect(fanout.calls.map((c) => c.clipId)).toEqual([a.id, b.id, c.id]);
  });

  test('respects maxPages safety cap', async () => {
    // Every page is full of fresh items; without the cap the poller would loop
    // forever. maxPages=2 stops after the second fetch.
    const a = clip();
    const b = clip();
    const c = clip();
    const d = clip();
    const pages = new Map<string, ClipFeedResponse>([
      ['', { items: [d, c], nextCursor: 'p2' }],
      ['p2', { items: [b, a], nextCursor: 'p3' }],
      // p3 would be requested without the cap.
    ]);
    const api = stubPagedApi(pages);
    const sub = subscription({ channelId: 'C1' });
    const db = stubDb({
      cursor: new Date(0).toISOString(),
      subs: [sub],
    });
    const fanout = okFanout();

    await pollOnce({ db, api, fanout, log: silentLog, pageSize: 2, maxPages: 2 });
    expect(api.callCount()).toBe(2);
  });

  test('de-dupes items that appear on two pages (mid-pagination insert)', async () => {
    // If a new clip arrives between page-1 and page-2 fetches, the boundary
    // item can shift onto both pages. We de-dupe by clip id.
    const a = clip();
    const b = clip();
    const pages = new Map<string, ClipFeedResponse>([
      ['', { items: [b, a], nextCursor: 'p2' }],
      // a appears again on page 2 (shifted due to mid-fetch insert).
      ['p2', { items: [a], nextCursor: null }],
    ]);
    const api = stubPagedApi(pages);
    const sub = subscription({ channelId: 'C1' });
    const db = stubDb({
      cursor: new Date(0).toISOString(),
      subs: [sub],
    });
    const fanout = okFanout();

    const res = await pollOnce({ db, api, fanout, log: silentLog, pageSize: 2 });
    expect(res.posts).toBe(2);
    expect(fanout.calls.map((c) => c.clipId)).toEqual([a.id, b.id]);
  });
});

describe('startPoller', () => {
  test('runs immediately, then resolves on abort', async () => {
    const c1 = clip();
    const db = stubDb({ cursor: c1.createdAt });
    const fanout = okFanout();
    const abort = new AbortController();
    const loopDone = startPoller(
      { db, api: stubApi([c1]), fanout, log: silentLog },
      3600,
      abort.signal,
    );
    await Promise.resolve();
    await Promise.resolve();
    abort.abort();
    await loopDone;
  });

  test('catches errors thrown by pollOnce so the loop survives', async () => {
    const errLog = mock(() => {});
    const log: PollerLogger = { info: () => {}, warn: () => {}, error: errLog };
    const brokenDb = stubDb();
    brokenDb.getState = async () => {
      throw new Error('db blew up');
    };
    const abort = new AbortController();
    const loopDone = startPoller(
      { db: brokenDb, api: stubApi([]), fanout: okFanout(), log },
      3600,
      abort.signal,
    );
    await Promise.resolve();
    await Promise.resolve();
    abort.abort();
    await loopDone;

    expect(errLog).toHaveBeenCalled();
  });
});
