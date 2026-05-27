#!/usr/bin/env bun
// Prebuild: pull the web build's VITE_* values from Vaultwarden into the repo-root
// .env.production.local (the highest-precedence prod env file Vite reads via envDir: '../'). No-op
// when the bootstrap vars are unset, so local builds use the committed .env. VITE_* values are
// baked into the public bundle — not secret; Vaultwarden is just the single source of truth.

import { writeFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { fetchSecrets, resolveCollection } from '../../shared/vaultwarden/client'

// VITE_* build-time vars — all baked into the public bundle.
export const viteManifest = [
  'VITE_API_BASE_URL',
  'VITE_GA_MEASUREMENT_ID',
  'VITE_USE_SECURE_COOKIES',
  'VITE_MAX_UPLOAD_SIZE_MB',
] as const

// Render a key/value map to dotenv lines, quoting values that contain whitespace, '#', or quotes.
export function renderEnvFile(values: Record<string, string>): string {
  return (
    Object.entries(values)
      .map(([k, v]) => `${k}=${/[\s#"']/.test(v) ? JSON.stringify(v) : v}`)
      .join('\n') + '\n'
  )
}

// Fetch the VITE_* manifest. No-op (returns {}) when the bootstrap vars are unset. A missing key
// (404) is skipped — the GA id is optional — but an auth/transport error throws so a misconfigured
// prod build fails loudly.
export async function fetchViteEnv(
  env: Record<string, string | undefined> = process.env,
  fetchImpl: typeof fetch = fetch,
  manifest: readonly string[] = viteManifest,
): Promise<Record<string, string>> {
  const apiUrl = env.VAULTWARDEN_API_URL
  const apiKey = env.VAULTWARDEN_API_KEY
  if (!apiUrl?.trim() || !apiKey?.trim()) return {}
  return fetchSecrets({
    apiUrl,
    apiKey,
    collection: resolveCollection(env),
    manifest,
    organization: env.VAULTWARDEN_ORG?.trim() || undefined,
    throwIfMissing: false,
    throwOnError: true,
    alreadySet: (key) => Boolean(env[key]?.trim()),
    fetchImpl,
  })
}

// Run directly (not when imported by tests): write only when there's something to write.
if (import.meta.main) {
  const values = await fetchViteEnv()
  if (Object.keys(values).length > 0) {
    const target = resolve(import.meta.dirname, '..', '..', '.env.production.local')
    writeFileSync(target, renderEnvFile(values), 'utf8')
    console.log(`Wrote ${Object.keys(values).length} VITE_* var(s) to ${target}`)
  } else {
    console.log(
      'Vaultwarden not configured (or nothing to write) — leaving the committed .env as-is.',
    )
  }
}
