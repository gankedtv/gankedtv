// Per-deployment web config, resolved at call time so ONE built bundle works in any environment.
// Precedence per key: runtime `window.__APP_CONFIG__` (written by the production image's entrypoint
// from container env) → build-time `import.meta.env.VITE_*` (used by `bun dev` / the dev build) → a
// hardcoded default. This is what lets the published web image stay generic: operators set values as
// runtime container env instead of baking them in. Keep the key set in sync with
// web/scripts/fetch-vault-env.ts, web/env.d.ts, and the entrypoint (web/docker-entrypoint.sh).

export interface AppRuntimeConfig {
  VITE_API_BASE_URL?: string
  VITE_GA_MEASUREMENT_ID?: string
  VITE_USE_SECURE_COOKIES?: string
  VITE_MAX_UPLOAD_SIZE_MB?: string
  VITE_SENTRY_DSN?: string
  VITE_SENTRY_ENVIRONMENT?: string
  VITE_SENTRY_RELEASE?: string
  VITE_SENTRY_TRACES_SAMPLE_RATE?: string
}

declare global {
  interface Window {
    __APP_CONFIG__?: AppRuntimeConfig
  }
}

// Blanks and unsubstituted "${VAR}" placeholders (an env var the entrypoint left empty) count as
// "not provided", so resolution falls through to the next source.
function clean(value: string | undefined): string | undefined {
  const trimmed = value?.trim()
  return trimmed && !trimmed.includes('${') ? trimmed : undefined
}

function fromRuntime(key: keyof AppRuntimeConfig): string | undefined {
  return typeof window !== 'undefined' ? clean(window.__APP_CONFIG__?.[key]) : undefined
}

// Literal `import.meta.env.VITE_*` access so Vite statically inlines each value, wrapped in thunks
// so every lookup happens at call time (keeps dev/test, which mutate import.meta.env, accurate).
const fromBuild: Record<keyof AppRuntimeConfig, () => string | undefined> = {
  VITE_API_BASE_URL: () => clean(import.meta.env.VITE_API_BASE_URL),
  VITE_GA_MEASUREMENT_ID: () => clean(import.meta.env.VITE_GA_MEASUREMENT_ID),
  VITE_USE_SECURE_COOKIES: () => clean(import.meta.env.VITE_USE_SECURE_COOKIES),
  VITE_MAX_UPLOAD_SIZE_MB: () => clean(import.meta.env.VITE_MAX_UPLOAD_SIZE_MB),
  VITE_SENTRY_DSN: () => clean(import.meta.env.VITE_SENTRY_DSN),
  VITE_SENTRY_ENVIRONMENT: () => clean(import.meta.env.VITE_SENTRY_ENVIRONMENT),
  VITE_SENTRY_RELEASE: () => clean(import.meta.env.VITE_SENTRY_RELEASE),
  VITE_SENTRY_TRACES_SAMPLE_RATE: () => clean(import.meta.env.VITE_SENTRY_TRACES_SAMPLE_RATE),
}

function resolve(key: keyof AppRuntimeConfig): string | undefined {
  return fromRuntime(key) ?? fromBuild[key]()
}

export const config = {
  get apiBaseUrl(): string {
    return resolve('VITE_API_BASE_URL') ?? 'http://localhost:5050'
  },
  get gaMeasurementId(): string | undefined {
    return resolve('VITE_GA_MEASUREMENT_ID')
  },
  get useSecureCookies(): boolean {
    return resolve('VITE_USE_SECURE_COOKIES') === 'true'
  },
  get maxUploadSizeMb(): string | undefined {
    return resolve('VITE_MAX_UPLOAD_SIZE_MB')
  },
  get sentryDsn(): string | undefined {
    return resolve('VITE_SENTRY_DSN')
  },
  get sentryEnvironment(): string | undefined {
    return resolve('VITE_SENTRY_ENVIRONMENT')
  },
  get sentryRelease(): string | undefined {
    return resolve('VITE_SENTRY_RELEASE')
  },
  get sentryTracesSampleRate(): string | undefined {
    return resolve('VITE_SENTRY_TRACES_SAMPLE_RATE')
  },
}
