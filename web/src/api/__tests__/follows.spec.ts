import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { follows } from '../follows'
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
  return new Response(null, { status })
}

describe('api/follows', () => {
  describe('follow()', () => {
    it('POSTs /users/{username}/follow and resolves on 204', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => emptyResponse(204)),
      )

      await expect(follows.follow('alice')).resolves.toBeUndefined()
      const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(url).toBe(`${BASE_URL}/users/alice/follow`)
      expect(init.method).toBe('POST')
    })

    it('URI-encodes the username path segment', async () => {
      // Usernames are normally [a-z0-9_], but encoding is cheap insurance against
      // future schemes (display names, unicode handles).
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => emptyResponse(204)),
      )

      await follows.follow('weird name/with?chars')

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/users/${encodeURIComponent('weird name/with?chars')}/follow`)
    })

    it('sends the bearer token when one is configured', async () => {
      configureAuth({
        getAccessToken: () => 'tok-xyz',
        getRefreshToken: () => null,
        onTokenRefreshed: () => {},
        onRefreshFailed: () => {},
      })
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => emptyResponse(204)),
      )

      await follows.follow('alice')

      const [, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(new Headers(init.headers).get('Authorization')).toBe('Bearer tok-xyz')
    })

    it('throws ApiError on non-2xx (e.g. self_follow → 400)', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ code: 'self_follow' }, 400)),
      )

      await expect(follows.follow('me')).rejects.toMatchObject({
        status: 400,
        body: { code: 'self_follow' },
      })
    })
  })

  describe('unfollow()', () => {
    it('DELETEs /users/{username}/follow and resolves on 204', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => emptyResponse(204)),
      )

      await expect(follows.unfollow('alice')).resolves.toBeUndefined()
      const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(url).toBe(`${BASE_URL}/users/alice/follow`)
      expect(init.method).toBe('DELETE')
    })
  })

  describe('listFollowers() / listFollowing()', () => {
    it('GETs /users/{username}/followers without params when query is empty', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ items: [], nextCursor: null })),
      )

      const result = await follows.listFollowers('alice')

      expect(result).toEqual({ items: [], nextCursor: null })
      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/users/alice/followers`)
    })

    it('GETs /users/{username}/following with cursor + limit', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ items: [], nextCursor: null })),
      )

      await follows.listFollowing('alice', { cursor: 'abc=', limit: 5 })

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toContain('cursor=abc%3D')
      expect(url).toContain('limit=5')
      expect(url.startsWith(`${BASE_URL}/users/alice/following?`)).toBe(true)
    })

    it('parses the UserSummaryPage shape', async () => {
      const page = {
        items: [
          { id: 'u1', username: 'bob', avatarUrl: null },
          { id: 'u2', username: 'carol', avatarUrl: 'https://cdn/c.png' },
        ],
        nextCursor: 'next',
      }
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse(page)),
      )

      const result = await follows.listFollowers('alice')

      expect(result).toEqual(page)
    })
  })
})
