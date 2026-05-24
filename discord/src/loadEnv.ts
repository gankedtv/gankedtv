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

// Side effect at import time. Tests of the helpers above pass an explicit
// fake path/target and never depend on this default invocation.
loadRootEnv();
