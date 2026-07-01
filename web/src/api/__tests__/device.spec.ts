import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { device } from '../device'
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

describe('api/device', () => {
  it('lookup() GETs /me/device/{userCode} url-encoded', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ clientName: 'rewynd', status: 'pending' })),
    )

    const result = await device.lookup('WDJB-MJHT')

    expect(result.clientName).toBe('rewynd')
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    expect(url).toBe(`${BASE_URL}/me/device/WDJB-MJHT`)
    expect(init.method ?? 'GET').toBe('GET')
  })

  it('approve() POSTs /me/device/approve with the code', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => emptyResponse(204)),
    )

    await expect(device.approve('WDJB-MJHT')).resolves.toBeUndefined()
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    expect(url).toBe(`${BASE_URL}/me/device/approve`)
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({ userCode: 'WDJB-MJHT' })
  })

  it('deny() POSTs /me/device/deny with the code', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => emptyResponse(204)),
    )

    await device.deny('WDJB-MJHT')
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    expect(url).toBe(`${BASE_URL}/me/device/deny`)
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({ userCode: 'WDJB-MJHT' })
  })
})
