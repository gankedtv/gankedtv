import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { profileMedia } from '../profileMedia'
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

function stubJson(body: object, status = 200) {
  vi.stubGlobal(
    'fetch',
    vi.fn(
      async () =>
        new Response(JSON.stringify(body), {
          status,
          headers: { 'content-type': 'application/json' },
        }),
    ),
  )
}

describe('api/profileMedia', () => {
  it('POSTs the avatar upload-url request with the chosen content type', async () => {
    stubJson({ url: 'https://signed', expiresAt: '', contentType: 'image/png', objectKey: 'k' })
    const out = await profileMedia.getAvatarUploadUrl('image/png')
    expect(out.url).toBe('https://signed')
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    expect(url).toBe(`${BASE_URL}/auth/me/avatar/upload-url`)
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({ contentType: 'image/png' })
  })

  it('POSTs the banner upload-url request with the chosen content type', async () => {
    stubJson({ url: 'https://signed', expiresAt: '', contentType: 'image/webp', objectKey: 'k' })
    await profileMedia.getBannerUploadUrl('image/webp')
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    expect(url).toBe(`${BASE_URL}/auth/me/banner/upload-url`)
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({ contentType: 'image/webp' })
  })

  it('POSTs the avatar complete request with the echoed object key', async () => {
    stubJson({ url: 'https://final', objectKey: 'abc', avatarSource: 'upload' })
    const out = await profileMedia.completeAvatar('abc')
    expect(out.objectKey).toBe('abc')
    expect(out.avatarSource).toBe('upload')
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    expect(url).toBe(`${BASE_URL}/auth/me/avatar/complete`)
    expect(JSON.parse(init.body as string)).toEqual({ objectKey: 'abc' })
  })

  it('POSTs the banner complete request', async () => {
    stubJson({ url: 'https://final', objectKey: 'abc' })
    const out = await profileMedia.completeBanner('abc')
    expect(out.avatarSource).toBeUndefined()
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    expect(url).toBe(`${BASE_URL}/auth/me/banner/complete`)
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({ objectKey: 'abc' })
  })

  it('DELETEs the avatar and surfaces the restored URL / source', async () => {
    stubJson({ url: 'https://cdn.discord/u.png', avatarSource: 'oauth:discord' })
    const out = await profileMedia.deleteAvatar()
    expect(out.url).toBe('https://cdn.discord/u.png')
    expect(out.avatarSource).toBe('oauth:discord')
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    expect(url).toBe(`${BASE_URL}/auth/me/avatar`)
    expect(init.method).toBe('DELETE')
  })

  it('DELETEs the banner', async () => {
    stubJson({ url: null })
    const out = await profileMedia.deleteBanner()
    expect(out.avatarSource).toBeUndefined()
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    expect(url).toBe(`${BASE_URL}/auth/me/banner`)
    expect(init.method).toBe('DELETE')
  })
})
