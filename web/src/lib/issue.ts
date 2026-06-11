// Newsprint issue numbers. Derived client-side until a real DB column exists —
// deterministic so the same clip always files under the same number.

const ISSUE_RANGE = 999

function djb2(str: string): number {
  let hash = 5381
  for (let i = 0; i < str.length; i++) {
    hash = (hash * 33) ^ str.charCodeAt(i)
  }
  return hash >>> 0
}

/** "042" — zero-padded 3-digit issue number derived from a clip id. */
export function issueNumber(clipId: string | number): string {
  const numeric = typeof clipId === 'number' ? clipId : Number(clipId)
  const n = Number.isSafeInteger(numeric) && numeric >= 0 ? numeric : djb2(String(clipId))
  return String((n % ISSUE_RANGE) + 1).padStart(3, '0')
}

/** "No. 042" — display form used on thumbnails and hero blocks. */
export function formatIssueNo(clipId: string | number): string {
  return `No. ${issueNumber(clipId)}`
}

// The site itself is an issue too: VOL counts years since launch, ISS is the
// UTC day-of-year. UTC keeps the strip identical across users and tests.
const LAUNCH_YEAR = 2026

/** "VOL 1 · ISS 162 · 06.11.26" — nav meta strip + footer colophon line. */
export function volIssMeta(date: Date = new Date()): string {
  const vol = Math.max(1, date.getUTCFullYear() - LAUNCH_YEAR + 1)
  const startOfYear = Date.UTC(date.getUTCFullYear(), 0, 0)
  const dayOfYear = Math.floor((date.getTime() - startOfYear) / 86_400_000)
  const iss = String(dayOfYear).padStart(3, '0')
  const mm = String(date.getUTCMonth() + 1).padStart(2, '0')
  const dd = String(date.getUTCDate()).padStart(2, '0')
  const yy = String(date.getUTCFullYear() % 100).padStart(2, '0')
  return `VOL ${vol} · ISS ${iss} · ${mm}.${dd}.${yy}`
}
