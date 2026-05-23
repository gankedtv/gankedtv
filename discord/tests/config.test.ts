import { describe, expect, test } from 'bun:test';
import { loadConfig } from '../src/config.ts';

const baseEnv: NodeJS.ProcessEnv = {
  DISCORD_DATABASE_URL: 'postgres://x@localhost:5432/x',
  GANKEDTV_API_BASE: 'http://api.test',
  GANKEDTV_PUBLIC_BASE: 'http://web.test',
};

describe('loadConfig', () => {
  test('disabled when token/app id missing', () => {
    const cfg = loadConfig({ ...baseEnv });
    expect(cfg.enabled).toBe(false);
  });

  test('disabled when only the token is set', () => {
    const cfg = loadConfig({ ...baseEnv, DISCORD_BOT_TOKEN: 't' });
    expect(cfg.enabled).toBe(false);
  });

  test('enabled when token AND app id are present', () => {
    const cfg = loadConfig({ ...baseEnv, DISCORD_BOT_TOKEN: 't', DISCORD_BOT_APP_ID: 'a' });
    expect(cfg.enabled).toBe(true);
  });

  test('disabled when token is explicitly empty (shell `VAR=` syntax)', () => {
    // Reproduces `env DISCORD_BOT_TOKEN= bun run src/index.ts` — shells encode
    // "unset" as an empty string. The schema must accept this and the enabled
    // check must treat it as off, otherwise the disabled-boot contract from
    // .env.example (which ships empty placeholders) throws ZodError at startup.
    const cfg = loadConfig({ ...baseEnv, DISCORD_BOT_TOKEN: '', DISCORD_BOT_APP_ID: '' });
    expect(cfg.enabled).toBe(false);
  });

  test('disabled when only one of the credentials is empty', () => {
    const cfg = loadConfig({ ...baseEnv, DISCORD_BOT_TOKEN: 't', DISCORD_BOT_APP_ID: '' });
    expect(cfg.enabled).toBe(false);
  });

  test('poll interval defaults to 30s', () => {
    const cfg = loadConfig({ ...baseEnv });
    expect(cfg.DISCORD_POLL_INTERVAL_SECONDS).toBe(30);
  });

  test('poll interval reads numeric env override', () => {
    const cfg = loadConfig({ ...baseEnv, DISCORD_POLL_INTERVAL_SECONDS: '120' });
    expect(cfg.DISCORD_POLL_INTERVAL_SECONDS).toBe(120);
  });

  test('DISCORD_DATABASE_URL is required', () => {
    expect(() => loadConfig({})).toThrow();
  });
});
