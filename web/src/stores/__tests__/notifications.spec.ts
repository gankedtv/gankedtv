import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'
import { useNotificationsStore } from '../notifications'
import type { NotificationItem } from '@/api/notifications'

const mockList = vi.fn()
const mockUnreadCount = vi.fn()
const mockMarkAllRead = vi.fn()
const mockMarkOneRead = vi.fn()

vi.mock('@/api/notifications', () => ({
  notifications: {
    list: (...args: unknown[]) => mockList(...args),
    unreadCount: () => mockUnreadCount(),
    markAllRead: () => mockMarkAllRead(),
    markOneRead: (id: string) => mockMarkOneRead(id),
  },
}))

function makeItem(overrides: Partial<NotificationItem> = {}): NotificationItem {
  return {
    id: overrides.id ?? crypto.randomUUID(),
    type: overrides.type ?? 'follow',
    actor: overrides.actor ?? { id: crypto.randomUUID(), username: 'actor', avatarUrl: null },
    clip: overrides.clip ?? null,
    comment: overrides.comment ?? null,
    createdAt: overrides.createdAt ?? new Date().toISOString(),
    readAt: overrides.readAt ?? null,
  }
}

beforeEach(() => {
  setActivePinia(createPinia())
  vi.useFakeTimers()
  mockList.mockReset()
  mockUnreadCount.mockReset()
  mockMarkAllRead.mockReset()
  mockMarkOneRead.mockReset()
})

afterEach(() => {
  vi.useRealTimers()
})

describe('useNotificationsStore', () => {
  it('startPolling fires unreadCount immediately and every 30s', async () => {
    mockUnreadCount.mockResolvedValue({ count: 3 })
    const store = useNotificationsStore()

    store.startPolling()
    // Drain the immediate microtask so the initial unreadCount lands.
    await vi.advanceTimersByTimeAsync(0)
    expect(mockUnreadCount).toHaveBeenCalledTimes(1)
    expect(store.unreadCount).toBe(3)

    mockUnreadCount.mockResolvedValue({ count: 5 })
    await vi.advanceTimersByTimeAsync(30_000)
    expect(mockUnreadCount).toHaveBeenCalledTimes(2)
    expect(store.unreadCount).toBe(5)
  })

  it('startPolling is idempotent — repeat calls do not double the interval', async () => {
    mockUnreadCount.mockResolvedValue({ count: 0 })
    const store = useNotificationsStore()

    store.startPolling()
    store.startPolling()
    store.startPolling()
    await vi.advanceTimersByTimeAsync(0)
    // One immediate fire — not three.
    expect(mockUnreadCount).toHaveBeenCalledTimes(1)

    await vi.advanceTimersByTimeAsync(30_000)
    // One tick more — not three.
    expect(mockUnreadCount).toHaveBeenCalledTimes(2)
  })

  it('stopPolling halts subsequent ticks (logout scenario)', async () => {
    mockUnreadCount.mockResolvedValue({ count: 1 })
    const store = useNotificationsStore()
    store.startPolling()
    await vi.advanceTimersByTimeAsync(0)
    expect(mockUnreadCount).toHaveBeenCalledTimes(1)

    store.stopPolling()
    await vi.advanceTimersByTimeAsync(60_000)
    // Polling stopped — no further requests.
    expect(mockUnreadCount).toHaveBeenCalledTimes(1)
  })

  it('reset clears state and stops polling', async () => {
    mockUnreadCount.mockResolvedValue({ count: 7 })
    const store = useNotificationsStore()
    store.startPolling()
    await vi.advanceTimersByTimeAsync(0)
    store.items = [makeItem()]
    store.cursor = 'opaque'

    store.reset()

    expect(store.unreadCount).toBe(0)
    expect(store.items).toHaveLength(0)
    expect(store.cursor).toBeNull()
    await vi.advanceTimersByTimeAsync(60_000)
    expect(mockUnreadCount).toHaveBeenCalledTimes(1)
  })

  it('loadFirstPage replaces items and stores cursor', async () => {
    const item = makeItem()
    mockList.mockResolvedValue({ items: [item], nextCursor: 'next' })
    const store = useNotificationsStore()

    await store.loadFirstPage(10)

    expect(mockList).toHaveBeenCalledWith({ limit: 10 })
    expect(store.items).toEqual([item])
    expect(store.cursor).toBe('next')
    expect(store.hasMore).toBe(true)
  })

  it('loadMore appends and is a no-op when no cursor', async () => {
    const store = useNotificationsStore()
    store.cursor = null

    await store.loadMore()
    expect(mockList).not.toHaveBeenCalled()

    const item = makeItem()
    mockList.mockResolvedValue({ items: [item], nextCursor: null })
    store.cursor = 'opaque'
    await store.loadMore(20)

    expect(mockList).toHaveBeenCalledWith({ cursor: 'opaque', limit: 20 })
    expect(store.items).toEqual([item])
    expect(store.cursor).toBeNull()
  })

  it('markAllRead optimistically zeroes the badge and stamps unread rows', async () => {
    const unread = makeItem({ readAt: null })
    const alreadyRead = makeItem({ readAt: '2026-01-01T00:00:00Z' })
    const store = useNotificationsStore()
    store.items = [unread, alreadyRead]
    store.unreadCount = 1
    mockMarkAllRead.mockResolvedValue({ marked: 1 })

    await store.markAllRead()

    expect(store.unreadCount).toBe(0)
    expect(store.items[0].readAt).not.toBeNull()
    expect(store.items[1].readAt).toBe('2026-01-01T00:00:00Z')
    expect(mockMarkAllRead).toHaveBeenCalled()
  })

  it('markOneRead decrements unreadCount only for previously-unread rows', async () => {
    const unread = makeItem({ readAt: null })
    const store = useNotificationsStore()
    store.items = [unread]
    store.unreadCount = 2
    mockMarkOneRead.mockResolvedValue(undefined)

    await store.markOneRead(unread.id)
    expect(store.unreadCount).toBe(1)
    expect(store.items[0].readAt).not.toBeNull()

    // Calling again on the same (now-read) row must NOT decrement again.
    await store.markOneRead(unread.id)
    expect(store.unreadCount).toBe(1)
  })

  it('refreshUnreadCount swallows transient failures', async () => {
    mockUnreadCount.mockRejectedValueOnce(new Error('boom'))
    const store = useNotificationsStore()
    store.unreadCount = 4

    // Must not throw — polling tick failures are silent.
    await store.refreshUnreadCount()
    expect(store.unreadCount).toBe(4)
  })

  it('loadFirstPage marks errored on failure and clears loading', async () => {
    mockList.mockRejectedValue(new Error('5xx'))
    const store = useNotificationsStore()

    await store.loadFirstPage()
    expect(store.errored).toBe(true)
    expect(store.loading).toBe(false)
  })
})
