import { ref, type Ref } from 'vue'

// Wraps an async fetcher with the monotonic-token stale-check our list views need: rapid
// argument changes (e.g. clicking a time-window tab three times in 200ms) trigger overlapping
// fetches, and we want the latest one to win regardless of resolve order. v1 has no
// AbortController cancellation — none of the current call sites cancelled before, and adding
// it would change semantics (in-flight requests would reject rather than silently discard).
export interface UseLatestRequestOptions<T> {
  /** Seed value for `data` before the first run completes. */
  initial?: T
  /**
   * Prefix for console.error when the fetcher throws — keeps logs attributable to the
   * specific view (e.g. `"trending"`) instead of a generic helper name.
   */
  label?: string
}

export interface UseLatestRequestResult<T> {
  data: Ref<T | undefined>
  loading: Ref<boolean>
  errored: Ref<boolean>
  /** Trigger a new fetch. Concurrent calls are tolerated; only the last-started wins. */
  run: () => Promise<void>
}

export function useLatestRequest<T>(
  fetcher: () => Promise<T>,
  options: UseLatestRequestOptions<T> = {},
): UseLatestRequestResult<T> {
  const data = ref<T | undefined>(options.initial) as Ref<T | undefined>
  const loading = ref(false)
  const errored = ref(false)
  let latestLoadId = 0

  async function run() {
    const id = ++latestLoadId
    loading.value = true
    errored.value = false
    try {
      const result = await fetcher()
      if (id !== latestLoadId) return
      data.value = result
    } catch (err) {
      if (id !== latestLoadId) return
      console.error(`${options.label ?? 'useLatestRequest'}: load failed`, err)
      errored.value = true
    } finally {
      if (id === latestLoadId) loading.value = false
    }
  }

  return { data, loading, errored, run }
}
