<script setup lang="ts">
import { ref, watch, computed, onMounted, onUnmounted } from 'vue'
import { ApiError } from '@/api/client'
import {
  clips,
  type ClipDetail,
  type ClipVisibility,
  type UpdateClipBody,
  type GameSummary,
} from '@/api/clips'
import GameSelector from '@/components/GameSelector.vue'
import TagInput from '@/components/TagInput.vue'
import IconGlobe from '@/components/icons/IconGlobe.vue'
import IconLink from '@/components/icons/IconLink.vue'
import IconLock from '@/components/icons/IconLock.vue'
import { VISIBILITY_OPTIONS } from '@/lib/visibility'

const props = defineProps<{ clip: ClipDetail; open: boolean }>()
const emit = defineEmits<{
  close: []
  saved: [updated: ClipDetail]
  error: [message: string]
}>()

const localTitle = ref(props.clip.title)
const localDesc = ref(props.clip.description ?? '')
const localGame = ref<GameSummary | null>(props.clip.game)
const localVisibility = ref<ClipVisibility>(props.clip.visibility)
const localTags = ref<string[]>(props.clip.tags.map((t) => t.slug))
const submitting = ref(false)

watch(
  () => props.open,
  (open) => {
    if (open) {
      localTitle.value = props.clip.title
      localDesc.value = props.clip.description ?? ''
      localGame.value = props.clip.game
      localVisibility.value = props.clip.visibility
      localTags.value = props.clip.tags.map((t) => t.slug)
      submitting.value = false
    }
  },
)

function arraysEqualSorted(a: string[], b: string[]): boolean {
  if (a.length !== b.length) return false
  const sa = [...a].sort()
  const sb = [...b].sort()
  for (let i = 0; i < sa.length; i++) if (sa[i] !== sb[i]) return false
  return true
}

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape' && props.open) emit('close')
}

onMounted(() => window.addEventListener('keydown', onKeydown))
onUnmounted(() => window.removeEventListener('keydown', onKeydown))

// Build a sparse payload containing only changed fields.
const diff = computed((): UpdateClipBody => {
  const body: UpdateClipBody = {}
  const trimmedTitle = localTitle.value.trim()
  if (trimmedTitle !== props.clip.title) body.title = trimmedTitle
  // Normalize empty string ↔ null when comparing, but send "" to the server to clear.
  const trimmedDesc = localDesc.value.trim()
  const localDescNorm = trimmedDesc === '' ? null : trimmedDesc
  const originalDescNorm = props.clip.description || null
  if (localDescNorm !== originalDescNorm) body.description = trimmedDesc
  if ((localGame.value?.id ?? null) !== (props.clip.game?.id ?? null))
    body.gameId = localGame.value?.id ?? null
  if (localVisibility.value !== props.clip.visibility) body.visibility = localVisibility.value
  const originalTagSlugs = props.clip.tags.map((t) => t.slug)
  if (!arraysEqualSorted(localTags.value, originalTagSlugs)) {
    body.tags = [...localTags.value]
  }
  return body
})

const canSave = computed(
  () => Object.keys(diff.value).length > 0 && localTitle.value.trim().length > 0,
)

const ERROR_CODES: Record<string, string> = {
  invalid_title: 'Title is invalid or too long',
  invalid_description: 'Description is too long',
  invalid_visibility: 'Invalid visibility value',
  invalid_game: 'Game not found',
  forbidden: "You don't have permission to edit this clip",
  not_found: 'Clip not found',
  invalid_state: 'Only published clips can be edited',
  too_many_tags: 'You can use up to 5 tags',
  invalid_tag: 'One of your tags is invalid (use 2-24 letters, digits or hyphens)',
}

async function save() {
  if (!canSave.value || submitting.value) return
  submitting.value = true
  try {
    const updated = await clips.update(props.clip.id, diff.value)
    emit('saved', updated)
    emit('close')
  } catch (err) {
    let msg = 'Failed to save — please try again'
    if (err instanceof ApiError) {
      const code = (err.body as { code?: string } | null)?.code
      if (code && ERROR_CODES[code]) msg = ERROR_CODES[code]
    }
    emit('error', msg)
  } finally {
    submitting.value = false
  }
}

const inputClass =
  'w-full rounded-md border border-border bg-surface-high px-3.5 py-3 text-sm text-text-primary outline-none placeholder:text-text-muted focus:border-accent transition-colors duration-150'
