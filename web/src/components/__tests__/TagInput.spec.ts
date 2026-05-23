import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import TagInput from '../TagInput.vue'

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  })
}

function mountInput(initial: string[] = []) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => jsonResponse([])),
  )
  return mount(TagInput, { props: { modelValue: initial, 'onUpdate:modelValue': () => {} } })
}

async function setDraft(wrapper: ReturnType<typeof mountInput>, value: string) {
  const input = wrapper.find('input')
  await input.setValue(value)
}

describe('TagInput', () => {
  it('commits a draft when Enter is pressed', async () => {
    const wrapper = mountInput()
    await setDraft(wrapper, 'clutch')
    await wrapper.find('input').trigger('keydown', { key: 'Enter' })
    const events = wrapper.emitted('update:modelValue')!
    expect(events[events.length - 1][0]).toEqual(['clutch'])
  })

  it('commits on comma and clears the draft', async () => {
    const wrapper = mountInput()
    await setDraft(wrapper, 'clutch')
    await wrapper.find('input').trigger('keydown', { key: ',' })
    const events = wrapper.emitted('update:modelValue')!
    expect(events[events.length - 1][0]).toEqual(['clutch'])
    expect((wrapper.find('input').element as HTMLInputElement).value).toBe('')
  })

  it('commits on space when the draft is non-empty', async () => {
    const wrapper = mountInput()
    await setDraft(wrapper, 'clutch')
    await wrapper.find('input').trigger('keydown', { key: ' ' })
    const events = wrapper.emitted('update:modelValue')!
    expect(events[events.length - 1][0]).toEqual(['clutch'])
  })

  it('normalizes casing and whitespace before emitting', async () => {
    const wrapper = mountInput()
    await setDraft(wrapper, 'Clutch Play')
    await wrapper.find('input').trigger('keydown', { key: 'Enter' })
    const events = wrapper.emitted('update:modelValue')!
    expect(events[events.length - 1][0]).toEqual(['clutch-play'])
  })

  it('rejects too-short input (no emit)', async () => {
    const wrapper = mountInput()
    await setDraft(wrapper, 'a')
    await wrapper.find('input').trigger('keydown', { key: 'Enter' })
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
  })

  it('rejects fully-invalid input (no emit)', async () => {
    const wrapper = mountInput()
    await setDraft(wrapper, '!!!')
    await wrapper.find('input').trigger('keydown', { key: 'Enter' })
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
  })

  it('does not re-emit when the slug is already present', async () => {
    const wrapper = mountInput(['clutch'])
    await setDraft(wrapper, 'Clutch')
    await wrapper.find('input').trigger('keydown', { key: 'Enter' })
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
  })

  it('disables the input when at max tags', () => {
    const wrapper = mountInput(['a1', 'a2', 'a3', 'a4', 'a5'])
    expect((wrapper.find('input').element as HTMLInputElement).disabled).toBe(true)
  })

  it('removes the last chip on Backspace when draft is empty', async () => {
    const wrapper = mountInput(['clutch', 'ace'])
    await wrapper.find('input').trigger('keydown', { key: 'Backspace' })
    const events = wrapper.emitted('update:modelValue')!
    expect(events[events.length - 1][0]).toEqual(['clutch'])
  })

  it('does not repopulate the dropdown after the draft is cleared mid-fetch', async () => {
    // Regression: lastQuery must be invalidated when the user clears the input
    // while an autocomplete is in flight, otherwise the stale response can pass
    // the `lastQuery !== queryAtCall` guard and reopen the dropdown.
    let resolveFetch: (resp: Response) => void = () => {}
    const fetchPromise = new Promise<Response>((resolve) => {
      resolveFetch = resolve
    })
    const fetchStub = vi.fn(() => fetchPromise)
    vi.stubGlobal('fetch', fetchStub)

    const wrapper = mount(TagInput, {
      props: { modelValue: [], 'onUpdate:modelValue': () => {} },
    })
    const input = wrapper.find('input')
    await input.setValue('clu')
    // Let the debounce timer fire so the fetch starts (but stays unresolved).
    await vi.advanceTimersByTimeAsync(200)
    expect(fetchStub).toHaveBeenCalledTimes(1)

    // User clears the input before the response arrives.
    await input.setValue('')
    await nextTick()

    // Stale response arrives now — must be discarded.
    resolveFetch(
      new Response(JSON.stringify([{ id: 1, slug: 'clutch', name: 'clutch', clipCount: 7 }]), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    )
    await fetchPromise
    await nextTick()

    expect(wrapper.find('ul[role="listbox"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('clutch')
  })

  it('debounces the autocomplete fetch', async () => {
    const fetchStub = vi.fn(async () => jsonResponse([]))
    vi.stubGlobal('fetch', fetchStub)
    const wrapper = mount(TagInput, {
      props: { modelValue: [], 'onUpdate:modelValue': () => {} },
    })
    const input = wrapper.find('input')
    await input.setValue('c')
    await input.setValue('cl')
    await input.setValue('clu')

    // No fetch yet — still inside the debounce window.
    expect(fetchStub).not.toHaveBeenCalled()

    await vi.advanceTimersByTimeAsync(200)
    await nextTick()
    expect(fetchStub).toHaveBeenCalledTimes(1)
    const calls = fetchStub.mock.calls as unknown as Array<[string]>
    expect(calls[0][0]).toContain('prefix=clu')
  })
})
