import { describe, expect, test } from 'bun:test';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  loadRootEnv,
  loadVaultwardenSecrets,
  mergeFirstWins,
  optionalVaultwardenManifest,
  parseEnvFile,
  vaultwardenManifest,
} from '../src/loadEnv.ts';

describe('parseEnvFile', () => {
  test('parses KEY=value pairs', () => {
    expect(parseEnvFile('FOO=bar\nBAZ=qux')).toEqual({ FOO: 'bar', BAZ: 'qux' });
  });

  test('skips blank lines and # comments', () => {
    const text = `# a comment\n\nFOO=1\n  # indented comment\nBAR=2`;
    expect(parseEnvFile(text)).toEqual({ FOO: '1', BAR: '2' });
  });

  test('strips surrounding double quotes', () => {
    expect(parseEnvFile('FOO="hello world"')).toEqual({ FOO: 'hello world' });
  });

  test('strips surrounding single quotes', () => {
    expect(parseEnvFile("FOO='hello'")).toEqual({ FOO: 'hello' });
  });

  test('trims whitespace around key and value', () => {
    expect(parseEnvFile('  FOO  =  bar  ')).toEqual({ FOO: 'bar' });
  });

  test('skips lines with no equals sign', () => {
    expect(parseEnvFile('FOO=bar\nbroken-line\nBAZ=qux')).toEqual({ FOO: 'bar', BAZ: 'qux' });
  });

  test('skips lines starting with =', () => {
    expect(parseEnvFile('=nope\nFOO=bar')).toEqual({ FOO: 'bar' });
  });

  test('preserves = inside values (only splits on first)', () => {
    expect(parseEnvFile('CONN=Host=localhost;Port=5432')).toEqual({
      CONN: 'Host=localhost;Port=5432',
    });
  });
});

describe('mergeFirstWins', () => {
  test('sets keys that are absent from target', () => {
    const target: Record<string, string | undefined> = { A: '1' };
    mergeFirstWins(target, { B: '2' });
    expect(target).toEqual({ A: '1', B: '2' });
  });

  test('does NOT overwrite existing keys (first-set wins)', () => {
    const target: Record<string, string | undefined> = { A: 'shell' };
    mergeFirstWins(target, { A: 'file' });
    expect(target.A).toBe('shell');
  });

  test('explicit undefined counts as present (fallback does NOT override)', () => {
    const target: Record<string, string | undefined> = { A: undefined };
    mergeFirstWins(target, { A: 'fallback' });
    // `'A' in target` is true even though A === undefined, so the first-set-wins
    // check considers A "already set" and skips the fallback. Mirrors Node's
    // process.env: setting `process.env.X = undefined` is distinct from never
    // setting X at all (the former keeps the slot, the latter leaves it free
    // for a downstream merge to fill).
    expect(target.A).toBeUndefined();
  });
});

