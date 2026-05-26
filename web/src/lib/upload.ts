// Browser-side PUT to a presigned S3/MinIO URL. The Content-Type header MUST exactly match
// the value the server signed for — S3 includes it in the signature and 403s on mismatch.
//
// Uses XHR rather than fetch so callers can opt into upload-progress events; fetch's
// ReadableStream upload progress is still gated behind flags on Chromium and unsupported
// on Safari at time of writing. Keep this small: it's the shared bare metal for the clip
// uploader and the profile media uploader.

export interface PutOptions {
  onProgress?: (pct: number) => void
  signal?: AbortSignal
  timeoutMs?: number
}

const DEFAULT_TIMEOUT_MS = 60 * 1000

export function putToPresignedUrl(
  url: string,
  body: Blob,
  contentType: string,
  opts: PutOptions = {},
): Promise<void> {
  const { onProgress, signal, timeoutMs = DEFAULT_TIMEOUT_MS } = opts
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new DOMException('Aborted', 'AbortError'))
      return
    }
    const xhr = new XMLHttpRequest()
    xhr.open('PUT', url)
    xhr.timeout = timeoutMs
    xhr.setRequestHeader('Content-Type', contentType)
    if (onProgress) {
      xhr.upload.onprogress = (ev) => {
        if (ev.lengthComputable) onProgress((ev.loaded / ev.total) * 100)
      }
    }
    const onAbort = () => xhr.abort()
    signal?.addEventListener('abort', onAbort)
    const cleanup = () => signal?.removeEventListener('abort', onAbort)
    xhr.onload = () => {
      cleanup()
      if (xhr.status >= 200 && xhr.status < 300) resolve()
      else reject(new Error(`PUT failed: ${xhr.status}`))
    }
    xhr.onerror = () => {
      cleanup()
      reject(new Error('PUT network error'))
    }
    xhr.onabort = () => {
      cleanup()
      reject(new DOMException('Aborted', 'AbortError'))
    }
    xhr.ontimeout = () => {
      cleanup()
      reject(new Error('Upload timed out — check your connection and try again'))
    }
    xhr.send(body)
  })
}
