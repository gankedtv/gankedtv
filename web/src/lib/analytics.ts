// Thin, swappable analytics wrapper. The app talks to `trackPageView` / `trackEvent`; the
// concrete backend (Google Analytics 4 today, e.g. PostHog later) sits behind an
// `AnalyticsProvider` so swapping vendors is a one-file change. Loading is gated on the
// presence of `VITE_GA_MEASUREMENT_ID`, so without the env var everything is a no-op and
// dev/local stays clean (no network, no cookies). Consent gating is a documented follow-up.

export interface AnalyticsProvider {
  trackPageView(path: string, title?: string): void
  trackEvent(name: string, params?: Record<string, unknown>): void
}

// Re-exported so the router keeps importing redaction from one place; the implementation +
// key list live in ./sensitiveParams (shared with Sentry's PII scrubbing).
export { stripSensitiveParams } from './sensitiveParams'

const noopProvider: AnalyticsProvider = {
  trackPageView() {},
  trackEvent() {},
}

let provider: AnalyticsProvider = noopProvider

/** Replace the active provider — used by `initAnalytics` and by tests. */
export function setAnalyticsProvider(next: AnalyticsProvider): void {
  provider = next
}

/** True once a real (non-noop) provider is active. */
export function isAnalyticsEnabled(): boolean {
  return provider !== noopProvider
}

declare global {
  interface Window {
    dataLayer?: unknown[]
    gtag?: (...args: unknown[]) => void
  }
}

/** Loads gtag.js once and returns a GA4-backed provider. */
function createGtagProvider(measurementId: string): AnalyticsProvider {
  if (typeof document !== 'undefined') {
    // The script + dataLayer/gtag stub bootstrap exactly once; re-init must not inject twice.
    if (!window.gtag) {
      const script = document.createElement('script')
      script.async = true
      script.src = `https://www.googletagmanager.com/gtag/js?id=${encodeURIComponent(measurementId)}`
      document.head.appendChild(script)

      window.dataLayer = window.dataLayer || []
      window.gtag = function gtag(...args: unknown[]) {
        window.dataLayer!.push(args)
      }
      window.gtag('js', new Date())
    }
    // config runs on every init (outside the bootstrap guard) so a re-init with a new
    // measurement id still configures it. send_page_view:false — SPA page views are emitted by
    // the router (see router/index.ts); the initial automatic hit would double-count otherwise.
    window.gtag('config', measurementId, { send_page_view: false })
  }

  return {
    trackPageView(path, title) {
      // GA4 reads page_location (absolute URL) + page_title; page_path is kept for reports
      // that still key off it. Resolve location/title from the document when not supplied.
      const origin = typeof window !== 'undefined' ? window.location.origin : ''
      window.gtag?.('event', 'page_view', {
        page_path: path,
        page_location: origin + path,
        page_title: title ?? (typeof document !== 'undefined' ? document.title : undefined),
      })
    },
    trackEvent(name, params) {
      window.gtag?.('event', name, params)
    },
  }
}

/**
 * Wires up analytics from the build-time measurement id. No id (undefined/empty) ⇒ no-op,
 * so production is the only place GA actually loads. Idempotent enough for app bootstrap.
 */
export function initAnalytics(measurementId: string | undefined): void {
  const id = measurementId?.trim()
  if (!id) {
    setAnalyticsProvider(noopProvider)
    return
  }
  setAnalyticsProvider(createGtagProvider(id))
}

export function trackPageView(path: string, title?: string): void {
  provider.trackPageView(path, title)
}

export function trackEvent(name: string, params?: Record<string, unknown>): void {
  provider.trackEvent(name, params)
}
