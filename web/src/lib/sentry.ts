// Browser error/crash monitoring → self-hosted GlitchTip (Sentry-API-compatible). Opt-in: no
// VITE_SENTRY_DSN (the case for dev/local) ⇒ no-op, mirroring the analytics wrapper. Errors-only
// posture with light tracing (GlitchTip doesn't support replay/profiling/logs, so they stay off).
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

/**
 * Defense-in-depth PII scrubbing on outgoing events: drop request headers/cookies entirely and
 * redact sensitive query params from the request URL, even though sendDefaultPii is already false.
 * Exported (with scrubBreadcrumb) so the redaction is unit-tested — the rest of init is wiring.
 */
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

/**
 * Initialise Sentry from the build-time DSN. No DSN ⇒ no-op (dev/local stays clean, no network).
 * Must be called before `app.use(router)` so the browser-tracing/router integration is registered.
 */
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
    // PII off: don't attach IP/headers/cookies. Defense-in-depth scrubbing via the helpers above.
    sendDefaultPii: false,
    beforeSend: scrubEvent,
    beforeBreadcrumb: scrubBreadcrumb,
  })
}
