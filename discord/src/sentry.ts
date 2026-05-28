import * as Sentry from '@sentry/bun';

import type { Config } from './config.ts';
import pkg from '../package.json';

// Minimal surface so tests can inject a fake and assert init args without a live SDK.
type SentrySdk = Pick<typeof Sentry, 'init'>;

/** Git short SHA of the checkout, or '' when unavailable (e.g. prod container without .git). */
function gitShortSha(): string {
  try {
    const out = Bun.spawnSync(['git', 'rev-parse', '--short', 'HEAD']);
    return out.success ? out.stdout.toString().trim() : '';
  } catch {
    return '';
  }
}

/**
 * Opt-in error monitoring → GlitchTip (no-op without DISCORD_SENTRY_DSN). Init also installs global
 * unhandledRejection / uncaughtException handlers, so crashes in this long-running process report.
 */
export function initSentry(config: Config, sdk: SentrySdk = Sentry): void {
  if (!config.sentryEnabled) return;

  const rate = Number.parseFloat(config.DISCORD_SENTRY_TRACES_SAMPLE_RATE ?? '');
  sdk.init({
    dsn: config.DISCORD_SENTRY_DSN,
    environment: config.DISCORD_SENTRY_ENVIRONMENT?.trim() || process.env.NODE_ENV || 'development',
    release: config.DISCORD_SENTRY_RELEASE?.trim() || gitShortSha() || pkg.version,
    tracesSampleRate: Number.isFinite(rate) ? rate : 0.01,
    sendDefaultPii: false,
  });
}
