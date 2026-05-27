import { describe, expect, test } from 'bun:test';
import { fetchSecrets, resolveCollection } from '../../shared/vaultwarden/client.ts';

// Captures requests and returns canned responses so no real network is hit.
function fakeFetch(responder: (url: string) => { status: number; body?: unknown } | 'throw') {
  const calls: { url: string; init?: RequestInit }[] = [];
  const fetchImpl = (async (input: string | URL | Request, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString();
    calls.push({ url, init });
    const r = responder(url);
    if (r === 'throw') throw new Error('network down');
    return new Response(r.body === undefined ? null : JSON.stringify(r.body), { status: r.status });
  }) as typeof fetch;
  return { fetchImpl, calls };
}

const base = {
  apiUrl: 'https://vault.test',
  apiKey: 'test-key',
  collection: 'Secrets - DEV',
  manifest: ['DATABASE_URL'] as const,
};

describe('resolveCollection', () => {
  test('Production maps to PROD (via ASPNETCORE_ENVIRONMENT or NODE_ENV)', () => {
    expect(resolveCollection({ ASPNETCORE_ENVIRONMENT: 'Production' })).toBe('Secrets - PROD');
    expect(resolveCollection({ NODE_ENV: 'production' })).toBe('Secrets - PROD');
  });

  test('non-production / unset maps to DEV', () => {
    expect(resolveCollection({ ASPNETCORE_ENVIRONMENT: 'Development' })).toBe('Secrets - DEV');
    expect(resolveCollection({ NODE_ENV: 'staging' })).toBe('Secrets - DEV');
    expect(resolveCollection({})).toBe('Secrets - DEV');
  });

  test('explicit VAULTWARDEN_COLLECTION always wins', () => {
    expect(
      resolveCollection({
        VAULTWARDEN_COLLECTION: 'Secrets - CUSTOM',
        ASPNETCORE_ENVIRONMENT: 'Production',
      }),
    ).toBe('Secrets - CUSTOM');
  });
});

describe('fetchSecrets', () => {
  test('returns values and scopes the request by org + collection with a bearer token', async () => {
    const { fetchImpl, calls } = fakeFetch(() => ({
      status: 200,
      body: { name: 'DATABASE_URL', value: 'pg://x' },
    }));

    const got = await fetchSecrets({ ...base, fetchImpl });

    expect(got).toEqual({ DATABASE_URL: 'pg://x' });
    expect(calls).toHaveLength(1);
    expect(calls[0]!.url).toContain('/secret/DATABASE_URL');
    expect(calls[0]!.url).toContain('organization_name=GankedTV');
    expect(calls[0]!.url).toContain('collection_name=Secrets%20-%20DEV');
    const headers = (calls[0]!.init?.headers ?? {}) as Record<string, string>;
    expect(headers.authorization).toBe('Bearer test-key');
  });

  test('honours an organization override', async () => {
    const { fetchImpl, calls } = fakeFetch(() => ({ status: 200, body: { value: 'v' } }));
    await fetchSecrets({ ...base, organization: 'OtherOrg', fetchImpl });
    expect(calls[0]!.url).toContain('organization_name=OtherOrg');
  });

  test('skips a key whose value is empty', async () => {
    const { fetchImpl } = fakeFetch(() => ({ status: 200, body: { value: '' } }));
    expect(await fetchSecrets({ ...base, fetchImpl })).toEqual({});
  });

  test('skips already-set keys without a request', async () => {
    const { fetchImpl, calls } = fakeFetch(() => ({ status: 200, body: { value: 'v' } }));
    const got = await fetchSecrets({ ...base, alreadySet: () => true, fetchImpl });
    expect(got).toEqual({});
    expect(calls).toHaveLength(0);
  });

  test('dev: 404, non-2xx and transport errors are skipped', async () => {
    expect(
      await fetchSecrets({ ...base, fetchImpl: fakeFetch(() => ({ status: 404 })).fetchImpl }),
    ).toEqual({});
    expect(
      await fetchSecrets({ ...base, fetchImpl: fakeFetch(() => ({ status: 500 })).fetchImpl }),
    ).toEqual({});
    expect(await fetchSecrets({ ...base, fetchImpl: fakeFetch(() => 'throw').fetchImpl })).toEqual(
      {},
    );
  });

  test('throwIfMissing: throws on a 404', async () => {
    await expect(
      fetchSecrets({
        ...base,
        throwIfMissing: true,
        fetchImpl: fakeFetch(() => ({ status: 404 })).fetchImpl,
      }),
    ).rejects.toThrow(/not found/);
  });

  test('throwOnError: throws on non-2xx and transport errors, but a 404 is still skipped', async () => {
    await expect(
      fetchSecrets({
        ...base,
        throwOnError: true,
        fetchImpl: fakeFetch(() => ({ status: 500 })).fetchImpl,
      }),
    ).rejects.toThrow(/500/);
    await expect(
      fetchSecrets({ ...base, throwOnError: true, fetchImpl: fakeFetch(() => 'throw').fetchImpl }),
    ).rejects.toThrow(/failed to fetch/);
    // throwOnError does NOT escalate a 404 — this is the web build's "GA id is optional" mode.
    expect(
      await fetchSecrets({
        ...base,
        throwOnError: true,
        fetchImpl: fakeFetch(() => ({ status: 404 })).fetchImpl,
      }),
    ).toEqual({});
  });

  test('fetches multiple manifest keys sequentially', async () => {
    const { fetchImpl, calls } = fakeFetch((url) => {
      const key = decodeURIComponent(url.split('/secret/')[1]!.split('?')[0]!);
      return { status: 200, body: { value: `v-${key}` } };
    });
    const got = await fetchSecrets({ ...base, manifest: ['A', 'B'], fetchImpl });
    expect(got).toEqual({ A: 'v-A', B: 'v-B' });
    expect(calls).toHaveLength(2);
  });
});
