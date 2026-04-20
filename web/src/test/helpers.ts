// Test-only helpers shared across api / stores / router specs. Keep this tiny — the existing
// specs already show preferred patterns inline. Helpers here cover the parts that would
// otherwise get copy-pasted across multiple files (localStorage mock, env stubs).

export interface MockLocalStorage {
  getItem: (key: string) => string | null
  setItem: (key: string, value: string) => void
  removeItem: (key: string) => void
  clear: () => void
  __throwMode: boolean
}

/// Returns a fresh in-memory localStorage stub. Set `__throwMode = true` to force every
/// method to throw — exercises the try/catch paths around localStorage calls that guard
/// against Safari Private Mode / SecurityError.
export function createLocalStorageMock(): MockLocalStorage {
  let store: Record<string, string> = {}
  const mock: MockLocalStorage = {
    __throwMode: false,
    getItem(key) {
      if (mock.__throwMode) throw new Error('localStorage denied')
      return store[key] ?? null
    },
    setItem(key, value) {
      if (mock.__throwMode) throw new Error('localStorage denied')
      store[key] = value
    },
    removeItem(key) {
      if (mock.__throwMode) throw new Error('localStorage denied')
      delete store[key]
    },
    clear() {
      if (mock.__throwMode) throw new Error('localStorage denied')
      store = {}
    },
  }
  return mock
}

export function installLocalStorage(mock: MockLocalStorage): void {
  Object.defineProperty(window, 'localStorage', { value: mock, configurable: true })
}
