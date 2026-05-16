<script setup lang="ts">
import { ref, watch, nextTick, onMounted, onUnmounted, useId } from 'vue'

const props = defineProps<{
  open: boolean
  title: string
  body: string
  confirmLabel: string
  cancelLabel?: string
  variant?: 'danger' | 'default'
  busy?: boolean
}>()

const emit = defineEmits<{
  confirm: []
  cancel: []
}>()

const titleId = useId()
const cancelBtn = ref<HTMLButtonElement | null>(null)

watch(
  () => props.open,
  (open) => {
    if (open) nextTick(() => cancelBtn.value?.focus())
  },
)

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape' && props.open && !props.busy) emit('cancel')
}

onMounted(() => window.addEventListener('keydown', onKeydown))
onUnmounted(() => window.removeEventListener('keydown', onKeydown))
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
        <!-- Backdrop -->
        <div class="absolute inset-0 bg-black/70" @click="!busy && emit('cancel')" />

        <!-- Dialog card -->
        <div
          class="relative z-10 w-full max-w-lg rounded-md border border-border bg-surface-raised shadow-[0_0_40px_var(--color-brand-glow)]"
          @click.stop
        >
          <!-- Header -->
          <div class="flex items-center justify-between border-b border-border px-5 py-4">
            <h2
              :id="titleId"
              class="font-heading text-lg font-bold uppercase tracking-[0.04em] text-text-primary"
            >
              {{ title }}
            </h2>
            <button
              type="button"
              :disabled="busy"
              @click="emit('cancel')"
              aria-label="Close"
              class="cursor-pointer font-mono text-xl leading-none text-text-muted transition-colors duration-150 hover:text-text-primary disabled:pointer-events-none disabled:opacity-40"
            >
              ×
            </button>
          </div>

          <!-- Body -->
          <div class="px-5 py-4 font-body text-sm leading-relaxed text-text-secondary">
            {{ body }}
          </div>

          <!-- Footer -->
          <div class="flex justify-end gap-2.5 border-t border-border px-5 py-4">
            <button
              ref="cancelBtn"
              type="button"
              :disabled="busy"
              @click="emit('cancel')"
              class="cursor-pointer rounded-md border border-border bg-surface-overlay px-5 py-2.5 font-heading text-sm font-bold uppercase tracking-wider text-text-secondary transition-colors duration-150 hover:text-text-primary disabled:pointer-events-none disabled:opacity-40"
            >
              {{ cancelLabel ?? 'Cancel' }}
            </button>
            <button
              type="button"
              :disabled="busy"
              @click="emit('confirm')"
              :class="[
                'rounded-md px-5 py-2.5 font-heading text-sm font-bold uppercase tracking-wider transition-all duration-150',
                busy
                  ? 'cursor-not-allowed border border-border bg-surface-overlay text-text-muted'
                  : variant === 'danger'
                    ? 'cursor-pointer bg-[color:var(--color-error)] text-white hover:bg-[color:var(--color-error)]/90'
                    : 'cursor-pointer bg-brand-light text-white hover:bg-brand',
              ]"
            >
              {{ busy ? `${confirmLabel}…` : confirmLabel }}
            </button>
          </div>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>
