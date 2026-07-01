import { describe, it, expect, vi, beforeEach } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'
import { useApiKeysStore } from '../apiKeys'
import type { ApiKeyItem } from '@/api/apiKeys'
import { ApiError } from '@/api/client'

const mockList = vi.fn()
const mockRevoke = vi.fn()

vi.mock('@/api/apiKeys', () => ({
  apiKeys: {
    list: () => mockList(),
    revoke: (id: string) => mockRevoke(id),
  },
}))

function makeItem(overrides: Partial<ApiKeyItem> = {}): ApiKeyItem {
  return {
    id: overrides.id ?? crypto.randomUUID(),
    name: overrides.name ?? 'k',
    keyPrefix: overrides.keyPrefix ?? 'gtv_abcd1234',
    createdAt: overrides.createdAt ?? new Date().toISOString(),
    lastUsedAt: overrides.lastUsedAt ?? null,
    expiresAt: overrides.expiresAt ?? null,
    revokedAt: overrides.revokedAt ?? null,
  }
}

beforeEach(() => {
  setActivePinia(createPinia())
  mockList.mockReset()
  mockRevoke.mockReset()
})

describe('useApiKeysStore', () => {
  it('load() populates items and clears loading', async () => {
    const store = useApiKeysStore()
    mockList.mockResolvedValue([makeItem({ name: 'one' })])

    await store.load()

    expect(store.items).toHaveLength(1)
    expect(store.items[0].name).toBe('one')
    expect(store.loading).toBe(false)
    expect(store.error).toBeNull()
  })

  it('load() maps a 401 to a session-expired message', async () => {
    const store = useApiKeysStore()
    mockList.mockRejectedValue(new ApiError(401, null))

    await store.load()

    expect(store.error).toBe('Session expired. Sign in again.')
    expect(store.loading).toBe(false)
  })

  it('load() maps unknown errors to a generic message', async () => {
    const store = useApiKeysStore()
    mockList.mockRejectedValue(new Error('boom'))

    await store.load()

    expect(store.error).toBe('Something went wrong. Try again.')
  })

  it('revoke() reloads on success', async () => {
    const store = useApiKeysStore()
    mockRevoke.mockResolvedValue(undefined)
    mockList.mockResolvedValue([])

    const ok = await store.revoke('id-1')

    expect(ok).toBe(true)
    expect(mockRevoke).toHaveBeenCalledWith('id-1')
    expect(mockList).toHaveBeenCalledTimes(1)
  })

  it('revoke() surfaces an error and returns false', async () => {
    const store = useApiKeysStore()
    mockRevoke.mockRejectedValue(new ApiError(500, null))

    const ok = await store.revoke('id-1')

    expect(ok).toBe(false)
    expect(store.error).toBe('Something went wrong. Try again.')
    expect(mockList).not.toHaveBeenCalled()
  })

  it('reset() clears state', async () => {
    const store = useApiKeysStore()
    mockList.mockResolvedValue([makeItem()])
    await store.load()

    store.reset()
    expect(store.items).toEqual([])
    expect(store.error).toBeNull()
  })
})
