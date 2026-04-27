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
    // Allow image data URLs but reject SVG explicitly — SVG can carry inline
    // <script> that executes in <object>/<iframe> contexts (and historically in
    // some <img> contexts via foreignObject). `data:text/html,...` would also
    // be dangerous outside <img>; rejecting both keeps this helper safe to
    // reuse anywhere a URL is bound to a media-loading element.
    if (parsed.protocol === 'data:') {
      const isImage = /^data:image\//i.test(url)
      const isSvg = /^data:image\/svg\b/i.test(url)
      if (isImage && !isSvg) return url
    }
    return null
  } catch {
    return null
  }
}
