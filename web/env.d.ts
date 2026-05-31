/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Base URL of the API. Injected at build time; falls back to localhost:5050 in dev. */
  readonly VITE_API_BASE_URL?: string
  /** GA4 measurement id (e.g. "G-XXXXXXX"). Present only in production builds; gates analytics. */
  readonly VITE_GA_MEASUREMENT_ID?: string
  /** When "true", skip localStorage token persistence (HttpOnly-cookie strategy). */
  readonly VITE_USE_SECURE_COOKIES?: string
  /** Max upload size in MB shown in the upload UI. */
  readonly VITE_MAX_UPLOAD_SIZE_MB?: string
  /** Sentry/GlitchTip DSN. Present only in production builds; gates error monitoring. */
  readonly VITE_SENTRY_DSN?: string
  /** Sentry environment tag. Falls back to import.meta.env.MODE when unset. */
  readonly VITE_SENTRY_ENVIRONMENT?: string
  /** Sentry release tag. Falls back to the app version (__APP_VERSION__) when unset. */
  readonly VITE_SENTRY_RELEASE?: string
  /** Sentry traces sample rate (0–1). Falls back to 0.01 when unset/invalid. */
  readonly VITE_SENTRY_TRACES_SAMPLE_RATE?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}

/** App version injected at build time from package.json (see vite.config.ts). */
declare const __APP_VERSION__: string

declare module '*.vue' {
  import type { DefineComponent } from 'vue'
  // eslint-disable-next-line @typescript-eslint/no-empty-object-type, @typescript-eslint/no-explicit-any
  const component: DefineComponent<{}, {}, any>
  export default component
}

// Plyr ships a .d.ts that mixes `export =` and `export default`, which trips
// `verbatimModuleSyntax`. Re-declare the minimal surface we actually use as a
// clean ESM default class so `import Plyr from 'plyr'` type-checks both as a
// value (constructor) and as a type.
//
// If you need additional Plyr methods (e.g. `play()`, `source = ...`,
// `on('ended', ...)`), extend this shim — don't reach into `plyr/src/js/plyr`.
declare module 'plyr' {
  // Quality menu wiring for adaptive HLS (issue #102). `default`/`options` use 0 as the
  // "Auto" sentinel; `onChange` maps the chosen height back onto the hls.js level.
  export interface PlyrQualityOptions {
    default: number
    options: number[]
    forced?: boolean
    onChange?: (quality: number) => void
  }
  export interface PlyrOptions {
    controls?: string[]
    tooltips?: { controls?: boolean; seek?: boolean }
    quality?: PlyrQualityOptions
    // i18n is a free-form label bag in Plyr; we only set qualityLabel.
    i18n?: { qualityLabel?: Record<number, string> }
  }
  export default class Plyr {
    constructor(
      target: HTMLElement | string | NodeList | HTMLElement[],
      options?: PlyrOptions,
    )
    destroy(): void
  }
}
