import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { comments } from '../comments'
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

const CLIP_ID = 'a4f1e2c0-0000-0000-0000-000000000001'
const COMMENT_ID = 'b5f2e3d1-0000-0000-0000-000000000002'

describe('api/comments', () => {
  describe('list()', () => {
    it('GETs /clips/{id}/comments without params when no query is supplied', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ items: [], nextCursor: null })),
      )

      const result = await comments.list(CLIP_ID)

      expect(result).toEqual({ items: [], nextCursor: null })
      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/clips/${CLIP_ID}/comments`)
    })

    it('encodes cursor and limit into the query string', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ items: [], nextCursor: null })),
      )

      await comments.list(CLIP_ID, { cursor: 'abc=', limit: 5 })

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toContain('cursor=abc%3D')
      expect(url).toContain('limit=5')
      expect(url.startsWith(`${BASE_URL}/clips/${CLIP_ID}/comments?`)).toBe(true)
    })

    it('URI-encodes the clip id path segment', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ items: [], nextCursor: null })),
      )

      await comments.list('weird id/with?chars')

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/clips/${encodeURIComponent('weird id/with?chars')}/comments`)
    })
  })

  describe('listReplies()', () => {
    it('GETs /comments/{id}/replies and forwards the cursor', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ items: [], nextCursor: null })),
      )

      await comments.listReplies(COMMENT_ID, { cursor: 'cur1' })

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/comments/${COMMENT_ID}/replies?cursor=cur1`)
    })
  })

  describe('create()', () => {
    it('POSTs a top-level comment body and returns the created item', async () => {
      const created = {
        id: 'new-id',
        body: 'first!',
        author: { id: 'u', username: 'zoe', avatarUrl: null },
        parentId: null,
        createdAt: '2026-05-22T12:00:00Z',
        replyCount: 0,
        replies: [],
        deleted: false,
      }
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse(created, 201)),
      )

      const result = await comments.create(CLIP_ID, { body: 'first!' })

      expect(result).toEqual(created)
      const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(url).toBe(`${BASE_URL}/clips/${CLIP_ID}/comments`)
      expect(init.method).toBe('POST')
      expect(new Headers(init.headers).get('Content-Type')).toBe('application/json')
      expect(JSON.parse(String(init.body))).toEqual({ body: 'first!' })
    })

    it('includes parentId when replying', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({}, 201)),
      )

      await comments.create(CLIP_ID, { body: 'nice', parentId: COMMENT_ID })

      const [, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(JSON.parse(String(init.body))).toEqual({ body: 'nice', parentId: COMMENT_ID })
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
        vi.fn(async () => jsonResponse({}, 201)),
      )

      await comments.create(CLIP_ID, { body: 'hi' })

      const [, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(new Headers(init.headers).get('Authorization')).toBe('Bearer tok-xyz')
    })

    it('throws ApiError on non-2xx with the response body', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ code: 'invalid_parent' }, 400)),
      )

      await expect(comments.create(CLIP_ID, { body: 'x', parentId: 'p' })).rejects.toMatchObject({
        status: 400,
        body: { code: 'invalid_parent' },
      })
    })
  })

  describe('delete()', () => {
    it('DELETEs /comments/{id} and resolves to undefined on 204', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => new Response(null, { status: 204 })),
      )

      const result = await comments.delete(COMMENT_ID)

      expect(result).toBeUndefined()
      const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(url).toBe(`${BASE_URL}/comments/${COMMENT_ID}`)
      expect(init.method).toBe('DELETE')
    })

    it('throws ApiError on 403 with the response body', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ code: 'forbidden' }, 403)),
      )

      await expect(comments.delete(COMMENT_ID)).rejects.toMatchObject({
        status: 403,
        body: { code: 'forbidden' },
      })
    })
  })
})
