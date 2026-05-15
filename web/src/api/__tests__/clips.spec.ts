import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { clips } from '../clips'
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

describe('api/clips', () => {
  describe('feed()', () => {
    it('GETs /clips/feed without params when no query is supplied', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ items: [], nextCursor: null })),
      )

      const result = await clips.feed()

      expect(result).toEqual({ items: [], nextCursor: null })
      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/clips/feed`)
    })

    it('encodes cursor and limit into the query string', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ items: [], nextCursor: null })),
      )

      await clips.feed({ cursor: 'abc=', limit: 5 })

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      // URLSearchParams encodes '=' inside the value; both keys must be present.
      expect(url).toContain('cursor=abc%3D')
      expect(url).toContain('limit=5')
      expect(url.startsWith(`${BASE_URL}/clips/feed?`)).toBe(true)
    })
  })

  describe('getDetail()', () => {
    it('issues GET /clips/{id} and returns the parsed detail', async () => {
      const detail = {
        id: 'a4f1e2c0-0000-0000-0000-000000000001',
        title: 'Sample',
        description: null,
        videoUrl: 'https://cdn.example.com/clips/abc.mp4?sig=xyz',
        videoUrlExpiresAt: '2026-04-26T13:00:00Z',
        thumbnailUrl: 'https://cdn.example.com/thumbs/abc.jpg?sig=xyz',
        durationSecs: 42,
        width: 1920,
        height: 1080,
        viewCount: 7,
        likeCount: 3,
        createdAt: '2026-04-26T12:00:00Z',
        author: { id: 'u', username: 'zoe', avatarUrl: null },
        likedByMe: false,
      }
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse(detail)),
      )

      const result = await clips.getDetail(detail.id)

      expect(result).toEqual(detail)
      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/clips/${detail.id}`)
    })

    it('URI-encodes special characters in the id path segment', async () => {
      // Server uses GUIDs today, but encoding is a cheap insurance against future
      // schemes (slugs, short codes) that could include reserved characters.
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({})),
      )

      await clips.getDetail('weird id/with?chars')

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/clips/${encodeURIComponent('weird id/with?chars')}`)
    })
  })

  describe('create()', () => {
    it('POSTs the clip metadata and returns the new clip id', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ id: 'new-clip-id' }, 201)),
      )

      const result = await clips.create({
        title: 'My clip',
        description: 'desc',
        gameId: null,
        visibility: 'public',
      })

      expect(result).toEqual({ id: 'new-clip-id' })
      const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(url).toBe(`${BASE_URL}/clips`)
      expect(init.method).toBe('POST')
      // The api() client must auto-set this when body is a plain object — locked here
      // so a future client refactor doesn't silently drop the header on JSON requests.
      expect(new Headers(init.headers).get('Content-Type')).toBe('application/json')
      expect(JSON.parse(String(init.body))).toEqual({
        title: 'My clip',
        description: 'desc',
        gameId: null,
        visibility: 'public',
      })
    })
  })

  describe('getUploadUrl()', () => {
    it('POSTs to /clips/{id}/upload-url and returns the presigned PUT URL', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () =>
          jsonResponse({
            url: 'https://minio.example.com/clips/abc?sig=xyz',
            expiresAt: '2026-04-26T13:15:00Z',
          }),
        ),
      )

      const result = await clips.getUploadUrl('clip-id')

      expect(result.url).toBe('https://minio.example.com/clips/abc?sig=xyz')
      expect(result.expiresAt).toBe('2026-04-26T13:15:00Z')
      const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(url).toBe(`${BASE_URL}/clips/clip-id/upload-url`)
      expect(init.method).toBe('POST')
    })
  })

  describe('complete()', () => {
    it('POSTs to /clips/{id}/complete and returns the file size confirmation', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ id: 'clip-id', fileSizeBytes: 1234567 })),
      )

      const result = await clips.complete('clip-id')

      expect(result).toEqual({ id: 'clip-id', fileSizeBytes: 1234567 })
      const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(url).toBe(`${BASE_URL}/clips/clip-id/complete`)
      expect(init.method).toBe('POST')
    })
  })

  describe('update()', () => {
    const CLIP_ID = 'a4f1e2c0-0000-0000-0000-000000000001'
    const baseDetail = {
      id: CLIP_ID,
      title: 'My clip',
      description: null,
      videoUrl: 'https://cdn.example.com/clips/abc.mp4?sig=xyz',
      videoUrlExpiresAt: '2026-04-26T13:00:00Z',
      thumbnailUrl: 'https://cdn.example.com/thumbs/abc.jpg?sig=xyz',
      durationSecs: 42,
      width: 1920,
      height: 1080,
      viewCount: 7,
      likeCount: 3,
      createdAt: '2026-04-26T12:00:00Z',
      author: { id: 'u', username: 'zoe', avatarUrl: null },
      game: null,
      likedByMe: false,
      visibility: 'public' as const,
    }

    it('PATCHes /clips/{id} and returns the updated ClipDetail', async () => {
      const updated = { ...baseDetail, title: 'Updated title', visibility: 'unlisted' as const }
      vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(updated)))

      const result = await clips.update(CLIP_ID, { title: 'Updated title', visibility: 'unlisted' })

      expect(result).toEqual(updated)
      const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(url).toBe(`${BASE_URL}/clips/${CLIP_ID}`)
      expect(init.method).toBe('PATCH')
      expect(new Headers(init.headers).get('Content-Type')).toBe('application/json')
    })

    it('sends only the provided keys (sparse payload)', async () => {
      vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(baseDetail)))

      await clips.update(CLIP_ID, { title: 'New title' })

      const [, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(JSON.parse(String(init.body))).toEqual({ title: 'New title' })
    })

    it('includes gameId: null when explicitly passed', async () => {
      vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(baseDetail)))

      await clips.update(CLIP_ID, { gameId: null })

      const [, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(JSON.parse(String(init.body))).toEqual({ gameId: null })
    })

    it('sends the bearer token when one is configured', async () => {
      configureAuth({
        getAccessToken: () => 'tok-xyz',
        getRefreshToken: () => null,
        onTokenRefreshed: () => {},
        onRefreshFailed: () => {},
      })
      vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(baseDetail)))

      await clips.update(CLIP_ID, { visibility: 'unlisted' })

      const [, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(new Headers(init.headers).get('Authorization')).toBe('Bearer tok-xyz')
    })

    it('throws ApiError on non-2xx with the response body', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ code: 'invalid_title' }, 400)),
      )

      await expect(clips.update(CLIP_ID, { title: '' })).rejects.toMatchObject({
        status: 400,
      })
    })
  })

  describe('like() / unlike()', () => {
    it('POSTs /clips/{id}/like and returns the new count + liked flag', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ likeCount: 8, liked: true })),
      )

      const result = await clips.like('clip-id')

      expect(result).toEqual({ likeCount: 8, liked: true })
      const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(url).toBe(`${BASE_URL}/clips/clip-id/like`)
      expect(init.method).toBe('POST')
    })

    it('DELETEs /clips/{id}/like to unlike', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ likeCount: 7, liked: false })),
      )

      const result = await clips.unlike('clip-id')

      expect(result).toEqual({ likeCount: 7, liked: false })
      const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(url).toBe(`${BASE_URL}/clips/clip-id/like`)
      expect(init.method).toBe('DELETE')
    })
  })
})
