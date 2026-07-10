// The API's thumbnailUrl is a presigned URL with a bounded lifetime, and Discord's
// media proxy re-fetches embed images from origin later — once the signature expires
// the image breaks for everyone who scrolls back (in dev the host is localhost and
// Discord can never fetch it at all). Downloading the bytes while the URL is fresh
// and uploading them as a message attachment pins the image to Discord's CDN forever.

export const THUMBNAIL_FILENAME = 'clip.jpg';

const FETCH_TIMEOUT_MS = 10_000;
// Discord's upload cap for non-boosted guilds; a poster frame should never get close.
const MAX_BYTES = 8 * 1024 * 1024;

export type ThumbnailFetcher = (url: string | null) => Promise<Buffer | null>;

// Best-effort by design: any failure (expired URL, timeout, oversized body) returns
// null and the embed falls back to the raw URL — same behavior as before this helper.
// fetchImpl is injectable for tests, mirroring createApi's convention.
export const fetchThumbnail: ThumbnailFetcher = (url) => downloadThumbnail(url);

export async function downloadThumbnail(
  url: string | null,
  fetchImpl: typeof fetch = fetch,
): Promise<Buffer | null> {
  if (!url) return null;
  try {
    const resp = await fetchImpl(url, { signal: AbortSignal.timeout(FETCH_TIMEOUT_MS) });
    if (!resp.ok) return null;
    const bytes = Buffer.from(await resp.arrayBuffer());
    if (bytes.byteLength === 0 || bytes.byteLength > MAX_BYTES) return null;
    return bytes;
  } catch {
    return null;
  }
}
