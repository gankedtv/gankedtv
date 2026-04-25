<script setup lang="ts">
import { ref, onUnmounted } from 'vue'
import { useRouter } from 'vue-router'
import { GAMES } from '@/lib/mock-data'

const router = useRouter()

// State
const step = ref<1 | 2 | 3>(1)
const file = ref<{ name: string; size: number; type: string } | null>(null)
const title = ref('')
const desc = ref('')
const game = ref('valorant')
const visibility = ref<'public' | 'unlisted'>('public')
const progress = ref(0)
const dragging = ref(false)

// Upload simulation
let uploadInterval: ReturnType<typeof setInterval> | null = null

function startUpload() {
  step.value = 3
  progress.value = 0
  uploadInterval = setInterval(() => {
    const increment = 5 + Math.random() * 5
    progress.value = Math.min(100, progress.value + increment)
    if (progress.value >= 100) {
      progress.value = 100
      if (uploadInterval) clearInterval(uploadInterval)
    }
  }, 180)
}

onUnmounted(() => {
  if (uploadInterval) clearInterval(uploadInterval)
})

// File handling
function handleFileSelect(e: Event) {
  const input = e.target as HTMLInputElement
  if (input.files?.[0]) {
    const f = input.files[0]
    file.value = { name: f.name, size: f.size, type: f.type }
  }
}

function handleDrop(e: DragEvent) {
  dragging.value = false
  const dropped = e.dataTransfer?.files?.[0]
  if (dropped) {
    file.value = { name: dropped.name, size: dropped.size, type: dropped.type }
  }
}

function formatSize(bytes: number): string {
  if (bytes >= 1_073_741_824) return (bytes / 1_073_741_824).toFixed(1) + ' GB'
  if (bytes >= 1_048_576) return (bytes / 1_048_576).toFixed(1) + ' MB'
  return (bytes / 1024).toFixed(1) + ' KB'
}

const STEPS = [
  { num: '1', label: 'Select file' },
  { num: '2', label: 'Describe' },
  { num: '3', label: 'Upload' },
]

const SOURCES = ['OBS', 'ShadowPlay', 'Medal', 'Xbox', 'PS5', 'Switch']

const GAME_KEYS = Object.keys(GAMES)

// Checklist timing
function checklistDone(index: number): boolean {
  if (index === 0) return progress.value >= 30
  if (index === 1) return progress.value >= 80
  return progress.value >= 100
}

const inputClass =
  'w-full rounded-md border border-border bg-surface-raised px-3.5 py-3 font-body text-sm text-text-primary outline-none'
const labelClass =
  'mb-1.5 block font-mono text-[10px] uppercase tracking-widest text-text-muted'
</script>

