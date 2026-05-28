import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import {
  initAnalytics,
  isAnalyticsEnabled,
  setAnalyticsProvider,
  stripSensitiveParams,
  trackEvent,
  trackPageView,
} from '../analytics'

describe('analytics wrapper', () => {
  beforeEach(() => {
    // Reset module-level state and any injected gtag between tests.
    setAnalyticsProvider({ trackPageView() {}, trackEvent() {} })
    delete window.gtag
    delete window.dataLayer
    document.head.querySelectorAll('script').forEach((s) => s.remove())
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('is a no-op when no measurement id is provided', () => {
    initAnalytics(undefined)
    expect(isAnalyticsEnabled()).toBe(false)
    // Must not throw or touch window.gtag.
    trackPageView('/x')
    trackEvent('click', { a: 1 })
    expect(window.gtag).toBeUndefined()
  })

  it('treats a blank/whitespace id as disabled', () => {
    initAnalytics('   ')
    expect(isAnalyticsEnabled()).toBe(false)
    expect(window.gtag).toBeUndefined()
  })

  it('loads gtag.js and configures GA4 when an id is provided', () => {
    initAnalytics('G-TEST123')
    expect(isAnalyticsEnabled()).toBe(true)

    const script = document.head.querySelector<HTMLScriptElement>('script[src*="googletagmanager"]')
    expect(script).not.toBeNull()
    expect(script!.src).toContain('id=G-TEST123')
    expect(typeof window.gtag).toBe('function')
  })

  it('forwards page views and events to gtag', () => {
    initAnalytics('G-TEST123')
    const gtag = vi.fn()
    window.gtag = gtag

    trackPageView('/feed', 'Feed')
    trackEvent('share', { clipId: '42' })

    expect(gtag).toHaveBeenCalledWith(
      'event',
      'page_view',
      expect.objectContaining({
        page_path: '/feed',
        page_location: `${window.location.origin}/feed`,
        page_title: 'Feed',
      }),
    )
    expect(gtag).toHaveBeenCalledWith('event', 'share', { clipId: '42' })
  })

  it('does not inject the script twice but still configures on re-init', () => {
    const gtag = vi.fn()
    window.gtag = gtag
    initAnalytics('G-TEST123')

    const scripts = document.head.querySelectorAll('script[src*="googletagmanager"]')
    expect(scripts).toHaveLength(0)
    // config must still run even though the gtag stub already existed.
    expect(gtag).toHaveBeenCalledWith('config', 'G-TEST123', { send_page_view: false })
  })
})

describe('stripSensitiveParams', () => {
  it('returns the path unchanged when there is no query string', () => {
    expect(stripSensitiveParams('/feed')).toBe('/feed')
  })

  it('drops OAuth credential params but keeps the rest', () => {
    const out = stripSensitiveParams(
      '/auth/callback?token=jwt&refresh=rt&code=c&state=s&access_token=a&id_token=i&returnTo=/feed',
    )
    expect(out).toBe('/auth/callback?returnTo=%2Ffeed')
  })

  it('collapses to the bare path when every param is sensitive', () => {
    expect(stripSensitiveParams('/auth/callback?token=jwt&refresh=rt')).toBe('/auth/callback')
  })

  it('leaves non-sensitive query strings intact', () => {
    expect(stripSensitiveParams('/trending?sort=week&page=2')).toBe('/trending?sort=week&page=2')
  })

  it('preserves a fragment while redacting the query', () => {
    expect(stripSensitiveParams('/auth/callback?token=jwt&returnTo=/feed#section')).toBe(
      '/auth/callback?returnTo=%2Ffeed#section',
    )
  })

  it('keeps a fragment when there is no query string', () => {
    expect(stripSensitiveParams('/article#comments')).toBe('/article#comments')
  })
})
