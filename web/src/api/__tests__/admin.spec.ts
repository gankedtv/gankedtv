import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import {
  banUser,
  hideClip,
  listReports,
  removeComment,
  resolveReport,
  unbanUser,
  unhideClip,
} from '../admin'
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

describe('api/admin', () => {
  it('listReports() encodes status + paging into the query', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ items: [], page: 1, pageSize: 20, total: 0 })),
    )

    await listReports({ status: 'open', page: 2, pageSize: 50 })

    const [url] = vi.mocked(fetch).mock.calls[0] as [string]
    expect(url).toContain('status=open')
    expect(url).toContain('page=2')
    expect(url).toContain('pageSize=50')
    expect(url.startsWith(`${BASE_URL}/admin/reports?`)).toBe(true)
  })

  it('listReports() with no params hits the base URL', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ items: [], page: 1, pageSize: 20, total: 0 })),
    )
    await listReports({})
    const [url] = vi.mocked(fetch).mock.calls[0] as [string]
    expect(url).toBe(`${BASE_URL}/admin/reports`)
  })

  it('resolveReport() POSTs the outcome', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({})),
    )
    await resolveReport('r1', 'dismissed')
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    expect(url).toBe(`${BASE_URL}/admin/reports/r1/resolve`)
    expect(JSON.parse(String(init.body))).toEqual({ outcome: 'dismissed' })
  })

  it('hideClip / unhideClip route to the right endpoints', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({})),
    )
    await hideClip('c1')
    await unhideClip('c2')
    const [hideUrl] = vi.mocked(fetch).mock.calls[0] as [string]
    const [unhideUrl] = vi.mocked(fetch).mock.calls[1] as [string]
    expect(hideUrl).toBe(`${BASE_URL}/admin/clips/c1/hide`)
    expect(unhideUrl).toBe(`${BASE_URL}/admin/clips/c2/unhide`)
  })

  it('removeComment routes to /admin/comments/{id}/remove', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({})),
    )
    await removeComment('cm1')
    const [url] = vi.mocked(fetch).mock.calls[0] as [string]
    expect(url).toBe(`${BASE_URL}/admin/comments/cm1/remove`)
  })

  it('banUser passes the reason through; unbanUser does not', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({})),
    )
    await banUser('u1', 'spamming')
    await unbanUser('u1')
    const [, banInit] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    expect(JSON.parse(String(banInit.body))).toEqual({ reason: 'spamming' })
    const [, unbanInit] = vi.mocked(fetch).mock.calls[1] as [string, RequestInit]
    expect(JSON.parse(String(unbanInit.body))).toEqual({})
  })
})
