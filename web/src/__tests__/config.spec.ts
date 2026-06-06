import { afterEach, describe, expect, it, vi } from 'vitest'
import { config } from '@/config'

afterEach(() => {
  vi.unstubAllEnvs()
  delete window.__APP_CONFIG__
})

describe('config resolution precedence', () => {
  it('prefers runtime window.__APP_CONFIG__ over build-time import.meta.env', () => {
    vi.stubEnv('VITE_API_BASE_URL', 'https://build.example')
    window.__APP_CONFIG__ = { VITE_API_BASE_URL: 'https://runtime.example' }
    expect(config.apiBaseUrl).toBe('https://runtime.example')
  })

  it('falls back to import.meta.env when runtime is unset', () => {
    vi.stubEnv('VITE_API_BASE_URL', 'https://build.example')
    expect(config.apiBaseUrl).toBe('https://build.example')
  })

  it('falls back to the hardcoded default when both are unset', () => {
    vi.stubEnv('VITE_API_BASE_URL', '')
    expect(config.apiBaseUrl).toBe('http://localhost:5050')
  })

  it('treats blanks and unsubstituted "${VAR}" placeholders as not provided', () => {
    vi.stubEnv('VITE_API_BASE_URL', 'https://build.example')
    window.__APP_CONFIG__ = { VITE_API_BASE_URL: '${VITE_API_BASE_URL}' }
    // the placeholder is ignored, so it falls through to the build value
    expect(config.apiBaseUrl).toBe('https://build.example')

    window.__APP_CONFIG__ = { VITE_API_BASE_URL: '   ' }
    expect(config.apiBaseUrl).toBe('https://build.example')
  })

  it('parses useSecureCookies as a strict "true" boolean', () => {
    window.__APP_CONFIG__ = { VITE_USE_SECURE_COOKIES: 'true' }
    expect(config.useSecureCookies).toBe(true)
    window.__APP_CONFIG__ = { VITE_USE_SECURE_COOKIES: 'false' }
    expect(config.useSecureCookies).toBe(false)
    delete window.__APP_CONFIG__
    expect(config.useSecureCookies).toBe(false)
  })

  it('returns undefined for unset optional values', () => {
    expect(config.gaMeasurementId).toBeUndefined()
    expect(config.sentryDsn).toBeUndefined()
  })
})
