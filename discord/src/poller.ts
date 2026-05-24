import type { ApiClient, ClipFeedItem } from './api.ts';
import type { Db, Subscription } from './db.ts';
import { matchingSubscriptions } from './filters.ts';

// High-water-mark cursor: ISO 8601 timestamp of the most-recent clip we've seen.
// Tracked in discord_bot_state under this key. On boot, the poller seeds it to
// "now" so a fresh install doesn't backfill the entire history.
const STATE_KEY = 'last_clip_created_at';

export type PollerLogger = {
  info(message: string, fields?: Record<string, unknown>): void;
  warn(message: string, fields?: Record<string, unknown>): void;
  error(message: string, fields?: Record<string, unknown>): void;
};

// Per-channel post target. The poller resolves which channel + ping role each
// clip should land on; the fanout just needs to know where to send and whether
// to ping. Returns true on successful send, false on transient failure (the
// poller does NOT record the post and the next round will retry).
export type PostTarget = { channelId: string; pingRoleId: string | null };
export type Fanout = (clip: ClipFeedItem, target: PostTarget) => Promise<boolean>;

export type PollerDeps = {
  db: Db;
  api: ApiClient;
  fanout: Fanout;
  log: PollerLogger;
  // Max items to pull per API page. The feed page caps at 100 server-side;
  // default 50 + cursor-pagination handles bursts of >50 clips per poll round
  // without losing the older ones (C2 from review).
  pageSize?: number;
  // Safety cap on cursor-driven pagination. Without this, a misconfigured
  // cursor (e.g. lastSeen far in the past after a manual DB reset) could pull
  // the entire history in one round. 10 × 50 = 500 clips per round is plenty;
  // beyond that, dropping older clips on the floor is preferable to spending
  // the whole poll interval on a single round.
  maxPages?: number;
};

export type PollResult = {
  fetched: number;
  newClips: number;
  posts: number;
};

