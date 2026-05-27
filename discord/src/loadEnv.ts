// Merges the repo-root `.env` into process.env so the bot picks up the same
// shared values the API and web app use (DATABASE_URL, etc.) without forcing
// contributors to duplicate them in discord/.env. Mirrors web/vite.config.ts's
// `envDir: '../'` convention.
//
// Precedence (first-set wins; same as the standard 12-factor order):
//   1. shell env (already in process.env before this runs)
//   2. discord/.env (auto-loaded by Bun before the script starts)
//   3. repo-root .env (loaded here)
//
// Pure helpers (parseEnvFile, mergeFirstWins) are exported for tests; the
// bottom-of-file invocation is the side effect that makes import './loadEnv.ts'
// "just work" from src/index.ts.

import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fetchSecrets, resolveCollection } from '../../shared/vaultwarden/client.ts';

export function parseEnvFile(contents: string): Record<string, string> {
  const out: Record<string, string> = {};
  for (const rawLine of contents.split('\n')) {
    const line = rawLine.trim();
    if (!line || line.startsWith('#')) continue;
    const eq = line.indexOf('=');
    if (eq <= 0) continue;
    const key = line.slice(0, eq).trim();
    let value = line.slice(eq + 1).trim();
    // Strip surrounding single or double quotes — matches dotenv/Bun behavior.
    if (
      (value.startsWith('"') && value.endsWith('"')) ||
      (value.startsWith("'") && value.endsWith("'"))
    ) {
      value = value.slice(1, -1);
    }
    out[key] = value;
  }
  return out;
}

export function mergeFirstWins(
  target: Record<string, string | undefined>,
  source: Record<string, string>,
): void {
  for (const [k, v] of Object.entries(source)) {
    if (!(k in target)) target[k] = v;
  }
}

const ROOT_ENV_PATH = resolve(import.meta.dir, '..', '..', '.env');

export function loadRootEnv(
  envPath: string = ROOT_ENV_PATH,
  target: Record<string, string | undefined> = process.env,
): void {
  if (!existsSync(envPath)) return;
  mergeFirstWins(target, parseEnvFile(readFileSync(envPath, 'utf8')));
}

// The bot's required secrets, fetched from Vaultwarden by these exact names. No SENTRY_DSN — no
// Sentry integration yet.
export const vaultwardenManifest = [
  'DISCORD_BOT_TOKEN',
  'DISCORD_BOT_APP_ID',
  'DISCORD_DATABASE_URL',
] as const;

// Fetches the bot's secrets from Vaultwarden and layers them into `target` via mergeFirstWins, so a
// shell/.env value still beats the vault. No-op when the bootstrap vars are unset. Production fails
// fast on a missing/errored secret; dev falls back to .env.
export async function loadVaultwardenSecrets(
  target: Record<string, string | undefined> = process.env,
  opts: { manifest?: readonly string[]; fetchImpl?: typeof fetch; timeoutMs?: number } = {},
): Promise<void> {
  const apiUrl = target.VAULTWARDEN_API_URL;
  const apiKey = target.VAULTWARDEN_API_KEY;
  if (!apiUrl?.trim() || !apiKey?.trim()) return; // opt-in: no bootstrap vars → no-op

  const environment = target.ASPNETCORE_ENVIRONMENT ?? target.NODE_ENV;
  const failFast = environment?.toLowerCase() === 'production';
  const fetched = await fetchSecrets({
    apiUrl,
    apiKey,
    collection: resolveCollection(target),
    manifest: opts.manifest ?? vaultwardenManifest,
    organization: target.VAULTWARDEN_ORG?.trim() || undefined,
    // Production fails fast on anything missing or errored; dev falls back to .env on both.
    throwIfMissing: failFast,
    throwOnError: failFast,
    alreadySet: (key) => Boolean(target[key]?.trim()),
    fetchImpl: opts.fetchImpl,
    timeoutMs: opts.timeoutMs,
  });
  mergeFirstWins(target, fetched);
}

// Side effect at import time. Tests of the helpers above pass an explicit
// fake path/target and never depend on this default invocation.
loadRootEnv();
