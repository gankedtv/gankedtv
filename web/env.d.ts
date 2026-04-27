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
  export interface PlyrOptions {
    controls?: string[]
    tooltips?: { controls?: boolean; seek?: boolean }
  }
  export default class Plyr {
    constructor(
      target: HTMLElement | string | NodeList | HTMLElement[],
      options?: PlyrOptions,
    )
    destroy(): void
  }
}
