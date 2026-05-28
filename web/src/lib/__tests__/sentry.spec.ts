import { describe, it, expect } from 'vitest'
import type { Breadcrumb, ErrorEvent } from '@sentry/vue'
import { scrubEvent, scrubBreadcrumb } from '../sentry'

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
