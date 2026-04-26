import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { users } from '../users'
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

describe('api/users', () => {
  describe('getByUsername()', () => {
    it('issues GET /users/{username} (URI-encoded) and returns the parsed profile', async () => {
      const profile = {
        id: 'u-1',
        username: 'zoe.qa',
        bio: null,
        avatarUrl: null,
        createdAt: '2026-01-01T00:00:00Z',
        clips: [],
      }
      vi.stubGlobal(
        'fetch',
        vi.fn(
          async () =>
            new Response(JSON.stringify(profile), {
              status: 200,
              headers: { 'content-type': 'application/json' },
            }),
        ),
      )

      const result = await users.getByUsername('zoe.qa')

      expect(result).toEqual(profile)
      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      // Encoding matters — usernames may contain '.' (legal, no encoding) but the API
      // also accepts case-sensitive lookups; encodeURIComponent guards against future
      // characters that would otherwise corrupt the path.
      expect(url).toBe(`${BASE_URL}/users/zoe.qa`)
    })

    it('URI-encodes special characters in the username path segment', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(
          async () =>
            new Response(JSON.stringify({}), {
              status: 200,
              headers: { 'content-type': 'application/json' },
            }),
        ),
      )

      await users.getByUsername('weird user/name')

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/users/${encodeURIComponent('weird user/name')}`)
    })
  })
})
