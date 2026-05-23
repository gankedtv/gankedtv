/// <reference types="vite/client" />

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
