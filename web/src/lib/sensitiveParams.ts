// Single source of truth for query params that can carry credentials/secrets and must never
// leave the browser to a third party (analytics page views, Sentry event/breadcrumb URLs).
// The OAuth callback lands on /auth/callback?token=<JWT>&refresh=<token>, so a naive
// `trackPageView(to.fullPath)` or a captured error URL would otherwise leak a 30-day refresh token.

const SENSITIVE_QUERY_KEYS = new Set([
  'token',
  'refresh',
  'code',
  'state',
  'access_token',
  'id_token',
])

/**
 * Removes sensitive query params from a path or absolute URL, preserving everything before the
 * query string. Origin-agnostic: works on router fullPaths (`/a?token=x`) and full URLs alike.
 */
export function stripSensitiveParams(fullPath: string): string {
  const [path, query = ''] = fullPath.split('?')
  if (!query) return path
  // Rebuild from only the non-sensitive entries (rather than deleting in place, which would
  // mutate the live iterator) — preserves original order and is straightforward to read.
  const kept = new URLSearchParams()
  for (const [key, value] of new URLSearchParams(query)) {
    if (!SENSITIVE_QUERY_KEYS.has(key)) kept.append(key, value)
  }
  const qs = kept.toString()
  return qs ? `${path}?${qs}` : path
}
