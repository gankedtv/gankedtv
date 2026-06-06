// Browser error monitoring → self-hosted GlitchTip. Opt-in: no VITE_SENTRY_DSN ⇒ no-op (dev/local).
import type { App } from 'vue'
import type { Router } from 'vue-router'
import type { Breadcrumb, ErrorEvent } from '@sentry/vue'
import * as Sentry from '@sentry/vue'

import { config } from '../config'
import { stripSensitiveParams } from './sensitiveParams'

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
  const dsn = config.sentryDsn
  if (!dsn) return

  const parsedSampleRate = Number.parseFloat(config.sentryTracesSampleRate ?? '')
  // Reject NaN/Infinity and out-of-range values — Sentry expects a probability in [0,1].
  const tracesSampleRate =
    Number.isFinite(parsedSampleRate) && parsedSampleRate >= 0 && parsedSampleRate <= 1
      ? parsedSampleRate
      : 0.01

  Sentry.init({
    app,
    dsn,
    environment: config.sentryEnvironment || import.meta.env.MODE,
    release: config.sentryRelease || __APP_VERSION__,
    integrations: [Sentry.browserTracingIntegration({ router })],
    tracesSampleRate,
    tracePropagationTargets: [config.apiBaseUrl],
    // PII off; the helpers above scrub defensively.
    sendDefaultPii: false,
    beforeSend: scrubEvent,
    beforeBreadcrumb: scrubBreadcrumb,
  })
}
