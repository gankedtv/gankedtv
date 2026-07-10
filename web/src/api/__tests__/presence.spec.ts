import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { presence, getClientId } from '../presence'
import { configureAuth, BASE_URL } from '../client'
import { createLocalStorageMock, installLocalStorage, type MockLocalStorage } from '@/test/helpers'

let storage: MockLocalStorage

beforeEach(() => {
  configureAuth({
    getAccessToken: () => null,
    getRefreshToken: () => null,
    onTokenRefreshed: () => {},
    onRefreshFailed: () => {},
  })
  storage = createLocalStorageMock()
  installLocalStorage(storage)
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

describe('getClientId', () => {
  it('generates a UUID once and returns the same id on later calls', () => {
    const first = getClientId()
    const second = getClientId()

    expect(first).toBeTruthy()
    expect(second).toBe(first)
    expect(storage.getItem('presence_cid')).toBe(first)
  })

  it('returns null when localStorage is unavailable', () => {
    storage.__throwMode = true

    expect(getClientId()).toBeNull()
  })
})

describe('api/presence', () => {
  it('GETs /presence/summary with the stable cid attached', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ online: 12, followsOnline: [] })),
    )

    await presence.summary()

    const cid = storage.getItem('presence_cid')
    const [url] = vi.mocked(fetch).mock.calls[0] as [string]
    expect(cid).toBeTruthy()
    expect(url).toBe(`${BASE_URL}/presence/summary?cid=${cid}`)
  })

  it('omits cid when localStorage is unavailable', async () => {
    storage.__throwMode = true
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ online: 1, followsOnline: [] })),
    )

    await presence.summary()

    const [url] = vi.mocked(fetch).mock.calls[0] as [string]
    expect(url).toBe(`${BASE_URL}/presence/summary`)
  })

  it('returns the parsed summary shape', async () => {
    const body = {
      online: 12847,
      followsOnline: [{ id: 'u1', username: 'gankster', avatarUrl: null }],
    }
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse(body)),
    )

    const result = await presence.summary()

    expect(result).toEqual(body)
  })
})