const labelClass =
  'mb-1.5 block text-[10px] font-bold uppercase tracking-widest text-text-secondary'
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
        @click.self="emit('close')"
      >
        <!-- Backdrop — plain scrim, no blur. -->
        <div class="absolute inset-0 bg-black/70" @click="emit('close')" />

        <!-- Dialog card -->
        <div
          class="relative z-10 w-full max-w-lg rounded-lg border border-border-strong bg-surface-raised"
          @click.stop
        >
          <!-- Header -->
          <div class="flex items-start justify-between border-b border-border px-5 py-4">
            <div>
              <p
                class="m-0 text-[10px] font-bold uppercase leading-none tracking-[0.14em] text-text-secondary"
              >
                Edit filing
              </p>
              <h2
                class="m-0 mt-2 font-condensed text-lg font-extrabold uppercase leading-none tracking-wide text-text-primary"
              >
                Edit Clip
              </h2>
            </div>
            <button
              type="button"
              @click="emit('close')"
              aria-label="Close"
              class="inline-flex size-8 shrink-0 cursor-pointer items-center justify-center rounded-lg border border-border text-base leading-none text-text-muted transition-colors duration-150 hover:border-accent hover:text-accent"
            >
              ×
            </button>
          </div>

          <!-- Body -->
          <div class="flex flex-col gap-5 p-5">
            <!-- Title -->
            <div>
              <div class="mb-1.5 flex items-baseline justify-between">
                <label :class="labelClass + ' mb-0'">Title</label>
                <span class="text-[10px] text-text-muted"> {{ localTitle.length }}/100 </span>
              </div>
              <input
                v-model="localTitle"
                maxlength="100"
                placeholder="Clip title"
                :class="inputClass"
              />
            </div>

            <!-- Description -->
            <div>
              <div class="mb-1.5 flex items-baseline justify-between">
                <label :class="labelClass + ' mb-0'">
                  Description
                  <span class="text-[9px] text-text-muted">(optional)</span>
                </label>
                <span class="text-[10px] text-text-muted"> {{ localDesc.length }}/500 </span>
              </div>
              <textarea
                v-model="localDesc"
                maxlength="500"
                rows="3"
                placeholder="Add context, callouts, settings…"
                :class="inputClass + ' resize-y min-h-20'"
              />
            </div>

            <!-- Game -->
            <div>
              <label :class="labelClass">
                Game <span class="text-[9px] text-text-muted">(optional)</span>
              </label>
              <GameSelector v-model="localGame" />
            </div>

            <!-- Tags -->
            <div>
              <label :class="labelClass">
                Tags <span class="text-[9px] text-text-muted">(optional, max 5)</span>
              </label>
              <TagInput v-model="localTags" :input-class="inputClass" />
            </div>

            <!-- Visibility -->
            <div>
              <label :class="labelClass">Visibility</label>
              <div class="grid grid-cols-3 gap-2.5">
                <button
                  v-for="opt in VISIBILITY_OPTIONS"
                  :key="opt.value"
                  type="button"
                  @click="localVisibility = opt.value"
                  :class="[
                    'cursor-pointer rounded-lg border px-4 py-3 text-left transition-colors duration-150',
                    localVisibility === opt.value
                      ? 'border-accent text-text-primary'
                      : 'border-border text-text-secondary hover:border-border-strong',
                  ]"
                >
                  <div class="mb-1 flex items-center gap-2">
                    <IconGlobe v-if="opt.value === 'public'" :size="15" />
                    <IconLock v-else-if="opt.value === 'private'" :size="15" />
                    <IconLink v-else :size="15" />
                    <span class="font-condensed text-sm font-bold uppercase">
                      {{ opt.label }}
                    </span>
                  </div>
                  <div class="text-xs text-text-muted">
                    {{ opt.description }}
                  </div>
                </button>
              </div>
            </div>
          </div>

          <!-- Footer -->
          <div class="flex justify-end gap-2.5 border-t border-border px-5 py-4">
            <button
              type="button"
              @click="emit('close')"
              class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-1.5 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
            >
              Cancel
            </button>
            <button
              type="button"
              :disabled="!canSave || submitting"
              @click="save"
              :class="[
                'rounded-lg px-4 py-1.5 text-xs font-bold transition-[filter] duration-150',
                canSave && !submitting
                  ? 'cursor-pointer bg-accent text-[#080f0d] hover:brightness-105'
                  : 'cursor-not-allowed border border-border bg-transparent text-text-muted',
              ]"
            >
              {{ submitting ? 'Saving…' : 'Save' }}
            </button>
          </div>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>
