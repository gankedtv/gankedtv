import { describe, it, expect, vi, beforeEach } from 'vitest'
import type { Breadcrumb, ErrorEvent } from '@sentry/vue'
import * as Sentry from '@sentry/vue'
import { scrubEvent, scrubBreadcrumb, reportPlaybackFailure, notePlaybackRecovery } from '../sentry'

vi.mock('@sentry/vue', () => ({ captureMessage: vi.fn(), addBreadcrumb: vi.fn() }))

describe('scrubEvent', () => {
  it('drops request headers and cookies', () => {
    const event = {
      request: {
        url: '/feed',
        headers: { Authorization: 'Bearer jwt', 'Content-Type': 'application/json' },
        cookies: { refresh: 'rt' },
      },
    } as unknown as ErrorEvent

    const out = scrubEvent(event)

    expect(out.request?.headers).toBeUndefined()
    expect(out.request?.cookies).toBeUndefined()
  })

  it('redacts sensitive query params from the request url', () => {
    const event = {
      request: { url: '/auth/callback?token=jwt&refresh=rt&foo=bar' },
    } as unknown as ErrorEvent

    expect(scrubEvent(event).request?.url).toBe('/auth/callback?foo=bar')
  })

  it('passes an event without request data through unchanged', () => {
    const event = { message: 'boom' } as ErrorEvent
    expect(scrubEvent(event)).toBe(event)
  })
})

describe('scrubBreadcrumb', () => {
  it('redacts url/from/to in breadcrumb data', () => {
    const crumb: Breadcrumb = {
      category: 'navigation',
      data: {
        url: '/auth/callback?code=abc&keep=1',
        from: '/login?state=xyz',
        to: '/feed?refresh=rt',
      },
    }

    const out = scrubBreadcrumb(crumb)

    expect(out.data?.url).toBe('/auth/callback?keep=1')
    expect(out.data?.from).toBe('/login')
    expect(out.data?.to).toBe('/feed')
  })

  it('passes a breadcrumb without data through unchanged', () => {
    const crumb: Breadcrumb = { message: 'navigated' }
    expect(scrubBreadcrumb(crumb)).toBe(crumb)
  })
})

describe('playback reporting', () => {
  beforeEach(() => {
    vi.mocked(Sentry.captureMessage).mockReset()
    vi.mocked(Sentry.addBreadcrumb).mockReset()
  })

  it('reports a failed playback as one tagged warning', () => {
    reportPlaybackFailure({
      clipId: 'clp_01',
      mode: 'direct',
      codec: 'av1',
      mediaErrorCode: 4,
      detail: 'MEDIA_ELEMENT_ERROR: Format error',
    })

    expect(Sentry.captureMessage).toHaveBeenCalledWith('Clip playback failed', {
      level: 'warning',
      tags: { 'playback.mode': 'direct', 'playback.codec': 'av1', 'playback.media_error': '4' },
      extra: { clipId: 'clp_01', detail: 'MEDIA_ELEMENT_ERROR: Format error' },
    })
  })

  it('tags an unknown codec and a missing media error explicitly', () => {
    reportPlaybackFailure({ clipId: 'clp_01', mode: 'hls.js', codec: null, mediaErrorCode: null })

    expect(Sentry.captureMessage).toHaveBeenCalledWith('Clip playback failed', {
      level: 'warning',
      tags: {
        'playback.mode': 'hls.js',
        'playback.codec': 'unknown',
        'playback.media_error': 'none',
      },
      extra: { clipId: 'clp_01', detail: null },
    })
  })

  it('records a recovery step as a breadcrumb, not an event', () => {
    notePlaybackRecovery('Falling back to the JIT stream', { clipId: 'clp_01' })

    expect(Sentry.addBreadcrumb).toHaveBeenCalledWith({
      category: 'playback',
      level: 'warning',
      message: 'Falling back to the JIT stream',
      data: { clipId: 'clp_01' },
    })
    expect(Sentry.captureMessage).not.toHaveBeenCalled()
  })
})
