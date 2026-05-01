import { fileURLToPath } from 'node:url'
import type { UserConfig } from 'vite'
import { mergeConfig, defineConfig, configDefaults } from 'vitest/config'
import viteConfig from './vite.config'

export default mergeConfig(
  viteConfig as UserConfig,
  defineConfig({
    test: {
      environment: 'jsdom',
      exclude: [...configDefaults.exclude, 'e2e/**'],
      root: fileURLToPath(new URL('./', import.meta.url)),
      passWithNoTests: true,
      coverage: {
        // Istanbul (source instrumentation) instead of v8 (precise-coverage inspector
        // API). v8 needs `node:inspector`'s `Profiler.startPreciseCoverage`, which
        // bun's polyfill doesn't implement on 1.3.13+ — local `make ci-web` runs
        // crash with "Coverage APIs are not supported" before any tests execute.
        // Istanbul rewrites the source and works under any runtime; CI is unaffected.
        provider: 'istanbul',
        include: ['src/api/**', 'src/router/**', 'src/stores/**'],
        exclude: ['**/__tests__/**', '**/*.spec.ts', '**/*.test.ts', '**/*.d.ts'],
        reporter: ['text', 'text-summary', 'json-summary'],
        thresholds: {
          lines: 85,
          branches: 85,
        },
      },
    },
  }),
)