describe('loadRootEnv', () => {
  test('no-ops when the file does not exist', () => {
    const target: Record<string, string | undefined> = { FOO: 'kept' };
    loadRootEnv('/tmp/this-file-does-not-exist-' + Math.random(), target);
    expect(target).toEqual({ FOO: 'kept' });
  });

  test('merges a real file with first-set-wins semantics', () => {
    const dir = mkdtempSync(join(tmpdir(), 'gktv-env-'));
    const path = join(dir, '.env');
    try {
      writeFileSync(path, 'NEW_KEY=fromfile\nSHELL_KEY=shouldbeignored\n');
      const target: Record<string, string | undefined> = { SHELL_KEY: 'shell' };
      loadRootEnv(path, target);
      expect(target.NEW_KEY).toBe('fromfile');
      expect(target.SHELL_KEY).toBe('shell');
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});

describe('loadVaultwardenSecrets', () => {
  const throwingFetch = (async () => {
    throw new Error('fetch should not have been called');
  }) as unknown as typeof fetch;

  test('no-ops when the bootstrap vars are unset', async () => {
    const target: Record<string, string | undefined> = { DISCORD_BOT_TOKEN: undefined };
    await loadVaultwardenSecrets(target, { fetchImpl: throwingFetch });
    expect(target).toEqual({ DISCORD_BOT_TOKEN: undefined });
  });

  test('fills unset keys but never overwrites an already-set one (env wins)', async () => {
    const target: Record<string, string | undefined> = {
      VAULTWARDEN_API_URL: 'https://vault.test',
      VAULTWARDEN_API_KEY: 'k',
      DISCORD_BOT_TOKEN: 'from-shell',
    };
    const fetchImpl = (async (input: string | URL | Request) => {
      const key = decodeURIComponent(String(input).split('/secret/')[1]!.split('?')[0]!);
      return new Response(JSON.stringify({ name: key, value: `vault-${key}` }), { status: 200 });
    }) as typeof fetch;

    await loadVaultwardenSecrets(target, {
      fetchImpl,
      manifest: ['DISCORD_BOT_TOKEN', 'DISCORD_BOT_APP_ID'],
    });

    expect(target.DISCORD_BOT_TOKEN).toBe('from-shell'); // already set → not overwritten
    expect(target.DISCORD_BOT_APP_ID).toBe('vault-DISCORD_BOT_APP_ID'); // filled from vault
  });

  test('fills a blank placeholder from the vault (blank counts as unset, matching the server)', async () => {
    const target: Record<string, string | undefined> = {
      VAULTWARDEN_API_URL: 'https://vault.test',
      VAULTWARDEN_API_KEY: 'k',
      DISCORD_BOT_TOKEN: '', // blank placeholder → should be filled from the vault
      DISCORD_BOT_APP_ID: 'set-app', // genuinely set → preserved
    };
    const fetchImpl = (async (input: string | URL | Request) => {
      const key = decodeURIComponent(String(input).split('/secret/')[1]!.split('?')[0]!);
      return new Response(JSON.stringify({ name: key, value: `vault-${key}` }), { status: 200 });
    }) as typeof fetch;

    await loadVaultwardenSecrets(target, {
      fetchImpl,
      manifest: ['DISCORD_BOT_TOKEN', 'DISCORD_BOT_APP_ID'],
    });

    expect(target.DISCORD_BOT_TOKEN).toBe('vault-DISCORD_BOT_TOKEN'); // blank filled
    expect(target.DISCORD_BOT_APP_ID).toBe('set-app'); // non-empty preserved
  });

  test('production fails fast when a required secret is missing', async () => {
    const target: Record<string, string | undefined> = {
      VAULTWARDEN_API_URL: 'https://vault.test',
      VAULTWARDEN_API_KEY: 'k',
      ASPNETCORE_ENVIRONMENT: 'Production',
    };
    const fetchImpl = (async () =>
      new Response('{"error":"secret not found"}', { status: 404 })) as unknown as typeof fetch;

    await expect(
      loadVaultwardenSecrets(target, { fetchImpl, manifest: ['DISCORD_BOT_TOKEN'] }),
    ).rejects.toThrow(/not found/);
  });

  test('optional tier is best-effort in production: missing key does not throw, present is filled', async () => {
    const target: Record<string, string | undefined> = {
      VAULTWARDEN_API_URL: 'https://vault.test',
      VAULTWARDEN_API_KEY: 'k',
      ASPNETCORE_ENVIRONMENT: 'Production', // would fail-fast on the required tier
    };
    const fetchImpl = (async (input: string | URL | Request) => {
      const key = decodeURIComponent(String(input).split('/secret/')[1]!.split('?')[0]!);
      return key === 'DISCORD_SENTRY_DSN'
        ? new Response(JSON.stringify({ name: key, value: 'https://k@glitchtip/2' }), {
            status: 200,
          })
        : new Response('{"error":"secret not found"}', { status: 404 });
    }) as typeof fetch;

    await loadVaultwardenSecrets(target, {
      fetchImpl,
      manifest: optionalVaultwardenManifest,
      optional: true,
    });

    expect(target.DISCORD_SENTRY_DSN).toBe('https://k@glitchtip/2'); // present → filled
    expect(target.GANKEDTV_PUBLIC_BASE).toBeUndefined(); // missing → skipped, no throw
  });
});

describe('manifests', () => {
  test('Sentry is in the optional tier, not the required one, and the two are disjoint', () => {
    expect(optionalVaultwardenManifest).toContain('DISCORD_SENTRY_DSN');
    expect(vaultwardenManifest as readonly string[]).not.toContain('DISCORD_SENTRY_DSN');
    const required = new Set<string>(vaultwardenManifest);
    expect(optionalVaultwardenManifest.some((k) => required.has(k))).toBe(false);
  });
});
