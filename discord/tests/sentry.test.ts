import { describe, expect, mock, test } from 'bun:test';
import { loadConfig } from '../src/config.ts';
import { initSentry } from '../src/sentry.ts';

const baseEnv: NodeJS.ProcessEnv = {
  DISCORD_DATABASE_URL: 'postgres://x@localhost:5432/x',
  GANKEDTV_API_BASE: 'http://api.test',
  GANKEDTV_PUBLIC_BASE: 'http://web.test',
};

describe('initSentry', () => {
  test('no-op when DISCORD_SENTRY_DSN is unset', () => {
    const init = mock((_options?: unknown) => undefined);
    initSentry(loadConfig({ ...baseEnv }), { init });
    expect(init).not.toHaveBeenCalled();
  });

  test('no-op when DISCORD_SENTRY_DSN is an empty placeholder', () => {
    const init = mock((_options?: unknown) => undefined);
    initSentry(loadConfig({ ...baseEnv, DISCORD_SENTRY_DSN: '' }), { init });
    expect(init).not.toHaveBeenCalled();
  });

  test('ignores the API SENTRY_DSN from the shared root .env', () => {
    // The bot loads the repo-root .env, which carries the API's SENTRY_DSN. Only the
    // DISCORD_-prefixed var should switch the bot on, so the two projects never cross-report.
    const init = mock((_options?: unknown) => undefined);
    initSentry(loadConfig({ ...baseEnv, SENTRY_DSN: 'https://api@glitchtip.test/1' }), { init });
    expect(init).not.toHaveBeenCalled();
  });

  test('initialises with dsn, environment, release and PII off when DSN is set', () => {
    const init = mock((_options?: unknown) => undefined);
    const config = loadConfig({
      ...baseEnv,
      DISCORD_SENTRY_DSN: 'https://abc@glitchtip.test/1',
      DISCORD_SENTRY_ENVIRONMENT: 'production',
      DISCORD_SENTRY_RELEASE: 'gankedtv-discord@1.2.3',
      DISCORD_SENTRY_TRACES_SAMPLE_RATE: '0.25',
    });

    initSentry(config, { init });

    expect(init).toHaveBeenCalledTimes(1);
    expect(init.mock.calls[0]?.[0]).toMatchObject({
      dsn: 'https://abc@glitchtip.test/1',
      environment: 'production',
      release: 'gankedtv-discord@1.2.3',
      tracesSampleRate: 0.25,
      sendDefaultPii: false,
    });
  });

  test('falls back to default sample rate when unset/invalid', () => {
    const init = mock((_options?: unknown) => undefined);
    initSentry(loadConfig({ ...baseEnv, DISCORD_SENTRY_DSN: 'https://abc@glitchtip.test/1' }), {
      init,
    });
    expect(init.mock.calls[0]?.[0]).toMatchObject({ tracesSampleRate: 0.1 });
  });
});
