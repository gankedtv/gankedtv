// Browser error monitoring → self-hosted GlitchTip. Opt-in: no VITE_SENTRY_DSN ⇒ no-op (dev/local).
import type { App } from 'vue'
import type { Router } from 'vue-router'
import type { Breadcrumb, ErrorEvent } from '@sentry/vue'
import * as Sentry from '@sentry/vue'

import { stripSensitiveParams } from './sensitiveParams'

const API_BASE_URL =
  (import.meta.env.VITE_API_BASE_URL as string | undefined) ?? 'http://localhost:5050'

/** Strip credential-bearing query params from any captured URL string. */
function redactUrl(url: unknown): unknown {
  return typeof url === 'string' ? stripSensitiveParams(url) : url
}

/** Drop request headers/cookies and redact sensitive URL params — belt-and-braces with sendDefaultPii=false. */
export function scrubEvent(event: ErrorEvent): ErrorEvent {
  if (event.request) {
    event.request.url = redactUrl(event.request.url) as string | undefined
    delete event.request.headers
    delete event.request.cookies
  }
  return event
}

/** Redact sensitive query params from breadcrumb URLs (navigation/fetch/xhr crumbs). */
export function scrubBreadcrumb(breadcrumb: Breadcrumb): Breadcrumb {
  if (breadcrumb.data) {
    breadcrumb.data.url = redactUrl(breadcrumb.data.url)
    breadcrumb.data.from = redactUrl(breadcrumb.data.from)
    breadcrumb.data.to = redactUrl(breadcrumb.data.to)
  }
  return breadcrumb
}

/** No DSN ⇒ no-op. Must run before `app.use(router)` so the router/tracing integration binds. */
export function initSentry(app: App, router: Router): void {
  const dsn = import.meta.env.VITE_SENTRY_DSN?.trim()
  if (!dsn) return

  const sampleRate = Number.parseFloat(import.meta.env.VITE_SENTRY_TRACES_SAMPLE_RATE ?? '')

  Sentry.init({
    app,
    dsn,
    environment: import.meta.env.VITE_SENTRY_ENVIRONMENT?.trim() || import.meta.env.MODE,
    release: import.meta.env.VITE_SENTRY_RELEASE?.trim() || __APP_VERSION__,
    integrations: [Sentry.browserTracingIntegration({ router })],
    tracesSampleRate: Number.isFinite(sampleRate) ? sampleRate : 0.01,
    tracePropagationTargets: [API_BASE_URL],
    // PII off; the helpers above scrub defensively.
    sendDefaultPii: false,
    beforeSend: scrubEvent,
    beforeBreadcrumb: scrubBreadcrumb,
  })
}
