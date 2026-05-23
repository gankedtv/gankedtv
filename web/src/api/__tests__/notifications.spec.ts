import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { notifications } from '../notifications'
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

const NOTIF_ID = 'b5f2e3d1-0000-0000-0000-000000000007'

describe('api/notifications', () => {
  describe('list()', () => {
    it('GETs /me/notifications without query when no params supplied', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ items: [], nextCursor: null })),
      )

      const result = await notifications.list()

      expect(result).toEqual({ items: [], nextCursor: null })
      const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(url).toBe(`${BASE_URL}/me/notifications`)
      expect(init.method ?? 'GET').toBe('GET')
    })

    it('encodes cursor and limit into the query string', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ items: [], nextCursor: null })),
      )

      await notifications.list({ cursor: 'abc=', limit: 5 })
      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      const parsed = new URL(url)
      expect(parsed.pathname).toBe('/me/notifications')
      expect(parsed.searchParams.get('cursor')).toBe('abc=')
      expect(parsed.searchParams.get('limit')).toBe('5')
    })
  })

  it('unreadCount() GETs /me/notifications/unread-count', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ count: 7 })),
    )

    const result = await notifications.unreadCount()

    expect(result.count).toBe(7)
    const [url] = vi.mocked(fetch).mock.calls[0] as [string]
    expect(url).toBe(`${BASE_URL}/me/notifications/unread-count`)
  })

  it('markAllRead() POSTs /me/notifications/read', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ marked: 3 })),
    )

    const result = await notifications.markAllRead()

    expect(result.marked).toBe(3)
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    expect(url).toBe(`${BASE_URL}/me/notifications/read`)
    expect(init.method).toBe('POST')
  })

  it('markOneRead() POSTs /me/notifications/{id}/read and tolerates 204', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => emptyResponse(204)),
    )

    await expect(notifications.markOneRead(NOTIF_ID)).resolves.toBeUndefined()
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    expect(url).toBe(`${BASE_URL}/me/notifications/${NOTIF_ID}/read`)
    expect(init.method).toBe('POST')
  })
})
