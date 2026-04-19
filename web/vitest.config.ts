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
        provider: 'v8',
        include: ['src/api/**', 'src/router/**', 'src/stores/**'],
        exclude: ['**/__tests__/**', '**/*.spec.ts', '**/*.d.ts'],
        reporter: ['text', 'text-summary', 'json-summary'],
        thresholds: {
          lines: 60,
          branches: 57,
        },
      },
    },
  }),
)
