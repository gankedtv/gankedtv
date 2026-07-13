import { DiscordAPIError, RateLimitError } from 'discord.js';
import type { Db } from './db.ts';
import type { ThumbnailFetcher } from './lib/thumbnail.ts';
import { buildMessage, postToChannel, type Sendable } from './posting.ts';
import type { Fanout, PollerLogger } from './poller.ts';

// Terminal channel errors: the channel/guild is gone or the bot can never post
// there. 10003: Unknown Channel · 10004: Unknown Guild · 50001: Missing Access ·
// 50007: Cannot Send Messages To This User.
const TERMINAL_CHANNEL_CODES = new Set<number>([10003, 10004, 50001, 50007]);

// Structural slice of discord.js's ChannelManager / Channel so tests can pass
// fakes; the caller narrows text-based-ness here, not at the call site.
export type FetchedChannel = { isTextBased(): boolean } | null;
export type FanoutDeps = {
  channels: { fetch(id: string): Promise<FetchedChannel> };
  db: Pick<Db, 'removeAllSubscriptionsForChannel'>;
  log: PollerLogger;
  publicBase: string;
  fetchThumbnail: ThumbnailFetcher;
};

// One clip fans out to many channels; download its thumbnail once and reuse the
// bytes for every (clip, channel) pair. FIFO-evicted on a byte budget so a run of
// unusually large images can't pile up — a poll round only touches fresh clips.
const THUMBNAIL_CACHE_MAX_BYTES = 32 * 1024 * 1024;

function isRateLimit(err: unknown): boolean {
  return err instanceof RateLimitError || (err instanceof DiscordAPIError && err.status === 429);
}

// One channel per call. The poller invokes this per matched (clip, sub) pair and
// the return value (true=delivered, false=transient failure) drives whether the
// post-log row is written. Returning false means the poller isPosted()-checks
// this pair again next round and retries.
export function createFanout(deps: FanoutDeps): Fanout {
  const thumbnails = new Map<string, Buffer>();
  let thumbnailBytes = 0;
  async function thumbnailFor(clipId: string, url: string | null): Promise<Buffer | null> {
    const cached = thumbnails.get(clipId);
    if (cached !== undefined) return cached;
    const bytes = await deps.fetchThumbnail(url);
    // Only successes are memoized: a transient download failure must not pin every
    // later post of this clip to the short-lived URL fallback.
    if (bytes !== null) {
      while (thumbnails.size > 0 && thumbnailBytes + bytes.byteLength > THUMBNAIL_CACHE_MAX_BYTES) {
        const oldest = thumbnails.keys().next().value!;
        thumbnailBytes -= thumbnails.get(oldest)!.byteLength;
        thumbnails.delete(oldest);
      }
      thumbnails.set(clipId, bytes);
      thumbnailBytes += bytes.byteLength;
    }
    return bytes;
  }

  return async (clip, target) => {
    // fetch() pulls from the cache when warm, REST when cold. discord.js v14
    // THROWS DiscordAPIError for missing/inaccessible resources; the `null`
    // return is reserved for forced cache-miss configurations.
    let channel: FetchedChannel;
    try {
      channel = await deps.channels.fetch(target.channelId);
    } catch (err) {
      // Terminal channel errors → remove the sub so we don't loop on this
      // channel for every future clip.
      if (err instanceof DiscordAPIError && TERMINAL_CHANNEL_CODES.has(Number(err.code))) {
        const removed = await deps.db.removeAllSubscriptionsForChannel(target.channelId);
        deps.log.warn('channel inaccessible — removed subscriptions', {
          channelId: target.channelId,
          clipId: clip.id,
          code: err.code,
          removed,
        });
        // Return false so the poller halts the cursor this round; next round
        // the subs are gone and the cursor will advance cleanly.
        return false;
      }
      if (isRateLimit(err)) {
        // Transient by definition — do NOT throw (that would wedge the whole
        // round); skip and let the next round retry this pair.
        deps.log.warn('rate limited fetching channel — will retry next round', {
          channelId: target.channelId,
          clipId: clip.id,
        });
        return false;
      }
      throw err;
    }
    if (!channel || !channel.isTextBased() || !('send' in channel)) {
      deps.log.warn('channel unavailable or not text-based', {
        channelId: target.channelId,
        clipId: clip.id,
      });
      return false;
    }
    const thumbnail = await thumbnailFor(clip.id, clip.thumbnailUrl);
    const message = buildMessage(
      clip,
      { pingRoleId: target.pingRoleId },
      deps.publicBase,
      thumbnail,
    );
    try {
      return await postToChannel(channel as Sendable, message);
    } catch (err) {
      if (isRateLimit(err)) {
        deps.log.warn('rate limited sending message — will retry next round', {
          channelId: target.channelId,
          clipId: clip.id,
        });
        return false;
      }
      throw err;
    }
  };
}
