<script setup lang="ts">
// Chip-style tag input. v-model:tags is an array of normalized slug strings.
//
// Commit: comma, space, or Enter commit the current draft. Backspace at an
// empty draft pops the last chip (matches every chat/email composer).
//
// Normalization mirrors TagNormalization on the server: lowercase, replace
// whitespace/underscores with '-', strip anything outside [a-z0-9-], collapse
// repeated hyphens, length in [2, 24]. The server re-normalizes too — this is
// purely for instant feedback.
//
// Autocomplete: typing fires a debounced GET /tags?prefix=… with the *raw*
// (non-normalized) draft, and the dropdown ranks results by clipCount. Picking
// a result commits the canonical slug. ArrowUp/Down navigate, Enter selects
// the highlighted row (or commits the draft if none is highlighted).
import { ref, computed, onUnmounted, nextTick, useId, watch } from 'vue'
import { tags as tagsApi, type TagSummary } from '@/api/tags'

const props = defineProps<{
  modelValue: string[]
  max?: number
  inputClass?: string
}>()

const emit = defineEmits<{ 'update:modelValue': [value: string[]] }>()

const MAX_TAGS_DEFAULT = 5
const MIN_LEN = 2
const MAX_LEN = 24

const max = computed(() => props.max ?? MAX_TAGS_DEFAULT)
const isFull = computed(() => props.modelValue.length >= max.value)

const draft = ref('')
const results = ref<TagSummary[]>([])
const showDropdown = ref(false)
const highlightedIndex = ref(-1)
const inputEl = ref<HTMLInputElement | null>(null)

// IDs for the listbox + active option wiring. useId() guarantees uniqueness across
// multiple TagInputs on the same page (e.g. upload form + a future filter sidebar).
const listboxId = useId()
const optionId = (i: number) => `${listboxId}-opt-${i}`
const activeDescendantId = computed(() =>
  showDropdown.value && highlightedIndex.value >= 0 ? optionId(highlightedIndex.value) : undefined,
)

let searchTimer: ReturnType<typeof setTimeout> | null = null
let blurTimer: ReturnType<typeof setTimeout> | null = null
let lastQuery = ''

// Same algorithm as the server's TagNormalization.TryNormalize. Returns null for
// inputs that don't yield a valid slug.
function normalize(raw: string): string | null {
  if (!raw) return null
  const out: string[] = []
  let lastWasHyphen = false
  for (const ch of raw) {
    const code = ch.charCodeAt(0)
    const isLower = code >= 97 && code <= 122
    const isUpper = code >= 65 && code <= 90
    const isDigit = code >= 48 && code <= 57
    if (isLower || isDigit) {
      out.push(ch)
      lastWasHyphen = false
    } else if (isUpper) {
      out.push(String.fromCharCode(code + 32))
      lastWasHyphen = false
    } else if (ch === '-' || ch === '_' || /\s/.test(ch)) {
      if (out.length === 0 || lastWasHyphen) continue
      out.push('-')
      lastWasHyphen = true
    }
  }
  if (out.length > 0 && out[out.length - 1] === '-') out.pop()
  const slug = out.join('')
  if (slug.length < MIN_LEN || slug.length > MAX_LEN) return null
  return slug
}

function commit(rawOrSlug: string) {
  const slug = normalize(rawOrSlug)
  if (!slug) return false
  if (props.modelValue.includes(slug)) {
    draft.value = ''
    return false
  }
  if (isFull.value) return false
  emit('update:modelValue', [...props.modelValue, slug])
  draft.value = ''
  results.value = []
  showDropdown.value = false
  highlightedIndex.value = -1
  return true
}

function removeAt(index: number) {
  const next = props.modelValue.slice()
  next.splice(index, 1)
  emit('update:modelValue', next)
}

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'ArrowDown' && results.value.length > 0) {
    e.preventDefault()
    showDropdown.value = true
    highlightedIndex.value = Math.min(highlightedIndex.value + 1, results.value.length - 1)
    return
  }
  if (e.key === 'ArrowUp' && results.value.length > 0) {
    e.preventDefault()
    highlightedIndex.value = Math.max(highlightedIndex.value - 1, -1)
    return
  }
  if (e.key === 'Enter') {
    e.preventDefault()
    if (highlightedIndex.value >= 0 && results.value[highlightedIndex.value]) {
      commit(results.value[highlightedIndex.value].slug)
    } else if (draft.value) {
      commit(draft.value)
    }
    return
  }
  if (e.key === ',' || e.key === ' ') {
    if (draft.value.trim()) {
      e.preventDefault()
      commit(draft.value)
    }
    return
  }
  if (e.key === 'Backspace' && draft.value === '' && props.modelValue.length > 0) {
    removeAt(props.modelValue.length - 1)
    return
  }
  if (e.key === 'Escape') {
    showDropdown.value = false
    highlightedIndex.value = -1
  }
}

