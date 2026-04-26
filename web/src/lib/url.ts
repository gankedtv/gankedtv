/**
 * Returns `url` if it's a safe `<img src>` candidate, else `null`.
 *
 * Server validates avatar URLs on `PATCH /me` today, but the frontend should
 * never bind a `javascript:` or unknown-scheme URL into an `<img>` regardless
 * of where it came from — defence in depth against a future server bug or a
 * compromised OAuth provider response.
 */
export function safeImageUrl(url: string | null | undefined): string | null {
  if (!url) return null
  try {
    const parsed = new URL(url, window.location.origin)
    if (parsed.protocol === 'https:' || parsed.protocol === 'http:') return url
    // Allow only image data URLs — `data:text/html,...` is harmless inside <img> but
    // would be dangerous if this helper ever gets reused for <iframe src> etc.
    if (parsed.protocol === 'data:' && /^data:image\//i.test(url)) return url
    return null
  } catch {
    return null
  }
}
