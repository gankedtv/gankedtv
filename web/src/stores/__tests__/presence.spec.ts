import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'
import { usePresenceStore } from '../presence'

const summaryMock = vi.fn()

vi.mock('@/api/presence', () => ({
  presence: {
    summary: (...args: unknown[]) => summaryMock(...args),
  },
}))

beforeEach(() => {
  setActivePinia(createPinia())
  vi.useFakeTimers()
  summaryMock.mockReset()
})

afterEach(() => {
  vi.useRealTimers()
})

describe('stores/presence', () => {
  it('startPolling fetches immediately and then every 45s', async () => {
    summaryMock.mockResolvedValue({ online: 7, followsOnline: [], followsOnlineCount: 0 })
    const store = usePresenceStore()

    store.startPolling()
    await vi.advanceTimersByTimeAsync(0)

    expect(summaryMock).toHaveBeenCalledTimes(1)
    expect(store.online).toBe(7)

    await vi.advanceTimersByTimeAsync(45_000)
    expect(summaryMock).toHaveBeenCalledTimes(2)
  })

  it('startPolling is idempotent', async () => {
    summaryMock.mockResolvedValue({ online: 1, followsOnline: [], followsOnlineCount: 0 })
    const store = usePresenceStore()

    store.startPolling()
    store.startPolling()
    await vi.advanceTimersByTimeAsync(0)

    expect(summaryMock).toHaveBeenCalledTimes(1)
  })

  it('stopPolling clears the interval', async () => {
    summaryMock.mockResolvedValue({ online: 1, followsOnline: [], followsOnlineCount: 0 })
    const store = usePresenceStore()

    store.startPolling()
    await vi.advanceTimersByTimeAsync(0)
    store.stopPolling()
    await vi.advanceTimersByTimeAsync(90_000)

    expect(summaryMock).toHaveBeenCalledTimes(1)
    expect(store.pollTimer).toBeNull()
  })

  it('a failed poll resets to unknown so the UI renders nothing', async () => {
    const follows = [{ id: 'u1', username: 'gankster', avatarUrl: null }]
    summaryMock.mockResolvedValueOnce({ online: 5, followsOnline: follows, followsOnlineCount: 9 })
    const store = usePresenceStore()

    store.startPolling()
    await vi.advanceTimersByTimeAsync(0)
    expect(store.online).toBe(5)
    expect(store.followsOnline).toEqual(follows)
    expect(store.followsOnlineCount).toBe(9)

    summaryMock.mockRejectedValueOnce(new Error('503'))
    await vi.advanceTimersByTimeAsync(45_000)

    expect(store.online).toBeNull()
    expect(store.followsOnline).toEqual([])
    expect(store.followsOnlineCount).toBe(0)
  })
})
