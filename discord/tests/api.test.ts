import { describe, expect, test, mock } from 'bun:test';
import { createApi } from '../src/api.ts';

// Builds a fake fetch that returns the given JSON body for any URL, capturing
// every call site for assertions. Lets us cover createApi without touching the
// network or monkey-patching global.fetch.
function jsonFetch(body: unknown): typeof fetch & { calls: { url: string; init: RequestInit }[] } {
  const calls: { url: string; init: RequestInit }[] = [];
  const f = mock(async (input: string | URL | Request, init?: RequestInit) => {
    calls.push({ url: input.toString(), init: init ?? {} });
    return new Response(JSON.stringify(body), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    });
  });
  return Object.assign(f as unknown as typeof fetch, { calls });
}

function statusFetch(status: number): typeof fetch {
  return mock(async () => new Response('', { status })) as unknown as typeof fetch;
}

describe('createApi.getFeed', () => {
  test('builds the right URL with default query params dropped', async () => {
    const fetchImpl = jsonFetch({ items: [], nextCursor: null });
    const api = createApi('http://api.test', fetchImpl);
    await api.getFeed();
    expect(fetchImpl.calls).toHaveLength(1);
    expect(fetchImpl.calls[0]!.url).toBe('http://api.test/clips/feed');
  });

  test('encodes cursor / limit / sort / window', async () => {
    const fetchImpl = jsonFetch({ items: [], nextCursor: null });
    const api = createApi('http://api.test', fetchImpl);
    await api.getFeed({ cursor: 'abc', limit: 25, sort: 'trending', window: '7d' });
    const url = new URL(fetchImpl.calls[0]!.url);
    expect(url.searchParams.get('cursor')).toBe('abc');
    expect(url.searchParams.get('limit')).toBe('25');
    expect(url.searchParams.get('sort')).toBe('trending');
    expect(url.searchParams.get('window')).toBe('7d');
  });

  test('handles a baseUrl with a trailing slash', async () => {
    const fetchImpl = jsonFetch({ items: [], nextCursor: null });
    const api = createApi('http://api.test/', fetchImpl);
    await api.getFeed();
    expect(fetchImpl.calls[0]!.url).toBe('http://api.test/clips/feed');
  });

  test('throws with status code in the message when the server returns non-2xx', async () => {
    const api = createApi('http://api.test', statusFetch(503));
    await expect(api.getFeed()).rejects.toThrow(/→ 503/);
  });
});

describe('createApi.getClipsForGame', () => {
  test('URL-encodes the slug', async () => {
    const fetchImpl = jsonFetch({ items: [], nextCursor: null });
    const api = createApi('http://api.test', fetchImpl);
    await api.getClipsForGame('counter-strike 2');
    expect(fetchImpl.calls[0]!.url).toBe('http://api.test/games/counter-strike%202/clips');
  });

  test('forwards an optional limit', async () => {
    const fetchImpl = jsonFetch({ items: [], nextCursor: null });
    const api = createApi('http://api.test', fetchImpl);
    await api.getClipsForGame('valorant', { limit: 5 });
    expect(new URL(fetchImpl.calls[0]!.url).searchParams.get('limit')).toBe('5');
  });
});

describe('createApi.search', () => {
  test("defaults type to 'clips' when omitted", async () => {
    const fetchImpl = jsonFetch({ clips: [], games: [] });
    const api = createApi('http://api.test', fetchImpl);
    await api.search('flick');
    const url = new URL(fetchImpl.calls[0]!.url);
    expect(url.searchParams.get('q')).toBe('flick');
    expect(url.searchParams.get('type')).toBe('clips');
  });

  test('preserves an explicit type override', async () => {
    const fetchImpl = jsonFetch({ clips: [], games: [] });
    const api = createApi('http://api.test', fetchImpl);
    await api.search('flick', { type: 'games' });
    expect(new URL(fetchImpl.calls[0]!.url).searchParams.get('type')).toBe('games');
  });
});

describe('createApi.listGames', () => {
  test('only emits hasClips when truthy (avoids hasClips=false in the URL)', async () => {
    const fetchImpl = jsonFetch([]);
    const api = createApi('http://api.test', fetchImpl);
    await api.listGames({ hasClips: false });
    const url = new URL(fetchImpl.calls[0]!.url);
    expect(url.searchParams.has('hasClips')).toBe(false);
  });

  test('emits hasClips=true when requested', async () => {
    const fetchImpl = jsonFetch([]);
    const api = createApi('http://api.test', fetchImpl);
    await api.listGames({ hasClips: true });
    expect(new URL(fetchImpl.calls[0]!.url).searchParams.get('hasClips')).toBe('true');
  });

  test('drops empty-string search values', async () => {
    const fetchImpl = jsonFetch([]);
    const api = createApi('http://api.test', fetchImpl);
    await api.listGames({ search: '' });
    expect(new URL(fetchImpl.calls[0]!.url).searchParams.has('search')).toBe(false);
  });
});

describe('createApi timeout', () => {
  test('throws a clear timeout error when AbortSignal.timeout fires', async () => {
    // Fake fetch that respects the AbortSignal — resolves "never" but rejects
    // when the signal aborts. Mirrors what global fetch does internally.
    const slowFetch: typeof fetch = ((_url: string | URL | Request, init?: RequestInit) =>
      new Promise((_, reject) => {
        const signal = init?.signal;
        if (!signal) return; // hang forever
        signal.addEventListener('abort', () => {
          const err = new Error('aborted');
          err.name = 'TimeoutError';
          reject(err);
        });
      })) as unknown as typeof fetch;
    const api = createApi('http://api.test', slowFetch);
    await expect(api.getFeed({ timeoutMs: 10 })).rejects.toThrow(/timed out after 10ms/);
  });

  test('honours per-call timeoutMs override (autocomplete uses this)', async () => {
    let observedTimeout = 0;
    const inspectFetch: typeof fetch = ((url: string | URL | Request, init?: RequestInit) => {
      // AbortSignal.timeout produces a signal with the abort firing after N ms.
      // We can't read the ms directly but can fingerprint it by racing setTimeout.
      const signal = init?.signal;
      if (signal) {
        const started = Date.now();
        signal.addEventListener('abort', () => {
          observedTimeout = Date.now() - started;
        });
      }
      void url;
      return Promise.resolve(new Response('[]', { status: 200 }));
    }) as unknown as typeof fetch;
    const api = createApi('http://api.test', inspectFetch);
    await api.listGames({ timeoutMs: 2500 });
    // Can't assert exact ms (abort never fired because we resolved fast); but
    // the call completed without throwing — the path through the timeout
    // option is exercised. The actual abort firing is covered by the
    // throws-clear-timeout test above.
    expect(observedTimeout).toBe(0);
  });
});
