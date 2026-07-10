import { describe, expect, test, mock } from 'bun:test';
import { DiscordAPIError } from 'discord.js';
import { createFanout, type FanoutDeps, type FetchedChannel } from '../src/fanout.ts';
import type { PollerLogger } from '../src/poller.ts';
import { clip } from './factories.ts';

const PUBLIC_BASE = 'https://gankedtv.com';

function apiError(code: number, status = 404): DiscordAPIError {
  return new DiscordAPIError({ message: 'boom', code }, code, status, 'GET', 'https://x', {});
}

function silentLog(): PollerLogger & { warns: string[] } {
  const warns: string[] = [];
  return {
    warns,
    info: () => {},
    warn: (m) => warns.push(m),
    error: () => {},
  };
}

function deps(overrides: Partial<FanoutDeps> = {}): FanoutDeps & {
  log: ReturnType<typeof silentLog>;
  removed: string[];
} {
  const removed: string[] = [];
  return {
    channels: { fetch: async () => null },
    db: {
      removeAllSubscriptionsForChannel: async (channelId: string) => {
        removed.push(channelId);
        return 1;
      },
    },
    log: silentLog(),
    publicBase: PUBLIC_BASE,
    fetchThumbnail: async () => null,
    removed,
    ...overrides,
  } as FanoutDeps & { log: ReturnType<typeof silentLog>; removed: string[] };
}

function sendableChannel() {
  const send = mock(async (_payload: unknown) => undefined);
  const channel = { isTextBased: () => true, send } as unknown as FetchedChannel;
  return { channel, send };
}

const target = { channelId: 'chan-1', pingRoleId: null };

describe('createFanout', () => {
  test('delivers the clip message to a text channel', async () => {
    const { channel, send } = sendableChannel();
    const d = deps({ channels: { fetch: async () => channel } });

    const ok = await createFanout(d)(clip({ shareCode: 'fan1' }), target);

    expect(ok).toBe(true);
    const payload = send.mock.calls[0]![0] as {
      embeds: { url?: string }[];
      allowedMentions: { parse: string[] };
    };
    expect(payload.embeds[0]?.url).toBe(`${PUBLIC_BASE}/c/fan1`);
    expect(payload.allowedMentions.parse).toEqual(['roles']);
  });

  test.each([[10003], [10004], [50001], [50007]])(
    'terminal channel error %d removes subscriptions and returns false',
    async (code) => {
      const d = deps({
        channels: {
          fetch: async () => {
            throw apiError(code);
          },
        },
      });

      const ok = await createFanout(d)(clip(), target);

      expect(ok).toBe(false);
      expect(d.removed).toEqual(['chan-1']);
    },
  );

  test('rate limit on fetch is transient: no removal, returns false', async () => {
    const d = deps({
      channels: {
        fetch: async () => {
          throw apiError(0, 429);
        },
      },
    });

    const ok = await createFanout(d)(clip(), target);

    expect(ok).toBe(false);
    expect(d.removed).toEqual([]);
    expect(d.log.warns.some((w) => w.includes('rate limited'))).toBe(true);
  });

  test('rate limit on send is transient: returns false', async () => {
    const channel = {
      isTextBased: () => true,
      send: async () => {
        throw apiError(0, 429);
      },
    } as unknown as FetchedChannel;
    const d = deps({ channels: { fetch: async () => channel } });

    const ok = await createFanout(d)(clip(), target);

    expect(ok).toBe(false);
    expect(d.removed).toEqual([]);
  });

  test('unknown fetch error is rethrown', async () => {
    const d = deps({
      channels: {
        fetch: async () => {
          throw new Error('network down');
        },
      },
    });

    await expect(createFanout(d)(clip(), target)).rejects.toThrow('network down');
    expect(d.removed).toEqual([]);
  });

  test('unknown send error is rethrown', async () => {
    const channel = {
      isTextBased: () => true,
      send: async () => {
        throw new Error('socket reset');
      },
    } as unknown as FetchedChannel;
    const d = deps({ channels: { fetch: async () => channel } });

    await expect(createFanout(d)(clip(), target)).rejects.toThrow('socket reset');
  });

  test('null channel returns false without removal', async () => {
    const d = deps();

    const ok = await createFanout(d)(clip(), target);

    expect(ok).toBe(false);
    expect(d.removed).toEqual([]);
  });

  test('non-text channel returns false', async () => {
    const d = deps({
      channels: { fetch: async () => ({ isTextBased: () => false }) },
    });

    expect(await createFanout(d)(clip(), target)).toBe(false);
  });

  test('ping role rides along in content', async () => {
    const { channel, send } = sendableChannel();
    const d = deps({ channels: { fetch: async () => channel } });

    await createFanout(d)(clip(), { channelId: 'chan-1', pingRoleId: '42' });

    const payload = send.mock.calls[0]![0] as { content?: string };
    expect(payload.content).toBe('<@&42>');
  });

  test('downloads the thumbnail once per clip and attaches it to every channel post', async () => {
    const { channel, send } = sendableChannel();
    let fetches = 0;
    const bytes = Buffer.from('JPEGDATA');
    const d = deps({
      channels: { fetch: async () => channel },
      fetchThumbnail: async () => {
        fetches++;
        return bytes;
      },
    });
    const fan = createFanout(d);
    const c = clip({ shareCode: 'thumb1' });

    expect(await fan(c, target)).toBe(true);
    expect(await fan(c, { channelId: 'chan-2', pingRoleId: null })).toBe(true);

    expect(fetches).toBe(1);
    const payload = send.mock.calls[0]?.[0] as {
      files?: unknown[];
      embeds: { image?: { url?: string } }[];
    };
    expect(payload.files).toEqual([{ attachment: bytes, name: 'clip.jpg' }]);
    expect(payload.embeds[0]?.image?.url).toBe('attachment://clip.jpg');
  });
});
