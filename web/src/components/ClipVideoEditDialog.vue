<script setup lang="ts">
import { ref, watch, computed, onMounted, onUnmounted } from 'vue'
import { ApiError } from '@/api/client'
import { clips, type ClipDetail } from '@/api/clips'
import ClipTrimmer from '@/components/ClipTrimmer.vue'
import ClipCropper from '@/components/ClipCropper.vue'
import UnderlineTabs from '@/components/UnderlineTabs.vue'
import type { TrimRange } from '@/lib/trim'
import type { CropRect } from '@/lib/crop'

// Post-publish re-edit. Both editors work against the clip's current master, so the range and
// the rect are already in the coordinate space the server expects.
//
// ONE dialog rather than a Trim one and a Crop one, because /edit applies both in a single
// re-encode: split across two dialogs the owner pays two generations of quality loss for the
// same result, and the endpoint's whole reason for existing goes unused. Submitting hands the
// clip back to the media pipeline; the parent reloads and its existing "processing" state
// covers the gap.

const props = defineProps<{ clip: ClipDetail; open: boolean }>()
const emit = defineEmits<{
  close: []
  edited: []
  error: [message: string]
}>()

type EditTab = 'trim' | 'crop'
const TABS: { key: EditTab; label: string }[] = [
  { key: 'trim', label: 'Trim' },
  { key: 'crop', label: 'Crop' },
]

const tab = ref<EditTab>('trim')
const range = ref<TrimRange | null>(null)
const crop = ref<CropRect | null>(null)
const submitting = ref(false)

watch(
  () => props.open,
  (open) => {
    if (open) {
      tab.value = 'trim'
      range.value = null
      crop.value = null
      submitting.value = false
    }
  },
)

function onKeydown(e: KeyboardEvent) {
  // Both editors bind Escape to "reset my selection", so only close when neither handled it.
  if (e.key === 'Escape' && props.open && !submitting.value && !e.defaultPrevented) emit('close')
}

onMounted(() => window.addEventListener('keydown', onKeydown))
onUnmounted(() => window.removeEventListener('keydown', onKeydown))

const canSave = computed(() => (range.value !== null || crop.value !== null) && !submitting.value)

const ERROR_CODES: Record<string, string> = {
  invalid_trim: 'That trim range is invalid — try again',
  invalid_crop: 'That crop is invalid — try again',
  trim_unavailable: 'Trimming is unavailable on this server right now',
  crop_unavailable: 'Cropping is unavailable on this server right now',
  no_operations: 'Nothing to apply — trim or crop the clip first',
  invalid_state: 'This clip is already being processed — try again once it finishes',
  moderated: 'This clip is under moderation and can’t be edited',
  forbidden: "You don't have permission to edit this clip",
  not_found: 'Clip not found',
}

async function save() {
  const picked = range.value
  const rect = crop.value
  if ((!picked && !rect) || submitting.value) return
  submitting.value = true
  try {
    // One call carrying whatever the owner set, so a trim AND a crop cost one re-encode.
    await clips.edit(props.clip.id, {
      ...(picked ? { trimStartSeconds: picked.start, trimEndSeconds: picked.end } : {}),
      ...(rect
        ? { cropX: rect.x, cropY: rect.y, cropWidth: rect.width, cropHeight: rect.height }
        : {}),
    })
    emit('edited')
    emit('close')
  } catch (err) {
    let msg = 'Failed to edit — please try again'
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
                Trim &amp; Crop
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
            <div class="flex items-baseline justify-between gap-3">
              <UnderlineTabs :tabs="TABS" :active="tab" class="flex-1" @select="tab = $event" />
              <span v-if="range || crop" class="text-[10px] font-bold text-accent">
                {{
                  range && crop
                    ? 'Trim + crop, one re-encode'
                    : range
                      ? 'Trim pending'
                      : 'Crop pending'
                }}
              </span>
            </div>

            <!-- v-if, not v-show: only the active editor is mounted so exactly one <video>
                 decodes at a time. Each re-seeds from its model on loadedmetadata, so switching
                 tabs preserves both selections. -->
            <ClipTrimmer v-if="tab === 'trim'" v-model="range" :src="clip.videoUrl" />
            <ClipCropper v-else v-model="crop" :src="clip.videoUrl" :clip-id="clip.id" />

            <p class="m-0 text-[11px] leading-relaxed text-text-muted">
              Editing re-encodes the clip, so it drops out of feeds for a moment and comes back with
              an <span class="font-semibold text-text-secondary">Edited</span> badge. Whatever you
              cut away is permanent — the previous version isn't stored. Setting both at once
              applies them in a single re-encode.
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
              {{ submitting ? 'Submitting…' : 'Apply changes' }}
            </button>
          </div>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>
