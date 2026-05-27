import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { useLatestRequest } from '../useLatestRequest'

describe('useLatestRequest', () => {
  beforeEach(() => {
    // The composable's error path logs to console.error; silence the noise for the cases
    // that exercise it so the test runner output stays clean.
    vi.spyOn(console, 'error').mockImplementation(() => {})
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('exposes data, loading, errored, run with sensible defaults', () => {
    const { data, loading, errored } = useLatestRequest(() => Promise.resolve('x'))
    expect(data.value).toBeUndefined()
    expect(loading.value).toBe(false)
    expect(errored.value).toBe(false)
  })

  it('uses the initial value before run() completes', async () => {
    const { data } = useLatestRequest(() => Promise.resolve('next'), { initial: 'seed' })
    expect(data.value).toBe('seed')
  })

  it('sets data and clears loading on success', async () => {
    const { data, loading, errored, run } = useLatestRequest(() => Promise.resolve(42))
    await run()
    expect(data.value).toBe(42)
    expect(loading.value).toBe(false)
    expect(errored.value).toBe(false)
  })

  it('sets errored=true and leaves data alone on failure', async () => {
    const { data, loading, errored, run } = useLatestRequest(
      () => Promise.reject(new Error('boom')),
      { initial: 'kept' },
    )
    await run()
    expect(errored.value).toBe(true)
    expect(loading.value).toBe(false)
    expect(data.value).toBe('kept')
  })

  it('discards an older response when a newer call has already resolved', async () => {
    // The point of the composable: out-of-order resolutions must not stomp a newer result.
    // Use deferred promises so we can control resolve order independently of call order.
    let resolveFirst!: (v: string) => void
    let resolveSecond!: (v: string) => void
    const first = new Promise<string>((r) => (resolveFirst = r))
    const second = new Promise<string>((r) => (resolveSecond = r))
    const calls: Array<Promise<string>> = [first, second]
    let i = 0

    const { data, run } = useLatestRequest(() => calls[i++])

    const p1 = run()
    const p2 = run()
    resolveSecond('new')
    await p2
    expect(data.value).toBe('new')
    resolveFirst('old')
    await p1
    // Stale resolution must NOT overwrite the newer winner.
    expect(data.value).toBe('new')
  })

  it('discards an older rejection so errored does not flip after a newer success', async () => {
    let rejectFirst!: (e: unknown) => void
    let resolveSecond!: (v: string) => void
    const first = new Promise<string>((_, r) => (rejectFirst = r))
    const second = new Promise<string>((r) => (resolveSecond = r))
    const calls: Array<Promise<string>> = [first, second]
    let i = 0

    const { data, errored, run } = useLatestRequest(() => calls[i++])

    const p1 = run()
    const p2 = run()
    resolveSecond('ok')
    await p2
    rejectFirst(new Error('stale'))
    await p1
    expect(data.value).toBe('ok')
    expect(errored.value).toBe(false)
  })

  it('flips loading=true while a fetch is in flight', async () => {
    let resolve!: (v: string) => void
    const pending = new Promise<string>((r) => (resolve = r))
    const { loading, run } = useLatestRequest(() => pending)
    const p = run()
    expect(loading.value).toBe(true)
    resolve('done')
    await p
    expect(loading.value).toBe(false)
  })

  it('uses the provided label as the console.error prefix', async () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {})
    const { run } = useLatestRequest(() => Promise.reject(new Error('x')), { label: 'trending' })
    await run()
    expect(spy).toHaveBeenCalled()
    expect(String(spy.mock.calls[0][0])).toContain('trending')
  })
})
