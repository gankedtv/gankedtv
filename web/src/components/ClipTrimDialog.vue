<script setup lang="ts">
import { ref, watch, computed, onMounted, onUnmounted } from 'vue'
import { ApiError } from '@/api/client'
import { clips, type ClipDetail } from '@/api/clips'
import ClipTrimmer from '@/components/ClipTrimmer.vue'
import type { TrimRange } from '@/lib/trim'

// Post-publish re-cut. The trimmer scrubs the clip's current master, so the range the user
// picks is already in the coordinate space the server expects. Submitting hands the clip back
// to the media pipeline; the parent reloads and its existing "processing" state covers the gap.

const props = defineProps<{ clip: ClipDetail; open: boolean }>()
const emit = defineEmits<{
  close: []
  trimmed: []
  error: [message: string]
}>()

const range = ref<TrimRange | null>(null)
const submitting = ref(false)

watch(
  () => props.open,
  (open) => {
    if (open) {
      range.value = null
      submitting.value = false
    }
  },
)

function onKeydown(e: KeyboardEvent) {
  // The trimmer binds Escape to "reset the range", so only close when it isn't focused.
  if (e.key === 'Escape' && props.open && !submitting.value && !e.defaultPrevented) emit('close')
}

onMounted(() => window.addEventListener('keydown', onKeydown))
onUnmounted(() => window.removeEventListener('keydown', onKeydown))

const canSave = computed(() => range.value !== null && !submitting.value)

const ERROR_CODES: Record<string, string> = {
  invalid_trim: 'That trim range is invalid — try again',
  trim_unavailable: 'Trimming is unavailable on this server right now',
  invalid_state: 'This clip is already being processed — try again once it finishes',
  moderated: 'This clip is under moderation and can’t be edited',
  forbidden: "You don't have permission to edit this clip",
  not_found: 'Clip not found',
}

async function save() {
  const picked = range.value
  if (!picked || submitting.value) return
  submitting.value = true
  try {
    await clips.trim(props.clip.id, {
      trimStartSeconds: picked.start,
      trimEndSeconds: picked.end,
    })
    emit('trimmed')
    emit('close')
  } catch (err) {
    let msg = 'Failed to trim — please try again'
    if (err instanceof ApiError) {
      const code = (err.body as { code?: string } | null)?.code
      if (code && ERROR_CODES[code]) msg = ERROR_CODES[code]
    }
    emit('error', msg)
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
        @click.self="!submitting && emit('close')"
      >
        <div class="absolute inset-0 bg-black/70" @click="!submitting && emit('close')" />

        <div
          class="relative z-10 max-h-[90vh] w-full max-w-3xl overflow-y-auto rounded-lg border border-border-strong bg-surface-raised"
          @click.stop
        >
          <!-- Header -->
          <div class="flex items-start justify-between border-b border-border px-5 py-4">
            <div>
              <p
                class="m-0 text-[10px] font-bold uppercase leading-none tracking-[0.14em] text-text-secondary"
              >
                Re-cut footage
              </p>
              <h2
                class="m-0 mt-2 font-condensed text-lg font-extrabold uppercase leading-none tracking-wide text-text-primary"
              >
                Trim Clip
              </h2>
            </div>
            <button
              type="button"
              :disabled="submitting"
              @click="emit('close')"
              aria-label="Close"
              class="inline-flex size-8 shrink-0 cursor-pointer items-center justify-center rounded-lg border border-border text-base leading-none text-text-muted transition-colors duration-150 hover:border-accent hover:text-accent disabled:cursor-not-allowed disabled:opacity-50"
            >
              ×
            </button>
          </div>

          <!-- Body -->
          <div class="flex flex-col gap-4 p-5">
            <ClipTrimmer v-model="range" :src="clip.videoUrl" />

            <p class="m-0 text-[11px] leading-relaxed text-text-muted">
              Trimming re-encodes the clip, so it drops out of feeds for a moment and comes back
              with an <span class="font-semibold text-text-secondary">Edited</span> badge. The cut
              is permanent — the removed footage can't be restored.
            </p>
          </div>

          <!-- Footer -->
          <div class="flex justify-end gap-2.5 border-t border-border px-5 py-4">
            <button
              type="button"
              :disabled="submitting"
              @click="emit('close')"
              class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-1.5 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent disabled:cursor-not-allowed disabled:opacity-50"
            >
              Cancel
            </button>
            <button
              type="button"
              :disabled="!canSave"
              @click="save"
              :class="[
                'rounded-lg px-4 py-1.5 text-xs font-bold transition-[filter] duration-150',
                canSave
                  ? 'cursor-pointer bg-accent text-[#080f0d] hover:brightness-105'
                  : 'cursor-not-allowed border border-border bg-transparent text-text-muted',
              ]"
            >
              {{ submitting ? 'Submitting…' : 'Apply trim' }}
            </button>
          </div>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>