<template>
  <main class="mx-auto max-w-225 px-6 pt-8 pb-30">
    <!-- Page header -->
    <div class="mb-7">
      <div
        class="mb-2.5 font-mono text-[11px] uppercase tracking-widest text-text-muted"
      >
        Any source welcome · OBS, ShadowPlay, Medal, Xbox, consoles — just drop the file
      </div>
      <h1
        class="m-0 font-heading text-[clamp(32px,4vw,52px)] font-bold leading-none uppercase text-text-primary"
      >
        Upload a clip
      </h1>
    </div>

    <!-- Stepper -->
    <div
      class="mb-8 flex overflow-hidden rounded-md border border-border bg-surface-raised"
    >
      <div
        v-for="(s, i) in STEPS"
        :key="s.num"
        :class="[
          'relative flex-1 px-5 py-4 border-b-2',
          i < STEPS.length - 1 ? 'border-r border-r-border' : '',
          step >= Number(s.num) ? 'bg-surface-overlay' : 'bg-transparent',
          step === Number(s.num) ? 'border-b-brand-light' : 'border-b-transparent',
        ]"
      >
        <div
          :class="[
            'mb-1 font-mono text-[10px] uppercase tracking-widest',
            step >= Number(s.num) ? 'text-neon' : 'text-text-muted',
          ]"
        >
          Step {{ s.num }}
        </div>
        <div
          class="font-heading text-base font-bold uppercase text-text-primary"
        >
          {{ s.label }}
        </div>
      </div>
    </div>

    <!-- Step 1: File picker -->
    <div v-if="step === 1">
      <!-- Drop zone -->
      <div
        @dragover.prevent="dragging = true"
        @dragleave.prevent="dragging = false"
        @drop.prevent="handleDrop"
        :class="[
          'flex flex-col items-center gap-4 rounded-lg border-2 border-dashed px-6 py-16 text-center transition-[border-color] duration-200',
          dragging
            ? 'border-brand-light bg-brand-glow'
            : 'border-border-strong bg-transparent',
        ]"
      >
        <!-- Upload icon circle -->
        <div
          class="flex h-16 w-16 items-center justify-center rounded-full border border-border-strong bg-surface-overlay"
        >
          <svg
            width="28"
            height="28"
            viewBox="0 0 24 24"
            fill="none"
            stroke="var(--color-brand-light)"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
          >
            <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
            <polyline points="17 8 12 3 7 8" />
            <line x1="12" y1="3" x2="12" y2="15" />
          </svg>
        </div>

        <div>
          <div
            class="mb-1.5 font-heading text-[22px] font-bold uppercase text-text-primary"
          >
            Drop your clip here
          </div>
          <div class="font-body text-sm text-text-secondary">
            MP4, MOV, WebM — up to 4 GB
          </div>
        </div>

        <!-- Choose file button -->
        <label
          class="inline-flex cursor-pointer items-center gap-2 rounded-md bg-brand px-5.5 py-2.5 font-heading text-sm font-bold uppercase tracking-wider text-white transition-[background] duration-150"
        >
          <svg
            width="16"
            height="16"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
          >
            <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
            <polyline points="14 2 14 8 20 8" />
          </svg>
          Choose file
          <input type="file" accept="video/*" class="sr-only" @change="handleFileSelect" />
        </label>

        <!-- Source badges -->
        <div class="mt-2 flex flex-wrap justify-center gap-2">
          <span
            v-for="src in SOURCES"
            :key="src"
            class="rounded-sm border border-border bg-surface-overlay px-2.5 py-1 font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted"
          >
            {{ src }}
          </span>
        </div>
      </div>

      <!-- File confirmation row -->
      <div
        v-if="file"
        class="mt-5 flex items-center gap-4 rounded-md border border-neon bg-neon-dim px-5 py-4"
      >
        <svg
          width="20"
          height="20"
          viewBox="0 0 24 24"
          fill="none"
          stroke="var(--color-neon)"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
          class="shrink-0"
        >
          <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
          <polyline points="14 2 14 8 20 8" />
          <line x1="16" y1="13" x2="8" y2="13" />
          <line x1="16" y1="17" x2="8" y2="17" />
        </svg>
        <div class="min-w-0 flex-1">
          <div
            class="overflow-hidden font-body text-sm whitespace-nowrap text-ellipsis text-text-primary"
          >
            {{ file.name }}
          </div>
          <div class="mt-0.5 font-mono text-[11px] text-text-muted">
            {{ formatSize(file.size) }}
          </div>
        </div>
        <button
          @click="step = 2"
          class="inline-flex shrink-0 cursor-pointer items-center gap-1.5 rounded-md bg-brand-light px-5 py-2.5 font-heading text-sm font-bold whitespace-nowrap uppercase tracking-wider text-white"
        >
          Continue
          <svg
            width="14"
            height="14"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2.5"
            stroke-linecap="round"
            stroke-linejoin="round"
          >
            <line x1="5" y1="12" x2="19" y2="12" />
            <polyline points="12 5 19 12 12 19" />
          </svg>
        </button>
      </div>
    </div>

    <!-- Step 2: Metadata -->
    <div v-else-if="step === 2">
      <div
        class="grid gap-8 grid-cols-1 min-[761px]:grid-cols-[1fr_320px]"
      >
        <!-- Left: form -->
        <div class="flex flex-col gap-6">
          <!-- Title -->
          <div>
            <div class="mb-1.5 flex items-baseline justify-between">
              <label :class="labelClass + ' mb-0'">Title</label>
              <span class="font-mono text-[10px] text-text-muted">
                {{ title.length }}/100
              </span>
            </div>
            <input
              v-model="title"
              maxlength="100"
              placeholder="What happened in this clip?"
              :class="inputClass"
            />
          </div>

          <!-- Game picker -->
          <div>
            <label :class="labelClass">Game</label>
            <div class="flex flex-wrap gap-2">
              <button
                v-for="key in GAME_KEYS"
                :key="key"
                @click="game = key"
                :class="[
                  'cursor-pointer rounded-md border px-3.5 py-2 font-heading text-[13px] font-bold uppercase tracking-[0.04em] transition-all duration-150',
                  game === key
                    ? 'border-brand-light bg-brand text-white'
                    : 'border-border bg-surface-raised text-text-secondary',
                ]"
              >
                {{ GAMES[key].tag }}
              </button>
            </div>
          </div>

          <!-- Description -->
          <div>
            <div class="mb-1.5 flex items-baseline justify-between">
              <label :class="labelClass + ' mb-0'"
                >Description
                <span class="text-[9px] text-text-muted">(optional)</span></label
              >
              <span class="font-mono text-[10px] text-text-muted">
                {{ desc.length }}/500
              </span>
            </div>
            <textarea
              v-model="desc"
              maxlength="500"
              rows="4"
              placeholder="Add context, callouts, settings — anything worth knowing"
              :class="inputClass + ' resize-y min-h-24'"
            ></textarea>
          </div>

          <!-- Visibility -->
          <div>
            <label :class="labelClass">Visibility</label>
            <div class="grid grid-cols-2 gap-2.5">
              <button
                v-for="opt in ['public', 'unlisted'] as const"
                :key="opt"
                @click="visibility = opt"
                :class="[
                  'cursor-pointer rounded-md border px-4 py-3.5 text-left transition-all duration-150',
                  visibility === opt
                    ? 'border-brand-light bg-brand-glow text-text-primary'
                    : 'border-border bg-surface-raised text-text-secondary',
                ]"
              >
                <div class="mb-1 flex items-center gap-2">
                  <svg
                    v-if="opt === 'public'"
                    width="16"
                    height="16"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                  >
                    <circle cx="12" cy="12" r="10" />
                    <line x1="2" y1="12" x2="22" y2="12" />
                    <path
                      d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"
                    />
                  </svg>
                  <svg
                    v-else
                    width="16"
                    height="16"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                  >
                    <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71" />
                    <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71" />
                  </svg>
                  <span
                    class="font-heading text-sm font-bold uppercase"
                  >
                    {{ opt === 'public' ? 'Public' : 'Unlisted' }}
                  </span>
                </div>
                <div class="font-body text-xs text-text-muted">
                  {{ opt === 'public' ? 'Visible on feed + search' : 'Only accessible via link' }}
                </div>
              </button>
            </div>
          </div>

          <!-- Action buttons -->
          <div class="flex gap-3 pt-2">
            <button
              @click="step = 1"
              class="inline-flex cursor-pointer items-center gap-1.5 rounded-md border border-border bg-surface-overlay px-5 py-3 font-heading text-sm font-bold uppercase text-text-secondary"
            >
              <svg
                width="14"
                height="14"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2.5"
                stroke-linecap="round"
                stroke-linejoin="round"
              >
                <line x1="19" y1="12" x2="5" y2="12" />
                <polyline points="12 19 5 12 12 5" />
              </svg>
              Back
            </button>
            <button
              :disabled="!title.trim()"
              @click="startUpload"
              :class="[
                'inline-flex flex-1 items-center justify-center gap-2 rounded-md px-5 py-3 font-heading text-[15px] font-bold uppercase tracking-wider transition-all duration-150',
                title.trim()
                  ? 'cursor-pointer border-0 bg-brand-light text-white'
                  : 'cursor-not-allowed border border-border bg-surface-overlay text-text-muted',
              ]"
            >
              Start upload
              <svg
                width="14"
                height="14"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2.5"
                stroke-linecap="round"
                stroke-linejoin="round"
              >
                <line x1="5" y1="12" x2="19" y2="12" />
                <polyline points="12 5 19 12 12 19" />
              </svg>
            </button>
          </div>
        </div>

        <!-- Right: preview card -->
        <div>
          <label :class="labelClass + ' mb-3'">Preview</label>
          <div
            class="overflow-hidden rounded-md border border-border bg-surface-raised"
          >
            <!-- Thumbnail -->
            <div
              class="relative aspect-video bg-surface-sunken"
            >
              <img
                v-if="GAMES[game]?.art"
                :src="GAMES[game].art"
                :alt="GAMES[game].name"
                class="h-full w-full object-cover"
              />
              <!-- Game tag -->
              <div
                class="absolute top-2 left-2 rounded-sm bg-brand px-2 py-0.75 font-mono text-[10px] uppercase tracking-[0.08em] text-white"
              >
                {{ GAMES[game]?.tag }}
              </div>
              <!-- Visibility badge -->
              <div
                class="absolute top-2 right-2 rounded-sm bg-black/60 px-2 py-0.75 font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted"
              >
                {{ visibility }}
              </div>
            </div>

            <div class="p-3.5">
              <!-- Title -->
              <div
                :class="[
                  'mb-2.5 font-heading text-[15px] font-bold leading-[1.3]',
                  title.trim() ? 'not-italic text-text-primary' : 'italic text-text-muted',
                ]"
              >
                {{ title.trim() || 'Your clip title will appear here' }}
              </div>

              <!-- User row -->
              <div class="flex items-center gap-2">
                <div
                  class="flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-brand font-mono text-[9px] text-white"
                >
                  P
                </div>
                <span class="font-mono text-[11px] text-text-secondary">
                  @phantomveil
                </span>
              </div>
            </div>

            <!-- Share URL preview -->
            <div
              class="mx-3.5 mb-3.5 rounded-sm border border-dashed border-border-strong px-3 py-2.5"
            >
              <div
                class="mb-1 font-mono text-[9px] uppercase tracking-[0.08em] text-text-muted"
              >
                Share URL preview
              </div>
              <div
                class="overflow-hidden font-mono text-[11px] whitespace-nowrap text-ellipsis text-brand-light"
              >
                ganked.tv/clip/clp_new
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Step 3: Upload progress -->
    <div v-else-if="step === 3">
      <div class="mx-auto max-w-140">
        <!-- Clip summary -->
        <div
          class="mb-8 flex gap-0 overflow-hidden rounded-md border border-border bg-surface-raised"
        >
          <!-- Thumbnail -->
          <div class="relative w-35 shrink-0">
            <img
              :src="GAMES[game]?.art"
              :alt="GAMES[game]?.name"
              class="block h-full w-full object-cover"
            />
          </div>
          <div class="min-w-0 flex-1 p-4">
            <div
              class="mb-1.5 font-mono text-[10px] uppercase tracking-[0.08em] text-neon"
            >
              {{ GAMES[game]?.tag }}
            </div>
            <div
              class="mb-2 font-heading text-base font-bold leading-[1.3] text-text-primary"
            >
              {{ title }}
            </div>
            <div
              class="font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted"
            >
              {{ visibility }}
            </div>
          </div>
        </div>

        <!-- Progress bar -->
        <div class="mb-2 flex items-baseline justify-between">
          <span
            class="font-mono text-[11px] uppercase tracking-[0.08em] text-text-muted"
          >
            Uploading
          </span>
          <span class="font-mono text-[11px] text-neon">
            {{ Math.round(progress) }}%
          </span>
        </div>
        <div
          class="mb-7 h-1.5 w-full overflow-hidden rounded-full bg-surface-overlay"
        >
          <div
            class="h-full rounded-full bg-[linear-gradient(90deg,var(--color-brand),var(--color-brand-light))] transition-[width] duration-180 ease"
            :style="{ width: progress + '%' }"
          ></div>
        </div>

        <!-- Checklist -->
        <div class="mb-9 flex flex-col gap-3.5">
          <div
            v-for="(item, i) in ['Create record', 'Upload video', 'Generate thumbnail']"
            :key="item"
            class="flex items-center gap-3"
          >
            <div
              :class="[
                'h-2 w-2 shrink-0 rounded-full transition-[background,box-shadow] duration-300',
                checklistDone(i)
                  ? 'bg-neon shadow-[0_0_8px_var(--color-neon)]'
                  : 'bg-border-strong',
              ]"
            ></div>
            <span
              :class="[
                'font-mono text-xs uppercase tracking-[0.08em] transition-colors duration-300',
                checklistDone(i) ? 'text-text-primary' : 'text-text-muted',
              ]"
            >
              {{ i + 1 }}. {{ item }}
            </span>
          </div>
        </div>

        <!-- Done actions -->
        <div v-if="progress >= 100" class="flex flex-col gap-2.5">
          <button
            @click="router.push('/clip/clp_04')"
            class="flex w-full cursor-pointer items-center justify-center gap-2 rounded-md bg-brand-light px-6 py-3.5 font-heading text-base font-bold uppercase tracking-wider text-white"
          >
            View your clip
            <svg
              width="16"
              height="16"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="2.5"
              stroke-linecap="round"
              stroke-linejoin="round"
            >
              <line x1="5" y1="12" x2="19" y2="12" />
              <polyline points="12 5 19 12 12 19" />
            </svg>
          </button>
          <button
            @click="router.push('/')"
            class="flex w-full cursor-pointer items-center justify-center rounded-md border border-border bg-transparent px-6 py-3 font-heading text-sm font-bold uppercase tracking-wider text-text-secondary"
          >
            Back to feed
          </button>
        </div>
      </div>
    </div>
  </main>
</template>
