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
        <!-- Backdrop — plain scrim; separation comes from borders, never blur or shadow. -->
        <div class="absolute inset-0 bg-black/70" @click="!busy && emit('cancel')" />

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
                Confirm
              </p>
              <h2
                :id="titleId"
                class="m-0 mt-2 font-condensed text-lg font-extrabold uppercase leading-none tracking-wide text-text-primary"
              >
                {{ title }}
              </h2>
            </div>
            <button
              type="button"
              :disabled="busy"
              @click="emit('cancel')"
              aria-label="Close"
              class="inline-flex size-8 shrink-0 cursor-pointer items-center justify-center rounded-lg border border-border text-base leading-none text-text-muted transition-colors duration-150 hover:border-accent hover:text-accent disabled:pointer-events-none disabled:opacity-40"
            >
              ×
            </button>
          </div>

          <!-- Body -->
          <div class="px-5 py-4 text-sm leading-relaxed text-text-secondary">
            {{ body }}
          </div>

          <!-- Footer — confirm stays solid mint even for danger: the copy carries
               the warning, never a second color. -->
          <div class="flex justify-end gap-2.5 border-t border-border px-5 py-4">
            <button
              ref="cancelBtn"
              type="button"
              :disabled="busy"
              @click="emit('cancel')"
              class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-1.5 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent disabled:pointer-events-none disabled:opacity-40"
            >
              {{ cancelLabel ?? 'Cancel' }}
            </button>
            <button
              type="button"
              :disabled="busy"
              @click="emit('confirm')"
              :class="[
                'rounded-lg px-4 py-1.5 text-xs font-bold transition-[filter] duration-150',
                busy
                  ? 'cursor-not-allowed border border-border bg-transparent text-text-muted'
                  : 'cursor-pointer bg-accent text-[#080f0d] hover:brightness-105',
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