watch(draft, (q) => {
  if (searchTimer) clearTimeout(searchTimer)
  const trimmed = q.trim()
  if (!trimmed) {
    // Invalidate lastQuery so any in-flight autocomplete response that started
    // before this clear can't pass the `lastQuery !== queryAtCall` guard below
    // and re-open the dropdown after the user has emptied the draft.
    lastQuery = ''
    results.value = []
    showDropdown.value = false
    highlightedIndex.value = -1
    return
  }
  searchTimer = setTimeout(async () => {
    const queryAtCall = trimmed
    lastQuery = queryAtCall
    try {
      const rows = await tagsApi.autocomplete(queryAtCall, 8)
      if (lastQuery !== queryAtCall) return
      // Hide already-chosen tags so the user can't pick them twice from the dropdown.
      results.value = rows.filter((r) => !props.modelValue.includes(r.slug))
      showDropdown.value = results.value.length > 0
      highlightedIndex.value = -1
    } catch {
      if (lastQuery !== queryAtCall) return
      results.value = []
      showDropdown.value = false
    }
  }, 150)
})

function onInputBlur() {
  // Delay so a mousedown on a dropdown row registers before we hide it.
  if (blurTimer) clearTimeout(blurTimer)
  blurTimer = setTimeout(() => {
    showDropdown.value = false
    blurTimer = null
  }, 150)
}

function focusInput() {
  nextTick(() => inputEl.value?.focus())
}

onUnmounted(() => {
  if (searchTimer) clearTimeout(searchTimer)
  if (blurTimer) clearTimeout(blurTimer)
})

const resolvedInputClass = computed(
  () =>
    props.inputClass ??
    'w-full rounded-md border border-border bg-surface-high px-3.5 py-3 text-sm text-text-primary outline-none placeholder:text-text-muted transition-colors duration-150 focus:border-accent',
)
</script>

<template>
  <div>
    <div class="flex flex-wrap items-center gap-2">
      <span
        v-for="(slug, i) in props.modelValue"
        :key="slug"
        class="inline-flex items-center gap-1.5 rounded-full border border-accent-border bg-accent-bg px-2.5 py-0.5 text-[11px] font-semibold text-accent"
      >
        #{{ slug }}
        <button
          type="button"
          :aria-label="`Remove tag ${slug}`"
          class="cursor-pointer leading-none text-accent/70 transition-colors duration-150 hover:text-text-primary"
          @click="
            () => {
              removeAt(i)
              focusInput()
            }
          "
        >
          ×
        </button>
      </span>
      <div class="relative min-w-40 flex-1">
        <input
          ref="inputEl"
          v-model="draft"
          type="text"
          autocomplete="off"
          spellcheck="false"
          :maxlength="MAX_LEN"
          :disabled="isFull"
          :placeholder="
            isFull
              ? `Max ${max} tags`
              : props.modelValue.length === 0
                ? 'add a tag…'
                : 'add another…'
          "
          :class="resolvedInputClass"
          role="combobox"
          :aria-expanded="showDropdown && results.length > 0"
          :aria-controls="listboxId"
          :aria-activedescendant="activeDescendantId"
          aria-autocomplete="list"
          @keydown="onKeydown"
          @focus="showDropdown = results.length > 0"
          @blur="onInputBlur"
        />
        <ul
          v-if="showDropdown && results.length"
          :id="listboxId"
          role="listbox"
          class="absolute left-0 right-0 top-full z-10 mt-1 max-h-60 overflow-auto rounded-lg border border-border-strong bg-surface-base"
        >
          <li
            v-for="(r, i) in results"
            :id="optionId(i)"
            :key="r.id"
            role="option"
            :aria-selected="i === highlightedIndex"
            class="flex cursor-pointer items-center justify-between gap-3 px-3 py-2 text-xs text-text-primary transition-colors duration-150"
            :class="i === highlightedIndex ? 'bg-accent-bg text-accent' : 'hover:bg-surface-high'"
            @mousedown.prevent="commit(r.slug)"
          >
            <span>#{{ r.name }}</span>
            <span class="text-[10px] text-text-muted">{{ r.clipCount }}</span>
          </li>
        </ul>
      </div>
    </div>
    <p
      v-if="props.modelValue.length > 0 || draft.length > 0"
      class="mt-1.5 text-[11px] text-text-muted"
    >
      {{ props.modelValue.length }} / {{ max }} — comma, space or enter to add
    </p>
  </div>
</template>
