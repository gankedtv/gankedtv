import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { apiKeys } from '../apiKeys'
import { configureAuth, BASE_URL } from '../client'

beforeEach(() => {
  configureAuth({
    getAccessToken: () => null,
    getRefreshToken: () => null,
    onTokenRefreshed: () => {},
    onRefreshFailed: () => {},
  })
})

afterEach(() => {
  vi.unstubAllGlobals()
})

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  })
}

function emptyResponse(status = 204): Response {
  return new Response(null, { status, headers: { 'content-length': '0' } })
}

const KEY_ID = 'aa11bb22-0000-0000-0000-000000000042'

describe('api/apiKeys', () => {
  it('list() GETs /me/api-keys', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse([{ id: KEY_ID, keyPrefix: 'gtv_abcd1234' }])),
    )

    const result = await apiKeys.list()

    expect(result).toHaveLength(1)
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    expect(url).toBe(`${BASE_URL}/me/api-keys`)
    expect(init.method ?? 'GET').toBe('GET')
  })

  it('revoke() DELETEs /me/api-keys/{id} and tolerates 204', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => emptyResponse(204)),
    )

    await expect(apiKeys.revoke(KEY_ID)).resolves.toBeUndefined()
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    expect(url).toBe(`${BASE_URL}/me/api-keys/${KEY_ID}`)
    expect(init.method).toBe('DELETE')
  })

  it('revoke() url-encodes the id', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => emptyResponse(204)),
    )

    await apiKeys.revoke('weird/id')
    const [url] = vi.mocked(fetch).mock.calls[0] as [string]
    expect(url).toBe(`${BASE_URL}/me/api-keys/weird%2Fid`)
  })
})
