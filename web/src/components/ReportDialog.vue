<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, useId, watch } from 'vue'
import { report, type ReportReason, type ReportTargetType } from '@/api/reports'
import { ApiError } from '@/api/client'

const props = defineProps<{
  open: boolean
  targetType: ReportTargetType
  targetId: string
}>()

const emit = defineEmits<{
  submitted: []
  cancel: []
}>()

const titleId = useId()
const reasonSelect = ref<HTMLSelectElement | null>(null)

const reason = ref<ReportReason>('spam')
const note = ref('')
const submitting = ref(false)
const error = ref<string | null>(null)

// Reuse the server's enum verbatim so a future addition only requires touching the array
// — the values are validated server-side. `wrong_game` is clip-specific in spirit but the
// server doesn't filter by target type, so we keep the order tidy (categorisation issues
// last in the abuse list, before the catch-all "Other").
const REASONS: { value: ReportReason; label: string }[] = [
  { value: 'spam', label: 'Spam' },
  { value: 'harassment', label: 'Harassment' },
  { value: 'hate', label: 'Hate speech' },
  { value: 'nsfw', label: 'NSFW / Inappropriate' },
  { value: 'violence', label: 'Violence / threats' },
  { value: 'wrong_game', label: 'Wrong game tag' },
  { value: 'other', label: 'Other (add a note)' },
]

const noteRequired = computed(() => reason.value === 'other')
const canSubmit = computed(() => {
  if (submitting.value) return false
  if (noteRequired.value && !note.value.trim()) return false
  return true
})

watch(
  () => props.open,
  (open) => {
    if (open) {
      reason.value = 'spam'
      note.value = ''
      error.value = null
      nextTick(() => reasonSelect.value?.focus())
    }
  },
)

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape' && props.open && !submitting.value) emit('cancel')
}

onMounted(() => window.addEventListener('keydown', onKeydown))
onUnmounted(() => window.removeEventListener('keydown', onKeydown))

async function submit() {
  if (!canSubmit.value) return
  submitting.value = true
  error.value = null
  try {
    await report(props.targetType, props.targetId, reason.value, note.value.trim() || undefined)
    emit('submitted')
  } catch (err) {
    if (err instanceof ApiError) {
      const code = (err.body as { code?: string } | null)?.code
      if (code === 'duplicate_report') {
        error.value = 'You already reported this. A moderator will review it.'
      } else if (code === 'self_report') {
        error.value = "You can't report your own content."
      } else if (code === 'note_required') {
        error.value = 'Please add a note explaining the issue.'
      } else if (err.status === 401) {
        error.value = 'You must be signed in to report.'
      } else if (err.status === 404) {
        error.value = 'This content no longer exists.'
      } else {
        error.value = 'Could not submit the report. Try again.'
      }
    } else {
      error.value = 'Could not submit the report. Try again.'
    }
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <Teleport to="body">
    <Transition
      enter-active-class="transition-opacity duration-200"
      enter-from-class="opacity-0"
      leave-active-class="transition-opacity duration-150"
      leave-to-class="opacity-0"
    >
      <div
        v-if="open"
        class="fixed inset-0 z-50 flex items-center justify-center px-4"
        role="dialog"
        aria-modal="true"
        :aria-labelledby="titleId"
      >
        <div class="absolute inset-0 bg-black/70" @click="!submitting && emit('cancel')" />

        <div
          class="relative z-10 w-full max-w-lg rounded-lg border border-border-strong bg-surface-raised"
          @click.stop
        >
          <div class="flex items-start justify-between border-b border-border px-5 py-4">
            <div>
              <p
                class="m-0 text-[10px] font-bold uppercase leading-none tracking-[0.14em] text-text-secondary"
              >
                File a report
              </p>
              <h2
                :id="titleId"
                class="m-0 mt-2 font-condensed text-lg font-extrabold uppercase leading-none tracking-wide text-text-primary"
              >
                Report
                {{ targetType === 'clip' ? 'clip' : targetType === 'comment' ? 'comment' : 'user' }}
              </h2>
            </div>
            <button
              type="button"
              :disabled="submitting"
              @click="emit('cancel')"
              aria-label="Close"
              class="inline-flex size-8 shrink-0 cursor-pointer items-center justify-center rounded-lg border border-border text-base leading-none text-text-muted transition-colors duration-150 hover:border-accent hover:text-accent disabled:pointer-events-none disabled:opacity-40"
            >
              ×
            </button>
          </div>

          <form @submit.prevent="submit" class="px-5 py-4">
            <label
              class="mb-2 block text-[10px] font-bold uppercase tracking-widest text-text-secondary"
            >
              Reason
            </label>
            <select
              ref="reasonSelect"
              v-model="reason"
              :disabled="submitting"
              class="mb-4 block h-11 w-full cursor-pointer rounded-md border border-border bg-surface-high px-3 text-sm text-text-primary focus:border-accent focus:outline-none"
            >
              <option v-for="r in REASONS" :key="r.value" :value="r.value">{{ r.label }}</option>
            </select>

            <label
              class="mb-2 block text-[10px] font-bold uppercase tracking-widest text-text-secondary"
            >
              Note
              <span v-if="noteRequired" class="text-accent">(required)</span>
              <span v-else class="text-text-muted">(optional)</span>
            </label>
            <textarea
              v-model="note"
              :disabled="submitting"
              rows="4"
              maxlength="2000"
              class="mb-2 block w-full rounded-md border border-border bg-surface-high px-3 py-2 text-sm text-text-primary placeholder:text-text-muted focus:border-accent focus:outline-none"
              :placeholder="noteRequired ? `What's the issue?` : 'Add any extra context...'"
            />

            <p v-if="error" class="mb-2 text-xs font-medium text-accent">
              {{ error }}
            </p>

            <div class="mt-2 flex justify-end gap-2.5 border-t border-border pt-4">
              <button
                type="button"
                :disabled="submitting"
                @click="emit('cancel')"
                class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-1.5 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent disabled:pointer-events-none disabled:opacity-40"
              >
                Cancel
              </button>
              <button
                type="submit"
                :disabled="!canSubmit"
                :class="[
                  'rounded-lg px-4 py-1.5 text-xs font-bold transition-[filter] duration-150',
                  !canSubmit
                    ? 'cursor-not-allowed border border-border bg-transparent text-text-muted'
                    : 'cursor-pointer bg-accent text-[#080f0d] hover:brightness-105',
                ]"
              >
                {{ submitting ? 'Reporting…' : 'Report' }}
              </button>
            </div>
          </form>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>
