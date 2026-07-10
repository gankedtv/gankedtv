import { describe, expect, test, mock } from 'bun:test';
import { buildMessage, postToChannel, type ClipMessage, type Sendable } from '../src/posting.ts';
import { clip } from './factories.ts';

describe('buildMessage', () => {
  test('no ping role \u2192 embed only, no content', () => {
    const c = clip({ shareCode: 'abcd1234', title: 'Ace clutch' });
    const msg = buildMessage(c, { pingRoleId: null }, 'https://gankedtv.com');

    expect(msg.content).toBeUndefined();
    expect(msg.embeds).toHaveLength(1);
    expect(msg.embeds[0]?.url).toBe('https://gankedtv.com/c/abcd1234');
    expect(msg.embeds[0]?.title).toBe('Ace clutch');
  });

  test('with ping role \u2192 role mention in content, share URL only in the embed', () => {
    const c = clip({ shareCode: 'abcd1234' });
    const msg = buildMessage(c, { pingRoleId: '987' }, 'https://gankedtv.com');

    expect(msg.content).toBe('<@&987>');
    // The URL must NOT be in content \u2014 that would trigger a second auto-unfurl embed.
    expect(msg.content).not.toContain('https://');
    expect(msg.embeds[0]?.url).toBe('https://gankedtv.com/c/abcd1234');
  });
});

describe('buildMessage thumbnail attachment', () => {
  test('with downloaded bytes → file attached and embed points at it', () => {
    const c = clip({ thumbnailUrl: 'https://minio.local/thumbs/x.jpg?sig=1' });
    const bytes = Buffer.from('JPEGDATA');

    const msg = buildMessage(c, { pingRoleId: null }, 'https://gankedtv.com', bytes);

    expect(msg.files).toEqual([{ attachment: bytes, name: 'clip.jpg' }]);
    expect(msg.embeds[0]?.image?.url).toBe('attachment://clip.jpg');
  });

  test('download failed (null) → no files, embed falls back to the raw URL', () => {
    const c = clip({ thumbnailUrl: 'https://minio.local/thumbs/x.jpg?sig=1' });

    const msg = buildMessage(c, { pingRoleId: null }, 'https://gankedtv.com', null);

    expect(msg.files).toBeUndefined();
    expect(msg.embeds[0]?.image?.url).toBe(c.thumbnailUrl);
  });
});

describe('postToChannel', () => {
  const message: ClipMessage = { embeds: [{ title: 't' }] };

  test('returns false and does nothing when channel is null', async () => {
    expect(await postToChannel(null, message)).toBe(false);
  });

  test('forwards embeds + content to channel.send with role-only allowed mentions', async () => {
    const send = mock(async (_payload: unknown) => undefined);
    const fake: Sendable = { send };

    const ok = await postToChannel(fake, { content: '<@&1>', embeds: [{ title: 't' }] });

    expect(ok).toBe(true);
    expect(send).toHaveBeenCalledTimes(1);
    const arg = send.mock.calls[0]![0] as ClipMessage & {
      allowedMentions: { parse: string[] };
    };
    expect(arg.content).toBe('<@&1>');
    expect(arg.embeds).toHaveLength(1);
    expect(arg.allowedMentions.parse).toEqual(['roles']);
  });
});
