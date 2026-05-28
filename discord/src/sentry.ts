import * as Sentry from '@sentry/bun';

import type { Config } from './config.ts';
import pkg from '../package.json';

// Minimal surface so tests can inject a fake and assert init args without a live SDK.
type SentrySdk = Pick<typeof Sentry, 'init'>;

/**
 * Error/crash monitoring → self-hosted GlitchTip. Opt-in: no SENTRY_DSN ⇒ no-op (dev/local stays
 * clean), mirroring the bot's "disabled; exiting" contract. Initialising the SDK also installs
 * global `unhandledRejection` / `uncaughtException` handlers, so the long-running poller and its
 * crashes report automatically. Errors-only posture with light tracing — GlitchTip doesn't support
 * replay/profiling/logs, so they stay off.
 */
export function initSentry(config: Config, sdk: SentrySdk = Sentry): void {
  if (!config.sentryEnabled) return;

  const rate = Number.parseFloat(config.SENTRY_TRACES_SAMPLE_RATE ?? '');
  sdk.init({
    dsn: config.SENTRY_DSN,
    environment: config.SENTRY_ENVIRONMENT?.trim() || process.env.NODE_ENV || 'development',
    release: config.SENTRY_RELEASE?.trim() || pkg.version,
    tracesSampleRate: Number.isFinite(rate) ? rate : 0.1,
    sendDefaultPii: false,
  });
}