// One poll round. Side-effect-free until it calls fanout / db writes, which
// makes the function easy to drive from tests.
export async function pollOnce({
  db,
  api,
  fanout,
  log,
  pageSize = 50,
  maxPages = 10,
}: PollerDeps): Promise<PollResult> {
  const lastSeenIso = await db.getState(STATE_KEY);
  if (lastSeenIso === null) {
    // First-ever poll: anchor to "now" so we don't backfill. The next round
    // forward will pick up anything that lands after this timestamp.
    await db.setState(STATE_KEY, new Date().toISOString());
    return { fetched: 0, newClips: 0, posts: 0 };
  }
  let lastSeen = new Date(lastSeenIso);
  if (Number.isNaN(lastSeen.getTime())) {
    // Garbage value in the cursor (manual DB tampering, future schema bug,
    // truncated write). Without the guard, every clip comparison `>= lastSeen`
    // returns false → entire feed treated as stale → silent posting outage.
    // Re-anchor to "now" instead, log loudly, and continue.
    log.warn('cursor value is not a valid ISO date; re-anchoring to now', { value: lastSeenIso });
    lastSeen = new Date();
    await db.setState(STATE_KEY, lastSeen.toISOString());
  }

  // Paginate via nextCursor until a page returns at least one clip ≤ lastSeen
  // (we've walked back past the high-water mark) or we hit maxPages. The feed
  // is sorted DESC by (createdAt, id), so each follow-up page covers older
  // clips. Without this loop, a burst of >pageSize clips between polls would
  // silently drop everything older than the first pageSize newest (C2).
  const all: ClipFeedItem[] = [];
  let cursor: string | undefined;
  let fetched = 0;
  for (let pageNum = 0; pageNum < maxPages; pageNum++) {
    const page = await api.getFeed(cursor ? { limit: pageSize, cursor } : { limit: pageSize });
    fetched += page.items.length;
    const lastItem = page.items[page.items.length - 1];
    // Use >= so tied-timestamp clips at the boundary are included (C3); the
    // post-log dedupe (isPosted) below ensures we don't double-send them.
    const freshInPage = page.items.filter((c) => new Date(c.createdAt) >= lastSeen);
    all.push(...freshInPage);
    // Stop when the oldest clip in the page is ≤ lastSeen (we've walked back
    // past the cursor) OR the page wasn't full (no more clips upstream) OR
    // there's no next cursor.
    const pageWalkedPastCursor = lastItem && new Date(lastItem.createdAt) < lastSeen;
    if (pageWalkedPastCursor || page.items.length < pageSize || !page.nextCursor) break;
    cursor = page.nextCursor;
  }

  // Sort oldest-first so Discord channel ordering matches upload order, then
  // de-dupe by id (the same clip can appear on two pages if a new clip arrives
  // mid-pagination — both pages then include the boundary item).
  const seen = new Set<string>();
  const fresh = all
    .filter((c) => {
      if (seen.has(c.id)) return false;
      seen.add(c.id);
      return true;
    })
    .sort((a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime());

  if (fresh.length === 0) {
    return { fetched, newClips: 0, posts: 0 };
  }

  const subs = await db.listAllSubscriptions();
  let posts = 0;
  // Index of the first clip (in oldest-first order) that had ANY post failure.
  // We keep processing remaining clips so we don't artificially halt newer
  // work, but the cursor only advances to the predecessor of this clip — so
  // the failed (clip, sub) pair is re-evaluated next round. Without this, a
  // transient channel failure would silently drop the clip forever (cursor
  // moved past it, post log has no row, isPosted-pre-check finds nothing,
  // but the feed no longer returns it because lastSeen >= its createdAt).
  let firstFailureIdx = -1;

  for (let i = 0; i < fresh.length; i++) {
    const clip = fresh[i]!;
    const matched = matchingSubscriptions(subs, clip);
    for (const sub of matched) {
      const outcome = await postOne(clip, sub, db, fanout, log);
      if (outcome === 'sent') posts++;
      else if (outcome === 'failed' && firstFailureIdx === -1) firstFailureIdx = i;
    }
  }

  // Cursor advancement:
  //   - all clips fully done → advance to the newest clip's createdAt
  //   - partial failure → advance only to the clip BEFORE the failure boundary
  //     (so the failed clip is re-fetched next round; isPosted dedupes the subs
  //     that already succeeded; only the failed sub retries)
  //   - first clip in the round failed → don't advance at all
  const advanceIdx = firstFailureIdx === -1 ? fresh.length - 1 : firstFailureIdx - 1;
  if (advanceIdx >= 0) {
    await db.setState(STATE_KEY, fresh[advanceIdx]!.createdAt);
  }

  log.info('poll round complete', {
    fetched,
    newClips: fresh.length,
    posts,
    haltedAtIdx: firstFailureIdx === -1 ? null : firstFailureIdx,
  });
  return { fetched, newClips: fresh.length, posts };
}

type PostOutcome = 'sent' | 'skipped' | 'failed';

async function postOne(
  clip: ClipFeedItem,
  sub: Subscription,
  db: Db,
  fanout: Fanout,
  log: PollerLogger,
): Promise<PostOutcome> {
  // Read-only pre-check skips clips we've already posted to this channel
  // (idempotence on restart with an un-advanced cursor, or at the tied-
  // timestamp cursor boundary when `>=` re-includes processed clips).
  if (await db.isPosted(sub.channelId, clip.id)) return 'skipped';

  let posted: boolean;
  try {
    posted = await fanout(clip, { channelId: sub.channelId, pingRoleId: sub.pingRoleId });
  } catch (err) {
    log.error('post threw', { channelId: sub.channelId, clipId: clip.id, err: String(err) });
    return 'failed';
  }
  if (!posted) {
    // fanout swallowed the failure (e.g. channel unavailable) and returned
    // false — same retry semantics as a throw: skip recording, next round
    // tries again.
    return 'failed';
  }

  // Record AFTER a successful send. If a crash happens between send and
  // record, the next round will isPosted()-check, see nothing, post again
  // → at-least-once duplicate. Inevitable without 2PC; logged here so it's
  // greppable when investigating dupe reports.
  try {
    await db.recordPost(sub.channelId, clip.id);
  } catch (err) {
    log.warn('post recorded send but recordPost failed', {
      channelId: sub.channelId,
      clipId: clip.id,
      err: String(err),
    });
  }
  return 'sent';
}

// Long-lived loop. Resolves only when stop() is called (or the abort signal
// fires). Errors per round are logged but never crash the loop.
export function startPoller(
  deps: PollerDeps,
  intervalSeconds: number,
  signal: AbortSignal,
): Promise<void> {
  return new Promise((resolve) => {
    // Reentrancy guard: setInterval keeps firing on schedule, but a poll round
    // can outlast `intervalSeconds` when paginating through a burst or when the
    // API is slow. Without the guard, two overlapping ticks would re-fetch the
    // same clips (post-log dedupe still prevents double-sends, but wastes API
    // calls and creates avoidable DB churn).
    let inFlight = false;
    const tick = async () => {
      if (signal.aborted || inFlight) return;
      inFlight = true;
      try {
        await pollOnce(deps);
      } catch (err) {
        deps.log.error('poll round threw', { err: String(err) });
      } finally {
        inFlight = false;
      }
    };

    let running = true;
    const handle = setInterval(() => {
      void tick();
    }, intervalSeconds * 1000);

    signal.addEventListener('abort', () => {
      if (!running) return;
      running = false;
      clearInterval(handle);
      resolve();
    });

    // Kick off immediately so the first round doesn't wait for the interval.
    void tick();
  });
}
