<script setup lang="ts">
import { onMounted, onUnmounted, ref, useId, watch } from 'vue'
import type { GameSummary } from '@/api/clips'
import GameSelector from '@/components/GameSelector.vue'

const props = defineProps<{
  open: boolean
}>()

const emit = defineEmits<{
  // null = "clear the tag" (admin couldn't find a fitting game or wants no tag).
  submit: [gameId: number | null]
  cancel: []
}>()

const titleId = useId()
// Always starts empty — the existing (wrong) tag isn't a useful default. The admin
// actively picks the correct game (or leaves it empty to clear).
const selected = ref<GameSummary | null>(null)

watch(
  () => props.open,
  (open) => {
    if (open) selected.value = null
  },
)

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape' && props.open) emit('cancel')
}

onMounted(() => window.addEventListener('keydown', onKeydown))
onUnmounted(() => window.removeEventListener('keydown', onKeydown))

function submit() {
  emit('submit', selected.value?.id ?? null)
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
        <div class="absolute inset-0 bg-black/70" @click="emit('cancel')" />
        <div
          class="relative z-10 w-full max-w-md rounded-lg border border-border-strong bg-surface-raised"
          @click.stop
        >
          <div class="flex items-start justify-between border-b border-border px-5 py-4">
            <div>
              <p
                class="m-0 text-[10px] font-bold uppercase leading-none tracking-[0.14em] text-text-secondary"
              >
                Re-file
              </p>
              <h2
                :id="titleId"
                class="m-0 mt-2 font-condensed text-lg font-extrabold uppercase leading-none tracking-wide text-text-primary"
              >
                Fix game tag
              </h2>
            </div>
            <button
              type="button"
              @click="emit('cancel')"
              aria-label="Close"
              class="inline-flex size-8 shrink-0 cursor-pointer items-center justify-center rounded-lg border border-border text-base leading-none text-text-muted transition-colors duration-150 hover:border-accent hover:text-accent"
            >
              ×
            </button>
          </div>

          <form @submit.prevent="submit" class="px-5 py-4">
            <label
              class="mb-2 block text-[10px] font-bold uppercase tracking-widest text-text-secondary"
            >
              Game
            </label>
            <GameSelector v-model="selected" />
            <p class="mt-2 text-[10px] text-text-muted">
              Leave empty to clear the game tag entirely.
            </p>

            <div class="mt-4 flex justify-end gap-2.5 border-t border-border pt-4">
              <button
                type="button"
                @click="emit('cancel')"
                class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-1.5 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
              >
                Cancel
              </button>
              <button
                type="submit"
                class="cursor-pointer rounded-lg bg-accent px-4 py-1.5 text-xs font-bold text-[#080f0d] transition-[filter] duration-150 hover:brightness-105"
              >
                Save
              </button>
            </div>
          </form>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>
