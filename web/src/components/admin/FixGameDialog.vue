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
          class="relative z-10 w-full max-w-md rounded-md border border-border bg-surface-raised shadow-[0_0_40px_var(--color-brand-glow)]"
          @click.stop
        >
          <div class="flex items-center justify-between border-b border-border px-5 py-4">
            <h2
              :id="titleId"
              class="font-heading text-lg font-bold uppercase tracking-[0.04em] text-text-primary"
            >
              Fix game tag
            </h2>
            <button
              type="button"
              @click="emit('cancel')"
              aria-label="Close"
              class="cursor-pointer font-mono text-xl leading-none text-text-muted transition-colors duration-150 hover:text-text-primary"
            >
              ×
            </button>
          </div>

          <form @submit.prevent="submit" class="px-5 py-4">
            <label
              class="mb-2 block font-heading text-xs font-bold uppercase tracking-wider text-text-muted"
            >
              Game
            </label>
            <GameSelector v-model="selected" />
            <p class="mt-2 font-mono text-[10px] text-text-muted">
              Leave empty to clear the game tag entirely.
            </p>

            <div class="mt-4 flex justify-end gap-2.5 border-t border-border pt-4">
              <button
                type="button"
                @click="emit('cancel')"
                class="cursor-pointer rounded-md border border-border bg-surface-overlay px-5 py-2.5 font-heading text-sm font-bold uppercase tracking-wider text-text-secondary transition-colors duration-150 hover:text-text-primary"
              >
                Cancel
              </button>
              <button
                type="submit"
                class="cursor-pointer rounded-md bg-brand-light px-5 py-2.5 font-heading text-sm font-bold uppercase tracking-wider text-white transition-all duration-150 hover:bg-brand"
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
