import { describe, expect, it } from 'vitest'
import { fetchViteEnv, renderEnvFile } from '../fetch-vault-env'

// Builds canned responses so no real network is hit; mirrors the discord shared-client tests.
function fakeFetch(responder: (url: string) => { status: number; body?: unknown } | 'throw') {
  const calls: string[] = []
  const fetchImpl = (async (input: string | URL | Request) => {
    const url = typeof input === 'string' ? input : input.toString()
    calls.push(url)
    const r = responder(url)
    if (r === 'throw') throw new Error('network down')
    return new Response(r.body === undefined ? null : JSON.stringify(r.body), { status: r.status })
  }) as typeof fetch
  return { fetchImpl, calls }
}

const bootstrap = { VAULTWARDEN_API_URL: 'https://vault.test', VAULTWARDEN_API_KEY: 'k' }

describe('renderEnvFile', () => {
  it('emits KEY=value lines and trails with a newline', () => {
    expect(renderEnvFile({ VITE_API_BASE_URL: 'https://api.ganked.tv' })).toBe(
      'VITE_API_BASE_URL=https://api.ganked.tv\n',
    )
  })

  it('quotes values containing whitespace', () => {
    expect(renderEnvFile({ VITE_X: 'a b' })).toBe('VITE_X="a b"\n')
  })
})

describe('fetchViteEnv', () => {
  it('no-ops (returns {}) and never fetches when bootstrap vars are unset', async () => {
    const { fetchImpl, calls } = fakeFetch(() => ({ status: 200, body: { value: 'x' } }))
    expect(await fetchViteEnv({}, fetchImpl)).toEqual({})
    expect(calls).toHaveLength(0)
  })

  it('fetches the manifest from the env-derived collection with a bearer token', async () => {
    const { fetchImpl, calls } = fakeFetch((url) => {
      const key = decodeURIComponent(url.split('/secret/')[1]!.split('?')[0]!)
      return { status: 200, body: { value: `v-${key}` } }
    })
    const got = await fetchViteEnv(
      { ...bootstrap, ASPNETCORE_ENVIRONMENT: 'Production' },
      fetchImpl,
      ['VITE_API_BASE_URL'],
    )
    expect(got).toEqual({ VITE_API_BASE_URL: 'v-VITE_API_BASE_URL' })
    expect(calls[0]).toContain('collection_name=Secrets%20-%20PROD')
  })

  it('skips a missing (404) optional key but throws on a non-2xx error', async () => {
    expect(
      await fetchViteEnv({ ...bootstrap }, fakeFetch(() => ({ status: 404 })).fetchImpl, [
        'VITE_GA_MEASUREMENT_ID',
      ]),
    ).toEqual({})
    await expect(
      fetchViteEnv({ ...bootstrap }, fakeFetch(() => ({ status: 500 })).fetchImpl, [
        'VITE_API_BASE_URL',
      ]),
    ).rejects.toThrow(/500/)
  })
})
