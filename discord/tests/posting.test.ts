import { describe, expect, test, mock } from 'bun:test';
import { buildMessage, postToChannel, type Sendable } from '../src/posting.ts';
import { clip } from './factories.ts';

describe('buildMessage', () => {
  test('no ping role → bare share URL', () => {
    const c = clip({ shareCode: 'abcd1234' });
    expect(buildMessage(c, { pingRoleId: null }, 'https://gankedtv.com')).toBe(
      'https://gankedtv.com/c/abcd1234',
    );
  });

  test('with ping role → role mention prefix', () => {
    const c = clip({ shareCode: 'abcd1234' });
    expect(buildMessage(c, { pingRoleId: '987' }, 'https://gankedtv.com')).toBe(
      '<@&987> https://gankedtv.com/c/abcd1234',
    );
  });
});

describe('postToChannel', () => {
  test('returns false and does nothing when channel is null', async () => {
    expect(await postToChannel(null, 'hello')).toBe(false);
  });

  test('forwards to channel.send with role-only allowed mentions', async () => {
    const send = mock(async (_payload: unknown) => undefined);
    const fake: Sendable = { send };
    const ok = await postToChannel(fake, 'hi');
    expect(ok).toBe(true);
    expect(send).toHaveBeenCalledTimes(1);
    const arg = send.mock.calls[0]![0] as { content: string; allowedMentions: { parse: string[] } };
    expect(arg.content).toBe('hi');
    expect(arg.allowedMentions.parse).toEqual(['roles']);
  });
});
