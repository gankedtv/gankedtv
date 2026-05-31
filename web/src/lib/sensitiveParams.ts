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
 * Removes sensitive query params from a path or absolute URL, preserving the path and any fragment.
 * Origin-agnostic: works on router fullPaths (`/a?token=x`) and full URLs alike.
 */
export function stripSensitiveParams(fullPath: string): string {
  // Peel off an optional fragment first so it survives untouched — otherwise `?a=1#frag` parses
  // `1#frag` as a single query value and corrupts (percent-encodes) the fragment.
  const hashIndex = fullPath.indexOf('#')
  const fragment = hashIndex === -1 ? '' : fullPath.slice(hashIndex)
  const beforeHash = hashIndex === -1 ? fullPath : fullPath.slice(0, hashIndex)

  const queryIndex = beforeHash.indexOf('?')
  if (queryIndex === -1) return beforeHash + fragment

  const path = beforeHash.slice(0, queryIndex)
  const query = beforeHash.slice(queryIndex + 1)
  // Rebuild from only the non-sensitive entries (rather than deleting in place, which would
  // mutate the live iterator) — preserves original order and is straightforward to read.
  const kept = new URLSearchParams()
  for (const [key, value] of new URLSearchParams(query)) {
    // Case-insensitive match keeps parity with the server scrubber (OrdinalIgnoreCase),
    // so `?Token=` / `?CODE=` redact the same as `?token=` / `?code=`.
    if (!SENSITIVE_QUERY_KEYS.has(key.toLowerCase())) kept.append(key, value)
  }
  const qs = kept.toString()
  return (qs ? `${path}?${qs}` : path) + fragment
}
